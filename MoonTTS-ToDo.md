### Candidate 1: The Unified Audio Pipeline & Zero-Copy Streaming Engine [COMPLETED]

  • Cluster: KokoroOnnxEngine, SpeechService, WavCombiner, LinuxAudioStreamer, LinuxProcessRunner
  • Why they're coupled: They form an end-to-end audio pipeline with leaky boundary abstractions. PCM data is artificially wrapped in WAV headers only to be unpacked two layers later.
  • The "Code Judo" Move: Delete the artificial WAV wrapping in the hot path. Let the synthesis engine emit strongly-typed PCM audio buffers (ReadOnlyMemory<byte> or a lightweight struct AudioBuffer(Memory, SampleRate, Channels)). Pass raw PCM directly to the audio player with SIMD-vectorized volume scaling, and only attach a RIFF/WAV header at the persistence/export boundary.
  • Dependency category: In-process & Local-substitutable (audio players can be substituted with an in-memory stream sink).
  • Test impact: Deletes fragile WAV header-parsing tests and combiners. Boundary tests simply assert that given input text, the emitted PCM byte stream matches expected duration and sample characteristics.
  • Implementation Notes:
    - Zero-copy streaming implemented with `ReadOnlyMemory<byte>` across `ISpeechPipeline` and `IAudioPlayer`.
    - SIMD volume scaling and boundary de-clicking active in `SimdAudioProcessor`.
    - Dynamic WAV format detection extracted to `FormatReader` (eliminating `KokoroDefault` hardcoded presets).
    - Legacy untyped `byte[]` methods (`PlayAsync`, `PlayStreamAsync`) removed from `IAudioPlayer` and platform audio players in favor of `StreamPcmAsync` and `PlayPcmAsync`.

### Candidate 2: Unified Configuration & State Store (Consolidating the 5 Managers) [COMPLETED]

  • Cluster: SettingsManager, JsonSettingsRepository, DictionaryManager, ProfileManager, HotkeyManager
  • Why they're coupled: All five services duplicate identical responsibilities: JSON file read/write, path resolution, in-memory list caching, and UI change notification events. This fragmentation caused the unassigned _hotkeysDir bug and thread-safety oversights.
  • The "Code Judo" Move: Collapse the fragmented managers into a unified, thread-safe Application State & Persistence Store (IAppStore). Use atomic file writing and System. Text.Json source generation for reflection-free, high-speed serialization. Fixes ProfileManager and DictionaryManager bugs in the process.
  • Dependency category: Local-substitutable (tested using an in-memory file system or mock store).
  • Test impact: Replaces 5 separate file-parsing test sets with one cohesive state- transition and persistence boundary test suite.
  • Implementation Notes:
    - Unified store created under `IConfigurationRepository` / `JsonConfigurationRepository`.
    - System.Text.Json AOT source generation implemented via `AppJsonContext`.
    - Atomic file writes implemented via `.tmp` file creation and atomic `File.Move(overwrite: true)`.
    - Granular change events wired (`AppConfigChanged`, `EngineConfigChanged`, `DictionaryChanged`, `HotkeysChanged`, `ProfilesChanged`, `HistoryChanged`).
    - Managers (`SettingsManager`, `DictionaryManager`, `ProfileManager`, `HotkeyManager`, `DownloadManager`) refactored to act as focused domain service facades over `IConfigurationRepository`.
    - `ISettingsRepository` and `JsonSettingsRepository` deleted.

### Candidate 3: Binary-Framed IPC Protocol for the Background Daemon [COMPLETED]

  • Cluster: DaemonServer, DaemonClient, DaemonRequest, DaemonResponse, DaemonIpcProtocol
  • Why they're coupled: The CLI and Daemon use a full JSON object protocol over named pipes that Base64-encodes binary audio data.
  • The "Code Judo" Move: Replace the Base64 JSON pipe transmission with a simple framed binary protocol ([1-byte opcode][4-byte payload length][payload]). Control messages remain JSON; audio responses stream raw PCM/WAV directly over the pipe. Enables instant, low-latency audio playback in the CLI as the daemon generates chunks.
  • Dependency category: Remote but owned (Ports & Adapters).
  • Test impact: Replaces external pipe tests with fast in-memory duplex stream tests verifying protocol framing, cancellation, and streaming behavior.
  
### Candidate 4: Audio Sink Abstraction & Subprocess Decoupling [COMPLETED]

  • Cluster: IAudioPlayer, LinuxAudioPlayer, LinuxAudioStreamer, LinuxProcessRunner, LinuxDeviceEnumerator, LinuxVirtualMicService
  • Why they're coupled: LinuxAudioPlayer manages sub-processes (paplay, pw-play), sends raw POSIX signals (SIGSTOP/SIGCONT), handles dual-output multiplexing (primary + monitor), and manages process lifecycle with low-level locks.
  • The "Code Judo" Move: Deepen IAudioPlayer behind a resilient audio sink abstraction. Caller passes audio streams and target sink IDs without knowing about process IDs, pipehandles, or fallback chains. Encapsulates subprocess lifecycle, eliminates process leakrisks on cancellation, and centralizes error recovery.
  • Dependency category: Ports & Adapters.
  • Test impact: Replaces complex subprocess mocking with an audio sink port adapter testsuite.
  • Implementation Notes:
    - Introduced `IAudioSinkSession`, `ILinuxAudioBackend`, and `ILinuxCommandRunner` ports in `MoonTTS.Infrastructure/Audio/Linux/`.
    - Encapsulated `Process`, `StandardInput` stream, `SIGSTOP`/`SIGCONT` POSIX signaling, and deterministic process-tree cleanup inside `LinuxProcessRunner.LinuxProcessSinkSession`.
    - Refactored `LinuxAudioStreamer` (`ILinuxAudioBackend`) to manage device discovery (`LinuxVirtualMicService` + `LinuxDeviceEnumerator`), dynamic sample format resolution from `AudioFormat`, and the `paplay` -> `pw-play` fallback chain.
    - Decoupled `LinuxAudioPlayer` from `System.Diagnostics.Process` and OS signaling; it now orchestrates dual-sink multiplexing, dynamic 50ms block-aligned slicing, SIMD volume scaling, and per-sink error isolation over `IAudioSinkSession`.
    - Fixed `LinuxDeviceEnumerator.ParseWpctlSinks` regex capture groups and converted it to an AOT-compiled `[GeneratedRegex]`.
