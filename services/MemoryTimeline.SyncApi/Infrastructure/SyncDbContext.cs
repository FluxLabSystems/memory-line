using MemoryTimeline.SyncApi.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MemoryTimeline.SyncApi.Infrastructure;

/// <summary>
/// EF Core context for the sync service's own SQLite database. The service
/// owns this schema exclusively (created via EnsureCreated at startup); it is
/// unrelated to the Windows app database.
/// </summary>
public class SyncDbContext : DbContext
{
    /// <summary>Converts DateTime columns to/from UTC so responses serialize with a Z suffix.</summary>
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
        new(v => ToUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter =
        new(v => v.HasValue ? ToUtc(v.Value) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    public SyncDbContext(DbContextOptions<SyncDbContext> options)
        : base(options)
    {
    }

    public DbSet<Owner> Owners => Set<Owner>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<ServerCapture> Captures => Set<ServerCapture>();

    public DbSet<ServerArtifact> Artifacts => Set<ServerArtifact>();

    public DbSet<CaptureStatusRow> CaptureStatuses => Set<CaptureStatusRow>();

    public DbSet<AssistantSessionRow> AssistantSessions => Set<AssistantSessionRow>();

    public DbSet<AssistantTurnRow> AssistantTurns => Set<AssistantTurnRow>();

    public DbSet<TripRow> Trips => Set<TripRow>();

    public DbSet<RouteContextRow> RouteContexts => Set<RouteContextRow>();

    public DbSet<DetourSuggestionRow> DetourSuggestions => Set<DetourSuggestionRow>();

    public DbSet<SyncChangeRow> SyncChanges => Set<SyncChangeRow>();

    public DbSet<PushReceipt> PushReceipts => Set<PushReceipt>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Owner>(e =>
        {
            e.ToTable("owners");
            e.HasKey(x => x.OwnerId);
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        });

        modelBuilder.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.DeviceId);
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.Platform).HasColumnName("platform");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
            e.Property(x => x.AppVersion).HasColumnName("app_version");
            e.Property(x => x.PublicKey).HasColumnName("public_key");
            // Concurrency token: concurrent refresh rotations conflict with
            // DbUpdateConcurrencyException instead of silently clobbering.
            e.Property(x => x.RefreshTokenHash).HasColumnName("refresh_token_hash").IsConcurrencyToken();
            e.Property(x => x.RefreshTokenExpiresAtUtc).HasColumnName("refresh_token_expires_at_utc");
            e.Property(x => x.PreviousRefreshTokenHash).HasColumnName("previous_refresh_token_hash");
            e.Property(x => x.PreviousRefreshTokenExpiresAtUtc).HasColumnName("previous_refresh_token_expires_at_utc");
            e.Property(x => x.AckedCursor).HasColumnName("acked_cursor");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.LastSeenAtUtc).HasColumnName("last_seen_at_utc");
            e.Property(x => x.RevokedAtUtc).HasColumnName("revoked_at_utc");
            e.HasIndex(x => x.OwnerId);
        });

        modelBuilder.Entity<ServerCapture>(e =>
        {
            e.ToTable("captures");
            e.HasKey(x => x.CaptureId);
            e.Property(x => x.CaptureId).HasColumnName("capture_id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.SourceDeviceId).HasColumnName("source_device_id");
            e.Property(x => x.SourcePlatform).HasColumnName("source_platform");
            e.Property(x => x.CaptureType).HasColumnName("capture_type");
            e.Property(x => x.CapturedAtUtc).HasColumnName("captured_at_utc");
            e.Property(x => x.TimezoneId).HasColumnName("timezone_id");
            e.Property(x => x.LocalOffsetMinutes).HasColumnName("local_offset_minutes");
            e.Property(x => x.TripId).HasColumnName("trip_id");
            e.Property(x => x.TitleHint).HasColumnName("title_hint");
            e.Property(x => x.UserNote).HasColumnName("user_note");
            e.Property(x => x.LocationJson).HasColumnName("location_json");
            e.Property(x => x.ClientSchemaVersion).HasColumnName("client_schema_version");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Revision).HasColumnName("revision");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.HasIndex(x => x.OwnerId);
        });

        modelBuilder.Entity<ServerArtifact>(e =>
        {
            e.ToTable("capture_artifacts");
            e.HasKey(x => x.ArtifactId);
            e.Property(x => x.ArtifactId).HasColumnName("artifact_id");
            e.Property(x => x.CaptureId).HasColumnName("capture_id");
            e.Property(x => x.ArtifactType).HasColumnName("artifact_type");
            e.Property(x => x.MediaType).HasColumnName("media_type");
            e.Property(x => x.OriginalFileName).HasColumnName("original_file_name");
            e.Property(x => x.ExpectedByteLength).HasColumnName("expected_byte_length");
            e.Property(x => x.ExpectedSha256).HasColumnName("expected_sha256");
            e.Property(x => x.ActualByteLength).HasColumnName("actual_byte_length");
            e.Property(x => x.ActualSha256).HasColumnName("actual_sha256");
            e.Property(x => x.State).HasColumnName("state");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
            e.HasIndex(x => x.CaptureId);
        });

        // Column names and types here are duplicated in
        // SyncApiBootstrapper.EnsureCaptureStatusTableAsync, which creates this
        // table on databases that predate it — keep the two in step.
        modelBuilder.Entity<CaptureStatusRow>(e =>
        {
            e.ToTable("capture_status");
            e.HasKey(x => x.CaptureId);
            e.Property(x => x.CaptureId).HasColumnName("capture_id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.ProcessingStage).HasColumnName("processing_stage");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.Property(x => x.TranscriptAvailable).HasColumnName("transcript_available");
            e.Property(x => x.TranscriptPreview).HasColumnName("transcript_preview");
            e.Property(x => x.TranscriptCharCount).HasColumnName("transcript_char_count");
            e.Property(x => x.PendingEventCount).HasColumnName("pending_event_count");
            e.Property(x => x.ApprovedEventCount).HasColumnName("approved_event_count");
            e.Property(x => x.FailureReason).HasColumnName("failure_reason");
            e.Property(x => x.FailureRetryable).HasColumnName("failure_retryable");
            e.Property(x => x.Revision).HasColumnName("revision");
            e.Property(x => x.ReceivedAtUtc).HasColumnName("received_at_utc");
            e.HasIndex(x => x.OwnerId);
        });

        // Column names and types here are duplicated in
        // SyncApiBootstrapper.EnsureAssistantTablesAsync, which creates these
        // tables on databases that predate them — keep the two in step.
        modelBuilder.Entity<AssistantSessionRow>(e =>
        {
            e.ToTable("assistant_sessions");
            e.HasKey(x => x.SessionId);
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.PreferredResponder).HasColumnName("preferred_responder");
            e.Property(x => x.Surface).HasColumnName("surface");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.LastTurnAtUtc).HasColumnName("last_turn_at_utc");
            e.Property(x => x.TurnCount).HasColumnName("turn_count");
            // Every session read is scoped to the owning device, never to the
            // session ID alone, so the index carries the device too.
            e.HasIndex(x => new { x.OwnerId, x.DeviceId });
        });

        modelBuilder.Entity<AssistantTurnRow>(e =>
        {
            e.ToTable("assistant_turns");
            e.HasKey(x => x.TurnId);
            e.Property(x => x.TurnId).HasColumnName("turn_id");
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.OriginDeviceId).HasColumnName("origin_device_id");
            e.Property(x => x.Sequence).HasColumnName("sequence");
            e.Property(x => x.Question).HasColumnName("question");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.RequestedResponder).HasColumnName("requested_responder");
            e.Property(x => x.ActualResponder).HasColumnName("actual_responder");
            e.Property(x => x.ClientContextJson).HasColumnName("client_context_json");
            e.Property(x => x.Stream).HasColumnName("stream");
            e.Property(x => x.Answer).HasColumnName("answer");
            e.Property(x => x.Grounding).HasColumnName("grounding");
            e.Property(x => x.CitationsJson).HasColumnName("citations_json");
            e.Property(x => x.Model).HasColumnName("model");
            e.Property(x => x.ElapsedMs).HasColumnName("elapsed_ms");
            e.Property(x => x.FailureReason).HasColumnName("failure_reason");
            e.Property(x => x.FailureRetryable).HasColumnName("failure_retryable");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
            e.Property(x => x.Revision).HasColumnName("revision");
            e.HasIndex(x => x.SessionId);

            // Turn sequence is assigned from the session's turn count, so two
            // concurrent submissions can compute the same number. The unique
            // index makes that a lost race the service retries rather than two
            // turns silently sharing a position in the conversation.
            e.HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique();
        });

        // Column names and types here are duplicated in
        // SyncApiBootstrapper.EnsureTripTablesAsync, which creates these tables
        // on databases that predate them — keep the two in step.
        modelBuilder.Entity<TripRow>(e =>
        {
            e.ToTable("trips");
            e.HasKey(x => x.TripId);
            e.Property(x => x.TripId).HasColumnName("trip_id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
            e.Property(x => x.EndedAtUtc).HasColumnName("ended_at_utc");
            e.Property(x => x.OriginLabel).HasColumnName("origin_label");
            e.Property(x => x.DestinationLabel).HasColumnName("destination_label");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.PreferencesJson).HasColumnName("preferences_json");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.HasIndex(x => x.OwnerId);
        });

        modelBuilder.Entity<RouteContextRow>(e =>
        {
            e.ToTable("trip_route_contexts");
            e.HasKey(x => x.RouteContextId);
            e.Property(x => x.RouteContextId).HasColumnName("route_context_id");
            e.Property(x => x.TripId).HasColumnName("trip_id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.CapturedAtUtc).HasColumnName("captured_at_utc");
            e.Property(x => x.Latitude).HasColumnName("latitude");
            e.Property(x => x.Longitude).HasColumnName("longitude");
            e.Property(x => x.HorizontalAccuracyM).HasColumnName("horizontal_accuracy_m");
            e.Property(x => x.HeadingDegrees).HasColumnName("heading_degrees");
            e.Property(x => x.SpeedMps).HasColumnName("speed_mps");
            e.Property(x => x.RouteProgress).HasColumnName("route_progress");
            e.Property(x => x.RemainingDistanceM).HasColumnName("remaining_distance_m");
            e.Property(x => x.RemainingDurationS).HasColumnName("remaining_duration_s");
            e.Property(x => x.CurrentRoadName).HasColumnName("current_road_name");
            e.Property(x => x.DestinationEtaUtc).HasColumnName("destination_eta_utc");
            e.Property(x => x.Provider).HasColumnName("provider");
            e.Property(x => x.ReceivedAtUtc).HasColumnName("received_at_utc");
            e.HasIndex(x => new { x.TripId, x.CapturedAtUtc });
        });

        modelBuilder.Entity<DetourSuggestionRow>(e =>
        {
            e.ToTable("detour_suggestions");
            e.HasKey(x => x.SuggestionId);
            e.Property(x => x.SuggestionId).HasColumnName("suggestion_id");
            e.Property(x => x.TripId).HasColumnName("trip_id");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.GeneratedAtUtc).HasColumnName("generated_at_utc");
            e.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            e.Property(x => x.PlaceProvider).HasColumnName("place_provider");
            e.Property(x => x.ProviderPlaceId).HasColumnName("provider_place_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Category).HasColumnName("category");
            e.Property(x => x.Reason).HasColumnName("reason");
            e.Property(x => x.Latitude).HasColumnName("latitude");
            e.Property(x => x.Longitude).HasColumnName("longitude");
            e.Property(x => x.RouteAddedDistanceM).HasColumnName("route_added_distance_m");
            e.Property(x => x.RouteAddedDurationS).HasColumnName("route_added_duration_s");
            e.Property(x => x.OpenStatus).HasColumnName("open_status");
            e.Property(x => x.NextOpenInterval).HasColumnName("next_open_interval");
            e.Property(x => x.PriceSummary).HasColumnName("price_summary");
            e.Property(x => x.AdmissionCurrency).HasColumnName("admission_currency");
            e.Property(x => x.Confidence).HasColumnName("confidence");
            e.Property(x => x.VerificationSourcesJson).HasColumnName("verification_sources_json");
            e.Property(x => x.State).HasColumnName("state");
            e.Property(x => x.StateChangedAtUtc).HasColumnName("state_changed_at_utc");
            e.HasIndex(x => new { x.TripId, x.GeneratedAtUtc });
        });

        modelBuilder.Entity<SyncChangeRow>(e =>
        {
            e.ToTable("sync_changes");
            e.HasKey(x => x.ChangeId);
            e.Property(x => x.ChangeId).HasColumnName("change_id").ValueGeneratedOnAdd();
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.Operation).HasColumnName("operation");
            e.Property(x => x.Revision).HasColumnName("revision");
            e.Property(x => x.SourceDeviceId).HasColumnName("source_device_id");
            e.Property(x => x.PayloadJson).HasColumnName("payload_json");
            e.Property(x => x.ChangedAtUtc).HasColumnName("changed_at_utc");
            e.HasIndex(x => new { x.OwnerId, x.ChangeId });
        });

        modelBuilder.Entity<PushReceipt>(e =>
        {
            e.ToTable("push_receipts");
            e.HasKey(x => new { x.DeviceId, x.ClientSequence });
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.ClientSequence).HasColumnName("client_sequence");
            e.Property(x => x.ServerChangeId).HasColumnName("server_change_id");
            e.Property(x => x.ReceivedAtUtc).HasColumnName("received_at_utc");

            // FK to the change row so a receipt added alongside a new change
            // picks up the store-generated change ID and both rows commit in
            // one SaveChanges (a retried push can never duplicate the change).
            e.HasOne<SyncChangeRow>().WithMany().HasForeignKey(x => x.ServerChangeId);
        });

        modelBuilder.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records");
            e.HasKey(x => new { x.DeviceId, x.IdempotencyKey });
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(x => x.Endpoint).HasColumnName("endpoint");
            e.Property(x => x.StatusCode).HasColumnName("status_code");
            e.Property(x => x.ResponseJson).HasColumnName("response_json");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        });

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(UtcConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(NullableUtcConverter);
                }
            }
        }
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
