using System.Runtime.CompilerServices;
using System.Text.Json;
using MoonTTS.Core.Services;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Audio;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Entities;
using MoonTTS.Domain.Exceptions;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;
using MoonTTS.Engine.Protocol;

namespace MoonTTS.Infrastructure.Tts;

/* File Structure:
        - CreateAsync(
            EngineDescriptor engine,
            ModelMetadata model,
            SettingsManager settings,
            EngineVoiceStore voiceStore,
            CancellationToken cancellationToken) : Task<Result<ITtsEngine>>
        - ListVoices() : IReadOnlyList<VoiceInfo>
        - SynthesizeStreamAsync(string text, float? speedOverride, CancellationToken cancellationToken) : IAsyncEnumerable<ReadOnlyMemory<byte>>
        - SynthesizeAsync(string text, float? speedOverride, CancellationToken cancellationToken) : Task<Result<byte[]>>
        - StartWorkerAsync(CancellationToken cancellationToken) : Task
        - EnsureWorkerAsync(CancellationToken cancellationToken) : Task<EngineProcess>
        - LoadVoicesAsync(CancellationToken cancellationToken) : Task<IReadOnlyList<VoiceInfo>>
        - ResolveVoice() : string?
        - Dispose() : void
*/

/* Generic ITtsEngine backed by an out-of-process worker speaking the engine IPC protocol.
   Every engine-specific detail (audio format, voices, model loading) is obtained from the worker at runtime */
