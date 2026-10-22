# Windows-Native Feature Implementation Audit

**Date:** 2026-07-09
**Method:** Five specialized audit agents traced every major feature end-to-end
(UI control → command → ViewModel → service → repository → SQLite → back to UI),
read-only, with file:line evidence. Findings are tagged **CONFIRMED** (proven
from code) or **PROBABLE** (depends on runtime state we can't observe).

**Trigger:** three user-reported live bugs —
(1) pressing **Save** on Add Event does nothing, (2) searching for the added
event shows **"search error"**, (3) the timeline **Refresh** button doesn't
bring up the new event.

> ### Status (2026-10-21)
> This document is the **original audit**, kept as written for its root-cause analysis.
> Almost everything it asked for has since landed; the per-section `Status` notes below
> say what. In short:
>
> | Area | State |
> |------|-------|
> | Bugs 1 / 2 / 3 | ✅ fixed (category case + in-dialog errors; `IDbContextFactory` per-operation contexts + debounced search; Ctrl+R refreshes in place) |
> | Part 1 foundation | ✅ `AddDbContextFactory` everywhere, Singleton services; `SchemaUpgrader` replaced raw `EnsureCreated` and, since 2026-10-22, applies the EF migration baseline (`MigrateAsync`) |
> | 2.3 Search | ✅ `Tag.Name` → `TagName`, suggestions bound, sort default aligned, honest error messages, Save Search dialog wired |
> | 2.4 Audio pipeline | ✅ storage path fixed, local Whisper STT (the mic-only stub was deleted 2026-10), transcript persisted, atomic approve, Resume/Cancel, API-key pre-flight, LLM config re-read per call, per-item progress events; the dead `OnnxWhisperService` and `AnthropicClaudeService` were removed |
> | 2.5 RAG / Connections | ✅ OpenAI key UI (plus local ONNX default), Connections navigation with the event id, mapped embedding columns, persisted cross-references; cross-references are **heuristic** (not LLM-powered) |
> | 2.6 Analytics | ✅ export implemented |
> | 2.7 Settings | ✅ `SettingKeys` constants (writer and reader), display-case normalization, live config re-read; Reset to Defaults and key removal fixed 2026-10 |
> | 2.8 Export / Import | ✅ `Include` fix; import options honored |
> | 2.9 Shell / Windows integration | ✅ unified `INotificationService`, `AddQuickActionsAsync` at launch, activation handling, `await _host.StopAsync()`; **2026-10:** `MainViewModel` + nav badges implemented, Jump List recent events + real quick actions wired, Windows Timeline service removed as dead code |
>
> Nothing from the plan is still open: the EF migration baseline landed on 2026-10-22 and startup
> migrates (see `HARDENING-FOLLOWUPS.md` §3).

---

## Part 0 — Root causes of the three reported bugs

### Bug 1 — "Save does nothing" · ROOT CAUSE CONFIRMED
A **category string case mismatch**, made invisible by swallowed errors:

1. The Add Event dialog opens with **no category selected**
   (`TimelineControl.xaml.cs:721` sets `SelectedIndex = -1`; the field looks optional).
2. On Save, the handler falls back to the literal `"Other"` — **capital O**
   (`TimelineControl.xaml.cs:825-829`; the XAML items carry lowercase tags).
3. `EventService.ValidateEvent` does a **case-sensitive** check against
   `EventCategory.AllCategories`, which are all **lowercase** (`"other"`)
   (`IEventService.cs:571-575` vs `Event.cs:77-91`) → throws
   `ArgumentException: Invalid category: Other` **before any DB write**.
4. The exception is caught in `TimelineViewModel.CreateEventAsync:763-767` and
   reduced to a log line + a small `StatusText` string in the CommandBar — and
   the dialog has already closed because the Save handler takes **no dialog
   deferral** (`TimelineControl.xaml.cs:809`). Net effect: Save appears to do
   nothing, and no row exists.

Secondary, intermittent contributor when a category *is* picked: the
fire-and-forget embedding task (`IEventService.cs:98`) races the post-save
reload on the app-wide shared `DbContext` (see Part 1), which can surface as a
swallowed "Error creating event" **after** a successful insert.

### Bug 2 — "search error" · ROOT CAUSE CONFIRMED
**DbContext lifetime/concurrency, not a query bug.** The whole app shares one
root-scoped `AppDbContext` (Part 1). Adding an event arms a fire-and-forget
`Task.Run` that uses that same context from a thread-pool thread
(`IEventService.cs:98` → settings read + `SaveChangesAsync` at `:608-609`);
meanwhile **every keystroke** in the search box fires un-debounced,
un-cancelled autocomplete queries on it too (`SearchViewModel.cs:25-28` → 5
sequential EF queries each, `IAdvancedSearchService.cs:312-376`). When the user
presses Enter, `CountAsync`/`ToListAsync` collide with an in-flight operation →
`InvalidOperationException: "A second operation was started on this context"` →
caught at `SearchViewModel.cs:225-229` → generic `"Search error"`. The main
search query itself is fully SQLite-translatable. This reproduces *right after
adding an event* — exactly the user's sequence.

### Bug 3 — "Refresh does nothing" · CONFIRMED DOWNSTREAM OF BUG 1
The refresh pipeline is **mechanically sound**: `RefreshCommand` is correctly
x:Bind-wired (`TimelineControl.xaml:399-404`), and
`EventRepository.GetByDateRangeAsync` is `AsNoTracking()` — it genuinely
re-reads SQLite each time (`EventRepository.cs:103-111`). It shows nothing
because **the insert never happened**. Two real secondary traps: an event dated
outside `viewport ± 30 days` will never appear on refresh
(`ITimelineService.cs:52-54`), and the Ctrl+R accelerator re-navigates the
Frame instead of refreshing, which no-ops when window dimensions are unchanged
(`MainWindow.xaml.cs:132-140` → `TimelineViewModel.cs:704-723`).

---

## Part 1 — The foundation defect underneath everything (CONFIRMED by all 5 agents)

> **Status:** ✅ resolved — `App.xaml.cs` registers `AddDbContextFactory<AppDbContext>()`, every repository/service opens a context per operation and is a Singleton, and the shared-context `Task.Run` is gone. `EnsureCreated` was replaced by `SchemaUpgrader.EnsureSchemaAsync`, which now applies the regenerated EF migration baseline (`20261022004346_InitialCreate`) with `MigrateAsync`.

### One shared, never-disposed `AppDbContext` for the entire app
- `App.xaml.cs:80` registers `AddDbContext<AppDbContext>()` (**Scoped**), but
  **every page resolves its ViewModel from the root provider**
  (`App.Current.Services.GetRequiredService<…>` in all 8 pages' constructors),
  and the only `CreateScope()` in the product is the startup EnsureCreated
  block (`App.xaml.cs:237`). Scoped services resolved from the root are cached
  in the root scope → **one `AppDbContext` instance for the app's lifetime**,
  shared by every feature.
- Singletons capture it too: `SettingsService` (Singleton, `App.xaml.cs:97`)
  directly injects `AppDbContext` (`ISettingsService.cs:40`);
  `TimelineViewModel` (Singleton, `App.xaml.cs:133`) holds root-scoped
  `ITimelineService`/`IEventService`.
- `Host.CreateDefaultBuilder` validates scopes **only in Development**; an
  unpackaged double-clicked exe runs as Production → no startup crash, just
  silent sharing. (If anyone sets `DOTNET_ENVIRONMENT=Development`, the app
  instead **crashes at `_host.Build()`** on the singleton→scoped chain.)

**Consequences:** (a) `DbContext` is not thread-safe — any two overlapping
operations anywhere throw `InvalidOperationException` ("second operation"),
surfaced as search errors, silent save failures, blank analytics sections;
(b) the change tracker grows for the app's lifetime; (c) a failed
`SaveChangesAsync` leaves poisoned tracked entities that can re-fail every
subsequent save of any feature until restart.

**How it should be implemented:** a desktop app has no per-request scope, so
`AddDbContext` (Scoped) is the wrong primitive. Standard pattern:
`AddDbContextFactory<AppDbContext>()`, and every service/repository creates a
short-lived context per operation (`await using var db = await
factory.CreateDbContextAsync()`). Alternatively, create an `IServiceScope` per
page navigation and resolve VMs from it. Background work (embeddings, queue
processing) must create its **own** scope/context — never a bare `Task.Run`
over a captured context.

**Fix (do this first — it unblocks every feature):**
1. Replace `AddDbContext` with `AddDbContextFactory<AppDbContext>`.
2. Convert repositories + `SettingsService`, `EventService`,
   `AdvancedSearchService`, `AnalyticsService`, `QueueService`,
   `EventExtractionService`, `ExportService`, `ImportService`, `RagService` to
   consume the factory (context per method).
3. `GenerateEmbeddingForEventAsync` gets its own scope/context (via
   `IServiceScopeFactory`) — or the `Task.Run` is removed entirely.
4. Make `QueueService` an intentional Singleton (its semaphore/events only work
   today by accident of root-scope caching).
5. Enable scope validation in debug builds to prevent regression.

### Database lifecycle: `EnsureCreatedAsync` + stale migrations (CONFIRMED)
`App.xaml.cs:241` uses `EnsureCreatedAsync()`; the checked-in migration
(`Migrations/20250121000000_InitialCreate.cs`) is badly stale — **no
`era_categories`, `milestones`, `era_tags`, or `saved_searches` tables** and
missing `eras`/`events` columns. `EnsureCreated` is a **no-op when the DB file
already exists**, so any database created by an earlier build keeps its old
schema → runtime `SqliteException: no such table/column`, reported only as
generic "Error loading…" status text — indistinguishable from the concurrency
failures. **Fix:** regenerate the migration, switch to `MigrateAsync()`, and
bridge EnsureCreated-born DBs (insert baseline history row, then migrate).

---

## Part 2 — Per-feature audit

### 2.1 Event creation / editing  — BROKEN (Bug 1)

> **Status:** ✅ fixed — lowercase/case-insensitive category validation, dialog deferral with an in-dialog InfoBar, embedding generation rescoped, timeline navigates to the new event's date (`EventCreatedMessage`).
**Should be:** dialog Save takes `args.GetDeferral()`, awaits the VM call, and
on failure cancels the close and shows the error in-dialog; validation shares
one source of truth with the UI (bind the ComboBox to
`EventCategory.AllCategories` instead of duplicating string literals);
category comparison case-insensitive; embeddings generated in their own scope.

**Wrong now:** capital-`"Other"` fallback vs lowercase validation
(CRITICAL, CONFIRMED — Bug 1); silent error channel (CRITICAL, CONFIRMED);
fire-and-forget embedding race (HIGH, CONFIRMED defect); empty-title/no-date
validation cancels the dialog with zero feedback; the Edit path silently fails
the same way if a stored category isn't in the combo list.

**Must do:** (1) lowercase/normalize the category fallback and make
`ValidateEvent` case-insensitive; (2) dialog deferral + in-dialog error
(InfoBar); (3) fix or remove the embedding `Task.Run`; (4) after a successful
create, navigate the viewport to the event's date if it isn't visible.

### 2.2 Timeline render / refresh — SOUND, with traps

> **Status:** ✅ fixed — refresh errors surface in the timeline InfoBar, Ctrl+R invokes `RefreshCommand` in place, and `EventCreated/Updated/Deleted` messages keep the singleton ViewModel current.
Render pipeline and refresh are correct. **Must do:** surface refresh errors
visibly; make Ctrl+R invoke `RefreshCommand` instead of re-navigating; add an
event-created message (`WeakReferenceMessenger`) so approve/create flows
refresh the singleton `TimelineViewModel` without renavigation (today an
approved event only appears after a pan/zoom/resize —
`TimelineControl.xaml.cs:86-90`, `TimelineViewModel.cs:699-724`).

### 2.3 Search — BROKEN error path + degraded features (Bug 2)

> **Status:** ✅ fixed — context factory + debounced/cancellable autocomplete, `Tag.Name` → `TagName`, suggestions bound to the AutoSuggestBox, sort default aligned, `Search error: {message}`.
**Should be:** context-per-operation; debounced (~300 ms) input with a
`CancellationToken` per search that cancels the previous one; only mapped
columns in predicates; errors surfaced with the message.

**Wrong now (beyond the Bug 2 concurrency root cause):**
- `[NotMapped] Tag.Name` used in EF queries (`IAdvancedSearchService.cs:236,
  328`) — untranslatable → tag facets **always throw**, which aborts
  people/location/era facets and counts; swallowed silently (CONFIRMED).
- The AutoSuggestBox never binds `AutocompleteSuggestions`
  (`SearchPage.xaml:32-40`) — suggestions are computed per keystroke (cost) but
  never shown (dead UI) (CONFIRMED).
- Sort default mismatch XAML `created_desc` vs VM `date_desc`; facets re-run
  ~10 queries every search (CONFIRMED, minor).
- If the DB file predates the current model, `saved_searches` doesn't exist →
  save-search errors (PROBABLE, depends on user's DB).

**Must do:** (1) fix Bug 2 via the DbContext factory + remove/debounce the
per-keystroke autocomplete; (2) `Tag.Name` → `TagName`; (3) wire or remove the
suggestions UI; (4) `StatusMessage = $"Search error: {ex.Message}"`.

### 2.4 Audio pipeline (record → STT → LLM → review) — NOT FUNCTIONAL END-TO-END

> **Status:** ✅ fixed — storage path under `%LOCALAPPDATA%`, local file-based Whisper STT (the live-mic stub was reduced to fail-fast and then deleted in 2026-10), transcripts persisted on the queue row, API-key pre-flight as a non-retryable `ConfigurationException`, atomic metadata-complete approve, `EventCreatedMessage` refresh, state re-attached from `GetRecordingState()`, Resume/Cancel buttons, `ProcessingProgressChanged` consumed, `AnthropicClaudeService` removed, LLM config re-read per call (`RoutingLlmService`). 2026-10 added a live input-level meter, a microphone picker, and audio file import.
Four breaks in series (all CONFIRMED):
1. **Recording cannot start in the checked-in configuration.**
   `WindowsPackageType=None` (unpackaged) + `ApplicationData.Current`
   (`AudioRecordingService.cs:329`, `IAudioImportService.cs:348`) which
   **requires package identity** → throws before any file is created →
   "Error starting recording" every time.
2. **STT never transcribes the file.** `WindowsSpeechRecognitionService.cs:53`
   loads the WAV into a variable that is **never used**, then line 68 calls
   `SpeechRecognizer.RecognizeAsync()` — which listens to the **live
   microphone** for one utterance. "Processing" a memory transcribes ambient
   room audio, up to 3× per item via the retry loop. File-based STT does not
   exist in the codebase (`OnnxWhisperService` is an unregistered TODO stub).
3. **Transcript never persisted** — lives only in memory between STT and LLM
   (`PendingEvent.Transcript`/`AudioFilePath` never assigned,
   `EventExtractionService.cs:110-123`).
4. **Approve loses data and isn't transactional** — only
   title/description/dates/category copied; tags/people/locations/confidence
   die in the `ExtractedData` blob; two separate `SaveChanges` mean a failure
   in between duplicates events on re-approve
   (`EventExtractionService.cs:147-188`). Approved events don't appear on the
   timeline until pan/zoom/resize.

Also: pause is a one-way trap (no Resume command; state collapses to
`IsRecording=false`, leaving a hot mic until app exit); navigating away orphans
an in-flight recording (`GetRecordingState()` has zero callers); no API-key
pre-flight (missing key burns 3 retries per item, re-opening the mic each
time); `AnthropicLlmService` caches the key/model forever after first init;
per-item progress UI dead (`ProcessingProgressChanged` has no subscribers);
`AnthropicClaudeService` is a complete second, unregistered `ILlmService`
reading a *different* settings key.

**Must do (order):** storage path → `Environment.SpecialFolder.LocalApplicationData`
(mirroring `AppDbContext.cs:40-41`); replace STT with a real file-based engine
(whisper.net/ONNX — the 16 kHz mono WAV output is already Whisper-ready;
honest stopgap: fail with "file transcription not supported" instead of
recording room noise); persist the transcript; API-key pre-flight
(non-retryable config error); transactional approve + full metadata mapping;
event-created message → timeline refresh; seed VM state from
`GetRecordingState()` on page load; add Resume/Cancel buttons.

### 2.5 RAG / Connections — CANNOT WORK (four independent blockers, all CONFIRMED)

> **Status:** ✅ fixed — OpenAI key field in Settings (and a local ONNX provider as the keyless default), "View connections" navigation carries the event id, queries use the mapped embedding column, cross-references are persisted via the repository and labelled as **heuristic** (no LLM call), provider/dimension set on save, zero-embedding CTA state. 2026-10: tag suggestions can be applied with one click.
1. **No settings writer exists for `OpenAIApiKey`** — the embedding service
   reads `"OpenAIApiKey"`/`"OpenAIEmbeddingModel"` (`OpenAIEmbeddingService.cs:57-58`)
   but the Settings UI only writes a generic `"ApiKey"` (consumed by Anthropic)
   and the seeds use `embedding_api_key`. **Zero embeddings can ever be
   created**; every failure is swallowed ("Generate Embeddings" is a silent
   no-op that reports success).
2. **No navigation path ever passes an eventId to ConnectionsPage** — it
   permanently renders "No Event Selected".
3. `[NotMapped] EventEmbedding.Embedding` used in EF `Where`
   (`RagService.cs:60, 281`) — throws as soon as embeddings exist.
4. Cross-references are a **stub**: a real (paid) LLM call whose result is
   discarded (`RagService.cs:368`), then hard-coded date/category heuristics
   with fabricated 0.8/0.7 confidences; results never persisted
   (`ICrossReferenceRepository` registered, zero consumers).

**Must do:** add an OpenAI key field in Settings; add "View connections" on
timeline events navigating with the eventId; `Embedding` → mapped
`EmbeddingVector` in queries (and remove the `[NotMapped]` aliases); persist
cross-references via the repository; implement real LLM relationship analysis
or label results "heuristic" and drop the wasted call; set
`EmbeddingProvider`/`Dimension` when saving; add a zero-embedding CTA state.

### 2.6 Analytics — WORKS

> **Status:** ✅ the export button is implemented (no longer a "coming soon" stub); per-section errors surface.
All queries are EF Core 8 / SQLite-translatable and every empty-DB edge
(Max/Min/First/divide) is guarded. Residual: shared-context concurrency can
blank sections silently (per-section catches swallow errors); the Export button
is a "coming soon" stub; `StartDate` fetched 5× per load. Fix opportunistically
after the DbContext factory.

### 2.7 Settings — SAVES DON'T TAKE EFFECT (CONFIRMED)

> **Status:** ✅ fixed — `SettingKeys` constants used by writers and readers, display casing normalized at the ViewModel boundary, the LLM client re-reads its configuration per call. 2026-10: Reset to Defaults uses the seeded values and keys can be removed ("Clear key"); the decorative audio boxes became a fixed-format note plus a microphone picker.
Writer/reader key mismatch: `SettingsViewModel` saves PascalCase
(`"DefaultZoomLevel"`, `"LlmProvider"`, `"LlmModel"` — lines 158-171) while
`SettingsService` and all consumers read snake_case (`"default_zoom_level"`
etc., `ISettingsService.cs:200-213`) matching the DB seeds. Result: settings
visibly "save" then revert; combo boxes load blank (value-casing mismatch
`"month"` vs `"Month"`); the LLM model can never be changed; the API key works
only until first use (singleton client caches it forever). **Must do:** one
`SettingKeys` constants class (snake_case), used by both sides; normalize
display casing at the VM boundary; make `AnthropicLlmService` re-read config on
settings change. Theme is the only setting that fully works.

### 2.8 Export / Import — EXPORT BROKEN AT RUNTIME (CONFIRMED)

> **Status:** ✅ fixed — `.Include(e => e.EventTags).ThenInclude(et => et.Tag)`; import's `CreateBackup`/`SkipDuplicates`/`UpdateExisting` options are honored.
`ExportService.cs:206, 283` do `.Include(e => e.Tags)` — but `Event.Tags` is
`[NotMapped]` → `InvalidOperationException` on the first query → **every
JSON/CSV/Markdown/full export fails**. Correct form:
`.Include(e => e.EventTags).ThenInclude(et => et.Tag)`. Import's
`CreateBackup`/`SkipDuplicates`/`UpdateExisting` options are decorative (never
read). File-picker window-handle initialization is done correctly.

### 2.9 App shell / Windows integration — DEAD WIRING (CONFIRMED)

> **Status:** ✅ fixed — one `INotificationService`, called from queue completion; `AddQuickActionsAsync()` in `OnLaunched`; activation arguments handled (cold start and redirected); `await _host.StopAsync()` before `Dispose()`. **2026-10:** `MainViewModel` implemented with Queue/Review `InfoBadge` counts; Jump List "Recent Events" fed from the messenger and the quick actions perform their actions; the Windows Timeline service was removed rather than wired (no callers, needs package identity, Timeline UI gone from Windows 11). The `AddTransient<Page>` registrations were removed as dead code.
- `MainViewModel` is an empty TODO stub; the nav `InfoBadge`s have no value
  source (dead UI).
- `NavigationService` → `Frame.Navigate(type)` instantiates pages via
  `Activator`, so all `AddTransient<Page>` registrations are dead code.
- Notification/JumpList/WindowsTimeline services are registered but have
  **zero call sites**; there are **two different `INotificationService`
  interfaces** and the one `QueueService` optionally wants is never registered
  (DI passes null) → completion toasts can never fire. `NotificationService`
  has a `Dispose()` but doesn't implement `IDisposable`, so it never runs.
- No activation-argument handling for JumpList/Timeline deep links.

**Must do:** implement or remove `MainViewModel`+badges; unify on one
`INotificationService` and call it from queue completion; call
`JumpListService.AddQuickActionsAsync()` in `OnLaunched`; handle activation
args; `await _host.StopAsync()` before `Dispose()`.

---

## Part 3 — Master fix plan (dependency-ordered)

| # | Fix | Unblocks |
|---|-----|----------|
| 1 | **Category case fix + dialog deferral/error surfacing** (2.1) | Bug 1, Bug 3 — smallest change with the biggest user-visible win |
| 2 | **`AddDbContextFactory` migration + kill shared-context `Task.Run`** (Part 1) | Bug 2, intermittent failures in every feature |
| 3 | **Search hygiene**: debounce/cancel autocomplete, `Tag.Name`→`TagName`, error messages (2.3) | Facets, suggestions, honest errors |
| 4 | **Settings key unification** (`SettingKeys` constants) + LLM config re-read (2.7) | Settings actually working; prerequisite for 5 & 6 |
| 5 | **Migrations**: regenerate, `MigrateAsync()`, EnsureCreated bridge (Part 1) | Upgrade safety; schema-drift errors |
| 6 | **Audio pipeline**: storage path → real file STT → transcript persistence → key pre-flight → transactional approve + metadata → timeline invalidation message (2.4) | The app's core ingestion loop |
| 7 | **RAG**: OpenAI key UI, navigation wiring, mapped-column queries, persistence (2.5) | Connections feature |
| 8 | **Export Include fix**; import options honored (2.8) | Data portability |
| 9 | **Shell**: MainViewModel/badges, notifications, JumpList, host shutdown (2.9) | Polish/integration |

Items 1-3 together fully resolve the three reported bugs. Items 4-6 make the
core product loop real. Items 7-9 finish the feature surface.

---

## Cross-agent convergence (confidence signal)
- The **captive shared DbContext** was independently confirmed by **all five
  agents** with the same evidence chain.
- **Settings key mismatches** were found independently three times
  (PascalCase/snake_case; `OpenAIApiKey` never written; the dead second
  `AnthropicApiKey` in the unregistered `AnthropicClaudeService`).
- **Silent error swallowing into `StatusText`/`StatusMessage`** appears in
  every feature audit — it is the reason all of these defects present as
  "nothing happens" instead of visible errors, and should be treated as a
  design defect in its own right (adopt a visible `InfoBar` pattern app-wide).
