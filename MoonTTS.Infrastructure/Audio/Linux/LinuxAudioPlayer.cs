using System.Diagnostics;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Infrastructure.Audio.Linux;


namespace MoonTTS.Infrastructure.Audio;

/* File Structure:
        - GetAvailableDevices() : Result<List<AudioDeviceInfo>>
        - SetOutputDevices(string? primaryDeviceId, string? monitorDeviceId) : Result
        - SetVolume(int? primaryVolume, int? monitorVolume) : void
        - PlayPcmAsync(ReadOnlyMemory<byte> pcmBytes, AudioFormat format, CancellationToken cancellationToken) : Task<Result>
        - StreamPcmAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcmChunks, AudioFormat format, CancellationToken cancellationToken) : Task<Result>
        - Pause() : void
        - Resume() : void
        - Stop() : void
*/
public class LinuxAudioPlayer : IAudioPlayer
{
    private const string KeepCurrent = "__keep_current__";
    
    private string? _primaryDeviceId = "-1";
    private string? _monitorDeviceId = null;

    private float _primaryVolume = 1.0f;
    private float _monitorVolume = 1.0f;

    private readonly Lock _processLock = new();
    private readonly List<Process> _activeProcesses = new();
    
    private CancellationTokenSource? _playbackCts;
    private bool _isPaused = false;

    /*Returns a list with all the available Windows audio output devices visible by the application*/
    public Result<List<AudioDeviceInfo>> GetAvailableDevices()
    {
        try
        {
            var devices = new List<AudioDeviceInfo>
            {
                new AudioDeviceInfo
                {
                    DeviceId = "-1",
                    DeviceName = "Default system device"
                }
            };

            var micResult = LinuxVirtualMicService.EnsureCreated();
            if (micResult.IsSuccess)
            {
                devices.Add(new AudioDeviceInfo
                {
                    DeviceId = LinuxVirtualMicService.SinkName,
                    DeviceName = "Moon-TTS Virtual Microphone"
                });
            }

            var nativeSinks = LinuxDeviceEnumerator.GetSinks();
            foreach (var s in nativeSinks)
            {
                if (devices.All(existing => existing.DeviceId != s.RealName))
                {
                    devices.Add(new AudioDeviceInfo
                    {
                        DeviceId = s.RealName,
                        DeviceName = s.DisplayName
                    });
                }
            }

            return Result<List<AudioDeviceInfo>>.Success(devices);
        }
        catch (Exception ex)
        {
            return Result<List<AudioDeviceInfo>>.Failure(
                $"Failed to retrieve audio devices: {ex.Message}", ex);
        }
    }

    /* Config */
    
    /* Sets audio outputs individually or both at the same time */
    public Result SetOutputDevices(string? primaryDeviceId = KeepCurrent, string? monitorDeviceId = KeepCurrent)
    {
        lock (_processLock)
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
        lock (_processLock)
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
        lock (_processLock)
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
        lock (_processLock)
        {
            _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedToken = _playbackCts.Token;
            _isPaused = false;
        }

        var targets = new List<LinuxProcessRunner.SinkStreamTarget>();

        void RegisterProcess(Process p)
        {
            lock (_processLock)
            {
                _activeProcesses.Add(p);
            }
        }

        if (playPrimary)
        {
            string? targetSink = primaryId == "-1" ? null : primaryId;
            var session = LinuxAudioStreamer.StartSinkSession(targetSink, format, () => _primaryVolume, RegisterProcess);
            if (session.IsSuccess && session.Value != null) targets.Add(session.Value);
        }
        
        /* Lauches secondary audio process if any */
        if (playMonitor)
        {
            string? targetSink = monitorId == "-1" ? null : monitorId;
            var session = LinuxAudioStreamer.StartSinkSession(targetSink, format, () => _monitorVolume, RegisterProcess);
            if (session.IsSuccess && session.Value != null) targets.Add(session.Value);
        }
        
        if (targets.Count == 0)
            return Result.Failure("Failed to initialize any Linux audio output stream targets.");

        try
        {
            return await LinuxProcessRunner.PipeStreamedAudioAsync(targets, pcmChunks, format, linkedToken);
        }
        finally
        {
            Stop();
        }
    }

    /* Pauses playback on all active sink processes via SIGSTOP */
    public void Pause()
    {
        lock (_processLock)
        {
            if (_isPaused) return;
            foreach (var proc in _activeProcesses.Where(p => !p.HasExited))
            {
                LinuxProcessRunner.SendPauseSignal(proc.Id);
            }
            _isPaused = true;
        }
    }

    /* Resumes playback on all paused sink processes via SIGCONT */
    public void Resume()
    {
        lock (_processLock)
        {
            if (!_isPaused) return;
            foreach (var proc in _activeProcesses.Where(p => !p.HasExited))
            {
                LinuxProcessRunner.SendResumeSignal(proc.Id);
            }
            _isPaused = false;
        }
    }

    /* Immediately terminates playback and disposes of all spawned audio processes */
    public void Stop()
    {
        lock (_processLock)
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = null;

            foreach (var proc in _activeProcesses)
            {
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                    proc.Dispose();
                }
                catch { }
            }
            _activeProcesses.Clear();
            _isPaused = false;
        }
    }
}