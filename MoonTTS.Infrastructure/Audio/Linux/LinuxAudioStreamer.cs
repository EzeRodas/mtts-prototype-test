using System.Diagnostics;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;

namespace MoonTTS.Infrastructure.Audio.Linux;

/* Streams audio directly to a Linux sink with automatic fallback.
   First tries with paplay (PulseAudio / pipewire-pulse), and if that doesn't work
   attempts with pw-play (Native PipeWire) */

public static class LinuxAudioStreamer
{
    /* Spawns an audio sink playback session (paplay with pw-play fallback) configured for raw PCM stdin streaming */
    public static Result<LinuxProcessRunner.SinkStreamTarget> StartSinkSession(
        string? targetSink,
        AudioFormat format,
        Func<float> volumeProvider,
        Action<Process>? onProcessStarted = null)
    {
        /* Tries launching paplay with raw pcm parameters */
        string paplayArgs = string.IsNullOrEmpty(targetSink)
            ? $"--raw --rate={format.SampleRate} --channels={format.Channels} --latency-msec=50 --format=s16le /dev/stdin"
            : $"--raw --rate={format.SampleRate} --channels={format.Channels} --latency-msec=50 --format=s16le -d \"{targetSink}\" /dev/stdin";

        var paplayResult = StartProcess("paplay", paplayArgs);
        if (paplayResult.IsSuccess)
        {
            onProcessStarted?.Invoke(paplayResult.Value.Process);
            return Result<LinuxProcessRunner.SinkStreamTarget>.Success(
                new LinuxProcessRunner.SinkStreamTarget(
                    paplayResult.Value.Process,
                    paplayResult.Value.StandardInput,
                    volumeProvider));
        }
        
        /* Fallback to pw-play if paplay doesn't work */
        string pwArgs = string.IsNullOrEmpty(targetSink)
            ? $"--raw --rate {format.SampleRate} --channels {format.Channels} --format s16 -"
            : $"--raw --rate {format.SampleRate} --channels {format.Channels} --format s16 --target \"{targetSink}\" -";

        var pwResult = StartProcess("pw-play", pwArgs);
        if (pwResult.IsSuccess)
        {
            onProcessStarted?.Invoke(pwResult.Value.Process);
            return Result<LinuxProcessRunner.SinkStreamTarget>.Success(
                new LinuxProcessRunner.SinkStreamTarget(
                    pwResult.Value.Process,
                    pwResult.Value.StandardInput,
                    volumeProvider));
        }

        return Result<LinuxProcessRunner.SinkStreamTarget>.Failure(
            $"Failed to start audio playback (paplay: {paplayResult.ErrorMessage} | pw-play: {pwResult.ErrorMessage})");
    }

    /* Helper to start an external process with redirected standard input stream */
    public static Result<(Process Process, Stream StandardInput)> StartProcess(string fileName, string arguments)
    {
        try
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!proc.Start())
                return Result<(Process, Stream)>.Failure($"Failed to start '{fileName}'.");

            return Result<(Process, Stream)>.Success((proc, proc.StandardInput.BaseStream));
        }
        catch (Exception ex)
        {
            return Result<(Process, Stream)>.Failure($"Failed to execute '{fileName}': {ex.Message}", ex);
        }
    }
}