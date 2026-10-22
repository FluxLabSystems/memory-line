using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Data;

/// <summary>
/// Ensures the SQLite database schema matches the current EF Core model.
///
/// This class substitutes for EF Core migrations until a machine with the
/// .NET SDK / dotnet-ef can regenerate a proper migration baseline. The
/// previously checked-in migration was badly stale (it lacked the
/// era_categories, era_tags, milestones and saved_searches tables plus
/// several eras/events/tags columns) and has been deleted.
///
/// Strategy:
///  1. Database.EnsureCreatedAsync creates a brand-new database with the FULL
///     current model (including seed data) when no database file exists yet.
///  2. For databases created by OLDER builds, EnsureCreated is a no-op, so we
///     detect schema drift by inspecting sqlite_master / PRAGMA table_info and
///     apply idempotent, additive DDL (CREATE TABLE IF NOT EXISTS /
///     ALTER TABLE ... ADD COLUMN) for everything the current model needs that
///     older schemas are known to be missing.
///
/// All DDL below is derived from AppDbContext.OnModelCreating and the entity
/// models' snake_case [Table]/[Column] mappings (the source of truth), using
/// EF Core's SQLite type affinities: TEXT for strings/Guids/DateTimes,
/// INTEGER for int/bool/enums, REAL for double.
///
/// A repair failure is logged but never allowed to crash startup.
/// </summary>
public static class SchemaUpgrader
{
    public static async Task EnsureSchemaAsync(AppDbContext context, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        // Step 1: create the database with the full current model if it does
        // not exist yet. No-op (by design) when the database already exists.
        await context.Database.EnsureCreatedAsync();

        // Step 2: repair drift on databases created by older builds. Never
        // let a repair failure crash startup - the app can still run against
        // the portion of the schema that exists.
        try
        {
            await RepairSchemaDriftAsync(context, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "SchemaUpgrader: schema drift repair failed; continuing startup with existing schema.");
        }
    }

    private static async Task RepairSchemaDriftAsync(AppDbContext context, ILogger? logger)
    {
        var connection = context.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync();
        }

