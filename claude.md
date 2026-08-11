# claude.md — AI-Assistant Development Guide (Memory Timeline)

This is the working guide for an AI assistant or new contributor making changes in
this repo. Read it before editing. For the product-level overview, see the root
[`README.md`](./README.md) and [`windows-native/README.md`](./windows-native/README.md).

## TL;DR

- **Primary product = the Windows Native app** under [`windows-native/`](./windows-native)
  (.NET 10 / WinUI 3, clean architecture). **Do all new work here.**
- **`MemoryTimeline.Core`, `.Data` and `.Sync` target plain `net10.0`** and must stay that
  way — no WinUI/WinRT types below the UI head. See
  [Keeping Core portable](#keeping-core-portable).
- A **macOS app** (SwiftUI) is being brought up under [`macos-native/`](./macos-native),
  sharing code with `ios-companion/`. See
  [`docs/design/MACOS-PORT-PLAN.md`](./docs/design/MACOS-PORT-PLAN.md).
- The repo also contains the **sync service** under [`services/`](./services) (ASP.NET
  Core, plain net10.0 — **CAN be built/tested with the `dotnet` CLI**, no VS msbuild
  needed; own Linux CI workflow `sync-api-build.yml`) and the **Windows sync client
  library** `windows-native/src/MemoryTimeline.Sync` — the iOS roadtrip companion sync
  layer. See [Sync service (iOS companion)](#sync-service-ios-companion).
- **You cannot build this repo in the cloud sandbox** — there is no .NET SDK, and the
  WinUI 3 app cannot be built with `dotnet build` anyway (see [Build & run reality](#build--run-reality)).
  Changes are validated by **CI on a Windows runner**, not by a local build. Reason
  carefully and keep changes correct-by-construction.

---

## Windows Native — the app you work on

### Solution layout (clean architecture)

The solution is `windows-native/src/MemoryTimeline.sln`, six projects:

| Project | Role |
|---------|------|
| **MemoryTimeline** | WinUI 3 app — Views (XAML), ViewModels, Controls, Converters, and platform services (audio, STT, notifications, jump list, navigation, theme). DI is wired in `App.xaml.cs`. |
| **MemoryTimeline.Core** | Business logic — services (events, timeline, queue, extraction, RAG, ask/query, narrative, resurfacing, recall prompts, media, backup/revisions, export/import, settings, search, analytics), DTOs, timeline math, `SettingKeys`. No UI, no EF Core internals leaking out. |
| **MemoryTimeline.Data** | Data access — `AppDbContext` (EF Core 10 + SQLite), entity models, repositories, and `SchemaUpgrader`. |
| **MemoryTimeline.Tests** | xUnit unit, integration, and performance tests. |
| **MemoryTimeline.Sync** | Sync client library (no UI) — `SyncApiClient` (pairing, push/pull/ack), artifact download, `SyncBackgroundWorker` loop, `LocalOutboxPublisher`, `RemoteChangeApplier`, settings/cursor stores. |
| **MemoryTimeline.SyncContracts** | Shared wire DTOs; lives at `shared-contracts/dotnet/` and is referenced into both this solution and `services/MemoryTimeline.Services.sln`. CamelCase JSON per the OpenAPI contract. |

Dependency direction: `MemoryTimeline` → `Core` → `Data`; `Sync` sits beside them
(`Sync` → `Core`/`Data`/`SyncContracts`). Keep it that way; don't make
`Core` depend on WinUI types or `Data` depend on `Core`.

### Data access — the single most important rule

**Every DB operation opens its own short-lived `AppDbContext` from the factory.** There
is no shared or app-lifetime context.

```csharp
await using var ctx = await _contextFactory.CreateDbContextAsync();
// ... one logical operation, then the context is disposed ...
```

- DI registers `services.AddDbContextFactory<AppDbContext>()` (in `App.xaml.cs`).
- Repositories take `IDbContextFactory<AppDbContext>` and create a context per method
  (see `MemoryTimeline.Data/Repositories/EventRepository.cs` for the canonical pattern).
- **Do NOT** reintroduce `services.AddDbContext<AppDbContext>()` (scoped), a cached/shared
  `AppDbContext` field, or a single context passed around. A desktop app has no request
  scope; a shared context across features/threads was the **root cause of prior
  concurrency bugs** ("A second operation was started on this context…", search errors
  after adding an event). Don't recreate it.
- Because repositories/services are **stateless over the factory**, they are registered
  **Singleton**. Follow that when adding a new repository or service.
- Startup uses `SchemaUpgrader.EnsureSchemaAsync` (EnsureCreated + idempotent DDL
  repairs for schema drift on older DBs) — a deliberate stopgap until real EF migrations
  are regenerated. Don't swap it for raw `EnsureCreated`.

### MVVM & UI conventions

- **CommunityToolkit.Mvvm** throughout: `[ObservableProperty]` for bindable state,
  `[RelayCommand]` for commands, `x:Bind` compiled bindings in XAML.
- **Cross-feature updates go through `WeakReferenceMessenger`**, not direct VM-to-VM
  references. E.g. creating/approving an event publishes a message
  (`EventCreatedMessage`) that the timeline subscribes to and refreshes on. Add new
  cross-feature signals the same way rather than coupling view models.
- **Marshal background work back to the UI thread** via the `DispatcherQueue` before
  touching bound state / `ObservableCollection`s.
- **Surface errors in the UI** — `InfoBar`, status text, in-dialog validation — and log.
  **Never swallow an exception into logs only.** A silent catch after a dialog closes is
  exactly the class of bug the audit fixed.
- Naming: Views `PascalCasePage.xaml`; ViewModels `XxxViewModel`; services as interface +
  impl (`IEventService` / `EventService`); commands generated from `[RelayCommand]`.

### Settings

- All setting keys live in **`MemoryTimeline.Core/Services/SettingKeys.cs`** — a
  constants class (snake_case values, e.g. `llm_provider`, `embedding_api_key`, `theme`;
  the Anthropic key is stored under `ApiKey` to preserve existing user rows).
  **Use these constants, never string literals** — a writer/reader key mismatch is what
  used to make settings "revert".
- Settings persist to the `app_settings` table and apply **without a restart**. The LLM
  client re-reads its configuration live, so a changed provider/model/key takes effect on
  the next call.

### External integrations

| Capability | Implementation | Notes |
|------------|----------------|-------|
| **Speech-to-text** | **Local Whisper** via `WhisperSpeechToTextService` (Whisper.net, ggml `base` model) | File-based, fully offline after a one-time ~140 MB model download to `%LOCALAPPDATA%\MemoryTimeline\Models\`. The recorded WAV is transcribed on-device — **not** the old Windows `SpeechRecognizer`, which is mic-only and can't transcribe a file. |
| **LLM** (extraction, Ask, narratives, recall wording) | **Anthropic Claude** (`AnthropicLlmService`) **or** any **OpenAI-compatible endpoint** (`OpenAiCompatibleLlmService` — Ollama, LM Studio, vLLM) | `RoutingLlmService` is the registered `ILlmService`; it re-reads `llm_provider` on **every call**, so switching providers applies live. `ILlmUsageTracker` counts per-session calls/tokens for the Settings UI. |
| **Embeddings (RAG / Ask)** | **Local ONNX** `all-MiniLM-L6-v2` (`OnnxEmbeddingService`, 384-dim, CPU execution provider) **or** **OpenAI** (`OpenAIEmbeddingService`, `AddHttpClient`) | `RoutingEmbeddingService` routes on `embedding_provider` (seeded default: `local`), so Connections/Ask semantic retrieval works with **no API key**. A dimension guard (`EmbeddingDimensionMismatchException`) blocks mixing 384/1536-dim vectors; Settings offers a re-embed flow. Local model (~90 MB + vocab) downloads once to `%LOCALAPPDATA%\MemoryTimeline\Models\all-MiniLM-L6-v2\`. |
| **Geocoding** | Optional **Nominatim** via `NominatimGeocodingService` (`AddHttpClient`) | **Opt-in and OFF by default** (`geocoding_enabled`). When off, location coordinates come only from photo EXIF or manual pin drops — no place name leaves the machine. |

### Sync service (iOS companion)

The iOS roadtrip companion sync layer (design:
[`docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md`](./docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md)):

- **Server** — `services/MemoryTimeline.SyncApi`: self-hosted, single owner, SQLite +
  filesystem artifact store. Devices join with a one-time **pairing code**
  (`POST /api/v1/devices/register`) and get a device-bound short-lived JWT plus a
  rotating refresh token; revocation (from Windows Settings → Sync) is immediate.
- **Loop** — the Windows client (`MemoryTimeline.Sync`, wired in `App.xaml.cs`) runs
  **pull → ingest → ack**: `SyncBackgroundWorker` pulls changes after its stored
  cursor, `RemoteChangeApplier` downloads each capture's audio artifact and feeds it
  to the idempotent `ICaptureIngestionService` (a remote capture enters review
  **exactly once**), the cursor advances only past applied/skipped/permanently-failed
  changes, then `LocalOutboxPublisher` drains `sync_outbox` to `POST /api/v1/sync/push`.
- Wire DTOs: `shared-contracts/dotnet/MemoryTimeline.SyncContracts` (camelCase JSON —
  clients must use `JsonSerializerDefaults.Web`); contract source of truth:
  `shared-contracts/openapi/memory-line-sync-v1.yaml`. Operator guide (run, Docker,
  pairing, endpoints): [`services/README.md`](./services/README.md).

### The memory pipeline

`Record (Windows MediaCapture) → recording_queue → Transcribe (local Whisper) → Extract
(LLM → pending_events) → Review/approve → Timeline`. **Text sources join audio in the
same queue**: pasted/typed text (Ctrl+Shift+V on the Queue page) becomes a
`recording_queue` row with `source_type = Text` and the text stored in its `transcript`
column, skipping transcription; Whisper output is likewise persisted to `transcript` and
reused across retries instead of re-transcribing. Each hop persists state so a failure is
recoverable and visible in the UI. Extraction assigns a **date precision**
(`DatePrecision`: Exact/Day/Month/Season/Year/Decade/Unknown) rather than inventing exact
days, and resolves people **alias-aware** (case-insensitive name → alias lookup). Approve
is an **atomic** transaction writing the event plus its tags/people/locations (and
precision/uncertainty fields) together.
Recordings and text enter the queue through `ICaptureIngestionService` (idempotent by
source capture ID, hashes the audio, and writes a sync outbox record atomically with
the capture/artifact/queue rows) — see the iOS companion design doc for the sync
architecture this feeds.

---

## Build & run reality

Critical for an assistant — get this wrong and you'll suggest broken commands:

- **x64 only.** The solution defines x64/x86/ARM64 — **no AnyCPU**. Always pass
  `-p:Platform=x64` (or select x64 in Visual Studio).
- **Build with Visual Studio 2022 (17.8+) or `msbuild.exe`, NOT `dotnet build` / `dotnet run`.**
  WinUI 3 PRI resource generation (`MrtCore.PriGen` → `ExpandPriContent`) uses a .NET
  Framework MSBuild task that loads under VS's `msbuild.exe` but **fails under the
  `dotnet` CLI build engine** (error **MSB4062**). So `dotnet build` / `dotnet run`
  cannot build or launch the app.
- **SDK pin.** `windows-native/src/global.json` pins the **.NET 10** SDK to its
  `10.0.1xx` feature band (`10.0.100`, `rollForward: "latestPatch"`). The band is
  deliberate: `10.0.1xx` declares MSBuild 17.14 as its minimum, so Visual Studio 2022
  can still build; bands `10.0.2xx`+ require MSBuild 18 / Visual Studio 2026. (An
  earlier note here claimed .NET 10 was incompatible with the WindowsAppSDK PRI task —
  that is not the case; CI builds it green via VS msbuild.)
- **CI is the validation gate.** `.github/workflows/windows-native-build.yml` builds
  **Release | x64** on **windows-latest** via VS `msbuild` (`/t:Restore,Build`) and runs
  tests best-effort. Compile success is the gate.
- **No .NET SDK exists in the cloud dev sandbox.** You cannot build or run locally here —
  rely on CI to validate. Make changes that are correct by inspection; don't assume you
  can "just run it".

For local dev on a real Windows machine: open the solution in VS 2022 and press F5, or
`msbuild MemoryTimeline.sln /t:Restore,Build /p:Configuration=Debug /p:Platform=x64`.

### Troubleshooting a local build

**`MSB4064: The "DisabledXamlOptionalChanges" parameter is not supported by the
"CompileXaml" task` (followed by `MSB4063`).** Read the two paths in the error: the
`.targets` file comes from one `microsoft.windowsappsdk.winui` version and the task DLL
from another. That is a **stale `obj/`**, not a code problem — a machine that built the
repo before the Windows App SDK 1.8 → 2.3.1 bump keeps generated `obj\*.nuget.g.props`
pointing the compiler path at the old package. The repo references only 2.3.1; nothing
references 1.8.

```powershell
cd windows-native\src
Get-ChildItem -Path . -Include bin,obj -Recurse -Directory | Remove-Item -Recurse -Force
msbuild MemoryTimeline.sln /t:Restore,Build /p:Configuration=Debug /p:Platform=x64
```

If it survives that, delete the stale package from the global cache
(`$env:USERPROFILE\.nuget\packages\microsoft.windowsappsdk.winui\1.8.*`) and restore again.
A clean CI checkout builds this commit green, so a local-only failure of this shape is
almost always leftover state.

**`global.json` SDK resolution fails.** The pin is `10.0.100` with
`rollForward: latestPatch`, which accepts `10.0.1xx` patches **only** — it will not roll
forward to `10.0.2xx`. A machine with only a newer band installed (Visual Studio 2026
ships one) cannot resolve it. Install a 10.0.1xx SDK, or raise the pin deliberately; the
band is chosen so Visual Studio 2022 can still build (see the note inside `global.json`).

### Tests: the SQLite parallelism hazard

`SqliteConnection.ClearAllPools()` is **process-global** — it disposes the underlying
`SQLitePCL.sqlite3` handle of every pooled connection, for every connection string.
`BackupService.CreateBackupAsync` calls it (legitimately, to release file handles), and so
do two test classes. xUnit runs test classes **in parallel**, so with pooling on, a clear
in one class can dispose a handle another class is mid-operation on. The victim then
throws `ObjectDisposedException` from wherever it happened to be — typically the pragma
interceptor's command in `ConnectionOpened` during `EnsureDeleted` teardown, which that
interceptor does not catch because it only expects `SqliteException`.

`TestDbContextFactory.CreateSqliteFile` therefore defaults to `Pooling=False`. Do not
"fix" a flake of this shape by catching `ObjectDisposedException` in the interceptor —
that hides the race and silently skips the `busy_timeout`/`foreign_keys` pragmas it exists
to set. Pass `pooled: true` only when pooling is what is being measured (`PerformanceTests`
does, because the app pools in production and an unpooled throughput number would be
timing a path that never ships).

---

## Adding a feature in Windows Native

1. Entity in `MemoryTimeline.Data/Models/` (+ `AppDbContext` / `SchemaUpgrader` if the
   schema changes).
2. Repository in `MemoryTimeline.Data/Repositories/` using the `IDbContextFactory`
   per-operation pattern.
3. Service interface in `MemoryTimeline.Core/Services/` + implementation.
4. Register the repository/service **Singleton** in `App.xaml.cs`.
5. ViewModel (CommunityToolkit.Mvvm) — `[ObservableProperty]` / `[RelayCommand]`; publish
   or subscribe to messenger events for cross-feature effects.
6. XAML View with `x:Bind`; marshal background updates via `DispatcherQueue`; surface
   errors in an `InfoBar`/status.
7. New settings → add a constant to `SettingKeys`.
8. Tests in `MemoryTimeline.Tests`.

---

## Data model (shared conceptual schema)

Core: `events` (with `date_precision` + `earliest_possible`/`latest_possible`
uncertainty window and `last_viewed_at`/`view_count` view tracking), `eras`,
`era_categories`, `era_tags`, `milestones`, `tags`, `people` (contact fields plus a
`merged_into_id` merge tombstone), `person_aliases`, `locations` (optional
`latitude`/`longitude`, `place_type`, `canonical_name`, `geocoded_at`).
Junctions: `event_tags`, `event_people`, `event_locations`.
Processing: `recording_queue` (with `source_type`, `source_label`, persisted
`transcript`), `pending_events` (mirrors the events precision columns).
Sync groundwork (Phase 0): `captures`, `capture_artifacts`, `sync_outbox` — the
device-neutral capture/artifact/outbox schema from the iOS companion design
([`docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md`](./docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md));
`recording_queue` also gained device-neutral provenance columns
(`source_capture_id` **unique**, `source_device_id`, `source_platform`,
`audio_artifact_id`, `content_sha256`, `processing_stage`, `sync_state`, ...).
Attachments & history: `event_media` (managed media copies, EXIF, thumbnails, content
hash), `event_revisions` (append-only edit history), `recall_prompts` (guided-recall
questions, deduped forever).
RAG: `event_embeddings` (with per-row `embedding_dimension`), `cross_references`.
UX: `drafts`, `saved_searches`.
System: `app_settings` — 33 seeded keys; **seed parity is three-way** (the
`AppDbContext.SeedDefaultSettings` HasData seed, the `SchemaUpgrader` INSERT OR IGNORE
backfill, and the `SettingKeys` constants must stay in sync — keep all three when adding
a seeded setting).
Identity: the unique name indexes on `people`/`tags`/`locations` are **COLLATE NOCASE**;
`SchemaUpgrader` defensively merges pre-existing case-variant duplicates (backfilling the
keeper's empty contact columns) before rebuilding each index.
DB file: `%LOCALAPPDATA%\MemoryTimeline\memory-timeline.db`.

---

## Security & privacy notes

- **Local-first**: all data in local SQLite; audio transcribed on-device (Whisper);
  embeddings default to the **local ONNX** provider, so Connections/Ask retrieval needs no
  cloud. Only the text you choose to process is sent to the configured LLM/embedding
  provider (which can itself be a local OpenAI-compatible endpoint).
- **Geocoding is opt-in and off by default**; when enabled, only the location's name is
  sent to Nominatim. Media/EXIF processing is entirely local.
- **API keys today live in the `app_settings` table** (not Windows Credential Manager —
  that's stale). **Encrypting keys at rest with DPAPI (`ProtectedData`) is a tracked
  follow-up**, not yet implemented. Don't describe key storage as encrypted; don't log keys.
- Use parameterized EF Core queries (default with LINQ) — no string-built SQL.

---

## Key docs (read these for detail)

| Doc | What's in it |
|-----|--------------|
| [`windows-native/README.md`](./windows-native/README.md) | Windows Native overview, setup, tech stack. |
| [`windows-native/FEATURE-AUDIT.md`](./windows-native/FEATURE-AUDIT.md) | Feature-by-feature audit + root-cause analysis of the bugs the rework fixed. |
| [`windows-native/HARDENING-FOLLOWUPS.md`](./windows-native/HARDENING-FOLLOWUPS.md) | Deferred hardening items (DPAPI, EF migrations, etc.). |
| [`windows-native/DEVELOPMENT-STATUS.md`](./windows-native/DEVELOPMENT-STATUS.md) | Phase-level status and next steps. |
| Root [`README.md`](./README.md) | Product overview + "Recent engineering work" summary. |

---

## Keeping Core portable

`MemoryTimeline.Core`, `.Data` and `.Sync` target **plain `net10.0`**. Only the UI head
(`MemoryTimeline`) and the test project target `net10.0-windows`. This is load-bearing,
not incidental: it is what lets the business layer back a non-WinUI head (see
[`docs/design/MACOS-PORT-PLAN.md`](./docs/design/MACOS-PORT-PLAN.md)) and what keeps the
layering rule above enforceable by the compiler rather than by review.

**Rules for anything under Core/Data/Sync:**

- **No WinUI or WinRT types.** No `Microsoft.UI.*`, no `Windows.*`. That includes
  presentation types that look harmless: `SolidColorBrush`, `Visibility`, `Color`,
  `Point`. Colors cross the boundary as `"#RRGGBB"` strings and become brushes in the
  view via `HexToBrushConverter` / `HexToUncertaintyBrushConverter`.
- **No Windows-only packages.** Core takes `Microsoft.ML.OnnxRuntime`, *not* the
  `.DirectML` variant — `OnnxEmbeddingService` deliberately uses default (CPU) session
  options, so the DirectML EP was never registered.
- **Platform capabilities enter through a port.** Declare the interface in Core and put
  the platform implementation in the head — `IThumbnailGenerator` →
  `WindowsThumbnailGenerator`, `ICaptureStatusPublisher` → `CaptureStatusPublisher`.
  Follow that shape rather than reaching for a `Windows.*` API inside Core.
- **Watch `Environment.SpecialFolder.LocalApplicationData`.** It compiles everywhere but
  resolves to `~/.local/share` on macOS, not `~/Library/Application Support`. Any new
  head needs a path abstraction; the port plan tracks this.

---

## The Swift apps (iOS companion + macOS)

Two SwiftUI apps share one folder of code: `ios-companion/MemoryLineCompanion/Shared/`
(domain models, sync client, SQLite stores, Keychain, and the timeline projection). The
macOS project compiles that folder directly via an Xcode **synchronized folder group**, so a
file added there lands in both apps with no project edit. Full detail:
[`macos-native/README.md`](./macos-native/README.md) and
[`docs/design/MACOS-PORT-PLAN.md`](./docs/design/MACOS-PORT-PLAN.md).

**Both apps have CI**, and both need a *committed shared scheme* to have it. Xcode writes
schemes per-user under `xcuserdata/`, which is not in git, so `xcodebuild -scheme` has
nothing to resolve without one — the iOS app had no CI for exactly this reason until
`MemoryLineCompanion.xcscheme` was committed. If you add a target or a project, commit its
scheme in the same change.

**Rules for anything under `Shared/`:**

- **It must compile on both platforms.** No `UIKit`, no `WidgetKit`, no
  `BackgroundTasks`. The two files that do (`ConfirmationFeedback`,
  `WidgetStatusPublisher`) are excluded from the macOS target by name in its
  `PBXFileSystemSynchronizedBuildFileExceptionSet` — adding a third means editing that
  list, which is a signal you are putting platform code in the wrong folder.
- **Everything is `internal`.** That is deliberate and is why the code is shared as a
  folder rather than a Swift package: a package would need `public` on ~50 declarations
  across a shipping app. Do not start adding `public` piecemeal.
- **Platform differences go behind `#if os(macOS)`, sparingly.** `KeychainTokenStore` is
  the model: the service name derives from `Bundle.main.bundleIdentifier` (so iOS keeps
  its existing item byte for byte and the Mac gets its own), and only the
  data-protection-keychain flag is `#if`-guarded.

**Sync client semantics are a server contract, not a local choice.** `MacSyncCoordinator`
and the iOS `StatusSyncCoordinator` are separate implementations — the iOS one needs
`BackgroundTasks` — but both must: page until `hasMore`; persist the cursor **before**
acking; hold the cursor when *applying* fails so the page replays; never ack a cursor that
did not advance; and last-write-wins on the Windows-authored `updatedAtUtc`. Change one
and check the other.

Both coordinators make **two disjoint sweeps** over a page: `capture_status` inline, because
it moves a local capture's lifecycle and can raise a notification; everything in
`SyncChangeEntityType.projectedByWindows` through the shared `TimelineProjectionApplier`.
That split is what keeps a coordinator from growing a case per entity type. A page is not
applied atomically and does not need to be — every write on both paths is an idempotent
upsert or a delete, so a failed page simply replays.

Windows remains the only writer and the only editing/approval surface. Everything the
phone and the Mac show of the archive is a **read-only projection** (design §19). The one
exception is a `pending_event_decision`: approving or rejecting an extracted event is one
idempotent bit with no field values to merge, so the contract admits it from a companion
without making the system multi-writer. The Mac sends them
(`MacReviewCoordinator`, queued to disk before any network call); the phone does not, and
whether it should is an open question, not an oversight.

---

**Last Updated:** 2026-08-08
**Primary target:** Windows Native (.NET 10 / WinUI 3) — `windows-native/`
**In progress:** macOS (SwiftUI) — `macos-native/`
