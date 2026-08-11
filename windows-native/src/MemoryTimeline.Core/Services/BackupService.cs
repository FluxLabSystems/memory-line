using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Data;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// .mtbak backup/restore implementation.
///
/// Archive layout (zip):
///   database.db   - WAL-safe snapshot of the SQLite database, captured with
///                   the online backup API (never a raw File.Copy of the live
///                   file, which would miss/tear -wal contents)
///   manifest.json - { formatVersion, appVersion, createdAtUtc, eventCount,
///                     includesMedia, includesAudio }
///   media/**      - the managed media tree (optional)
///   audio/**      - the managed audio-recordings tree (optional)
///
/// The archive is written to "&lt;destination&gt;.tmp" and moved into place on
/// success so a torn .mtbak can never exist; the .tmp is deleted in a finally
/// on every failure/cancel path.
/// Registered singleton; every DB operation uses a short-lived context or a
/// raw, non-pooled SQLite connection.
/// </summary>
public class BackupService : IBackupService
{
    private const int CurrentFormatVersion = 1;
    private const string DatabaseEntryName = "database.db";
    private const string ManifestEntryName = "manifest.json";
    private const string MediaEntryPrefix = "media/";
    private const string AudioEntryPrefix = "audio/";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ISettingsService _settingsService;
    private readonly IMediaService _mediaService;
    private readonly ILogger<BackupService> _logger;
    private readonly string _audioRoot;

    /// <summary>
    /// Creates the service. <paramref name="audioRootOverride"/> replaces the
    /// default %LOCALAPPDATA%\MemoryTimeline\AudioRecordings root (mirroring
    /// AudioRecordingService's managed directory) - production leaves it null;
    /// tests inject a temp directory.
    /// </summary>
    public BackupService(
        IDbContextFactory<AppDbContext> contextFactory,
        ISettingsService settingsService,
        IMediaService mediaService,
        ILogger<BackupService> logger,
        string? audioRootOverride = null)
    {
        _contextFactory = contextFactory;
        _settingsService = settingsService;
        _mediaService = mediaService;
        _logger = logger;

        _audioRoot = string.IsNullOrWhiteSpace(audioRootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MemoryTimeline",
                "AudioRecordings")
            : audioRootOverride;
    }

    /// <summary>
    /// Pure scheduled-backup decision: "off" (or unknown/blank) never runs;
    /// "daily"/"weekly" run when there is no previous run or the previous run
    /// is at least 1/7 day(s) old. A last-run timestamp in the future (clock
    /// change) simply waits until the clock catches up.
    /// </summary>
    public static bool ShouldRunScheduledBackup(string? schedule, DateTime? lastRunAt, DateTime nowUtc)
    {
        var cadence = schedule?.Trim().ToLowerInvariant() switch
        {
            "daily" => TimeSpan.FromDays(1),
            "weekly" => TimeSpan.FromDays(7),
            _ => (TimeSpan?)null // "off", blank, unknown
        };

        if (cadence == null)
        {
            return false;
        }

        return lastRunAt == null || nowUtc - lastRunAt.Value >= cadence.Value;
    }

    /// <inheritdoc />
    public async Task<BackupResult> CreateBackupAsync(
        string destinationPath,
        BackupOptions options,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A backup destination path is required.", nameof(destinationPath));
        }

        options ??= new BackupOptions();
        var stopwatch = Stopwatch.StartNew();

