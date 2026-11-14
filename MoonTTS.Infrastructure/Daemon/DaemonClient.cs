using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Infrastructure.Daemon.DTOs;
using MoonTTS.Infrastructure.Persistence;

namespace MoonTTS.Infrastructure.Daemon;

/* File Structure:
        - IsRunningAsync(int timeoutMs) : Task<bool>
        - EnsureRunningAsync() : Task<Result>
        - StreamSpeechAsync(string text, float? speed, CancellationToken ct) : Task<Result<(AudioFormat Format, IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks)>>
        - ReadStreamChunksAsync(NamedPipeClientStream client, CancellationToken ct) : IAsyncEnumerable<ReadOnlyMemory<byte>>
        - SynthesizeAsync(string text, float? speed, CancellationToken ct) : Task<Result<byte[]>>
        - KillDaemonAsync(CancellationToken ct) : Task<Result>
        - StopSynthesisAsync(CancellationToken ct) : Task<Result>
        - GetStatusAsync(CancellationToken ct) : Task<DaemonResponse?>
        - SendControlRequestAsync(DaemonRequest request, CancellationToken ct) : Task<DaemonResponse?> 
*/

public class DaemonClient
{
    /* Sees if the daemon is listening */
    public async Task<bool> IsRunningAsync(int timeoutMs = 200)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                DaemonIpcProtocol.PipeName,
                PipeDirection.InOut);
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<Result> EnsureRunningAsync()
    {
        if (await IsRunningAsync()) return Result.Success();
        
        /* Auto spawn daemon in the background */
        string exePath = Environment.ProcessPath ?? "mtts";
        string args = "daemon run";
        
        /* If running uncompiled from "dotnet" command */
        if (Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string dllPath = Path.Combine(AppContext.BaseDirectory, "mtts.dll");
            args = $"\"{dllPath}\" daemon run";
        }
        
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to auto-spawn daemon: {ex.Message}", ex);
        }

        /* Waits up to 10 seconds for the daemon to load the model and open the pipe */
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            if (await IsRunningAsync(100))
                return Result.Success();
        }
        return Result.Failure("Daemon was spawned but timed out loading the model.");
    }

    public async Task<Result<(AudioFormat Format, IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks)>> StreamSpeechAsync(
            string text,
            float? speed = null,
            CancellationToken ct = default)
    {
        NamedPipeClientStream? client = null;
        try
        {
            client = new NamedPipeClientStream(".", DaemonIpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, ct).ConfigureAwait(false);

            var request = new DaemonRequest
            {
                Action = DaemonIpcProtocol.ActionSynthesize,
                Text = text,
                Speed = speed
            };

            byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, AppJsonContext.Default.DaemonRequest);
            await DaemonIpcProtocol.WriteFrameAsync(client, DaemonOpcode.Request, requestBytes, ct).ConfigureAwait(false);

            var (opcode, payload) = await DaemonIpcProtocol.ReadFrameAsync(client, ct).ConfigureAwait(false);
            if (opcode != DaemonOpcode.Response)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return Result<(AudioFormat Format, IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks)>.Failure(
                    $"Unexpected opcode from daemon: {opcode}");
            }

            var response = JsonSerializer.Deserialize(payload, AppJsonContext.Default.DaemonResponse);
            if (response == null || !response.Success)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return Result<(AudioFormat Format, IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks)>.Failure(
                    response?.ErrorMessage ?? "Daemon synthesis failed.");
            }

            if (response.Format == null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return Result<(AudioFormat Format, IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks)>.Failure(
                    "Daemon did not provide dynamic AudioFormat specification.");
            }
            
            var format = response.Format.Value;
            var chunks = ReadStreamChunksAsync(client, ct);
            return Result<(AudioFormat Format, IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks)>.Success((format, chunks));
        }
        catch (Exception ex)
        {
            if (client != null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            return Result<(AudioFormat Format, IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks)>.Failure(
                $"Failed to stream speech from daemon: {ex.Message}", ex);
        }
    }
    
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadStreamChunksAsync(
        NamedPipeClientStream client,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using (client.ConfigureAwait(false))
        {
            while (!ct.IsCancellationRequested)
            {
                (DaemonOpcode opcode, byte[] payload) frame;
                try
                {
                    frame = await DaemonIpcProtocol.ReadFrameAsync(client, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (EndOfStreamException)
                {
                    yield break;
                }

                if (frame.opcode == DaemonOpcode.AudioChunk)
                {
                    yield return frame.payload;
                }
                else if (frame.opcode == DaemonOpcode.AudioEnd)
                {
                    yield break;
                }
                else if (frame.opcode == DaemonOpcode.Error)
                {
                    string msg = Encoding.UTF8.GetString(frame.payload);
                    throw new InvalidOperationException($"Daemon streaming error: {msg}");
                }
                else
                {
                    yield break;
                }
            }
        }
    }
    
    public async Task<Result<byte[]>> SynthesizeAsync(string text, float? speed = null, CancellationToken ct = default)
    {
        var streamResult = await StreamSpeechAsync(text, speed, ct).ConfigureAwait(false);
        if (streamResult.IsFailure)
        {
            return Result<byte[]>.Failure(
                streamResult.ErrorMessage ?? 
                "Daemon synthesis failed.", 
                streamResult.Exception);
        }

        using var pcmAccumulator = new MemoryStream();
        try
        {
            await foreach (var chunk in streamResult.Value.Chunks.WithCancellation(ct)
                               .ConfigureAwait(false))
            {
                pcmAccumulator.Write(chunk.Span);
            }

            byte[] wav = WavWriter.CreateWav(pcmAccumulator.ToArray(), streamResult.Value.Format);
            return Result<byte[]>.Success(wav);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"Failed accumulating daemon speech stream: {ex.Message}", ex);
        }
    }

    /* Requests the termination of the background daemon process */
    public async Task<Result> KillDaemonAsync(CancellationToken ct = default)
    {
        if (!await IsRunningAsync().ConfigureAwait(false))
            return Result.Failure("Daemon is not running.");

        var response = await SendControlRequestAsync(new DaemonRequest { Action = DaemonIpcProtocol.ActionKill }, ct)
            .ConfigureAwait(false);
        return response?.Success == true ? Result.Success() : Result.Failure("Failure to stop daemon.");
    }

    /* Requests cancellation of an ongoing daemon synthesis stream */
    public async Task<Result> StopSynthesisAsync(CancellationToken ct = default)
    {
        if (!await IsRunningAsync().ConfigureAwait(false))
            return Result.Success();

        var response = await SendControlRequestAsync(new DaemonRequest { Action = DaemonIpcProtocol.ActionStop }, ct)
            .ConfigureAwait(false);
        return response?.Success == true ? Result.Success() : Result.Failure("Failed to stop daemon synthesis.");
    }
    
    /* Queries the status, active model, and idle countdown of the background daemon */
    public async Task<DaemonResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        if (!await IsRunningAsync().ConfigureAwait(false)) return null;
        return await SendControlRequestAsync(new DaemonRequest { Action = DaemonIpcProtocol.ActionStatus }, ct)
            .ConfigureAwait(false);
    }

    /* Sends a control request to the daemon and returns the parsed JSON response */
    private async Task<DaemonResponse?> SendControlRequestAsync(DaemonRequest request, CancellationToken ct)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".", 
                DaemonIpcProtocol.PipeName, 
                PipeDirection.InOut, 
                PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, ct).ConfigureAwait(false);

            byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, AppJsonContext.Default.DaemonRequest);
            await DaemonIpcProtocol.WriteFrameAsync(client, DaemonOpcode.Request, requestBytes, ct)
                .ConfigureAwait(false);

            var (opcode, payload) = await DaemonIpcProtocol.ReadFrameAsync(client, ct).ConfigureAwait(false);
            if (opcode != DaemonOpcode.Response) return null;

            return JsonSerializer.Deserialize(payload, AppJsonContext.Default.DaemonResponse);
        }
        catch
        {
            return null;
        }
    }
}