using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Services;
using System.Collections.ObjectModel;

namespace MemoryTimeline.ViewModels;

/// <summary>
/// ViewModel for the recording queue page.
/// </summary>
public partial class QueueViewModel : ObservableObject, IDisposable
{
    private readonly IQueueService _queueService;
    private readonly ICaptureIngestionService _captureIngestionService;
    private readonly IAudioImportService _audioImportService;
    private readonly IAudioRecordingService _recordingService;
    private readonly IAudioPlaybackService _playbackService;
    private readonly IRecallPromptService _recallPromptService;
    private readonly ILogger<QueueViewModel> _logger;

    /// <summary>
    /// Active recall prompts in rotation (the card shows the first one).
    /// Detached entities loaded from IRecallPromptService.
    /// </summary>
    private List<RecallPrompt> _recallPrompts = new();

    /// <summary>
    /// The prompt id the in-flight recording is answering (set by
    /// RecordRecallAnswer, consumed when the recording stops and its queue
    /// row exists). Null when the recording is a plain capture.
    /// KNOWN LIMIT: this is transient-VM state - navigating away mid-recording
    /// disposes the VM, so a recording stopped after coming back lands in the
    /// queue as a plain (unlinked) capture rather than a prompt answer.
    /// </summary>
    private string? _answeringRecallPromptId;

    // Captured on construction (UI thread) so background service-event and timer
    // callbacks can marshal UI/observable mutations back onto the dispatcher.
    private readonly DispatcherQueue? _dispatcherQueue;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<AudioRecordingDto> _queueItems = new();

    [ObservableProperty]
    private AudioRecordingDto? _selectedItem;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private double _recordingDuration;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _processingCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _failedCount;

    /// <summary>True while "Import audio" is copying/queuing files; disables the button.</summary>
    [ObservableProperty]
    private bool _isImporting;

    /// <summary>
    /// File-picker filter for "Import audio". The Whisper engine needs 16 kHz
    /// 16-bit mono WAV; non-WAV input is normalized by the ingestion pipeline
    /// (IAudioNormalizer) before transcription, so compressed formats are
    /// accepted here. Anything the import service still rejects comes back as
    /// a per-file "Unsupported format" entry in the outcome.
    /// </summary>
    public static readonly IReadOnlyList<string> ImportableAudioExtensions =
        new[] { ".wav", ".m4a", ".mp3", ".aac", ".ogg", ".flac" };

    // Computed properties for UI bindings
    public bool CanStartRecording => !IsRecording && !IsPaused;
    public bool CanPauseRecording => IsRecording;
    public bool CanResumeRecording => IsPaused;
    public bool CanStopRecording => IsRecording || IsPaused;
    public bool CanCancelRecording => IsRecording || IsPaused;
    /// <summary>True while a recording session exists, whether actively capturing or paused.</summary>
    public bool IsRecordingActive => IsRecording || IsPaused;
    public bool IsIdle => !IsRecording && !IsPaused && !IsProcessing;
    public bool HasPendingItems => PendingCount > 0;
    public bool IsQueueEmpty => QueueItems.Count == 0;
    public string FormattedRecordingDuration => TimeSpan.FromSeconds(_recordingDuration).ToString(@"mm\:ss");
    public string StatusMessage => StatusText;
    public bool CanStopPlayback => IsPlaying;

    // Commands for queue operations
    public IRelayCommand ProcessQueueCommand => ProcessAllCommand;

    // Guided recall card (one active prompt at a time)
    [ObservableProperty]
    private string? _recallQuestion;

    [ObservableProperty]
    private string _recallKindGlyph = "\uE787";

    [ObservableProperty]
    private bool _hasRecallPrompt;

    /// <summary>The prompt currently shown on the recall card, or null.</summary>
    public string? CurrentRecallPromptId => _recallPrompts.FirstOrDefault()?.PromptId;

    // Playback properties
    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _playbackPosition;

    [ObservableProperty]
    private double _playbackDuration;

