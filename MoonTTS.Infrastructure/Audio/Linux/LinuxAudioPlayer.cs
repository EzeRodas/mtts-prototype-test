using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Infrastructure.Audio.Linux;

namespace MoonTTS.Infrastructure.Audio;

/* File Structure:
        - LinuxAudioPlayer(ILinuxAudioBackend? backend)
        - GetAvailableDevices() : Result<List<AudioDeviceInfo>>
        - SetOutputDevices(string? primaryDeviceId, string? monitorDeviceId) : Result
        - SetVolume(int? primaryVolume, int? monitorVolume) : void
        - PlayPcmAsync(ReadOnlyMemory<byte> pcmBytes, AudioFormat format, CancellationToken cancellationToken) : Task<Result>
        - StreamPcmAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcmChunks, AudioFormat format, CancellationToken cancellationToken) : Task<Result>
        - Pause() : void
        - Resume() : void
        - Stop() : void
        - ActiveSinkTarget(IAudioSinkSession Session, Func<float> VolumeProvider)
*/
public class LinuxAudioPlayer : IAudioPlayer
{
    private const string KeepCurrent = "__keep_current__";

    private readonly ILinuxAudioBackend _backend;
    
    private string? _primaryDeviceId = "-1";
    private string? _monitorDeviceId = null;

    private float _primaryVolume = 1.0f;
    private float _monitorVolume = 1.0f;

    private readonly Lock _sessionLock = new();
    private readonly List<ActiveSinkTarget> _activeTargets = new();
    
    private CancellationTokenSource? _playbackCts;
    private bool _isPaused = false;

    /* Initializes the Linux audio player with the specified or default Linux audio backend */
    public LinuxAudioPlayer(ILinuxAudioBackend? backend = null)
    {
        _backend = backend ?? new LinuxAudioStreamer();
    }

    /* Returns a list with all the available Linux audio output devices visible by the application */
    public Result<List<AudioDeviceInfo>> GetAvailableDevices()
    {
        return _backend.GetAvailableDevices();
    }

    /* Config */
    
    /* Sets audio outputs individually or both at the same time */
    public Result SetOutputDevices(string? primaryDeviceId = KeepCurrent, string? monitorDeviceId = KeepCurrent)
    {
        lock (_sessionLock)
        {
            if (primaryDeviceId != KeepCurrent)
                _primaryDeviceId = !string.IsNullOrWhiteSpace(primaryDeviceId) ? primaryDeviceId : null;
            
            if (monitorDeviceId != KeepCurrent)
                _monitorDeviceId = !string.IsNullOrWhiteSpace(monitorDeviceId) ? monitorDeviceId : null;
        }
        return Result.Success();
    }

    /* Sets volume levels for each audio output individually or both at the same time */
    public void SetVolume(int? primaryVolume = null, int? monitorVolume = null)
    {
        lock (_sessionLock)
        {
            if (primaryVolume.HasValue)
                _primaryVolume = Math.Clamp(primaryVolume.Value, 0, 100) / 100.0f;

            if (monitorVolume.HasValue)
                _monitorVolume = Math.Clamp(monitorVolume.Value, 0, 100) / 100.0f;
        }
    }

    /* Plays a single memory buffer of raw PCM audio data */
    public async Task<Result> PlayPcmAsync(
        ReadOnlyMemory<byte> pcmBytes, 
        AudioFormat format, 
        CancellationToken cancellationToken = default)
    {
        async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleChunk()
        {
            yield return pcmBytes;
            await Task.CompletedTask;
        }

        return await StreamPcmAsync(SingleChunk(), format, cancellationToken);
    }
    
