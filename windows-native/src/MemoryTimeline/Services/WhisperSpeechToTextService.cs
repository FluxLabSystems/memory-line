using System.Text;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using Whisper.net;
using Whisper.net.Ggml;

namespace MemoryTimeline.Services;

/// <summary>
/// File-based speech-to-text service backed by Whisper.net (local ggml Whisper model).
/// <para>
/// The recorder produces 16 kHz / 16-bit / mono WAV files, and capture ingestion
/// normalizes every other source to the same layout, which is exactly the input
/// Whisper.net expects, so recordings can be streamed straight into the processor.
/// </para>
/// <para>
/// The model and language come from <see cref="SettingKeys.WhisperModel"/> and
/// <see cref="SettingKeys.WhisperLanguage"/>, re-read on every transcription so a
/// Settings change applies to the next recording. The chosen ggml model is
/// downloaded on first use into %LocalAppData%\MemoryTimeline\Models\ggml-{name}.bin
/// (no package identity required — the app runs unpackaged). The expensive
/// <see cref="WhisperFactory"/> (model load) is created once per model name and
/// cached; switching models in Settings disposes the old factory and loads the new
/// one on the next transcription. A fresh, cheap <see cref="WhisperProcessor"/> is
/// built per transcription, so this service is safe to register as a DI singleton.
/// </para>
/// </summary>
public class WhisperSpeechToTextService : ISpeechToTextService, IDisposable
{
    /// <summary>The model used when the setting is absent, blank or not a known ggml name.</summary>
    public const string DefaultModel = "base";

    /// <summary>The language used when the setting is absent or blank: detect per recording.</summary>
    public const string DefaultLanguage = "auto";

    /// <summary>
    /// The ggml model names Whisper.net 1.5.0 can download
    /// (<see cref="WhisperGgmlDownloader.GetGgmlModelAsync"/>), keyed by the
    /// whisper.cpp spelling the setting stores. The English-only variants are
    /// smaller and faster for English speech; the multilingual ones detect the
    /// language when the language setting is "auto".
    /// </summary>
    private static readonly IReadOnlyDictionary<string, GgmlType> KnownModels =
        new Dictionary<string, GgmlType>(StringComparer.OrdinalIgnoreCase)
        {
            ["tiny"] = GgmlType.Tiny,
            ["tiny.en"] = GgmlType.TinyEn,
            ["base"] = GgmlType.Base,
            ["base.en"] = GgmlType.BaseEn,
            ["small"] = GgmlType.Small,
            ["small.en"] = GgmlType.SmallEn,
            ["medium"] = GgmlType.Medium,
            ["medium.en"] = GgmlType.MediumEn,
            ["large-v1"] = GgmlType.LargeV1,
            ["large-v2"] = GgmlType.LargeV2,
            ["large-v3"] = GgmlType.LargeV3,
        };

    /// <summary>The model names a Settings UI may offer, in size order.</summary>
    public static IReadOnlyList<string> SupportedModelNames { get; } = new[]
    {
        "tiny", "tiny.en", "base", "base.en", "small", "small.en", "medium", "medium.en",
        "large-v1", "large-v2", "large-v3",
    };

    private readonly ILogger<WhisperSpeechToTextService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly string _modelsDirectory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private WhisperFactory? _factory;
    private string? _factoryModelName;
    private bool _disposed;

    public string EngineName => "Whisper (local, Whisper.net)";
    public bool SupportsStreaming => false;
    public bool RequiresInternet => false; // offline after the one-time model download

