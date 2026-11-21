using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;

namespace MoonTTS.Infrastructure.Audio;

/* File Structure:
        - GetAvailableDevices() : Result<List<AudioDeviceInfo>>
        - SetOutputDevices(string? primaryDeviceId, string? monitorDeviceId) : Result
        - PlayPcmAsync(ReadOnlyMemory<byte> pcmBytes, AudioFormat format, CancellationToken cancellationToken) : Task<Result>
        - StreamPcmAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcmChunks, AudioFormat format, CancellationToken cancellationToken) : Task<Result>
        - SetVolume(int? primaryVolume, int? monitorVolume) : void
        - Pause() : void
        - Resume() : void
        - Stop() : void
        - OpenTarget(string deviceId, AudioFormat format, Func<float> volumeProvider) : Result<WasapiTarget>
        - ResolveEndpoint(nint enumerator, string deviceId) : nint
        - WriteChunkToTargetAsync(WasapiTarget target, ReadOnlyMemory<byte> chunk, CancellationToken token) : Task<Result>
        - DrainTargetAsync(WasapiTarget target, CancellationToken token) : Task
        - WaitForBufferSpaceAsync(WasapiTarget target, int timeoutMs, CancellationToken token) : Task
        - WaitHandleAsync(WaitHandle handle, int timeoutMs, CancellationToken token) : Task<bool>
        - DisposeTarget(WasapiTarget target) : void
        - ReadDeviceId(nint device) : string?
        - ReadFriendlyName(nint device, int fallbackIndex) : string
        - EnsureComInitialized() : bool
        - WasapiTarget
        - LibWasapi
*/

/* Windows audio player adapter built directly on WASAPI (IMMDeviceEnumerator / IAudioClient / IAudioRenderClient)
   via AOT-safe COM vtable calls. Provides full untruncated device names, stable Windows Endpoint GUIDs,
   OS-native sample-rate conversion (AUTOCONVERTPCM), low-latency shared-mode playback, and dual-device routing */
public class WindowsAudioPlayer : IAudioPlayer
{
    private const string DefaultDeviceId = "-1";
    private const long SharedBufferDuration100Ns = 100 * 10_000; /* 100ms shared-mode ring buffer */
    private const int WaitTimeoutMs = 15;

    private readonly List<WasapiTarget> _activeTargets = new();
    private readonly Lock _processLock = new();

    private string? _primaryDeviceId = DefaultDeviceId;
    private string? _monitorDeviceId;

    private float _primaryVolume = 1.0f;
    private float _monitorVolume = 1.0f;

    private CancellationTokenSource? _playbackCts;
    private bool _isPaused;

    /* Enumerates active Windows WASAPI render endpoints with full untruncated friendly names and stable endpoint IDs */
    public unsafe Result<List<AudioDeviceInfo>> GetAvailableDevices()
    {
        bool uninitCom = EnsureComInitialized();
        nint enumerator = 0;
        nint collection = 0;

        try
        {
            var devices = new List<AudioDeviceInfo>
            {
                new AudioDeviceInfo
                {
                    DeviceId = DefaultDeviceId,
                    DeviceName = "Default System Device",
                }
            };

            enumerator = CreateDeviceEnumerator();
            if (enumerator == 0)
                return Result<List<AudioDeviceInfo>>.Failure("Failed to create WASAPI IMMDeviceEnumerator.");

            int hr = LibWasapi.EnumAudioEndpoints(enumerator, LibWasapi.EDataFlowRender, LibWasapi.DeviceStateActive, out collection);
            if (hr < 0 || collection == 0)
                return Result<List<AudioDeviceInfo>>.Failure($"WASAPI EnumAudioEndpoints failed (HRESULT 0x{hr:X8}).");

            hr = LibWasapi.CollectionGetCount(collection, out uint count);
            if (hr < 0)
                return Result<List<AudioDeviceInfo>>.Failure($"WASAPI GetCount failed (HRESULT 0x{hr:X8}).");

            for (uint i = 0; i < count; i++)
            {
                if (LibWasapi.CollectionItem(collection, i, out nint device) < 0 || device == 0)
                    continue;

                try
                {
                    string? id = ReadDeviceId(device);
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    string name = ReadFriendlyName(device, (int)i);
                    devices.Add(new AudioDeviceInfo
                    {
                        DeviceId = id,
                        DeviceName = name
                    });
                }
                finally
                {
                    LibWasapi.Release(device);
                }
            }

            return Result<List<AudioDeviceInfo>>.Success(devices);
        }
        catch (Exception ex)
        {
            return Result<List<AudioDeviceInfo>>.Failure(
                $"Failed to retrieve Windows WASAPI audio devices: {ex.Message}", ex);
        }
        finally
        {
            if (collection != 0) LibWasapi.Release(collection);
            if (enumerator != 0) LibWasapi.Release(enumerator);
            if (uninitCom) LibWasapi.CoUninitialize();
        }
    }

