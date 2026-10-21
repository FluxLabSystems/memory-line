using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MemoryTimeline.Data.Models;
using System.Data.Common;

namespace MemoryTimeline.Data;

/// <summary>
/// Entity Framework Core database context for Memory Timeline.
/// </summary>
public class AppDbContext : DbContext
{
    public DbSet<Event> Events { get; set; } = null!;
    public DbSet<Era> Eras { get; set; } = null!;
    public DbSet<EraCategory> EraCategories { get; set; } = null!;
    public DbSet<EraTag> EraTags { get; set; } = null!;
    public DbSet<Milestone> Milestones { get; set; } = null!;
    public DbSet<RecordingQueue> RecordingQueues { get; set; } = null!;
    public DbSet<PendingEvent> PendingEvents { get; set; } = null!;
    public DbSet<Tag> Tags { get; set; } = null!;
    public DbSet<EventTag> EventTags { get; set; } = null!;
    public DbSet<Person> People { get; set; } = null!;
    public DbSet<EventPerson> EventPeople { get; set; } = null!;
    public DbSet<PersonAlias> PersonAliases { get; set; } = null!;
    public DbSet<Location> Locations { get; set; } = null!;
    public DbSet<EventLocation> EventLocations { get; set; } = null!;
    public DbSet<CrossReference> CrossReferences { get; set; } = null!;
    public DbSet<EventEmbedding> EventEmbeddings { get; set; } = null!;
    public DbSet<EventMedia> EventMedia { get; set; } = null!;
    public DbSet<AppSetting> AppSettings { get; set; } = null!;
    public DbSet<SavedSearch> SavedSearches { get; set; } = null!;
    public DbSet<Draft> Drafts { get; set; } = null!;
    public DbSet<RecallPrompt> RecallPrompts { get; set; } = null!;
    public DbSet<EventRevision> EventRevisions { get; set; } = null!;
    public DbSet<Capture> Captures { get; set; } = null!;
    public DbSet<CaptureArtifact> CaptureArtifacts { get; set; } = null!;
    public DbSet<SyncOutbox> SyncOutboxEntries { get; set; } = null!;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // Get the ApplicationData folder path for the database
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dbPath = Path.Combine(appDataPath, "MemoryTimeline", "memory-timeline.db");

