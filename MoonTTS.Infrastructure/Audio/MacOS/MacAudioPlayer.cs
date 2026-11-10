using System.Runtime.InteropServices;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Interfaces;

namespace MoonTTS.Infrastructure.Audio.MacOS;

/* File Structure:
        - MacDevice(string Uid, string Name)
        - GetAvailableDevices() : Result<List<AudioDeviceInfo>>
        - SetOutputDevices(string? primaryDeviceId, string? secondaryMonitorDeviceId) : Result
        - SetVolume(int? primaryVolume, int? monitorVolume) : void
        - PlayPcmAsync(ReadOnlyMemory<byte> pcmBytes, AudioFormat format, CancellationToken cancellationToken) : Task<Result>
        - StreamPcmAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcmChunks, AudioFormat format, CancellationToken cancellationToken) : Task<Result>
        - PlayQueueStreamAsync(byte[] pcmBytes, string? deviceUid, float volume, bool isPrimary, CancellationToken token) : Task<Result>
        - Pause() : void
        - Resume() : void
        - Stop() : void
        - EnumerateOutputDevices() : List<MacDevice>
        - ResolveDeviceUid(string? deviceId) : string? 
*/

public class MacAudioPlayer : IAudioPlayer
{
    private string? _primaryDeviceId = "-1";
    private string? _monitorDeviceId = null;
    
    
    private float _primaryVolume = 1.0f;
    private float _monitorVolume = 1.0f;

    private IntPtr _primaryQueue = IntPtr.Zero;
    private IntPtr _monitorQueue = IntPtr.Zero;

    private CancellationTokenSource? _playbackCts;
    private readonly Lock _processLock = new();

    public record MacDevice(string Uid, string Name);
    
    /* Device discovery */

    public Result<List<AudioDeviceInfo>> GetAvailableDevices()
    {
        var devices = new List<AudioDeviceInfo>()
        {
            new AudioDeviceInfo { DeviceId = "-1", DeviceName = "Default System Device" }
        };

        try
        {
            var macDevices = EnumerateOutputDevices();
            int index = 1;
            foreach (var dev in macDevices)
            {
                devices.Add(new AudioDeviceInfo
                {
                    DeviceId = (index++).ToString(),
                    DeviceName = dev.Name
                });
            }

            return Result<List<AudioDeviceInfo>>.Success(devices);
        }
        catch (Exception ex)
        {
            return Result<List<AudioDeviceInfo>>.Failure($"Failed to retrieve macOS CoreAudio devices: {ex.Message}", ex);
        }
    }
    
    /* Configuration */

    public Result SetOutputDevices(
        string? primaryDeviceId = IAudioPlayer.KeepCurrent, 
        string? secondaryMonitorDeviceId = IAudioPlayer.KeepCurrent)
    {
        lock (_processLock)
        {
            if (primaryDeviceId != IAudioPlayer.KeepCurrent)
            {
                _primaryDeviceId = !string.IsNullOrWhiteSpace(primaryDeviceId) ? primaryDeviceId : null;
            }

            if (secondaryMonitorDeviceId != IAudioPlayer.KeepCurrent)
            {
                _monitorDeviceId = !string.IsNullOrWhiteSpace(secondaryMonitorDeviceId)
                    ? secondaryMonitorDeviceId
                    : null;
            }
        }

        return Result.Success();
    }

    public void SetVolume(int? primaryVolume = null, int? monitorVolume = null)
    {
        lock (_processLock)
        {
            if (primaryVolume.HasValue)
            {
                _primaryVolume = Math.Clamp(primaryVolume.Value, 0, 100) / 100.0f;
                /* Live mid-playback volume update on active CoreAudio queue */
                if (_primaryQueue != IntPtr.Zero)
                    MacCoreAudioApi.AudioQueueSetParameter(_primaryQueue, MacCoreAudioApi.kAudioQueueParam_Volume, _primaryVolume);
            }
            
            if (monitorVolume.HasValue)
            {
                _monitorVolume = Math.Clamp(monitorVolume.Value, 0, 100) / 100.0f;
                /* Live mid-playback volume update on active CoreAudio queue */
                if (_monitorQueue != IntPtr.Zero)
                    MacCoreAudioApi.AudioQueueSetParameter(
                        _monitorQueue, 
                        MacCoreAudioApi.kAudioQueueParam_Volume,
                        _monitorVolume);
            }
            

        }
    }
    
