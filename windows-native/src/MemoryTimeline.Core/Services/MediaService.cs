using CommunityToolkit.Mvvm.Messaging;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MetadataExtractor.Formats.Exif;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Pure, dependency-free rules for media attachments: the extension
/// allowlist -> <see cref="MediaType"/> mapping and the EXIF value
/// normalization. Kept separate from <see cref="MediaService"/> so tests can
/// exercise the logic without binary image fixtures.
/// </summary>
public static class MediaAttachmentRules
{
    private static readonly (string Extension, MediaType Type)[] AllowedExtensions =
    {
        (".jpg", MediaType.Image),
        (".jpeg", MediaType.Image),
        (".png", MediaType.Image),
        (".gif", MediaType.Image),
        (".bmp", MediaType.Image),
        (".webp", MediaType.Image),
        (".heic", MediaType.Image),
        (".mp4", MediaType.Video),
        (".mov", MediaType.Video),
        (".mp3", MediaType.Audio),
        (".wav", MediaType.Audio),
        (".m4a", MediaType.Audio),
        (".pdf", MediaType.Document),
        (".txt", MediaType.Document),
        (".md", MediaType.Document),
    };

    private static readonly Dictionary<string, MediaType> ExtensionMap =
        AllowedExtensions.ToDictionary(e => e.Extension, e => e.Type, StringComparer.OrdinalIgnoreCase);

    /// <summary>Human-readable allowlist for error messages.</summary>
    public static readonly string SupportedExtensionsDisplay =
        string.Join(", ", AllowedExtensions.Select(e => e.Extension));

    /// <summary>The image extensions of the allowlist (with leading dot), for file pickers.</summary>
    public static readonly IReadOnlyList<string> ImageExtensions =
        AllowedExtensions.Where(e => e.Type == MediaType.Image).Select(e => e.Extension).ToList();

    /// <summary>
    /// Maps a file extension (with or without leading dot, any casing) to its
    /// <see cref="MediaType"/>, or null when the extension is not allowlisted.
    /// </summary>
    public static MediaType? GetMediaTypeForExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
        {
            normalized = "." + normalized;
        }

        return ExtensionMap.TryGetValue(normalized, out var type) ? type : null;
    }

    /// <summary>
    /// Normalizes raw EXIF values into trustworthy ones:
    ///  - a default(DateTime) capture date is treated as absent;
    ///  - GPS coordinates are kept only when BOTH are present, finite, within
    ///    range (|lat| &lt;= 90, |lon| &lt;= 180) and not the (0, 0) "null
    ///    island" that cameras write when the fix was missing.
    /// </summary>
    public static (DateTime? CapturedAt, double? Latitude, double? Longitude) NormalizeExif(
        DateTime? capturedAt,
        double? latitude,
        double? longitude)
    {
        DateTime? normalizedDate =
            capturedAt.HasValue && capturedAt.Value != default ? capturedAt.Value : null;

        double? lat = latitude;
        double? lon = longitude;

        var coordinatesInvalid =
            !lat.HasValue || !lon.HasValue ||
            double.IsNaN(lat.Value) || double.IsNaN(lon.Value) ||
            double.IsInfinity(lat.Value) || double.IsInfinity(lon.Value) ||
            Math.Abs(lat.Value) > 90.0 || Math.Abs(lon.Value) > 180.0 ||
            (Math.Abs(lat.Value) < 1e-7 && Math.Abs(lon.Value) < 1e-7);

        if (coordinatesInvalid)
        {
            lat = null;
            lon = null;
        }

        return (normalizedDate, lat, lon);
    }
}

/// <summary>
/// Media attachment implementation. Copies files into the managed media tree
/// ({MediaRoot}\{yyyy}\{MM}\{guid}{ext} keyed on the EXIF capture date when
/// known, else the current UTC time), stores paths relative to the media
/// root, and generates 256px JPEG thumbnails into {MediaRoot}\.thumbs via the
/// injected <see cref="IThumbnailGenerator"/>.
/// Registered singleton; every DB operation uses a short-lived context or a
/// factory-based repository.
/// </summary>
public class MediaService : IMediaService
{
    /// <summary>Longest edge (px) of generated thumbnails.</summary>
    public const int ThumbnailLongestEdge = 256;

