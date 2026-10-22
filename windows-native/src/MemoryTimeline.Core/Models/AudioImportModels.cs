namespace MemoryTimeline.Core.Models;

/// <summary>
/// Represents an audio file to be imported.
/// </summary>
public class AudioImportItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public TimeSpan? Duration { get; set; }
    public DateTime? RecordedAt { get; set; }
    public DateTime FileCreatedAt { get; set; }
    public string? Format { get; set; }
    public AudioImportStatus Status { get; set; } = AudioImportStatus.Pending;
    public string? ErrorMessage { get; set; }
    public string? QueueItemId { get; set; }
    public double Progress { get; set; }
}

/// <summary>
/// Status of an audio import item.
/// </summary>
public enum AudioImportStatus
{
    Pending,
    Copying,
    Queued,
    Transcribing,
    Extracting,
    Completed,
    Failed,
    Skipped
}

/// <summary>
/// Options for audio import.
/// </summary>
public class AudioImportOptions
{
    public bool AutoProcess { get; set; } = true;
    public bool AutoExtract { get; set; } = true;
    public bool DeleteSourceAfterImport { get; set; }
    public bool SkipDuplicates { get; set; } = true;
    public bool RecursiveScan { get; set; } = true;

    /// <summary>
    /// The containers import accepts when a platform audio normalizer is
    /// registered: capture ingestion transcodes each of them to the 16 kHz
    /// WAV the transcriber reads (Services.IAudioNormalizer). Without a
    /// normalizer — the portable stack, or a head that has none yet — import
    /// accepts <c>.wav</c> only, whatever this list says; AudioImportService
    /// applies that narrowing, this list is the ceiling.
    /// </summary>
    public string[] SupportedFormats { get; set; } = { ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".wma" };
    public long MaxFileSizeBytes { get; set; } = 500 * 1024 * 1024; // 500 MB
}

/// <summary>
/// Result of an audio import batch operation.
/// </summary>
public class AudioImportResult
{
    public List<AudioImportItem> Items { get; set; } = new();
    public int TotalFiles { get; set; }
    public int SuccessfulImports { get; set; }
    public int FailedImports { get; set; }
    public int SkippedDuplicates { get; set; }
    public int QueuedForProcessing { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Progress information for audio import.
/// </summary>
public class AudioImportProgress
{
    public int CurrentItem { get; set; }
    public int TotalItems { get; set; }
    public string CurrentFileName { get; set; } = string.Empty;
    public AudioImportStatus CurrentStatus { get; set; }
    public double OverallProgress { get; set; } // 0-100
    public double CurrentItemProgress { get; set; } // 0-100
    public string StatusMessage { get; set; } = string.Empty;
}
