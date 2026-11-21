using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;

namespace MoonTTS.Domain.Interfaces;

/* Unified speech pipeline interface for text splitting, dictionary replacements, streaming playback and history */
public interface ISpeechPipeline
{
    /* Fired whenever IsSpeaking or IsPaused changes */
    event Action? PlaybackStateChanged;

    /* Synthesizes and streams speech using default accessibility settings */
    Task<Result<SpeechResult>> SpeakAsync(
        string text, 
        CancellationToken cancellationToken = default);

    /* Synthesizes and streams speech with custom speech options overrides */
    Task<Result<SpeechResult>> SpeakAsync(
        string text, 
        SpeechOptions options, 
        CancellationToken cancellationToken = default);

    /* Plays a pre-rendered WAV file through the active pipeline session so HUD controls stay unified */
    Task<Result> PlayWavFileAsync(
        string wavPath,
        CancellationToken cancellationToken = default);

    /* Plays a pre-rendered WAV file through the active pipeline session and reports its duration and PCM byte count */
    Task<Result<(TimeSpan Duration, int PcmBytes)>> PlayWavFileWithInfoAsync(
        string wavPath,
        CancellationToken cancellationToken = default);

    /* Pauses the active speech playback */
    void Pause();

    /* Resumes paused speech playback */
    void Resume();

    /* Immediately cancels synthesis and stops speech playback */
    void Stop();

    /* Indicates whether speech synthesis or playback is currently in progress */
    bool IsSpeaking { get; }

    /* Indicates whether speech playback is currently paused */
    bool IsPaused { get; }
}