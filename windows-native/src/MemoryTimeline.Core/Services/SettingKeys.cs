namespace MemoryTimeline.Core.Services;

/// <summary>
/// Single source of truth for application setting keys.
/// Writers (SettingsViewModel) and readers (SettingsService, LLM/embedding services)
/// must both use these constants — never string literals — to prevent key drift.
/// Values are snake_case to match the AppDbContext seed rows, except
/// <see cref="AnthropicApiKey"/> which preserves existing user rows.
/// </summary>
public static class SettingKeys
{
    public const string AnthropicApiKey = "ApiKey";            // preserves existing user rows
    public const string LlmProvider = "llm_provider";
    public const string LlmModel = "llm_model";
    public const string LlmBaseUrl = "llm_base_url";           // OpenAI-compatible endpoint (Ollama / LM Studio / vLLM); matches DB seed
    public const string DefaultZoomLevel = "default_zoom_level";
    public const string EmbeddingProvider = "embedding_provider"; // matches DB seed ("local" | "openai")
    public const string EmbeddingApiKey = "embedding_api_key"; // matches DB seed
    public const string EmbeddingModel = "embedding_model";    // matches DB seed
    // Seeded for provider parity/visibility only; the AUTHORITATIVE dimension is
    // IEmbeddingService.EmbeddingDimension (and the per-row embedding_dimension column).
    public const string EmbeddingDimension = "embedding_dimension"; // matches DB seed
    public const string LocalModelPath = "local_model_path";   // overrides the local ONNX model directory when non-empty; matches DB seed
    public const string Theme = "theme";                       // matches DB seed
    public const string AskTopK = "ask_top_k";                 // matches DB seed
    public const string AskIncludeTranscripts = "ask_include_transcripts"; // matches DB seed
    public const string HomeIsDefaultPage = "home_is_default_page"; // matches DB seed
    public const string DailyToastEnabled = "daily_toast_enabled";  // matches DB seed
    public const string WeeklyDigestEnabled = "weekly_digest_enabled"; // matches DB seed
    public const string LastToastDate = "last_toast_date";     // NOT seeded: absent = toast never shown

    // Backup & revision history (2026-08 F12)
    public const string BackupDestination = "backup_destination";           // matches DB seed ("")
    public const string BackupIncludeMedia = "backup_include_media";        // matches DB seed ("true")
    public const string BackupSchedule = "backup_schedule";                 // matches DB seed: "off" | "daily" | "weekly"
    public const string LastBackupAt = "last_backup_at";                    // NOT seeded: absent = never backed up; invariant "o" round-trip format
    public const string RevisionHistoryEnabled = "revision_history_enabled"; // matches DB seed ("true")

    // Map / geocoding (2026-08 F10)
    public const string GeocodingEnabled = "geocoding_enabled";   // matches DB seed; "false" = no place name leaves the machine
    public const string GeocodingProvider = "geocoding_provider"; // matches DB seed ("nominatim")
    public const string MapDefaultZoom = "map_default_zoom";      // matches DB seed ("1.0" = whole world)

    // --- Sync (iOS companion Phase 1) ---
    // Tokens live in app_settings like the other keys for now; DPAPI encryption
    // at rest is the tracked hardening follow-up (HARDENING-FOLLOWUPS.md).
    public const string SyncEnabled = "sync_enabled";
    public const string SyncServerUrl = "sync_server_url";
    public const string SyncDeviceId = "sync_device_id";
    public const string SyncDeviceDisplayName = "sync_device_display_name";
    public const string SyncAccessToken = "sync_access_token";
    public const string SyncRefreshToken = "sync_refresh_token";
    public const string SyncCursor = "sync_cursor";
    /// <summary>Automatically transcribe/extract remote captures after ingestion.</summary>
    public const string SyncAutoProcess = "sync_auto_process";
    /// <summary>
    /// "true" while a full timeline projection backfill is owed to the sync
    /// service: set when a registration is saved and after a restore, cleared
    /// by the sync worker once <c>ITimelineProjectionPublisher.PublishAllAsync</c>
    /// has run. NOT seeded: absent = nothing owed.
    /// </summary>
    public const string TimelineProjectionBackfillPending = "timeline_projection_backfill_pending";
}