    /// <summary>
    /// Files created within this window are never deleted by
    /// <see cref="CleanupOrphansAsync"/>. <see cref="AttachAsync"/> copies a
    /// file into the managed tree BEFORE inserting its EventMedia row, so a
    /// just-copied file can look orphaned while its row is still on the way;
    /// 5 minutes comfortably covers the copy→hash→thumbnail→insert span of
    /// even a huge attachment. Internal so tests can age files past it.
    /// </summary>
    internal static readonly TimeSpan CleanupGraceWindow = TimeSpan.FromMinutes(5);

    private const string ThumbsDirectoryName = ".thumbs";

    /// <summary>Folder under the media root that holds people photos (see <see cref="ImportPersonPhotoAsync"/>).</summary>
    public const string PeoplePhotosDirectoryName = "people";

    private readonly IEventMediaRepository _mediaRepository;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IThumbnailGenerator _thumbnailGenerator;
    private readonly ITimelineProjectionPublisher? _projectionPublisher;
    private readonly IRecordingStorage? _recordingStorage;
    private readonly ILogger<MediaService> _logger;

    /// <summary>
    /// Creates the service. <paramref name="mediaRootOverride"/> replaces the
    /// default %LOCALAPPDATA%\MemoryTimeline\Media root - production leaves it
    /// null (the DI container cannot resolve a string, so the default kicks
    /// in); tests inject a temp directory.
    /// <paramref name="projectionPublisher"/> and <paramref name="recordingStorage"/>
    /// are optional and trailing so every existing construction keeps compiling:
    /// production picks up the registered implementations, and the many tests
    /// that predate them stay silent. With a recording storage, Clear Cache
    /// sweeps the recording trees as well as the media tree.
    /// </summary>
    public MediaService(
        IEventMediaRepository mediaRepository,
        IDbContextFactory<AppDbContext> contextFactory,
        IThumbnailGenerator thumbnailGenerator,
        ILogger<MediaService> logger,
        string? mediaRootOverride = null,
        ITimelineProjectionPublisher? projectionPublisher = null,
        IRecordingStorage? recordingStorage = null)
    {
        _mediaRepository = mediaRepository;
        _contextFactory = contextFactory;
        _thumbnailGenerator = thumbnailGenerator;
        _projectionPublisher = projectionPublisher;
        _recordingStorage = recordingStorage;
        _logger = logger;

        MediaRoot = string.IsNullOrWhiteSpace(mediaRootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MemoryTimeline",
                "Media")
            : mediaRootOverride;
    }

    /// <inheritdoc />
    public string MediaRoot { get; }

    /// <inheritdoc />
    public Task<EventMedia> AttachAsync(
        string eventId,
        string sourceFilePath,
        string? caption = null,
        CancellationToken ct = default)
        => AttachCoreAsync(eventId, sourceFilePath, caption, publishProjection: true, ct);

