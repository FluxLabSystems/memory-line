# Memory Timeline - Windows Native Application

A native Windows 11 implementation of Memory Timeline, featuring local Whisper transcription, AI-powered event extraction with honest date precision, media attachments, ask-your-timeline retrieval, narrative generation, an offline map, backup/revision history, RAG-based cross-referencing (local embeddings by default), and deep Windows integration.

**This is the primary, actively developed product.** A native macOS app is being brought up alongside it under [`macos-native/`](../macos-native).

**Platform:** Windows 11 (22H2+)
**Framework:** .NET 10 (LTS) + WinUI 3 / Windows App SDK 2.3.1
**Status:** Active development — Phases 0-6 complete; core pipeline rebuilt and hardened via a multi-agent feature audit (2026-07), followed by a twelve-feature spec implementation (F1–F12, 2026-08). Builds are green in CI; end-to-end runtime validation is ongoing. Phase 7 (testing, MSIX, Microsoft Store) is in progress.

> This is not "production ready" or runtime-verified yet. See [Recent hardening](#recent-hardening) and [Development Status](#development-status).

---

## Overview

This is the native Windows implementation of Memory Timeline: a **local-first** desktop app for recording, organizing, and rediscovering personal history. You speak a memory, the app transcribes it **locally with Whisper**, a **Claude** model extracts a structured event, you review and approve it, and it lands on an interactive timeline. Optional embeddings surface connections between related memories.

It is designed to provide strong performance and to leverage Windows-specific features such as native touch/pen support (Windows Ink), toast notifications, JumpList, and Windows Timeline integration.

### Performance targets

Building on the OS's own UI stack rather than a bundled web runtime is what buys low
memory use, fast cold start, smooth timeline rendering, a small package, and first-class
Windows integration.

The table below lists **targets**, not measured/verified results — treat them as design goals while runtime validation is ongoing:

| Aspect | Target |
|--------|--------|
| Memory (idle) | ~30 MB |
| Memory (5000 events) | ~100 MB |
| Cold start | < 2 seconds |
| Timeline FPS | 60 FPS |
| Package size | ~30 MB |
| Touch/Pen | Full Windows Ink |
| Windows Integration | Full (JumpList, Timeline, Notifications) |

---

## The memory pipeline

Every stage persists its state, so a failure is recoverable and visible in the UI rather than silently swallowed:

```
🎙  Record            →  ⏱  Queue           →  📝  Transcribe        →  🤖  Extract
 Windows MediaCapture     recording_queue       Whisper (local)          Claude → pending_events
 (16 kHz mono WAV)        (pending → done)      offline, on-device       structured, reviewable
                                                                                 │
                                                                                 ▼
🗓  Timeline          ←  ✅  Approve          ←  👀  Review
 event appears,          atomic write:            edit / approve / reject
 auto-refresh            event + tags + people    per pending event
                         + locations, one txn
```

- **Record** — `Windows.Media.Capture` writes a 16 kHz mono WAV under `%LOCALAPPDATA%\MemoryTimeline\AudioRecordings`.
- **Text capture** — pasted/typed text (Ctrl+Shift+V on the Queue page) enters the same `recording_queue` as a **text source**; the text is stored as the item's transcript and skips transcription entirely.
- **Transcribe** — local Whisper transcribes the recorded file; the transcript is persisted on the queue row and **reused across retries** instead of re-transcribing.
- **Extract** — the configured LLM (Claude, or an OpenAI-compatible endpoint) produces a structured event (title, dates **with precision**, description, category, tags/people/locations, alias-aware people matching) into `pending_events`.
- **Approve** — approving writes the event and all its tags/people/locations (plus date precision/uncertainty) in **one transaction**; the timeline auto-refreshes via a `WeakReferenceMessenger` `EventCreatedMessage`.

---

## Features

### Core Functionality
- **Timeline Visualization** - Interactive timeline with Year/Month/Week/Day zoom, pan, and keyboard navigation; duration **spans**, optional **swimlanes** (collapsible lanes), and **uncertainty bands** for vague dates; scroll reloads are coalesced; new/approved events refresh onto the timeline automatically.
- **Honest Date Precision** - Events carry a `DatePrecision` (Exact/Day/Month/Season/Year/Decade/Unknown) plus an earliest/latest uncertainty window; every display formats dates at their true precision ("Summer 2003", never an invented day).
- **Audio Recording** - Record audio memories with pause/resume/cancel using Windows `MediaCapture`.
- **Text & Paste Capture** - Ctrl+Shift+V (or the Queue-page button) captures typed/pasted text through the same queue; transcripts are persisted and reused across retries.
- **Local Speech-to-Text** - Transcribe recordings **on-device** with **Whisper** ([Whisper.net](https://github.com/sandrohanea/whisper.net)); no audio leaves the machine.
- **LLM Event Extraction** - Extract structured events (with date precision and alias-aware people) from transcripts using Anthropic **Claude** or any **OpenAI-compatible endpoint** (Ollama, LM Studio), held in a review queue with approve / edit / reject.
- **Media Attachments** - Photos/video/audio/documents on events: managed copy tree under `%LOCALAPPDATA%\MemoryTimeline\Media`, EXIF read via **MetadataExtractor**, thumbnail generation, content-hash dedupe, drag-drop, lightbox viewer.
- **Home / On This Day** - New default landing page: precision-aware anniversary cards, recall-prompt card, view tracking for neglected-memory bias, optional daily toast, working `event:` deep links, first-run empty state.
- **Ask Your Timeline** - Conversational page with hybrid keyword+semantic retrieval (reciprocal-rank fusion), grounded answers **cited to real events**, honest refusal when nothing relevant exists, and a keyword-only degraded mode when embeddings are unavailable.
- **Narrative Generation** - Grounded prose "stories" from a timeline range, era, or person with citation integrity enforced in code; Markdown/HTML export via a story dialog on the Eras, Timeline, and People pages.
- **Guided Recall Prompts** - Questions generated from archive gaps (density gaps, dangling people, era edges, thin events, anniversaries), surfaced on Home and Queue; a dismissed/answered prompt is never asked again.
- **People Hub** - Contact book plus **aliases**, case-insensitive (NOCASE) identity with a defensive duplicate-merging migration, merge **tombstones**, and per-person profiles.
- **Offline Map** - Fully offline canvas map (deliberately no `MapControl`/Azure Maps): coordinates via photo EXIF GPS backfill or manual pin drop; **opt-in** Nominatim geocoding, off by default.
- **RAG Cross-References** - Discover connections between events; the **default embedding provider is local ONNX** (`all-MiniLM-L6-v2`), so this works with no API key, with OpenAI embeddings as an alternative.
- **Backup / Restore / Revisions** - `.mtbak` backup archives (online SQLite backup + optional media), restore with preview + confirmation, scheduled daily/weekly backups, append-only event revision history with a history dialog, and a real Clear Cache.
- **Search & Analytics** - Faceted search across events, tags, people, locations, and eras; category distribution, timeline density, tag cloud, people network.
- **Export/Import** - JSON, CSV, and Markdown export with JSON import support.

### Windows 11 Integration
- **Toast Notifications** - Processing-complete alerts.
- **JumpList** - Quick access to recent events and common actions.
- **Windows Timeline** - Events published to Windows Timeline with Adaptive Cards.
- **Theme Support** - Light/Dark/System theme switching.
- **Touch & Pen** - Gesture support with Windows Ink ready.

---

## Quick Start

### Prerequisites

1. **Windows 11** (Version 22H2 or later)
   - Check version: `winver` in the Run dialog.

2. **Visual Studio 2026 (18.0+)** — required; VS 2022 can no longer build this repo.
   - Download: https://visualstudio.microsoft.com/
   - Required workloads:
     - `.NET Desktop Development`
     - `Windows App SDK` (Windows App SDK C# templates)
   - The projects target `net10.0`, and Microsoft supports targeting .NET 10 only in Visual
     Studio 18.0+. The pinned SDK band (below) also requires MSBuild 18.

3. **The .NET 10 SDK, 10.0.3xx or newer** — pinned by `windows-native/src/global.json`
   (`"version": "10.0.300"`, `"rollForward": "latestFeature"`).
   - SDK bands `10.0.2xx` and later require MSBuild 18, i.e. VS 2026, so the repo no longer
     builds in VS 2022. Visual Studio 2026 ships a satisfying SDK.
   - .NET 10 is required, not optional: .NET 8 and .NET 9 both reach end of support on
     2026-11-10, and the Windows SDK projection ref pack the build now uses is compiled against
     a newer runtime than net8.0 can consume (it fails with `CS1705` assembly-version conflicts).

### Automated Setup

```powershell
# Navigate to scripts directory
cd windows-native\scripts

# Run setup (requires Administrator)
.\Setup-Dependencies.ps1 -Mode Development

# Verify installation
.\Verify-Installation.ps1 -Detailed
```

See [`scripts/README.md`](./scripts/README.md) for detailed script documentation.

### Build and Run

The solution defines **x64 / x86 / ARM64** platforms only (**no AnyCPU**), and the app is built
**unpackaged** (`WindowsPackageType=None`). Build **Release | x64**.

> **Important — build with Visual Studio or `msbuild`, not `dotnet build`.**
> WinUI 3 PRI resource generation (`MrtCore.PriGen` → `ExpandPriContent`) uses a .NET Framework
> MSBuild task that does **not** load under the `dotnet` CLI's build engine. Running
> `dotnet build` / `dotnet run` on the WinUI app fails with **error MSB4062**. Build from
> **Visual Studio (F5)** or the Visual Studio **`msbuild.exe`** for a full app build. (`dotnet
> build -p:Platform=x64` still works fine for the `MemoryTimeline.Core` / `MemoryTimeline.Data`
> class libraries in isolation — it's specifically the WinUI app that needs VS/msbuild.)

```powershell
# Clone and navigate
git clone <repository-url>
cd memory-line/windows-native/src

# Full app build (Visual Studio MSBuild), Release | x64:
msbuild MemoryTimeline.sln /t:Restore,Build /p:Configuration=Release /p:Platform=x64

# Or open MemoryTimeline.sln in Visual Studio 2026 and press F5 (debug) / Ctrl+F5 (run).
```

### First Run

- The app creates its SQLite database at `%LOCALAPPDATA%\MemoryTimeline\memory-timeline.db` (WAL mode).
- The first transcription downloads the Whisper `ggml-base.bin` model (~140 MB) to
  `%LOCALAPPDATA%\MemoryTimeline\Models\` — one time, then fully offline.
- Add your API key(s) in **Settings** before processing the queue (see [API Keys & Configuration](#api-keys--configuration)).

### Data & model locations

```
%LOCALAPPDATA%\MemoryTimeline\
├── memory-timeline.db          SQLite database (WAL)
├── AudioRecordings\            recorded 16 kHz mono WAV files
├── Media\                      managed copies of attached media ({yyyy}\{MM}\{guid}.ext)
├── Models\ggml-base.bin        Whisper model (downloaded on first use)
├── Models\all-MiniLM-L6-v2\    local embedding model + vocab (~90 MB, downloaded on
│                               first local embedding; overridable via local_model_path)
└── error.log                   startup / diagnostic log
```

Backups (`.mtbak`) are written to the destination folder chosen in Settings.

---

## Project Structure

```
windows-native/
├── src/
│   ├── MemoryTimeline/              # Main WinUI 3 application
│   │   ├── Views/                   # XAML pages (nav order):
│   │   │   ├── HomePage.xaml        #   default landing (On This Day, recall card)
│   │   │   ├── TimelinePage.xaml
│   │   │   ├── AskPage.xaml         #   ask-your-timeline Q&A
│   │   │   ├── QueuePage.xaml
│   │   │   ├── ReviewPage.xaml
│   │   │   ├── ContactsPage.xaml    #   People hub
│   │   │   ├── ConnectionsPage.xaml
│   │   │   ├── ErasPage.xaml
│   │   │   ├── MapPage.xaml         #   offline canvas map
│   │   │   ├── SearchPage.xaml
│   │   │   ├── AnalyticsPage.xaml
│   │   │   └── SettingsPage.xaml
│   │   ├── ViewModels/              # MVVM view models
│   │   ├── Controls/                # Custom controls & dialogs (TimelineControl,
│   │   │                            #   EventBubble, EventSpanBar, NarrativeDialog,
│   │   │                            #   EventHistoryDialog, ...)
│   │   ├── Services/                # Platform services (audio, Whisper STT, thumbnails,
│   │   │                            #   notifications, JumpList, navigation, theme)
│   │   ├── Assets/                  # Images, icons, resources
│   │   ├── App.xaml(.cs)            # Application entry point & DI composition root
│   │   └── MainWindow.xaml          # Main window
│   │
│   ├── MemoryTimeline.Core/         # Business logic layer
│   │   ├── Services/                # Service interfaces & implementations
│   │   │   ├── IEventService.cs / ISettingsService.cs / ITimelineService.cs
│   │   │   ├── ISpeechToTextService.cs
│   │   │   ├── ILlmService.cs (RoutingLlmService → AnthropicLlmService |
│   │   │   │                    OpenAiCompatibleLlmService) / ILlmUsageTracker.cs
│   │   │   ├── IEventExtractionService.cs
│   │   │   ├── IEmbeddingService.cs (RoutingEmbeddingService → OnnxEmbeddingService |
│   │   │   │                    OpenAIEmbeddingService) / IRagService.cs
│   │   │   ├── IMemoryQueryService.cs   # Ask retrieval + grounded answers
│   │   │   ├── INarrativeService.cs / IResurfacingService.cs / IRecallPromptService.cs
│   │   │   ├── IMediaService.cs / IThumbnailGenerator.cs
│   │   │   ├── IBackupService.cs / IRevisionService.cs
│   │   │   ├── IGeocodingService.cs (NominatimGeocodingService, opt-in)
│   │   │   ├── IExportService.cs / IImportService.cs
│   │   │   ├── INotificationService.cs
│   │   │   ├── IQueueService.cs
│   │   │   └── SettingKeys.cs       # unified snake_case setting keys
│   │   └── Models/                  # Business models / DTOs
│   │
│   ├── MemoryTimeline.Data/         # Data access layer
│   │   ├── Models/                  # EF Core entities
│   │   ├── Repositories/            # Repository pattern (stateless over the factory)
│   │   ├── AppDbContext.cs          # EF Core database context
│   │   └── SchemaUpgrader.cs        # EnsureCreated + idempotent schema-drift repair
│   │
│   ├── MemoryTimeline.Tests/        # Unit & integration tests
│   │   ├── UnitTests/
│   │   ├── Integration/
│   │   └── Performance/
│   │
│   ├── global.json                  # pins the .NET SDK (8.0.100, rollForward major)
│   └── MemoryTimeline.sln           # Visual Studio solution file
│
├── scripts/                         # PowerShell automation scripts
├── packaging/                       # MSIX packaging configuration
├── README.md                        # This file
├── FEATURE-AUDIT.md                 # Feature-by-feature audit & root causes
├── HARDENING-FOLLOWUPS.md           # Deferred hardening items
├── DEVELOPMENT-STATUS.md            # Current development status
├── DEVELOPMENT-HISTORY.md           # Consolidated phase reports
├── TESTING.md                       # Testing guide
└── DEPLOYMENT.md                    # Deployment guide
```

> **Note:** there is intentionally **no** `MemoryTimeline.Data/Migrations/` folder — the stale EF
> Migrations baseline was deleted, and schema is currently created/repaired by `SchemaUpgrader`.
> Regenerating a proper EF migration baseline is a tracked follow-up.

---

## Architecture

### Clean Architecture Layers

```
┌─────────────────────────────────────────┐
│  Presentation Layer (WinUI 3 + XAML)     │
│  ┌──────────────────────────────────┐    │
│  │ Views (XAML)                     │    │
│  │ ViewModels (CommunityToolkit.Mvvm)│   │
│  │ Converters, platform services    │    │
│  └──────────────────────────────────┘    │
│              ↕  WeakReferenceMessenger    │
│  ┌──────────────────────────────────┐    │
│  │ Application / Business Layer      │   │
│  │ - Services (LLM, STT, RAG, queue)│    │
│  │ - DTOs & timeline math           │    │
│  └──────────────────────────────────┘    │
│              ↕                            │
│  ┌──────────────────────────────────┐    │
│  │ Data Access Layer                │    │
│  │ - EF Core 8 + SQLite (WAL)       │    │
│  │ - IDbContextFactory per-operation│    │
│  │ - Repositories + SchemaUpgrader  │    │
│  └──────────────────────────────────┘    │
└─────────────────────────────────────────┘
```

### Key architectural decisions

- **Per-operation `DbContext` via `IDbContextFactory<AppDbContext>`.** A desktop app has no
  request scope, so every repository and service opens a short-lived context per operation
  (`await using var ctx = await factory.CreateDbContextAsync()`). This replaced a single
  app-lifetime context shared across all features — the root cause of intermittent "second
  operation on this context" failures. Because the factory is stateless and thread-safe,
  repositories and most Core services are registered as **Singletons**.
- **`SchemaUpgrader` instead of raw `EnsureCreated`.** On startup, `App.OnLaunched` calls
  `MemoryTimeline.Data.SchemaUpgrader.EnsureSchemaAsync`, which runs `EnsureCreated` and then
  idempotently repairs schema drift (missing tables/columns) on databases created by older builds
  — a stopgap until a full EF migration baseline is regenerated.
- **MVVM with a message bus.** Cross-feature updates (e.g. "event created" → refresh the timeline)
  flow through `WeakReferenceMessenger` rather than tight coupling between view models.
- **Errors are surfaced, not swallowed.** Failures propagate to visible `InfoBar`/status
  affordances instead of disappearing into logs.

### MVVM Pattern

Using `CommunityToolkit.Mvvm` for `ObservableObject`, `[RelayCommand]`, `x:Bind` compiled
bindings, and `WeakReferenceMessenger`.

- **Model**: entities in `MemoryTimeline.Data/Models/`
- **View**: XAML pages in `MemoryTimeline/Views/`
- **ViewModel**: view logic in `MemoryTimeline/ViewModels/`

### Dependency Injection

Services are registered in `App.xaml.cs` (the composition root). The build uses an
`IDbContextFactory` (not a shared scoped `AddDbContext`), and repositories/services are
Singletons over that factory:

```csharp
// Per-operation contexts via a factory (no app-wide shared DbContext).
services.AddDbContextFactory<AppDbContext>();

// Repositories are stateless over the factory (Singleton-safe).
services.AddSingleton<IEventRepository, EventRepository>();
services.AddSingleton<IAppSettingRepository, AppSettingRepository>();
// ... other repositories

// Core services (stateless over the factory/repositories).
services.AddSingleton<ISettingsService, SettingsService>();
services.AddSingleton<IEventService, EventService>();
services.AddSingleton<ITimelineService, TimelineService>();

// Audio & queue. QueueService is an explicit Singleton.
services.AddSingleton<IAudioRecordingService, AudioRecordingService>();
services.AddSingleton<IQueueService, QueueService>();

// Local Whisper is the registered STT engine. The Windows SpeechRecognizer
// stub cannot transcribe files (mic-only) and now fails fast instead.
services.AddSingleton<ISpeechToTextService, WhisperSpeechToTextService>();

// LLM: a routing facade re-reads llm_provider on every call and delegates
// to Claude or an OpenAI-compatible endpoint (Ollama / LM Studio).
services.AddSingleton<AnthropicLlmService>();
services.AddHttpClient<OpenAiCompatibleLlmService>();
services.AddSingleton<ILlmUsageTracker, LlmUsageTracker>();
services.AddSingleton<ILlmService, RoutingLlmService>();
services.AddSingleton<IEventExtractionService, EventExtractionService>();

// Embeddings: routed between local ONNX (default, keyless) and OpenAI.
services.AddHttpClient<OpenAIEmbeddingService>();
services.AddSingleton<OnnxEmbeddingService>();
services.AddSingleton<IEmbeddingService, RoutingEmbeddingService>();
services.AddSingleton<IRagService, RagService>();

// Ask, resurfacing, narrative, recall, media, backup/revisions, geocoding.
services.AddSingleton<IMemoryQueryService, MemoryQueryService>();
services.AddSingleton<IResurfacingService, ResurfacingService>();
services.AddSingleton<INarrativeService, NarrativeService>();
services.AddSingleton<IRecallPromptService, RecallPromptService>();
services.AddSingleton<IThumbnailGenerator, WindowsThumbnailGenerator>();
services.AddSingleton<IMediaService, MediaService>();
services.AddSingleton<IBackupService, BackupService>();
services.AddSingleton<IRevisionService, RevisionService>();
services.AddHttpClient<IGeocodingService, NominatimGeocodingService>();

// The Core INotificationService is the single registered interface
// (the duplicate app-project interface was deleted).
services.AddSingleton<INotificationService, Services.NotificationService>();
```

> Cleanup from the audit: the old `AnthropicClaudeService` and the `IAudioService` stub were
> deleted (the real services are `AnthropicLlmService` and `IAudioRecordingService` /
> `IAudioPlaybackService`), and the duplicate app-project `INotificationService` interface was
> removed in favor of the Core one.

---

## Database

### SQLite with Entity Framework Core 8

**Database location:**
```
%LOCALAPPDATA%\MemoryTimeline\memory-timeline.db   (WAL mode)
```

Access goes through `IDbContextFactory<AppDbContext>` — a short-lived context per operation.
Connection configuration lives in `AppDbContext.OnConfiguring`.

### Schema Overview

**Core tables:** `events` (with `date_precision`, `earliest_possible`/`latest_possible`, `last_viewed_at`/`view_count`), `eras`, `era_categories`, `era_tags`, `milestones`, `tags`, `people` (contact fields + `merged_into_id` merge tombstone), `person_aliases`, `locations` (optional `latitude`/`longitude`, `place_type`, `canonical_name`, `geocoded_at`)
**Junction tables:** `event_tags`, `event_people`, `event_locations`
**Processing tables:** `recording_queue` (audio *and text* sources — `source_type`, `source_label`, persisted `transcript`), `pending_events` (extracted events awaiting review, mirroring the precision columns)
**Attachments & history:** `event_media` (managed media copies, EXIF, thumbnails, content hash), `event_revisions` (append-only edit history), `recall_prompts` (guided-recall questions)
**RAG tables:** `event_embeddings` (vector embeddings + per-row `embedding_dimension`), `cross_references` (event relationships)
**UX tables:** `drafts`, `saved_searches`
**Settings:** `app_settings` (key/value; keys defined in `SettingKeys`; 33 seeded keys kept in three-way parity between the `AppDbContext` seed, the `SchemaUpgrader` backfill, and `SettingKeys`)

Name uniqueness on `people`/`tags`/`locations` is **case-insensitive** (`COLLATE NOCASE` unique indexes); `SchemaUpgrader` defensively merges case-variant duplicates on older databases (backfilling contact columns) before rebuilding each index.

### Schema creation & drift repair

The schema is created and repaired at startup by `SchemaUpgrader.EnsureSchemaAsync`
(`EnsureCreated` + idempotent DDL repairs). There is **no EF Migrations folder** — regenerating a
proper EF migration history is a tracked follow-up (see [Roadmap](#roadmap--known-follow-ups)).

---

## API Keys & Configuration

### API Keys & AI providers

| Provider | Purpose | Required? |
|----------|---------|-----------|
| **Anthropic** ([console](https://console.anthropic.com/)) | Claude — extraction, Ask, narratives, recall wording | Needed for the default (Claude) LLM provider; not needed when an **OpenAI-compatible endpoint** (Ollama / LM Studio, via `llm_base_url`) is selected instead |
| **OpenAI** ([keys](https://platform.openai.com/api-keys)) | Embeddings — Connections / Ask similarity | Optional — the **default embedding provider is local ONNX** (`all-MiniLM-L6-v2`, no key needed) |

LLM and embedding providers are routed **per call** (`RoutingLlmService` / `RoutingEmbeddingService`
re-read the provider settings each time), so switching applies live. Local and OpenAI embeddings
have different dimensions (384 vs. 1536); a dimension guard refuses to mix them and Settings
offers a **re-embed all events** flow after a provider switch.

### Settings storage

Settings and API keys are configured from the **Settings** page and persisted to the local
**`app_settings`** database table. Keys are unified behind a **`SettingKeys`** constants class
(snake_case keys) so writers and readers never drift; the LLM client **re-reads configuration
live**, so provider/model/key changes apply **without a restart**.

> **Security note:** API keys are stored in the `app_settings` table **today**. Encrypting them at
> rest with **Windows DPAPI** (`ProtectedData`) is a tracked hardening follow-up. (They are **not**
> stored in Windows Credential Manager.)

The embedding service reports availability via `IsAvailableAsync`; with the local ONNX default
Connections/Ask work with no key at all (after a one-time ~90 MB model download), and if the
local model cannot load (or the OpenAI provider has no key) the Ask page degrades to
keyword-only retrieval and Connections shows a clear call-to-action instead of failing.
Geocoding (`geocoding_enabled`) is **off by default**; when off, no place name is ever sent to
Nominatim and coordinates come only from photo EXIF or manual pin drops.

---

## Recent hardening

**2026-08 — feature-spec implementation (F1–F12).** Twelve features landed on top of the
hardened base, in dependency-ordered waves with verification fix passes between them: date
precision/uncertainty, media attachments, text/paste capture, ask-your-timeline, narrative
generation, guided recall prompts, On This Day + Home page, timeline spans/swimlanes/uncertainty
rendering with reload coalescing, the people hub (aliases/merge/NOCASE identity), the offline
map, pluggable AI providers (local ONNX embeddings + OpenAI-compatible LLM endpoints), and
backup/restore/revision history. New dependencies actually in use: **MetadataExtractor** (EXIF)
and **Microsoft.ML.OnnxRuntime** (local embeddings; CPU execution provider — the DirectML EP is
deliberately deferred). All waves are CI-green; runtime validation on a real machine is still
pending. Per-feature status: [`DEVELOPMENT-STATUS.md`](./DEVELOPMENT-STATUS.md).

**2026-07 — audit and fix pass.** Before that, the core pipeline was rebuilt and hardened
through a structured, **multi-agent feature audit and fix pass**. Highlights:

- **Made the voice pipeline real** — replaced a "speech recognition" path that transcribed the
  **live microphone** (not the recorded file) with local file-based **Whisper**
  (`WhisperSpeechToTextService`); the old `WindowsSpeechRecognitionService` now fails fast without
  opening the mic. Fixed the unpackaged storage-path crash, persisted transcripts, and made
  **approve** atomic (event + tags/people/locations in one transaction).
- **Fixed DbContext concurrency** — moved from a single app-wide `AddDbContext` to per-operation
  contexts via `IDbContextFactory`, resolving intermittent "second operation on this context"
  failures behind add-event and search.
- **Unified settings** — a `SettingKeys` constants class ended the writer/reader key mismatch that
  made settings appear to "revert"; the LLM client re-reads config live.
- **DI cleanup** — deleted `AnthropicClaudeService` and the `IAudioService` stub, removed the
  duplicate app-project `INotificationService`, and decoupled `RagService` from `ILlmService`.
- **Schema resilience** — startup uses `SchemaUpgrader` (EnsureCreated + drift repair) after the
  stale EF Migrations baseline was deleted.

Full details: [`FEATURE-AUDIT.md`](./FEATURE-AUDIT.md) (findings + root causes) and
[`HARDENING-FOLLOWUPS.md`](./HARDENING-FOLLOWUPS.md) (deferred items).

---

## Testing

The test project references the WinUI app, so tests are built and run **Release | x64**.

```powershell
cd windows-native\src

# Build the solution first via Visual Studio MSBuild (see Build and Run), then run tests
# against the built assembly:
dotnet vstest MemoryTimeline.Tests\bin\x64\Release\<tfm>\MemoryTimeline.Tests.dll

# Filter by category:
dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~UnitTests"
```

Tests cover timeline math, services, repository/integration behavior, and performance. See
[`TESTING.md`](./TESTING.md) for comprehensive testing documentation.

### Continuous Integration

`.github/workflows/windows-native-build.yml` builds the full solution **Release | x64** on
`windows-latest`, driving the build with **Visual Studio MSBuild** (`microsoft/setup-msbuild`)
because the WinUI PRI task cannot run under `dotnet build`. Tests run best-effort via
`dotnet vstest`. Builds are currently **green** in CI.

---

## Deployment

### MSIX Package

**Visual Studio:**
1. Right-click the `MemoryTimeline` project.
2. Select `Publish > Create App Packages`.
3. Follow the wizard to create the MSIX.

**Command Line (Visual Studio MSBuild):**
```powershell
msbuild MemoryTimeline.sln /t:Publish /p:Configuration=Release /p:Platform=x64
```

### Microsoft Store

See [`DEPLOYMENT.md`](./DEPLOYMENT.md) for complete deployment instructions including MSIX
packaging, code signing, Microsoft Store submission, and side-loading. Packaging and Store
submission are part of Phase 7 (in progress).

---

## Performance Targets

These are **design targets**, not verified/measured results — runtime validation is ongoing:

| Metric | Target |
|--------|--------|
| Timeline FPS (5000 events) | 60 FPS |
| Memory usage (5000 events) | < 100 MB |
| Cold start time | < 2 seconds |
| Database query time | < 50 ms |

---

## Development Status

**Current focus:** Phase 7 (Testing, MSIX packaging & Microsoft Store) — in progress. The core
pipeline was recently rebuilt and hardened; builds are green in CI and end-to-end runtime
validation is ongoing.

| Phase | Status |
|-------|--------|
| Phase 0: Preparation | ✅ Complete |
| Phase 1: Core Infrastructure | ✅ Complete |
| Phase 2: Timeline Visualization | ✅ Complete |
| Phase 3: Audio & Processing | ✅ Complete |
| Phase 4: LLM Integration | ✅ Complete |
| Phase 5: RAG & Embeddings | ✅ Complete |
| Phase 6: Polish & Integration | ✅ Complete |
| Phase 7: Testing & Deployment | 🔄 In progress |

See [`DEVELOPMENT-STATUS.md`](./DEVELOPMENT-STATUS.md) for detailed status and
[`DEVELOPMENT-HISTORY.md`](./DEVELOPMENT-HISTORY.md) for consolidated phase reports.

---

## Roadmap / known follow-ups

- **End-to-end runtime validation** of the pipeline and the F1–F12 feature wave on Windows.
- **Encrypt API keys at rest** (Windows DPAPI / `ProtectedData`).
- **Regenerate EF Core migrations** to replace the `SchemaUpgrader` stopgap with a proper
  migration history.
- **Deferred F1–F12 items** — photo-import wizard, map tile basemaps, timeline location chip,
  LLM token streaming, PDF narrative export, and more; see the deferred list in
  [`DEVELOPMENT-STATUS.md`](./DEVELOPMENT-STATUS.md).
- **Whisper model options** (larger models for accuracy; language selection UI).
- **MSIX packaging and Microsoft Store submission** (Phase 7).

---

## Troubleshooting

### Build Errors

**`error MSB4062` / PRI generation fails under `dotnet build`**
- Expected: the WinUI 3 PRI task does not load under the `dotnet` CLI. Build the app with
  **Visual Studio (F5)** or the Visual Studio **`msbuild.exe`** instead.

**"Windows App SDK not found"**
- Install the **Windows App SDK** workload, or update Visual Studio to 18.0+.

**"SDK version not found" / wrong SDK selected**
- The build is pinned to the **.NET 10 SDK, 10.0.3xx or newer** (`global.json`, `10.0.300`,
  `rollForward: latestFeature`). Install it from https://dotnet.microsoft.com/download/dotnet/10.0
  — a `10.0.1xx`/`10.0.2xx` SDK will *not* satisfy this pin. These newer bands require MSBuild 18
  (Visual Studio 2026), so Visual Studio 2022 can no longer build the repo.

### Runtime Errors

**"Database file not found"**
- The first run creates the database automatically. Check `%LOCALAPPDATA%\MemoryTimeline\` exists;
  see `error.log` there for startup diagnostics.

**"MediaCapture not available"**
- Check microphone permissions in Windows Settings.

**Transcription produces nothing / hangs on first use**
- The first transcription downloads `ggml-base.bin` (~140 MB) to
  `%LOCALAPPDATA%\MemoryTimeline\Models\`; ensure network access for that one-time download.
  Afterwards Whisper runs fully offline.

---

## Documentation

| Document | Description |
|----------|-------------|
| [`FEATURE-AUDIT.md`](./FEATURE-AUDIT.md) | Feature-by-feature audit & root-cause analysis |
| [`HARDENING-FOLLOWUPS.md`](./HARDENING-FOLLOWUPS.md) | Deferred hardening items |
| [`DEVELOPMENT-STATUS.md`](./DEVELOPMENT-STATUS.md) | Current development status and next steps |
| [`DEVELOPMENT-HISTORY.md`](./DEVELOPMENT-HISTORY.md) | Consolidated phase completion reports |
| [`TESTING.md`](./TESTING.md) | Testing guide and best practices |
| [`DEPLOYMENT.md`](./DEPLOYMENT.md) | Deployment and distribution guide |
| [`scripts/README.md`](./scripts/README.md) | Setup scripts documentation |
| [`../README.md`](../README.md) | Repository overview (root README) |

---

## Contributing

- Branch from the active development branch.
- Follow WinUI 3 design guidelines and use `CommunityToolkit.Mvvm`.
- Keep `DbContext` usage **per-operation via the factory**, and surface errors in the UI rather
  than swallowing them.
- Ensure the solution builds (Visual Studio / CI) and update docs as needed.

---

## License

MIT License — see the main repository `LICENSE` file.

---

**Last Updated:** 2026-08-06
**Status:** Active development (Windows Native is the primary product)
