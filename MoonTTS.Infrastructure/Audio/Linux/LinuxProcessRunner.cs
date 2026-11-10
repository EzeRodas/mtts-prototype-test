using System.Diagnostics;
using System.Runtime.InteropServices;
using MoonTTS.Domain.Common;

namespace MoonTTS.Infrastructure.Audio.Linux;

/* File Structure:
        - Instance : ILinuxCommandRunner
        - RunCommand(string fileName, string arguments, int timeoutMs) : Result<string>
        - StartStreamingSession(string fileName, string arguments) : Result<IAudioSinkSession>
        - SendPauseSignal(int pid) : Result
        - SendResumeSignal(int pid) : Result
        - LinuxProcessSinkSession : IAudioSinkSession
*/

public class LinuxProcessRunner : ILinuxCommandRunner
{
    private const int SigCont = 18;
    private const int SigStop = 19;

    /* Shared singleton adapter instance for default command and subprocess execution */
    public static ILinuxCommandRunner Instance { get; } = new LinuxProcessRunner();

    /* Import to manage Pulse and Pipewire processes */
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    /* Explicit interface forwarding to static RunCommand */
    Result<string> ILinuxCommandRunner.RunCommand(string fileName, string arguments, int timeoutMs) =>
        RunCommand(fileName, arguments, timeoutMs);

    /* Explicit interface forwarding to static StartStreamingSession */
    Result<IAudioSinkSession> ILinuxCommandRunner.StartStreamingSession(string fileName, string arguments) =>
        StartStreamingSession(fileName, arguments);

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
                try { proc.Kill(entireProcessTree: true); } catch { }
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

    /* Spawns a streaming audio subprocess with redirected stdin wrapped in a self-cleaning IAudioSinkSession */
    public static Result<IAudioSinkSession> StartStreamingSession(string fileName, string arguments)
    {
        Process? proc = null;
        try
        {
            proc = new Process
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
            {
                proc.Dispose();
                return Result<IAudioSinkSession>.Failure($"Failed to start '{fileName}'.");
            }

            return Result<IAudioSinkSession>.Success(
                new LinuxProcessSinkSession(proc, proc.StandardInput.BaseStream));
        }
        catch (Exception ex)
        {
            try { proc?.Dispose(); } catch { }
            return Result<IAudioSinkSession>.Failure($"Failed to execute '{fileName}': {ex.Message}", ex);
        }
    }
    
    /* Sends SIGSTOP to a process to pause playback without closing its audio stream */
    public static Result SendPauseSignal(int pid)
    {
        try
        {
            int ret = kill(pid, SigStop); // SIGSTOP
            return ret == 0 ? Result.Success() : Result.Failure($"kill -STOP failed: {Marshal.GetLastPInvokeError()}");
        }
        catch (Exception ex) { return Result.Failure(ex.Message, ex); }
    }
    
    /* Sends SIGCONT to a process to resume paused audio playback */
    public static Result SendResumeSignal(int pid)
    {
        try
        {
            int ret = kill(pid, SigCont); // SIGCONT
            return ret == 0 ? Result.Success() : Result.Failure($"kill -CONT failed: {Marshal.GetLastPInvokeError()}");
        }
        catch (Exception ex) { return Result.Failure(ex.Message, ex); }
    }

    /* Encapsulates an active Linux audio player subprocess (paplay or pw-play) and its stdin pipe */
    private sealed class LinuxProcessSinkSession(Process process, Stream standardInput) : IAudioSinkSession
    {
        private readonly Lock _stateLock = new();
        private bool _isDisposed;

        /* Indicates whether the underlying subprocess is still running and accepting PCM data */
        public bool IsActive
        {
            get
            {
                lock (_stateLock)
                {
                    if (_isDisposed)
                        return false;

                    try
                    {
                        return !process.HasExited;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        /* Writes a raw PCM slice to the subprocess standard input pipe and flushes it */
        public async Task<Result> WriteAsync(ReadOnlyMemory<byte> pcmSlice, CancellationToken cancellationToken = default)
        {
            if (pcmSlice.IsEmpty)
                return Result.Success();

            if (!IsActive)
                return Result.Failure("Linux audio sink subprocess has exited.");

            try
            {
                // Writes directly to pipe; kernel backpressure paces audio smoothly
                await standardInput.WriteAsync(pcmSlice, cancellationToken);
                await standardInput.FlushAsync(cancellationToken);
                return Result.Success();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Result.Failure($"Failed to write PCM slice to Linux audio sink: {ex.Message}", ex);
            }
        }

        /* Closes the standard input pipe and waits for the subprocess to finish playing buffered samples */
        public async Task<Result> CompleteAsync(CancellationToken cancellationToken = default)
        {
            lock (_stateLock)
            {
                if (_isDisposed)
                    return Result.Success();
            }

            try
            {
                try { standardInput.Close(); } catch { }
                await process.WaitForExitAsync(cancellationToken);
                return Result.Success();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Result.Failure($"Failed while draining Linux audio sink subprocess: {ex.Message}", ex);
            }
        }

        /* Pauses the underlying subprocess via SIGSTOP */
        public Result Pause()
        {
            lock (_stateLock)
            {
                if (_isDisposed)
                    return Result.Failure("Audio sink session is disposed.");

                try
                {
                    if (process.HasExited)
                        return Result.Failure("Audio sink subprocess has already exited.");

                    return SendPauseSignal(process.Id);
                }
                catch (Exception ex)
                {
                    return Result.Failure($"Failed to pause Linux audio sink: {ex.Message}", ex);
                }
            }
        }

        /* Resumes the paused subprocess via SIGCONT */
        public Result Resume()
        {
            lock (_stateLock)
            {
                if (_isDisposed)
                    return Result.Failure("Audio sink session is disposed.");

                try
                {
                    if (process.HasExited)
                        return Result.Failure("Audio sink subprocess has already exited.");

                    return SendResumeSignal(process.Id);
                }
                catch (Exception ex)
                {
                    return Result.Failure($"Failed to resume Linux audio sink: {ex.Message}", ex);
                }
            }
        }

        /* Terminates the subprocess tree if still running and releases all OS handles */
        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;

                try { standardInput.Close(); } catch { }

                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }

                try { process.Dispose(); } catch { }
            }
        }
    }
}
