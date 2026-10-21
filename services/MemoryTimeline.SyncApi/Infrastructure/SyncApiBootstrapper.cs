using System.Security.Cryptography;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.SyncApi.Infrastructure;

/// <summary>
/// Startup bootstrap for the sync service: creates the data directory,
/// materializes the token signing key and pairing code (from configuration or
/// generated-and-persisted files), creates the SQLite schema, and ensures the
/// single owner row exists (design §4.2 self-hosted mode).
/// </summary>
public static class SyncApiBootstrapper
{
    private const string SigningKeyFileName = "signing.key";
    private const string PairingCodeFileName = "pairing-code.txt";
    private const int MinimumSigningKeyBytes = 32;

    /// <summary>Idempotency record retention window (design §11.4).</summary>
    private const int IdempotencyRetentionHours = 48;

    /// <summary>
    /// Validates options and prepares the on-disk environment. Runs before the
    /// host is built because the JWT signing key must exist for authentication
    /// setup. Throws <see cref="InvalidOperationException"/> on invalid config.
    /// </summary>
    public static SyncServerEnvironment Initialize(SyncApiOptions options)
    {
        if (options.AccessTokenLifetimeMinutes <= 0)
        {
            throw new InvalidOperationException("SyncApi:AccessTokenLifetimeMinutes must be positive.");
        }

        if (options.RefreshTokenLifetimeDays <= 0)
        {
            throw new InvalidOperationException("SyncApi:RefreshTokenLifetimeDays must be positive.");
        }

        if (options.RefreshTokenGraceSeconds < 0)
        {
            throw new InvalidOperationException("SyncApi:RefreshTokenGraceSeconds must be zero or positive.");
        }

        if (options.MaxPartSizeBytes <= 0)
        {
            throw new InvalidOperationException("SyncApi:MaxPartSizeBytes must be positive.");
        }

        if (options.ArtifactRetentionDays <= 0)
        {
            throw new InvalidOperationException("SyncApi:ArtifactRetentionDays must be positive.");
        }

        if (options.ArtifactStorageBudgetBytes < 0)
        {
            throw new InvalidOperationException("SyncApi:ArtifactStorageBudgetBytes must be zero or positive.");
        }

        if (options.RetentionSweepIntervalMinutes <= 0)
        {
            throw new InvalidOperationException("SyncApi:RetentionSweepIntervalMinutes must be positive.");
        }

        if (options.TemporaryFileGraceMinutes < 0)
        {
            throw new InvalidOperationException("SyncApi:TemporaryFileGraceMinutes must be zero or positive.");
        }

        if (options.AssistantSessionIdleHours <= 0)
        {
            throw new InvalidOperationException("SyncApi:AssistantSessionIdleHours must be positive.");
        }

        if (options.AssistantReaperIntervalMinutes <= 0)
        {
            throw new InvalidOperationException("SyncApi:AssistantReaperIntervalMinutes must be positive.");
        }

        if (options.RouteContextRetentionDays <= 0)
        {
            throw new InvalidOperationException("SyncApi:RouteContextRetentionDays must be positive.");
        }

        if (options.DetourSuggestionLifetimeHours <= 0)
        {
            throw new InvalidOperationException("SyncApi:DetourSuggestionLifetimeHours must be positive.");
        }

        var dataDir = Path.GetFullPath(string.IsNullOrWhiteSpace(options.DataDir) ? "data" : options.DataDir);
        Directory.CreateDirectory(dataDir);
        var artifactsDir = Path.Combine(dataDir, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        return new SyncServerEnvironment
        {
            DataDirectory = dataDir,
            ArtifactsDirectory = artifactsDir,
            DatabasePath = Path.Combine(dataDir, "sync.db"),
            SigningKey = ResolveSigningKey(options, dataDir),
            PairingCode = ResolvePairingCode(options, dataDir),
        };
    }

    /// <summary>
    /// Creates the schema (EnsureCreated — the service owns it) and the single
    /// owner row on first run, and sweeps idempotency records older than the
    /// retention window (design §11.4). Returns the owner ID.
    /// </summary>
    public static async Task<string> InitializeDatabaseAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<SyncDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await EnsureCaptureStatusTableAsync(db);
        await EnsureAssistantTablesAsync(db);
        await EnsureTripTablesAsync(db);

        var retentionCutoffUtc = DateTime.UtcNow.AddHours(-IdempotencyRetentionHours);
        await db.IdempotencyRecords
            .Where(r => r.CreatedAtUtc < retentionCutoffUtc)
            .ExecuteDeleteAsync();

        var owner = await db.Owners.OrderBy(o => o.CreatedAtUtc).FirstOrDefaultAsync();
        if (owner is null)
        {
            owner = new Owner
            {
                OwnerId = Guid.NewGuid().ToString("D"),
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Owners.Add(owner);
            await db.SaveChangesAsync();
        }

        return owner.OwnerId;
    }

    /// <summary>
    /// Creates the capture_status projection table (design §19 Phase 3) on
    /// databases created before it existed: EnsureCreated builds the whole
    /// schema on first run but leaves an existing database untouched, so a
    /// later table has to be created explicitly. Both statements are
    /// idempotent, so the fresh-database path is a no-op. Column names and
    /// types mirror the CaptureStatusRow mapping in <see cref="SyncDbContext"/>.
    /// </summary>
    private static async Task EnsureCaptureStatusTableAsync(SyncDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS capture_status (
                capture_id TEXT NOT NULL CONSTRAINT PK_capture_status PRIMARY KEY,
                owner_id TEXT NOT NULL,
                status TEXT NOT NULL,
                processing_stage TEXT NULL,
                updated_at_utc TEXT NOT NULL,
                transcript_available INTEGER NOT NULL,
                transcript_preview TEXT NULL,
                transcript_char_count INTEGER NULL,
                pending_event_count INTEGER NULL,
                approved_event_count INTEGER NULL,
                failure_reason TEXT NULL,
                failure_retryable INTEGER NULL,
                revision INTEGER NOT NULL,
                received_at_utc TEXT NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_capture_status_owner_id ON capture_status (owner_id)");
    }

    /// <summary>
    /// Creates the assistant session and turn tables (design §19 Phase 4) on
    /// databases created before they existed, for the same reason
    /// <see cref="EnsureCaptureStatusTableAsync"/> exists: EnsureCreated builds
    /// the whole schema on first run but leaves an existing database untouched.
    /// Every statement is idempotent, so the fresh-database path is a no-op.
    /// Column names and types mirror the assistant mappings in
    /// <see cref="SyncDbContext"/>.
    ///
    /// The unique index on (session_id, sequence) is not merely an
    /// optimization: it is what turns two submissions racing for the same
    /// position in a conversation into a retry rather than two turns sharing a
    /// sequence number.
    /// </summary>
    private static async Task EnsureAssistantTablesAsync(SyncDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS assistant_sessions (
                session_id TEXT NOT NULL CONSTRAINT PK_assistant_sessions PRIMARY KEY,
                owner_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                preferred_responder TEXT NOT NULL,
                surface TEXT NULL,
                created_at_utc TEXT NOT NULL,
                last_turn_at_utc TEXT NOT NULL,
                turn_count INTEGER NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_assistant_sessions_owner_id_device_id " +
            "ON assistant_sessions (owner_id, device_id)");

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS assistant_turns (
                turn_id TEXT NOT NULL CONSTRAINT PK_assistant_turns PRIMARY KEY,
                session_id TEXT NOT NULL,
                owner_id TEXT NOT NULL,
                origin_device_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                question TEXT NOT NULL,
                status TEXT NOT NULL,
                requested_responder TEXT NOT NULL,
                actual_responder TEXT NULL,
                client_context_json TEXT NULL,
                stream INTEGER NOT NULL,
                answer TEXT NULL,
                grounding TEXT NULL,
                citations_json TEXT NULL,
                model TEXT NULL,
                elapsed_ms INTEGER NULL,
                failure_reason TEXT NULL,
                failure_retryable INTEGER NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NULL,
                revision INTEGER NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_assistant_turns_session_id ON assistant_turns (session_id)");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_assistant_turns_session_id_sequence " +
            "ON assistant_turns (session_id, sequence)");
    }

    /// <summary>
    /// Creates the trip, route-context and detour-suggestion tables (design §19
    /// Phase 5) on databases that predate them, for the same reason the
    /// capture_status and assistant tables are created this way: EnsureCreated
    /// leaves an existing database untouched. Every statement is idempotent.
    /// Column names and types mirror the trip mappings in <see cref="SyncDbContext"/>.
    /// </summary>
    private static async Task EnsureTripTablesAsync(SyncDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS trips (
                trip_id TEXT NOT NULL CONSTRAINT PK_trips PRIMARY KEY,
                owner_id TEXT NOT NULL,
                name TEXT NOT NULL,
                started_at_utc TEXT NULL,
                ended_at_utc TEXT NULL,
                origin_label TEXT NULL,
                destination_label TEXT NULL,
                status TEXT NOT NULL,
                preferences_json TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_trips_owner_id ON trips (owner_id)");

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS trip_route_contexts (
                route_context_id TEXT NOT NULL CONSTRAINT PK_trip_route_contexts PRIMARY KEY,
                trip_id TEXT NOT NULL,
                owner_id TEXT NOT NULL,
                captured_at_utc TEXT NOT NULL,
                latitude REAL NOT NULL,
                longitude REAL NOT NULL,
                horizontal_accuracy_m REAL NULL,
                heading_degrees REAL NULL,
                speed_mps REAL NULL,
                route_progress REAL NULL,
                remaining_distance_m REAL NULL,
                remaining_duration_s REAL NULL,
                current_road_name TEXT NULL,
                destination_eta_utc TEXT NULL,
                provider TEXT NULL,
                received_at_utc TEXT NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_trip_route_contexts_trip_id_captured_at_utc " +
            "ON trip_route_contexts (trip_id, captured_at_utc)");

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS detour_suggestions (
                suggestion_id TEXT NOT NULL CONSTRAINT PK_detour_suggestions PRIMARY KEY,
                trip_id TEXT NOT NULL,
                owner_id TEXT NOT NULL,
                generated_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NULL,
                place_provider TEXT NULL,
                provider_place_id TEXT NULL,
                name TEXT NOT NULL,
                category TEXT NULL,
                reason TEXT NULL,
                latitude REAL NULL,
                longitude REAL NULL,
                route_added_distance_m REAL NULL,
                route_added_duration_s REAL NULL,
                open_status TEXT NULL,
                next_open_interval TEXT NULL,
                price_summary TEXT NULL,
                admission_currency TEXT NULL,
                confidence REAL NULL,
                verification_sources_json TEXT NULL,
                state TEXT NOT NULL,
                state_changed_at_utc TEXT NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_detour_suggestions_trip_id_generated_at_utc " +
            "ON detour_suggestions (trip_id, generated_at_utc)");
    }

    private static byte[] ResolveSigningKey(SyncApiOptions options, string dataDir)
    {
        if (!string.IsNullOrWhiteSpace(options.TokenSigningKeyBase64))
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(options.TokenSigningKeyBase64.Trim());
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("SyncApi:TokenSigningKeyBase64 is not valid base64.", ex);
            }

            if (key.Length < MinimumSigningKeyBytes)
            {
                throw new InvalidOperationException(
                    $"SyncApi:TokenSigningKeyBase64 must decode to at least {MinimumSigningKeyBytes} bytes.");
            }

            return key;
        }

        var keyPath = Path.Combine(dataDir, SigningKeyFileName);
        if (File.Exists(keyPath))
        {
            byte[]? key = null;
            try
            {
                key = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
            }
            catch (FormatException)
            {
                // Fall through to the explicit corrupt-file error below.
            }

            if (key is { Length: >= MinimumSigningKeyBytes })
            {
                return key;
            }

            throw new InvalidOperationException(
                $"Signing key file '{keyPath}' is corrupt or too short; delete it to regenerate " +
                "(existing access tokens will be invalidated).");
        }

        var generated = RandomNumberGenerator.GetBytes(64);
        File.WriteAllText(keyPath, Convert.ToBase64String(generated));
        return generated;
    }

    private static string ResolvePairingCode(SyncApiOptions options, string dataDir)
    {
        var codePath = Path.Combine(dataDir, PairingCodeFileName);

        if (!string.IsNullOrWhiteSpace(options.PairingCode))
        {
            var configured = options.PairingCode.Trim();
            File.WriteAllText(codePath, configured);
            return configured;
        }

        if (File.Exists(codePath))
        {
            var existing = File.ReadAllText(codePath).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        var generated = GeneratePairingCode();
        File.WriteAllText(codePath, generated);
        return generated;
    }

    private static string GeneratePairingCode()
    {
        var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..]}";
    }
}