    /* Streams an asynchronous sequence of raw PCM chunks simultaneously to primary and monitor audio sinks */
    public async Task<Result> StreamPcmAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> pcmChunks, 
        AudioFormat format, 
        CancellationToken cancellationToken = default)
    {
        string? primaryId;
        string? monitorId;

        /* Snapshots current device ID inside the lock */
        lock (_sessionLock)
        {
            primaryId = _primaryDeviceId;
            monitorId = _monitorDeviceId;
        }
        
        bool playPrimary = !string.IsNullOrEmpty(primaryId);
        bool playMonitor = !string.IsNullOrEmpty(monitorId);
        
        /* If both disabled returns */
        if (!playPrimary && !playMonitor)
            return Result.Success();

        /* Terminates any previous playback */
        Stop();

        /* Sets up the linked cts for this session */
        CancellationToken linkedToken;
        lock (_sessionLock)
        {
            _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedToken = _playbackCts.Token;
            _isPaused = false;
        }

        var sessionTargets = new List<ActiveSinkTarget>();
        string? lastOpenError = null;

        if (playPrimary)
        {
            var session = _backend.OpenSinkSession(primaryId, format);
            if (session.IsSuccess && session.Value != null)
            {
                var target = new ActiveSinkTarget(session.Value, () => _primaryVolume);
                lock (_sessionLock)
                {
                    _activeTargets.Add(target);
                }
                sessionTargets.Add(target);
            }
            else
            {
                lastOpenError = session.ErrorMessage;
            }
        }
        
        /* Launches secondary audio sink session if any */
        if (playMonitor)
        {
            var session = _backend.OpenSinkSession(monitorId, format);
            if (session.IsSuccess && session.Value != null)
            {
                var target = new ActiveSinkTarget(session.Value, () => _monitorVolume);
                lock (_sessionLock)
                {
                    _activeTargets.Add(target);
                }
                sessionTargets.Add(target);
            }
            else
            {
                lastOpenError = session.ErrorMessage;
            }
        }
        
        if (sessionTargets.Count == 0)
        {
            return Result.Failure(
                lastOpenError ?? "Failed to initialize any Linux audio output stream targets.");
        }

        int bytesPerSample = Math.Max(format.BitDepth / 8, 1);
        int blockAlign = Math.Max(format.Channels * bytesPerSample, 1);
        int bytesPerSecond = Math.Max(format.SampleRate * blockAlign, blockAlign);
        /* ~50ms slice aligned to block boundary */
        int sliceSize = Math.Max((bytesPerSecond / 20) / blockAlign * blockAlign, blockAlign);
        byte[] scratchBuffer = new byte[sliceSize];
        string? lastWriteError = null;

        try
        {
            /* Consumes incoming audio chunks as soon as they finish synthesizing */
            await foreach (var chunk in pcmChunks.WithCancellation(linkedToken))
            {
                if (chunk.Length == 0)
                    continue;

                int remaining = chunk.Length;
                int currentPos = 0;

                while (remaining > 0 && !linkedToken.IsCancellationRequested)
                {
                    int bytesToWrite = Math.Min(remaining, sliceSize);
                    /* Keep multi-byte samples aligned */
                    if (bytesToWrite > blockAlign)
                        bytesToWrite -= bytesToWrite % blockAlign;

                    int activeCount = 0;

                    /* Writes to all active targets with per-sink error isolation */
                    foreach (var target in sessionTargets)
                    {
                        if (!target.Session.IsActive)
                            continue;

                        chunk.Span.Slice(currentPos, bytesToWrite).CopyTo(scratchBuffer);

                        // SIMD volume scaling in-place
                        float liveVolume = target.VolumeProvider();
                        SimdAudioProcessor.ScaleVolumeInPlace(scratchBuffer.AsSpan(0, bytesToWrite), liveVolume);

                        var writeRes = await target.Session.WriteAsync(
                            scratchBuffer.AsMemory(0, bytesToWrite),
                            linkedToken);

                        if (writeRes.IsFailure)
                        {
                            lastWriteError = writeRes.ErrorMessage;
                            target.Session.Dispose();
                        }
                        else
                        {
                            activeCount++;
                        }
                    }

                    if (activeCount == 0)
                    {
                        return Result.Failure(
                            lastWriteError ?? "All Linux audio output sinks terminated during playback.");
                    }

                    currentPos += bytesToWrite;
                    remaining -= bytesToWrite;
                }
            }

            /* Waits until all active sink sessions finish draining their remaining samples */
            foreach (var target in sessionTargets)
            {
                if (!target.Session.IsActive)
                    continue;

                var drainRes = await target.Session.CompleteAsync(linkedToken);
                if (drainRes.IsFailure)
                    lastWriteError = drainRes.ErrorMessage;
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Playback cancelled");
        }
        catch (Exception ex)
        {
            return Result.Failure($"Streaming audio failed: {ex.Message}", ex);
        }
        finally
        {
            Stop();
        }
    }

    /* Pauses playback on all active sink sessions */
    public void Pause()
    {
        lock (_sessionLock)
        {
            if (_isPaused) return;
            foreach (var target in _activeTargets)
            {
                if (target.Session.IsActive)
                {
                    target.Session.Pause();
                }
            }
            _isPaused = true;
        }
    }

    /* Resumes playback on all paused sink sessions */
    public void Resume()
    {
        lock (_sessionLock)
        {
            if (!_isPaused) return;
            foreach (var target in _activeTargets)
            {
                if (target.Session.IsActive)
                {
                    target.Session.Resume();
                }
            }
            _isPaused = false;
        }
    }

    /* Immediately terminates playback and disposes of all active audio sink sessions */
    public void Stop()
    {
        lock (_sessionLock)
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = null;

            foreach (var target in _activeTargets)
            {
                try
                {
                    target.Session.Dispose();
                }
                catch { }
            }
            _activeTargets.Clear();
            _isPaused = false;
        }
    }

    /* Binds an active audio sink session with its live volume provider */
    private sealed record ActiveSinkTarget(IAudioSinkSession Session, Func<float> VolumeProvider);
}
