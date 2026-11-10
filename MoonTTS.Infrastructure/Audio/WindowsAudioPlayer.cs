using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
        - TryResolveDeviceNumber(string deviceId, out int deviceNumber) : bool
        - OpenTarget(int deviceNumber, AudioFormat format, Func<float> volumeProvider) : Result<WaveOutTarget>
        - EnqueueBuffer(WaveOutTarget target, ReadOnlySpan<byte> pcmSlice, byte[] scratchBuffer) : Result
        - ReapCompletedBuffers(WaveOutTarget target) : int
        - WaitForTargetCapacityAsync(WaveOutTarget target, int maxInFlight, CancellationToken token) : Task
        - DrainTargetAsync(WaveOutTarget target, CancellationToken token) : Task
        - DisposeTarget(WaveOutTarget target) : void
        - ExtractDeviceName(ref LibWinMm.WAVEOUTCAPSW caps, uint index) : string
        - PreparedWaveBuffer
        - WaveOutTarget
        - LibWinMm
*/

public class WindowsAudioPlayer : IAudioPlayer
{
    private const int DefaultDevice = -1; /* -1 is WAVE_MAPPER (default for Windows) */
    private const int DisabledDevice = -2; /* -2 is disabled */
    private const int MaxInFlightBuffers = 8; /* ~400ms max queued audio per sink at 50ms slices */

    private readonly List<WaveOutTarget> _activeTargets = new();

    private int _primaryDeviceNumber = DefaultDevice; /* -1 is default for Windows */
    private int _monitorDeviceNumber = DisabledDevice; /* -2 is disabled */

    private float _primaryVolume = 1.0f;
    private float _monitorVolume = 1.0f;

    private CancellationTokenSource? _playbackCts;
    private readonly Lock _processLock = new();
    private bool _isPaused;

    /* Returns a list with all the available Windows audio output devices visible by the application */
    public unsafe Result<List<AudioDeviceInfo>> GetAvailableDevices()
    {
        try
        {
            var devices = new List<AudioDeviceInfo>
            {
                new AudioDeviceInfo
                {
                    DeviceId = "-1",
                    DeviceName = "Default System Device",
                }
            };

            uint count = LibWinMm.waveOutGetNumDevs();

            /* Adds them one by one to the list */
            for (uint i = 0; i < count; i++)
            {
                int mmResult = LibWinMm.waveOutGetDevCapsW(
                    (nuint)i,
                    out LibWinMm.WAVEOUTCAPSW caps,
                    (uint)sizeof(LibWinMm.WAVEOUTCAPSW));

                string name = mmResult == LibWinMm.MMSYSERR_NOERROR
                    ? ExtractDeviceName(ref caps, i)
                    : $"Audio Output Device {i}";

                devices.Add(new AudioDeviceInfo
                {
                    DeviceId = i.ToString(),
                    DeviceName = name,
                });
            }

            return Result<List<AudioDeviceInfo>>.Success(devices);
        }
        catch (Exception ex)
        {
            return Result<List<AudioDeviceInfo>>.Failure(
                $"Failed to retrieve Windows audio devices: {ex.Message}", ex);
        }
    }

    /* Sets the primary and monitoring (If enabled) audio device output */
    public Result SetOutputDevices(
        string? primaryDeviceId = IAudioPlayer.KeepCurrent,
        string? monitorDeviceId = IAudioPlayer.KeepCurrent)
    {
        lock (_processLock)
        {
            if (primaryDeviceId != IAudioPlayer.KeepCurrent)
            {
                /* Sets the main output if it is not null */
                if (!string.IsNullOrWhiteSpace(primaryDeviceId))
                {
                    if (!TryResolveDeviceNumber(primaryDeviceId, out int primaryId))
                        return Result.Failure($"Invalid primary audio device ID '{primaryDeviceId}'");
                    /* Gets the ID from the primaryDeviceId */
                    _primaryDeviceNumber = primaryId;
                }
                else
                {
                    _primaryDeviceNumber = DisabledDevice; /* Disabled */
                }
            }

            if (monitorDeviceId != IAudioPlayer.KeepCurrent)
            {
                /* Sets the monitoring output if it is not null */
                if (!string.IsNullOrWhiteSpace(monitorDeviceId))
                {
                    if (!TryResolveDeviceNumber(monitorDeviceId, out int monitorId))
                        return Result.Failure($"Invalid monitor audio device ID '{monitorDeviceId}'");
                    _monitorDeviceNumber = monitorId;
                }
                else
                {
                    _monitorDeviceNumber = DisabledDevice;
                }
            }
        }

        return Result.Success();
    }