    /* Sets the primary and monitor audio output endpoint IDs ("-1" for system default, null/empty to disable) */
    public Result SetOutputDevices(
        string? primaryDeviceId = IAudioPlayer.KeepCurrent,
        string? monitorDeviceId = IAudioPlayer.KeepCurrent)
    {
        lock (_processLock)
        {
            if (primaryDeviceId != IAudioPlayer.KeepCurrent)
            {
                _primaryDeviceId = !string.IsNullOrWhiteSpace(primaryDeviceId) ? primaryDeviceId.Trim() : null;
            }

            if (monitorDeviceId != IAudioPlayer.KeepCurrent)
            {
                _monitorDeviceId = !string.IsNullOrWhiteSpace(monitorDeviceId) ? monitorDeviceId.Trim() : null;
            }
        }

        return Result.Success();
    }

    /* Sets live volume attenuation factors for primary and monitor sinks */
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

    /* Plays a single memory buffer of raw PCM audio data via WASAPI */
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

    /* Streams incoming PCM chunks gaplessly and concurrently to configured WASAPI primary and monitor endpoints */
    public async Task<Result> StreamPcmAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> pcmChunks,
        AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        string? primaryId;
        string? monitorId;

        lock (_processLock)
        {
            primaryId = _primaryDeviceId;
            monitorId = _monitorDeviceId;
        }

        bool playPrimary = !string.IsNullOrEmpty(primaryId);
        bool playMonitor = !string.IsNullOrEmpty(monitorId);

        if (!playPrimary && !playMonitor)
            return Result.Success();

        Stop();

        CancellationTokenSource sessionCts;
        CancellationToken linkedToken;
        lock (_processLock)
        {
            sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _playbackCts = sessionCts;
            linkedToken = sessionCts.Token;
            _isPaused = false;
        }

        var sessionTargets = new List<WasapiTarget>(2);
        string? lastOpenError = null;

        try
        {
            if (playPrimary)
            {
                var primaryTarget = OpenTarget(primaryId!, format, () => Volatile.Read(ref _primaryVolume));
                if (primaryTarget.IsSuccess)
                {
                    lock (_processLock) { _activeTargets.Add(primaryTarget.Value); }
                    sessionTargets.Add(primaryTarget.Value);
                }
                else
                {
                    lastOpenError = primaryTarget.ErrorMessage;
                }
            }

            if (playMonitor)
            {
                var monitorTarget = OpenTarget(monitorId!, format, () => Volatile.Read(ref _monitorVolume));
                if (monitorTarget.IsSuccess)
                {
                    lock (_processLock) { _activeTargets.Add(monitorTarget.Value); }
                    sessionTargets.Add(monitorTarget.Value);
                }
                else
                {
                    lastOpenError = monitorTarget.ErrorMessage;
                }
            }

            if (sessionTargets.Count == 0)
                return Result.Failure(lastOpenError ?? "Failed to initialize any Windows WASAPI audio output targets.");

            await foreach (var chunk in pcmChunks.WithCancellation(linkedToken))
            {
                if (chunk.IsEmpty)
                    continue;

                if (sessionTargets.Count == 1)
                {
                    var res = await WriteChunkToTargetAsync(sessionTargets[0], chunk, linkedToken);
                    if (res.IsFailure)
                        return res;
                }
                else
                {
                    var tasks = new Task<Result>[sessionTargets.Count];
                    for (int i = 0; i < sessionTargets.Count; i++)
                        tasks[i] = WriteChunkToTargetAsync(sessionTargets[i], chunk, linkedToken);

                    Result[] results = await Task.WhenAll(tasks);
                    int succeeded = 0;
                    Result? firstFailure = null;
                    foreach (var r in results)
                    {
                        if (r.IsSuccess) succeeded++;
                        else firstFailure ??= r;
                    }

                    if (succeeded == 0 && firstFailure.HasValue)
                        return firstFailure.Value;
                }
            }

            /* Wait concurrently for all hardware render buffers to finish draining */
            if (sessionTargets.Count == 1)
            {
                await DrainTargetAsync(sessionTargets[0], linkedToken);
            }
            else
            {
                var drainTasks = new Task[sessionTargets.Count];
                for (int i = 0; i < sessionTargets.Count; i++)
                    drainTasks[i] = DrainTargetAsync(sessionTargets[i], linkedToken);
                await Task.WhenAll(drainTasks);
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Playback cancelled");
        }
        catch (Exception ex)
        {
            AppLogger.Error("WindowsAudioPlayer", "WASAPI streaming failed", ex);
            return Result.Failure($"Streaming Windows audio failed: {ex.Message}", ex);
        }
        finally
        {
            lock (_processLock)
            {
                foreach (var target in sessionTargets)
                {
                    _activeTargets.Remove(target);
                    DisposeTarget(target);
                }

                if (ReferenceEquals(_playbackCts, sessionCts))
                    _playbackCts = null;

                if (_activeTargets.Count == 0)
                    _isPaused = false;
            }
            sessionCts.Dispose();
        }
    }