            // Ensure directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            // Enable foreign key enforcement at the connection level. Microsoft.Data.Sqlite
            // does not enforce foreign keys unless explicitly requested.
            optionsBuilder.UseSqlite($"Data Source={dbPath};Foreign Keys=True");
        }

        // Apply SQLite pragmas (WAL journal mode, busy timeout, foreign key enforcement)
        // on every connection open. Pragmas such as busy_timeout and foreign_keys are
        // per-connection and must be re-applied for pooled connections.
        optionsBuilder.AddInterceptors(SqlitePragmaInterceptor.Instance);
    }

    /// <summary>
    /// Applies SQLite pragmas each time a connection is opened.
    /// </summary>
    private sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
    {
        public static readonly SqlitePragmaInterceptor Instance = new();

        // journal_mode=WAL is a persistent setting that WRITES to the database
        // file when the file is not already in WAL mode. EF opens read-only
        // connections for existence checks (EnsureCreated/EnsureDeleted), and
        // a database restored from an import backup is in the default DELETE
        // journal mode - on those opens the WAL pragma fails with SQLite
        // error 8 ("attempt to write a readonly database"), so it is applied
        // best-effort. The per-connection pragmas below never write and must
        // always be applied.
        private const string WalPragmaSql = "PRAGMA journal_mode=WAL;";

        private const string ConnectionPragmaSql =
            "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";

        // Mode=ReadOnly connections (EF's existence probes via
        // SqliteDatabaseCreator.Exists) can never set WAL, so skip the
        // attempt outright instead of relying on the exception path. The
        // try/catch around the WAL command still covers what the connection
        // string cannot reveal: read-only files and unparseable strings.
        private static bool ShouldAttemptWal(DbConnection connection)
        {
            try
            {
                var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
                return builder.Mode != SqliteOpenMode.ReadOnly;
            }
            catch (ArgumentException)
            {
                // Unparseable connection string: attempt best-effort.
                return true;
            }
        }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (ShouldAttemptWal(connection))
            {
                try
                {
                    using var walCommand = connection.CreateCommand();
                    walCommand.CommandText = WalPragmaSql;
                    walCommand.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Read-only file: WAL cannot be set here; the next
                    // writable open will set it.
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = ConnectionPragmaSql;
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (ShouldAttemptWal(connection))
            {
                try
                {
                    using var walCommand = connection.CreateCommand();
                    walCommand.CommandText = WalPragmaSql;
                    await walCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Read-only file: WAL cannot be set here; the next
                    // writable open will set it.
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = ConnectionPragmaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure Event entity
        modelBuilder.Entity<Event>(entity =>
        {
            entity.HasKey(e => e.EventId);
            entity.HasIndex(e => e.StartDate);
            entity.HasIndex(e => e.EndDate);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.EraId);

            entity.HasOne(e => e.Era)
                .WithMany(era => era.Events)
                .HasForeignKey(e => e.EraId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Embedding)
                .WithOne(emb => emb.Event)
                .HasForeignKey<EventEmbedding>(emb => emb.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Era entity
        modelBuilder.Entity<Era>(entity =>
        {
            entity.HasKey(e => e.EraId);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => new { e.StartDate, e.EndDate });
            entity.HasIndex(e => e.CategoryId);

            entity.HasOne(e => e.Category)
                .WithMany(c => c.Eras)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure EraCategory entity
        modelBuilder.Entity<EraCategory>(entity =>
        {
            entity.HasKey(c => c.CategoryId);
            entity.HasIndex(c => c.Name).IsUnique();
            entity.HasIndex(c => c.SortOrder);
        });

        // Configure EraTag entity
        modelBuilder.Entity<EraTag>(entity =>
        {
            entity.HasKey(et => new { et.EraId, et.Tag });

            entity.HasOne(et => et.Era)
                .WithMany(e => e.EraTags)
                .HasForeignKey(et => et.EraId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(et => et.EraId);
            entity.HasIndex(et => et.Tag);
        });

        // Configure Milestone entity
        modelBuilder.Entity<Milestone>(entity =>
        {
            entity.HasKey(m => m.MilestoneId);
            entity.HasIndex(m => m.Date);
            entity.HasIndex(m => m.LinkedEraId);
            entity.HasIndex(m => m.Type);

            entity.HasOne(m => m.LinkedEra)
                .WithMany(e => e.Milestones)
                .HasForeignKey(m => m.LinkedEraId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure RecordingQueue entity
        modelBuilder.Entity<RecordingQueue>(entity =>
        {
            entity.HasKey(q => q.QueueId);
            entity.HasIndex(q => q.Status);
            entity.HasIndex(q => q.CreatedAt);
            // Idempotent ingestion: at most one queue item per device-neutral
            // capture. SQLite treats NULLs as distinct, so Windows-legacy rows
            // (no capture id) are unaffected.
            entity.HasIndex(q => q.SourceCaptureId).IsUnique();
        });

        // Configure Capture entity
        modelBuilder.Entity<Capture>(entity =>
        {
            entity.HasKey(c => c.CaptureId);
            entity.HasIndex(c => c.Status);
            entity.HasIndex(c => c.CapturedAtUtc);

            entity.HasMany(c => c.Artifacts)
                .WithOne(a => a.Capture)
                .HasForeignKey(a => a.CaptureId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure CaptureArtifact entity
        modelBuilder.Entity<CaptureArtifact>(entity =>
        {
            entity.HasKey(a => a.ArtifactId);
            entity.HasIndex(a => a.CaptureId);
            entity.HasIndex(a => a.Sha256);
        });

        // Configure SyncOutbox entity
        modelBuilder.Entity<SyncOutbox>(entity =>
        {
            entity.HasKey(o => o.OutboxId);
            entity.Property(o => o.OutboxId).ValueGeneratedOnAdd();
            entity.HasIndex(o => o.DeliveredAt);
            entity.HasIndex(o => new { o.EntityType, o.EntityId });
        });

        // Configure PendingEvent entity
        modelBuilder.Entity<PendingEvent>(entity =>
        {
            entity.HasKey(p => p.PendingId);
            entity.HasIndex(p => p.Status);
            entity.HasIndex(p => p.CreatedAt);

            entity.HasOne(p => p.RecordingQueue)
                .WithMany(q => q.PendingEvents)
                .HasForeignKey(p => p.QueueId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure Tag entity
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasKey(t => t.TagId);
            // NOCASE collation makes the unique index case-insensitive
            // ("Family" and "family" are the same tag). Pre-existing databases
            // are converted by SchemaUpgrader's case-insensitive identity
            // repair (dedupe, then rebuild the index with COLLATE NOCASE).
            entity.Property(t => t.TagName).UseCollation("NOCASE");
            entity.HasIndex(t => t.TagName).IsUnique();
        });

        // Configure EventTag junction entity
        modelBuilder.Entity<EventTag>(entity =>
        {
            entity.HasKey(et => new { et.EventId, et.TagId });

            entity.HasOne(et => et.Event)
                .WithMany(e => e.EventTags)
                .HasForeignKey(et => et.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(et => et.Tag)
                .WithMany(t => t.EventTags)
                .HasForeignKey(et => et.TagId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(et => et.EventId);
            entity.HasIndex(et => et.TagId);
        });

        // Configure Person entity
        modelBuilder.Entity<Person>(entity =>
        {
            entity.HasKey(p => p.PersonId);
            // NOCASE collation makes the unique index case-insensitive
            // ("Sarah" and "sarah" are the same person). Pre-existing databases
            // are converted by SchemaUpgrader's case-insensitive identity
            // repair (dedupe, then rebuild the index with COLLATE NOCASE).
            entity.Property(p => p.Name).UseCollation("NOCASE");
            entity.HasIndex(p => p.Name).IsUnique();
            entity.HasIndex(p => p.IsFavorite);
            entity.HasIndex(p => p.MergedIntoId);
        });

        // Configure PersonAlias entity
        modelBuilder.Entity<PersonAlias>(entity =>
        {
            entity.HasKey(a => a.AliasId);
            // An alias spelling belongs to at most one person, case-insensitively.
            entity.Property(a => a.Alias).UseCollation("NOCASE");
            entity.HasIndex(a => a.Alias).IsUnique();
            entity.HasIndex(a => a.PersonId);

            entity.HasOne(a => a.Person)
                .WithMany(p => p.Aliases)
                .HasForeignKey(a => a.PersonId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure EventPerson junction entity
        modelBuilder.Entity<EventPerson>(entity =>
        {
            entity.HasKey(ep => new { ep.EventId, ep.PersonId });

            entity.HasOne(ep => ep.Event)
                .WithMany(e => e.EventPeople)
                .HasForeignKey(ep => ep.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ep => ep.Person)
                .WithMany(p => p.EventPeople)
                .HasForeignKey(ep => ep.PersonId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(ep => ep.EventId);
            entity.HasIndex(ep => ep.PersonId);
        });

        // Configure Location entity
        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasKey(l => l.LocationId);
            // NOCASE collation makes the unique index case-insensitive
            // ("Paris" and "paris" are the same location). Pre-existing
            // databases are converted by SchemaUpgrader's case-insensitive
            // identity repair.
            entity.Property(l => l.Name).UseCollation("NOCASE");
            entity.HasIndex(l => l.Name).IsUnique();
        });

        // Configure EventLocation junction entity
        modelBuilder.Entity<EventLocation>(entity =>
        {
            entity.HasKey(el => new { el.EventId, el.LocationId });

            entity.HasOne(el => el.Event)
                .WithMany(e => e.EventLocations)
                .HasForeignKey(el => el.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(el => el.Location)
                .WithMany(l => l.EventLocations)
                .HasForeignKey(el => el.LocationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(el => el.EventId);
            entity.HasIndex(el => el.LocationId);
        });

        // Configure CrossReference entity
        modelBuilder.Entity<CrossReference>(entity =>
        {
            entity.HasKey(cr => cr.ReferenceId);
            entity.HasIndex(cr => cr.EventId1);
            entity.HasIndex(cr => cr.EventId2);
            entity.HasIndex(cr => cr.RelationshipType);

            entity.HasOne(cr => cr.Event1)
                .WithMany()
                .HasForeignKey(cr => cr.EventId1)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(cr => cr.Event2)
                .WithMany()
                .HasForeignKey(cr => cr.EventId2)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Configure EventEmbedding entity
        modelBuilder.Entity<EventEmbedding>(entity =>
        {
            entity.HasKey(e => e.EmbeddingId);
            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => e.EmbeddingProvider);
        });

        // Configure EventMedia entity
        modelBuilder.Entity<EventMedia>(entity =>
        {
            entity.HasKey(m => m.MediaId);
            entity.HasIndex(m => m.EventId);
            entity.HasIndex(m => m.ContentHash);

            entity.HasOne(m => m.Event)
                .WithMany(e => e.Media)
                .HasForeignKey(m => m.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure AppSetting entity
        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(s => s.SettingKey);
        });

        // Configure SavedSearch entity
        modelBuilder.Entity<SavedSearch>(entity =>
        {
            entity.HasKey(s => s.SavedSearchId);
            entity.HasIndex(s => s.Name);
            entity.HasIndex(s => s.IsFavorite);
            entity.HasIndex(s => s.LastUsedAt);
        });

        // Configure Draft entity
        modelBuilder.Entity<Draft>(entity =>
        {
            entity.HasKey(d => d.DraftId);
            entity.HasIndex(d => d.DraftType);
            entity.HasIndex(d => d.UpdatedAt);
        });

        // Configure RecallPrompt entity
        modelBuilder.Entity<RecallPrompt>(entity =>
        {
            entity.HasKey(p => p.PromptId);
            entity.HasIndex(p => p.Status);
            // Dedupe lookup: has this gap (of this kind, about this entity)
            // ever been asked about, in any status?
            entity.HasIndex(p => new { p.Kind, p.EntityId });
        });

        // Configure EventRevision entity (F12 revision history)
        modelBuilder.Entity<EventRevision>(entity =>
        {
            entity.HasKey(r => r.RevisionId);
            // Deliberately NO foreign key to events (and the entity has no
            // navigation property that would create one by convention):
            // revision history must outlive event deletion so a deleted
            // event's past remains inspectable. History lookups run through
            // this composite index, newest revision first.
            entity.HasIndex(r => new { r.EventId, r.RevisedAt }).IsDescending(false, true);
        });

        // Seed default settings
        SeedDefaultSettings(modelBuilder);
    }

    /// <summary>
    /// Fixed timestamp for seed data. Seed values passed to HasData must be deterministic
    /// (constant across model builds); using DateTime.UtcNow here would make the model
    /// non-deterministic and break migration diffing / model validation.
    /// </summary>
    private static readonly DateTime SeedTimestamp = new(2025, 1, 21, 0, 0, 0, DateTimeKind.Utc);

    private void SeedDefaultSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>().HasData(
            new AppSetting { SettingKey = "theme", SettingValue = "light", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "default_zoom_level", SettingValue = "month", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "llm_provider", SettingValue = "anthropic", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "llm_model", SettingValue = "claude-sonnet-4-20250514", UpdatedAt = SeedTimestamp },
            // Consumed by the LLM services (extraction output budget; sampling
            // temperature for every call) and by RagService (similar-event
            // retrieval floor). Rows that no reader ever consulted — audio_quality,
            // stt_engine/stt_config (the engine is always local Whisper),
            // rag_auto_run_enabled/rag_schedule (no scheduler exists),
            // send_transcripts_only, require_confirmation — are no longer seeded;
            // existing rows are never deleted, merely ignored.
            new AppSetting { SettingKey = "llm_max_tokens", SettingValue = "4000", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "llm_temperature", SettingValue = "0.3", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "rag_similarity_threshold", SettingValue = "0.75", UpdatedAt = SeedTimestamp },
            // F11: embedding_model corrected from the never-implemented
            // 'onnx-text-embedding' placeholder to the real local model.
            // Existing databases keep whatever value their row already has
            // (HasData / INSERT OR IGNORE never overwrite); routing keys off
            // embedding_provider and treats unknown stored model strings
            // gracefully, so the stale value is harmless.
            new AppSetting { SettingKey = "embedding_provider", SettingValue = "local", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "embedding_model", SettingValue = "all-MiniLM-L6-v2", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "embedding_dimension", SettingValue = "384", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "embedding_api_key", SettingValue = "", UpdatedAt = SeedTimestamp },
            // F11: OpenAI-compatible LLM endpoint (Ollama / LM Studio / vLLM)
            // and the optional local ONNX model directory override.
            new AppSetting { SettingKey = "llm_base_url", SettingValue = "", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "local_model_path", SettingValue = "", UpdatedAt = SeedTimestamp },
            // Read by EventService/EventExtractionService before the background
            // embedding of a created or approved event.
            new AppSetting { SettingKey = "auto_generate_embeddings", SettingValue = "true", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "ask_top_k", SettingValue = "12", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "ask_include_transcripts", SettingValue = "false", UpdatedAt = SeedTimestamp },
            // Home / resurfacing (2026-08). last_toast_date is deliberately not
            // seeded: an absent row means "the daily toast has never been shown".
            new AppSetting { SettingKey = "home_is_default_page", SettingValue = "true", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "daily_toast_enabled", SettingValue = "true", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "weekly_digest_enabled", SettingValue = "true", UpdatedAt = SeedTimestamp },
            // Backup & revision history (2026-08 F12). last_backup_at is
            // deliberately not seeded: an absent row means "never backed up".
            new AppSetting { SettingKey = "backup_destination", SettingValue = "", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "backup_include_media", SettingValue = "true", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "backup_schedule", SettingValue = "off", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "revision_history_enabled", SettingValue = "true", UpdatedAt = SeedTimestamp },
            // Map / geocoding (2026-08 F10). Geocoding is OFF by default:
            // no place name leaves the machine until the user opts in.
            new AppSetting { SettingKey = "geocoding_enabled", SettingValue = "false", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "geocoding_provider", SettingValue = "nominatim", UpdatedAt = SeedTimestamp },
            new AppSetting { SettingKey = "map_default_zoom", SettingValue = "1.0", UpdatedAt = SeedTimestamp }
        );

        // Seed default era categories
        SeedDefaultEraCategories(modelBuilder);
    }

    private void SeedDefaultEraCategories(ModelBuilder modelBuilder)
    {
        var now = SeedTimestamp;
        modelBuilder.Entity<EraCategory>().HasData(
            new EraCategory { CategoryId = "cat-education", Name = "Education", DefaultColor = "#0078D4", IconGlyph = "\uE7BE", SortOrder = 1, CreatedAt = now, UpdatedAt = now },
            new EraCategory { CategoryId = "cat-employment", Name = "Employment", DefaultColor = "#107C10", IconGlyph = "\uE821", SortOrder = 2, CreatedAt = now, UpdatedAt = now },
            new EraCategory { CategoryId = "cat-relationship", Name = "Relationship", DefaultColor = "#E74856", IconGlyph = "\uEB51", SortOrder = 3, CreatedAt = now, UpdatedAt = now },
            new EraCategory { CategoryId = "cat-residence", Name = "Residence", DefaultColor = "#8764B8", IconGlyph = "\uE80F", SortOrder = 4, CreatedAt = now, UpdatedAt = now },
            new EraCategory { CategoryId = "cat-health", Name = "Health", DefaultColor = "#00B7C3", IconGlyph = "\uE95E", SortOrder = 5, CreatedAt = now, UpdatedAt = now },
            new EraCategory { CategoryId = "cat-project", Name = "Project", DefaultColor = "#FF8C00", IconGlyph = "\uE8F1", SortOrder = 6, CreatedAt = now, UpdatedAt = now },
            new EraCategory { CategoryId = "cat-other", Name = "Other", DefaultColor = "#6B6B6B", IconGlyph = "\uE7C3", SortOrder = 7, CreatedAt = now, UpdatedAt = now }
        );
    }

    /// <summary>
    /// Override SaveChanges to automatically update timestamps.
    /// </summary>
    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    /// <summary>
    /// Override SaveChangesAsync to automatically update timestamps.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            if (entry.Entity is Event eventEntity)
            {
                eventEntity.UpdatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is Era era)
            {
                era.UpdatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is EraCategory category)
            {
                category.UpdatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is Milestone milestone)
            {
                milestone.UpdatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is AppSetting setting)
            {
                setting.UpdatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is Person person)
            {
                person.UpdatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is Draft draft)
            {
                draft.UpdatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is RecallPrompt recallPrompt)
            {
                recallPrompt.UpdatedAt = DateTime.UtcNow;
            }
        }
    }
}