    /// <summary>
    /// The whole of <see cref="AttachAsync"/>, with one knob:
    /// <paramref name="publishProjection"/> lets <see cref="AttachManyAsync"/>
    /// attach forty photos and tell the sync feed once. Every other caller wants
    /// the publish, so the public method hard-codes it true.
    /// </summary>
    private async Task<EventMedia> AttachCoreAsync(
        string eventId,
        string sourceFilePath,
        string? caption,
        bool publishProjection,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("An event id is required.", nameof(eventId));
        }

        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new ArgumentException("A source file path is required.", nameof(sourceFilePath));
        }

        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException(
                $"The file could not be found: {sourceFilePath}", sourceFilePath);
        }

        var extension = Path.GetExtension(sourceFilePath);
        var mediaType = MediaAttachmentRules.GetMediaTypeForExtension(extension)
            ?? throw new InvalidOperationException(
                $"'{Path.GetFileName(sourceFilePath)}' is not a supported file type. " +
                $"Supported types: {MediaAttachmentRules.SupportedExtensionsDisplay}.");

        await using (var context = await _contextFactory.CreateDbContextAsync(ct))
        {
            if (!await context.Events.AnyAsync(e => e.EventId == eventId, ct))
            {
                throw new InvalidOperationException($"Event not found: {eventId}");
            }
        }

        var contentHash = await ComputeSha256Async(sourceFilePath, ct);

        // Attaching the same content to the SAME event twice is a no-op; the
        // same file may still be attached to a DIFFERENT event.
        var existing = await _mediaRepository.GetByHashForEventAsync(eventId, contentHash);
        if (existing != null)
        {
            _logger.LogInformation(
                "File '{File}' is already attached to event {EventId} (media {MediaId}); skipping duplicate.",
                Path.GetFileName(sourceFilePath), eventId, existing.MediaId);
            return existing;
        }

        var fileSizeBytes = new FileInfo(sourceFilePath).Length;

        DateTime? capturedAt = null;
        double? latitude = null;
        double? longitude = null;
        if (mediaType == MediaType.Image)
        {
            (capturedAt, latitude, longitude) = TryReadImageMetadata(sourceFilePath);
        }

        // Managed tree: {MediaRoot}\{yyyy}\{MM}\{guid}{ext}, folder keyed on
        // the capture date when known, else now (UTC).
        var mediaId = Guid.NewGuid().ToString();
        var anchor = capturedAt ?? DateTime.UtcNow;
        var relativeDirectory = Path.Combine(
            anchor.Year.ToString("D4", CultureInfo.InvariantCulture),
            anchor.Month.ToString("D2", CultureInfo.InvariantCulture));
        var relativePath = Path.Combine(relativeDirectory, mediaId + extension.ToLowerInvariant());
        var absolutePath = Path.Combine(MediaRoot, relativePath);

        try
        {
            // Directories are created lazily - the media root does not exist
            // until the first attachment.
            Directory.CreateDirectory(Path.Combine(MediaRoot, relativeDirectory));
            File.Copy(sourceFilePath, absolutePath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A read-only or otherwise unusable media root must surface as a
            // clear, user-presentable error - never a crash or a retry loop.
            throw new InvalidOperationException(
                $"Could not copy '{Path.GetFileName(sourceFilePath)}' into the media library at '{MediaRoot}': {ex.Message}",
                ex);
        }

        // Thumbnail (images only). Failure is logged and tolerated - the
        // attachment is still valid without one.
        string? thumbnailRelativePath = null;
        if (mediaType == MediaType.Image)
        {
            var thumbRelative = Path.Combine(ThumbsDirectoryName, mediaId + ".jpg");
            var thumbAbsolute = Path.Combine(MediaRoot, thumbRelative);
            try
            {
                Directory.CreateDirectory(Path.Combine(MediaRoot, ThumbsDirectoryName));
                if (await _thumbnailGenerator.TryGenerateAsync(absolutePath, thumbAbsolute, ThumbnailLongestEdge, ct))
                {
                    thumbnailRelativePath = thumbRelative;
                }
                else
                {
                    _logger.LogWarning(
                        "Thumbnail generation failed for {Path}; the attachment is still valid.", absolutePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Thumbnail generation threw for {Path}; the attachment is still valid.", absolutePath);
            }
        }

        var sortOrder = await _mediaRepository.MaxSortOrderAsync(eventId) + 1;

        var media = new EventMedia
        {
            MediaId = mediaId,
            EventId = eventId,
            FilePath = relativePath,
            ThumbnailPath = thumbnailRelativePath,
            MediaType = mediaType,
            Caption = NormalizeCaption(caption),
            CapturedAt = capturedAt,
            Latitude = latitude,
            Longitude = longitude,
            FileSizeBytes = fileSizeBytes,
            ContentHash = contentHash,
            SortOrder = sortOrder,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _mediaRepository.AddAsync(media);
        }
        catch
        {
            // Don't strand orphan files when the row could not be persisted.
            TryDeleteFile(absolutePath);
            if (thumbnailRelativePath != null)
            {
                TryDeleteFile(Path.Combine(MediaRoot, thumbnailRelativePath));
            }

            throw;
        }

        _logger.LogInformation(
            "Attached media {MediaId} ({MediaType}) to event {EventId}.", mediaId, mediaType, eventId);

        // EXIF GPS backfill (F10): a photo that knows where it was taken fills
        // in the coordinates of the event's linked locations that have none.
        // A backfill failure never turns a successful attach into an error.
        if (latitude.HasValue && longitude.HasValue)
        {
            try
            {
                await BackfillEventLocationCoordinatesAsync(eventId, latitude.Value, longitude.Value, ct);
            }
            catch (Exception backfillEx)
            {
                _logger.LogError(backfillEx,
                    "EXIF coordinate backfill failed for event {EventId}; the attachment itself succeeded.",
                    eventId);
            }
        }

        // Notify subscribers (timeline badge/strip). A subscriber failure must
        // not turn a successful attach into an error.
        try
        {
            WeakReferenceMessenger.Default.Send(new MediaAttachedMessage(eventId, mediaId));
        }
        catch (Exception messengerEx)
        {
            _logger.LogWarning(messengerEx,
                "Error publishing MediaAttachedMessage for media {MediaId}", mediaId);
        }

        // An event projection denormalises a media COUNT, so a companion's photo
        // badge is wrong from here until someone republishes the event (design
        // §19 Phase 3). Published last so the one payload also carries whatever
        // the EXIF backfill above changed: the projection's map pin comes from
        // the event's linked locations, and the backfill may have just given one
        // coordinates. Locations are shared rows, so OTHER events linked to a
        // backfilled location moved too; the backfill republishes those itself.
        // The duplicate-hash early return above wrote nothing and returns before
        // reaching this.
        if (publishProjection)
        {
            await PublishEventProjectionAsync(eventId);
        }

        return media;
    }

    /// <inheritdoc />
    public async Task<List<EventMedia>> AttachManyAsync(
        string eventId,
        IEnumerable<string> paths,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var pathList = (paths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var attached = new List<EventMedia>();
        if (pathList.Count == 0)
        {
            return attached;
        }

        var errors = new List<string>();
        var done = 0;

        try
        {
            foreach (var path in pathList)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    attached.Add(await AttachCoreAsync(eventId, path, caption: null, publishProjection: false, ct));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Keep going: one unreadable file must not abort the batch.
                    _logger.LogError(ex, "Error attaching '{Path}' to event {EventId}", path, eventId);
                    errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }

                done++;
                progress?.Report((done, pathList.Count));
            }
        }
        finally
        {
            // One publish for the batch rather than one per file. Every row here
            // lands on the SAME event, so a forty-photo drop is one badge change;
            // publishing per file would queue thirty-nine payloads that are
            // obsolete before a phone can draw them.
            // In a finally because the rows committed as they went: a cancelled
            // batch, or one that ends by throwing its failure summary, has still
            // changed the count, and the caller seeing an exception is no reason
            // to leave a companion showing the old one. The gate is "did this
            // call put anything on the event", not a guess at whether the change
            // was worth sending - the publisher decides that.
            if (attached.Count > 0)
            {
                await PublishEventProjectionAsync(eventId);
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"{errors.Count} of {pathList.Count} file(s) could not be attached. {string.Join(" ", errors)}");
        }

        return attached;
    }

    /// <inheritdoc />
    public async Task<string> ImportPersonPhotoAsync(string sourceFilePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("The chosen photo could not be found.", sourceFilePath);
        }

        var extension = Path.GetExtension(sourceFilePath);
        if (MediaAttachmentRules.GetMediaTypeForExtension(extension) != MediaType.Image)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(sourceFilePath)}' is not a supported image. Use one of: " +
                string.Join(", ", MediaAttachmentRules.ImageExtensions) + ".");
        }

        var relativePath = Path.Combine(PeoplePhotosDirectoryName, Guid.NewGuid().ToString("N") + extension.ToLowerInvariant());
        var absolutePath = Path.Combine(MediaRoot, relativePath);

        try
        {
            Directory.CreateDirectory(Path.Combine(MediaRoot, PeoplePhotosDirectoryName));
            await using var source = File.OpenRead(sourceFilePath);
            await using var destination = new FileStream(absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, ct);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(absolutePath);
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            TryDeleteFile(absolutePath);
            throw new InvalidOperationException(
                $"Could not copy '{Path.GetFileName(sourceFilePath)}' into the media library at '{MediaRoot}': {ex.Message}", ex);
        }

        _logger.LogInformation("Imported person photo {Source} as {Relative}", sourceFilePath, relativePath);
        return relativePath;
    }

    /// <inheritdoc />
    public string? GetAbsolutePersonPhotoPath(string? photoPath)
    {
        if (string.IsNullOrWhiteSpace(photoPath))
        {
            return null;
        }

        return Path.IsPathRooted(photoPath) ? photoPath : Path.Combine(MediaRoot, photoPath);
    }

    /// <inheritdoc />
    public Task<List<EventMedia>> GetForEventAsync(string eventId)
        => _mediaRepository.GetForEventAsync(eventId);

    /// <inheritdoc />
    public async Task RemoveAsync(string mediaId, bool deleteFile = true)
    {
        var media = await _mediaRepository.GetByIdAsync(mediaId);
        if (media == null)
        {
            _logger.LogWarning("RemoveAsync: media {MediaId} not found; nothing to remove.", mediaId);
            return;
        }

        await _mediaRepository.DeleteAsync(media);

        if (deleteFile)
        {
            DeleteFiles(new[] { media });
        }

        _logger.LogInformation(
            "Removed media {MediaId} from event {EventId} (deleteFile={DeleteFile}).",
            mediaId, media.EventId, deleteFile);

        // The count a companion badges the event with came from this row
        // existing; deleteFile only decides the file's fate, never the row's, so
        // both variants publish. The missing-row early return above changed
        // nothing and returned before here.
        await PublishEventProjectionAsync(media.EventId);
    }

    /// <inheritdoc />
    public async Task UpdateCaptionAsync(string mediaId, string? caption)
    {
        var media = await _mediaRepository.GetByIdAsync(mediaId)
            ?? throw new InvalidOperationException(
                "The photo could not be found. It may have been removed.");

        media.Caption = NormalizeCaption(caption);
        await _mediaRepository.UpdateAsync(media);

        // No projection publish here, and none in ReorderAsync. The event
        // payload denormalises a media COUNT and nothing else about the
        // attachments - no captions, no order, no thumbnails - so neither write
        // is visible to a companion, and a publish would only rebuild a payload
        // the publisher then drops as unchanged. The asymmetry with attach and
        // remove is the contract's shape, not a missed call site; it stops being
        // true the day the projection carries a media list.
    }

    /// <inheritdoc />
    /// <remarks>
    /// Publishes no projection - see <see cref="UpdateCaptionAsync"/> for why
    /// sort order does not cross the wire.
    /// </remarks>
    public async Task ReorderAsync(string eventId, IReadOnlyList<string> mediaIdsInOrder)
    {
        var media = await _mediaRepository.GetForEventAsync(eventId);
        if (media.Count == 0)
        {
            return;
        }

        var desiredOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < mediaIdsInOrder.Count; i++)
        {
            desiredOrder.TryAdd(mediaIdsInOrder[i], i);
        }

        // Ids not in the list keep their relative order after the listed ones.
        var next = mediaIdsInOrder.Count;
        foreach (var item in media.Where(m => !desiredOrder.ContainsKey(m.MediaId)).OrderBy(m => m.SortOrder))
        {
            desiredOrder[item.MediaId] = next++;
        }

        foreach (var item in media)
        {
            var desired = desiredOrder[item.MediaId];
            if (item.SortOrder == desired)
            {
                continue;
            }

            item.SortOrder = desired;
            await _mediaRepository.UpdateAsync(item);
        }
    }

    /// <inheritdoc />
    public Task<MediaProbeResult> ProbeAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException($"The file could not be found: {filePath}", filePath);
        }

        // File IO + metadata parsing off the caller's (possibly UI) thread.
        return Task.Run(() =>
        {
            var mediaType = MediaAttachmentRules.GetMediaTypeForExtension(Path.GetExtension(filePath));
            var result = new MediaProbeResult
            {
                MediaType = mediaType,
                FileSizeBytes = new FileInfo(filePath).Length
            };

            if (mediaType == MediaType.Image)
            {
                var (capturedAt, latitude, longitude) = TryReadImageMetadata(filePath);
                result.CapturedAt = capturedAt;
                result.Latitude = latitude;
                result.Longitude = longitude;
            }

            return result;
        });
    }

    /// <inheritdoc />
    public string GetAbsolutePath(EventMedia media)
        => Path.Combine(MediaRoot, media.FilePath);

    /// <inheritdoc />
    public string? GetAbsoluteThumbnailPath(EventMedia media)
        => string.IsNullOrEmpty(media.ThumbnailPath) ? null : Path.Combine(MediaRoot, media.ThumbnailPath);

    /// <inheritdoc />
    public void DeleteFiles(IEnumerable<EventMedia> media)
    {
        foreach (var item in media)
        {
            TryDeleteFile(GetAbsolutePath(item));

            var thumbnail = GetAbsoluteThumbnailPath(item);
            if (thumbnail != null)
            {
                TryDeleteFile(thumbnail);
            }
        }
    }

    /// <inheritdoc />
    public async Task<MediaCleanupResult> CleanupOrphansAsync(CancellationToken ct = default)
    {
        var result = new MediaCleanupResult();

        if (System.IO.Directory.Exists(MediaRoot))
        {
            await SweepMediaTreeAsync(result, ct);
        }

        // Recording files are orphaned by queue removals that predate the
        // release-on-remove rule, by failed ingestions, and by downloads whose
        // ingestion was deduplicated; the same Clear Cache action collects them.
        if (_recordingStorage != null)
        {
            var recordings = await _recordingStorage.CleanupOrphansAsync(ct);
            result.RecordingsDeleted = recordings.FilesDeleted;
            result.FilesDeleted += recordings.FilesDeleted;
            result.BytesFreed += recordings.BytesFreed;
        }

        return result;
    }

    private async Task SweepMediaTreeAsync(MediaCleanupResult result, CancellationToken ct)
    {

        // Snapshot ORDER matters: materialize the FILE list first, the DB row
        // sets second. AttachAsync copies a file into the managed tree BEFORE
        // inserting its EventMedia row, so with this order a file that appears
        // after the file snapshot is never even considered, and a row that
        // commits before the row query still protects its file. The reverse
        // order (rows first, lazy file walk second) could delete a
        // just-attached file whose row commits — permanent media loss.
        // NOTE: this scan covers the managed media tree only. Export .tmp
        // leftovers live wherever the user pointed the export/backup pickers
        // (arbitrary folders we must not sweep - an in-progress backup's .tmp
        // could be live there); cleaning those is deliberately deferred.
        var files = System.IO.Directory
            .EnumerateFiles(MediaRoot, "*", SearchOption.AllDirectories)
            .ToList();

        // Referenced paths from the database, normalized to forward slashes
        // (rows store OS-native separators via Path.Combine).
        HashSet<string> liveFiles;
        HashSet<string> liveThumbnails;
        await using (var context = await _contextFactory.CreateDbContextAsync(ct))
        {
            var rows = await context.EventMedia
                .AsNoTracking()
                .Select(m => new { m.FilePath, m.ThumbnailPath })
                .ToListAsync(ct);

            liveFiles = rows
                .Select(r => NormalizeRelativePath(r.FilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            liveThumbnails = rows
                .Where(r => !string.IsNullOrEmpty(r.ThumbnailPath))
                .Select(r => NormalizeRelativePath(r.ThumbnailPath!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // People photos live under the same root (people/{guid}{ext}) and
            // are referenced by people.photo_path, not event_media: keep every
            // file a person row still points at. A stored absolute path under
            // the root is mapped back to its relative form first.
            var peoplePhotos = await context.People
                .AsNoTracking()
                .Where(p => p.PhotoPath != null && p.PhotoPath != "")
                .Select(p => p.PhotoPath!)
                .ToListAsync(ct);
            foreach (var photo in peoplePhotos)
            {
                var relativePhoto = Path.IsPathRooted(photo) ? Path.GetRelativePath(MediaRoot, photo) : photo;
                liveFiles.Add(NormalizeRelativePath(relativePhoto));
            }
        }

        var graceCutoffUtc = DateTime.UtcNow - CleanupGraceWindow;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // Grace window (belt and braces with the snapshot order above):
            // never touch a file created moments ago - it may belong to an
            // attach whose row has not committed yet. A vanished file reports
            // an ancient creation time and simply falls through to the
            // tolerant delete below.
            if (File.GetCreationTimeUtc(file) > graceCutoffUtc)
            {
                continue;
            }

            var relative = NormalizeRelativePath(Path.GetRelativePath(MediaRoot, file));
            var isTemp = relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            var isThumbnail = relative.StartsWith(ThumbsDirectoryName + "/", StringComparison.OrdinalIgnoreCase);

            var isOrphan = isTemp
                || (isThumbnail && !liveThumbnails.Contains(relative))
                || (!isThumbnail && !liveFiles.Contains(relative));
            if (!isOrphan)
            {
                continue;
            }

            try
            {
                var sizeBytes = new FileInfo(file).Length;
                File.Delete(file);

                result.FilesDeleted++;
                result.BytesFreed += sizeBytes;
                if (isTemp)
                {
                    result.TempFilesDeleted++;
                }
                else if (isThumbnail)
                {
                    result.ThumbnailsDeleted++;
                }
                else
                {
                    result.MediaFilesDeleted++;
                }
            }
            catch (Exception ex)
            {
                // A locked/vanished file must not abort the sweep.
                _logger.LogWarning(ex, "Could not delete orphaned file {Path} during cleanup.", file);
            }
        }

        _logger.LogInformation(
            "Media cleanup: deleted {Files} file(s) ({Bytes} bytes) - {Media} orphaned media, {Thumbs} orphaned thumbnails, {Temp} temp leftovers.",
            result.FilesDeleted, result.BytesFreed,
            result.MediaFilesDeleted, result.ThumbnailsDeleted, result.TempFilesDeleted);

        // No projections, and no event ids to gather for them: this sweep - like
        // DeleteFiles - deletes FILES and never an event_media row, the isOrphan
        // test above skips every file a row still references, and the count an
        // event projection carries is a count of rows. Every row that vanished
        // to make these files orphans went through RemoveAsync or an event
        // delete, both of which already published. A file deleted here has no
        // event id to attribute it to anyway; publishing "everything" to be safe
        // would flood the outbox to say nothing.
    }

    /// <summary>
    /// Republishes the event onto the sync feed after its media set changed, so
    /// a companion's photo badge stops disagreeing with the archive (design §19
    /// Phase 3). Callers invoke this only AFTER their row write has committed -
    /// the publisher re-reads the event to count its media, so it has to see the
    /// new state.
    ///
    /// Deliberately takes no CancellationToken. Every call reports rows that are
    /// already durable, and a cancelled batch that skipped its publish would
    /// leave the feed stale for as long as nothing else touched the event -
    /// cancellation should stop work still to come, not suppress the report of
    /// work already done.
    ///
    /// A publish failure is warned and swallowed: the attachment is the user's
    /// photo, the projection is a badge on someone else's screen, and no outbox
    /// problem is worth failing the former for. The next media change
    /// republishes.
    /// </summary>
    private async Task PublishEventProjectionAsync(string eventId)
    {
        if (_projectionPublisher == null)
        {
            return;
        }

        try
        {
            await _projectionPublisher.PublishEventAsync(eventId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish the timeline projection for event {EventId}", eventId);
        }
    }

    /// <summary>
    /// Normalizes a media-root-relative path for comparison: forward slashes,
    /// trimmed. Rows written on Windows carry backslashes; the scan compares
    /// case-insensitively (NTFS semantics).
    /// </summary>
    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').Trim();

    /// <summary>
    /// Fills the photo's EXIF GPS coordinates into every location linked to
    /// the event that has no coordinates yet (Latitude or Longitude null).
    /// Locations that already have coordinates are never overwritten, and
    /// <see cref="Data.Models.Location.GeocodedAt"/> stays null - these
    /// coordinates came from a photo, not a geocoder. Internal (not on
    /// <see cref="IMediaService"/>) so tests can exercise the rule without a
    /// binary GPS-tagged image fixture; production reaches it only through
    /// <see cref="AttachAsync"/>.
    ///
    /// <para>A location is a shared row: every other event linked to one that
    /// just gained coordinates now has a map pin a companion has not seen, so
    /// those events are republished here (the attaching event is published by
    /// the attach itself, after the media count has moved too). Best-effort,
    /// like every projection publish - the coordinates are already saved.</para>
    /// </summary>
    /// <returns>The ids of the OTHER events whose projections were republished.</returns>
    internal async Task<IReadOnlyList<string>> BackfillEventLocationCoordinatesAsync(
        string eventId,
        double latitude,
        double longitude,
        CancellationToken ct = default)
    {
        List<string> otherLinkedEventIds;
        await using (var context = await _contextFactory.CreateDbContextAsync(ct))
        {
            var missingCoordinates = await context.Locations
                .Where(l => l.EventLocations.Any(el => el.EventId == eventId))
                .Where(l => l.Latitude == null || l.Longitude == null)
                .ToListAsync(ct);

            if (missingCoordinates.Count == 0)
            {
                return Array.Empty<string>();
            }

            foreach (var location in missingCoordinates)
            {
                location.Latitude = latitude;
                location.Longitude = longitude;
                location.GeocodedAt = null; // photo-sourced, not geocoded

                _logger.LogInformation(
                    "Backfilled coordinates ({Latitude}, {Longitude}) for location '{Name}' ({LocationId}) from photo EXIF GPS on event {EventId}.",
                    latitude, longitude, location.Name, location.LocationId, eventId);
            }

            await context.SaveChangesAsync(ct);

            var backfilledIds = missingCoordinates.Select(l => l.LocationId).ToList();
            otherLinkedEventIds = await context.EventLocations
                .AsNoTracking()
                .Where(el => backfilledIds.Contains(el.LocationId) && el.EventId != eventId)
                .Select(el => el.EventId)
                .Distinct()
                .ToListAsync(ct);
        }

        // Fan out AFTER the context is disposed and the rows are committed: the
        // publisher re-reads each event through its own connection.
        foreach (var otherEventId in otherLinkedEventIds)
        {
            await PublishEventProjectionAsync(otherEventId);
        }

        return otherLinkedEventIds;
    }

    /// <summary>
    /// Reads EXIF DateTimeOriginal and GPS coordinates from an image via
    /// MetadataExtractor, normalized through
    /// <see cref="MediaAttachmentRules.NormalizeExif"/>. Any parse failure
    /// (corrupt file, unsupported container) is logged and yields nulls - the
    /// attachment itself must not fail over metadata.
    /// </summary>
    private (DateTime? CapturedAt, double? Latitude, double? Longitude) TryReadImageMetadata(string path)
    {
        try
        {
            // NOTE: ImageMetadataReader and the TryGetDateTime extension are
            // invoked fully qualified - `using MetadataExtractor;` would make
            // `Directory` ambiguous with System.IO.Directory in this file.
            var directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(path);

            DateTime? capturedAt = null;
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd != null &&
                MetadataExtractor.DirectoryExtensions.TryGetDateTime(
                    subIfd, ExifDirectoryBase.TagDateTimeOriginal, out var dateTimeOriginal))
            {
                capturedAt = dateTimeOriginal;
            }

            double? latitude = null;
            double? longitude = null;
            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
            var location = gps?.GetGeoLocation();
            if (location != null)
            {
                latitude = location.Latitude;
                longitude = location.Longitude;
            }

            return MediaAttachmentRules.NormalizeExif(capturedAt, latitude, longitude);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read image metadata from {Path}; attaching without EXIF data.", path);
            return (null, null, null);
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeCaption(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return null;
        }

        var trimmed = caption.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
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
            _logger.LogWarning(ex, "Could not delete media file {Path}.", path);
        }
    }
}
