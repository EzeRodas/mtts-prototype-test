using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization.Metadata;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.ValueObjects;
using MoonTTS.Engine.Protocol;

namespace MoonTTS.Infrastructure.Tts;

/* File Structure:
        - Start(EngineDescriptor engine) : EngineProcess
        - HasExited : bool
        - RequestAsync(
            EngineOpcode opcode,
            ReadOnlyMemory<byte> payload,
            EngineOpcode expectedOpcode,
            TimeSpan timeout,
            CancellationToken cancellationToken) : Task<EngineFrame>
        - RequestJsonAsync<TResponse>(
            EngineOpcode opcode,
            ReadOnlyMemory<byte> payload,
            EngineOpcode expectedOpcode,
            JsonTypeInfo<TResponse> responseType,
            TimeSpan timeout,
            CancellationToken cancellationToken) : Task<TResponse>
        - StreamAsync(EngineOpcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) : IAsyncEnumerable<ReadOnlyMemory<byte>>
        - AbortStreamAsync(Task<EngineFrame?>? pendingRead) : Task
        - WriteAsync(EngineOpcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) : Task
        - ReadFrameAsync(CancellationToken cancellationToken) : Task<EngineFrame?>
        - ToWorkerException(EngineFrame frame) : EngineWorkerException
        - Kill() : void
        - Dispose() : void
*/

/* Owns one engine worker process and its binary stdin/stdout channel.
   One exchange runs at a time; a stream that is cancelled or abandoned is drained to its terminal frame so the
   channel never goes out of sync. A worker that stops answering is killed */
public sealed class EngineProcess : IDisposable
{
    private static readonly TimeSpan CancelDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly string _engineId;

