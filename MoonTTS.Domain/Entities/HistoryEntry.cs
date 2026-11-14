using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Domain.Entities;

public class HistoryEntry
{
    public required Guid Id { get; init;  }
    public required string Text { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string WavFileName { get; init; }

    /* Model, voice, processed text and speed that produced the audio. Defaults keep older history.json files readable */
    public ModelId Model { get; init; } = ModelId.Unselected;
    public string Voice { get; init; } = string.Empty;
    public string ProcessedText { get; init; } = string.Empty;
    public float Speed { get; init; } = 1.0f;
}