        try
        {
            var existingTables = await GetTableNamesAsync(connection);

            // ---- Missing tables (known drift from pre-2025-01 builds) ----

            await EnsureTableAsync(connection, existingTables, "era_categories", logger,
                """
                CREATE TABLE IF NOT EXISTS "era_categories" (
                    "category_id" TEXT NOT NULL CONSTRAINT "PK_era_categories" PRIMARY KEY,
                    "name" TEXT NOT NULL,
                    "icon_glyph" TEXT NULL,
                    "default_color" TEXT NOT NULL,
                    "sort_order" INTEGER NOT NULL,
                    "is_visible" INTEGER NOT NULL,
                    "created_at" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL
                );
                """,
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_era_categories_name" ON "era_categories" ("name");
                CREATE INDEX IF NOT EXISTS "IX_era_categories_sort_order" ON "era_categories" ("sort_order");
                """,
                // Seed parity with AppDbContext.SeedDefaultEraCategories (HasData);
                // INSERT OR IGNORE keeps this idempotent.
                """
                INSERT OR IGNORE INTO "era_categories" ("category_id", "name", "icon_glyph", "default_color", "sort_order", "is_visible", "created_at", "updated_at") VALUES
                    ('cat-education',    'Education',    char(59326), '#0078D4', 1, 1, '2025-01-21 00:00:00', '2025-01-21 00:00:00'),
                    ('cat-employment',   'Employment',   char(59425), '#107C10', 2, 1, '2025-01-21 00:00:00', '2025-01-21 00:00:00'),
                    ('cat-relationship', 'Relationship', char(60241), '#E74856', 3, 1, '2025-01-21 00:00:00', '2025-01-21 00:00:00'),
                    ('cat-residence',    'Residence',    char(59407), '#8764B8', 4, 1, '2025-01-21 00:00:00', '2025-01-21 00:00:00'),
                    ('cat-health',       'Health',       char(59742), '#00B7C3', 5, 1, '2025-01-21 00:00:00', '2025-01-21 00:00:00'),
                    ('cat-project',      'Project',      char(59633), '#FF8C00', 6, 1, '2025-01-21 00:00:00', '2025-01-21 00:00:00'),
                    ('cat-other',        'Other',        char(59331), '#6B6B6B', 7, 1, '2025-01-21 00:00:00', '2025-01-21 00:00:00');
                """);

            await EnsureTableAsync(connection, existingTables, "era_tags", logger,
                """
                CREATE TABLE IF NOT EXISTS "era_tags" (
                    "era_id" TEXT NOT NULL,
                    "tag" TEXT NOT NULL,
                    CONSTRAINT "PK_era_tags" PRIMARY KEY ("era_id", "tag"),
                    CONSTRAINT "FK_era_tags_eras_era_id" FOREIGN KEY ("era_id") REFERENCES "eras" ("era_id") ON DELETE CASCADE
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS "IX_era_tags_era_id" ON "era_tags" ("era_id");
                CREATE INDEX IF NOT EXISTS "IX_era_tags_tag" ON "era_tags" ("tag");
                """);

            await EnsureTableAsync(connection, existingTables, "milestones", logger,
                """
                CREATE TABLE IF NOT EXISTS "milestones" (
                    "milestone_id" TEXT NOT NULL CONSTRAINT "PK_milestones" PRIMARY KEY,
                    "name" TEXT NOT NULL,
                    "date" TEXT NOT NULL,
                    "type" INTEGER NOT NULL,
                    "linked_era_id" TEXT NULL,
                    "color_override" TEXT NULL,
                    "description" TEXT NULL,
                    "created_at" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL,
                    CONSTRAINT "FK_milestones_eras_linked_era_id" FOREIGN KEY ("linked_era_id") REFERENCES "eras" ("era_id") ON DELETE SET NULL
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS "IX_milestones_date" ON "milestones" ("date");
                CREATE INDEX IF NOT EXISTS "IX_milestones_linked_era_id" ON "milestones" ("linked_era_id");
                CREATE INDEX IF NOT EXISTS "IX_milestones_type" ON "milestones" ("type");
                """);

            await EnsureTableAsync(connection, existingTables, "saved_searches", logger,
                """
                CREATE TABLE IF NOT EXISTS "saved_searches" (
                    "saved_search_id" TEXT NOT NULL CONSTRAINT "PK_saved_searches" PRIMARY KEY,
                    "name" TEXT NOT NULL,
                    "search_term" TEXT NULL,
                    "categories" TEXT NULL,
                    "tag_ids" TEXT NULL,
                    "person_ids" TEXT NULL,
                    "location_ids" TEXT NULL,
                    "era_ids" TEXT NULL,
                    "start_date" TEXT NULL,
                    "end_date" TEXT NULL,
                    "has_audio" INTEGER NULL,
                    "has_transcript" INTEGER NULL,
                    "min_confidence" REAL NULL,
                    "sort_by" TEXT NOT NULL,
                    "page_size" INTEGER NOT NULL,
                    "is_favorite" INTEGER NOT NULL,
                    "use_count" INTEGER NOT NULL,
                    "last_used_at" TEXT NULL,
                    "created_at" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS "IX_saved_searches_name" ON "saved_searches" ("name");
                CREATE INDEX IF NOT EXISTS "IX_saved_searches_is_favorite" ON "saved_searches" ("is_favorite");
                CREATE INDEX IF NOT EXISTS "IX_saved_searches_last_used_at" ON "saved_searches" ("last_used_at");
                """);

            await EnsureTableAsync(connection, existingTables, "drafts", logger,
                """
                CREATE TABLE IF NOT EXISTS "drafts" (
                    "draft_id" TEXT NOT NULL CONSTRAINT "PK_drafts" PRIMARY KEY,
                    "draft_type" TEXT NOT NULL,
                    "title" TEXT NOT NULL,
                    "payload_json" TEXT NOT NULL,
                    "created_at" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS "IX_drafts_draft_type" ON "drafts" ("draft_type");
                CREATE INDEX IF NOT EXISTS "IX_drafts_updated_at" ON "drafts" ("updated_at");
                """);

            // Media attachments (2026-08 F2). New tables may carry their FK
            // inline in CREATE TABLE (unlike ALTER TABLE ADD COLUMN, which
            // cannot add one). media_type is an INTEGER enum
            // (0=Image 1=Video 2=Audio 3=Document).
            await EnsureTableAsync(connection, existingTables, "event_media", logger,
                """
                CREATE TABLE IF NOT EXISTS "event_media" (
                    "media_id" TEXT NOT NULL CONSTRAINT "PK_event_media" PRIMARY KEY,
                    "event_id" TEXT NOT NULL,
                    "file_path" TEXT NOT NULL,
                    "thumbnail_path" TEXT NULL,
                    "media_type" INTEGER NOT NULL,
                    "caption" TEXT NULL,
                    "captured_at" TEXT NULL,
                    "latitude" REAL NULL,
                    "longitude" REAL NULL,
                    "file_size_bytes" INTEGER NULL,
                    "content_hash" TEXT NULL,
                    "sort_order" INTEGER NOT NULL,
                    "created_at" TEXT NOT NULL,
                    CONSTRAINT "FK_event_media_events_event_id" FOREIGN KEY ("event_id") REFERENCES "events" ("event_id") ON DELETE CASCADE
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS "IX_event_media_event_id" ON "event_media" ("event_id");
                CREATE INDEX IF NOT EXISTS "IX_event_media_content_hash" ON "event_media" ("content_hash");
                """);

            // Guided recall prompts (2026-08 F6). kind/status/dismiss_reason
            // are INTEGER enums (RecallPromptKind / RecallPromptStatus /
            // DismissReason); the (kind, entity_id) index backs the dedupe
            // lookup that keeps the app from ever re-asking a question.
            await EnsureTableAsync(connection, existingTables, "recall_prompts", logger,
                """
                CREATE TABLE IF NOT EXISTS "recall_prompts" (
                    "prompt_id" TEXT NOT NULL CONSTRAINT "PK_recall_prompts" PRIMARY KEY,
                    "kind" INTEGER NOT NULL,
                    "question" TEXT NOT NULL,
                    "anchor_start" TEXT NULL,
                    "anchor_end" TEXT NULL,
                    "entity_id" TEXT NULL,
                    "entity_name" TEXT NULL,
                    "status" INTEGER NOT NULL,
                    "dismiss_reason" INTEGER NOT NULL,
                    "snoozed_until" TEXT NULL,
                    "queue_id" TEXT NULL,
                    "created_at" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS "IX_recall_prompts_status" ON "recall_prompts" ("status");
                CREATE INDEX IF NOT EXISTS "IX_recall_prompts_kind_entity_id" ON "recall_prompts" ("kind", "entity_id");
                """);

            // Person aliases (2026-08 F9). The alias unique index is
            // case-insensitive (COLLATE NOCASE): a spelling belongs to at most
            // one person. New tables may carry their FK inline in CREATE TABLE
            // (unlike ALTER TABLE ADD COLUMN, which cannot add one).
            await EnsureTableAsync(connection, existingTables, "person_aliases", logger,
                """
                CREATE TABLE IF NOT EXISTS "person_aliases" (
                    "alias_id" TEXT NOT NULL CONSTRAINT "PK_person_aliases" PRIMARY KEY,
                    "person_id" TEXT NOT NULL,
                    "alias" TEXT NOT NULL,
                    "created_at" TEXT NOT NULL,
                    CONSTRAINT "FK_person_aliases_people_person_id" FOREIGN KEY ("person_id") REFERENCES "people" ("person_id") ON DELETE CASCADE
                );
                """,
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_person_aliases_alias" ON "person_aliases" ("alias" COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS "IX_person_aliases_person_id" ON "person_aliases" ("person_id");
                """);

            // Event revision history (2026-08 F12). Deliberately NO foreign
            // key to events - history must outlive event deletion (matches
            // OnModelCreating, which configures no relationship). revision_kind
            // is an INTEGER enum (RevisionKind: 0=Created 1=Edited 2=Approved
            // 3=Imported 4=Merged 5=Restored). The composite index is
            // descending on revised_at to serve newest-first history reads.
            await EnsureTableAsync(connection, existingTables, "event_revisions", logger,
                """
                CREATE TABLE IF NOT EXISTS "event_revisions" (
                    "revision_id" TEXT NOT NULL CONSTRAINT "PK_event_revisions" PRIMARY KEY,
                    "event_id" TEXT NOT NULL,
                    "revised_at" TEXT NOT NULL,
                    "revision_kind" INTEGER NOT NULL,
                    "snapshot_json" TEXT NOT NULL,
                    "changed_fields_csv" TEXT NULL
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS "IX_event_revisions_event_id_revised_at" ON "event_revisions" ("event_id", "revised_at" DESC);
                """);

            // ---- Sync groundwork tables (iOS companion design, Phase 0) ----

            await EnsureTableAsync(connection, existingTables, "captures", logger,
                """
                CREATE TABLE IF NOT EXISTS "captures" (
                    "capture_id" TEXT NOT NULL CONSTRAINT "PK_captures" PRIMARY KEY,
                    "source_device_id" TEXT NULL,
                    "source_platform" TEXT NOT NULL,
                    "capture_type" TEXT NOT NULL,
                    "created_at_utc" TEXT NOT NULL,
                    "captured_at_utc" TEXT NOT NULL,
                    "timezone_id" TEXT NULL,
                    "local_offset_minutes" INTEGER NULL,
                    "status" TEXT NOT NULL,
                    "title_hint" TEXT NULL,
                    "user_note" TEXT NULL,
                    "trip_id" TEXT NULL,
                    "client_schema_version" INTEGER NOT NULL,
                    "server_revision" INTEGER NOT NULL,
                    "deleted_at_utc" TEXT NULL
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS "IX_captures_status" ON "captures" ("status");
                CREATE INDEX IF NOT EXISTS "IX_captures_captured_at_utc" ON "captures" ("captured_at_utc");
                """);

            await EnsureTableAsync(connection, existingTables, "capture_artifacts", logger,
                """
                CREATE TABLE IF NOT EXISTS "capture_artifacts" (
                    "artifact_id" TEXT NOT NULL CONSTRAINT "PK_capture_artifacts" PRIMARY KEY,
                    "capture_id" TEXT NOT NULL,
                    "artifact_type" TEXT NOT NULL,
                    "storage_locator" TEXT NOT NULL,
                    "media_type" TEXT NULL,
                    "byte_length" INTEGER NULL,
                    "sha256" TEXT NULL,
                    "encryption_scheme" TEXT NULL,
                    "created_at_utc" TEXT NOT NULL,
                    "upload_state" TEXT NOT NULL,
                    CONSTRAINT "FK_capture_artifacts_captures_capture_id" FOREIGN KEY ("capture_id") REFERENCES "captures" ("capture_id") ON DELETE CASCADE
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS "IX_capture_artifacts_capture_id" ON "capture_artifacts" ("capture_id");
                CREATE INDEX IF NOT EXISTS "IX_capture_artifacts_sha256" ON "capture_artifacts" ("sha256");
                """);

            await EnsureTableAsync(connection, existingTables, "sync_outbox", logger,
                """
                CREATE TABLE IF NOT EXISTS "sync_outbox" (
                    "outbox_id" INTEGER NOT NULL CONSTRAINT "PK_sync_outbox" PRIMARY KEY AUTOINCREMENT,
                    "entity_type" TEXT NOT NULL,
                    "entity_id" TEXT NOT NULL,
                    "operation" TEXT NOT NULL,
                    "payload_json" TEXT NULL,
                    "created_at" TEXT NOT NULL,
                    "delivered_at" TEXT NULL,
                    "attempt_count" INTEGER NOT NULL,
                    "last_error" TEXT NULL
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS "IX_sync_outbox_delivered_at" ON "sync_outbox" ("delivered_at");
                CREATE INDEX IF NOT EXISTS "IX_sync_outbox_entity_type_entity_id" ON "sync_outbox" ("entity_type", "entity_id");
                """);

            // ---- Missing columns on pre-existing tables ----
            // Note: SQLite's ALTER TABLE ADD COLUMN cannot add foreign key
            // constraints, so eras.category_id is added without one; the FK is
            // only present on databases created fresh by EnsureCreated. EF
            // does not require the constraint to query/save the relationship.

            await EnsureColumnsAsync(connection, existingTables, "eras", logger, new[]
            {
                ("subtitle",       "\"subtitle\" TEXT NULL"),
                ("category_id",    "\"category_id\" TEXT NULL"),
                ("color_override", "\"color_override\" TEXT NULL"),
                ("display_order",  "\"display_order\" INTEGER NOT NULL DEFAULT 0"),
                ("notes",          "\"notes\" TEXT NULL"),
            });

            await EnsureColumnsAsync(connection, existingTables, "events", logger, new[]
            {
                ("location",   "\"location\" TEXT NULL"),
                ("confidence", "\"confidence\" REAL NULL"),
                // Date precision & uncertainty (2026-08). DEFAULT 1 = DatePrecision.Day
                // preserves the meaning of every pre-existing row.
                ("date_precision",    "\"date_precision\" INTEGER NOT NULL DEFAULT 1"),
                ("earliest_possible", "\"earliest_possible\" TEXT NULL"),
                ("latest_possible",   "\"latest_possible\" TEXT NULL"),
                // On This Day resurfacing (2026-08): view tracking for the
                // neglected-memory random bias. DEFAULT 0 keeps every
                // pre-existing row "never viewed".
                ("last_viewed_at",    "\"last_viewed_at\" TEXT NULL"),
                ("view_count",        "\"view_count\" INTEGER NOT NULL DEFAULT 0"),
            });

            // Date precision & uncertainty on pending events (2026-08), mirroring
            // the events columns so approval can copy them straight across.
            await EnsureColumnsAsync(connection, existingTables, "pending_events", logger, new[]
            {
                ("date_precision",    "\"date_precision\" INTEGER NOT NULL DEFAULT 1"),
                ("earliest_possible", "\"earliest_possible\" TEXT NULL"),
                ("latest_possible",   "\"latest_possible\" TEXT NULL"),
            });

            await EnsureColumnsAsync(connection, existingTables, "tags", logger, new[]
            {
                ("color", "\"color\" TEXT NULL"),
            });

            // Geographic data on locations (2026-08 F10): nullable coordinates
            // (REAL, decimal degrees), optional place classification, the
            // geocoder's canonical display name, and the geocoded-at stamp
            // (null = coordinates came from photo EXIF or a manual pin drop,
            // i.e. the place name never left the machine).
            await EnsureColumnsAsync(connection, existingTables, "locations", logger, new[]
            {
                ("latitude",       "\"latitude\" REAL NULL"),
                ("longitude",      "\"longitude\" REAL NULL"),
                ("place_type",     "\"place_type\" TEXT NULL"),
                ("canonical_name", "\"canonical_name\" TEXT NULL"),
                ("geocoded_at",    "\"geocoded_at\" TEXT NULL"),
            });

            // Contact-book columns on people (2026-08 people feature). NOT NULL
            // columns must carry constant defaults - SQLite's ALTER TABLE ADD
            // COLUMN requirement.
            await EnsureColumnsAsync(connection, existingTables, "people", logger, new[]
            {
                ("nickname",       "\"nickname\" TEXT NULL"),
                ("relationship",   "\"relationship\" TEXT NULL"),
                ("email",          "\"email\" TEXT NULL"),
                ("phone",          "\"phone\" TEXT NULL"),
                ("birthday",       "\"birthday\" TEXT NULL"),
                ("company",        "\"company\" TEXT NULL"),
                ("notes",          "\"notes\" TEXT NULL"),
                ("photo_path",     "\"photo_path\" TEXT NULL"),
                ("avatar_color",   "\"avatar_color\" TEXT NULL"),
                ("is_favorite",    "\"is_favorite\" INTEGER NOT NULL DEFAULT 0"),
                ("first_met_date", "\"first_met_date\" TEXT NULL"),
                ("updated_at",     "\"updated_at\" TEXT NOT NULL DEFAULT '2025-01-21 00:00:00'"),
                // Merge tombstone (2026-08 F9): non-null means "merged into
                // this person"; NULL keeps every pre-existing row living.
                ("merged_into_id", "\"merged_into_id\" TEXT NULL"),
            });

            // Text/paste capture columns on recording_queue (2026-08 text-source
            // feature): source_type discriminator (INTEGER enum, 0 = Audio, so
            // every pre-existing row stays an audio source), optional label and
            // the persisted transcript (pasted text for Text/Imported sources,
            // cached Whisper output for Audio sources). NOT NULL added columns
            // must carry constant defaults - SQLite's ALTER TABLE ADD COLUMN
            // requirement.
            await EnsureColumnsAsync(connection, existingTables, "recording_queue", logger, new[]
            {
                ("source_type",  "\"source_type\" INTEGER NOT NULL DEFAULT 0"),
                ("source_label", "\"source_label\" TEXT NULL"),
                ("transcript",   "\"transcript\" TEXT NULL"),
            });

            // Device-neutral provenance columns on recording_queue (design §7.1/§7.3).
            // Existing rows are Windows-local recordings, so source_platform
            // backfills to 'windows' and everything else stays NULL.
            await EnsureColumnsAsync(connection, existingTables, "recording_queue", logger, new[]
            {
                ("source_capture_id",  "\"source_capture_id\" TEXT NULL"),
                ("source_device_id",   "\"source_device_id\" TEXT NULL"),
                ("source_platform",    "\"source_platform\" TEXT NOT NULL DEFAULT 'windows'"),
                ("audio_artifact_id",  "\"audio_artifact_id\" TEXT NULL"),
                ("original_file_name", "\"original_file_name\" TEXT NULL"),
                ("content_sha256",     "\"content_sha256\" TEXT NULL"),
                ("sync_state",         "\"sync_state\" TEXT NULL"),
                ("received_at",        "\"received_at\" TEXT NULL"),
                ("processing_stage",   "\"processing_stage\" TEXT NULL"),
            });

            // Unique index enforcing idempotent ingestion, mirroring
            // OnModelCreating's HasIndex(q => q.SourceCaptureId).IsUnique().
            // SQLite treats NULLs as distinct, so legacy rows are unaffected.
            if (existingTables.Contains("recording_queue"))
            {
                await ExecuteAsync(connection,
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_recording_queue_source_capture_id\" ON \"recording_queue\" (\"source_capture_id\");");
            }

            // Index on the (possibly just-added) eras.category_id column,
            // mirroring OnModelCreating's HasIndex(e => e.CategoryId).
            if (existingTables.Contains("eras"))
            {
                await ExecuteAsync(connection,
                    "CREATE INDEX IF NOT EXISTS \"IX_eras_category_id\" ON \"eras\" (\"category_id\");");
            }

            // Index on the (possibly just-added) people.is_favorite column,
            // mirroring OnModelCreating's HasIndex(p => p.IsFavorite).
            if (existingTables.Contains("people"))
            {
                await ExecuteAsync(connection,
                    "CREATE INDEX IF NOT EXISTS \"IX_people_is_favorite\" ON \"people\" (\"is_favorite\");");

                // Index on the (possibly just-added) people.merged_into_id
                // column, mirroring OnModelCreating's HasIndex(p => p.MergedIntoId).
                await ExecuteAsync(connection,
                    "CREATE INDEX IF NOT EXISTS \"IX_people_merged_into_id\" ON \"people\" (\"merged_into_id\");");
            }

            // ---- Case-insensitive identity (2026-08 F9) ----
            // Older builds created the unique indexes on people.name,
            // tags.tag_name and locations.name with BINARY collation, letting
            // case-variant duplicates ("Sarah"/"sarah") coexist. The current
            // model is NOCASE. Merge each case-variant duplicate group into
            // the row with the most junction links (backfilling the keeper's
            // empty contact columns from each loser, in MergeAsync's spirit,
            // so user-entered data survives), then rebuild the unique index
            // with COLLATE NOCASE. Each table runs in its own transaction
            // inside its own try/catch: a failure leaves that table untouched
            // and never crashes startup.
            await RepairCaseInsensitiveIdentityAsync(connection, existingTables, logger);

            // Seed parity with AppDbContext.SeedDefaultSettings (HasData);
            // EnsureCreated is a no-op on databases created by older builds, so
            // any settings row seeded after that build is missing there.
            // INSERT OR IGNORE keeps this idempotent and never overwrites a
            // value the user has since changed. NOTE (F11): embedding_model was
            // corrected from 'onnx-text-embedding' to 'all-MiniLM-L6-v2';
            // databases that already carry the old value keep it (never
            // overwritten) — the embedding router keys off embedding_provider
            // and ignores unknown stored model strings.
            if (existingTables.Contains("app_settings"))
            {
                await ExecuteAsync(connection,
                    """
                    INSERT OR IGNORE INTO "app_settings" ("setting_key", "setting_value", "updated_at") VALUES
                        ('theme',                    'light',                    '2025-01-21 00:00:00'),
                        ('default_zoom_level',       'month',                    '2025-01-21 00:00:00'),
                        ('llm_provider',             'anthropic',                '2025-01-21 00:00:00'),
                        ('llm_model',                'claude-sonnet-4-20250514', '2025-01-21 00:00:00'),
                        ('llm_max_tokens',           '4000',                     '2025-01-21 00:00:00'),
                        ('llm_temperature',          '0.3',                      '2025-01-21 00:00:00'),
                        ('rag_similarity_threshold', '0.75',                     '2025-01-21 00:00:00'),
                        ('embedding_provider',       'local',                    '2025-01-21 00:00:00'),
                        ('embedding_model',          'all-MiniLM-L6-v2',         '2025-01-21 00:00:00'),
                        ('embedding_dimension',      '384',                      '2025-01-21 00:00:00'),
                        ('embedding_api_key',        '',                         '2025-01-21 00:00:00'),
                        ('llm_base_url',             '',                         '2025-01-21 00:00:00'),
                        ('local_model_path',         '',                         '2025-01-21 00:00:00'),
                        ('auto_generate_embeddings', 'true',                     '2025-01-21 00:00:00'),
                        ('ask_top_k',                '12',                       '2025-01-21 00:00:00'),
                        ('ask_include_transcripts',  'false',                    '2025-01-21 00:00:00'),
                        ('audio_input_device_id',    '',                         '2025-01-21 00:00:00'),
                        ('whisper_model',            'base',                     '2025-01-21 00:00:00'),
                        ('whisper_language',         'auto',                     '2025-01-21 00:00:00'),
                        ('home_is_default_page',     'true',                     '2025-01-21 00:00:00'),
                        ('daily_toast_enabled',      'true',                     '2025-01-21 00:00:00'),
                        ('weekly_digest_enabled',    'true',                     '2025-01-21 00:00:00'),
                        ('backup_destination',       '',                         '2025-01-21 00:00:00'),
                        ('backup_include_media',     'true',                     '2025-01-21 00:00:00'),
                        ('backup_schedule',          'off',                      '2025-01-21 00:00:00'),
                        ('revision_history_enabled', 'true',                     '2025-01-21 00:00:00'),
                        ('geocoding_enabled',        'false',                    '2025-01-21 00:00:00'),
                        ('geocoding_provider',       'nominatim',                '2025-01-21 00:00:00'),
                        ('map_default_zoom',         '1.0',                      '2025-01-21 00:00:00');
                    """);
                // Note: last_toast_date is deliberately NOT seeded (here or in
                // AppDbContext.SeedDefaultSettings) - an absent row means "the
                // On This Day toast has never been shown". Likewise
                // last_backup_at is NOT seeded - absent means "never backed up",
                // and timeline_projection_backfill_pending - absent means "no
                // backfill owed". Keys that no reader ever consulted
                // (audio_quality, stt_engine, stt_config, rag_auto_run_enabled,
                // rag_schedule, send_transcripts_only, require_confirmation) were
                // dropped from both seeds; rows older databases carry are left
                // alone and ignored.
            }
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Creates a table (plus its indexes and optional seed data) when it is
    /// missing from the database. Failures are logged and swallowed so one
    /// broken repair does not prevent the remaining repairs.
    /// </summary>
    private static async Task EnsureTableAsync(
        DbConnection connection,
        HashSet<string> existingTables,
        string tableName,
        ILogger? logger,
        params string[] ddlBatches)
    {
        if (existingTables.Contains(tableName))
        {
            return;
        }

        try
        {
            foreach (var ddl in ddlBatches)
            {
                await ExecuteAsync(connection, ddl);
            }

            existingTables.Add(tableName);
            logger?.LogInformation("SchemaUpgrader: created missing table '{Table}'.", tableName);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "SchemaUpgrader: failed to create missing table '{Table}'.", tableName);
        }
    }

    /// <summary>
    /// Adds any missing columns to an existing table. Failures are logged and
    /// swallowed so one broken repair does not prevent the remaining repairs.
    /// </summary>
    private static async Task EnsureColumnsAsync(
        DbConnection connection,
        HashSet<string> existingTables,
        string tableName,
        ILogger? logger,
        (string ColumnName, string ColumnDdl)[] columns)
    {
        // If the table itself is missing (very old or corrupted database),
        // there is nothing to alter; EnsureCreated only builds full schemas
        // for brand-new databases.
        if (!existingTables.Contains(tableName))
        {
            logger?.LogWarning("SchemaUpgrader: table '{Table}' not found; skipping column repairs for it.", tableName);
            return;
        }

        HashSet<string> existingColumns;
        try
        {
            existingColumns = await GetColumnNamesAsync(connection, tableName);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "SchemaUpgrader: failed to inspect columns of '{Table}'.", tableName);
            return;
        }

        foreach (var (columnName, columnDdl) in columns)
        {
            if (existingColumns.Contains(columnName))
            {
                continue;
            }

            try
            {
                await ExecuteAsync(connection, $"ALTER TABLE \"{tableName}\" ADD COLUMN {columnDdl};");
                logger?.LogInformation("SchemaUpgrader: added missing column '{Table}.{Column}'.", tableName, columnName);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "SchemaUpgrader: failed to add column '{Table}.{Column}'.", tableName, columnName);
            }
        }
    }

    /// <summary>
    /// Rebuilds the unique name indexes of people/tags/locations with
    /// COLLATE NOCASE, merging case-variant duplicate rows first. See the
    /// call site comment for the full contract.
    /// </summary>
    private static async Task RepairCaseInsensitiveIdentityAsync(
        DbConnection connection,
        HashSet<string> existingTables,
        ILogger? logger)
    {
        await RepairTableIdentityAsync(connection, existingTables, logger,
            table: "people", idColumn: "person_id", nameColumn: "name",
            junctionTable: "event_people", junctionFkColumn: "person_id",
            isPeople: true,
            // The contact columns PersonService.MergeAsync preserves: each
            // loser's values backfill the keeper's empty columns before the
            // loser row is deleted, and is_favorite ORs together via MAX.
            backfillCoalesceColumns: new[]
            {
                "nickname", "relationship", "email", "phone", "birthday",
                "company", "notes", "photo_path", "avatar_color",
                "first_met_date"
            },
            backfillMaxColumns: new[] { "is_favorite" });

        await RepairTableIdentityAsync(connection, existingTables, logger,
            table: "tags", idColumn: "tag_id", nameColumn: "tag_name",
            junctionTable: "event_tags", junctionFkColumn: "tag_id",
            isPeople: false,
            backfillCoalesceColumns: new[] { "color" },
            backfillMaxColumns: Array.Empty<string>());

        await RepairTableIdentityAsync(connection, existingTables, logger,
            table: "locations", idColumn: "location_id", nameColumn: "name",
            junctionTable: "event_locations", junctionFkColumn: "location_id",
            isPeople: false,
            backfillCoalesceColumns: Array.Empty<string>(),
            backfillMaxColumns: Array.Empty<string>());
    }

    /// <summary>
    /// One table's case-insensitive identity repair: skip when the unique
    /// index over the name column is already NOCASE; otherwise merge
    /// case-variant duplicate groups into the row with the most junction
    /// links (ties prefer the row with the most populated backfill columns,
    /// then the first row), repointing junctions dedup-safely, backfilling
    /// the keeper's empty backfill columns from each loser before that loser
    /// is deleted (for people also keeping the losers' names as aliases of
    /// the keeper), then drop the BINARY unique index and recreate it with
    /// COLLATE NOCASE. All work happens inside a single transaction so a
    /// failure leaves the table untouched; the failure itself is logged and
    /// swallowed (never fatal), per this file's contract.
    /// </summary>
    private static async Task RepairTableIdentityAsync(
        DbConnection connection,
        HashSet<string> existingTables,
        ILogger? logger,
        string table,
        string idColumn,
        string nameColumn,
        string junctionTable,
        string junctionFkColumn,
        bool isPeople,
        string[] backfillCoalesceColumns,
        string[] backfillMaxColumns)
    {
        if (!existingTables.Contains(table))
        {
            return;
        }

        try
        {
            var (indexName, collation) = await FindUniqueIndexAsync(connection, table, nameColumn);
            if (string.Equals(collation, "NOCASE", StringComparison.OrdinalIgnoreCase))
            {
                // Already case-insensitive (fresh EF database or a previous
                // successful repair) - idempotent no-op.
                return;
            }

            var junctionExists = existingTables.Contains(junctionTable);
            var aliasTableExists = existingTables.Contains("person_aliases");
            var mergedGroups = 0;

            // Backfill columns filtered against the live schema: a partially
            // repaired older database (some contact columns still missing)
            // must still get its index fixed rather than failing the whole
            // per-table transaction on an absent column.
            var coalesceColumns = new List<string>();
            var maxColumns = new List<string>();
            if (backfillCoalesceColumns.Length > 0 || backfillMaxColumns.Length > 0)
            {
                var tableColumns = await GetColumnNamesAsync(connection, table);
                coalesceColumns.AddRange(backfillCoalesceColumns.Where(tableColumns.Contains));
                maxColumns.AddRange(backfillMaxColumns.Where(tableColumns.Contains));
            }

            // Raw BEGIN/COMMIT instead of DbConnection.BeginTransaction:
            // Microsoft.Data.Sqlite requires every command to carry the
            // connection's ADO transaction object, which the shared
            // ExecuteAsync helper does not do.
            await ExecuteAsync(connection, "BEGIN IMMEDIATE;");
            try
            {
                // 1. Merge case-variant duplicate groups (same lower(name)).
                var groups = await LoadCaseVariantGroupsAsync(connection, table, idColumn, nameColumn);
                foreach (var group in groups)
                {
                    // Keeper = the row with the most junction links; ties
                    // prefer the row with the most populated (non-null)
                    // backfill columns - a user-enriched contact card beats
                    // an extraction-created bare row - and the first row wins
                    // remaining ties, preserving insertion order.
                    var keeper = group[0];
                    var bestLinks = -1L;
                    var bestFilled = -1L;
                    foreach (var row in group)
                    {
                        var links = junctionExists
                            ? await ExecuteScalarLongAsync(connection,
                                $"SELECT COUNT(*) FROM \"{junctionTable}\" WHERE \"{junctionFkColumn}\" = @id",
                                ("@id", row.Id))
                            : 0L;
                        var filled = await CountFilledColumnsAsync(
                            connection, table, idColumn, row.Id, coalesceColumns);
                        if (links > bestLinks || (links == bestLinks && filled > bestFilled))
                        {
                            bestLinks = links;
                            bestFilled = filled;
                            keeper = row;
                        }
                    }

                    foreach (var loser in group)
                    {
                        if (ReferenceEquals(loser, keeper))
                        {
                            continue;
                        }

                        // Backfill the keeper the way the in-app MergeAsync
                        // would have: each empty (NULL) contact column takes
                        // the loser's value and flag columns OR together via
                        // MAX, so user-entered data survives the loser row's
                        // deletion below.
                        await BackfillKeeperFromLoserAsync(
                            connection, table, idColumn, keeper.Id, loser.Id,
                            coalesceColumns, maxColumns);

                        if (junctionExists)
                        {
                            // Repoint junction rows dedup-safely: rows that
                            // would duplicate an existing keeper link are
                            // skipped by OR IGNORE, then deleted.
                            await ExecuteAsync(connection,
                                $"UPDATE OR IGNORE \"{junctionTable}\" SET \"{junctionFkColumn}\" = @keeper WHERE \"{junctionFkColumn}\" = @loser;",
                                ("@keeper", keeper.Id), ("@loser", loser.Id));
                            await ExecuteAsync(connection,
                                $"DELETE FROM \"{junctionTable}\" WHERE \"{junctionFkColumn}\" = @loser;",
                                ("@loser", loser.Id));
                        }

                        if (isPeople)
                        {
                            if (aliasTableExists)
                            {
                                // Move the loser's aliases to the keeper (OR
                                // IGNORE tolerates NOCASE-unique collisions),
                                // then keep the loser's own spelling as an
                                // alias of the keeper.
                                await ExecuteAsync(connection,
                                    "UPDATE OR IGNORE \"person_aliases\" SET \"person_id\" = @keeper WHERE \"person_id\" = @loser;",
                                    ("@keeper", keeper.Id), ("@loser", loser.Id));
                                await ExecuteAsync(connection,
                                    "DELETE FROM \"person_aliases\" WHERE \"person_id\" = @loser;",
                                    ("@loser", loser.Id));
                                await ExecuteAsync(connection,
                                    "INSERT OR IGNORE INTO \"person_aliases\" (\"alias_id\", \"person_id\", \"alias\", \"created_at\") VALUES (@aliasId, @keeper, @alias, @createdAt);",
                                    ("@aliasId", Guid.NewGuid().ToString()),
                                    ("@keeper", keeper.Id),
                                    ("@alias", loser.Name),
                                    ("@createdAt", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
                            }

                            // Keep merge chains resolvable: tombstones that
                            // pointed at the loser now point at the keeper.
                            await ExecuteAsync(connection,
                                "UPDATE \"people\" SET \"merged_into_id\" = @keeper WHERE \"merged_into_id\" = @loser;",
                                ("@keeper", keeper.Id), ("@loser", loser.Id));
                        }

                        await ExecuteAsync(connection,
                            $"DELETE FROM \"{table}\" WHERE \"{idColumn}\" = @loser;",
                            ("@loser", loser.Id));

                        logger?.LogInformation(
                            "SchemaUpgrader: merged case-variant {Table} row '{LoserName}' ({LoserId}) into '{KeeperName}' ({KeeperId}).",
                            table, loser.Name, loser.Id, keeper.Name, keeper.Id);
                    }

                    mergedGroups++;
                }

                // 2. Rebuild the unique index with COLLATE NOCASE. SQLite
                //    autoindexes (from inline UNIQUE constraints) cannot be
                //    dropped; the additional NOCASE index is still created -
                //    it is the stricter of the two, so both can coexist.
                var canDropOldIndex = indexName != null &&
                    !indexName.StartsWith("sqlite_autoindex", StringComparison.OrdinalIgnoreCase);
                if (canDropOldIndex)
                {
                    await ExecuteAsync(connection, $"DROP INDEX IF EXISTS \"{indexName}\";");
                }
                var newIndexName = canDropOldIndex ? indexName! : $"IX_{table}_{nameColumn}";
                await ExecuteAsync(connection,
                    $"CREATE UNIQUE INDEX IF NOT EXISTS \"{newIndexName}\" ON \"{table}\" (\"{nameColumn}\" COLLATE NOCASE);");

                await ExecuteAsync(connection, "COMMIT;");

                logger?.LogInformation(
                    "SchemaUpgrader: rebuilt unique index on {Table}.{Column} with COLLATE NOCASE ({Groups} case-variant duplicate group(s) merged).",
                    table, nameColumn, mergedGroups);
            }
            catch
            {
                try
                {
                    await ExecuteAsync(connection, "ROLLBACK;");
                }
                catch
                {
                    // The transaction may already be gone; the outer catch
                    // logs the original failure.
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex,
                "SchemaUpgrader: case-insensitive identity repair failed for '{Table}'; table left unchanged.",
                table);
        }
    }

    /// <summary>One (id, name) row participating in the identity repair.</summary>
    private sealed record IdentityRow(string Id, string Name);

    /// <summary>
    /// Loads groups of rows whose names differ only by case. Grouping uses
    /// invariant lower-casing, a superset of SQLite's ASCII-only NOCASE
    /// folding, so every group the NOCASE unique index would reject is merged.
    /// </summary>
    private static async Task<List<List<IdentityRow>>> LoadCaseVariantGroupsAsync(
        DbConnection connection,
        string table,
        string idColumn,
        string nameColumn)
    {
        var rows = new List<IdentityRow>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT \"{idColumn}\", \"{nameColumn}\" FROM \"{table}\" ORDER BY rowid";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new IdentityRow(reader.GetString(0), reader.GetString(1)));
            }
        }

        return rows
            .GroupBy(r => r.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();
    }

    /// <summary>
    /// Counts how many of the given columns hold a non-null value on one row
    /// (keeper-selection tie-break). Returns 0 when no columns are given.
    /// Column/table names are compile-time constants (never user input); the
    /// row id is data and is parameterized.
    /// </summary>
    private static async Task<long> CountFilledColumnsAsync(
        DbConnection connection,
        string table,
        string idColumn,
        string rowId,
        List<string> columns)
    {
        if (columns.Count == 0)
        {
            return 0;
        }

        // In SQLite ("col" IS NOT NULL) evaluates to 0/1, so the sum is the
        // populated-column count.
        var filledExpression = string.Join(" + ", columns.Select(c => $"(\"{c}\" IS NOT NULL)"));
        return await ExecuteScalarLongAsync(connection,
            $"SELECT {filledExpression} FROM \"{table}\" WHERE \"{idColumn}\" = @id",
            ("@id", rowId));
    }

    /// <summary>
    /// Copies a loser row's values onto the keeper before the loser is
    /// deleted, mirroring PersonService.MergeAsync: COALESCE fills only the
    /// keeper's NULL columns, MAX ORs flag columns (is_favorite). One
    /// parameterized UPDATE per loser; the loser's values flow through
    /// per-column subqueries so no data value is ever interpolated into SQL
    /// (column/table names are compile-time constants). Idempotent: once a
    /// keeper column is non-null, COALESCE leaves it alone.
    /// </summary>
    private static async Task BackfillKeeperFromLoserAsync(
        DbConnection connection,
        string table,
        string idColumn,
        string keeperId,
        string loserId,
        List<string> coalesceColumns,
        List<string> maxColumns)
    {
        var assignments = new List<string>();
        foreach (var column in coalesceColumns)
        {
            assignments.Add(
                $"\"{column}\" = COALESCE(\"{column}\", (SELECT \"{column}\" FROM \"{table}\" WHERE \"{idColumn}\" = @loser))");
        }

        foreach (var column in maxColumns)
        {
            assignments.Add(
                $"\"{column}\" = MAX(\"{column}\", COALESCE((SELECT \"{column}\" FROM \"{table}\" WHERE \"{idColumn}\" = @loser), 0))");
        }

        if (assignments.Count == 0)
        {
            return;
        }

        await ExecuteAsync(connection,
            $"UPDATE \"{table}\" SET {string.Join(", ", assignments)} WHERE \"{idColumn}\" = @keeper;",
            ("@keeper", keeperId), ("@loser", loserId));
    }

    /// <summary>
    /// Finds the unique index covering exactly the given column and reports
    /// its collation (via PRAGMA index_list / index_info / index_xinfo).
    /// Returns (null, null) when no such index exists.
    /// </summary>
    private static async Task<(string? IndexName, string? Collation)> FindUniqueIndexAsync(
        DbConnection connection,
        string table,
        string column)
    {
        var uniqueIndexes = new List<string>();
        using (var listCommand = connection.CreateCommand())
        {
            // PRAGMA index_list columns: seq, name, unique, origin, partial.
            // Table names cannot be parameterized in PRAGMA statements; the
            // names used here are compile-time constants.
            listCommand.CommandText = $"PRAGMA index_list(\"{table}\")";
            await using var reader = await listCommand.ExecuteReaderAsync();
            var nameOrdinal = reader.GetOrdinal("name");
            var uniqueOrdinal = reader.GetOrdinal("unique");
            while (await reader.ReadAsync())
            {
                if (Convert.ToInt64(reader.GetValue(uniqueOrdinal)) == 1)
                {
                    uniqueIndexes.Add(reader.GetString(nameOrdinal));
                }
            }
        }

        foreach (var indexName in uniqueIndexes)
        {
            // PRAGMA index_xinfo columns: seqno, cid, name, desc, coll, key.
            // key=1 rows are the indexed columns (key=0 rows are the rowid tail).
            using var infoCommand = connection.CreateCommand();
            infoCommand.CommandText = $"PRAGMA index_xinfo(\"{indexName}\")";
            var keyColumns = new List<(string? Name, string Collation)>();
            await using var reader = await infoCommand.ExecuteReaderAsync();
            var nameOrdinal = reader.GetOrdinal("name");
            var collOrdinal = reader.GetOrdinal("coll");
            var keyOrdinal = reader.GetOrdinal("key");
            while (await reader.ReadAsync())
            {
                if (Convert.ToInt64(reader.GetValue(keyOrdinal)) != 1)
                {
                    continue;
                }

                keyColumns.Add((
                    reader.IsDBNull(nameOrdinal) ? null : reader.GetString(nameOrdinal),
                    reader.GetString(collOrdinal)));
            }

            if (keyColumns.Count == 1 &&
                string.Equals(keyColumns[0].Name, column, StringComparison.OrdinalIgnoreCase))
            {
                return (indexName, keyColumns[0].Collation);
            }
        }

        return (null, null);
    }

    private static async Task<long> ExecuteScalarLongAsync(
        DbConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync();
        return result == null || result is DBNull ? 0L : Convert.ToInt64(result);
    }

    private static async Task<HashSet<string>> GetTableNamesAsync(DbConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(DbConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        // Table names cannot be parameterized in PRAGMA statements; the names
        // used here are compile-time constants, never user input.
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        await using var reader = await command.ExecuteReaderAsync();
        var nameOrdinal = reader.GetOrdinal("name");
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(nameOrdinal));
        }

        return columns;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }
}