    /* One request/response (or stream) exchange at a time */
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);
    private bool _disposed;

    /* Wraps an already started worker */
    private EngineProcess(Process process, string engineId)
    {
        _process = process;
        _engineId = engineId;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
    }

    /* True once the worker exited (crash, Shutdown or killed) */
    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    /* Launches the worker inside its engine folder and forwards its stderr to the host log */
    public static EngineProcess Start(EngineDescriptor engine)
    {
        if (!File.Exists(engine.ExecutablePath))
            throw new EngineWorkerException($"Worker executable not found: '{engine.ExecutablePath}'.");

        var startInfo = new ProcessStartInfo
        {
            FileName = engine.ExecutablePath,
            WorkingDirectory = engine.RootDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo)
                      ?? throw new EngineWorkerException($"Failed to start the '{engine.Id}' engine worker.");

        /* stderr must always be drained, otherwise a chatty worker blocks once the pipe buffer fills */
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                AppLogger.Info($"engine:{engine.Id}", e.Data);
        };
        process.BeginErrorReadLine();

        /* On Windows, attach the worker to a kill-on-close Job Object so a hard host crash cannot leave orphan workers */
        if (OperatingSystem.IsWindows())
            WindowsJobObject.AssignProcess(process);

        return new EngineProcess(process, engine.Id);
    }

    /* Sends one request and waits for its reply. Error frames and unexpected opcodes raise EngineWorkerException.
       On timeout the worker is killed, since its channel state is no longer known */
    public async Task<EngineFrame> RequestAsync(
        EngineOpcode opcode,
        ReadOnlyMemory<byte> payload,
        EngineOpcode expectedOpcode,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await _exchangeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await WriteAsync(opcode, payload, timeoutCts.Token).ConfigureAwait(false);
                var frame = await ReadFrameAsync(timeoutCts.Token).ConfigureAwait(false)
                            ?? throw new EngineWorkerException($"The '{_engineId}' engine worker exited during {opcode}.");

                if (frame.Opcode == EngineOpcode.Error)
                    throw ToWorkerException(frame);
                if (frame.Opcode != expectedOpcode)
                    throw new EngineWorkerException(
                        $"The '{_engineId}' engine worker answered {opcode} with {frame.Opcode} instead of {expectedOpcode}.");

                return frame;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill();
                throw new EngineWorkerException(
                    $"The '{_engineId}' engine worker did not answer {opcode} within {timeout.TotalSeconds:0}s.");
            }
            catch (OperationCanceledException)
            {
                /* A half read reply leaves the channel unusable, so the worker gets replaced on next use */
                Kill();
                throw;
            }
        }
        finally
        {
            _exchangeLock.Release();
        }
    }

    /* Sends one request and deserializes the JSON payload of its reply */
    public async Task<TResponse> RequestJsonAsync<TResponse>(
        EngineOpcode opcode,
        ReadOnlyMemory<byte> payload,
        EngineOpcode expectedOpcode,
        JsonTypeInfo<TResponse> responseType,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var frame = await RequestAsync(opcode, payload, expectedOpcode, timeout, cancellationToken).ConfigureAwait(false);
        return EngineIpcProtocol.ReadJson(frame, responseType);
    }

    /* Sends a streaming request and yields every AudioChunk payload until EndOfStream.
       Cancellation sends a Cancel frame and drains the worker's remaining frames before returning */
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamAsync(
        EngineOpcode opcode,
        ReadOnlyMemory<byte> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _exchangeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        bool reachedTerminalFrame = false;
        Task<EngineFrame?>? pendingRead = null;
        try
        {
            await WriteAsync(opcode, payload, CancellationToken.None).ConfigureAwait(false);

            while (true)
            {
                /* Reads are never cancelled mid-frame: the caller's token only stops waiting for them */
                pendingRead = ReadFrameAsync(CancellationToken.None);
                var frame = await pendingRead.WaitAsync(cancellationToken).ConfigureAwait(false);
                pendingRead = null;

                if (frame == null)
                {
                    reachedTerminalFrame = true;
                    throw new EngineWorkerException($"The '{_engineId}' engine worker exited during {opcode}.");
                }

                var current = frame.Value;
                switch (current.Opcode)
                {
                    case EngineOpcode.AudioChunk:
                        if (!current.Payload.IsEmpty)
                            yield return current.Payload;
                        break;
                    case EngineOpcode.EndOfStream:
                        reachedTerminalFrame = true;
                        yield break;
                    case EngineOpcode.Error:
                        reachedTerminalFrame = true;
                        throw ToWorkerException(current);
                    default:
                        reachedTerminalFrame = true;
                        Kill();
                        throw new EngineWorkerException(
                            $"The '{_engineId}' engine worker sent {current.Opcode} while streaming {opcode}.");
                }
            }
        }
        finally
        {
            if (!reachedTerminalFrame)
                await AbortStreamAsync(pendingRead).ConfigureAwait(false);
            _exchangeLock.Release();
        }
    }

    /* Asks the worker to stop the active stream and consumes frames until its terminal frame.
       If the worker does not comply in time it is killed */
    private async Task AbortStreamAsync(Task<EngineFrame?>? pendingRead)
    {
        if (HasExited)
            return;

        try
        {
            using var drainCts = new CancellationTokenSource(CancelDrainTimeout);
            await WriteAsync(EngineOpcode.Cancel, ReadOnlyMemory<byte>.Empty, drainCts.Token).ConfigureAwait(false);

            while (true)
            {
                var frame = await (pendingRead ?? ReadFrameAsync(CancellationToken.None))
                    .WaitAsync(drainCts.Token).ConfigureAwait(false);
                pendingRead = null;

                if (frame == null
                    || frame.Value.Opcode == EngineOpcode.EndOfStream
                    || frame.Value.Opcode == EngineOpcode.Error)
                    return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or EngineWorkerException or IOException
                                       or InvalidDataException or ObjectDisposedException)
        {
            Kill();
        }
    }

    /* Writes a frame to the worker's stdin */
    private async Task WriteAsync(EngineOpcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        try
        {
            await EngineIpcProtocol.WriteFrameAsync(_input, opcode, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new EngineWorkerException($"The '{_engineId}' engine worker is not accepting requests.", innerException: ex);
        }
    }

    /* Reads a frame from the worker's stdout, mapping a broken channel to EngineWorkerException */
    private async Task<EngineFrame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await EngineIpcProtocol.ReadFrameAsync(_output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Kill();
            throw new EngineWorkerException($"Lost the channel to the '{_engineId}' engine worker: {ex.Message}", innerException: ex);
        }
    }

    /* Converts an Error frame into an exception carrying the worker's code and message */
    private EngineWorkerException ToWorkerException(EngineFrame frame)
    {
        try
        {
            var error = EngineIpcProtocol.ReadJson(frame, EngineProtocolJsonContext.Default.EngineErrorMessage);
            return new EngineWorkerException($"Engine '{_engineId}' reported: {error.Message}", error.Code);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            return new EngineWorkerException($"Engine '{_engineId}' reported an unreadable error.");
        }
    }

    /* Terminates the worker immediately */
    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            /* Already exited */
        }
    }

    /* Sends a best-effort Shutdown frame, closes stdin, and kills the worker if it does not exit promptly */
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!HasExited)
            {
                try
                {
                    using var shutdownCts = new CancellationTokenSource(300);
                    EngineIpcProtocol.WriteFrameAsync(_input, EngineOpcode.Shutdown, ReadOnlyMemory<byte>.Empty, shutdownCts.Token)
                        .AsTask()
                        .Wait(300);
                }
                catch
                {
                    /* Worker may already be exiting or pipe closed */
                }
            }

            _input.Dispose();
            if (!_process.WaitForExit(ShutdownTimeout))
                Kill();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Kill();
        }
        finally
        {
            _process.Dispose();
            _exchangeLock.Dispose();
        }
    }

    /* Process-wide Windows Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE */
    private static unsafe class WindowsJobObject
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private static readonly nint JobHandle = CreateKillOnCloseJob();

        /* Assigns a child worker process to the shared kill-on-close Job Object */
        public static void AssignProcess(Process process)
        {
            if (JobHandle == 0)
                return;

            try
            {
                AssignProcessToJobObject(JobHandle, process.Handle);
            }
            catch
            {
                /* Best-effort; e.g. process already exited or nested job disallowed on legacy OS */
            }
        }

        private static nint CreateKillOnCloseJob()
        {
            if (!OperatingSystem.IsWindows())
                return 0;

            try
            {
                nint job = CreateJobObjectW(0, null);
                if (job == 0)
                    return 0;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };

                if (!SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformation,
                        &info,
                        (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
                {
                    CloseHandle(job);
                    return 0;
                }

                return job;
            }
            catch
            {
                return 0;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(
            nint hJob,
            int jobObjectInfoClass,
            void* lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(nint hObject);
    }
}