    public WhisperSpeechToTextService(
        ILogger<WhisperSpeechToTextService> logger,
        ISettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _modelsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MemoryTimeline",
            "Models");
    }

    /// <summary>
    /// Transcribes an audio file to text.
    /// </summary>
    public Task<TranscriptionResult> TranscribeAsync(string audioFilePath)
    {
        return TranscribeAsync(audioFilePath, null);
    }

    /// <summary>
    /// Transcribes an audio file to text with progress reporting.
    /// Never throws — all failures are returned as an unsuccessful
    /// <see cref="TranscriptionResult"/> with a user-readable message.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(string audioFilePath, IProgress<double>? progress)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new TranscriptionResult();

        try
        {
            progress?.Report(0.0);

            if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
            {
                result.Success = false;
                result.ErrorMessage = $"Audio file not found: {audioFilePath}";
                result.ProcessingDurationSeconds = stopwatch.Elapsed.TotalSeconds;
                _logger.LogWarning("Whisper transcription requested for missing file: {FilePath}", audioFilePath);
                return result;
            }

            // Whisper.net only parses 16 kHz mono 16-bit PCM WAV input. Ingestion
            // normalizes every capture to that layout; anything else reaching
            // here (a normalizer that could not convert, a file edited by hand)
            // is rejected up front instead of being fed to the decoder as noise.
            var header = WavHeader.TryRead(audioFilePath);
            if (header is not { IsTranscriptionReady: true })
            {
                result.Success = false;
                result.ErrorMessage = header == null
                    ? "Unsupported audio format — only 16 kHz mono WAV recordings can be transcribed, and this file could not be converted"
                    : $"Unsupported WAV layout ({header.SampleRate} Hz, {header.Channels} channel(s), {header.BitsPerSample}-bit) — 16 kHz mono 16-bit PCM is required";
                result.ProcessingDurationSeconds = stopwatch.Elapsed.TotalSeconds;
                _logger.LogWarning("Whisper transcription rejected non-transcribable input: {FilePath}", audioFilePath);
                return result;
            }

            var (modelName, ggmlType) = await ReadModelSettingAsync();
            var language = await ReadLanguageSettingAsync();

            // Ensure the chosen model is present and loaded (one-time cost per model).
            var factory = await GetOrCreateFactoryAsync(modelName, ggmlType);
            progress?.Report(0.1);

            _logger.LogDebug(
                "Starting Whisper transcription of {FilePath} (model {Model}, language {Language})",
                audioFilePath, modelName, language);

            // The factory is cached, but processors are cheap and NOT reused across
            // calls, so concurrent transcriptions each get their own processor.
            // "auto" is whisper.cpp's own spelling for language detection.
            await using var processor = factory.CreateBuilder()
                .WithLanguage(language)
                .Build();

            await using var fileStream = File.OpenRead(audioFilePath);

            // Estimate the audio duration from the file size (16 kHz * 16-bit * mono
            // = 32,000 bytes/sec after the 44-byte WAV header) to drive progress.
            var estimatedAudioSeconds = Math.Max((fileStream.Length - 44) / 32000.0, 0.1);

            var textBuilder = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(fileStream, CancellationToken.None))
            {
                textBuilder.Append(segment.Text);

                if (progress != null)
                {
                    var fraction = 0.1 + 0.85 * Math.Min(segment.End.TotalSeconds / estimatedAudioSeconds, 1.0);
                    progress.Report(Math.Min(fraction, 0.95));
                }
            }

            stopwatch.Stop();

            result.Text = textBuilder.ToString().Trim();
            result.Success = true;
            result.ProcessingDurationSeconds = stopwatch.Elapsed.TotalSeconds;
            progress?.Report(1.0);

            _logger.LogDebug(
                "Whisper transcription completed in {Duration:F1}s, {Length} characters",
                result.ProcessingDurationSeconds, result.Text.Length);

            return result;
        }
        catch (ModelDownloadFailedException ex)
        {
            stopwatch.Stop();
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.ProcessingDurationSeconds = stopwatch.Elapsed.TotalSeconds;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Whisper transcription of {FilePath}", audioFilePath);

            stopwatch.Stop();
            result.Success = false;
            result.ErrorMessage = $"Transcription failed: {ex.Message}";
            result.ProcessingDurationSeconds = stopwatch.Elapsed.TotalSeconds;
            return result;
        }
    }

    /// <summary>
    /// The configured model, validated against the names Whisper.net 1.5.0 can
    /// download. An unknown name falls back to <see cref="DefaultModel"/> with a
    /// logged warning rather than failing the transcription: a typo in Settings
    /// should cost accuracy, not the recording.
    /// </summary>
    private async Task<(string Name, GgmlType Type)> ReadModelSettingAsync()
    {
        var configured = (await _settingsService.GetSettingAsync<string>(SettingKeys.WhisperModel, DefaultModel) ?? string.Empty)
            .Trim();

        if (string.IsNullOrEmpty(configured))
        {
            return (DefaultModel, KnownModels[DefaultModel]);
        }

        if (KnownModels.TryGetValue(configured, out var type))
        {
            return (configured.ToLowerInvariant(), type);
        }

        _logger.LogWarning(
            "Unknown Whisper model '{Model}' in settings; falling back to '{Default}'. Known models: {Known}",
            configured, DefaultModel, string.Join(", ", SupportedModelNames));
        return (DefaultModel, KnownModels[DefaultModel]);
    }

    /// <summary>
    /// The configured language: "auto" (detect), or an ISO-639-1 code passed to
    /// whisper.cpp as-is (it rejects what it does not know at build time, which
    /// surfaces as a failed transcription with the decoder's message).
    /// </summary>
    private async Task<string> ReadLanguageSettingAsync()
    {
        var configured = (await _settingsService.GetSettingAsync<string>(SettingKeys.WhisperLanguage, DefaultLanguage) ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        return string.IsNullOrEmpty(configured) ? DefaultLanguage : configured;
    }

    /// <summary>
    /// Returns the cached <see cref="WhisperFactory"/> for <paramref name="modelName"/>,
    /// downloading the ggml model and loading it on first use. A different model
    /// than the cached one replaces it. Serialized with a semaphore so concurrent
    /// transcriptions never double-download or double-load the model.
    /// </summary>
    private async Task<WhisperFactory> GetOrCreateFactoryAsync(string modelName, GgmlType ggmlType)
    {
        if (_factory != null && string.Equals(_factoryModelName, modelName, StringComparison.OrdinalIgnoreCase))
        {
            return _factory;
        }

        await _initLock.WaitAsync();
        try
        {
            if (_factory != null && string.Equals(_factoryModelName, modelName, StringComparison.OrdinalIgnoreCase))
            {
                return _factory;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);

            var modelPath = Path.Combine(_modelsDirectory, $"ggml-{modelName}.bin");
            await EnsureModelDownloadedAsync(modelName, ggmlType, modelPath);

            if (_factory != null)
            {
                _logger.LogInformation("Whisper model changed from {Old} to {New}; unloading the old model", _factoryModelName, modelName);
                _factory.Dispose();
                _factory = null;
                _factoryModelName = null;
            }

            _logger.LogInformation("Loading Whisper model from {ModelPath}", modelPath);
            _factory = WhisperFactory.FromPath(modelPath);
            _factoryModelName = modelName;
            _logger.LogInformation("Whisper model {Model} loaded and cached", modelName);

            return _factory;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Downloads the ggml model to local app data if it is not already present.
    /// Must be called while holding <see cref="_initLock"/>.
    /// </summary>
    private async Task EnsureModelDownloadedAsync(string modelName, GgmlType ggmlType, string modelPath)
    {
        if (File.Exists(modelPath))
        {
            return;
        }

        _logger.LogInformation("Whisper model {Model} not found; downloading to {ModelPath}", modelName, modelPath);

        var tempPath = modelPath + ".download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);

            await using (var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(ggmlType))
            await using (var fileStream = File.Create(tempPath))
            {
                await modelStream.CopyToAsync(fileStream);
            }

            // Write to a temp file first, then move into place, so a failed/partial
            // download is never mistaken for a valid model on the next run.
            File.Move(tempPath, modelPath, overwrite: true);

            _logger.LogInformation("Whisper model {Model} downloaded successfully", modelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download the Whisper speech model {Model}", modelName);

            TryDeleteFile(tempPath);

            throw new ModelDownloadFailedException(
                $"The Whisper '{modelName}' speech model could not be downloaded. Check your internet " +
                "connection and try again — the model is fetched once and then cached locally.",
                ex);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete partial model download at {Path}", path);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _factory?.Dispose();
        _factory = null;
        _factoryModelName = null;
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Internal signal that the one-time model download failed; converted to a clean
    /// failure <see cref="TranscriptionResult"/> — never surfaced to callers as a throw.
    /// </summary>
    private sealed class ModelDownloadFailedException : Exception
    {
        public ModelDownloadFailedException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
