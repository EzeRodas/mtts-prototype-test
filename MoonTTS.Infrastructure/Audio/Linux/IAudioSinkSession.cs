using MoonTTS.Domain.Common;

namespace MoonTTS.Infrastructure.Audio.Linux;

/* File Structure:
        - IsActive : bool
        - WriteAsync(ReadOnlyMemory<byte> pcmSlice, CancellationToken cancellationToken) : Task<Result>
        - CompleteAsync(CancellationToken cancellationToken) : Task<Result>
        - Pause() : Result
        - Resume() : Result
*/

/* Represents an active audio output sink session encapsulating underlying OS resources */
public interface IAudioSinkSession : IDisposable
{
    /* Indicates whether the underlying sink session is still open and accepting PCM frames */
    bool IsActive { get; }

    /* Writes a raw PCM slice into the sink and flushes the underlying transport */
    Task<Result> WriteAsync(ReadOnlyMemory<byte> pcmSlice, CancellationToken cancellationToken = default);

    /* Closes the input stream and waits for the sink to finish draining remaining audio samples */
    Task<Result> CompleteAsync(CancellationToken cancellationToken = default);

    /* Pauses active audio playback on this sink */
    Result Pause();

    /* Resumes paused audio playback on this sink */
    Result Resume();
}
