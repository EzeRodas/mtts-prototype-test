using System;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Infrastructure.Audio.MacOS;

namespace MoonTTS.Infrastructure.Audio;

/* File Structure:
        - Create() : IAudioPlayer
*/

public class AudioPlayerManager
{
    /* Manager that returns correct object with correct audio handling depending on platform */
    public static IAudioPlayer Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsAudioPlayer();
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return new MacAudioPlayer();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxAudioPlayer();
        }

        throw new PlatformNotSupportedException($"{Environment.OSVersion} is not supported to play audio yet");
    }
}