    /* Pauses all active WASAPI audio clients */
    public void Pause()
    {
        lock (_processLock)
        {
            if (_isPaused) return;
            foreach (var target in _activeTargets)
            {
                if (!target.IsDisposed && target.AudioClient != 0)
                    LibWasapi.AudioClientStop(target.AudioClient);
            }
            _isPaused = true;
        }
    }

    /* Resumes all paused WASAPI audio clients */
    public void Resume()
    {
        lock (_processLock)
        {
            if (!_isPaused) return;
            foreach (var target in _activeTargets)
            {
                if (!target.IsDisposed && target.AudioClient != 0)
                {
                    LibWasapi.AudioClientStart(target.AudioClient);
                    target.BufferEvent?.Set();
                }
            }
            _isPaused = false;
        }
    }

    /* Cancels active playback, stops and resets hardware clients, and releases COM interfaces */
    public void Stop()
    {
        lock (_processLock)
        {
            /* Cancel-only; the owning StreamPcmAsync session disposes its CTS in finally */
            _playbackCts?.Cancel();

            foreach (var target in _activeTargets)
            {
                DisposeTarget(target);
            }

            _activeTargets.Clear();
            _isPaused = false;
        }
    }

    /* Opens and starts a shared-mode WASAPI render client with OS automatic sample-rate conversion */
    private static unsafe Result<WasapiTarget> OpenTarget(
        string deviceId,
        AudioFormat format,
        Func<float> volumeProvider)
    {
        bool uninitCom = EnsureComInitialized();
        nint enumerator = 0;
        nint device = 0;
        nint audioClient = 0;
        nint renderClient = 0;
        AutoResetEvent? bufferEvent = null;

        try
        {
            enumerator = CreateDeviceEnumerator();
            if (enumerator == 0)
                return Result<WasapiTarget>.Failure("Failed to create WASAPI IMMDeviceEnumerator.");

            device = ResolveEndpoint(enumerator, deviceId);
            if (device == 0)
                return Result<WasapiTarget>.Failure($"Failed to resolve Windows audio endpoint '{deviceId}'.");

            Guid iidAudioClient = LibWasapi.IID_IAudioClient;
            int hr = LibWasapi.DeviceActivate(device, ref iidAudioClient, LibWasapi.CLSCTX_ALL, 0, out audioClient);
            if (hr < 0 || audioClient == 0)
                return Result<WasapiTarget>.Failure($"WASAPI IMMDevice.Activate failed (HRESULT 0x{hr:X8}).");

            int channels = Math.Max((int)format.Channels, 1);
            int bitDepth = Math.Max((int)format.BitDepth, 8);
            int sampleRate = Math.Max(format.SampleRate, 8000);
            int bytesPerSample = Math.Max(bitDepth / 8, 1);
            ushort blockAlign = (ushort)Math.Max(channels * bytesPerSample, 1);

            var wfx = new LibWasapi.WAVEFORMATEX
            {
                wFormatTag = LibWasapi.WAVE_FORMAT_PCM,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                nAvgBytesPerSec = (uint)(sampleRate * blockAlign),
                nBlockAlign = blockAlign,
                wBitsPerSample = (ushort)bitDepth,
                cbSize = 0
            };

            uint baseFlags = LibWasapi.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | LibWasapi.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            bool useEventCallback = true;

            hr = LibWasapi.AudioClientInitialize(
                audioClient,
                LibWasapi.AUDCLNT_SHAREMODE_SHARED,
                baseFlags | LibWasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                SharedBufferDuration100Ns,
                0,
                ref wfx,
                0);

            /* Fallback without EVENTCALLBACK on older drivers that reject event-driven autoconvert */
            if (hr < 0)
            {
                useEventCallback = false;
                hr = LibWasapi.AudioClientInitialize(
                    audioClient,
                    LibWasapi.AUDCLNT_SHAREMODE_SHARED,
                    baseFlags,
                    SharedBufferDuration100Ns,
                    0,
                    ref wfx,
                    0);
            }

            if (hr < 0)
                return Result<WasapiTarget>.Failure($"WASAPI IAudioClient.Initialize failed (HRESULT 0x{hr:X8}).");

            if (useEventCallback)
            {
                bufferEvent = new AutoResetEvent(false);
                hr = LibWasapi.AudioClientSetEventHandle(audioClient, bufferEvent.SafeWaitHandle.DangerousGetHandle());
                if (hr < 0)
                {
                    bufferEvent.Dispose();
                    bufferEvent = null;
                }
            }

            hr = LibWasapi.AudioClientGetBufferSize(audioClient, out uint bufferFrameCount);
            if (hr < 0 || bufferFrameCount == 0)
                return Result<WasapiTarget>.Failure($"WASAPI GetBufferSize failed (HRESULT 0x{hr:X8}).");

            Guid iidRenderClient = LibWasapi.IID_IAudioRenderClient;
            hr = LibWasapi.AudioClientGetService(audioClient, ref iidRenderClient, out renderClient);
            if (hr < 0 || renderClient == 0)
                return Result<WasapiTarget>.Failure($"WASAPI GetService(IAudioRenderClient) failed (HRESULT 0x{hr:X8}).");

            hr = LibWasapi.AudioClientStart(audioClient);
            if (hr < 0)
                return Result<WasapiTarget>.Failure($"WASAPI IAudioClient.Start failed (HRESULT 0x{hr:X8}).");

            var target = new WasapiTarget(
                audioClient,
                renderClient,
                bufferEvent,
                bufferFrameCount,
                blockAlign,
                bitDepth,
                sampleRate,
                volumeProvider);

            /* Ownership transferred to target; clear locals so finally doesn't release them */
            audioClient = 0;
            renderClient = 0;
            bufferEvent = null;
            return Result<WasapiTarget>.Success(target);
        }
        catch (Exception ex)
        {
            return Result<WasapiTarget>.Failure($"Failed to open WASAPI audio target: {ex.Message}", ex);
        }
        finally
        {
            bufferEvent?.Dispose();
            if (renderClient != 0) LibWasapi.Release(renderClient);
            if (audioClient != 0) LibWasapi.Release(audioClient);
            if (device != 0) LibWasapi.Release(device);
            if (enumerator != 0) LibWasapi.Release(enumerator);
            if (uninitCom) LibWasapi.CoUninitialize();
        }
    }

