using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Entities;

namespace MoonTTS.Infrastructure.Audio.Linux;

/* File Structure:
        - GetAvailableDevices() : Result<List<AudioDeviceInfo>>
        - OpenSinkSession(string? targetSinkId, AudioFormat format) : Result<IAudioSinkSession>
*/

/* Port abstraction providing Linux audio device discovery and sink session creation */
public interface ILinuxAudioBackend
{
    /* Enumerates available Linux output sinks and ensures the virtual microphone sink is registered */
    Result<List<AudioDeviceInfo>> GetAvailableDevices();

    /* Opens a streaming audio sink session for the target sink ID (null or "-1" for system default) */
    Result<IAudioSinkSession> OpenSinkSession(string? targetSinkId, AudioFormat format);
}
