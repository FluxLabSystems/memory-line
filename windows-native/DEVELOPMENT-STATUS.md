# Windows Native Development Status

**Last Updated:** 2026-10-21
**Current Phase:** Phase 7 - Testing & Deployment (plus the 2026-08 feature-spec wave and the 2026-10 head wiring pass, landed)
**Overall Progress:** Phases 0-6 complete; F1–F12 feature spec implemented; iOS companion Phases 0-3 landed; toolchain rolled to .NET 10 + Windows App SDK 2.3.1; Phase 7 in progress
**Status:** 🔄 IN PROGRESS — builds green in CI, end-to-end runtime validation ongoing

---

## Head wiring pass (2026-10)

**Date:** 2026-10-21

A read-only audit of the WinUI head found a set of registered-but-dead or decorative
surfaces; this pass wired or removed them, and the companion Core changes — DPAPI
secret protection, non-WAV audio normalization, cross-reference heuristics, seed
parity, the projection backfill and the assistant responder — landed in the same
change:

- **Shell:** `MainViewModel` implemented (Queue / Review `InfoBadge` counts, refreshed from
  queue status events and messenger signals); NavigationView selection and back button now
  follow every navigation (programmatic "view on timeline", draft resume, activations);
  Ctrl+1..7 page accelerators and Alt+Left/Right history chords wired.
- **Jump List:** "Recent Events" tracks created/opened events; "New Event" opens the
  add-event dialog and "Start Recording" starts capturing. Unpackaged caveat: the API needs
  package identity, so the service logs the first failure once and then no-ops.
- **Audio:** "Import Audio…" on the Queue page (through `ICaptureIngestionService`);
  live input-level meter while recording; microphone picker in Settings
  (`audio_input_device_id`); the decorative sample-rate/bit-depth boxes replaced by
  read-only text (the format is fixed at 16 kHz / 16-bit / mono for Whisper).
- **Settings:** default zoom level now applied on the timeline's first load; Reset to
  Defaults uses the seeded values and can remove stored API keys ("Clear key").
- **Eras / Connections:** milestones can be edited and deleted (context flyout,
  double-click); era/milestone dialogs stay open and show the error when a save fails;
  tag suggestions have an Add button.
- **Removed as dead code:** `WindowsTimelineService` (User Activities; no callers, needs
  identity, Timeline UI gone from Windows 11), the fail-fast `WindowsSpeechRecognitionService`
  stub, the divergent `windows-native/packaging/Package.appxmanifest`, the csproj's
  reference to a publish profile that never existed, the nested GitHub workflow under
  `windows-native/.github/`, and the tracked `.vs/` and `log/` files.
- **Single instancing:** the secondary instance no longer blocks the STA thread on
  `RedirectActivationToAsync` (worker thread + `CoWaitForMultipleObjects`, bounded).
- **Scripts/docs:** setup scripts moved to .NET 10 / VS 2026 / WinAppSDK 2.3.1; stale
  claims (Windows Timeline, Windows Ink, Credential Manager, best-effort tests, dotnet
  build/test commands, DirectML) corrected across the docs.

---

## Toolchain roll-up: Windows 11 24H2 SDK + .NET 10 LTS + Windows App SDK 2.3.1 (2026-08-07)

| | Before | After |
|---|---|---|
| Windows SDK (`TargetFramework` platform) | 22621 (Win11 22H2) | **26100 (Win11 24H2)** |
| .NET | 8 (support ends 2026-11-10) | **10 LTS (support ends 2028-11-14)** |
| Windows App SDK | 1.5.240802000 | **2.3.1** (1.8 support ended 2026-09-09) |
| SDK BuildTools | 10.0.22621.3233 | 10.0.28000.2526 |
| EF Core / Extensions / ASP.NET packages | 8.0.0 | 10.0.10 |

`TargetPlatformMinVersion` is unchanged at 10.0.22621.0 — the app still *runs* on
Windows 11 22H2; only the compile-time SDK moved.

**Landed as three separately bisectable commits** (.NET 10, then WinAppSDK 2.3.1,
then the perf-bound fix) because the two framework moves fail in confusingly
similar ways — both surface as assembly-load and type-resolution errors.

**Developer prerequisites changed** — see `README.md` → Prerequisites:

- The **.NET 10 SDK, 10.0.3xx or newer**, pinned by `src/global.json`
  (`10.0.300`, `rollForward: latestFeature`). Bands `10.0.2xx`+ require
  MSBuild 18 / Visual Studio 2026, so VS 2022 can no longer build the repo.
- **Visual Studio 2026 is the supported IDE.** Microsoft supports targeting
  .NET 10 only in VS 18.0+, and the SDK pin requires it. The CI runner (`windows-latest`) is already
  VS 2026 / MSBuild 18.8.2.

**Notes for whoever picks this up next:**

- The original `CS1705` build break was caused by pinning
  `WindowsSdkPackageVersion` to `10.0.26100.87`, whose `WinRT.Runtime` ships in
  `lib/net9.0`, against a `net8.0` project — *not* by the Windows App SDK, whose
  managed assemblies all target `net6.0` and impose no .NET floor. Revision
  `.86` is the `lib/net8.0` build. The pin is now removed entirely; the .NET 10
  SDK selects a matching projection.
- `Microsoft.Data.Sqlite.Pre10TimeZoneHandling` is set at both entry points.
  .NET 10 changed offset-less stored timestamps to read as UTC instead of local,
  which would silently shift every historical event by the local UTC offset.
  **Auditing and migrating the stored values is an open follow-up** — do not
  remove the switch without doing that work.
- `CommunityToolkit.WinUI.UI.Controls` / `.Animations` 7.1.2 were removed as
  verified-unused (the `<controls:*>` tags resolve to `MemoryTimeline.Controls`).
  If toolkit controls are wanted later, the modern packages are the split
  `CommunityToolkit.WinUI.Controls.*` 8.x line, not the 7.x UWP-era one.
- FluentAssertions is deliberately held at 6.x: **8.x is commercially licensed**.
- `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (transitive via EF Core 10) still carries
  GHSA-2m69-gcr7-jv3q, with no patched 2.x release; the fix is in its 3.x major,
  which EF Core 10 does not expect. Left deliberately.
- `LoadTest_UpdateOperationsOnLargeDataset_PerformReasonably` measured 5546 /
  8109 / 10179 ms across three identical CI runs (it commits 100 separate SQLite
  transactions, so it is fsync-bound). Its bound was raised 5000 → 20000 ms as a
  gross-regression gate. It passed under .NET 8 at the old bound, so **a modest
  EF Core 10 write-path slowdown is unconfirmed but not excluded** — worth
  measuring on real hardware.
- Untested from CI: the runtime interaction between the `Microsoft.ML.OnnxRuntime`
  package Core references (plain CPU execution provider — the `.DirectML` variant is
  deliberately **not** referenced; see claude.md "Keeping Core portable") and the ONNX
  runtime bundled in WinAppSDK 2.3.1 (two ONNX runtimes in one process is a known hazard).

---

## iOS roadtrip companion — Phase 3: processing status & review handoff (2026-08)

**Date:** 2026-08-07

Phase 3 of the iOS roadtrip companion plan (design doc §19 Phase 3) landed. Its
exit criterion: **the user can understand a capture's full lifecycle from the
phone without opening Windows, while all editing and approval stay on Windows.**

**Direction of flow — one way, Windows-authored:**

```text
IQueueService advances a processing stage
  → ICaptureStatusPublisher writes a capture_status row into sync_outbox
  → LocalOutboxPublisher drains it to POST /sync/push
  → service change log + one latest-wins capture_status projection row
  → GET /sync/pull on the originating phone
  → applied locally; a local notification on a milestone
