using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Core.DTOs;

/// <summary>
/// DTO for a queue capture source (audio recording or pasted/imported text).
/// </summary>
public partial class AudioRecordingDto : ObservableObject
{
    [ObservableProperty]
    private string _queueId = string.Empty;

    /// <summary>Empty for non-audio sources (see RecordingQueue.AudioFilePath).</summary>
    [ObservableProperty]
    private string _audioFilePath = string.Empty;

    [ObservableProperty]
    private QueueSourceType _sourceType = QueueSourceType.Audio;

    [ObservableProperty]
    private string? _sourceLabel;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private double? _durationSeconds;

    [ObservableProperty]
    private long? _fileSizeBytes;

    [ObservableProperty]
    private DateTime _createdAt;

    [ObservableProperty]
    private DateTime? _processedAt;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _transcriptionText;

    [ObservableProperty]
    private int _pendingEventCount;

    /// <summary>
    /// Sync state of the queue row (<see cref="QueueSyncState"/> values):
    /// local_only for a recording made here, received for a remote capture
    /// the service has not yet acknowledged, acknowledged once it has. Null
    /// on rows older than the sync columns.
    /// </summary>
    [ObservableProperty]
    private string? _syncState;

    [ObservableProperty]
    private double _progress;

    // Commands for UI actions
    public IRelayCommand? PlayCommand { get; set; }
    public IRelayCommand? RetryCommand { get; set; }
    public IRelayCommand? RemoveCommand { get; set; }

    // Display properties

    /// <summary>
    /// Card headline: the user's source label when present, otherwise a friendly
    /// local timestamp like "5 Aug 2026, 14:03" (never a raw DateTime dump).
    /// </summary>
    public string DisplayTitle => !string.IsNullOrWhiteSpace(SourceLabel)
        ? SourceLabel!
        : FormatCreatedAt(CreatedAt);

    /// <summary>Source icon: microphone for audio, document for text/imported.</summary>
    public string SourceGlyph => SourceType == QueueSourceType.Audio
        ? "\uE720"  // Microphone
        : "\uE8A5"; // Document

    /// <summary>
    /// True when this item is a real audio recording with a file path. Text
    /// sources store an empty AudioFilePath (NOT NULL column convention), so
    /// playback/duration affordances must be hidden for them.
    /// </summary>
    public bool IsAudioSource => SourceType == QueueSourceType.Audio
        && !string.IsNullOrEmpty(AudioFilePath);

    /// <summary>"mm:ss", or "h:mm:ss" once the recording reaches an hour; "Unknown" without a duration.</summary>
    public string DurationDisplay => DurationSeconds.HasValue
        ? DurationFormat.Clock(DurationSeconds.Value)
        : "Unknown";

    public string DurationText => DurationDisplay;

    public string FileSizeDisplay => FileSizeBytes.HasValue
        ? FormatFileSize(FileSizeBytes.Value)
        : "Unknown";

    public string StatusDisplay => Status switch
    {
        "pending" => "Pending",
        "processing" => "Processing...",
        "completed" => "Completed",
        "failed" => "Failed",
        _ => Status
    };

    public string StatusText => StatusDisplay;

    public string StatusIcon => Status switch
    {
        "pending" => "\uE81E", // Clock
        "processing" => "\uE895", // Sync
        "completed" => "\uE73E", // CheckMark
        "failed" => "\uE783", // Error
        _ => "\uE946" // Help
    };

    public string StatusGlyph => StatusIcon;

    /// <summary>
    /// Status tint as a "#RRGGBB" token. Core stays UI-framework-neutral; the
    /// view turns this into a brush via HexToBrushConverter.
    /// </summary>
    public string StatusColorHex => Status switch
    {
        "pending" => "#FFA500",    // Orange
        "processing" => "#1E90FF", // DodgerBlue
        "completed" => "#008000",  // Green
        "failed" => "#FF0000",     // Red
        _ => "#808080"             // Gray
    };

    public bool IsProcessing => Status == "processing";

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // Text sources have no audio to play; hide the play affordance for them.
    public bool CanPlay => IsAudioSource && Status is "pending" or "completed" or "failed";

    public bool CanRetry => Status == "failed";

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StatusColorHex));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanRetry));
    }

    partial void OnSourceTypeChanged(QueueSourceType value)
    {
        OnPropertyChanged(nameof(SourceGlyph));
        OnPropertyChanged(nameof(IsAudioSource));
        OnPropertyChanged(nameof(CanPlay));
    }

    partial void OnSourceLabelChanged(string? value)
    {
        OnPropertyChanged(nameof(DisplayTitle));
    }

    partial void OnCreatedAtChanged(DateTime value)
    {
        OnPropertyChanged(nameof(DisplayTitle));
    }

    partial void OnAudioFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(IsAudioSource));
        OnPropertyChanged(nameof(CanPlay));
    }

    partial void OnDurationSecondsChanged(double? value)
    {
        OnPropertyChanged(nameof(DurationDisplay));
        OnPropertyChanged(nameof(DurationText));
    }

    partial void OnFileSizeBytesChanged(long? value)
    {
        OnPropertyChanged(nameof(FileSizeDisplay));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Formats a stored-UTC timestamp as a friendly local date/time, e.g.
    /// "5 Aug 2026, 14:03". Rows round-tripped through SQLite come back with
    /// DateTimeKind.Unspecified, so treat those as UTC before converting.
    /// </summary>
    private static string FormatCreatedAt(DateTime createdAt)
    {
        var local = createdAt.Kind switch
        {
            DateTimeKind.Local => createdAt,
            DateTimeKind.Utc => createdAt.ToLocalTime(),
            _ => DateTime.SpecifyKind(createdAt, DateTimeKind.Utc).ToLocalTime()
        };

        return local.ToString("d MMM yyyy, HH:mm");
    }

    public static AudioRecordingDto FromRecordingQueue(Data.Models.RecordingQueue queue)
    {
        return new AudioRecordingDto
        {
            QueueId = queue.QueueId,
            AudioFilePath = queue.AudioFilePath,
            SourceType = queue.SourceType,
            SourceLabel = queue.SourceLabel,
            Status = queue.Status,
            DurationSeconds = queue.DurationSeconds,
            FileSizeBytes = queue.FileSizeBytes,
            CreatedAt = queue.CreatedAt,
            ProcessedAt = queue.ProcessedAt,
            ErrorMessage = queue.ErrorMessage,
            TranscriptionText = queue.Transcript,
            PendingEventCount = queue.PendingEvents.Count,
            SyncState = queue.SyncState
        };
    }
}

/// <summary>
/// Audio device information DTO.
/// </summary>
public class AudioDeviceDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Audio recording settings.
/// </summary>
public class AudioRecordingSettings
{
    public int SampleRate { get; set; } = 16000; // 16kHz
    public int BitsPerSample { get; set; } = 16;
    public int Channels { get; set; } = 1; // Mono
    public string? DeviceId { get; set; }
    public string AudioFormat { get; set; } = "wav";
}
