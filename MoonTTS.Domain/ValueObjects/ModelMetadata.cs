namespace MoonTTS.Domain.ValueObjects;

/* File Structure:
        - Unselected : ModelMetadata
        - CreateSeparator(string engineDisplayName) : ModelMetadata
        - IsUnselected : bool
        - IsSelectable : bool
        - IsUnavailable : bool
        - ToString() : string
*/

/* Describes a downloadable model of an installed engine.
   The keys of DownloadFiles are the local file names, its values the remote URLs */
public record ModelMetadata(
    ModelId Id,
    string DisplayName,
    Dictionary<string, string> DownloadFiles,
    int? Size = null,
    int? RamUsage = null,
    string? CpuUsage = null,
    IReadOnlyList<string>? Languages = null,
    bool IsSeparator = false,
    bool IsDownloaded = true
)
{
    /* Null-object entry representing "no model selected", so selectors always have a concrete item to show */
    public static ModelMetadata Unselected { get; } = new(ModelId.Unselected, "Unselected", new Dictionary<string, string>());

    /* Creates a non-selectable separator header entry for grouping models by engine in UI selectors */
    public static ModelMetadata CreateSeparator(string engineDisplayName) =>
        new(ModelId.Unselected, engineDisplayName, new Dictionary<string, string>(), IsSeparator: true, IsDownloaded: false);

    /* True when this entry stands for "no model selected" */
    public bool IsUnselected => Id.IsUnselected && !IsSeparator;

    /* True when this entry can be chosen in a model selector dropdown */
    public bool IsSelectable => !IsSeparator && (IsUnselected || IsDownloaded);

    /* True when this entry is a real model that is not downloaded yet */
    public bool IsUnavailable => !IsSeparator && !IsUnselected && !IsDownloaded;

    /* Keeps the display name visible wherever the model is rendered through ToString */
    public override string ToString() => DisplayName;
}