public sealed class ExternalTtsEngine : ITtsEngine
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LoadModelTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan VoicesTimeout = TimeSpan.FromSeconds(15);
    private const int MaxConsecutiveRestarts = 3;
    private const long RestartWindowMs = 15_000;

    private readonly EngineDescriptor _engine;
    private readonly ModelMetadata _model;
    private readonly SettingsManager _settings;
    private readonly EngineVoiceStore _voiceStore;

    /* Serializes worker (re)starts after a crash */
    private readonly SemaphoreSlim _workerLock = new(1, 1);
    private EngineProcess? _process;
    private EngineMetadataMessage _metadata = new();
    private IReadOnlyList<VoiceInfo> _voices = Array.Empty<VoiceInfo>();
    private int _consecutiveRestarts;
    private long _lastRestartTick;
    private bool _disposed;

    public AudioFormat OutputFormat => new(
        _metadata.AudioFormat.SampleRate,
        _metadata.AudioFormat.Channels,
        _metadata.AudioFormat.BitDepth);

    /* The worker started and loaded the model; a later crash is recovered on the next synthesis */
    public bool IsAvailable => !_disposed && _process != null;

    private EngineConfig CurrentConfig => _settings.GetEngineConfig(_model.Id);

    /* Stores dependencies; the worker is started by CreateAsync */
    private ExternalTtsEngine(EngineDescriptor engine, ModelMetadata model, SettingsManager settings, EngineVoiceStore voiceStore)
    {
        _engine = engine;
        _model = model;
        _settings = settings;
        _voiceStore = voiceStore;
    }

    /* Starts the worker, loads the model and resolves its voices (voices.json first, ListVoices otherwise) */
    public static async Task<Result<ITtsEngine>> CreateAsync(
        EngineDescriptor engine,
        ModelMetadata model,
        SettingsManager settings,
        EngineVoiceStore voiceStore,
        CancellationToken cancellationToken = default)
    {
        var instance = new ExternalTtsEngine(engine, model, settings, voiceStore);
        try
        {
            await instance.StartWorkerAsync(cancellationToken).ConfigureAwait(false);
            instance._voices = await instance.LoadVoicesAsync(cancellationToken).ConfigureAwait(false);
            return Result<ITtsEngine>.Success(instance);
        }
        catch (Exception ex) when (ex is EngineWorkerException or JsonException or InvalidDataException
                                       or IOException or System.ComponentModel.Win32Exception)
        {
            instance.Dispose();
            return Result<ITtsEngine>.Failure(ex.Message, ex);
        }
    }

    /* Gets and sets active voice */
    public string ActiveVoice
    {
        get => CurrentConfig.VoiceId;
        set
        {
            var config = CurrentConfig;
            config.VoiceId = value;
            _settings.SaveEngineConfig(_model.Id, config);
        }
    }

    /* Gets and sets speed */
    public float Speed
    {
        get => CurrentConfig.Speed;
        set
        {
            var config = CurrentConfig;
            config.Speed = value;
            _settings.SaveEngineConfig(_model.Id, config);
        }
    }

    /* Lists the voices of the loaded model */
    public IReadOnlyList<VoiceInfo> ListVoices() => _voices;

    /* Streams raw PCM chunks from the worker as they are rendered */
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeStreamAsync(
        string text,
        float? speedOverride = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !IsAvailable)
            yield break;

        string? voice = ResolveVoice();
        if (voice == null)
            yield break;

        var request = new SynthesizeRequest
        {
            Text = text,
            VoiceId = voice,
            Speed = speedOverride ?? CurrentConfig.Speed
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, EngineProtocolJsonContext.Default.SynthesizeRequest);

        var process = await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var chunk in process.StreamAsync(EngineOpcode.Synthesize, payload, cancellationToken)
                           .ConfigureAwait(false))
        {
            _consecutiveRestarts = 0;
            yield return chunk;
        }
    }

    /* Synthesizes text into a complete RIFF WAV byte array */
    public async Task<Result<byte[]>> SynthesizeAsync(string text, float? speedOverride = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result<byte[]>.Failure("Text to speak is empty.");

        /* Check if model is loaded */
        if (!IsAvailable)
            return Result<byte[]>.Failure(
                new InvalidEngineException(
                    "No TTS engine selected or model files are missing. Please select a model in settings before synthesizing."));

        /* If that succeeds, check that a voice can be used */
        if (ResolveVoice() == null)
            return Result<byte[]>.Failure(new InvalidVoiceException("No voice selected."));

        try
        {
            using var ms = new MemoryStream();
            await foreach (var chunk in SynthesizeStreamAsync(text, speedOverride, cancellationToken).ConfigureAwait(false))
            {
                ms.Write(chunk.Span);
            }
            return Result<byte[]>.Success(WavWriter.CreateWav(ms.ToArray(), OutputFormat));
        }
        catch (EngineWorkerException ex)
        {
            return Result<byte[]>.Failure(ex.Message, ex);
        }
    }

    /* Launches the worker, validates its metadata and loads the model files */
    private async Task StartWorkerAsync(CancellationToken cancellationToken)
    {
        var process = EngineProcess.Start(_engine);
        try
        {
            var metadata = await process.RequestJsonAsync(
                EngineOpcode.GetMetadata, ReadOnlyMemory<byte>.Empty, EngineOpcode.MetadataResponse,
                EngineProtocolJsonContext.Default.EngineMetadataMessage, MetadataTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(metadata.EngineId, _engine.Id, StringComparison.Ordinal))
                throw new EngineWorkerException(
                    $"Worker reports engine id '{metadata.EngineId}' but engine.json declares '{_engine.Id}'.");

            var format = metadata.AudioFormat;
            if (format.SampleRate <= 0 || format.Channels <= 0 || format.BitDepth <= 0 || format.BitDepth % 8 != 0)
                throw new EngineWorkerException(
                    $"Engine '{_engine.Id}' reported an invalid audio format " +
                    $"({format.SampleRate} Hz, {format.Channels} ch, {format.BitDepth} bit).");

            var loadRequest = new LoadModelRequest
            {
                ModelId = _model.Id.ModelName,
                ModelDirectory = _settings.GetModelDirectory(_model.Id),
                Files = _model.DownloadFiles.Keys.ToList()
            };
            await process.RequestAsync(
                EngineOpcode.LoadModel,
                JsonSerializer.SerializeToUtf8Bytes(loadRequest, EngineProtocolJsonContext.Default.LoadModelRequest),
                EngineOpcode.ModelLoaded, LoadModelTimeout, cancellationToken)
                .ConfigureAwait(false);

            _metadata = metadata;
            _process = process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /* Returns a live worker, restarting it with crash-loop protection if it crashed since the last request */
    private async Task<EngineProcess> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        var current = _process;
        if (current != null && !current.HasExited)
            return current;

        await _workerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process == null || _process.HasExited)
            {
                long now = Environment.TickCount64;
                if (now - _lastRestartTick > RestartWindowMs)
                    _consecutiveRestarts = 0;

                if (_consecutiveRestarts >= MaxConsecutiveRestarts)
                    throw new EngineWorkerException(
                        $"Engine worker '{_engine.Id}' crashed repeatedly ({MaxConsecutiveRestarts} times in {RestartWindowMs / 1000}s). Check logs for details.");

                _consecutiveRestarts++;
                _lastRestartTick = now;

                AppLogger.Warning("ExternalTtsEngine", $"Restarting crashed engine worker '{_engine.Id}' (attempt {_consecutiveRestarts}/{MaxConsecutiveRestarts}).");
                _process?.Dispose();
                _process = null;
                await StartWorkerAsync(cancellationToken).ConfigureAwait(false);
            }
            return _process!;
        }
        finally
        {
            _workerLock.Release();
        }
    }

    /* Uses the voices.json entry of the model, or asks the worker and writes the entry for next time */
    private async Task<IReadOnlyList<VoiceInfo>> LoadVoicesAsync(CancellationToken cancellationToken)
    {
        var cached = _voiceStore.TryGetVoices(_engine, _model.Id.ModelName);
        if (cached != null)
            return cached;

        var response = await _process!.RequestJsonAsync(
            EngineOpcode.ListVoices, ReadOnlyMemory<byte>.Empty, EngineOpcode.VoicesResponse,
            EngineProtocolJsonContext.Default.VoicesResponse, VoicesTimeout, cancellationToken)
            .ConfigureAwait(false);

        var voices = response.Voices
            .Where(v => !string.IsNullOrWhiteSpace(v.Id))
            .Select(v => new VoiceInfo(v.Id, v.Name, v.Language))
            .ToList();

        if (voices.Count > 0)
        {
            var saveResult = _voiceStore.SaveVoices(_engine, _model.Id.ModelName, voices);
            if (saveResult.IsFailure)
                Console.Error.WriteLine($"[ExternalTtsEngine] {saveResult.ErrorMessage}");
        }
        return voices;
    }

    /* The configured voice when the model offers it, otherwise the model's first voice */
    private string? ResolveVoice()
    {
        string configured = CurrentConfig.VoiceId;
        if (_voices.Any(v => v.Id == configured))
            return configured;
        return _voices.Count > 0 ? _voices[0].Id : null;
    }

    /* Shuts the worker down and releases its channel */
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _process?.Dispose();
        _process = null;
        _workerLock.Dispose();
    }
}
