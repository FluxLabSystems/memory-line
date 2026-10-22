using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for importing audio files and auto-processing.
/// </summary>
public interface IAudioImportService
{
    /// <summary>
    /// Import audio files from specified paths.
    /// </summary>
    Task<AudioImportResult> ImportFilesAsync(
        IEnumerable<string> filePaths,
        AudioImportOptions? options = null,
        IProgress<AudioImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scan a directory for audio files.
    /// </summary>
    Task<List<AudioImportItem>> ScanDirectoryAsync(
        string directoryPath,
        AudioImportOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import audio files from a directory.
    /// </summary>
    Task<AudioImportResult> ImportDirectoryAsync(
        string directoryPath,
        AudioImportOptions? options = null,
        IProgress<AudioImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a file is a supported audio format.
    /// </summary>
    bool IsSupportedFormat(string filePath, AudioImportOptions? options = null);

    /// <summary>
    /// Get audio file metadata.
    /// </summary>
    Task<AudioImportItem> GetFileInfoAsync(string filePath);

    /// <summary>
    /// Event raised when import progress changes.
    /// </summary>
    event EventHandler<AudioImportProgress>? ImportProgressChanged;
}

/// <summary>
/// Audio import service implementation. Queue insertion routes through
/// ICaptureIngestionService (design §7.2) so imported files get the same
/// capture/artifact identity as in-app recordings — and the same
/// normalization: ingestion converts a compressed import to the 16 kHz WAV
/// the transcriber reads, so this service accepts every format the
/// registered <see cref="IAudioNormalizer"/> can decode. The remaining
/// database work still opens a short-lived DbContext per operation via
/// IDbContextFactory.
/// </summary>
public class AudioImportService : IAudioImportService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IQueueService _queueService;
    private readonly ICaptureIngestionService _captureIngestionService;
    private readonly ISpeechToTextService? _sttService;
    private readonly IEventExtractionService? _extractionService;
    private readonly IAudioNormalizer? _audioNormalizer;
    private readonly ILogger<AudioImportService> _logger;

    public event EventHandler<AudioImportProgress>? ImportProgressChanged;

    /// <summary>
    /// Creates the service. <paramref name="audioNormalizer"/> is the same
    /// instance ingestion normalizes with; it is only consulted here to decide
    /// which files to accept (see <see cref="CanTranscode"/>).
    /// </summary>
    public AudioImportService(
        IDbContextFactory<AppDbContext> contextFactory,
        IQueueService queueService,
        ICaptureIngestionService captureIngestionService,
        ILogger<AudioImportService> logger,
        ISpeechToTextService? sttService = null,
        IEventExtractionService? extractionService = null,
        IAudioNormalizer? audioNormalizer = null)
    {
        _contextFactory = contextFactory;
        _queueService = queueService;
        _captureIngestionService = captureIngestionService;
        _sttService = sttService;
        _extractionService = extractionService;
        _audioNormalizer = audioNormalizer;
        _logger = logger;
    }

    /// <summary>
    /// Whether a compressed import can be made transcribable. True with a
    /// platform normalizer; false with none, or with the portable
    /// <see cref="WavOnlyAudioNormalizer"/>, which by definition converts
    /// nothing. The type test is deliberate: the port's shape carries no
    /// capability flag, and the one implementation that cannot transcode is
    /// the one Core itself ships.
    /// </summary>
    private bool CanTranscode => _audioNormalizer is not null and not WavOnlyAudioNormalizer;

    public async Task<AudioImportResult> ImportFilesAsync(
        IEnumerable<string> filePaths,
        AudioImportOptions? options = null,
        IProgress<AudioImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AudioImportOptions();
        var result = new AudioImportResult();
        var files = filePaths.ToList();
        result.TotalFiles = files.Count;

        _logger.LogInformation("Starting import of {Count} audio files", files.Count);

        for (int i = 0; i < files.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var filePath = files[i];
            var progressReport = new AudioImportProgress
            {
                CurrentItem = i + 1,
                TotalItems = files.Count,
                CurrentFileName = Path.GetFileName(filePath),
                OverallProgress = ((double)i / files.Count) * 100
            };

            try
            {
                // Check format
                if (!IsSupportedFormat(filePath, options))
                {
                    var skipped = new AudioImportItem
                    {
                        FilePath = filePath,
                        FileName = Path.GetFileName(filePath),
                        Status = AudioImportStatus.Skipped,
                        ErrorMessage = "Unsupported format"
                    };
                    result.Items.Add(skipped);
                    result.SkippedDuplicates++;
                    continue;
                }

                // Get file info
                progressReport.CurrentStatus = AudioImportStatus.Copying;
                progressReport.StatusMessage = "Reading file info...";
                ReportProgress(progress, progressReport);

                var importItem = await GetFileInfoAsync(filePath);

                // Check for duplicates
                if (options.SkipDuplicates)
                {
                    var isDuplicate = await CheckForDuplicateAsync(importItem);
                    if (isDuplicate)
                    {
                        importItem.Status = AudioImportStatus.Skipped;
                        importItem.ErrorMessage = "Duplicate file";
                        result.Items.Add(importItem);
                        result.SkippedDuplicates++;
                        continue;
                    }
                }

                // Check file size
                if (importItem.FileSize > options.MaxFileSizeBytes)
                {
                    importItem.Status = AudioImportStatus.Skipped;
                    importItem.ErrorMessage = $"File too large (max {options.MaxFileSizeBytes / 1024 / 1024}MB)";
                    result.Items.Add(importItem);
                    result.SkippedDuplicates++;
                    continue;
                }

                // Copy file to app data folder
                progressReport.StatusMessage = "Copying file...";
                progressReport.CurrentItemProgress = 25;
                ReportProgress(progress, progressReport);

                var destinationPath = await CopyToAppDataAsync(filePath, cancellationToken);

                // Add to queue
                progressReport.CurrentStatus = AudioImportStatus.Queued;
                progressReport.StatusMessage = "Adding to queue...";
                progressReport.CurrentItemProgress = 50;
                ReportProgress(progress, progressReport);

                var (queueItem, alreadyIngested) = await AddToQueueAsync(destinationPath, importItem);
                importItem.QueueItemId = queueItem.QueueId;

                if (alreadyIngested)
                {
                    // Ingestion's content-hash dedupe matched an existing queue
                    // item (same bytes under a different file name). Do NOT
                    // auto-process it again - that would duplicate pending
                    // events for an item that may already be reviewed.
                    importItem.Status = AudioImportStatus.Skipped;
                    importItem.ErrorMessage = "Duplicate content (already in queue)";
                    result.Items.Add(importItem);
                    result.SkippedDuplicates++;

                    // The freshly copied file is redundant; the queue item keeps
                    // pointing at its original artifact path.
                    TryDeleteFile(destinationPath);
                    continue;
                }

                importItem.Status = AudioImportStatus.Queued;

                // Auto-process if enabled
                if (options.AutoProcess && _sttService != null)
                {
                    progressReport.CurrentStatus = AudioImportStatus.Transcribing;
                    progressReport.StatusMessage = "Transcribing...";
                    progressReport.CurrentItemProgress = 60;
                    ReportProgress(progress, progressReport);

                    try
                    {
                        await ProcessQueueItemAsync(queueItem, options, progress, progressReport);
                        importItem.Status = AudioImportStatus.Completed;
                        result.QueuedForProcessing++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Auto-processing failed for {File}, will remain in queue", importItem.FileName);
                        importItem.Status = AudioImportStatus.Queued;
                    }
                }
                else
                {
                    result.QueuedForProcessing++;
                }

                // Delete source if requested
                if (options.DeleteSourceAfterImport && importItem.Status == AudioImportStatus.Completed)
                {
                    try
                    {
                        File.Delete(filePath);
                        _logger.LogInformation("Deleted source file: {Path}", filePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete source file: {Path}", filePath);
                    }
                }

                result.Items.Add(importItem);
                result.SuccessfulImports++;

                progressReport.CurrentItemProgress = 100;
                progressReport.StatusMessage = "Complete";
                ReportProgress(progress, progressReport);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing file: {Path}", filePath);

                var failed = new AudioImportItem
                {
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    Status = AudioImportStatus.Failed,
                    ErrorMessage = ex.Message
                };
                result.Items.Add(failed);
                result.FailedImports++;
                result.Errors.Add($"{Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        _logger.LogInformation(
            "Import complete: {Success} successful, {Failed} failed, {Skipped} skipped",
            result.SuccessfulImports, result.FailedImports, result.SkippedDuplicates);

        return result;
    }

    public async Task<List<AudioImportItem>> ScanDirectoryAsync(
        string directoryPath,
        AudioImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AudioImportOptions();
        var items = new List<AudioImportItem>();

        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("Directory not found: {Path}", directoryPath);
            return items;
        }

        var searchOption = options.RecursiveScan ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // Restrict the scan to the formats IsSupportedFormat accepts, so it
        // never lists a file that import would then reject.
        foreach (var format in options.SupportedFormats.Intersect(AcceptedFormats, StringComparer.OrdinalIgnoreCase))
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var files = Directory.GetFiles(directoryPath, $"*{format}", searchOption);
            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var item = await GetFileInfoAsync(file);
                    items.Add(item);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error scanning file: {Path}", file);
                }
            }
        }

        _logger.LogInformation("Found {Count} audio files in {Path}", items.Count, directoryPath);
        return items.OrderBy(i => i.FileCreatedAt).ToList();
    }

    public async Task<AudioImportResult> ImportDirectoryAsync(
        string directoryPath,
        AudioImportOptions? options = null,
        IProgress<AudioImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = await ScanDirectoryAsync(directoryPath, options, cancellationToken);
        var filePaths = items.Select(i => i.FilePath);
        return await ImportFilesAsync(filePaths, options, progress, cancellationToken);
    }

    // The bundled Whisper STT engine only parses 16 kHz PCM WAV input. With a
    // platform normalizer registered, ingestion transcodes every format in
    // AudioImportOptions.SupportedFormats to that layout before the item is
    // queued, so the whole advertised list is accepted. Without one, only
    // .wav is: a compressed file would import successfully and then fail at
    // transcription, which is a worse experience than "unsupported format"
    // at the picker.
    private static readonly string[] WavOnlyFormats = { ".wav" };

    /// <summary>The extensions this instance accepts, before the per-call options filter.</summary>
    private IReadOnlyList<string> AcceptedFormats =>
        CanTranscode ? new AudioImportOptions().SupportedFormats : WavOnlyFormats;

    public bool IsSupportedFormat(string filePath, AudioImportOptions? options = null)
    {
        options ??= new AudioImportOptions();
        var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
        return !string.IsNullOrEmpty(extension)
            && AcceptedFormats.Contains(extension, StringComparer.OrdinalIgnoreCase)
            && options.SupportedFormats.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AudioImportItem> GetFileInfoAsync(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var item = new AudioImportItem
        {
            FilePath = filePath,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            FileCreatedAt = fileInfo.CreationTimeUtc,
            Format = fileInfo.Extension.ToLowerInvariant()
        };

        // Duration is read straight out of the WAV header rather than from a
        // platform media-properties API, which keeps Core free of Windows
        // types. A compressed import has no duration until ingestion has
        // normalized it (ingestion fills the queue item's duration from the
        // normalized WAV); the picker shows "Unknown" for it until then.
        try
        {
            item.Duration = await Task.Run(() => WavHeader.TryRead(filePath)?.Duration);
            if (item.Duration <= TimeSpan.Zero)
            {
                item.Duration = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not get audio duration for: {Path}", filePath);
        }

        return item;
    }

    private async Task<bool> CheckForDuplicateAsync(AudioImportItem item)
    {
        // Check by file path containing filename and size in queue.
        // Use the async EF query directly rather than blocking a thread-pool thread via Task.Run.
        var fileName = item.FileName;

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        var existingInQueue = await dbContext.RecordingQueues.AnyAsync(q =>
            q.AudioFilePath.Contains(fileName) &&
            q.FileSizeBytes == item.FileSize);

        return existingInQueue;
    }

    private async Task<string> CopyToAppDataAsync(string sourcePath, CancellationToken cancellationToken)
    {
        // ApplicationData.Current requires MSIX package identity and throws in
        // the unpackaged configuration (WindowsPackageType=None). Use the plain
        // LocalApplicationData folder instead, mirroring AppDbContext.
        var recordingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MemoryTimeline",
            "AudioRecordings");

        Directory.CreateDirectory(recordingsFolder);

        var fileName = Path.GetFileName(sourcePath);
        var uniqueName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{fileName}";
        var destinationPath = Path.Combine(recordingsFolder, uniqueName);

        // Guarantee a unique destination even for multiple imports in the same second
        var attempt = 1;
        while (File.Exists(destinationPath))
        {
            destinationPath = Path.Combine(
                recordingsFolder,
                $"{DateTime.Now:yyyyMMdd_HHmmss}_{attempt++}_{fileName}");
        }

        await using (var sourceStream = File.OpenRead(sourcePath))
        await using (var destStream = File.Create(destinationPath))
        {
            await sourceStream.CopyToAsync(destStream, cancellationToken);
        }

        _logger.LogInformation("Copied audio file to: {Path}", destinationPath);
        return destinationPath;
    }

    private async Task<(RecordingQueue QueueItem, bool AlreadyIngested)> AddToQueueAsync(string filePath, AudioImportItem item)
    {
        // Route through capture ingestion (design §7.2) rather than inserting a
        // bare RecordingQueue row: the imported file gets the same capture +
        // artifact identity, content hash, and sync-outbox entry as an in-app
        // recording.
        var recording = new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = filePath,
            DurationSeconds = item.Duration?.TotalSeconds,
            FileSizeBytes = item.FileSize,
            CreatedAt = DateTime.UtcNow
        };

        var result = await _captureIngestionService.IngestLocalRecordingAsync(recording);
        if (!result.Success)
        {
            // The per-file catch in ImportFilesAsync turns this into a failed
            // import item for the file.
            throw new InvalidOperationException($"Failed to queue imported audio: {result.FailureReason}");
        }

        if (result.QueueId == null)
        {
            // Content matched a capture whose queue item was deliberately deleted;
            // ingestion does not resurrect it (§12.2).
            throw new InvalidOperationException("This audio was previously ingested and removed from the queue.");
        }

        var queueItem = await _queueService.GetQueueItemAsync(result.QueueId);
        if (queueItem == null)
        {
            throw new InvalidOperationException($"Queue item {result.QueueId} not found after ingestion.");
        }

        _logger.LogInformation("Added to queue: {Id} - {FileName}", queueItem.QueueId, Path.GetFileName(queueItem.AudioFilePath));
        return (queueItem, result.AlreadyIngested);
    }

    private async Task ProcessQueueItemAsync(
        RecordingQueue queueItem,
        AudioImportOptions options,
        IProgress<AudioImportProgress>? progress,
        AudioImportProgress progressReport)
    {
        if (_sttService == null)
            return;

        var fileName = Path.GetFileName(queueItem.AudioFilePath);

        // Status updates go through IQueueService so the fine-grained
        // processing_stage stays in lockstep with status (and status-changed
        // events reach the UI), exactly as when QueueService processes the item.
        await _queueService.UpdateQueueItemStatusAsync(queueItem.QueueId, QueueStatus.Processing,
            processingStage: QueueProcessingStage.Transcribing);

        try
        {
            // Transcribe
            _logger.LogInformation("Transcribing: {FileName}", fileName);
            var transcriptionResult = await _sttService.TranscribeAsync(queueItem.AudioFilePath);

            if (!transcriptionResult.Success || string.IsNullOrWhiteSpace(transcriptionResult.Text))
            {
                throw new InvalidOperationException(transcriptionResult.ErrorMessage ?? "Transcription returned empty result");
            }

            var transcript = transcriptionResult.Text;
            var extractedCount = 0;

            // Extract events if enabled - use the extraction service which handles creating pending events
            if (options.AutoExtract && _extractionService != null)
            {
                progressReport.CurrentStatus = AudioImportStatus.Extracting;
                progressReport.StatusMessage = "Extracting events...";
                progressReport.CurrentItemProgress = 80;
                ReportProgress(progress, progressReport);

                await _queueService.UpdateQueueItemStatusAsync(queueItem.QueueId, QueueStatus.Processing,
                    processingStage: QueueProcessingStage.Extracting);

                _logger.LogInformation("Extracting events from: {FileName}", fileName);
                var pendingEvents = await _extractionService.ExtractAndCreatePendingEventsAsync(queueItem.QueueId, transcript);
                extractedCount = pendingEvents.Count;
                _logger.LogInformation("Extracted {Count} events from: {FileName}", extractedCount, fileName);
            }

            await _queueService.UpdateQueueItemStatusAsync(queueItem.QueueId, QueueStatus.Completed,
                processingStage: extractedCount > 0
                    ? QueueProcessingStage.ReviewReady
                    : QueueProcessingStage.Completed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing queue item: {Id}", queueItem.QueueId);
            await _queueService.UpdateQueueItemStatusAsync(queueItem.QueueId, QueueStatus.Failed, ex.Message,
                processingStage: QueueProcessingStage.FailedRetryable);
            throw;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete redundant file: {Path}", path);
        }
    }

    private void ReportProgress(IProgress<AudioImportProgress>? progress, AudioImportProgress report)
    {
        progress?.Report(report);
        ImportProgressChanged?.Invoke(this, report);
    }
}