```

- **New `capture_status` change type** — `CaptureStatusChangePayload` and
  `SyncChangeEntityType.CaptureStatus` in the shared contracts, with the
  matching `SyncEntityType.CaptureStatus` on the Windows outbox side
  (`MemoryTimeline.Data/Models/SyncOutbox.cs`). `entityId` is the `captureId` — one status
  entity per capture — and only `upsert` is meaningful. The payload carries the
  coarse `SyncCaptureStatus`, the finer `QueueProcessingStage`, a transcript
  preview **capped at 600 characters** plus the full transcript's character
  count, pending/approved review counts, and a failure reason with its retry
  classification.
- **Windows publisher** — `CaptureStatusPublisher` projects a queue item's state
  into an outbox row. It publishes only for captures that came from another
  device (a Windows-local recording has no phone waiting on it) and drops a
  projection identical to the last one, so the pipeline can call it after every
  transition without producing noise.
- **Service** — `/sync/push` validates `capture_status` entries before they
  enter the change log and stores a `capture_status` projection row per capture;
  `/sync/pull` fans it out to the owner's other devices. Echo suppression keys
  on the *publishing* device, never on the device a capture came from, which is
  what lets a phone receive status about its own captures. The startup path
  creates the new table with idempotent DDL, so a Phase 1/2 deployment upgrades
  without deleting `sync.db`.
- **iOS** — a pull-driven `StatusSyncCoordinator` applies changes to a local
  `capture_status` table, and history/detail fuse the local upload leg with the
  remote processing leg into one readable position, plus the transcript preview,
  the review counts, and where the recording currently exists.

**The boundary, stated explicitly:** editing and approval stay on Windows.
Nothing in `capture_status` is an instruction — it is a read-only projection of
Windows-side state. The phone never authors the entity type, never edits a
transcript, and never approves or rejects an extracted event.

**Deliberate decision — notifications are LOCAL, not APNs.** Per the resolved
§22.1/§22.2 decisions (self-hosted deployment, no user accounts), there is no
push certificate and no notification server anywhere in this design. The phone
posts its own local notification the moment a *pull* observes a capture becoming
review-ready, completing, or failing. The trade-off accepted: updates arrive when
the phone pulls — foreground, after an upload pass, or on an iOS-scheduled
background refresh — not the instant Windows finishes.

**What Phase 3 deliberately does NOT do:**

- **The full transcript stays on Windows.** The phone gets a ≤600-character
  excerpt plus a character count, never a replica, and there is no endpoint that
  would give it one.
- **The phone cannot edit or approve.** No transcript editing, no event
  approval/rejection, and no way to restart Windows-side processing from the
  phone; a capture that failed on the PC is retried on the PC.
- **Progress does not rewind on the phone.** A pulled status can advance a
  capture's stored lifecycle state but never move it back, so a replayed or
  out-of-order change is harmless. A reported failure is the one transition
  allowed to reset that floor, so a Windows-side retry can move the capture
  forward again.
- **No audio flows back.** The design's Phase 3 line about playback of the
  original audio is satisfied by the phone's own local file; the Windows-side
  normalized artifact is not downloadable.
- **`processingStage` is forwarded unvalidated.** Only `status` is checked
  against a vocabulary, so consumers must tolerate an unrecognized stage string.

**Privacy (§14.5):** the transcript preview, transcript counts, and failure
reason are stored but never logged on any tier. `failureReason` is fixed
publisher text keyed on the failure stage — never `RecordingQueue.ErrorMessage`,
whose raw exception text can contain file paths or provider detail.

**Contracts:**
[`../shared-contracts/openapi/memory-line-sync-v1.yaml`](../shared-contracts/openapi/memory-line-sync-v1.yaml)
(v1.2, additive) and
[`../shared-contracts/json-schema/capture-status-payload.v1.json`](../shared-contracts/json-schema/capture-status-payload.v1.json).

**Next step:** Phase 4 — voice assistant (assistant sessions and streaming
turns, STT/TTS, memory retrieval, current trip context, interruption and
cancellation).

---

## iOS roadtrip companion — Phase 1: sync service & Windows sync client (2026-08)

**Date:** 2026-08-07

Phase 1 of the iOS roadtrip companion plan (design doc section 19; see the
Phase 0 entry below for the groundwork it builds on) landed:

- **Sync service** — new `services/MemoryTimeline.SyncApi` (ASP.NET Core,
  net10.0; unlike the WinUI solution it is plain cross-platform .NET and
  builds/tests/runs with the `dotnet` CLI). Self-hosted Mode A server
  (design §4.2): device pairing/registration gated by an owner pairing code,
  self-issued HMAC-SHA256 JWT access tokens with refresh-token rotation and
  immediate revocation (§14.1); idempotent capture metadata ingestion
  (replaying a `captureId` returns the existing capture); chunked artifact
  upload (initiate → PUT parts → complete with byte-length/part-count/SHA-256
  validation, §11.4) into a filesystem artifact store; and an append-only
  `sync_changes` log serving cursor-based `GET /sync/pull` (own-device echo
  suppression), per-device idempotent `POST /sync/push` (push receipts keyed
  on client sequence), and `POST /sync/ack`. SQLite metadata + artifact blobs
  live under one configurable data directory. Operator guide:
  [`../services/README.md`](../services/README.md).
- **Windows sync client** — new `MemoryTimeline.Sync` project in the WinUI
  solution. `SyncBackgroundWorker` runs a periodic (30 s) pull → apply →
  ack → publish-outbox loop; `RemoteChangeApplier` turns each pulled capture
  change into artifact download + idempotent `ICaptureIngestionService`
  ingestion, so a remote audio capture enters the existing review queue
  **exactly once**; `LocalOutboxPublisher` drains the Phase 0 `sync_outbox`
  in order to `/sync/push` and marks accepted/duplicate rows delivered. The
  cursor only advances past applied/skipped/permanently-failed changes;
  retryable failures stop the pull and retry next cycle. Settings → Sync UI:
  server URL + pairing code, enable/disable, sync-now, status line, and
  unpair/revoke. New `SettingKeys.Sync*` constants (server URL, device ID,
  tokens, cursor, auto-process); wire DTOs shared with the service via
  `shared-contracts/dotnet/MemoryTimeline.SyncContracts` (camelCase JSON,
  `JsonSerializerDefaults.Web`).
- **CI** — new Linux workflow `.github/workflows/sync-api-build.yml` builds
  `services/MemoryTimeline.Services.sln` and runs the SyncApi test suite with
  the dotnet CLI on `ubuntu-latest`, on pull requests and on pushes to `main`
  (path-filtered to `services/**`, `shared-contracts/**` and the workflow
  itself); the Windows workflow runs on pull requests and `main` and also for
  `shared-contracts/dotnet/**` changes.

**Exit criterion status:** implemented and integration-tested at the service
level — a test client can register, create a capture, upload its audio
artifact, and the Windows pull → ingest path dedupes it into review exactly
once. End-to-end validation against a real iPhone awaits the Phase 2 iOS
client.

**Known follow-ups (from the post-fix verification pass) — status as of 2026-10-22:**

- ✅ The `RefreshTokenHash` concurrency token applies to every device-row update;
  ack, revoke and the capture complete/retry touch now reload and retry
  (`DeviceRowConcurrency`, three attempts) instead of surfacing a 500 when they
  race a concurrent refresh.
- ✅ Crash-orphaned `*.tmp` assembly files in the service's artifact store are
  swept by `RetentionSweepHostedService` once they are older than
  `TemporaryFileGraceMinutes` (60).
- ✅ The register endpoint shares the authenticated-route `Idempotency-Key`
  reader and rejects keys longer than 128 characters with 400.
- Service schema is still `EnsureCreated` plus idempotent startup DDL (no
  migrations); the startup DDL now also creates the capture_status, assistant
  and trip tables, so upgrades across those phases no longer require deleting
  `sync.db` (see services/README.md).
- ✅ Sync tokens are stored in the Windows app's settings table like the other
  keys, through the `ISecretProtector` port (DPAPI on Windows; legacy plaintext
  rows are re-written in protected form on first read).
- ✅ Artifact retention per the resolved design §22 decisions: artifact bytes are
  purged 14 days after Windows acknowledges a `completed` capture_status
  (`ArtifactRetentionDays`), or oldest-acknowledged-first under
  `ArtifactStorageBudgetBytes`; the row survives as `purged` and downloads
  answer a permanent 410.

**Design decisions:** all §22 open decisions were resolved by the owner on
2026-08-07 — self-hosted only, no user accounts (pairing-code device model),
Windows-local transcription (GPU/NPU when available), optional on-device iOS
Whisper, Google places within the free tier with informed Apple failover,
CarPlay screens designed for v1 with the entitlement deferred (Siri/widget/
Action Button carry the in-car experience), 14-day artifact retention after
archival ack, pending transcripts excluded from the assistant unless
explicitly included via UI, 14-day route-context retention. See the design
doc §22 for the full records.

**Next step:** Phase 2 — iOS capture MVP (SwiftUI shell, one-touch recording,
durable local queue, background upload, capture history/status).

---

## iOS roadtrip companion — Phase 0: contract & schema groundwork (2026-08)

**Date:** 2026-08-06

Phase 0 of the iOS roadtrip companion plan (a separate track from the app phases
below; see
[`../docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md`](../docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md),
section 19) landed:

- **Device-neutral capture schema** — new `captures`, `capture_artifacts`, and
  `sync_outbox` tables (EF models, `AppDbContext` configuration, idempotent
  `SchemaUpgrader` DDL); `recording_queue` extended with device-neutral
  provenance columns (`source_capture_id` **unique**, `source_device_id`,
  `source_platform`, `audio_artifact_id`, `original_file_name`,
  `content_sha256`, `sync_state`, `received_at`, `processing_stage`).
- **Idempotent ingestion service** — `ICaptureIngestionService` /
  `CaptureIngestionService`: resolves artifacts via `IArtifactResolver`
  (Phase 0: local files), verifies declared byte length + SHA-256, dedupes by
  source capture ID (including the insert race), and writes capture + artifact +
  queue item + sync outbox record in **one atomic SaveChanges**
  (`ICaptureRepository.IngestAsync`).
- **Queue processing stages** — `QueueService` now stamps
  `ProcessingStage`/`SyncState` through the pipeline (transcribing,
  review_ready/completed, failed_configuration, failed_retryable), gained
  `ProcessCaptureAsync`, and resets items to pending on cancellation.
- **Shared API contracts** — new `shared-contracts/` directory:
  `openapi/memory-line-sync-v1.yaml` (Sync API v1 skeleton) and
  `json-schema/capture-envelope.v1.json` (remote capture ingestion envelope);
  Swift/C# clients will be generated from these (see `shared-contracts/README.md`).

Existing Windows recordings pass through the new ingestion abstraction with no
UI behavior change (the Phase 0 exit criterion).

**Next step:** Phase 1 — sync service (device registration/auth, capture
metadata + artifact upload, cursor-based push/pull) and the Windows sync worker
that drains the new `sync_outbox`.

## 2026-08 feature-spec implementation (F1–F12)

**Date:** 2026-08-06

Twelve features from the Memory Timeline feature spec were implemented on top of the
post-audit base, in dependency-ordered waves (F1/F3/F4 → F2/F7/F5 → F8/F6/F9 →
F11/F12/F10) with a verification fix pass after each wave. Every wave pushed CI-green
(`Release|x64`, `windows-latest`).

**Per-feature status (all landed):**

- **F1 — Date precision & uncertainty:** `DatePrecision` enum
  (Exact/Day/Month/Season/Year/Decade/Unknown) + `earliest_possible`/`latest_possible`
  window on `events` and `pending_events`; precision-honest date display everywhere;
  extraction assigns precision instead of inventing days; the category list bug fixed.
- **F3 — Text/paste capture:** `recording_queue.source_type` (Audio/Text/Imported),
  persisted `transcript` reused across retries (also for Whisper output), Ctrl+Shift+V
  paste-capture dialog on the Queue page.
- **F4 — Ask your timeline:** new Ask page; hybrid keyword+semantic retrieval merged
  with reciprocal-rank fusion; grounded answers cited to real events; honest refusal
  when nothing relevant exists; keyword-only degraded mode without embeddings.
- **F2 — Media attachments:** `event_media` table, managed copy tree under
  `%LOCALAPPDATA%\MemoryTimeline\Media`, EXIF via MetadataExtractor, thumbnails,
  content-hash dedupe, lightbox, drag-drop.
- **F7 — On This Day / Home:** new default landing page; precision-aware anniversaries
  (only Exact/Day events surface on a specific day); event view tracking; optional
  daily toast; `event:` deep links fixed; first-run empty state.
- **F5 — Narrative generation:** grounded prose per scope (range/era/person) with
  precision-honest dates and code-enforced citation integrity; Markdown/HTML export;
  story dialog reachable from Eras, Timeline, and People.
- **F8 — Spans/swimlanes/uncertainty + perf:** duration span bars, collapsible
  swimlanes, uncertainty band rendering; scroll reload coalescing (major timeline
  performance fix).
- **F6 — Guided recall prompts:** `recall_prompts` table; five gap sources (density
  gaps, dangling people, era edges, thin events, anniversaries); deduped against all
  prior prompts so a question is never re-asked; cards on Queue and Home.
- **F9 — People hub:** `person_aliases`, tombstone merges (`people.merged_into_id`),
  case-insensitive NOCASE identity with a defensive migration that merges case-variant
  duplicates and backfills contact columns; person profiles; alias-aware extraction.
- **F10 — Offline map:** coordinates on `locations` (EXIF GPS backfill + pin drop),
  opt-in Nominatim geocoding **off by default**, fully offline canvas map (deliberately
  **not** WinUI `MapControl`/Azure Maps).
- **F11 — Pluggable AI:** local ONNX `all-MiniLM-L6-v2` embeddings (384-dim, CPU EP) —
  local-first Connections with no OpenAI key; OpenAI-compatible LLM endpoint support
  (Ollama/LM Studio); per-call provider routing (`RoutingLlmService`/
  `RoutingEmbeddingService`); embedding dimension guard + re-embed flow; per-session
  LLM usage counter.
- **F12 — Backup/restore/revisions:** `.mtbak` online-backup archives (optionally with
  media), restore with preview + explicit confirmation, append-only `event_revisions`
  history + history dialog, scheduled daily/weekly backups, and a real Clear Cache.

**Infrastructure fixes in the same wave:** the SQLite pragma interceptor no longer
rewrites `journal_mode` on read-only connections; CI triggers on the feature branch;
`app_settings` seed parity is three-way (33 keys, `AppDbContext` seed = `SchemaUpgrader`
backfill = `SettingKeys`).

**Explicitly deferred (not implemented, by design):** PhotoImportPage bulk-import
wizard; map tile basemaps (the map is a plain offline canvas); a location chip on
timeline events; real LLM token streaming (responses arrive whole); PDF narrative
export (Markdown/HTML only); vertical scrolling within swimlanes; reorderable lanes;
an EXIF test against a real binary image fixture; a Merge restore mode (restore is
replace-only, with a pre-restore safety backup); DirectML execution provider for the
ONNX embedder (CPU is used).

**Honest caveats:** all of the above is code-complete and CI-green, but **runtime
validation on a real Windows machine is still pending** — none of the new features have
been exercised end-to-end outside tests. A **baseline EF migration now exists**
(`Data/Migrations/20261022004346_InitialCreate`), but every schema addition still ships
through `SchemaUpgrader`'s idempotent DDL at runtime; API keys and sync tokens are
DPAPI-protected through `ISecretProtector`.

---

## Post-audit hardening (2026-07)

**Date:** 2026-07-10

Between the Phase 6 "feature-complete" milestone and the start of formal Phase 7
testing, three live user-reported bugs (Save on Add Event did nothing; searching
for the added event showed a "search error"; the timeline Refresh button showed
nothing) triggered a **multi-agent feature audit and fix pass**. Five specialized
agents traced every major feature end-to-end (UI → command → ViewModel → service →
repository → SQLite → UI) with file:line evidence. The full findings are in
[`FEATURE-AUDIT.md`](FEATURE-AUDIT.md); deferred items are tracked in
[`HARDENING-FOLLOWUPS.md`](HARDENING-FOLLOWUPS.md).

**What the audit found (root causes):**

- **A category string case mismatch + swallowed errors** — the Add Event dialog
  fell back to `"Other"` (capital O) while validation checked case-sensitively
  against lowercase categories, throwing before any DB write; the exception was
  reduced to a status-text log line and the dialog had already closed. This alone
  produced "Save does nothing" (Bug 1) and, downstream, "Refresh shows nothing"
  (Bug 3, because no row was ever inserted).
- **One app-wide shared, never-disposed `AppDbContext`** — every page resolved its
  ViewModel from the root provider, so a single Scoped context served the whole
  app. Overlapping operations (e.g. a fire-and-forget embedding task racing an
  un-debounced per-keystroke search) threw "a second operation was started on this
  context," surfaced as the "search error" (Bug 2) and intermittent failures
  across features.
- **Mic-based STT that ignored the recorded file** — the speech-to-text path loaded
  the WAV into an unused variable and then transcribed the live microphone, so the
  core audio ingestion loop never processed the actual recording.

**What was fixed in this pass:**

- Category-casing fallback normalized and validation made case-insensitive; Add
  Event dialog now uses a deferral and surfaces errors in-dialog instead of
  silently closing.
- Data access converted to **per-operation contexts** via
  `IDbContextFactory<AppDbContext>`; the shared-context `Task.Run` for embeddings
  removed/rescoped; search input debounced/cancellable; `Tag.Name`→`TagName`.
- Startup now uses `SchemaUpgrader.EnsureSchemaAsync` (EnsureCreated + idempotent
  drift repair); stale EF migrations were deleted (regenerating a real migration
  baseline is a follow-up).
- STT replaced with **local Whisper** (Whisper.net, ggml base model, offline after
  first model download), transcribing the recorded file instead of the live mic.
- Atomic approve (event + tags/people/locations in one transaction) with timeline
  auto-refresh; settings key writer/reader unified under a `SettingKeys` constants
  class; embeddings settings-key + provider/dimension wiring; RAG mapped-column
  queries + persistence + `IsAvailable`; export `Include` fix; unified
  `INotificationService`; navigation param overload; dead
  `AnthropicClaudeService`/`IAudioService` deleted.
- A **CI Windows-build workflow** was added
  (`.github/workflows/windows-native-build.yml`) that builds `Release|x64` on
  `windows-latest` and runs the test suite.

**Outcome / honest current state:** the solution **builds green in CI** on a
Windows runner, but **end-to-end runtime validation is ongoing** — this is **not
yet production ready**. The audit work resolved the three reported bugs at the code
level and removed the shared-context foundation defect underneath them; the
remaining Phase 7 work is runtime verification plus the follow-ups listed below.

**Build reality (important for anyone building locally):**

- The solution is **x64-only** (no AnyCPU configuration).
- WinUI 3 PRI generation needs **Visual Studio `msbuild.exe`**; `dotnet build`
  fails with **MSB4062**.
- `global.json` pins the **.NET 10 SDK** at `10.0.300` with `rollForward: "latestFeature"`
  (any 10.0.3xx-or-newer band; those bands need MSBuild 18 / Visual Studio 2026).
- CI (`windows-native-build.yml`) builds `Release|x64` on `windows-latest` and runs
  the built test assembly with `dotnet vstest`; both gate the run.

**Immediate follow-ups (see `HARDENING-FOLLOWUPS.md`):** end-to-end runtime
validation; switching existing databases from `SchemaUpgrader` to the generated
migration baseline; MSIX packaging + Microsoft Store submission. (DPAPI protection of
keys and tokens, the EF baseline migration, the Whisper model/language options and
analytics export have landed; the tests were moved to the context factory and gate CI.)

---

## Executive Summary

The Memory Timeline Windows native application has completed **Phases 0-6** and is
in **Phase 7 (Testing & Deployment)**. Following the July 2026 post-audit hardening
pass and the **August 2026 F1–F12 feature-spec implementation** (see above), the app
is feature-complete across core functionality, advanced features, Windows
integration, and polish, and **builds green in CI on a Windows runner**. Phase 7
work is in progress: **end-to-end runtime validation is ongoing** (now covering the
feature wave as well), followed by performance validation, MSIX packaging, and
Microsoft Store submission. The application is **not yet production ready**.

### Quick Status Overview

| Phase | Status | Completion | Key Deliverables |
|-------|--------|------------|------------------|
| **Phase 0: Preparation** | ✅ Complete | 100% | Development environment, WinUI 3 skeleton, SQLite demo |
| **Phase 1: Core Infrastructure** | ✅ Complete | 100% | Database (EF Core), Settings, Navigation, Repository Pattern |
| **Phase 2: Timeline Visualization** | ✅ Complete | 100% | Timeline canvas, Zoom/pan, Touch support, TimelinePage UI |
| **Phase 3: Audio & Processing** | ✅ Complete | 100% | Audio recording, Queue system, Windows STT, QueuePage UI |
| **Phase 4: LLM Integration** | ✅ Complete | 100% | Claude API, Event extraction, ReviewPage, PendingEvent workflow |
| **Phase 5: RAG & Embeddings** | ✅ Complete | 100% | Backend services, ConnectionsPage UI, embedding workflow |
| **Phase 6: Polish & Integration** | ✅ Complete | 100% | Export/Import, Windows notifications, JumpList (the Windows Timeline integration was later removed as dead code) |
| **Phase 7: Testing & Deployment** | 🔄 In Progress | — | CI Windows build green; runtime validation, MSIX, Store pending |

> **Note (2026-07-10):** A post-Phase-6 multi-agent feature audit + fix pass hardened the
> data layer and core flows before formal Phase 7 testing. The solution now builds green in
> CI (`Release|x64`, `windows-latest`); end-to-end runtime validation is the current focus.
> See the "Post-audit hardening (2026-07)" section above, plus `FEATURE-AUDIT.md` and
> `HARDENING-FOLLOWUPS.md`.

---

## Phase 0: Preparation - ✅ COMPLETED

### Summary
Established complete project structure, solution configuration, and foundational architecture for the Windows native application.

### Completed Items

- [x] **Project Structure**
  - [x] Created `windows-native` directory
  - [x] Set up solution structure with 4 projects
  - [x] Configured clean architecture layers

- [x] **Solution & Project Files**
  - [x] `MemoryTimeline.sln` - Main solution file
  - [x] `MemoryTimeline.csproj` - WinUI 3 application project
  - [x] `MemoryTimeline.Core.csproj` - Business logic project
  - [x] `MemoryTimeline.Data.csproj` - Data access project
  - [x] `MemoryTimeline.Tests.csproj` - Test project

- [x] **NuGet Package References**
  - [x] Microsoft.WindowsAppSDK 1.5+
  - [x] CommunityToolkit.Mvvm 8.2.2
  - [x] EF Core 8 with SQLite
  - [x] xUnit testing framework
  - [x] Moq + FluentAssertions
  - [x] Anthropic SDK
  - [x] ONNX Runtime with DirectML
  - *(Historical list. The toolchain has since moved to Windows App SDK 2.3.1 and EF Core 10, and Core references the plain `Microsoft.ML.OnnxRuntime` package on the CPU execution provider — the DirectML variant is not used. See the toolchain roll-up above.)*

- [x] **Database Layer**
  - [x] All 13 data models (Event, Era, Tag, Person, Location, etc.)
  - [x] AppDbContext with EF Core configuration
  - [x] Repository pattern with generic and specialized interfaces
  - [x] Initial migrations created

- [x] **WinUI 3 Application Shell**
  - [x] App.xaml / App.xaml.cs with DI setup
  - [x] MainWindow.xaml with NavigationView
  - [x] Package.appxmanifest
  - [x] DPI awareness configured

**Completion Date:** 2025-11-21
**Verification:** consolidated into `DEVELOPMENT-HISTORY.md` (the original per-phase reports are no longer in the tree).

---

## Phase 1: Core Infrastructure - ✅ COMPLETED

### Summary
Implemented complete database layer, settings system, navigation framework, and dependency injection infrastructure.

### Completed Items

- [x] **Database Layer (EF Core)**
  - [x] All entity models with relationships configured
  - [x] EventRepository with specialized queries
  - [x] EraRepository, TagRepository, PeopleRepository, LocationRepository
  - [x] RecordingQueueRepository, PendingEventRepository
  - [x] EventEmbeddingRepository, CrossReferenceRepository
  - [x] Unit of Work pattern
  - [x] Timestamp auto-updates
  - [x] Soft delete support

- [x] **Settings System**
  - [x] SettingsService with Windows.Storage integration
  - [x] Strongly-typed settings models
  - [x] API key storage in the `app_settings` table, DPAPI-protected through `ISecretProtector` (Windows Credential Manager was never used)
  - [x] Theme settings (Light/Dark)
  - [x] LLM configuration settings
  - [x] Audio quality settings

- [x] **App Shell & Navigation**
  - [x] MainWindow with NavigationView
  - [x] Frame-based navigation service
  - [x] Navigation menu items
  - [x] Page transition animations
  - [x] Title bar customization
  - [x] Theme switching (Light/Dark/System)

- [x] **Dependency Injection**
  - [x] Full DI container setup (Microsoft.Extensions.DependencyInjection)
  - [x] All services registered (Singleton, Scoped, Transient)
  - [x] All repositories registered
  - [x] All ViewModels registered
  - [x] All Pages registered

**Completion Date:** 2025-11-21
**Verification:** consolidated into `DEVELOPMENT-HISTORY.md` (the original per-phase reports are no longer in the tree).

---

## Phase 2: Timeline Visualization - ✅ COMPLETED

### Summary
Built high-performance timeline rendering with zoom/pan controls, touch support, and smooth 60 FPS performance.

### Completed Items

- [x] **Timeline Canvas**
  - [x] Custom TimelineControl with XAML
  - [x] Virtualization for 5000+ events
  - [x] Hardware-accelerated rendering
  - [x] Viewport management with lazy loading
  - [x] Memory-efficient caching

- [x] **Event Rendering**
  - [x] Event bubbles with category colors
  - [x] Category icons (XAML Geometry)
  - [x] Era gradient backgrounds
  - [x] Duration event bars
  - [x] Tooltip on hover
  - [x] Responsive sizing

- [x] **Zoom & Pan Controls**
  - [x] 4 zoom levels: Year, Month, Week, Day
  - [x] Smooth zoom transitions with animations
  - [x] Pan with touch/mouse drag
  - [x] Keyboard navigation (←/→ arrows, +/- zoom)
  - [x] "Today" quick navigation (T key)

- [x] **Touch & Pen Support**
  - [x] Touch gestures (pinch-to-zoom, drag-to-pan)
  - [ ] ~~Windows Ink integration~~ — never implemented; no Ink code exists in the app
  - [x] High-DPI support
  - [x] Per-monitor DPI awareness

- [x] **TimelinePage UI**
  - [x] Command bar with zoom controls
  - [x] Date range display
  - [x] Event details panel
  - [x] Era filter row (per-era checkboxes with All/None)
  - [x] Loading indicators

**Completion Date:** 2025-11-22
**Verification:** consolidated into `DEVELOPMENT-HISTORY.md` (the original per-phase reports are no longer in the tree).

---

## Phase 3: Audio & Processing - ✅ COMPLETED

### Summary
Implemented audio recording, queue management, and speech-to-text integration using Windows APIs.

### Completed Items

- [x] **Audio Recording**
  - [x] MediaCapture API integration
  - [x] Audio device selection (Settings → Audio Recording microphone picker, persisted as `audio_input_device_id`; wired 2026-10)
  - [x] Format: 16 kHz, 16-bit, mono WAV (fixed — exactly what the local Whisper engine consumes)
  - [x] Pause/resume/cancel controls
  - [x] Live input-level meter while recording (dB-scaled peak sampled from a `MediaFrameReader`; wired 2026-10 — a level bar, not a waveform)
  - [x] File management (`%LOCALAPPDATA%\MemoryTimeline\AudioRecordings`; the app is unpackaged, so not the MSIX ApplicationData folder)
  - [x] Import existing audio files (Queue page "Import Audio…", through the capture-ingestion pipeline; 2026-10)

- [x] **Queue System**
  - [x] RecordingQueue database table
  - [x] QueueService with background processing
  - [x] Status tracking (pending, processing, completed, failed)
  - [x] Retry logic with exponential backoff
  - [x] Progress reporting
  - [x] Error handling and logging

- [x] **Speech-to-Text**
  - [x] ISpeechToTextService interface
  - [x] Local Whisper implementation (`WhisperSpeechToTextService`, file-based, offline). The original Windows Speech Recognition implementation transcribed the live microphone instead of the file; it was reduced to a fail-fast stub in the 2026-07 audit pass and deleted in 2026-10.
  - [x] Mock STT service for testing
  - [x] Transcription progress reporting
  - [x] Confidence scoring
  - [x] Multi-engine support (ready for cloud STT)

- [x] **QueuePage UI**
  - [x] Recording controls (Start, Pause, Cancel)
  - [x] ListView with queue items
  - [x] Play/remove buttons for queue items
  - [x] Progress bars for processing
  - [x] Status indicators with icons
  - [x] File metadata display (duration, size, timestamp)

**Completion Date:** 2025-11-22
**Verification:** consolidated into `DEVELOPMENT-HISTORY.md` (the original per-phase reports are no longer in the tree).

---

## Phase 4: LLM Integration - ✅ COMPLETED

### Summary
Integrated Anthropic Claude API for intelligent event extraction from transcripts with review workflow.

### Completed Items

- [x] **LLM Service**
  - [x] ILlmService interface
  - [x] AnthropicService implementation
  - [x] Claude 3.5 Sonnet integration
  - [x] Structured prompt engineering
  - [x] JSON response parsing
  - [x] Retry logic with Polly library
  - [x] Error handling

- [x] **Event Extraction**
  - [x] EventExtractionService
  - [x] Extract title, dates, description, category
  - [x] Extract tags, people, locations
  - [x] Confidence scoring per field
  - [x] Relative date parsing
  - [x] Category classification (9 categories)
  - [x] Batch processing support

- [x] **Review Workflow**
  - [x] PendingEvent entity and repository
  - [x] ReviewPage UI with pending events list
  - [x] Event edit dialog for modifications
  - [x] Approve/Edit/Reject actions
  - [x] Bulk operations (Approve All, Reject All)
  - [x] Confidence indicators with color coding
  - [x] Integration with timeline (approved events)

- [x] **ReviewPage UI**
  - [x] Pending events ListView
  - [x] Event details cards
  - [x] Edit dialog with all fields
  - [x] Confidence score displays
  - [x] Command bar with bulk actions
  - [x] Empty state handling

**Completion Date:** 2025-11-23
**Verification:** consolidated into `DEVELOPMENT-HISTORY.md` (the original per-phase reports are no longer in the tree).

---

## Phase 5: RAG & Embeddings - ✅ COMPLETED

### Summary
Implemented RAG (Retrieval-Augmented Generation) capabilities with vector embeddings, similarity search, cross-reference detection, and pattern analysis.

### Completed Items

- [x] **Embedding Service**
  - [x] IEmbeddingService interface
  - [x] OpenAIEmbeddingService implementation
  - [x] text-embedding-3-small model (1536 dimensions)
  - [x] Batch embedding generation
  - [x] Cosine similarity calculation
  - [x] K-nearest neighbors search
  - [x] Threshold-based filtering

- [x] **RAG Service**
  - [x] IRagService interface
  - [x] RagService implementation
  - [x] Similarity search (find similar events)
  - [x] Cross-reference detection — **heuristic**, four relationship types (Temporal, Participatory via shared people, Locational via shared locations, Thematic via category/tags), persisted through `ICrossReferenceRepository`
  - [x] Pattern detection (categories, clusters, transitions)
  - [x] Tag suggestions based on similarity (with a one-click Add on the Connections page; 2026-10)
  - [ ] ~~LLM-powered relationship analysis~~ — not implemented; cross-references are heuristic and involve no LLM call

- [x] **Data Layer**
  - [x] EventEmbedding entity
  - [x] CrossReference entity
  - [x] EventEmbeddingRepository
  - [x] CrossReferenceRepository
  - [x] Vector storage as JSON

- [x] **ConnectionsPage UI**
  - [x] ConnectionsViewModel with observable collections
  - [x] Three-section layout (Similar Events, Cross-References, Tag Suggestions)
  - [x] Similar events list with similarity scores
  - [x] Cross-references with relationship type icons
  - [x] Tag suggestions with confidence
  - [x] Refresh command
  - [x] Generate Embeddings batch workflow
  - [x] Navigate to event command

- [x] **Embedding Generation Workflow**
  - [x] Automatic embedding on event creation (fire-and-forget)
  - [x] Manual embedding generation per event
  - [x] Batch embedding generation for all events
  - [x] Progress reporting

- [x] **Navigation Integration**
  - [x] ConnectionsPage registered in navigation
  - [x] Navigation menu item with icon
  - [x] Deep linking with eventId parameter
  - [x] ViewModel and Page registered in DI

**Completion Date:** 2025-11-24
**Verification:** consolidated into `DEVELOPMENT-HISTORY.md` (the original per-phase reports are no longer in the tree).

---

## Phase 6: Polish & Windows Integration - ✅ COMPLETED

### Summary
Added export/import functionality, Windows 11 integration features (notifications, JumpList; the Windows Timeline integration built here was later removed as dead code), and comprehensive UI polish.

### Completed Items

- [x] **Export Service**
  - [x] IExportService interface
  - [x] ExportService implementation
  - [x] Export to JSON (with date filtering)
  - [x] Export to CSV (with proper escaping)
  - [x] Export to Markdown (grouped by year)
  - [x] Full database export
  - [x] Progress reporting (IProgress<int>)

- [x] **Import Service**
  - [x] IImportService interface
  - [x] ImportService implementation
  - [x] Import from JSON (with validation)
  - [x] Duplicate detection (by title + date)
  - [x] Conflict resolution options
  - [x] Automatic backup creation
  - [x] ImportResult with detailed statistics
  - [x] ValidationResult for file validation
  - [x] Progress reporting with messages

- [x] **Windows Notification Service**
  - [x] INotificationService interface
  - [x] NotificationService implementation
  - [x] Toast notifications (simple)
  - [x] Action notifications with callbacks
  - [x] Success/Error styled notifications
  - [x] Processing complete notifications
  - [x] AppNotificationManager integration

- [x] **Windows JumpList Service**
  - [x] IJumpListService interface
  - [x] JumpListService implementation
  - [x] Recent events (last 10 created/opened — fed from the messenger since 2026-10)
  - [x] Quick actions (New Event, Start Recording, View Timeline, Search — since 2026-10 the first two open the add-event dialog and start capturing instead of only navigating)
  - [ ] Works on an unpackaged install — `Windows.UI.StartScreen.JumpList` needs package identity; the service logs the first failure once and no-ops thereafter
  - [x] Deep linking with arguments
  - [x] Group-based organization
  - [x] Duplicate prevention

- ~~**Windows Timeline Service**~~ — **removed 2026-10.** `IWindowsTimelineService` /
  `WindowsTimelineService` (User Activity API + Adaptive Cards, `memory-timeline://event/{id}`
  deep links) were registered in DI but never called by anything, the User Activity channel
  needs package identity this unpackaged app does not have, the protocol handler existed only
  in an unused packaging manifest, and Windows 11 dropped the Timeline UI. The port plan (§4.4)
  also calls for dropping it. Deep links into events now exist only as the Jump List's
  `event:{id}` launch arguments and toast activations.

- [x] **Settings UI Enhancement**
  - [x] Export/Import section added to SettingsPage
  - [x] Export buttons (JSON, CSV, Markdown)
  - [x] Import button with file picker
  - [x] Progress indicators (ProgressBar, ProgressRing)
  - [x] Status message displays
  - [x] FileSavePicker/FileOpenPicker integration
  - [x] Button state management during operations

- [x] **SettingsViewModel Enhancement**
  - [x] ExportToJsonCommand
  - [x] ExportToCsvCommand
  - [x] ExportToMarkdownCommand
  - [x] ImportFromJsonCommand
  - [x] Progress properties (IsExporting, IsImporting, ExportProgress)
  - [x] Status message properties
  - [x] Window handle initialization for pickers

**Completion Date:** 2025-11-25
**Verification:** consolidated into `DEVELOPMENT-HISTORY.md` (the original per-phase reports are no longer in the tree).

---

## Phase 7: Testing & Deployment - 🔄 IN PROGRESS

### Status: In Progress
**Current Focus:** End-to-end runtime validation of the post-audit hardening fixes,
followed by comprehensive automated testing, performance validation, packaging, and
store submission.

**Done so far in Phase 7:**
- Multi-agent feature audit + fix pass (see "Post-audit hardening (2026-07)" above and
  `FEATURE-AUDIT.md`) — resolved the three reported user bugs at the code level and
  removed the shared-context foundation defect.
- CI workflow `.github/workflows/windows-native-build.yml` added: builds `Release|x64`
  on `windows-latest` and runs the test suite. **The solution builds green in CI.**

**Still ahead:** end-to-end runtime validation on real hardware; regenerating the EF
migration baseline; performance validation; MSIX packaging + code signing; Microsoft Store
submission. (The tests now gate CI; see 7.1 for what exists.)

### Objectives

1. **Comprehensive Testing**
   - Unit tests for all services
   - Integration tests for database operations
   - UI tests with WinAppDriver
   - Performance tests with large datasets

2. **Performance Validation**
   - Timeline rendering at 60 FPS with 5000+ events
   - Memory usage < 100 MB for 5000 events
   - Cold start time < 2 seconds
   - Database query performance < 50ms

3. **MSIX Packaging**
   - Package manifest configuration
   - App icons and assets (all sizes)
   - Code signing with EV certificate
   - Dependencies bundling

4. **Microsoft Store Submission**
   - Store listing preparation
   - Screenshots (1920×1080, 2560×1440)
   - App description and keywords
   - Privacy policy
   - Age ratings

### Pending Items

#### 7.1 Testing

*Status 2026-10:* `MemoryTimeline.Tests` (xUnit; unit, integration and performance suites)
runs in CI with `dotnet vstest` and **gates** the workflow; the plain-`net10.0`
`MemoryTimeline.Portability.Tests` runs with `dotnet test` on the macOS workflow.

- [ ] **Unit Tests**
  - [x] EventService tests (CRUD operations) — `Services/EventServiceTests.cs`
  - [x] SettingsService tests — `Services/SettingsServiceTests.cs`
  - [ ] AudioService tests — the WinRT `MediaCapture` recorder has no test seam; only the pure `AudioLevelMeter` math is covered
  - [x] LlmService tests (with mocked API) — `UnitTests/OpenAiCompatibleLlmServiceTests.cs`, `RoutingServicesTests.cs`
  - [x] RagService tests — `UnitTests/RagServiceTests.cs`, `RagDimensionGuardTests.cs`, `ReEmbedAllAsyncTests.cs`
  - [x] ExportService tests — `UnitTests/ExportServiceTests.cs`, `NarrativeExportTests.cs`
  - [x] ImportService tests — `UnitTests/ImportServiceTests.cs`
  - [x] Repository tests — `Integration/DatabaseIntegrationTests.cs`, `UnitTests/SyncOutboxRepositoryTests.cs`, the schema tests
  - [ ] Target: > 80% code coverage (no coverage collection is configured)

- [ ] **Integration Tests**
  - [x] Database integration tests — `Integration/*`
  - [ ] API integration tests (LLM, STT, Embeddings) — provider routing is covered with mocked HTTP only; no live-API or Whisper tests
  - [x] File system tests (audio files, exports) — `BackupServiceTests`, `ExportServiceTests`, `MediaServiceTests`, `MediaCleanupTests`
  - [ ] Windows API integration tests

- [ ] **UI Tests**
  - [ ] WinAppDriver setup
  - [ ] Timeline navigation tests
  - [ ] Event CRUD workflow tests
  - [ ] Audio recording workflow tests
  - [ ] Review workflow tests
  - [ ] Settings tests
  - [ ] Accessibility tests (keyboard, screen reader)

- [ ] **Performance Tests**
  - [x] Load testing with 10/100/1000/5000/10000 events — `Performance/PerformanceTests.cs` (gross-regression bounds)
  - [ ] Memory profiling (Visual Studio Profiler)
  - [ ] Render performance benchmarks
  - [x] Database query performance tests — `Performance/PerformanceTests.cs`
  - [ ] Embedding generation performance tests
  - [ ] Timeline FPS monitoring

#### 7.2 Packaging

> **Honest state (2026-10):** the app ships **unpackaged** (`WindowsPackageType=None`,
> self-contained Windows App SDK) and no MSIX identity exists. The only manifest is
> `src/MemoryTimeline/Package.appxmanifest`, which still carries the template
> placeholders (`Publisher="CN=Your Name"`, a dummy `PhoneIdentity` GUID) and
> references `Assets\*.png` files that have not been created (`src/MemoryTimeline/Assets/`
> does not exist; the csproj's `Assets\**` glob matches nothing). A second, divergent
> manifest under `windows-native/packaging/` (different publisher, MinVersion 19041, and
> protocol / file-type / share-target extensions nothing in the app handled) was deleted
> in 2026-10, as was the csproj's reference to a `win-$(Platform).pubxml` publish profile
> that was never added. Filling in a real publisher identity, producing the icon assets,
> and signing are still to do — nothing below has been started.

- [ ] **MSIX Package**
  - [ ] Configure Package.appxmanifest
    - [ ] Identity (name, publisher, version)
    - [ ] Display name and description
    - [ ] Logos (Square44x44, Square150x150, Wide310x150, StoreLogo)
    - [ ] Capabilities (internetClient, microphone, runFullTrust)
    - [ ] Target device families
  - [ ] Create all icon assets
    - [ ] Square44x44Logo.png (list icon)
    - [ ] Square150x150Logo.png (medium tile)
    - [ ] Wide310x150Logo.png (wide tile)
    - [ ] StoreLogo.png (50x50)
    - [ ] SplashScreen.png (620x300)
  - [ ] Privacy policy document
  - [ ] License agreement
  - [ ] Package build configuration

- [ ] **Code Signing**
  - [ ] Obtain EV Code Signing Certificate
  - [ ] Configure SignTool
  - [ ] Test signing process
  - [ ] Automate signing in CI/CD

#### 7.3 Deployment
- [ ] **Microsoft Store**
  - [ ] Create Partner Center account ($19)
  - [ ] Prepare store listing
    - [ ] App title: "Memory Timeline - AI-Powered Personal History"
    - [ ] Short description (< 200 chars)
    - [ ] Full description with features
    - [ ] Keywords (max 7)
    - [ ] Category selection
  - [ ] Create screenshots
    - [ ] Timeline view
    - [ ] Audio recording
    - [ ] Event review
    - [ ] Connections/RAG
    - [ ] Settings
  - [ ] Age rating questionnaire
  - [ ] Submit for certification

- [ ] **Side-loading Option**
  - [ ] Create standalone MSIX installer
  - [ ] PowerShell install script
  - [ ] Installation instructions document
  - [ ] Troubleshooting guide

- [ ] **Auto-Updates**
  - [ ] Microsoft Store auto-update (if Store)
  - [ ] Or custom UpdateService implementation
  - [ ] Version check mechanism
  - [ ] Update notification UI

#### 7.4 Documentation
- [ ] **User Manual**
  - [ ] Getting started guide
  - [ ] Feature walkthroughs
  - [ ] Keyboard shortcuts reference
  - [ ] FAQ
  - [ ] Troubleshooting

- [ ] **Developer Guide**
  - [ ] Architecture documentation
  - [ ] API reference
  - [ ] Contributing guidelines
  - [ ] Build instructions

- [ ] **Deployment Guide**
  - [ ] System requirements
  - [ ] Installation instructions
  - [ ] Configuration options
  - [ ] Backup and restore procedures

### Timeline

No dates are scheduled. Phase 7 is being worked incrementally — the 2026-07 audit/fix
pass, the 2026-08 feature wave, and the 2026-10 head wiring pass have all landed — and
what remains (runtime validation on real hardware, MSIX identity/assets/signing, Store
submission) has neither a start nor a target date.

---

## Technology Stack Summary

| Component | Technology | Version | Status |
|-----------|-----------|---------|--------|
| **UI Framework** | WinUI 3 + XAML | Windows App SDK 2.3.1 | ✅ |
| **Runtime** | .NET 10 (LTS) | SDK 10.0.3xx+ (global.json) | ✅ |
| **Database** | SQLite + EF Core | EF Core 10.0.10 | ✅ |
| **MVVM** | CommunityToolkit.Mvvm | 8.2.2 | ✅ |
| **Audio** | Windows.Media.Capture | Windows SDK | ✅ |
| **STT** | Local Whisper (Whisper.net, ggml base) | offline after first model download | ✅ |
| **LLM** | Anthropic Claude API **or** OpenAI-compatible endpoint (Ollama / LM Studio), routed per call | claude-sonnet-4-20250514 default | ✅ |
| **Embeddings** | Local ONNX `all-MiniLM-L6-v2` (default, 384-dim, CPU EP) **or** OpenAI API | text-embedding-3-small (1536-dim) as the cloud option | ✅ |
| **Media metadata** | MetadataExtractor (EXIF) | 2.8.1 | ✅ |
| **Geocoding** | Nominatim (opt-in, off by default) | HTTP typed client | ✅ |
| **Navigation** | Frame-based | WinUI 3 | ✅ |
| **Theming** | Light/Dark mode | WinUI 3 | ✅ |
| **Notifications** | AppNotifications | Windows App SDK | ✅ |
| **JumpList** | Windows.UI.StartScreen | UWP API — needs package identity; no-ops (logged once) on an unpackaged install | ✅ |
| ~~**Timeline**~~ | ~~UserActivities~~ | removed 2026-10 (dead code; Windows 11 dropped the Timeline UI) | — |

---

## Code Statistics

### Overall Project (after the 2026-08 feature wave)
- **Total Projects:** 7 (Main App, Core, Data, Sync, Tests, Portability.Tests, plus the shared SyncContracts project referenced from `shared-contracts/dotnet`)
- **Total Code Files:** 250+ (`.cs`/`.xaml`, excluding build output)
- **Total Lines of Code:** ~70,000+ (including XAML and tests)
- **Data Models:** 20
- **Core Service Interfaces:** 30
- **Repositories:** 15
- **ViewModels:** 14
- **Views (XAML Pages):** 12
- **Test Files:** unit/integration/performance suites in `MemoryTimeline.Tests` (run in CI; coverage targets still Phase 7)

### By Phase
- **Phase 0:** ~1,500 lines (scaffolding, configuration)
- **Phase 1:** ~2,500 lines (database, services, DI)
- **Phase 2:** ~2,000 lines (timeline UI, controls)
- **Phase 3:** ~1,800 lines (audio, queue, STT)
- **Phase 4:** ~2,200 lines (LLM, extraction, review)
- **Phase 5:** ~2,000 lines (RAG, embeddings, connections)
- **Phase 6:** ~2,500 lines (export, import, Windows integration, polish)
- **Phase 7:** Pending (tests)

---

## Known Issues & Limitations

### Current Limitations
1. **Runtime validation:** The post-audit fixes *and* the entire F1–F12 feature wave
   build green in CI but are not yet validated end-to-end on real hardware (Phase 7,
   in progress)
2. **Automated tests:** unit/integration/performance suites exist and gate CI; AudioService
   tests, UI tests (WinAppDriver) and coverage reporting are still absent (Phase 7)
3. **EF migrations:** Stale migrations deleted; a real migration baseline still needs
   regenerating — all 2026-08 schema additions ship via `SchemaUpgrader.EnsureSchemaAsync`
4. **API keys:** DPAPI-protected at rest through `ISecretProtector` (CurrentUser scope); a backup restored under another Windows account must have its keys re-entered
5. **Performance:** Not yet validated with 5000+ events (Phase 7)
6. **MSIX Package / Store Submission:** Not created or submitted yet (Phase 7)
7. **ONNX acceleration:** The local embedder runs on the CPU execution provider;
   DirectML/NPU registration is deliberately deferred (can throw on non-DX12 machines)
8. **PDF Export:** Not implemented — narrative export ships Markdown/HTML only
9. **Deferred F1–F12 items:** See the deferred list in the 2026-08 section above
   (photo-import wizard, tile basemaps, token streaming, Merge restore mode, etc.)

### Future Enhancements
1. **DirectML/NPU execution** for the local ONNX embedder (currently CPU)
2. **Advanced Timeline Features:** Filtering, advanced search from timeline
3. **Pattern Visualization:** Charts and graphs for detected patterns
4. **More Embedding Models:** Voyage AI, Cohere, alternative local ONNX models
   (local `all-MiniLM-L6-v2` and OpenAI are implemented)
5. **Performance Optimization:** Caching, lazy loading, pagination for very large datasets
6. **PDF Export:** Using WinUI 3 printing APIs
7. **Cloud Sync:** OneDrive/Dropbox integration (optional)
8. **Collaborative Features:** Shared timelines, multi-user support

---

## Success Criteria

### Completed ✅
- [x] Full feature parity across the core features
- [x] All 6 phases (0-6) complete
- [x] Clean architecture maintained
- [x] MVVM pattern throughout
- [x] Full dependency injection
- [x] Windows-exclusive features (Notifications, JumpList)
- [x] Export/Import functionality
- [x] RAG capabilities (embeddings, cross-references, patterns)

### Phase 7 Targets ⬜
- [ ] > 80% code coverage
- [ ] 60 FPS timeline rendering with 5000+ events
- [ ] < 100 MB memory usage for 5000 events
- [ ] < 2 second cold start time
- [ ] Zero critical bugs
- [ ] MSIX package created and signed
- [ ] Microsoft Store submission completed
- [ ] User documentation complete

---

## Next Steps (Immediate Priorities)

These reflect the real post-audit follow-ups (see `HARDENING-FOLLOWUPS.md`).

1. **End-to-end runtime validation**
   - Run the app on real Windows hardware and exercise each fixed flow
     (add event → search → refresh; record → local Whisper STT → LLM extract →
     approve → timeline; embeddings/connections; export/import; settings persistence).
   - Confirm the three reported bugs are gone at runtime, not just in code.

2. **Data-layer / migrations**
   - Regenerate a real EF migration baseline (stale migrations were deleted; startup
     currently uses `SchemaUpgrader.EnsureSchemaAsync`).

3. **Security & config hardening**
   - ~~Encrypt API keys at rest via DPAPI / `ProtectedData`~~ — done (`ISecretProtector` / `DpapiSecretProtector`).
   - ~~Add Whisper model/language options~~ — done (`whisper_model` / `whisper_language`, chosen in
     Settings and read per transcription). Analytics export is done.

4. **Testing & performance baseline**
   - The xUnit structure, SQLite test factory and EventService tests exist and gate CI;
     still to do: AudioService tests, UI tests (WinAppDriver), coverage reporting.
   - Generate test datasets (100/1000/5000 events) and measure baseline metrics.

5. **MSIX & Store**
   - Design app icons/assets, write privacy policy, configure Package.appxmanifest,
     code-sign, and prepare the Microsoft Store submission.

---

## Documentation References

- **Windows README:** `README.md` (this folder) — overview, setup, tech stack
- **AI-assistant / contributor guide:** `../claude.md`
- **Feature audit (2026-07):** `FEATURE-AUDIT.md`; **deferred hardening items:** `HARDENING-FOLLOWUPS.md`
- **Exhaustive repo audit (2026-07):** `../docs/reviews/exhaustive-audit-2026-07.md`
- **Deployment Guide:** `DEPLOYMENT.md`
- **Testing Guide:** `TESTING.md`
- **Setup scripts:** `scripts/README.md`
- **Phase Reports:** consolidated into `DEVELOPMENT-HISTORY.md` (the original
  `PHASE*-REPORT.md` files, `MIGRATION-TO-NATIVE-WIN.md` and `README-WINDOWS.md` are no
  longer in the tree)
- **Design docs:** `../docs/design/` (macOS port plan, iOS companion system design, people feature contracts)

---

**Document Owner:** Development Team
**Phase 7 Status:** In progress (CI Windows build + tests green through the F1–F12 wave and the 2026-10 head wiring pass; runtime validation ongoing)
**Next Review:** After end-to-end runtime validation
**Last Updated:** 2026-10-21

<!-- CI retrigger 2026-08-06: runs 31117811806/31119588286/31121424093/31124292681 died (pushes a4f25ca/b9f4276 produced no runs at all — Actions event pipeline outage) to runner/Actions-service infrastructure failures before reaching the code; tree identical to 0d365b2. -->
