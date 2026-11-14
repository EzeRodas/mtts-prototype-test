using System.CommandLine;
using System.IO.Pipes;
using MoonTTS.CLI.Utilities;
using MoonTTS.Infrastructure.Daemon.DTOs;

namespace MoonTTS.CLI.commands;

/* File Structure:
        - Build(IServiceProvider services) : Command
        - SendPauseAsync(CancellationToken ct) : Task<int>
*/

/* Pauses or resumes the active CLI speech playback session via IPC */
public static class PauseCommand
{
    /* Constructs the 'pause' CLI command */
    public static Command Build(IServiceProvider services)
    {
        var command = new Command("pause", "Pauses or resumes active CLI audio playback.");
        command.SetAction(async (_, cancellationToken) => await SendPauseAsync(cancellationToken));
        return command;
    }

    /* Signals an active 'mtts play' process to toggle pause state */
    private static async Task<int> SendPauseAsync(CancellationToken ct)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                DaemonIpcProtocol.PlaybackControlPipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await client.ConnectAsync(200, ct);
            await using var writer = new StreamWriter(client);
            await writer.WriteLineAsync("pause");
            await writer.FlushAsync(ct);

            Terminal.Warning("Playback pause toggled.");
            return 0;
        }
        catch
        {
            Terminal.Warning("No active CLI playback session found.");
            return 1;
        }
    }
}