        var databasePath = await GetDatabasePathAsync();

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            // Works for local and UNC paths alike.
            Directory.CreateDirectory(destinationDirectory);
        }

        var tmpZipPath = destinationPath + ".tmp";
        var tmpDbPath = Path.Combine(Path.GetTempPath(), $"mtbak-{Guid.NewGuid():N}.db");

        try
        {
            // ALL the heavy lifting (db snapshot, zip build over the media +
            // audio trees, manifest, publish move) runs on the thread pool -
            // mirroring PreviewRestoreAsync/RestoreAsync - so "Back Up Now"
            // never freezes the awaiting UI thread. progress is a Progress<T>
            // in production, which marshals its callbacks to the captured UI
            // context, so reporting from inside Task.Run is safe. ct is both
            // the Task.Run token (skips the body when already cancelled) and
            // checked inside the body/AddTree loops.
            var (eventCount, sizeBytes) = await Task.Run(() =>
            {
                progress?.Report(5);

                // WAL-safe snapshot of the live database (online backup API;
                // the proven pattern from ImportService.TryCreateDatabaseBackup).
                SnapshotDatabase(databasePath, tmpDbPath);
                var snapshotEventCount = TryCountEvents(tmpDbPath);
                progress?.Report(25);

                using (var zipStream = new FileStream(tmpZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(tmpDbPath, DatabaseEntryName, CompressionLevel.Optimal);
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(35);

                    if (options.IncludeMedia)
                    {
                        AddTree(zip, _mediaService.MediaRoot, MediaEntryPrefix, ct);
                    }

                    progress?.Report(70);

                    if (options.IncludeAudio)
                    {
                        AddTree(zip, _audioRoot, AudioEntryPrefix, ct);
                    }

                    progress?.Report(90);

                    var manifest = new BackupManifest
                    {
                        FormatVersion = CurrentFormatVersion,
                        AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                        CreatedAtUtc = DateTime.UtcNow,
                        EventCount = snapshotEventCount,
                        IncludesMedia = options.IncludeMedia,
                        IncludesAudio = options.IncludeAudio
                    };
                    var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                    using var manifestWriter = new StreamWriter(manifestEntry.Open());
                    manifestWriter.Write(JsonSerializer.Serialize(manifest, ManifestJsonOptions));
                }

                ct.ThrowIfCancellationRequested();

                // Atomic-ish publish: the finished archive appears in one move -
                // a reader can never observe a torn .mtbak.
                File.Move(tmpZipPath, destinationPath, overwrite: true);
                progress?.Report(100);

                return (snapshotEventCount, new FileInfo(destinationPath).Length);
            }, ct);

            var result = new BackupResult
            {
                FilePath = destinationPath,
                SizeBytes = sizeBytes,
                EventCount = eventCount,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds
            };

            _logger.LogInformation(
                "Backup written to {Path} ({SizeBytes} bytes, {EventCount} events, {Duration:F1}s).",
                result.FilePath, result.SizeBytes, result.EventCount, result.DurationSeconds);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Backup to {Path} was cancelled.", destinationPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup to {Path} failed.", destinationPath);
            throw new InvalidOperationException($"Backup failed: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteFile(tmpDbPath);
            TryDeleteFile(tmpZipPath); // no-op after a successful move
        }
    }

    /// <inheritdoc />
    public async Task<RestorePreview> PreviewRestoreAsync(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            throw new FileNotFoundException($"The backup file could not be found: {backupPath}", backupPath);
        }

        // Read-only zip inspection off the caller's (possibly UI) thread;
        // zero writes to the live database.
        return await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(backupPath);

            var manifestEntry = zip.GetEntry(ManifestEntryName)
                ?? throw new InvalidDataException(
                    "This file is not a Memory Timeline backup (manifest.json is missing).");
            if (zip.GetEntry(DatabaseEntryName) == null)
            {
                throw new InvalidDataException(
                    "This backup is unusable: it contains no database.db.");
            }

            BackupManifest? manifest;
            using (var manifestStream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<BackupManifest>(manifestStream, ManifestJsonOptions);
            }

            if (manifest == null)
            {
                throw new InvalidDataException("This backup's manifest is unreadable.");
            }

            if (manifest.FormatVersion > CurrentFormatVersion)
            {
                throw new InvalidDataException(
                    $"This backup was created by a newer version of Memory Timeline (format {manifest.FormatVersion}); update the app to restore it.");
            }

            var mediaFiles = zip.Entries.Count(e => IsFileEntry(e) &&
                e.FullName.StartsWith(MediaEntryPrefix, StringComparison.OrdinalIgnoreCase));
            var audioFiles = zip.Entries.Count(e => IsFileEntry(e) &&
                e.FullName.StartsWith(AudioEntryPrefix, StringComparison.OrdinalIgnoreCase));

            return new RestorePreview
            {
                FilePath = backupPath,
                FormatVersion = manifest.FormatVersion,
                AppVersion = manifest.AppVersion,
                CreatedAtUtc = manifest.CreatedAtUtc,
                EventCount = manifest.EventCount,
                IncludesMedia = manifest.IncludesMedia,
                IncludesAudio = manifest.IncludesAudio,
                MediaFileCount = mediaFiles,
                AudioFileCount = audioFiles,
                SizeBytes = new FileInfo(backupPath).Length
            };
        });
    }

    /// <inheritdoc />
    public async Task RestoreAsync(string backupPath, RestoreMode mode, CancellationToken ct = default)
    {
        if (mode != RestoreMode.Replace)
        {
            throw new NotSupportedException($"Restore mode {mode} is not supported.");
        }

        // Validates the archive (manifest + database.db present, format
        // readable) BEFORE anything touches the live installation.
        await PreviewRestoreAsync(backupPath);

        var databasePath = await GetDatabasePathAsync();

        // (1) Automatic db-only safety backup of the CURRENT archive, so even
        // a restore of the wrong file is recoverable. Goes to the configured
        // backup destination; falls back to the database's own directory when
        // none is configured. A safety-backup failure ABORTS the restore.
        // CreateBackupAsync runs its snapshot/zip work in Task.Run, so this
        // call - like the preview/extract phases - never blocks the awaiting
        // (possibly UI) thread.
        var safetyDirectory = await _settingsService.GetSettingAsync<string>(
            SettingKeys.BackupDestination, string.Empty);
        if (string.IsNullOrWhiteSpace(safetyDirectory))
        {
            safetyDirectory = Path.GetDirectoryName(databasePath) ?? Path.GetTempPath();
        }

        var safetyPath = Path.Combine(
            safetyDirectory, $"pre-restore-{DateTime.Now:yyyyMMdd_HHmmss}.mtbak");
        try
        {
            await CreateBackupAsync(
                safetyPath,
                new BackupOptions { IncludeMedia = false, IncludeAudio = false },
                progress: null,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Restore aborted: the automatic safety backup could not be written to '{safetyPath}' ({ex.Message}). Nothing was changed.",
                ex);
        }

        _logger.LogInformation("Pre-restore safety backup written to {Path}.", safetyPath);

        // (2) Release every pooled SQLite connection so the database file can
        // be replaced on disk.
        SqliteConnection.ClearAllPools();

        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(backupPath);

                // (3) Replace the database. The new file is fully extracted to
                // a sibling temp path first, then the OLD database's WAL/SHM
                // sidecars are removed (their contents are being discarded by
                // design - and a stale -wal must never be replayed into the
                // restored file) and the new file is moved into place, keeping
                // the window in which the live path is inconsistent minimal.
                var databaseEntry = zip.GetEntry(DatabaseEntryName)!;
                var tmpRestorePath = databasePath + ".restore-tmp";
                try
                {
                    databaseEntry.ExtractToFile(tmpRestorePath, overwrite: true);
                    TryDeleteFile(databasePath + "-wal");
                    TryDeleteFile(databasePath + "-shm");
                    File.Move(tmpRestorePath, databasePath, overwrite: true);
                }
                finally
                {
                    TryDeleteFile(tmpRestorePath);
                }

                // (4) Media/audio trees - existing files are overwritten.
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!IsFileEntry(entry))
                    {
                        continue;
                    }

                    if (entry.FullName.StartsWith(MediaEntryPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        ExtractUnderRoot(entry, _mediaService.MediaRoot, MediaEntryPrefix);
                    }
                    else if (entry.FullName.StartsWith(AudioEntryPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        ExtractUnderRoot(entry, _audioRoot, AudioEntryPrefix);
                    }
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Restore from {Path} was cancelled MID-REPLACE; the archive may mix old and new content. Safety backup: {SafetyPath}.",
                backupPath, safetyPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore from {Path} failed. Safety backup: {SafetyPath}.", backupPath, safetyPath);
            throw new InvalidOperationException(
                $"Restore failed: {ex.Message}. A safety backup of the previous archive is at '{safetyPath}'.",
                ex);
        }

        // (5) Drop the restored sync identity, so this installation re-pairs as
        // itself rather than impersonating the machine the backup came from.
        //
        // This is not tidiness. `sync_device_id` lives in `app_settings`, which
        // lives in the database file step (3) just overwrote — so restoring a
        // backup makes this machine authenticate as the machine that made it.
        // "Set my new computer up from a backup" is the most natural way there
        // is to end up with two machines holding one device id, and the sync
        // system then fails silently in two compounding ways:
        //
        //  - Pull filters out the caller's own changes
        //    (`SyncChangeService.PullAsync`: SourceDeviceId != caller.DeviceId),
        //    so two machines sharing an id become permanently invisible to each
        //    other while both believe they are syncing.
        //  - A push receipt is keyed (DeviceId, ClientSequence), and
        //    ClientSequence is the local outbox row id
        //    (`LocalOutboxPublisher`). Two machines mint the same ids, the
        //    service short-circuits on the receipt BEFORE validating anything
        //    and answers Accepted=true/Duplicate=true, and the client treats
        //    Duplicate exactly like Accepted and marks the row delivered. Real
        //    edits are dropped with a green sync status and nothing logged.
        //
        // Clearing the same four keys as `SyncSettingsStore.ClearRegistrationAsync`,
        // duplicated rather than called because that type lives in
        // MemoryTimeline.Sync and Core must not depend on it. Best-effort: a
        // restore that has already replaced the database must not be reported
        // as failed because a settings row would not delete, but an
        // installation left holding a foreign identity has to be loud about it.
        try
        {
            await _settingsService.DeleteSettingAsync(SettingKeys.SyncDeviceId);
            await _settingsService.DeleteSettingAsync(SettingKeys.SyncAccessToken);
            await _settingsService.DeleteSettingAsync(SettingKeys.SyncRefreshToken);
            await _settingsService.DeleteSettingAsync(SettingKeys.SyncCursor);
            await _settingsService.SetSettingAsync(SettingKeys.SyncEnabled, false);
            _logger.LogInformation(
                "Sync registration cleared after restore; this installation must re-pair before it syncs again.");
        }
        catch (Exception syncEx)
        {
            _logger.LogError(syncEx,
                "Archive restored, but the sync registration from the backup could NOT be cleared. "
                + "This installation may now share a device id with the machine that made the backup. "
                + "Unpair and re-pair in Settings before enabling sync.");
        }

        // (6) Restart recommendation: singleton caches (settings, view-model
        // state) still reflect the pre-restore database.
        _logger.LogWarning(
            "Archive restored from {Path}. Restart the app so all views and caches load the restored data.",
            backupPath);
        try
        {
            WeakReferenceMessenger.Default.Send(new ArchiveRestoredMessage(backupPath));
        }
        catch (Exception messengerEx)
        {
            _logger.LogWarning(messengerEx, "Error publishing ArchiveRestoredMessage.");
        }
    }

    /// <inheritdoc />
    public async Task<List<BackupRecord>> ListBackupsAsync()
    {
        var records = new List<BackupRecord>();

        var destination = await _settingsService.GetSettingAsync<string>(
            SettingKeys.BackupDestination, string.Empty);
        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination))
        {
            return records;
        }

        foreach (var file in Directory.EnumerateFiles(destination, "*.mtbak", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var preview = await PreviewRestoreAsync(file);
                records.Add(new BackupRecord
                {
                    FilePath = file,
                    FileName = Path.GetFileName(file),
                    SizeBytes = preview.SizeBytes,
                    CreatedAtUtc = preview.CreatedAtUtc,
                    EventCount = preview.EventCount,
                    IncludesMedia = preview.IncludesMedia,
                    IncludesAudio = preview.IncludesAudio
                });
            }
            catch (Exception ex)
            {
                // A corrupt/foreign file must not hide the healthy backups.
                _logger.LogWarning(ex, "Skipping unreadable backup file {Path}.", file);
            }
        }

        return records.OrderByDescending(r => r.CreatedAtUtc).ToList();
    }

    // ---- helpers ----

    private async Task<string> GetDatabasePathAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var databasePath = context.Database.GetDbConnection().DataSource;

        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                $"The database file could not be found at '{databasePath}'.");
        }

        return Path.GetFullPath(databasePath);
    }

    /// <summary>
    /// WAL-safe database snapshot via SQLite's online backup API. A raw
    /// File.Copy of the live .db would miss every transaction still in the
    /// -wal sidecar (and could tear against a checkpoint); BackupDatabase
    /// takes a read transaction and produces a consistent snapshot including
    /// WAL contents, even while another connection holds a write transaction.
    /// Pooling=False so neither connection lingers holding a file handle.
    /// </summary>
    private static void SnapshotDatabase(string databasePath, string snapshotPath)
    {
        using var source = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        using var destination = new SqliteConnection(
            $"Data Source={snapshotPath};Pooling=False");
        source.Open();
        destination.Open();
        ApplyBusyTimeout(source);
        ApplyBusyTimeout(destination);
        source.BackupDatabase(destination);
    }

    private static void ApplyBusyTimeout(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
    }

    private int TryCountEvents(string snapshotPath)
    {
        try
        {
            using var connection = new SqliteConnection(
                $"Data Source={snapshotPath};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM \"events\";";
            var result = command.ExecuteScalar();
            return result == null || result is DBNull ? 0 : Convert.ToInt32(result);
        }
        catch (Exception ex)
        {
            // The count is manifest metadata; its failure must not kill the backup.
            _logger.LogWarning(ex, "Could not count events in the database snapshot; recording 0.");
            return 0;
        }
    }

    /// <summary>
    /// Adds every file under <paramref name="root"/> (recursively) to the zip
    /// under <paramref name="entryPrefix"/>, with forward-slash entry names.
    /// *.tmp files (in-progress copies) are skipped; a file that vanishes or
    /// is locked mid-walk is skipped with a log entry rather than failing the
    /// whole backup. A missing root is a no-op.
    /// </summary>
    private void AddTree(ZipArchive zip, string root, string entryPrefix, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

            try
            {
                zip.CreateEntryFromFile(file, entryPrefix + relative, CompressionLevel.Fastest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                _logger.LogWarning(ex, "Skipping unreadable file {Path} during backup.", file);
            }
        }
    }

    /// <summary>
    /// Extracts one archive entry under the given root, overwriting existing
    /// files. Entries whose (attacker-controllable) relative path would
    /// escape the root ("../", rooted paths) are skipped with a warning.
    /// </summary>
    private void ExtractUnderRoot(ZipArchiveEntry entry, string root, string entryPrefix)
    {
        var relative = entry.FullName[entryPrefix.Length..]
            .Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            _logger.LogWarning("Skipping suspicious backup entry '{Entry}'.", entry.FullName);
            return;
        }

        var rootFull = Path.GetFullPath(root);
        var destination = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!destination.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Skipping path-traversal backup entry '{Entry}'.", entry.FullName);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, overwrite: true);
    }

    private static bool IsFileEntry(ZipArchiveEntry entry)
        => !string.IsNullOrEmpty(entry.Name); // directory entries end with '/' and have an empty Name

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
            _logger.LogWarning(ex, "Could not delete temporary file {Path}.", path);
        }
    }

    /// <summary>The manifest.json shape (camelCase in the archive).</summary>
    private sealed class BackupManifest
    {
        public int FormatVersion { get; set; }
        public string? AppVersion { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public int EventCount { get; set; }
        public bool IncludesMedia { get; set; }
        public bool IncludesAudio { get; set; }
    }
}