    private System.Threading.Timer? _recordingTimer;

    public QueueViewModel(
        IQueueService queueService,
        ICaptureIngestionService captureIngestionService,
        IAudioImportService audioImportService,
        IAudioRecordingService recordingService,
        IAudioPlaybackService playbackService,
        IRecallPromptService recallPromptService,
        ILogger<QueueViewModel> logger)
    {
        _queueService = queueService;
        _captureIngestionService = captureIngestionService;
        _audioImportService = audioImportService;
        _recordingService = recordingService;
        _playbackService = playbackService;
        _recallPromptService = recallPromptService;
        _logger = logger;

        // Capture the UI dispatcher while we are still on the UI thread.
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Subscribe to service events. NOTE: the audio recording/playback services
        // are DI singletons while this ViewModel is transient, so these subscriptions
        // root the ViewModel for the lifetime of the app unless Dispose() is called.
        _queueService.QueueItemStatusChanged += OnQueueItemStatusChanged;
        _queueService.ProcessingProgressChanged += OnProcessingProgressChanged;
        _recordingService.RecordingStateChanged += OnRecordingStateChanged;
        _playbackService.PlaybackStateChanged += OnPlaybackStateChanged;
        _playbackService.PositionChanged += OnPlaybackPositionChanged;
    }

    /// <summary>
    /// Marshals the supplied action onto the UI dispatcher thread. Service events and
    /// timer callbacks arrive on background threads; mutating observable state or
    /// ObservableCollections off the UI thread corrupts/crashes the WinUI binding layer.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
    }

    /// <summary>
    /// Initializes the queue view.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            // Re-attach to any in-flight recording. The recording service is a
            // singleton that outlives this transient ViewModel, so navigating
            // away and back must resume tracking instead of orphaning a hot mic.
            var recordingState = _recordingService.GetRecordingState();
            IsRecording = recordingState == AudioRecordingState.Recording;
            IsPaused = recordingState == AudioRecordingState.Paused;

            if (IsRecording || IsPaused)
            {
                RecordingDuration = _recordingService.GetRecordingDuration();
            }

            if (IsRecording)
            {
                StartRecordingTimer();
            }

            await RefreshQueueAsync();

            StatusText = IsRecording
                ? "Recording..."
                : IsPaused
                    ? "Recording paused"
                    : $"{QueueItems.Count} recordings in queue";

            // Loaded after the status line so a recall failure message (set
            // inside LoadRecallPromptsAsync) is not immediately overwritten.
            await LoadRecallPromptsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing queue");
            StatusText = "Error loading queue";
        }
    }

    /// <summary>
    /// (Re)starts the timer that polls the recording duration while capturing.
    /// The timer callback runs on a threadpool thread, so the observable update
    /// is marshalled back onto the UI dispatcher.
    /// </summary>
    private void StartRecordingTimer()
    {
        _recordingTimer?.Dispose();
        _recordingTimer = new System.Threading.Timer(
            _ => RunOnUi(() => RecordingDuration = _recordingService.GetRecordingDuration()),
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100));
    }

    #region Recording Commands

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        try
        {
            var settings = new AudioRecordingSettings
            {
                SampleRate = 16000,
                BitsPerSample = 16,
                Channels = 1
            };

            await _recordingService.StartRecordingAsync(settings);
            IsRecording = true;
            IsPaused = false;
            StatusText = "Recording...";

            StartRecordingTimer();

            _logger.LogInformation("Recording started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting recording");
            StatusText = "Error starting recording";
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteStopRecording))]
    private async Task StopRecordingAsync()
    {
        try
        {
            _recordingTimer?.Dispose();
            _recordingTimer = null;

            var recording = await _recordingService.StopRecordingAsync();
            IsRecording = false;
            IsPaused = false;
            StatusText = "Recording stopped";

            // Add to queue through capture ingestion (design §7.2), which mints the
            // capture/artifact identity and sync-outbox entry alongside the queue item.
            // A duplicate replay comes back with Success == true / AlreadyIngested.
            var result = await _captureIngestionService.IngestLocalRecordingAsync(recording);
            if (!result.Success)
            {
                // The audio file is already safe on disk — only the queue hand-off
                // failed — so say so instead of pretending the item was queued.
                _logger.LogError("Failed to ingest recording {QueueId}: {Reason}",
                    recording.QueueId, result.FailureReason);
                StatusText = $"Recording saved, but could not be queued: {result.FailureReason}";
                // Nothing reached the queue, so there is no row to stamp as a
                // prompt answer - drop the recall link (mirrors the catch below)
                // so the NEXT successful recording is not stamped as this
                // prompt's answer.
                _answeringRecallPromptId = null;
            }
            else if (result.QueueId is string queueId)
            {
                // Ingestion is the point where the queue row id first exists,
                // so a recording started from the recall card can be linked to
                // its prompt right here.
                await LinkRecallAnswerAsync(queueId);
            }
            else
            {
                // Success without a queue row: an idempotent replay of a capture
                // whose queue item was deliberately deleted (design §12.2).
                // Nothing exists to link, so drop the recall link.
                _answeringRecallPromptId = null;
            }

            await RefreshQueueAsync();

            if (result.Success)
            {
                _logger.LogInformation("Recording stopped and added to queue");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping recording");
            StatusText = "Error stopping recording";
            // A failed stop answers nothing - drop the recall link (mirrors
            // CancelRecording) so the NEXT successful plain recording is not
            // stamped as this prompt's answer.
            _answeringRecallPromptId = null;
        }
    }

    // Stop must also work while paused, otherwise pause becomes a one-way trap
    // that leaves the hardware session open until app exit.
    private bool CanExecuteStopRecording() => IsRecording || IsPaused;

    [RelayCommand(CanExecute = nameof(CanExecutePauseRecording))]
    private async Task PauseRecordingAsync()
    {
        try
        {
            await _recordingService.PauseRecordingAsync();

            _recordingTimer?.Dispose();
            _recordingTimer = null;

            // Paused is a distinct state: the session still exists (hardware
            // resources retained), it is just not capturing. Do NOT collapse
            // this into "not recording at all".
            IsRecording = false;
            IsPaused = true;
            StatusText = "Recording paused";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing recording");
            StatusText = "Error pausing recording";
        }
    }

    private bool CanExecutePauseRecording() => IsRecording;

    [RelayCommand(CanExecute = nameof(CanExecuteResumeRecording))]
    private async Task ResumeRecordingAsync()
    {
        try
        {
            await _recordingService.ResumeRecordingAsync();
            IsRecording = true;
            IsPaused = false;
            StatusText = "Recording...";

            StartRecordingTimer();

            _logger.LogInformation("Recording resumed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming recording");
            StatusText = "Error resuming recording";
        }
    }

    private bool CanExecuteResumeRecording() => IsPaused;

    [RelayCommand(CanExecute = nameof(CanExecuteCancelRecording))]
    private async Task CancelRecordingAsync()
    {
        try
        {
            _recordingTimer?.Dispose();
            _recordingTimer = null;

            await _recordingService.CancelRecordingAsync();
            IsRecording = false;
            IsPaused = false;
            RecordingDuration = 0;
            // A cancelled recording answers nothing - drop the recall link.
            _answeringRecallPromptId = null;
            StatusText = "Recording cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling recording");
            StatusText = "Error cancelling recording";
        }
    }

    // Cancel must also work while paused (see CanExecuteStopRecording).
    private bool CanExecuteCancelRecording() => IsRecording || IsPaused;

    #endregion

    #region Queue Management Commands

    [RelayCommand]
    private async Task RefreshQueueAsync()
    {
        try
        {
            var items = await _queueService.GetAllQueueItemsAsync();

            QueueItems.Clear();
            foreach (var item in items)
            {
                // Initialize commands for each item
                item.PlayCommand = new RelayCommand(() => _ = PlayItemAsync(item));
                item.RetryCommand = new RelayCommand(() => _ = RetryItemAsync(item));
                item.RemoveCommand = new RelayCommand(() => _ = RemoveItemAsync(item));

                QueueItems.Add(item);
            }

            OnPropertyChanged(nameof(IsQueueEmpty));
            await UpdateStatusCountsAsync();

            // Every queue mutation on this page ends in a refresh, so this is
            // the one place the shell's Queue badge needs to hear about it.
            WeakReferenceMessenger.Default.Send(new QueueChangedMessage());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing queue");
        }
    }

    [RelayCommand]
    private async Task ProcessNextAsync()
    {
        if (IsProcessing) return;

        try
        {
            IsProcessing = true;
            StatusText = "Processing next item...";

            await _queueService.ProcessNextItemAsync();

            await RefreshQueueAsync();
            StatusText = "Processing complete";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing next item");
            StatusText = "Error processing item";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task ProcessAllAsync()
    {
        if (IsProcessing) return;

        try
        {
            IsProcessing = true;
            StatusText = "Processing all pending items...";

            await _queueService.ProcessAllPendingAsync();

            await RefreshQueueAsync();
            StatusText = "All items processed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing all items");
            StatusText = "Error processing items";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task RemoveItemAsync(AudioRecordingDto? item)
    {
        if (item == null) return;

        try
        {
            await _queueService.RemoveQueueItemAsync(item.QueueId);
            QueueItems.Remove(item);
            OnPropertyChanged(nameof(IsQueueEmpty));
            await UpdateStatusCountsAsync();

            StatusText = "Item removed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing item");
            StatusText = "Error removing item";
        }
    }

    [RelayCommand]
    private async Task RetryItemAsync(AudioRecordingDto? item)
    {
        if (item == null || item.Status != Data.Models.QueueStatus.Failed) return;

        try
        {
            await _queueService.RetryFailedItemAsync(item.QueueId);
            await RefreshQueueAsync();

            StatusText = "Item queued for retry";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrying item");
            StatusText = "Error retrying item";
        }
    }

    [RelayCommand]
    private async Task ClearCompletedAsync()
    {
        try
        {
            await _queueService.ClearCompletedItemsAsync();
            await RefreshQueueAsync();

            StatusText = "Completed items cleared";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing completed items");
            StatusText = "Error clearing items";
        }
    }

    /// <summary>
    /// Enqueues pasted text as a Text source (no audio, no speech-to-text).
    /// Returns null on success, or a user-facing error message on failure —
    /// the paste dialog keeps itself open and shows the message in its InfoBar.
    /// Errors are logged here; the caller only displays them.
    /// </summary>
    public async Task<string?> EnqueuePastedTextAsync(string? text, string? label)
    {
        try
        {
            var queueId = await _queueService.EnqueueTextAsync(text ?? string.Empty, label);
            await RefreshQueueAsync();
            StatusText = "Text added to queue";

            _logger.LogInformation("Pasted text enqueued as {QueueId}", queueId);
            return null;
        }
        catch (ArgumentException ex)
        {
            // Validation rejection (empty/whitespace text) — expected user error.
            _logger.LogWarning(ex, "Rejected pasted text");
            return ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding pasted text to queue");
            StatusText = "Error adding text to queue";
            return $"Could not add text to the queue: {ex.Message}";
        }
    }

    /// <summary>
    /// "Import audio…": routes each picked file through
    /// <see cref="IAudioImportService.ImportFilesAsync"/>, which copies it into
    /// the app's recordings folder and enqueues it via ICaptureIngestionService
    /// (same capture/artifact identity and content-hash dedupe as an in-app
    /// recording). Files enter the queue as pending; "Process All" transcribes
    /// and extracts them with the queue's normal retry/config-error handling.
    /// Returns a summary the page shows in its InfoBar, with one line per
    /// skipped or failed file; never throws.
    /// </summary>
    public async Task<AudioImportOutcome> ImportAudioFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths == null || filePaths.Count == 0)
        {
            return new AudioImportOutcome("No files selected", "Choose one or more audio files to import.", 0, 0);
        }

        if (IsImporting)
        {
            return new AudioImportOutcome("Import already running", "Wait for the current import to finish.", 0, 0);
        }

        try
        {
            IsImporting = true;
            StatusText = filePaths.Count == 1
                ? "Importing 1 audio file..."
                : $"Importing {filePaths.Count} audio files...";

            var options = new AudioImportOptions
            {
                // Queue as pending (like a stopped recording) instead of
                // transcribing inline: the queue owns processing order, retries,
                // and the non-retryable configuration-error path.
                AutoProcess = false,
                SupportedFormats = ImportableAudioExtensions.ToArray()
            };

            var result = await _audioImportService.ImportFilesAsync(filePaths, options);
            await RefreshQueueAsync();

            var outcome = BuildImportOutcome(result);
            StatusText = outcome.Title;
            _logger.LogInformation("Audio import finished: {Imported} imported, {Problems} skipped/failed",
                outcome.ImportedCount, outcome.ProblemCount);
            return outcome;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing audio files");
            StatusText = "Error importing audio";
            return new AudioImportOutcome("Import failed", ex.Message, 0, filePaths.Count);
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// Pure summary of an import batch for the InfoBar: imported count in the
    /// title, one "file: reason" line per skipped or failed file in the message.
    /// </summary>
    public static AudioImportOutcome BuildImportOutcome(AudioImportResult result)
    {
        var imported = result.SuccessfulImports;
        var problems = result.Items
            .Where(i => i.Status == AudioImportStatus.Failed || i.Status == AudioImportStatus.Skipped)
            .Select(i => $"{i.FileName}: {i.ErrorMessage ?? i.Status.ToString().ToLowerInvariant()}")
            .ToList();

        var importedText = imported == 1
            ? "1 audio file added to the queue"
            : $"{imported} audio files added to the queue";

        if (problems.Count == 0)
        {
            return new AudioImportOutcome(importedText, "Press Process All to transcribe and extract events.", imported, 0);
        }

        var title = imported == 0
            ? (problems.Count == 1 ? "The audio file was not imported" : "No audio files were imported")
            : $"{importedText}; {problems.Count} skipped or failed";

        return new AudioImportOutcome(title, string.Join(Environment.NewLine, problems), imported, problems.Count);
    }

    #endregion

    #region Guided Recall

    /// <summary>
    /// Loads (topping up as needed) the active recall prompts and refreshes
    /// the card. Failures surface on the status bar and are logged; the rest
    /// of the queue page keeps working.
    /// </summary>
    private async Task LoadRecallPromptsAsync()
    {
        try
        {
            _recallPrompts = await _recallPromptService.GetPromptsAsync(5);
            RunOnUi(UpdateRecallCard);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading recall prompts");
            RunOnUi(() => StatusText = "Error loading memory prompts");
        }
    }

    /// <summary>Shows the first prompt in rotation on the card.</summary>
    private void UpdateRecallCard()
    {
        var current = _recallPrompts.FirstOrDefault();
        RecallQuestion = current?.Question;
        RecallKindGlyph = current == null ? string.Empty : GetRecallKindGlyph(current.Kind);
        HasRecallPrompt = current != null;
    }

    private static string GetRecallKindGlyph(RecallPromptKind kind)
    {
        return kind switch
        {
            RecallPromptKind.DensityGap => "\uE787",        // Calendar
            RecallPromptKind.DanglingPerson => "\uE77B",    // Contact
            RecallPromptKind.EraEdge => "\uE81C",           // History (matches Eras nav)
            RecallPromptKind.ThinEvent => "\uE70B",         // QuickNote
            RecallPromptKind.AnniversaryAnchor => "\uE735", // FavoriteStar
            _ => "\uE787"
        };
    }

    /// <summary>
    /// Brings the prompt referenced by a "recall:&lt;promptId&gt;" navigation
    /// parameter (Home page's Answer button) to the front of the card.
    /// Called on the UI thread from QueuePage.OnNavigatedTo.
    /// </summary>
    public void FocusRecallPrompt(string promptId)
    {
        var index = _recallPrompts.FindIndex(p => p.PromptId == promptId);
        if (index < 0)
        {
            // Answered/dismissed elsewhere, or generation reshuffled the pool.
            _logger.LogWarning("Recall prompt {PromptId} not found to focus", promptId);
            StatusText = "That memory prompt is no longer available";
            return;
        }

        if (index > 0)
        {
            var prompt = _recallPrompts[index];
            _recallPrompts.RemoveAt(index);
            _recallPrompts.Insert(0, prompt);
        }

        UpdateRecallCard();
    }

    /// <summary>
    /// "Record answer": remembers the active prompt id, then starts the
    /// normal recording flow. When the recording stops and its queue row is
    /// created, <see cref="LinkRecallAnswerAsync"/> stamps the row with the
    /// question and marks the prompt Answered.
    /// </summary>
    [RelayCommand]
    private async Task RecordRecallAnswerAsync()
    {
        var promptId = CurrentRecallPromptId;
        if (promptId == null || !CanStartRecording)
        {
            return;
        }

        _answeringRecallPromptId = promptId;
        await StartRecordingAsync();

        if (!IsRecording)
        {
            // The recording failed to start (StartRecordingAsync already
            // surfaced the error); there is nothing to link.
            _answeringRecallPromptId = null;
        }
    }

    /// <summary>
    /// Links a just-created queue row to the recall prompt being answered (if
    /// any): the service stamps the row's SourceLabel with the question and
    /// marks the prompt Answered. No-op for plain recordings.
    /// </summary>
    private async Task LinkRecallAnswerAsync(string queueId)
    {
        var promptId = _answeringRecallPromptId;
        _answeringRecallPromptId = null;
        if (promptId == null)
        {
            return;
        }

        try
        {
            await _recallPromptService.MarkAnsweredAsync(promptId, queueId);
            await RemoveRecallPromptFromRotationAsync(promptId);
            StatusText = "Recording captured as a prompt answer";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error linking recording {QueueId} to recall prompt {PromptId}",
                queueId, promptId);
            StatusText = $"Could not link the answer to the prompt: {ex.Message}";
        }
    }

    /// <summary>
    /// "Type answer" submit path: routes the paste dialog's text through
    /// StartAnsweringAsync (labels the queue row with the question and marks
    /// the prompt Answered). Returns null on success, or a user-facing error
    /// message that the dialog shows in its InfoBar.
    /// </summary>
    public async Task<string?> EnqueueRecallAnswerAsync(string? text)
    {
        var prompt = _recallPrompts.FirstOrDefault();
        if (prompt == null)
        {
            return "The memory prompt is no longer available.";
        }

        try
        {
            var queueId = await _recallPromptService.StartAnsweringAsync(prompt.PromptId, text ?? string.Empty);
            await RefreshQueueAsync();
            await RemoveRecallPromptFromRotationAsync(prompt.PromptId);
            StatusText = "Answer added to queue";

            _logger.LogInformation("Recall prompt {PromptId} answered as queue item {QueueId}",
                prompt.PromptId, queueId);
            return null;
        }
        catch (ArgumentException ex)
        {
            // Validation rejection (empty/whitespace text) - expected user error.
            _logger.LogWarning(ex, "Rejected recall answer text");
            return ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error answering recall prompt {PromptId}", prompt.PromptId);
            StatusText = "Error adding the answer to the queue";
            return $"Could not add the answer to the queue: {ex.Message}";
        }
    }

    /// <summary>
    /// "Skip" flyout: NotInterested/AlreadyCovered permanently retire the
    /// prompt; Later snoozes it for 14 days. The card then advances.
    /// </summary>
    [RelayCommand]
    private async Task DismissRecallPromptAsync(string? reason)
    {
        var prompt = _recallPrompts.FirstOrDefault();
        if (prompt == null)
        {
            return;
        }

        if (!Enum.TryParse<DismissReason>(reason, out var parsedReason) || parsedReason == DismissReason.None)
        {
            _logger.LogWarning("Unknown recall dismiss reason '{Reason}'", reason);
            StatusText = "Could not skip the prompt: unknown reason";
            return;
        }

        try
        {
            await _recallPromptService.DismissAsync(prompt.PromptId, parsedReason);
            await RemoveRecallPromptFromRotationAsync(prompt.PromptId);
            StatusText = parsedReason == DismissReason.Later
                ? "Prompt snoozed for two weeks"
                : "Prompt dismissed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dismissing recall prompt {PromptId}", prompt.PromptId);
            StatusText = $"Could not skip the prompt: {ex.Message}";
        }
    }

    /// <summary>
    /// Drops a handled prompt from the local rotation and advances the card,
    /// reloading (which tops the pool back up) when the rotation runs dry.
    /// </summary>
    private async Task RemoveRecallPromptFromRotationAsync(string promptId)
    {
        _recallPrompts.RemoveAll(p => p.PromptId == promptId);
        if (_recallPrompts.Count == 0)
        {
            await LoadRecallPromptsAsync();
        }
        else
        {
            RunOnUi(UpdateRecallCard);
        }
    }

    #endregion

    #region Playback Commands

    [RelayCommand]
    private async Task PlayItemAsync(AudioRecordingDto? item)
    {
        // Text sources have no audio file (AudioFilePath is string.Empty by
        // convention); the play button is hidden for them, but guard anyway.
        if (item == null || !item.IsAudioSource) return;

        try
        {
            if (IsPlaying && SelectedItem?.QueueId == item.QueueId)
            {
                // Pause current playback
                _playbackService.Pause();
            }
            else
            {
                // Load and play new item
                SelectedItem = item;
                await _playbackService.LoadAudioAsync(item.AudioFilePath);
                _playbackService.Play();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing audio");
            StatusText = "Error playing audio";
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteStopPlayback))]
    private void StopPlayback()
    {
        try
        {
            _playbackService.Stop();
            SelectedItem = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping playback");
        }
    }

    private bool CanExecuteStopPlayback() => IsPlaying;

    #endregion

    #region Property Changed Handlers

    partial void OnIsRecordingChanged(bool value)
    {
        NotifyRecordingStateDependents();
    }

    partial void OnIsPausedChanged(bool value)
    {
        NotifyRecordingStateDependents();
    }

    /// <summary>
    /// Re-evaluates every computed property and command CanExecute that derives from
    /// IsRecording/IsPaused. Called from both partials so state set directly by
    /// commands and state pushed by RecordingStateChanged stay consistent.
    /// </summary>
    private void NotifyRecordingStateDependents()
    {
        OnPropertyChanged(nameof(CanStartRecording));
        OnPropertyChanged(nameof(CanPauseRecording));
        OnPropertyChanged(nameof(CanResumeRecording));
        OnPropertyChanged(nameof(CanStopRecording));
        OnPropertyChanged(nameof(CanCancelRecording));
        OnPropertyChanged(nameof(IsRecordingActive));
        OnPropertyChanged(nameof(IsIdle));

        StopRecordingCommand.NotifyCanExecuteChanged();
        PauseRecordingCommand.NotifyCanExecuteChanged();
        ResumeRecordingCommand.NotifyCanExecuteChanged();
        CancelRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsProcessingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStopPlayback));
    }

    partial void OnPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPendingItems));
    }

    partial void OnRecordingDurationChanged(double value)
    {
        OnPropertyChanged(nameof(FormattedRecordingDuration));
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(StatusMessage));
    }

    #endregion

    #region Private Methods

    private async Task UpdateStatusCountsAsync()
    {
        PendingCount = await _queueService.GetQueueCountByStatusAsync(Data.Models.QueueStatus.Pending);
        ProcessingCount = await _queueService.GetQueueCountByStatusAsync(Data.Models.QueueStatus.Processing);
        CompletedCount = await _queueService.GetQueueCountByStatusAsync(Data.Models.QueueStatus.Completed);
        FailedCount = await _queueService.GetQueueCountByStatusAsync(Data.Models.QueueStatus.Failed);
    }

    private void OnQueueItemStatusChanged(object? sender, QueueItemStatusChangedEventArgs e)
    {
        // Service events arrive on a background thread; marshal all observable mutations.
        RunOnUi(() =>
        {
            var item = QueueItems.FirstOrDefault(i => i.QueueId == e.QueueId);
            if (item != null)
            {
                item.Status = e.NewStatus;
                item.ErrorMessage = e.ErrorMessage;
            }

            _ = UpdateStatusCountsAsync();
        });
    }

    private void OnRecordingStateChanged(object? sender, AudioRecordingStateChangedEventArgs e)
    {
        RunOnUi(() =>
        {
            // Map the full service state: Paused must remain distinct from Idle,
            // otherwise the UI loses track of the still-open recording session.
            IsRecording = e.NewState == AudioRecordingState.Recording;
            IsPaused = e.NewState == AudioRecordingState.Paused;
            // Computed properties and command CanExecute states are refreshed by
            // the OnIsRecordingChanged/OnIsPausedChanged partials.
        });
    }

    private void OnProcessingProgressChanged(object? sender, QueueProcessingProgressEventArgs e)
    {
        // Raised from the queue's background processing; marshal before touching
        // observable state so the per-item ProgressBar bindings update safely.
        RunOnUi(() =>
        {
            var item = QueueItems.FirstOrDefault(i => i.QueueId == e.QueueId);
            if (item != null)
            {
                item.Progress = e.ProgressPercentage;
            }

            if (!string.IsNullOrEmpty(e.StatusMessage))
            {
                StatusText = e.StatusMessage;
            }
        });
    }

    private void OnPlaybackStateChanged(object? sender, AudioPlaybackStateChangedEventArgs e)
    {
        RunOnUi(() =>
        {
            IsPlaying = e.NewState == AudioPlaybackState.Playing;
            StopPlaybackCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnPlaybackPositionChanged(object? sender, AudioPositionChangedEventArgs e)
    {
        // Fires very frequently on a background thread during playback.
        RunOnUi(() =>
        {
            PlaybackPosition = e.Position;
            PlaybackDuration = e.Duration;
        });
    }

    #endregion

    /// <summary>
    /// Unsubscribes from service events and stops the recording timer. Must be called
    /// when the owning page is unloaded/navigated away from, otherwise the singleton
    /// audio services keep this transient ViewModel alive for the life of the app.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queueService.QueueItemStatusChanged -= OnQueueItemStatusChanged;
        _queueService.ProcessingProgressChanged -= OnProcessingProgressChanged;
        _recordingService.RecordingStateChanged -= OnRecordingStateChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _playbackService.PositionChanged -= OnPlaybackPositionChanged;

        _recordingTimer?.Dispose();
        _recordingTimer = null;

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Outcome of an "Import audio" batch, shaped for the Queue page's InfoBar.
/// </summary>
/// <param name="Title">One-line summary (also shown in the status bar).</param>
/// <param name="Message">Per-file skip/failure reasons, or the next step when everything imported.</param>
/// <param name="ImportedCount">Files that entered the queue.</param>
/// <param name="ProblemCount">Files skipped (unsupported, duplicate, too large) or failed.</param>
public sealed record AudioImportOutcome(string Title, string Message, int ImportedCount, int ProblemCount)
{
    /// <summary>True when at least one file was skipped or failed.</summary>
    public bool HasProblems => ProblemCount > 0;
}
