# Moon-TTS: Out-of-Process Engine Architecture & IPC Specification

> **Document Version:** 1.1.0  
> **Status:** Implemented (host: `MoonTTS.Engine.Protocol` + `MoonTTS.Infrastructure/Tts`, reference worker: `Engines/Kokoro`)  
> **Domain:** Local Accessibility & Speech Synthesis Platform  

---

## 1. Architectural Vision & Core Goals

### 1.1 The Problem (before this architecture)
Previously, `MoonTTS.Infrastructure` directly linked speech synthesis engines (specifically `KokoroSharp` and its G2P dependency `MisakiSharp`) into the main application assembly. 
- In **Native AOT**, this forces all static dictionaries (such as `MisakiSharp`'s 67 MB embedded CMUdict) to be permanently baked into the `.rodata` segment of the primary `MoonTTS.Avalonia` and `mtts` binaries.
- If a user only intends to use a lightweight cloud voice, an OS-native voice (SAPI/SpeechDispatcher), or an alternative local model (e.g., Piper), they are still forced to download and store 70 MB of unused neural dependencies.
- Third-party contributors wanting to add new engines (e.g., in Python, Rust, C++, or Go) cannot do so without modifying the C# source code and ensuring 100% Native AOT compatibility.

### 1.2 The Out-of-Process Solution
Decouple Moon-TTS into an **ultra-lean core host (~20 MB)** and **modular, out-of-process engine workers**:

```
┌───────────────────────────────────────────────────────────────┐
│                    Moon-TTS Host Core (~20 MB)                │
│  - Avalonia GUI & CLI Frontends                               │
│  - Global OS Shortcuts (Wayland / X11 / Win / macOS)          │
│  - Audio Routing & Playback (pw-play / aplay / NAudio / Core) │
│  - Text History, Replacement Dictionaries, Hotkeys            │
└───────────────────────────────┬───────────────────────────────┘
                                │
                  Standard Binary IPC Stream
             (stdin/stdout pipes or Unix Sockets)
                                │
         ┌──────────────────────┼──────────────────────┐
         ▼                      ▼                      ▼
┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐
│  Kokoro Engine   │  │   Piper Engine   │  │  Custom Engine   │
│  (Worker Proc)   │  │   (Worker Proc)  │  │ (Python/Rust/C++)│
│  - KokoroSharp   │  │  - piper binary  │  │  - Any library   │
│  - Misaki Dict   │  │  - onnxruntime   │  │  - Cloud API     │
└──────────────────┘  └──────────────────┘  └──────────────────┘
```

### 1.3 Key Architectural Benefits
1. **Core Binary Footprint:** Drops the main GUI and CLI binaries from ~96 MB down to **~20 MB**.
2. **Language Independence:** Community contributors can write engine plugins in **any language** (Python, Rust, C++, C#, Go).
3. **Zero Native AOT Constraints on Plugins:** Plugins do not need to satisfy .NET Native AOT compiler trimming or reflection limits.
4. **Crash Isolation & Sandboxing:** If a neural engine runs out of VRAM, corrupts memory, or throws a segfault, **Moon-TTS never crashes**. The host detects process termination and can gracefully restart it.
5. **Drop-in Extensibility:** Users install new engines simply by placing a folder into `~/.local/share/Moon-TTS/engines/<Name>/`.

---

## 2. Binary IPC Framing Protocol Specification

Communication between Moon-TTS and an engine worker runs over standard input/output streams (`stdin` / `stdout`) or local Unix domain sockets. To ensure maximum throughput with zero-copy audio, the protocol uses strict binary framing.

### 2.1 Packet Structure

Every frame sent between Host and Worker follows a fixed 8-byte header:

```text
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|       Magic (0x4D 0x54)       |    Opcode     |   Flags (1B)  |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                        Payload Length                         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
+                       Payload (N bytes)                       +
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

- **Magic (2 bytes):** `0x4D 0x54` (`"MT"` in ASCII) — verifies frame alignment.
- **Opcode (1 byte):** Defines command or response type.
- **Flags (1 byte):** Reserved for stream flags (e.g., `0x01` = Compressed payload).
- **Payload Length (4 bytes):** 32-bit unsigned integer (big-endian), size of following payload in bytes.
- **Payload ($N$ bytes):** Binary body (raw PCM audio, UTF-8 JSON, or control parameters).

---

### 2.2 Opcode Definitions

| Opcode | Name | Direction | Payload Format | Description |
| :---: | :--- | :---: | :--- | :--- |
| `0x01` | `Ping` | Host $\to$ Worker | None | Liveness heartbeat check. |
| `0x02` | `Pong` | Worker $\to$ Host | None | Heartbeat response. |
| `0x10` | `GetMetadata` | Host $\to$ Worker | None | Requests engine identity and the PCM format it emits. |
| `0x11` | `MetadataResponse` | Worker $\to$ Host | JSON (UTF-8) | Engine capabilities and audio format. |
| `0x12` | `ListVoices` | Host $\to$ Worker | None | Requests the voices of the loaded model. |
| `0x13` | `VoicesResponse` | Worker $\to$ Host | JSON (UTF-8) | Array of voice descriptors. |
| `0x14` | `LoadModel` | Host $\to$ Worker | JSON (UTF-8) | Loads a model from the host's model directory. |
| `0x15` | `ModelLoaded` | Worker $\to$ Host | None | The model is ready for synthesis. |
| `0x20` | `Synthesize` | Host $\to$ Worker | JSON (UTF-8) | Speech request (text, voice ID, speed, pitch). |
| `0x21` | `AudioChunk` | Worker $\to$ Host | Binary PCM | Streamed raw PCM audio chunk in the format declared by the metadata. |
| `0x22` | `EndOfStream` | Worker $\to$ Host | None | Indicates completion of the current synthesis stream. |
| `0x23` | `Cancel` | Host $\to$ Worker | None | Aborts the current synthesis job. No direct reply. |
| `0xFE` | `Error` | Worker $\to$ Host | JSON (UTF-8) | Error payload with code and human-readable message. |
| `0xFF` | `Shutdown` | Host $\to$ Worker | None | Graceful termination signal. |

**Exchange rules**
- The host runs one exchange at a time. Every request is answered by exactly one terminal frame: its reply opcode, `EndOfStream` for `Synthesize`, or `Error`.
- `Synthesize` is answered by zero or more `AudioChunk` frames followed by `EndOfStream` (or `Error`).
- After `Cancel` the worker must still finish the active stream with `EndOfStream` (or `Error`). A `Cancel` that arrives when no synthesis is running is ignored.
- Workers must exit when they receive `Shutdown` or when stdin reaches end of file (the host closes stdin when it disposes an engine or exits).
- stdout carries only protocol frames. Diagnostics go to stderr, which the host forwards to its log.
- Workers should emit click-free audio (e.g. short fades at sentence boundaries); the host plays chunks as-is.

---

### 2.3 Payload Formats

#### `0x11: MetadataResponse` (JSON)
```json
{
  "engineId": "kokoro-onnx",
  "engineName": "Kokoro ONNX Neural Synthesizer",
  "version": "1.0.0",
  "author": "Moon-TTS Team",
  "audioFormat": {
    "sampleRate": 24000,
    "channels": 1,
    "bitDepth": 16,
    "encoding": "PCM_SIGNED"
  },
  "supportsStreaming": true,
  "supportsPitchAdjustment": false,
  "supportsSpeedAdjustment": true
}
```

#### `0x14: LoadModel` (JSON)
```json
{
  "modelId": "kokoro-v1.0-fp16",
  "modelDirectory": "/home/user/.local/share/Moon-TTS/models/kokoro",
  "files": ["kokoro-v1.0-fp16.onnx"]
}
```
`files` are the keys of the model's `downloadFiles` in `engine.json`, already downloaded into `modelDirectory`.

#### `0x13: VoicesResponse` (JSON)
```json
{
  "voices": [
    { "id": "af_heart", "name": "Heart", "language": "en-US" },
    { "id": "bm_george", "name": "George", "language": "en-GB" }
  ]
}
```

#### `0xFE: Error` (JSON)
```json
{ "code": "model_not_found", "message": "No .onnx file was found." }
```

#### `0x20: Synthesize` (JSON)
```json
{
  "text": "Hello world, this is accessibility speech synthesis.",
  "voiceId": "af_heart",
  "speed": 1.0,
  "pitch": 1.0
}
```

#### `0x21: AudioChunk` (Binary)
- **Header:** Standard 8-byte packet header (`Opcode = 0x21`).
- **Payload:** Raw, uncompressed PCM audio bytes (little-endian signed samples, in the format declared by `MetadataResponse`).
- **Latency Rule:** The worker emits chunks as soon as a phonetic clause or sentence is rendered (typically 100ms–250ms of audio per chunk) to achieve instant playback start.

---

## 3. Plugin Discovery & Manifest Format

Engines are discovered dynamically at startup by scanning the platform engines directory:
- **Linux:** `$XDG_DATA_HOME/Moon-TTS/engines/` (default `~/.local/share/Moon-TTS/engines/`)
- **Windows:** `%AppData%/Moon-TTS/engines/`
- **macOS:** `~/Library/Application Support/Moon-TTS/engines/`

Every sub folder containing an `engine.json` is an engine. The folder name is free; the engine is identified by the manifest `id`. Model files are not stored in the engine folder: the host downloads them into `<models directory>/<engineId>/` (honouring the user's custom models directory) and passes that path in `LoadModel`.

### 3.1 Folder Structure
```text
~/.local/share/Moon-TTS/engines/
├── Piper/
│   ├── engine.json            (Plugin manifest)
│   ├── voices.json            (Optional, generated by the host when missing)
│   └── bin/
│       └── piper-worker       (Executable)
└── Kokoro/
    ├── engine.json
    ├── voices.json
    ├── kokoro-worker
    └── voices/                (Engine-private assets)
```

### 3.2 Manifest Schema (`engine.json`)
```json
{
  "id": "piper-tts",
  "displayName": "Piper Neural Synthesizer",
  "description": "Ultra-fast local neural text-to-speech engine running on ONNX.",
  "version": "1.2.0",
  "executable": "bin/piper-worker",
  "models": [
    {
      "id": "en_US-lessac-medium",
      "displayName": "Lessac (US English, Medium)",
      "languages": ["English"],
      "downloadFiles": {
        "en_US-lessac-medium.onnx": "https://example.org/en_US-lessac-medium.onnx",
        "en_US-lessac-medium.onnx.json": "https://example.org/en_US-lessac-medium.onnx.json"
      }
    }
  ]
}
```
- `id` must not contain `/`: models are addressed as `<engineId>/<modelId>` (e.g. `piper-tts/en_US-lessac-medium`).
- `executable` is relative to the engine folder; on Windows `.exe` is appended when the file without extension does not exist. Script workers (`#!/usr/bin/env python3`) work on POSIX systems.
- `downloadFiles` maps local file names to URLs. A model with no files (e.g. a cloud or OS voice) is always considered downloaded.

### 3.3 Voices (`voices.json`)
Voices are kept out of `engine.json` so engine authors never have to map them by hand. `voices.json` sits next to the manifest and lists voices per model id:
```json
{
  "models": {
    "en_US-lessac-medium": [
      { "id": "lessac", "name": "Lessac", "language": "en-US" }
    ]
  }
}
```
When a model has no entry, the host sends `ListVoices` after `LoadModel` and writes the reply into `voices.json` (atomic `.tmp` + rename). Authors can ship or edit the file to override names and languages.

---

## 4. Host Implementation (`MoonTTS.Engine.Protocol` / `MoonTTS.Infrastructure/Tts`)

| Component | Responsibility |
| :--- | :--- |
| `EngineIpcProtocol`, `EngineOpcode`, DTOs | Shared framing and JSON payloads (`MoonTTS.Engine.Protocol`, AOT source-generated JSON). |
| `EngineCatalog : IModelRegistry` | Scans the engines directory and exposes engines and models. |
| `EngineVoiceStore` | Reads / writes `voices.json`. |
| `EngineProcess` | Owns the worker process: one exchange at a time, streaming with cancel + drain, kills unresponsive workers, forwards stderr. |
| `ExternalTtsEngine : ITtsEngine` | Handshake (`GetMetadata` → `LoadModel` → voices), synthesis, per-model voice/speed, restart after a crash. |
| `TtsEngineFactory : ITtsEngineFactory` | Builds an `ExternalTtsEngine` for any installed model (`EmptyTtsEngine` when unselected). |

Startup sequence for one model:
1. Spawn `executable` with the engine folder as working directory and stdin/stdout/stderr redirected.
2. `GetMetadata` → `MetadataResponse`; the `engineId` must match the manifest and the audio format must be valid.
3. `LoadModel` → `ModelLoaded`.
4. Voices from `voices.json`, otherwise `ListVoices` → `VoicesResponse` (then persisted).

---

## 5. Contributor Reference Implementation (Python Template)

A third-party developer can create an engine in about 50 lines of Python using standard library utilities:

```python
#!/usr/bin/env python3
import sys
import struct
import json

MAGIC = b'\x4D\x54'
OP_GET_METADATA, OP_METADATA = 0x10, 0x11
OP_LIST_VOICES, OP_VOICES = 0x12, 0x13
OP_LOAD_MODEL, OP_MODEL_LOADED = 0x14, 0x15
OP_SYNTHESIZE, OP_AUDIO_CHUNK, OP_END_OF_STREAM = 0x20, 0x21, 0x22
OP_ERROR, OP_SHUTDOWN = 0xFE, 0xFF

def send_frame(opcode, payload=b''):
    header = struct.pack('>2sBBI', MAGIC, opcode, 0, len(payload))
    sys.stdout.buffer.write(header + payload)
    sys.stdout.buffer.flush()

def send_json(opcode, obj):
    send_frame(opcode, json.dumps(obj).encode('utf-8'))

def read_frame():
    header = sys.stdin.buffer.read(8)
    if len(header) < 8:
        return None, None
    magic, opcode, flags, length = struct.unpack('>2sBBI', header)
    if magic != MAGIC:
        raise ValueError("Invalid magic bytes")
    payload = sys.stdin.buffer.read(length) if length > 0 else b''
    return opcode, payload

def main():
    while True:
        opcode, payload = read_frame()
        if opcode is None or opcode == OP_SHUTDOWN:
            break

        if opcode == OP_GET_METADATA:
            send_json(OP_METADATA, {
                "engineId": "python-tts",
                "engineName": "Python TTS",
                "version": "1.0.0",
                "audioFormat": {"sampleRate": 22050, "channels": 1, "bitDepth": 16}
            })
        elif opcode == OP_LOAD_MODEL:
            req = json.loads(payload)
            # load req["files"] from req["modelDirectory"]
            send_frame(OP_MODEL_LOADED)
        elif opcode == OP_LIST_VOICES:
            send_json(OP_VOICES, {"voices": [{"id": "default", "name": "Default", "language": "en-US"}]})
        elif opcode == OP_SYNTHESIZE:
            req = json.loads(payload)
            # for pcm_chunk in my_tts_model.stream(req["text"], req["voiceId"], req["speed"]):
            #     send_frame(OP_AUDIO_CHUNK, pcm_chunk)
            send_frame(OP_END_OF_STREAM)
        else:
            send_json(OP_ERROR, {"code": "unsupported_opcode", "message": hex(opcode)})

if __name__ == '__main__':
    main()
```

A synchronous worker like this one cannot honour `Cancel` mid-sentence; the host still drains its stream. Workers that need prompt cancellation read stdin on a separate thread (see `Engines/Kokoro/KokoroWorker.cs`).

---

## 6. Implementation Status

1. **Phase 1: Protocol Definitions** — done: `MoonTTS.Engine.Protocol` (framing, opcodes, payload DTOs, manifests).
2. **Phase 2: External Engine Adapter** — done: `ExternalTtsEngine` + `EngineProcess`, with restart on the next request after a crash.
3. **Phase 3: Separate Kokoro Worker** — done: `Engines/Kokoro` (`kokoro-worker`).
4. **Phase 4: Decouple Core Dependencies** — done: `KokoroSharp` and `OnnxRuntime` removed from the host; no engine-specific types remain in Domain, Core, Infrastructure, Avalonia or CLI.
5. **Phase 5: Plugin Discovery** — done: `EngineCatalog` scans the engines directory. Downloading worker bundles themselves (not just model files) remains future work.