    /* Playback */
    public async Task<Result> PlayPcmAsync(ReadOnlyMemory<byte> pcmBytes, AudioFormat format, CancellationToken cancellationToken = default)
    {
        string? primaryId;
        string? monitorId;
        float pVol, mVol;

        lock (_processLock)
        {
            primaryId = _primaryDeviceId;
            monitorId = _monitorDeviceId;
            pVol = _primaryVolume;
            mVol = _monitorVolume;
        }

        bool playPrimary = !string.IsNullOrEmpty(primaryId);
        bool playMonitor = !string.IsNullOrEmpty(monitorId);

        if (!playPrimary && !playMonitor)
            return Result.Success();

        if (pcmBytes.Length == 0)
            return Result.Failure("PCM buffer is empty.");

        Stop();

        CancellationToken linkedToken;
        lock (_processLock)
        {
            _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedToken = _playbackCts.Token;
        }

        byte[] rawArray = pcmBytes.ToArray();

        try
        {
            var tasks = new List<Task<Result>>();

            if (playPrimary)
            {
                string? targetUid = ResolveDeviceUid(primaryId);
                tasks.Add(PlayQueueStreamAsync(rawArray, targetUid, pVol, isPrimary: true, linkedToken));
            }

            if (playMonitor)
            {
                string? targetUid = ResolveDeviceUid(monitorId);
                tasks.Add(PlayQueueStreamAsync(rawArray, targetUid, mVol, isPrimary: false, linkedToken));
            }

            var results = await Task.WhenAll(tasks);
            Result? failure = results.FirstOrDefault(r => r.IsFailure);
            return failure ?? Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Playback cancelled.");
        }
        finally
        {
            Stop();
        }
    }

