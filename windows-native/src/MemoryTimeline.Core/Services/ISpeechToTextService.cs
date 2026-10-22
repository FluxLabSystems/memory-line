namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for speech-to-text transcription.
/// </summary>
public interface ISpeechToTextService
{
    /// <summary>
    /// Transcribes an audio file to text.
    /// </summary>
    /// <param name="audioFilePath">Path to the audio file</param>
    /// <returns>Transcription result</returns>
    Task<TranscriptionResult> TranscribeAsync(string audioFilePath);

    /// <summary>
    /// Transcribes an audio file to text with progress reporting.
    /// </summary>
    /// <param name="audioFilePath">Path to the audio file</param>
    /// <param name="progress">Progress reporter (0.0 to 1.0)</param>
    /// <returns>Transcription result</returns>
    Task<TranscriptionResult> TranscribeAsync(string audioFilePath, IProgress<double>? progress);

    /// <summary>
    /// Transcribes an audio file to text with progress reporting and
    /// cancellation. Cancelling aborts the decode and propagates as
    /// <see cref="OperationCanceledException"/> rather than as a failed
    /// result, so a cancelled queue item is put back rather than marked failed.
    /// </summary>
    /// <param name="audioFilePath">Path to the audio file</param>
    /// <param name="progress">Progress reporter (0.0 to 1.0)</param>
    /// <param name="cancellationToken">Aborts the in-flight transcription</param>
    /// <returns>Transcription result</returns>
    Task<TranscriptionResult> TranscribeAsync(string audioFilePath, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the name of the STT engine.
    /// </summary>
    string EngineName { get; }

    /// <summary>
    /// Gets whether the engine supports streaming transcription.
    /// </summary>
    bool SupportsStreaming { get; }

    /// <summary>
    /// Gets whether the engine requires internet connectivity.
    /// </summary>
    bool RequiresInternet { get; }
}

/// <summary>
/// Result of a transcription operation.
/// </summary>
public class TranscriptionResult
{
    /// <summary>
    /// The transcribed text.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Confidence score (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>
    /// Duration of the transcription process in seconds.
    /// </summary>
    public double ProcessingDurationSeconds { get; set; }

    /// <summary>
    /// Language detected or specified.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Word-level timestamps (if supported by engine).
    /// </summary>
    public List<WordTimestamp>? WordTimestamps { get; set; }

    /// <summary>
    /// Whether the transcription was successful.
    /// </summary>
    public bool Success { get; set; } = true;

    /// <summary>
    /// Error message if transcription failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Word-level timestamp information.
/// </summary>
public class WordTimestamp
{
    public string Word { get; set; } = string.Empty;
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public double Confidence { get; set; } = 1.0;
}
