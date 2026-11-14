using System.CommandLine;
using System.IO.Pipes;
using Microsoft.Extensions.DependencyInjection;
using MoonTTS.CLI.Utilities;
using MoonTTS.Infrastructure.Daemon;
using MoonTTS.Infrastructure.Daemon.DTOs;

namespace MoonTTS.CLI.commands;

/* File Structure:
        - Build(IServiceProvider services) : Command
        - SendStopAsync(DaemonClient daemonClient, CancellationToken ct) : Task<int>
*/

/* Cancels synthesis and terminates active CLI speech playback across processes */
public static class StopCommand
{
    /* Constructs the 'stop' CLI command */
    public static Command Build(IServiceProvider services)
    {
        var command = new Command("stop", "Stops active CLI audio playback.");
        command.SetAction(async (_, cancellationToken) =>
        {
            var daemonClient = services.GetRequiredService<DaemonClient>();
            return await SendStopAsync(daemonClient, cancellationToken);
        });

        return command;
    }

    /* Signals any active 'mtts play' session and running daemon synthesis stream to stop immediately */
    private static async Task<int> SendStopAsync(DaemonClient daemonClient, CancellationToken ct)
    {
        bool stoppedPlayback = false;

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                DaemonIpcProtocol.PlaybackControlPipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await client.ConnectAsync(200, ct);
            await using var writer = new StreamWriter(client);
            await writer.WriteLineAsync("stop");
            await writer.FlushAsync(ct);
            stoppedPlayback = true;
        }
        catch
        {
            /* No active 'mtts play' listener; still check daemon synthesis */
        }

        if (await daemonClient.IsRunningAsync(150))
        {
            var res = await daemonClient.StopSynthesisAsync(ct);
            if (res.IsSuccess)
                stoppedPlayback = true;
        }

        if (stoppedPlayback)
        {
            Terminal.Warning("Playback stopped.");
            return 0;
        }

        Terminal.Warning("No active CLI playback session found.");
        return 1;
    }
}
