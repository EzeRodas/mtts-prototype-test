using System.CommandLine;
using System.IO.Pipes;
using Microsoft.Extensions.DependencyInjection;
using MoonTTS.CLI.Utilities;
using MoonTTS.Core.Services;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Infrastructure.Daemon;
using MoonTTS.Infrastructure.Daemon.DTOs;

namespace MoonTTS.CLI.commands;

/* File Structure:
        - Build(IServiceProvider services) : Command
        - ExecuteAsync(...) : Task<int>
        - ListenForControlCommandsAsync(...) : Task
*/

/* Synthesizes text given into speech with default or custom parameters */
public static class PlayCommand
{
    /* Constructs the 'play' CLI command and binds playback options */
    public static Command Build(IServiceProvider services)
    {
        var command = new Command("play", "Synthesizes and plays the provided text.");

        /* Options */
        var textArg = new Argument<string>("text") { Description = "Text to be synthesized." };
        var volOption = new Option<int?>("-v", "--volume") { Description = "Sets the volume level from 0 to 100." };
        var primaryVolOption = new Option<int?>("-p", "--primary-volume")
            { Description = "Sets the volume level for the main output" };
        var monitorVolOption = new Option<int?>("-m", "--monitor-volume")
            { Description = "Sets the volume level for the monitor output" };
        var speedOption = new Option<float?>("-s", "--speed")
            { Description = "Sets the audio speed from 0.0 to 2.0 if supported." };
        var outputOption = new Option<string?>("-o", "--output")
            { Description = "Optional file path to save generated .wav file." };
        
        command.Add(textArg);
        command.Add(volOption);
        command.Add(primaryVolOption);
        command.Add(monitorVolOption);
        command.Add(speedOption);
        command.Add(outputOption);
        
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string text = parseResult.GetValue(textArg) ?? string.Empty;
            int? volume = parseResult.GetValue(volOption);
            int? primaryVol = parseResult.GetValue(primaryVolOption) ?? volume;
            int? monitorVol = parseResult.GetValue(monitorVolOption) ?? volume;
            float? speed = parseResult.GetValue(speedOption);
            string? output = parseResult.GetValue(outputOption);

            var pipeline = services.GetRequiredService<ISpeechPipeline>();
            var daemonClient = services.GetRequiredService<DaemonClient>();
            var audioPlayer = services.GetRequiredService<IAudioPlayer>();
            var settingsManager = services.GetRequiredService<SettingsManager>();
            
            return await ExecuteAsync(
                text, 
                primaryVol, 
                monitorVol, 
                speed, 
                output, 
                pipeline, 
                daemonClient, 
                audioPlayer, 
                settingsManager, 
                cancellationToken);
        });

        return command;
    }

    /* Executes synthesis via the background daemon when active, or in-process otherwise, exposing pause/stop IPC control */
    private static async Task<int> ExecuteAsync(
        string text,
        int? primaryVol,
        int? monitorVol,
        float? speed,
        string? output,
        ISpeechPipeline pipeline,
        DaemonClient daemonClient,
        IAudioPlayer audioPlayer,
        SettingsManager settingsManager,
        CancellationToken cancellationToken)
    {
        Terminal.Info($"Synthesizing: {text}");

        using var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var controlStopCts = new CancellationTokenSource();
        bool usingPipeline = false;

        Task controlListenerTask = ListenForControlCommandsAsync(
            () => usingPipeline,
            pipeline,
            audioPlayer,
            playbackCts,
            controlStopCts.Token);

        try
        {
            /* Checks if daemon active */
            if (await daemonClient.IsRunningAsync(150))
            {
                var daemonResult = await daemonClient.StreamSpeechAsync(text, speed, playbackCts.Token);
                if (daemonResult.IsSuccess)
                {
                    /* If active routes synthesis through it */
                    var (format, chunks) = daemonResult.Value;
                    var config = settingsManager.CurrentAppConfig;

                    string? primaryId = config.EnablePrimaryPlayback ? config.PrimaryAudioDeviceId : null;
                    string? monitorId = config.EnableMonitorPlayback ? config.MonitorAudioDeviceId : null;

                    audioPlayer.SetOutputDevices(primaryId, monitorId);
                    audioPlayer.SetVolume(
                        primaryVol ?? config.PrimaryVolume,
                        monitorVol ?? config.MonitorVolume);

                    using var pcmAccumulator = new MemoryStream();

                    /* Helper method */
                    async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAndStream()
                    {
                        await foreach (var chunk in chunks.WithCancellation(playbackCts.Token))
                        {
                            pcmAccumulator.Write(chunk.Span);
                            yield return chunk;
                        }
                    }

                    var playResult = await audioPlayer.StreamPcmAsync(CaptureAndStream(), format, playbackCts.Token);
                    if (playResult.IsFailure)
                    {
                        Terminal.Error($"Error: {playResult.ErrorMessage ?? "Daemon playback failed."}");
                        return 1;
                    }

                    /* Writes to file if specified */
                    if (!string.IsNullOrWhiteSpace(output) && pcmAccumulator.Length > 0)
                    {
                        byte[] wav = WavWriter.CreateWav(pcmAccumulator.ToArray(), format);
                        await File.WriteAllBytesAsync(output, wav, playbackCts.Token);
                    }

                    var duration = format.GetDuration((int)pcmAccumulator.Length);
                    Terminal.Success($"Speech synthesized via daemon successfully ({duration.TotalSeconds:F1}s)");
                    return 0;
                }
            }

            /* Fallback for when the daemon is disabled */
            usingPipeline = true;
            var options = new SpeechOptions
            {
                Speed = speed,
                PrimaryVolume = primaryVol,
                MonitorVolume = monitorVol,
                ExportPath = output
            };

            var result = await pipeline.SpeakAsync(text, options, playbackCts.Token);
            if (result.IsFailure)
            {
                Terminal.Error($"Error: {result.ErrorMessage ?? result.Exception?.Message ?? "Playback failed."}");
                return 1;
            }

            Terminal.Success($"Speech synthesized successfully ({result.Value?.AudioDuration.TotalSeconds ?? 0:F1}s)");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Terminal.Warning("Playback stopped.");
            return 0;
        }
        finally
        {
            controlStopCts.Cancel();
            try { await controlListenerTask; } catch { /* Shutdown race ignored */ }
        }
    }

    /* Listens on the user-scoped playback control pipe so concurrent 'pause' and 'stop' commands control active audio */
    private static async Task ListenForControlCommandsAsync(
        Func<bool> isUsingPipeline,
        ISpeechPipeline pipeline,
        IAudioPlayer audioPlayer,
        CancellationTokenSource playbackCts,
        CancellationToken listenerStopToken)
    {
        bool isPaused = false;

        while (!listenerStopToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    DaemonIpcProtocol.PlaybackControlPipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(listenerStopToken);
                using var reader = new StreamReader(server);
                string? command = await reader.ReadLineAsync(listenerStopToken);

                if (string.Equals(command, "pause", StringComparison.OrdinalIgnoreCase))
                {
                    if (isUsingPipeline())
                    {
                        if (!pipeline.IsPaused) pipeline.Pause();
                        else pipeline.Resume();
                    }
                    else
                    {
                        isPaused = !isPaused;
                        if (isPaused) audioPlayer.Pause();
                        else audioPlayer.Resume();
                    }
                }
                else if (string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (isUsingPipeline())
                        pipeline.Stop();
                    else
                        audioPlayer.Stop();

                    playbackCts.Cancel();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                /* Ignore transient IPC errors while keeping playback alive */
            }
        }
    }
}