    /* Resolves a WASAPI endpoint ID, legacy numeric index, or display name, falling back to system default */
    private static unsafe nint ResolveEndpoint(nint enumerator, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == DefaultDeviceId)
        {
            LibWasapi.GetDefaultAudioEndpoint(enumerator, LibWasapi.EDataFlowRender, LibWasapi.ERoleConsole, out nint defDev);
            return defDev;
        }

        /* Direct lookup by stable WASAPI endpoint ID */
        fixed (char* idPtr = deviceId)
        {
            if (LibWasapi.GetDevice(enumerator, idPtr, out nint directDev) >= 0 && directDev != 0)
            {
                if (LibWasapi.DeviceGetState(directDev, out uint state) >= 0 && state == LibWasapi.DeviceStateActive)
                    return directDev;

                LibWasapi.Release(directDev);
            }
        }

        /* Fallback for legacy WinMM numeric IDs or friendly names stored in older settings.json files */
        if (LibWasapi.EnumAudioEndpoints(enumerator, LibWasapi.EDataFlowRender, LibWasapi.DeviceStateActive, out nint collection) >= 0
            && collection != 0)
        {
            try
            {
                if (LibWasapi.CollectionGetCount(collection, out uint count) >= 0)
                {
                    if (int.TryParse(deviceId, out int legacyIdx) && legacyIdx >= 0 && legacyIdx < count)
                    {
                        if (LibWasapi.CollectionItem(collection, (uint)legacyIdx, out nint byIndex) >= 0 && byIndex != 0)
                            return byIndex;
                    }

                    for (uint i = 0; i < count; i++)
                    {
                        if (LibWasapi.CollectionItem(collection, i, out nint candidate) < 0 || candidate == 0)
                            continue;

                        string name = ReadFriendlyName(candidate, (int)i);
                        if (string.Equals(name, deviceId, StringComparison.OrdinalIgnoreCase))
                            return candidate;

                        LibWasapi.Release(candidate);
                    }
                }
            }
            finally
            {
                LibWasapi.Release(collection);
            }
        }

