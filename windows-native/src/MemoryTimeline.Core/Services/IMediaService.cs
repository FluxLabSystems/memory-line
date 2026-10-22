using MemoryTimeline.Data.Models;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Manages media attachments for events. Files are COPIED into a managed
/// tree under the media root (%LOCALAPPDATA%\MemoryTimeline\Media by default,
/// mirroring AudioRecordingService's managed-directory pattern) - the source
/// file is never referenced in place and never modified. The database stores
/// paths RELATIVE to the media root; use <see cref="GetAbsolutePath"/> /
/// <see cref="GetAbsoluteThumbnailPath"/> to resolve them for display.
/// </summary>
public interface IMediaService
{
    /// <summary>Absolute path of the managed media root (created lazily on first attach).</summary>
    string MediaRoot { get; }

    /// <summary>
    /// Copies a chosen image into the managed tree's people-photos folder
    /// (<c>{MediaRoot}/people/{guid}{ext}</c>) and returns the path to store
    /// in <c>people.photo_path</c>, RELATIVE to the media root like
    /// <see cref="EventMedia.FilePath"/> — so backups of the media tree carry
    /// it and the tree can move. The Clear Cache sweep keeps every file a
    /// person row references; a photo that no person references any more
    /// (replaced, or its person deleted) is swept as an orphan. Throws
    /// <see cref="FileNotFoundException"/> for a missing source and
    /// <see cref="InvalidOperationException"/> (user-presentable) for a
    /// non-image extension.
    /// </summary>
    Task<string> ImportPersonPhotoAsync(string sourceFilePath, CancellationToken ct = default);

    /// <summary>
    /// Resolves a stored <c>people.photo_path</c> for display: a media-root-
    /// relative path is combined with the root, an absolute path is returned
    /// as-is, and null/blank yields null.
    /// </summary>
    string? GetAbsolutePersonPhotoPath(string? photoPath);

    /// <summary>
    /// Attaches a file to an event: validates the extension allowlist,
    /// verifies the event exists, hashes the content (attaching a file whose
    /// hash is already attached to the SAME event is a no-op returning the
    /// existing row), copies the file into the managed tree, reads EXIF
    /// capture date/GPS for images, generates a thumbnail (failure is logged
    /// and tolerated), persists the row with the next sort order and
    /// publishes <see cref="MediaAttachedMessage"/>.
    /// Failures throw with a user-presentable (InfoBar-ready) message.
    /// </summary>
    Task<EventMedia> AttachAsync(string eventId, string sourceFilePath, string? caption = null, CancellationToken ct = default);

    /// <summary>
    /// Attaches many files, reporting (done, total) progress after each file.
    /// Individual failures do not stop the batch; if any file failed, an
    /// exception listing the failures is thrown AFTER the rest were attached.
    /// </summary>
    Task<List<EventMedia>> AttachManyAsync(string eventId, IEnumerable<string> paths, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default);

    /// <summary>Gets the event's media ordered by sort order.</summary>
    Task<List<EventMedia>> GetForEventAsync(string eventId);

    /// <summary>
    /// Removes a media row (a missing row is a logged no-op) and, when
    /// <paramref name="deleteFile"/> is true, best-effort deletes the managed
    /// file and its thumbnail.
    /// </summary>
    Task RemoveAsync(string mediaId, bool deleteFile = true);

    /// <summary>Updates the caption (trimmed; blank clears it). Throws when the row is missing.</summary>
    Task UpdateCaptionAsync(string mediaId, string? caption);

    /// <summary>
    /// Persists a new display order for the event's media. Ids missing from
    /// the list keep their relative order after the listed ones.
    /// </summary>
    Task ReorderAsync(string eventId, IReadOnlyList<string> mediaIdsInOrder);

    /// <summary>
    /// Reads file metadata WITHOUT importing: media type (null when the
    /// extension is unsupported), file size, and - for images - the EXIF
    /// capture date and GPS coordinates (null island treated as absent).
    /// </summary>
    Task<MediaProbeResult> ProbeAsync(string filePath);

    /// <summary>Resolves the stored relative file path against the media root.</summary>
    string GetAbsolutePath(EventMedia media);

    /// <summary>Resolves the stored relative thumbnail path, or null when there is no thumbnail.</summary>
    string? GetAbsoluteThumbnailPath(EventMedia media);

    /// <summary>
    /// Best-effort deletion of the physical files (file + thumbnail) for rows
    /// that are already gone from the database - e.g. after the event-delete
    /// FK cascade, which removes the rows but cannot touch the files.
    /// Failures are logged, never thrown.
    /// </summary>
    void DeleteFiles(IEnumerable<EventMedia> media);

    /// <summary>
    /// Maintenance scan (Settings "Clear Cache"): deletes thumbnails with no
    /// referencing event_media row, managed media files with no row, *.tmp
    /// leftovers, and — through <see cref="IRecordingStorage"/> when one is
    /// registered — recording files under the recording trees that no queue
    /// item, pending event, event or capture artifact references, reporting
    /// the files deleted and the ACTUAL bytes freed. Files referenced by the
    /// database are never touched, and files created within the last few
    /// minutes are skipped (an in-flight attach or ingestion writes its file
    /// before committing the row) - they are swept on a later run if truly
    /// orphaned. Per-file deletion failures are logged and skipped; the scan
    /// itself throws only on database failure.
    /// </summary>
    Task<MediaCleanupResult> CleanupOrphansAsync(CancellationToken ct = default);
}

/// <summary>Result of <see cref="IMediaService.CleanupOrphansAsync"/>.</summary>
public class MediaCleanupResult
{
    /// <summary>Total files deleted (media + thumbnails + temp files).</summary>
    public int FilesDeleted { get; set; }

    /// <summary>Actual bytes freed (sizes measured before deletion).</summary>
    public long BytesFreed { get; set; }

    /// <summary>Orphaned managed media files deleted.</summary>
    public int MediaFilesDeleted { get; set; }

    /// <summary>Orphaned thumbnails deleted.</summary>
    public int ThumbnailsDeleted { get; set; }

    /// <summary>*.tmp leftovers deleted.</summary>
    public int TempFilesDeleted { get; set; }

    /// <summary>Orphaned recording files deleted under the recording trees (included in <see cref="FilesDeleted"/>).</summary>
    public int RecordingsDeleted { get; set; }
}

/// <summary>
/// Result of <see cref="IMediaService.ProbeAsync"/>: metadata read from a file
/// without importing it.
/// </summary>
public class MediaProbeResult
{
    /// <summary>The media type implied by the extension, or null when unsupported.</summary>
    public MediaType? MediaType { get; set; }

    /// <summary>EXIF DateTimeOriginal for images, if present.</summary>
    public DateTime? CapturedAt { get; set; }

    /// <summary>EXIF GPS latitude, if present (null island treated as absent).</summary>
    public double? Latitude { get; set; }

    /// <summary>EXIF GPS longitude, if present (null island treated as absent).</summary>
    public double? Longitude { get; set; }

    /// <summary>Size of the file in bytes.</summary>
    public long FileSizeBytes { get; set; }
}
