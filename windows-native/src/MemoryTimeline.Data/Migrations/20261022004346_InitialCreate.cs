using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace MemoryTimeline.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    setting_key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    setting_value = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings", x => x.setting_key);
                });

            migrationBuilder.CreateTable(
                name: "captures",
                columns: table => new
                {
                    capture_id = table.Column<string>(type: "TEXT", nullable: false),
                    source_device_id = table.Column<string>(type: "TEXT", nullable: true),
                    source_platform = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    capture_type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    captured_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    timezone_id = table.Column<string>(type: "TEXT", nullable: true),
                    local_offset_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    title_hint = table.Column<string>(type: "TEXT", nullable: true),
                    user_note = table.Column<string>(type: "TEXT", nullable: true),
                    trip_id = table.Column<string>(type: "TEXT", nullable: true),
                    client_schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    server_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_captures", x => x.capture_id);
                });

            migrationBuilder.CreateTable(
                name: "drafts",
                columns: table => new
                {
                    draft_id = table.Column<string>(type: "TEXT", nullable: false),
                    draft_type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drafts", x => x.draft_id);
                });

            migrationBuilder.CreateTable(
                name: "era_categories",
                columns: table => new
                {
                    category_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    icon_glyph = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    default_color = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    sort_order = table.Column<int>(type: "INTEGER", nullable: false),
                    is_visible = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_era_categories", x => x.category_id);
                });

            migrationBuilder.CreateTable(
                name: "event_revisions",
                columns: table => new
                {
                    revision_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    revised_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revision_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    snapshot_json = table.Column<string>(type: "TEXT", nullable: false),
                    changed_fields_csv = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_revisions", x => x.revision_id);
                });

            migrationBuilder.CreateTable(
                name: "locations",
                columns: table => new
                {
                    location_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    latitude = table.Column<double>(type: "REAL", nullable: true),
                    longitude = table.Column<double>(type: "REAL", nullable: true),
                    place_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    canonical_name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    geocoded_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_locations", x => x.location_id);
                });

            migrationBuilder.CreateTable(
                name: "people",
                columns: table => new
                {
                    person_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    nickname = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    relationship = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    birthday = table.Column<DateTime>(type: "TEXT", nullable: true),
                    company = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    photo_path = table.Column<string>(type: "TEXT", nullable: true),
                    avatar_color = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    is_favorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    first_met_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    merged_into_id = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_people", x => x.person_id);
                });

            migrationBuilder.CreateTable(
                name: "recall_prompts",
                columns: table => new
                {
                    prompt_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    question = table.Column<string>(type: "TEXT", nullable: false),
                    anchor_start = table.Column<DateTime>(type: "TEXT", nullable: true),
                    anchor_end = table.Column<DateTime>(type: "TEXT", nullable: true),
                    entity_id = table.Column<string>(type: "TEXT", nullable: true),
                    entity_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    dismiss_reason = table.Column<int>(type: "INTEGER", nullable: false),
                    snoozed_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    queue_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recall_prompts", x => x.prompt_id);
                });

            migrationBuilder.CreateTable(
                name: "recording_queue",
                columns: table => new
                {
                    queue_id = table.Column<string>(type: "TEXT", nullable: false),
                    audio_file_path = table.Column<string>(type: "TEXT", nullable: false),
                    source_type = table.Column<int>(type: "INTEGER", nullable: false),
                    source_label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    transcript = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    duration_seconds = table.Column<double>(type: "REAL", nullable: true),
                    file_size_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    processed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", nullable: true),
                    source_capture_id = table.Column<string>(type: "TEXT", nullable: true),
                    source_device_id = table.Column<string>(type: "TEXT", nullable: true),
                    source_platform = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    audio_artifact_id = table.Column<string>(type: "TEXT", nullable: true),
                    original_file_name = table.Column<string>(type: "TEXT", nullable: true),
                    content_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    sync_state = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    received_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    processing_stage = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recording_queue", x => x.queue_id);
                });

            migrationBuilder.CreateTable(
                name: "saved_searches",
                columns: table => new
                {
                    saved_search_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    search_term = table.Column<string>(type: "TEXT", nullable: true),
                    categories = table.Column<string>(type: "TEXT", nullable: true),
                    tag_ids = table.Column<string>(type: "TEXT", nullable: true),
                    person_ids = table.Column<string>(type: "TEXT", nullable: true),
                    location_ids = table.Column<string>(type: "TEXT", nullable: true),
                    era_ids = table.Column<string>(type: "TEXT", nullable: true),
                    start_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    end_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    has_audio = table.Column<bool>(type: "INTEGER", nullable: true),
                    has_transcript = table.Column<bool>(type: "INTEGER", nullable: true),
                    min_confidence = table.Column<double>(type: "REAL", nullable: true),
                    sort_by = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    page_size = table.Column<int>(type: "INTEGER", nullable: false),
                    is_favorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    use_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_searches", x => x.saved_search_id);
                });

            migrationBuilder.CreateTable(
                name: "sync_outbox",
                columns: table => new
                {
                    outbox_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    entity_id = table.Column<string>(type: "TEXT", nullable: false),
                    operation = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    delivered_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_outbox", x => x.outbox_id);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    tag_id = table.Column<string>(type: "TEXT", nullable: false),
                    tag_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    color = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.tag_id);
                });

            migrationBuilder.CreateTable(
                name: "capture_artifacts",
                columns: table => new
                {
                    artifact_id = table.Column<string>(type: "TEXT", nullable: false),
                    capture_id = table.Column<string>(type: "TEXT", nullable: false),
                    artifact_type = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    storage_locator = table.Column<string>(type: "TEXT", nullable: false),
                    media_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    byte_length = table.Column<long>(type: "INTEGER", nullable: true),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    encryption_scheme = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    upload_state = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capture_artifacts", x => x.artifact_id);
                    table.ForeignKey(
                        name: "FK_capture_artifacts_captures_capture_id",
                        column: x => x.capture_id,
                        principalTable: "captures",
                        principalColumn: "capture_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "eras",
                columns: table => new
                {
                    era_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    subtitle = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    start_date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    end_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    category_id = table.Column<string>(type: "TEXT", nullable: true),
                    color_code = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    color_override = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    display_order = table.Column<int>(type: "INTEGER", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eras", x => x.era_id);
                    table.ForeignKey(
                        name: "FK_eras_era_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "era_categories",
                        principalColumn: "category_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "person_aliases",
                columns: table => new
                {
                    alias_id = table.Column<string>(type: "TEXT", nullable: false),
                    person_id = table.Column<string>(type: "TEXT", nullable: false),
                    alias = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_aliases", x => x.alias_id);
                    table.ForeignKey(
                        name: "FK_person_aliases_people_person_id",
                        column: x => x.person_id,
                        principalTable: "people",
                        principalColumn: "person_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pending_events",
                columns: table => new
                {
                    pending_id = table.Column<string>(type: "TEXT", nullable: false),
                    extracted_data = table.Column<string>(type: "TEXT", nullable: false),
                    audio_file_path = table.Column<string>(type: "TEXT", nullable: true),
                    transcript = table.Column<string>(type: "TEXT", nullable: true),
                    queue_id = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    start_date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    end_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    confidence_score = table.Column<double>(type: "REAL", nullable: false),
                    date_precision = table.Column<int>(type: "INTEGER", nullable: false),
                    earliest_possible = table.Column<DateTime>(type: "TEXT", nullable: true),
                    latest_possible = table.Column<DateTime>(type: "TEXT", nullable: true),
                    is_approved = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_events", x => x.pending_id);
                    table.ForeignKey(
                        name: "FK_pending_events_recording_queue_queue_id",
                        column: x => x.queue_id,
                        principalTable: "recording_queue",
                        principalColumn: "queue_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "era_tags",
                columns: table => new
                {
                    era_id = table.Column<string>(type: "TEXT", nullable: false),
                    tag = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_era_tags", x => new { x.era_id, x.tag });
                    table.ForeignKey(
                        name: "FK_era_tags_eras_era_id",
                        column: x => x.era_id,
                        principalTable: "eras",
                        principalColumn: "era_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    start_date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    end_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    era_id = table.Column<string>(type: "TEXT", nullable: true),
                    audio_file_path = table.Column<string>(type: "TEXT", nullable: true),
                    raw_transcript = table.Column<string>(type: "TEXT", nullable: true),
                    extraction_metadata = table.Column<string>(type: "TEXT", nullable: true),
                    location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    confidence = table.Column<double>(type: "REAL", nullable: true),
                    date_precision = table.Column<int>(type: "INTEGER", nullable: false),
                    earliest_possible = table.Column<DateTime>(type: "TEXT", nullable: true),
                    latest_possible = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_viewed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    view_count = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.event_id);
                    table.ForeignKey(
                        name: "FK_events_eras_era_id",
                        column: x => x.era_id,
                        principalTable: "eras",
                        principalColumn: "era_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "milestones",
                columns: table => new
                {
                    milestone_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    type = table.Column<int>(type: "INTEGER", nullable: false),
                    linked_era_id = table.Column<string>(type: "TEXT", nullable: true),
                    color_override = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_milestones", x => x.milestone_id);
                    table.ForeignKey(
                        name: "FK_milestones_eras_linked_era_id",
                        column: x => x.linked_era_id,
                        principalTable: "eras",
                        principalColumn: "era_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "cross_references",
                columns: table => new
                {
                    reference_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_id_1 = table.Column<string>(type: "TEXT", nullable: false),
                    event_id_2 = table.Column<string>(type: "TEXT", nullable: false),
                    relationship_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    confidence_score = table.Column<double>(type: "REAL", nullable: true),
                    analysis_details = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cross_references", x => x.reference_id);
                    table.ForeignKey(
                        name: "FK_cross_references_events_event_id_1",
                        column: x => x.event_id_1,
                        principalTable: "events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cross_references_events_event_id_2",
                        column: x => x.event_id_2,
                        principalTable: "events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "event_embeddings",
                columns: table => new
                {
                    embedding_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    embedding_vector = table.Column<string>(type: "TEXT", nullable: false),
                    embedding_provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    embedding_model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    embedding_dimension = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_embeddings", x => x.embedding_id);
                    table.ForeignKey(
                        name: "FK_event_embeddings_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_locations",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    location_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_locations", x => new { x.event_id, x.location_id });
                    table.ForeignKey(
                        name: "FK_event_locations_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_event_locations_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "location_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_media",
                columns: table => new
                {
                    media_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    file_path = table.Column<string>(type: "TEXT", nullable: false),
                    thumbnail_path = table.Column<string>(type: "TEXT", nullable: true),
                    media_type = table.Column<int>(type: "INTEGER", nullable: false),
                    caption = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    captured_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    latitude = table.Column<double>(type: "REAL", nullable: true),
                    longitude = table.Column<double>(type: "REAL", nullable: true),
                    file_size_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    content_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    sort_order = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_media", x => x.media_id);
                    table.ForeignKey(
                        name: "FK_event_media_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_people",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    person_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_people", x => new { x.event_id, x.person_id });
                    table.ForeignKey(
                        name: "FK_event_people_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_event_people_people_person_id",
                        column: x => x.person_id,
                        principalTable: "people",
                        principalColumn: "person_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_tags",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    tag_id = table.Column<string>(type: "TEXT", nullable: false),
                    confidence_score = table.Column<double>(type: "REAL", nullable: false),
                    is_manual = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_tags", x => new { x.event_id, x.tag_id });
                    table.ForeignKey(
                        name: "FK_event_tags_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_event_tags_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "tags",
                        principalColumn: "tag_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "app_settings",
                columns: new[] { "setting_key", "setting_value", "updated_at" },
                values: new object[,]
                {
                    { "ask_include_transcripts", "false", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "ask_top_k", "12", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "audio_input_device_id", "", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "auto_generate_embeddings", "true", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "backup_destination", "", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "backup_include_media", "true", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "backup_schedule", "off", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "daily_toast_enabled", "true", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "default_zoom_level", "month", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "embedding_api_key", "", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "embedding_dimension", "384", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "embedding_model", "all-MiniLM-L6-v2", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "embedding_provider", "local", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "geocoding_enabled", "false", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "geocoding_provider", "nominatim", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "home_is_default_page", "true", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "llm_base_url", "", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "llm_max_tokens", "4000", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "llm_model", "claude-sonnet-4-20250514", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "llm_provider", "anthropic", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "llm_temperature", "0.3", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "local_model_path", "", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "map_default_zoom", "1.0", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "rag_similarity_threshold", "0.75", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "revision_history_enabled", "true", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "theme", "light", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "weekly_digest_enabled", "true", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "whisper_language", "auto", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "whisper_model", "base", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.InsertData(
                table: "era_categories",
                columns: new[] { "category_id", "created_at", "default_color", "icon_glyph", "is_visible", "name", "sort_order", "updated_at" },
                values: new object[,]
                {
                    { "cat-education", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc), "#0078D4", "", true, "Education", 1, new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "cat-employment", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc), "#107C10", "", true, "Employment", 2, new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "cat-health", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc), "#00B7C3", "", true, "Health", 5, new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "cat-other", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc), "#6B6B6B", "", true, "Other", 7, new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "cat-project", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc), "#FF8C00", "", true, "Project", 6, new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "cat-relationship", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc), "#E74856", "", true, "Relationship", 3, new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "cat-residence", new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc), "#8764B8", "", true, "Residence", 4, new DateTime(2025, 1, 21, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_capture_artifacts_capture_id",
                table: "capture_artifacts",
                column: "capture_id");

            migrationBuilder.CreateIndex(
                name: "IX_capture_artifacts_sha256",
                table: "capture_artifacts",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "IX_captures_captured_at_utc",
                table: "captures",
                column: "captured_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_captures_status",
                table: "captures",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_cross_references_event_id_1",
                table: "cross_references",
                column: "event_id_1");

            migrationBuilder.CreateIndex(
                name: "IX_cross_references_event_id_2",
                table: "cross_references",
                column: "event_id_2");

            migrationBuilder.CreateIndex(
                name: "IX_cross_references_relationship_type",
                table: "cross_references",
                column: "relationship_type");

            migrationBuilder.CreateIndex(
                name: "IX_drafts_draft_type",
                table: "drafts",
                column: "draft_type");

            migrationBuilder.CreateIndex(
                name: "IX_drafts_updated_at",
                table: "drafts",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "IX_era_categories_name",
                table: "era_categories",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_era_categories_sort_order",
                table: "era_categories",
                column: "sort_order");

            migrationBuilder.CreateIndex(
                name: "IX_era_tags_era_id",
                table: "era_tags",
                column: "era_id");

            migrationBuilder.CreateIndex(
                name: "IX_era_tags_tag",
                table: "era_tags",
                column: "tag");

            migrationBuilder.CreateIndex(
                name: "IX_eras_category_id",
                table: "eras",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "IX_eras_name",
                table: "eras",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_eras_start_date_end_date",
                table: "eras",
                columns: new[] { "start_date", "end_date" });

            migrationBuilder.CreateIndex(
                name: "IX_event_embeddings_embedding_provider",
                table: "event_embeddings",
                column: "embedding_provider");

            migrationBuilder.CreateIndex(
                name: "IX_event_embeddings_event_id",
                table: "event_embeddings",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_locations_event_id",
                table: "event_locations",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "IX_event_locations_location_id",
                table: "event_locations",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_event_media_content_hash",
                table: "event_media",
                column: "content_hash");

            migrationBuilder.CreateIndex(
                name: "IX_event_media_event_id",
                table: "event_media",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "IX_event_people_event_id",
                table: "event_people",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "IX_event_people_person_id",
                table: "event_people",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "IX_event_revisions_event_id_revised_at",
                table: "event_revisions",
                columns: new[] { "event_id", "revised_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_event_tags_event_id",
                table: "event_tags",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "IX_event_tags_tag_id",
                table: "event_tags",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_category",
                table: "events",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "IX_events_end_date",
                table: "events",
                column: "end_date");

            migrationBuilder.CreateIndex(
                name: "IX_events_era_id",
                table: "events",
                column: "era_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_start_date",
                table: "events",
                column: "start_date");

            migrationBuilder.CreateIndex(
                name: "IX_locations_name",
                table: "locations",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_milestones_date",
                table: "milestones",
                column: "date");

            migrationBuilder.CreateIndex(
                name: "IX_milestones_linked_era_id",
                table: "milestones",
                column: "linked_era_id");

            migrationBuilder.CreateIndex(
                name: "IX_milestones_type",
                table: "milestones",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "IX_pending_events_created_at",
                table: "pending_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_pending_events_queue_id",
                table: "pending_events",
                column: "queue_id");

            migrationBuilder.CreateIndex(
                name: "IX_pending_events_status",
                table: "pending_events",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_people_is_favorite",
                table: "people",
                column: "is_favorite");

            migrationBuilder.CreateIndex(
                name: "IX_people_merged_into_id",
                table: "people",
                column: "merged_into_id");

            migrationBuilder.CreateIndex(
                name: "IX_people_name",
                table: "people",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_person_aliases_alias",
                table: "person_aliases",
                column: "alias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_person_aliases_person_id",
                table: "person_aliases",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "IX_recall_prompts_kind_entity_id",
                table: "recall_prompts",
                columns: new[] { "kind", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_recall_prompts_status",
                table: "recall_prompts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_recording_queue_created_at",
                table: "recording_queue",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_recording_queue_source_capture_id",
                table: "recording_queue",
                column: "source_capture_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recording_queue_status",
                table: "recording_queue",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_saved_searches_is_favorite",
                table: "saved_searches",
                column: "is_favorite");

            migrationBuilder.CreateIndex(
                name: "IX_saved_searches_last_used_at",
                table: "saved_searches",
                column: "last_used_at");

            migrationBuilder.CreateIndex(
                name: "IX_saved_searches_name",
                table: "saved_searches",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_sync_outbox_delivered_at",
                table: "sync_outbox",
                column: "delivered_at");

            migrationBuilder.CreateIndex(
                name: "IX_sync_outbox_entity_type_entity_id",
                table: "sync_outbox",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_tags_tag_name",
                table: "tags",
                column: "tag_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");

            migrationBuilder.DropTable(
                name: "capture_artifacts");

            migrationBuilder.DropTable(
                name: "cross_references");

            migrationBuilder.DropTable(
                name: "drafts");

            migrationBuilder.DropTable(
                name: "era_tags");

            migrationBuilder.DropTable(
                name: "event_embeddings");

            migrationBuilder.DropTable(
                name: "event_locations");

            migrationBuilder.DropTable(
                name: "event_media");

            migrationBuilder.DropTable(
                name: "event_people");

            migrationBuilder.DropTable(
                name: "event_revisions");

            migrationBuilder.DropTable(
                name: "event_tags");

            migrationBuilder.DropTable(
                name: "milestones");

            migrationBuilder.DropTable(
                name: "pending_events");

            migrationBuilder.DropTable(
                name: "person_aliases");

            migrationBuilder.DropTable(
                name: "recall_prompts");

            migrationBuilder.DropTable(
                name: "saved_searches");

            migrationBuilder.DropTable(
                name: "sync_outbox");

            migrationBuilder.DropTable(
                name: "captures");

            migrationBuilder.DropTable(
                name: "locations");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "recording_queue");

            migrationBuilder.DropTable(
                name: "people");

            migrationBuilder.DropTable(
                name: "eras");

            migrationBuilder.DropTable(
                name: "era_categories");
        }
    }
}