        /* Device was unplugged: fall back gracefully to system default render endpoint */
        LibWasapi.GetDefaultAudioEndpoint(enumerator, LibWasapi.EDataFlowRender, LibWasapi.ERoleConsole, out nint fallback);
        return fallback;
    }

    /* Copies and SIMD-scales one PCM chunk directly into the WASAPI render ring buffer */
    private async Task<Result> WriteChunkToTargetAsync(
        WasapiTarget target,
        ReadOnlyMemory<byte> chunk,
        CancellationToken token)
    {
        int blockAlign = target.BlockAlign;
        int totalFrames = chunk.Length / blockAlign;
        int frameOffset = 0;

        while (frameOffset < totalFrames && !token.IsCancellationRequested)
        {
            uint availableFrames;
            int copyResult = 0;
            int framesToWrite = 0;

            lock (_processLock)
            {
                if (target.IsDisposed || target.AudioClient == 0 || target.RenderClient == 0)
                    return Result.Failure("WASAPI audio output target was closed.");

                if (_isPaused)
                {
                    availableFrames = 0;
                }
                else
                {
                    int padHr = LibWasapi.AudioClientGetCurrentPadding(target.AudioClient, out uint padding);
                    if (padHr < 0)
                        return Result.Failure($"WASAPI GetCurrentPadding failed (HRESULT 0x{padHr:X8}).");

                    availableFrames = target.BufferFrameCount > padding
                        ? target.BufferFrameCount - padding
                        : 0;

                    if (availableFrames > 0)
                    {
                        framesToWrite = (int)Math.Min(availableFrames, (uint)(totalFrames - frameOffset));
                        copyResult = WriteFramesDirectly(
                            target,
                            chunk.Span.Slice(frameOffset * blockAlign, framesToWrite * blockAlign),
                            framesToWrite);
                    }
                }
            }

            if (copyResult < 0)
                return Result.Failure($"WASAPI buffer write failed (HRESULT 0x{copyResult:X8}).");

            if (framesToWrite > 0)
            {
                frameOffset += framesToWrite;
                continue;
            }

            await WaitForBufferSpaceAsync(target, WaitTimeoutMs, token);
        }

        return Result.Success();
    }

    /* Writes PCM frames directly into the WASAPI native buffer and applies SIMD volume scaling in-place */
    private static unsafe int WriteFramesDirectly(WasapiTarget target, ReadOnlySpan<byte> pcmSlice, int frames)
    {
        int hr = LibWasapi.RenderClientGetBuffer(target.RenderClient, (uint)frames, out nint dataPtr);
        if (hr < 0 || dataPtr == 0)
            return hr < 0 ? hr : unchecked((int)0x80004005);

        var destSpan = new Span<byte>((void*)dataPtr, pcmSlice.Length);
        pcmSlice.CopyTo(destSpan);

        if (target.BitDepth == 16)
        {
            float liveVolume = target.VolumeProvider();
            SimdAudioProcessor.ScaleVolumeInPlace(destSpan, liveVolume);
        }

        return LibWasapi.RenderClientReleaseBuffer(target.RenderClient, (uint)frames, 0);
    }

    /* Waits until all queued frames in the WASAPI shared ring buffer finish playing */
    private async Task DrainTargetAsync(WasapiTarget target, CancellationToken token)
    {
        int maxWaitIterations = Math.Max(20, (int)(target.BufferFrameCount * 1000 / Math.Max(target.SampleRate, 8000) / WaitTimeoutMs) + 40);

        for (int iter = 0; iter < maxWaitIterations && !token.IsCancellationRequested; iter++)
        {
            uint padding;
            bool paused;
            lock (_processLock)
            {
                if (target.IsDisposed || target.AudioClient == 0)
                    return;

                paused = _isPaused;
                if (!paused)
                {
                    int hr = LibWasapi.AudioClientGetCurrentPadding(target.AudioClient, out padding);
                    if (hr < 0 || padding == 0)
                        return;
                }
                else
                {
                    padding = 1;
                }
            }

            if (paused)
                iter = 0; /* Do not time out drain while user explicitly paused playback */

            await WaitForBufferSpaceAsync(target, WaitTimeoutMs, token);
        }
    }

    /* Waits on the WASAPI buffer event when available, otherwise yields for a short interval */
    private static Task WaitForBufferSpaceAsync(WasapiTarget target, int timeoutMs, CancellationToken token)
    {
        if (target.BufferEvent != null)
            return WaitHandleAsync(target.BufferEvent, timeoutMs, token);

        return Task.Delay(timeoutMs, token);
    }

    /* Holds registration state for non-blocking WaitHandle awaiting without ThreadPool thread blocking */
    private sealed class WaitHandleState
    {
        public TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RegisteredWaitHandle? RegisteredWait;
        public CancellationTokenRegistration CancellationReg;
        public int Completed;

        public void Complete(bool timedOut)
        {
            if (Interlocked.Exchange(ref Completed, 1) != 0)
                return;

            RegisteredWait?.Unregister(null);
            CancellationReg.Dispose();
            Tcs.TrySetResult(!timedOut);
        }

        public void Cancel()
        {
            if (Interlocked.Exchange(ref Completed, 1) != 0)
                return;

            RegisteredWait?.Unregister(null);
            CancellationReg.Dispose();
            Tcs.TrySetCanceled();
        }
    }

    /* Awaits an OS WaitHandle asynchronously using ThreadPool wait registration */
    private static Task<bool> WaitHandleAsync(WaitHandle handle, int timeoutMs, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return Task.FromCanceled<bool>(token);

        var state = new WaitHandleState();
        state.RegisteredWait = ThreadPool.RegisterWaitForSingleObject(
            handle,
            static (rawState, timedOut) => ((WaitHandleState)rawState!).Complete(timedOut),
            state,
            timeoutMs,
            executeOnlyOnce: true);

        if (token.CanBeCanceled)
        {
            state.CancellationReg = token.Register(
                static rawState => ((WaitHandleState)rawState!).Cancel(),
                state);
        }

        return state.Tcs.Task;
    }

    /* Stops, resets, and releases all COM objects owned by a WasapiTarget (caller must hold _processLock) */
    private static void DisposeTarget(WasapiTarget target)
    {
        if (target.IsDisposed)
            return;

        target.IsDisposed = true;

        if (target.AudioClient != 0)
        {
            LibWasapi.AudioClientStop(target.AudioClient);
            LibWasapi.AudioClientReset(target.AudioClient);
        }

        if (target.RenderClient != 0)
        {
            LibWasapi.Release(target.RenderClient);
            target.RenderClient = 0;
        }

        if (target.AudioClient != 0)
        {
            LibWasapi.Release(target.AudioClient);
            target.AudioClient = 0;
        }

        if (target.BufferEvent != null)
        {
            try
            {
                target.BufferEvent.Set();
                target.BufferEvent.Dispose();
            }
            catch
            {
                /* Ignore disposal race on wait handle */
            }
        }
    }

    /* Instantiates IMMDeviceEnumerator via ole32 CoCreateInstance */
    private static nint CreateDeviceEnumerator()
    {
        Guid clsid = LibWasapi.CLSID_MMDeviceEnumerator;
        Guid iid = LibWasapi.IID_IMMDeviceEnumerator;
        int hr = LibWasapi.CoCreateInstance(ref clsid, 0, LibWasapi.CLSCTX_ALL, ref iid, out nint instance);
        return hr >= 0 ? instance : 0;
    }

    /* Reads the stable Windows endpoint GUID string from IMMDevice::GetId */
    private static string? ReadDeviceId(nint device)
    {
        if (LibWasapi.DeviceGetId(device, out nint idPtr) < 0 || idPtr == 0)
            return null;

        try
        {
            return Marshal.PtrToStringUni(idPtr);
        }
        finally
        {
            LibWasapi.CoTaskMemFree(idPtr);
        }
    }

    /* Reads the full untruncated PKEY_Device_FriendlyName string from IMMDevice::OpenPropertyStore */
    private static string ReadFriendlyName(nint device, int fallbackIndex)
    {
        if (LibWasapi.DeviceOpenPropertyStore(device, LibWasapi.STGM_READ, out nint propStore) < 0 || propStore == 0)
            return $"Audio Output Device {fallbackIndex}";

        try
        {
            LibWasapi.PROPERTYKEY key = LibWasapi.PKEY_Device_FriendlyName;
            if (LibWasapi.PropertyStoreGetValue(propStore, ref key, out LibWasapi.PROPVARIANT pv) < 0)
                return $"Audio Output Device {fallbackIndex}";

            try
            {
                if (pv.vt == LibWasapi.VT_LPWSTR && pv.pwszVal != 0)
                {
                    string? name = Marshal.PtrToStringUni(pv.pwszVal)?.Trim();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
            finally
            {
                LibWasapi.PropVariantClear(ref pv);
            }
        }
        finally
        {
            LibWasapi.Release(propStore);
        }

        return $"Audio Output Device {fallbackIndex}";
    }

    /* Ensures COM is initialized on the calling thread; returns true when caller must balance with CoUninitialize */
    private static bool EnsureComInitialized()
    {
        int hr = LibWasapi.CoInitializeEx(0, LibWasapi.COINIT_MULTITHREADED);
        return hr == 0 || hr == 1; /* S_OK or S_FALSE */
    }

    /* Represents an active WASAPI shared-mode render stream */
    private sealed class WasapiTarget(
        nint audioClient,
        nint renderClient,
        AutoResetEvent? bufferEvent,
        uint bufferFrameCount,
        int blockAlign,
        int bitDepth,
        int sampleRate,
        Func<float> volumeProvider)
    {
        public nint AudioClient { get; set; } = audioClient;
        public nint RenderClient { get; set; } = renderClient;
        public AutoResetEvent? BufferEvent { get; } = bufferEvent;
        public uint BufferFrameCount { get; } = bufferFrameCount;
        public int BlockAlign { get; } = blockAlign;
        public int BitDepth { get; } = bitDepth;
        public int SampleRate { get; } = sampleRate;
        public Func<float> VolumeProvider { get; } = volumeProvider;
        public bool IsDisposed { get; set; }
    }

    /* Zero-reflection COM vtable wrappers and structs for Windows WASAPI (ole32.dll) */
    private static unsafe class LibWasapi
    {
        public const uint COINIT_MULTITHREADED = 0x0;
        public const uint CLSCTX_ALL = 0x17;
        public const uint EDataFlowRender = 0;
        public const uint ERoleConsole = 0;
        public const uint DeviceStateActive = 0x00000001;
        public const uint STGM_READ = 0;
        public const ushort VT_LPWSTR = 31;

        public const ushort WAVE_FORMAT_PCM = 1;
        public const uint AUDCLNT_SHAREMODE_SHARED = 0;
        public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;

        public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
        public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

        public static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public nint pwszVal;
        }

        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();

        [DllImport("ole32.dll")]
        public static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(nint pv);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT pvar);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Release(nint comObj)
        {
            nint* vtbl = *(nint**)comObj;
            return ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(comObj);
        }

        /* IMMDeviceEnumerator */
        public static int EnumAudioEndpoints(nint enumerator, uint dataFlow, uint stateMask, out nint devices)
        {
            nint* vtbl = *(nint**)enumerator;
            return ((delegate* unmanaged[Stdcall]<nint, uint, uint, out nint, int>)vtbl[3])(enumerator, dataFlow, stateMask, out devices);
        }

        public static int GetDefaultAudioEndpoint(nint enumerator, uint dataFlow, uint role, out nint endpoint)
        {
            nint* vtbl = *(nint**)enumerator;
            return ((delegate* unmanaged[Stdcall]<nint, uint, uint, out nint, int>)vtbl[4])(enumerator, dataFlow, role, out endpoint);
        }

        public static int GetDevice(nint enumerator, char* id, out nint device)
        {
            nint* vtbl = *(nint**)enumerator;
            return ((delegate* unmanaged[Stdcall]<nint, char*, out nint, int>)vtbl[5])(enumerator, id, out device);
        }

        /* IMMDeviceCollection */
        public static int CollectionGetCount(nint collection, out uint count)
        {
            nint* vtbl = *(nint**)collection;
            return ((delegate* unmanaged[Stdcall]<nint, out uint, int>)vtbl[3])(collection, out count);
        }

        public static int CollectionItem(nint collection, uint index, out nint device)
        {
            nint* vtbl = *(nint**)collection;
            return ((delegate* unmanaged[Stdcall]<nint, uint, out nint, int>)vtbl[4])(collection, index, out device);
        }

        /* IMMDevice */
        public static int DeviceActivate(nint device, ref Guid iid, uint clsCtx, nint activationParams, out nint iface)
        {
            nint* vtbl = *(nint**)device;
            return ((delegate* unmanaged[Stdcall]<nint, ref Guid, uint, nint, out nint, int>)vtbl[3])(device, ref iid, clsCtx, activationParams, out iface);
        }

        public static int DeviceOpenPropertyStore(nint device, uint stgmAccess, out nint propStore)
        {
            nint* vtbl = *(nint**)device;
            return ((delegate* unmanaged[Stdcall]<nint, uint, out nint, int>)vtbl[4])(device, stgmAccess, out propStore);
        }

        public static int DeviceGetId(nint device, out nint ppstrId)
        {
            nint* vtbl = *(nint**)device;
            return ((delegate* unmanaged[Stdcall]<nint, out nint, int>)vtbl[5])(device, out ppstrId);
        }

        public static int DeviceGetState(nint device, out uint state)
        {
            nint* vtbl = *(nint**)device;
            return ((delegate* unmanaged[Stdcall]<nint, out uint, int>)vtbl[6])(device, out state);
        }

        /* IPropertyStore */
        public static int PropertyStoreGetValue(nint propStore, ref PROPERTYKEY key, out PROPVARIANT pv)
        {
            nint* vtbl = *(nint**)propStore;
            return ((delegate* unmanaged[Stdcall]<nint, ref PROPERTYKEY, out PROPVARIANT, int>)vtbl[5])(propStore, ref key, out pv);
        }

        /* IAudioClient */
        public static int AudioClientInitialize(
            nint client,
            uint shareMode,
            uint streamFlags,
            long bufferDuration,
            long periodicity,
            ref WAVEFORMATEX format,
            nint sessionGuid)
        {
            nint* vtbl = *(nint**)client;
            return ((delegate* unmanaged[Stdcall]<nint, uint, uint, long, long, ref WAVEFORMATEX, nint, int>)vtbl[3])(
                client, shareMode, streamFlags, bufferDuration, periodicity, ref format, sessionGuid);
        }

        public static int AudioClientGetBufferSize(nint client, out uint frameCount)
        {
            nint* vtbl = *(nint**)client;
            return ((delegate* unmanaged[Stdcall]<nint, out uint, int>)vtbl[4])(client, out frameCount);
        }

        public static int AudioClientGetCurrentPadding(nint client, out uint paddingFrames)
        {
            nint* vtbl = *(nint**)client;
            return ((delegate* unmanaged[Stdcall]<nint, out uint, int>)vtbl[6])(client, out paddingFrames);
        }

        public static int AudioClientStart(nint client)
        {
            nint* vtbl = *(nint**)client;
            return ((delegate* unmanaged[Stdcall]<nint, int>)vtbl[10])(client);
        }

        public static int AudioClientStop(nint client)
        {
            nint* vtbl = *(nint**)client;
            return ((delegate* unmanaged[Stdcall]<nint, int>)vtbl[11])(client);
        }

        public static int AudioClientReset(nint client)
        {
            nint* vtbl = *(nint**)client;
            return ((delegate* unmanaged[Stdcall]<nint, int>)vtbl[12])(client);
        }

        public static int AudioClientSetEventHandle(nint client, nint eventHandle)
        {
            nint* vtbl = *(nint**)client;
            return ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[13])(client, eventHandle);
        }

        public static int AudioClientGetService(nint client, ref Guid riid, out nint service)
        {
            nint* vtbl = *(nint**)client;
            return ((delegate* unmanaged[Stdcall]<nint, ref Guid, out nint, int>)vtbl[14])(client, ref riid, out service);
        }

        /* IAudioRenderClient */
        public static int RenderClientGetBuffer(nint renderClient, uint numFramesRequested, out nint data)
        {
            nint* vtbl = *(nint**)renderClient;
            return ((delegate* unmanaged[Stdcall]<nint, uint, out nint, int>)vtbl[3])(renderClient, numFramesRequested, out data);
        }

        public static int RenderClientReleaseBuffer(nint renderClient, uint numFramesWritten, uint flags)
        {
            nint* vtbl = *(nint**)renderClient;
            return ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)vtbl[4])(renderClient, numFramesWritten, flags);
        }
    }
}
