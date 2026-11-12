using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Domain.Entities;

public class HistoryEntry
{
    public required Guid Id { get; init;  }
    public required string Text { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string WavFileName { get; init; }

    /* Model and voice that generated the audio. Defaults keep older history.json files readable */
    public ModelId Model { get; init; } = ModelId.Unselected;
    public string Voice { get; init; } = string.Empty;
}
