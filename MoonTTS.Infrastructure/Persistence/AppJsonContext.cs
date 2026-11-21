using System.Text.Json.Serialization;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Entities;
using MoonTTS.Infrastructure.Daemon.DTOs;

namespace MoonTTS.Infrastructure.Persistence;

/* Source generator context for AOT serialization */

/* Main configs */
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(UiStrings))]
[JsonSerializable(typeof(List<DictionaryEntry>))]
[JsonSerializable(typeof(ProfileDataStore))]
[JsonSerializable(typeof(List<HotkeyEntry>))]
[JsonSerializable(typeof(List<HistoryEntry>))]

/* Daemon IPC */
[JsonSerializable(typeof(DaemonRequest))]
[JsonSerializable(typeof(DaemonResponse))]
[JsonSerializable(typeof(AudioFormat))]

[JsonSourceGenerationOptions(WriteIndented = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, Converters = new[] { typeof(ModelIdJsonConverter) })]

/* Per-model preferences */
[JsonSerializable(typeof(EngineConfig))]
public partial class AppJsonContext : JsonSerializerContext
{
}