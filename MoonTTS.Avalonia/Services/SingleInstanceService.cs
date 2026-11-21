using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace MoonTTS.Avalonia.Services;

/* Makes sure that there's ony one instance of the app running to avoid duplicate instances */

public class SingleInstanceService
{
    private static string PipeName => $"Moon-TTS_SingleInstance_Pipe_{Environment.UserName}";
    
    /* If there's an existing instance it just toggles its visibility */
    public static bool TrySendToggleToExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(150);
            using var writer = new StreamWriter(client);
            writer.WriteLine("toggle");
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void StartServer(Action onToggleReceived, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await server.WaitForConnectionAsync(cancellationToken);
                    using var reader = new StreamReader(server);
                    string? command = await reader.ReadLineAsync(cancellationToken);

                    if (command == "toggle")
                    {
                        onToggleReceived();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    /* Safe to ignore */
                }
            }
        }, cancellationToken);
    }
}