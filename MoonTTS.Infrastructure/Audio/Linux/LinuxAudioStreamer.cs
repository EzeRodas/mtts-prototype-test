using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Entities;

namespace MoonTTS.Infrastructure.Audio.Linux;

/* File Structure:
        - LinuxAudioStreamer(ILinuxCommandRunner? commandRunner)
        - GetAvailableDevices() : Result<List<AudioDeviceInfo>>
        - OpenSinkSession(string? targetSinkId, AudioFormat format) : Result<IAudioSinkSession>
        - StartSinkSession(string? targetSink, AudioFormat format, ILinuxCommandRunner? commandRunner) : Result<IAudioSinkSession>
        - ResolvePaplayFormat(short bitDepth) : string
        - ResolvePwPlayFormat(short bitDepth) : string
*/

/* Streams audio directly to a Linux sink with automatic fallback.
   First tries with paplay (PulseAudio / pipewire-pulse), and if that doesn't work
   attempts with pw-play (Native PipeWire) */

public class LinuxAudioStreamer : ILinuxAudioBackend
{
    private readonly ILinuxCommandRunner _commandRunner;

    /* Initializes the Linux audio backend with the specified or default command runner */
    public LinuxAudioStreamer(ILinuxCommandRunner? commandRunner = null)
    {
        _commandRunner = commandRunner ?? LinuxProcessRunner.Instance;
    }

    /* Enumerates available Linux audio output sinks and registers the virtual microphone sink */
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

            var micResult = LinuxVirtualMicService.EnsureCreated(_commandRunner);
            if (micResult.IsSuccess)
            {
                devices.Add(new AudioDeviceInfo
                {
                    DeviceId = LinuxVirtualMicService.SinkName,
                    DeviceName = "Moon-TTS Virtual Microphone"
                });
            }

            var nativeSinks = LinuxDeviceEnumerator.GetSinks(_commandRunner);
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

    /* Opens an active streaming audio sink session for the target sink ID (null or "-1" for system default) */
    public Result<IAudioSinkSession> OpenSinkSession(string? targetSinkId, AudioFormat format)
    {
        string? normalizedSink = string.IsNullOrWhiteSpace(targetSinkId) || targetSinkId == "-1"
            ? null
            : targetSinkId;

        return StartSinkSession(normalizedSink, format, _commandRunner);
    }

    /* Spawns an audio sink playback session (paplay with pw-play fallback) configured for raw PCM stdin streaming */
    public static Result<IAudioSinkSession> StartSinkSession(
        string? targetSink,
        AudioFormat format,
        ILinuxCommandRunner? commandRunner = null)
    {
        var runner = commandRunner ?? LinuxProcessRunner.Instance;
        int sampleRate = Math.Max(format.SampleRate, 8000);
        int channels = Math.Max((int)format.Channels, 1);
        string paplayFormat = ResolvePaplayFormat(format.BitDepth);
        string pwFormat = ResolvePwPlayFormat(format.BitDepth);

        /* Tries launching paplay with raw pcm parameters */
        string paplayArgs = string.IsNullOrEmpty(targetSink)
            ? $"--raw --rate={sampleRate} --channels={channels} --latency-msec=50 --format={paplayFormat} /dev/stdin"
            : $"--raw --rate={sampleRate} --channels={channels} --latency-msec=50 --format={paplayFormat} -d \"{targetSink}\" /dev/stdin";

        var paplayResult = runner.StartStreamingSession("paplay", paplayArgs);
        if (paplayResult.IsSuccess && paplayResult.Value != null)
        {
            return paplayResult;
        }
        
        /* Fallback to pw-play if paplay doesn't work */
        string pwArgs = string.IsNullOrEmpty(targetSink)
            ? $"--raw --rate {sampleRate} --channels {channels} --format {pwFormat} -"
            : $"--raw --rate {sampleRate} --channels {channels} --format {pwFormat} --target \"{targetSink}\" -";

        var pwResult = runner.StartStreamingSession("pw-play", pwArgs);
        if (pwResult.IsSuccess && pwResult.Value != null)
        {
            return pwResult;
        }

        return Result<IAudioSinkSession>.Failure(
            $"Failed to start audio playback (paplay: {paplayResult.ErrorMessage} | pw-play: {pwResult.ErrorMessage})");
    }

    /* Maps AudioFormat.BitDepth to PulseAudio (paplay) sample format identifier */
    private static string ResolvePaplayFormat(short bitDepth) => bitDepth switch
    {
        8 => "u8",
        24 => "s24le",
        32 => "s32le",
        _ => "s16le"
    };

    /* Maps AudioFormat.BitDepth to PipeWire (pw-play) sample format identifier */
    private static string ResolvePwPlayFormat(short bitDepth) => bitDepth switch
    {
        8 => "u8",
        24 => "s24",
        32 => "s32",
        _ => "s16"
    };
}