    /* Plays a single memory buffer of raw PCM audio data via Windows audio output */
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

    /* Streams incoming PCM chunks sequentially and gaplessly on Windows to primary and monitor outputs */
    public async Task<Result> StreamPcmAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> pcmChunks,
        AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        int primaryDev;
        int monitorDev;

        lock (_processLock)
        {
            primaryDev = _primaryDeviceNumber;
            monitorDev = _monitorDeviceNumber;
        }

        bool playPrimary = primaryDev >= DefaultDevice;
        bool playMonitor = monitorDev >= DefaultDevice;

        if (!playPrimary && !playMonitor)
            return Result.Success();

        Stop();

        CancellationToken linkedToken;
        lock (_processLock)
        {
            _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedToken = _playbackCts.Token;
            _isPaused = false;
        }

        var sessionTargets = new List<WaveOutTarget>();

        if (playPrimary)
        {
            var primaryTarget = OpenTarget(primaryDev, format, () => _primaryVolume);
            if (primaryTarget.IsSuccess && primaryTarget.Value != null)
            {
                lock (_processLock)
                {
                    _activeTargets.Add(primaryTarget.Value);
                }
                sessionTargets.Add(primaryTarget.Value);
            }
        }

        if (playMonitor)
        {
            var monitorTarget = OpenTarget(monitorDev, format, () => _monitorVolume);
            if (monitorTarget.IsSuccess && monitorTarget.Value != null)
            {
                lock (_processLock)
                {
                    _activeTargets.Add(monitorTarget.Value);
                }
                sessionTargets.Add(monitorTarget.Value);
            }
        }

        if (sessionTargets.Count == 0)
            return Result.Failure("Failed to initialize any Windows audio output stream targets.");

        int bytesPerSample = Math.Max(format.BitDepth / 8, 1);
        int blockAlign = Math.Max(format.Channels * bytesPerSample, 1);
        int bytesPerSecond = Math.Max(format.SampleRate * blockAlign, blockAlign);
        /* ~50ms slice aligned to block boundary */
        int sliceSize = Math.Max((bytesPerSecond / 20) / blockAlign * blockAlign, blockAlign);
        byte[] scratchBuffer = new byte[sliceSize];

        try
        {
            await foreach (var chunk in pcmChunks.WithCancellation(linkedToken))
            {
                if (chunk.Length == 0)
                    continue;

                int remaining = chunk.Length;
                int offset = 0;

                while (remaining > 0 && !linkedToken.IsCancellationRequested)
                {
                    int bytesToWrite = Math.Min(remaining, sliceSize);
                    /* Keep multi-byte samples aligned */
                    if (bytesToWrite > blockAlign)
                        bytesToWrite -= bytesToWrite % blockAlign;

                    foreach (var target in sessionTargets)
                    {
                        await WaitForTargetCapacityAsync(target, MaxInFlightBuffers, linkedToken);
                        if (linkedToken.IsCancellationRequested)
                            break;

                        var slice = chunk.Span.Slice(offset, bytesToWrite);
                        var enqueueRes = EnqueueBuffer(target, slice, scratchBuffer);
                        if (enqueueRes.IsFailure)
                            return enqueueRes;
                    }

                    offset += bytesToWrite;
                    remaining -= bytesToWrite;
                }
            }

            /* Wait for all queued hardware buffers to finish playing */
            foreach (var target in sessionTargets)
            {
                await DrainTargetAsync(target, linkedToken);
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Playback cancelled");
        }
        catch (Exception ex)
        {
            return Result.Failure($"Streaming Windows audio failed: {ex.Message}", ex);
        }
        finally
        {
            Stop();
        }
    }

    /* The self-explanatory ones */

    /* Sets volume levels for primary and monitor outputs (applied live per 50ms PCM slice) */
    public void SetVolume(int? primaryVolume = null, int? monitorVolume = null)
    {
        lock (_processLock)
        {
            if (primaryVolume.HasValue)
            {
                _primaryVolume = Math.Clamp(primaryVolume.Value, 0, 100) / 100.0f;
            }

            if (!monitorVolume.HasValue) return;
            _monitorVolume = Math.Clamp(monitorVolume.Value, 0, 100) / 100.0f;
        }
    }

