using MoonTTS.Domain.Common;

namespace MoonTTS.Infrastructure.Audio.Linux;

/* File Structure:
        - RunCommand(string fileName, string arguments, int timeoutMs) : Result<string>
        - StartStreamingSession(string fileName, string arguments) : Result<IAudioSinkSession>
*/

/* Port abstraction for executing Linux CLI commands and spawning streaming audio subprocesses */
public interface ILinuxCommandRunner
{
    /* Executes a CLI command synchronously with a timeout and returns stdout or an error Result */
    Result<string> RunCommand(string fileName, string arguments, int timeoutMs = 1000);

    /* Spawns a streaming subprocess wrapped in a self-cleaning IAudioSinkSession */
    Result<IAudioSinkSession> StartStreamingSession(string fileName, string arguments);
}
