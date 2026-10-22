# Windows-Native Hardening — Follow-ups

This file captures the **deferred** items from the Phase-7 multi-agent hardening
pass. The applied fixes are in the commit that adds this file; the items below
were intentionally **not** auto-fixed because they require a compiler to verify a
non-trivial refactor, a public-API/DI change spanning layers, or a team decision.

> Context: the pass was performed statically (no .NET SDK in the environment).
> **Before release, build `src/MemoryTimeline.sln` with Visual Studio 2026 / `msbuild`
> (`/t:Restore,Build /p:Platform=x64` — not `dotnet build`, which fails on the WinUI PRI
> task) and run the built test assembly with `dotnet vstest`**, as CI does, to validate
> all applied fixes.

> **Status key (updated 2026-10-22):** ✅ resolved · ⬜ still open.

## High priority

### 1. Captive / scope-captured `DbContext`  *(converged flag from 3 agents)* — ✅ resolved
`TimelineViewModel` is registered `AddSingleton` (App.xaml.cs) but depends on
`ITimelineService`/`IEventService` (Scoped) → `AppDbContext` (Scoped). Pages also
resolve their VMs from the **root** provider (`App.Current.Services.GetRequiredService<…>`),
not from a DI scope. Net effect:
- Production: a single `AppDbContext` lives for the whole app lifetime — not
  thread-safe, and the change-tracker grows unbounded.
- Development (host scope validation): throws *"Cannot consume scoped service
  from singleton."*

**Fix (coordinated, needs compile):** register the context via
`AddDbContextFactory<AppDbContext>()` and create a per-operation context in
services, **or** resolve VMs/pages inside an explicit `IServiceScope`. Touches
App DI + every repository/service ctor, so it was not done blind.

**Resolution:** `App.xaml.cs` registers `AddDbContextFactory<AppDbContext>()`; every
repository and service opens a short-lived context per operation
(`await using var ctx = await _contextFactory.CreateDbContextAsync()`) and is registered
Singleton. This is now the data-access rule in `claude.md`.

### 2. `EventService.CreateEventAsync` fire-and-forget embedding  *(Core + Tests agents)* — ✅ resolved
`_ = Task.Run(() => GenerateEmbeddingForEventAsync(createdEvent))` reuses the
request-scoped `AppDbContext` on a background thread → `ObjectDisposedException` /
"second operation on this context" once a real `IEmbeddingService` is registered.
Currently masked by an internal catch (embeddings silently fail).
**Fix:** inject `IServiceScopeFactory`, create a fresh scope+context for the
background work (ctor change → flagged rather than applied).

**Resolution:** the shared-context `Task.Run` went away with the factory rework; the
embedding path opens its own context per operation.

### 3. Migration + snapshot drift  *(Data agent — Critical for migration-based release)* — ✅ baseline regenerated (runtime path unchanged)
`AppDbContextModelSnapshot` is an empty stub and `InitialCreate` is far behind the
model (missing tables `era_categories`, `era_tags`, `milestones`, `saved_searches`;
missing columns on `eras`/`events`/`tags`; missing `cross_references` FKs; missing
seed `InsertData`). Runtime is fine today because startup uses `EnsureCreatedAsync()`
(schema built from the model), but **any migration-based deploy will fail**.
**Fix:** on a machine with the SDK, regenerate the baseline
(`dotnet ef migrations add InitialCreate --force`) before shipping migrations.

**Current state (2026-10-22):** a baseline migration
(`Data/Migrations/20261022004346_InitialCreate` + snapshot; 25 tables, both seeds) was
generated with `dotnet ef` through the new `AppDbContextDesignTimeFactory`. Startup still
uses `SchemaUpgrader.EnsureSchemaAsync` (EnsureCreated + idempotent DDL repairs) and
nothing calls `Migrate()`; moving existing EnsureCreated-built databases onto migrations
still needs a history-table baseline step, which remains open.

## Medium priority

- ✅ (approve path) / ⬜ (elsewhere) **Unit-of-work / atomicity** — multi-repository
  workflows (e.g. approve pending → create Event + delete PendingEvent) each call
  `SaveChangesAsync` independently and are not atomic. Introduce an explicit
  transaction / unit-of-work at the service layer. *Approve is now one atomic
  transaction (event + tags/people/locations + pending-event delete); other multi-step
  workflows still save independently.*
- ✅ **Polly referenced but unused** — `Core/Services/HttpRetryPolicy.cs` (Polly 8
  `ResiliencePipeline`: 3 retries, 500 ms exponential back-off with jitter, 4 s cap;
  retries transport errors, timeouts, 408/429/5xx, never caller cancellation or other
  4xx) wraps the OpenAI-compatible LLM, OpenAI embeddings, Nominatim (no 429 retry)
  and the Anthropic SDK call.
- ✅ **`CrossReference` delete behavior = `Restrict`** — the FK stays `Restrict`;
  `EventRepository.DeleteAsync`/`DeleteRangeAsync` remove the cross-references naming
  the event at either end in the same `SaveChanges`, proven by a file-backed SQLite test
  with `PRAGMA foreign_keys` on.
- ⬜ **Analytics color bindings** — `CategoryDistribution/TagCloudItem/ActivityHeatmapCell.Color`
  are `string` bound directly to `Brush` properties via `x:Bind` in `AnalyticsPage.xaml`.
  Expose a `Brush`/`SolidColorBrush` on the model (as the timeline DTOs do with
  `ColorBrush`) or add a string→Brush converter. *Not re-verified in the 2026-10 pass;
  the project's convention is now hex strings in Core + `HexToBrushConverter` in the view.*

## Low priority

- ✅ **API keys stored plaintext** in `AppSettings` — `SettingsService` stores the
  Anthropic key, `embedding_api_key` and the sync tokens through the `ISecretProtector`
  port (`DpapiSecretProtector` in the head: `ProtectedData`, CurrentUser scope,
  `dpapi:v1:` prefix); legacy plaintext rows are re-written in protected form on first
  read. A backup restored under another Windows account reads those secrets as empty and
  they must be re-entered. Keys can also be removed from Settings ("Clear key" / Reset
  to Defaults).
- ⬜ **`CancellationToken` not plumbable** through `IRepository<T>` and specialized repo
  interfaces — add `CancellationToken ct = default` in a coordinated interface change.
- ⬜ **`DateTime.Kind` round-trip** — SQLite returns `Unspecified`; add a UTC
  `ValueConverter` on `DateTime` properties to avoid local/UTC comparison bugs.
  *Related: `Microsoft.Data.Sqlite.Pre10TimeZoneHandling` is pinned at both entry
  points; auditing the stored values is still open (see DEVELOPMENT-STATUS).*
- ⬜ **Inclusive viewport boundaries** — `EventLayoutEngine.GetVisibleLayouts` /
  `TimelineViewport.IsEventVisible` treat edge-touching (zero-area) events as visible
  on both ends. Internally consistent (tests aligned to it); confirm it's intended.
- ✅ **Singleton `AnthropicLlmService` caches `_model`** at first init — a later model
  change in settings needs an app restart. *Resolved: `RoutingLlmService` is the
  registered `ILlmService` and re-reads provider/model/key on every call, so Settings
  changes apply live.*