    /* Pauses active Windows waveOut playback handles */
    public void Pause()
    {
        lock (_processLock)
        {
            if (_isPaused) return;
            foreach (var target in _activeTargets)
            {
                if (!target.IsDisposed && target.Handle != 0)
                {
                    LibWinMm.waveOutPause(target.Handle);
                }
            }
            _isPaused = true;
        }
    }

    /* Resumes paused Windows waveOut playback handles */
    public void Resume()
    {
        lock (_processLock)
        {
            if (!_isPaused) return;
            foreach (var target in _activeTargets)
            {
                if (!target.IsDisposed && target.Handle != 0)
                {
                    LibWinMm.waveOutRestart(target.Handle);
                    target.BufferDoneEvent.Set();
                }
            }
            _isPaused = false;
        }
    }

    /* Immediately stops playback, resets hardware queues, and releases unmanaged audio buffers */
    public void Stop()
    {
        lock (_processLock)
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = null;

            /* Process gets disposed after finished */
            foreach (var target in _activeTargets)
            {
                DisposeTarget(target);
            }

            _activeTargets.Clear();
            _isPaused = false;
        }
    }

    /* Resolves a device ID or device display name to a WinMM waveOut device index (-1 for default) */
    private bool TryResolveDeviceNumber(string deviceId, out int deviceNumber)
    {
        if (int.TryParse(deviceId, out int parsed) && parsed >= DefaultDevice)
        {
            deviceNumber = parsed;
            return true;
        }

        var devicesResult = GetAvailableDevices();
        if (devicesResult.IsSuccess && devicesResult.Value != null)
        {
            var matched = devicesResult.Value.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.DeviceName, deviceId, StringComparison.OrdinalIgnoreCase));

            if (matched != null && int.TryParse(matched.DeviceId, out int matchedId))
            {
                deviceNumber = matchedId;
                return true;
            }
        }

        deviceNumber = DisabledDevice;
        return false;
    }

    /* Opens a WinMM waveOut handle for the specified device and PCM format */
    private static Result<WaveOutTarget> OpenTarget(
        int deviceNumber,
        AudioFormat format,
        Func<float> volumeProvider)
    {
        int channels = Math.Max((int)format.Channels, 1);
        int bitDepth = Math.Max((int)format.BitDepth, 8);
        int sampleRate = Math.Max(format.SampleRate, 8000);
        int bytesPerSample = Math.Max(bitDepth / 8, 1);
        ushort blockAlign = (ushort)Math.Max(channels * bytesPerSample, 1);

        var wfx = new LibWinMm.WAVEFORMATEX
        {
            wFormatTag = LibWinMm.WAVE_FORMAT_PCM,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            nAvgBytesPerSec = (uint)(sampleRate * blockAlign),
            nBlockAlign = blockAlign,
            wBitsPerSample = (ushort)bitDepth,
            cbSize = 0
        };

        uint totalDevices = LibWinMm.waveOutGetNumDevs();
        uint targetDeviceId = (deviceNumber < 0 || deviceNumber >= totalDevices)
            ? LibWinMm.WAVE_MAPPER
            : (uint)deviceNumber;

        var doneEvent = new AutoResetEvent(false);
        nint eventHandle = doneEvent.SafeWaitHandle.DangerousGetHandle();

        int mmResult = LibWinMm.waveOutOpen(
            out nint hWaveOut,
            targetDeviceId,
            ref wfx,
            eventHandle,
            0,
            LibWinMm.CALLBACK_EVENT);

        /* Fall back to WAVE_MAPPER if a previously saved device index is unavailable */
        if (mmResult != LibWinMm.MMSYSERR_NOERROR && targetDeviceId != LibWinMm.WAVE_MAPPER)
        {
            mmResult = LibWinMm.waveOutOpen(
                out hWaveOut,
                LibWinMm.WAVE_MAPPER,
                ref wfx,
                eventHandle,
                0,
                LibWinMm.CALLBACK_EVENT);
        }

        if (mmResult != LibWinMm.MMSYSERR_NOERROR || hWaveOut == 0)
        {
            doneEvent.Dispose();
            return Result<WaveOutTarget>.Failure($"waveOutOpen failed with MMRESULT {mmResult}.");
        }

        return Result<WaveOutTarget>.Success(new WaveOutTarget(hWaveOut, doneEvent, volumeProvider));
    }

    /* Scales PCM samples by the target's live volume and queues an unmanaged WAVEHDR buffer to WinMM */
    private unsafe Result EnqueueBuffer(
        WaveOutTarget target,
        ReadOnlySpan<byte> pcmSlice,
        byte[] scratchBuffer)
    {
        lock (_processLock)
        {
            if (target.IsDisposed || target.Handle == 0)
                return Result.Failure("Audio output target was closed.");

            ReapCompletedBuffers(target);

            int length = pcmSlice.Length;
            Span<byte> scaledSpan = scratchBuffer.AsSpan(0, length);
            pcmSlice.CopyTo(scaledSpan);

            float liveVolume = target.VolumeProvider();
            SimdAudioProcessor.ScaleVolumeInPlace(scaledSpan, liveVolume);

            nint dataPtr = (nint)NativeMemory.Alloc((nuint)length);
            nint hdrPtr = (nint)NativeMemory.AllocZeroed((nuint)sizeof(LibWinMm.WAVEHDR));

            fixed (byte* srcPtr = scaledSpan)
            {
                Buffer.MemoryCopy(srcPtr, (void*)dataPtr, length, length);
            }

            var hdr = (LibWinMm.WAVEHDR*)hdrPtr;
            hdr->lpData = dataPtr;
            hdr->dwBufferLength = (uint)length;
            hdr->dwFlags = 0;

            int prepRes = LibWinMm.waveOutPrepareHeader(target.Handle, hdrPtr, (uint)sizeof(LibWinMm.WAVEHDR));
            if (prepRes != LibWinMm.MMSYSERR_NOERROR)
            {
                NativeMemory.Free((void*)hdrPtr);
                NativeMemory.Free((void*)dataPtr);
                return Result.Failure($"waveOutPrepareHeader failed with MMRESULT {prepRes}.");
            }

            int writeRes = LibWinMm.waveOutWrite(target.Handle, hdrPtr, (uint)sizeof(LibWinMm.WAVEHDR));
            if (writeRes != LibWinMm.MMSYSERR_NOERROR)
            {
                LibWinMm.waveOutUnprepareHeader(target.Handle, hdrPtr, (uint)sizeof(LibWinMm.WAVEHDR));
                NativeMemory.Free((void*)hdrPtr);
                NativeMemory.Free((void*)dataPtr);
                return Result.Failure($"waveOutWrite failed with MMRESULT {writeRes}.");
            }

            target.InFlight.Enqueue(new PreparedWaveBuffer(hdrPtr, dataPtr));
            return Result.Success();
        }
    }

    /* Unprepares and frees all completed WAVEHDR buffers at the front of the target queue; returns remaining in-flight count */
    private static unsafe int ReapCompletedBuffers(WaveOutTarget target)
    {
        if (target.IsDisposed)
            return 0;

        while (target.InFlight.TryPeek(out PreparedWaveBuffer front))
        {
            var hdr = (LibWinMm.WAVEHDR*)front.HeaderPtr;
            if ((hdr->dwFlags & LibWinMm.WHDR_DONE) == 0)
                break;

            target.InFlight.Dequeue();
            LibWinMm.waveOutUnprepareHeader(target.Handle, front.HeaderPtr, (uint)sizeof(LibWinMm.WAVEHDR));
            NativeMemory.Free((void*)front.HeaderPtr);
            NativeMemory.Free((void*)front.DataPtr);
        }

        return target.InFlight.Count;
    }

    /* Waits until the target has fewer than maxInFlight queued buffers */
    private async Task WaitForTargetCapacityAsync(
        WaveOutTarget target,
        int maxInFlight,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int remaining;
            lock (_processLock)
            {
                if (target.IsDisposed)
                    return;
                remaining = ReapCompletedBuffers(target);
            }

            if (remaining < maxInFlight)
                return;

            await Task.Run(() => target.BufferDoneEvent.WaitOne(15), token);
        }
    }

    /* Waits until all queued buffers on the target have completed hardware playback */
    private async Task DrainTargetAsync(WaveOutTarget target, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int remaining;
            lock (_processLock)
            {
                if (target.IsDisposed)
                    return;
                remaining = ReapCompletedBuffers(target);
            }

            if (remaining == 0)
                return;

            await Task.Run(() => target.BufferDoneEvent.WaitOne(15), token);
        }
    }

    /* Resets the WinMM device, frees all remaining WAVEHDR buffers, and closes the handle (must be called under _processLock) */
    private static unsafe void DisposeTarget(WaveOutTarget target)
    {
        if (target.IsDisposed)
            return;

        target.IsDisposed = true;

        if (target.Handle != 0)
        {
            LibWinMm.waveOutReset(target.Handle);

            while (target.InFlight.TryDequeue(out PreparedWaveBuffer buf))
            {
                LibWinMm.waveOutUnprepareHeader(target.Handle, buf.HeaderPtr, (uint)sizeof(LibWinMm.WAVEHDR));
                NativeMemory.Free((void*)buf.HeaderPtr);
                NativeMemory.Free((void*)buf.DataPtr);
            }

            LibWinMm.waveOutClose(target.Handle);
            target.Handle = 0;
        }

        try
        {
            target.BufferDoneEvent.Set();
            target.BufferDoneEvent.Dispose();
        }
        catch
        {
            /* Ignore disposal race on wait handle */
        }
    }

    /* Reads the null-terminated UTF-16 product name from WAVEOUTCAPSW */
    private static unsafe string ExtractDeviceName(ref LibWinMm.WAVEOUTCAPSW caps, uint index)
    {
        fixed (char* namePtr = caps.szPname)
        {
            var span = new ReadOnlySpan<char>(namePtr, LibWinMm.MAXPNAMELEN);
            int nullIdx = span.IndexOf('\0');
            if (nullIdx >= 0)
                span = span[..nullIdx];

            string trimmed = span.ToString().Trim();
            return string.IsNullOrEmpty(trimmed) ? $"Audio Output Device {index}" : trimmed;
        }
    }

    /* Tracks an unmanaged WAVEHDR and its PCM payload buffer */
    private readonly record struct PreparedWaveBuffer(nint HeaderPtr, nint DataPtr);

    /* Represents an active WinMM waveOut device playback stream */
    private sealed class WaveOutTarget(nint handle, AutoResetEvent bufferDoneEvent, Func<float> volumeProvider)
    {
        public nint Handle { get; set; } = handle;
        public AutoResetEvent BufferDoneEvent { get; } = bufferDoneEvent;
        public Func<float> VolumeProvider { get; } = volumeProvider;
        public Queue<PreparedWaveBuffer> InFlight { get; } = new();
        public bool IsDisposed { get; set; }
    }

    /* Native Win32 Multimedia (winmm.dll) declarations for AOT-safe PCM playback */
    private static unsafe class LibWinMm
    {
        public const int MMSYSERR_NOERROR = 0;
        public const uint WAVE_MAPPER = unchecked((uint)-1);
        public const ushort WAVE_FORMAT_PCM = 1;
        public const uint CALLBACK_EVENT = 0x00050000;
        public const uint WHDR_DONE = 0x00000001;
        public const int MAXPNAMELEN = 32;

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
        public struct WAVEHDR
        {
            public nint lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public nint dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public nint lpNext;
            public nint reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEOUTCAPSW
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            public fixed char szPname[MAXPNAMELEN];
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public uint dwSupport;
        }

        [DllImport("winmm.dll")]
        public static extern uint waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        public static extern int waveOutGetDevCapsW(nuint uDeviceID, out WAVEOUTCAPSW pwoc, uint cbwoc);

        [DllImport("winmm.dll")]
        public static extern int waveOutOpen(
            out nint phwo,
            uint uDeviceID,
            ref WAVEFORMATEX pwfx,
            nint dwCallback,
            nint dwInstance,
            uint fdwOpen);

        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(nint hwo, nint pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(nint hwo, nint pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(nint hwo, nint pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutPause(nint hwo);

        [DllImport("winmm.dll")]
        public static extern int waveOutRestart(nint hwo);

        [DllImport("winmm.dll")]
        public static extern int waveOutReset(nint hwo);

        [DllImport("winmm.dll")]
        public static extern int waveOutClose(nint hwo);
    }
}
