using System.Diagnostics;
using System.Runtime.InteropServices;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;

namespace MoonTTS.Infrastructure.Audio.Linux;

public class LinuxProcessRunner
{
    /* Import to manage Pulse and Pipewire processes */
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    /* Represents an active Linux sound sink process */
    public record SinkStreamTarget(
        Process Process,
        Stream StandardInput,
        Func<float> VolumeProvider);
    
    /* Executes a shell command synchronously with a specified timeout */
    public static Result<string> RunCommand(string fileName, string arguments, int timeoutMs = 1000)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (!proc.Start())
                return Result<string>.Failure($"Failed to start '{fileName}'.");

            string output = proc.StandardOutput.ReadToEnd();
            string error = proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
                return Result<string>.Failure($"'{fileName}' timed out after {timeoutMs}ms.");
            }
            return proc.ExitCode == 0 
                ? Result<string>.Success(output)
                : Result<string>.Failure($"'{fileName} {arguments}' failed (exit {proc.ExitCode}): {error.Trim()}");
        }
        catch (Exception ex)
        {
            return Result<string>.Failure($"Failed to execute '{fileName}': {ex.Message}", ex);
        }
    }
    
    /* Sends SIGSTOP to a process to pause playback without closing its audio stream */
    public static Result SendPauseSignal(int pid)
    {
        try
        {
            int ret = kill(pid, 19); // SIGSTOP
            return ret == 0 ? Result.Success() : Result.Failure($"kill -STOP failed: {Marshal.GetLastPInvokeError()}");
        }
        catch (Exception ex) { return Result.Failure(ex.Message, ex); }
    }
    
    /* Sends SIGCONT to a process to resume paused audio playback */
    public static Result SendResumeSignal(int pid)
    {
        try
        {
            int ret = kill(pid, 18); // SIGCONT
            return ret == 0 ? Result.Success() : Result.Failure($"kill -CONT failed: {Marshal.GetLastPInvokeError()}");
        }
        catch (Exception ex) { return Result.Failure(ex.Message, ex); }
    }

    /* Streams audio chunks into one or more audio sink processes */
    public static async Task<Result> PipeStreamedAudioAsync(
        IReadOnlyList<SinkStreamTarget> targets,
        IAsyncEnumerable<ReadOnlyMemory<byte>> pcmChunks,
        AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        /* Return early if no output sinks were requested or active */
        if (targets.Count == 0)
            return Result.Success();

        try
        {
            const int chunkSize = 2400; /* About 50ms at 24KHz 16 bit mono */
            byte[] primaryBuffer = new byte[chunkSize];

            /* Consumes incoming audio chunks as soon as they finish synthesizing */
            await foreach (var chunk in pcmChunks.WithCancellation(cancellationToken))
            {
                if (chunk.Length == 0) continue;

                int remaining = chunk.Length;
                int currentPos = 0;

                while (remaining > 0 && !cancellationToken.IsCancellationRequested)
                {
                    int bytesToWrite = Math.Min(remaining, chunkSize);

                    /* Writes to all active targets */
                    foreach (var target in targets)
                    {
                        if (target.Process.HasExited)
                            continue;
                        chunk.Span.Slice(currentPos, bytesToWrite).CopyTo(primaryBuffer);

                        // SIMD volume scaling in-place
                        float liveVolume = target.VolumeProvider();
                        SimdAudioProcessor.ScaleVolumeInPlace(primaryBuffer.AsSpan(0, bytesToWrite), liveVolume);

                        // Writes directly to pipe; kernel backpressure paces audio smoothly
                        await target.StandardInput.WriteAsync(primaryBuffer.AsMemory(0, bytesToWrite), cancellationToken);
                        await target.StandardInput.FlushAsync(cancellationToken);
                    }

                    currentPos += bytesToWrite;
                    remaining -= bytesToWrite;
                }
            }

            foreach (var target in targets)
            {
                try { target.StandardInput.Close(); } catch { }
            }

            /* Waits until all player processes finish playing their remaining samples */
            foreach (var target in targets)
            {
                await target.Process.WaitForExitAsync(cancellationToken);
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
    }
}