    /* Sequentially plays incoming PCM chunks on macOS */
    public async Task<Result> StreamPcmAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcmChunks, AudioFormat format, CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in pcmChunks.WithCancellation(cancellationToken))
        {
            var result = await PlayPcmAsync(chunk, format, cancellationToken);
            if (result.IsFailure) return result;
        }
        return Result.Success();
    }


    private Task<Result> PlayQueueStreamAsync(
        byte[] pcmBytes,
        string? deviceUid,
        float volume,
        bool isPrimary,
        CancellationToken token)
    {
        var tcs = new TaskCompletionSource<Result>();

        /* Format: 24000Hz, 16-bit mono PCM */
        var asbd = new MacCoreAudioApi.AudioStreamBasicDescription
        {
            mSampleRate = 24000.0,
            mFormatID = MacCoreAudioApi.kAudioFormatLinearPCM,
            mFormatFlags = MacCoreAudioApi.kAudioFormatFlagIsSignedInteger | MacCoreAudioApi.kAudioFormatFlagIsPacked,
            mBytesPerPacket = 2,
            mFramesPerPacket = 1,
            mBytesPerFrame = 2,
            mChannelsPerFrame = 1,
            mBitsPerChannel = 16,
            mReserved = 0
        };

        MacCoreAudioApi.AudioQueueOutputCallback callback = (userData, queue, buffer) =>
        {
            /* Fired when the audio buffer completes playback */
            tcs.TrySetResult(Result.Success());
        };

        if (MacCoreAudioApi.AudioQueueNewOutput(ref asbd, callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0,
                out IntPtr queue) != 0)
        {
            return Task.FromResult(Result.Failure("Failed to create macOS AudioQueue"));
        }

        lock (_processLock)
        {
            if (isPrimary) _primaryQueue = queue;
            else _monitorQueue = queue;
        }

        /* Routes to target CoreAudio device UID */
        if (!string.IsNullOrEmpty(deviceUid))
        {
            IntPtr cfUid = MacCoreAudioApi.StringToCFString(deviceUid);
            try
            {
                MacCoreAudioApi.AudioQueueSetProperty(
                    queue,
                    MacCoreAudioApi.kAudioQueueProperty_CurrentDevice,
                    ref cfUid,
                    (uint)IntPtr.Size);
            }
            finally
            {
                MacCoreAudioApi.CFRelease(cfUid);
            }
        }
        
        /* Sets initial volume */
        MacCoreAudioApi.AudioQueueSetParameter(queue, MacCoreAudioApi.kAudioQueueParam_Volume, volume);
        
        /* Allocates and populates the buffer */
        if (MacCoreAudioApi.AudioQueueAllocateBuffer(queue, (uint)pcmBytes.Length, out IntPtr bufferPtr) != 0)
        {
            return Task.FromResult(Result.Failure("Failed to allocate AudioQueue buffer."));
        }
        
        /* Writes PCM data to buffer pointer */
        IntPtr dataptr = Marshal.ReadIntPtr(bufferPtr, 4); /* Offsets to mAudioData in AudioQueueBuffer */
        Marshal.Copy(pcmBytes, 0, dataptr, pcmBytes.Length);
        Marshal.WriteInt32(bufferPtr, 12, pcmBytes.Length); /* mAudioDataSize */

        MacCoreAudioApi.AudioQueueEnqueueBuffer(queue, bufferPtr, 0, IntPtr.Zero);
        MacCoreAudioApi.AudioQueueStart(queue, IntPtr.Zero);
        
        /* Cancellation token registration */
        token.Register(() =>
        {
            tcs.TrySetCanceled(token);
        });

        return tcs.Task;
    }

    public void Pause()
    {
        lock (_processLock)
        {
            if (_primaryQueue != IntPtr.Zero) MacCoreAudioApi.AudioQueuePause(_primaryQueue);
            if (_monitorQueue != IntPtr.Zero) MacCoreAudioApi.AudioQueuePause(_monitorQueue);
        }
    }

    public void Resume()
    {
        lock (_processLock)
        {
            if (_primaryQueue != IntPtr.Zero) MacCoreAudioApi.AudioQueueStart(_primaryQueue, IntPtr.Zero);
            if (_monitorQueue != IntPtr.Zero) MacCoreAudioApi.AudioQueueStart(_monitorQueue, IntPtr.Zero);
        }
    }
    
    public void Stop()
    {
        lock (_processLock)
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = null;

            if (_primaryQueue != IntPtr.Zero)
            {
                MacCoreAudioApi.AudioQueueStop(_primaryQueue, true);
                MacCoreAudioApi.AudioQueueDispose(_primaryQueue, true);
                _primaryQueue = IntPtr.Zero;
            }

            if (_monitorQueue != IntPtr.Zero)
            {
                MacCoreAudioApi.AudioQueueStop(_monitorQueue, true);
                MacCoreAudioApi.AudioQueueDispose(_monitorQueue, true);
                _monitorQueue = IntPtr.Zero;
            }
        }
    }

    private static List<MacDevice> EnumerateOutputDevices()
    {
        var result = new List<MacDevice>();

        var address = new MacCoreAudioApi.AudioObjectPropertyAddress
        {
            mSelector = MacCoreAudioApi.kAudioHardwarePropertyDevices,
            mScope = MacCoreAudioApi.kAudioObjectPropertyScopeGlobal,
            mElement = MacCoreAudioApi.kAudioObjectPropertyElementMain
        };

        if (MacCoreAudioApi.AudioObjectGetPropertyDataSize(
                MacCoreAudioApi.kAudioObjectSystemObject, ref address, 0, IntPtr.Zero, out uint dataSize) != 0)
        {
            return result;
        }
        
        int count = (int)(dataSize / sizeof(uint));
        uint[] deviceIds = new uint[count];
        GCHandle handle = GCHandle.Alloc(deviceIds, GCHandleType.Pinned);

        try
        {
            if (MacCoreAudioApi.AudioObjectGetPropertyData(
                    MacCoreAudioApi.kAudioObjectSystemObject, 
                    ref address, 
                    0, 
                    IntPtr.Zero, 
                    ref dataSize,
                    handle.AddrOfPinnedObject()) != 0)
            {
                return result;
            }
        }
        finally
        {
            handle.Free();
        }

        foreach (uint devId in deviceIds)
        {
            var streamAddr = new MacCoreAudioApi.AudioObjectPropertyAddress
            {
                mSelector = MacCoreAudioApi.kAudioDevicePropertyStreamConfiguration,
                mScope = MacCoreAudioApi.kAudioObjectPropertyScopeOutput,
                mElement = MacCoreAudioApi.kAudioObjectPropertyElementMain
            };

            if (MacCoreAudioApi.AudioObjectGetPropertyDataSize(
                    devId, 
                    ref streamAddr, 
                    0, 
                    IntPtr.Zero, 
                    out uint streamSize) == 0 && streamSize > 0)
            {
                IntPtr bufferListPtr = Marshal.AllocHGlobal((int)streamSize);
                try
                {
                    if (MacCoreAudioApi.AudioObjectGetPropertyData(devId, ref streamAddr, 0, IntPtr.Zero,
                            ref streamSize, bufferListPtr) == 0)
                    {
                        var bufferList = Marshal.PtrToStructure<MacCoreAudioApi.AudioBufferList>(bufferListPtr);
                        if (bufferList.mNumberBuffers == 0 || bufferList.mBuffers.mNumberChannels == 0)
                            continue;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(bufferListPtr);
                }
            }
            else continue;
            
            string name = $"Audio Device {devId}";
            var nameAddr = new MacCoreAudioApi.AudioObjectPropertyAddress
            {
                mSelector = MacCoreAudioApi.kAudioDevicePropertyDeviceNameCFString,
                mScope = MacCoreAudioApi.kAudioObjectPropertyScopeGlobal,
                mElement = MacCoreAudioApi.kAudioObjectPropertyElementMain
            };

            uint ptrSize = (uint)IntPtr.Size;
            if (MacCoreAudioApi.AudioObjectGetPropertyData(
                    devId, 
                    ref nameAddr, 
                    0, 
                    IntPtr.Zero, 
                    ref ptrSize,
                    out IntPtr cfName) == 0)
            {
                name = MacCoreAudioApi.CFStringToString(cfName) ?? name;
            }
            
            string uid = devId.ToString();
            var uidAddr = new MacCoreAudioApi.AudioObjectPropertyAddress
            {
                mSelector = MacCoreAudioApi.kAudioDevicePropertyDeviceUID,
                mScope = MacCoreAudioApi.kAudioObjectPropertyScopeGlobal,
                mElement = MacCoreAudioApi.kAudioObjectPropertyElementMain
            };

            ptrSize = (uint)IntPtr.Size;
            if (MacCoreAudioApi.AudioObjectGetPropertyData(
                    devId, 
                    ref uidAddr, 
                    0, 
                    IntPtr.Zero, 
                    ref ptrSize, 
                    out IntPtr cfUid) == 0)
            {
                uid = MacCoreAudioApi.CFStringToString(cfUid) ?? uid;
            }
            result.Add(new MacDevice(uid, name));
        }

        return result;
    }

    private static string? ResolveDeviceUid(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == "-1")
            return null; /* System default device */

        var devices = EnumerateOutputDevices();

        if (int.TryParse(deviceId, out int index) && index >= 1 && index <= devices.Count)
        {
            return devices[index - 1].Uid;
        }

        var found = devices.FirstOrDefault(d =>
            string.Equals(d.Uid, deviceId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Name, deviceId, StringComparison.OrdinalIgnoreCase));

        return found?.Uid ?? deviceId;
    }
}