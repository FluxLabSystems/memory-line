# Memory Timeline

Capture your memories by voice or text, let AI turn them into structured events, and explore your life as an interactive timeline that surfaces the connections between moments — then ask it questions, read it back as stories, and see it on a map.

**Primary implementation:** Windows Native (.NET 10 · WinUI 3)
**Status:** Active development — core pipeline recently rebuilt and hardened; builds green in CI, runtime validation ongoing.
**Also in the repo:** an iOS roadtrip companion (SwiftUI) with its self-hosted sync service, and an in-progress [macOS app](#macos-app-in-progress).

---

## What it is

Memory Timeline is a **local-first** desktop app for recording, organizing, and rediscovering your personal history:

1. **Speak** a memory into the recorder — or **paste/type** one (Ctrl+Shift+V) into the same queue.
2. The app **transcribes** audio locally with Whisper, then an LLM (**Claude**, or a local **OpenAI-compatible** endpoint like Ollama) **extracts structured events** — title, dates *with honest precision* ("Summer 2003", not an invented day), description, category, and the people, places, and tags involved.
3. You **review and approve** the extracted events, which land on an **interactive timeline** — with photos and other **media attachments**, duration **spans**, **swimlanes**, and uncertainty bands for vague dates.
4. The app helps you **rediscover** what you captured: an **On This Day** home page, **guided recall prompts** that ask about gaps in your archive, **Ask your timeline** for grounded, cited answers, generated **narrative stories**, an **offline map**, plus connections, search, and analytics. **Backups and revision history** protect the archive itself.

Your data lives in a local SQLite database. Embeddings run **locally by default** (a bundled ONNX model), so nothing leaves your machine except the specific text you choose to send to the LLM/embedding providers you configure — and geocoding is off unless you opt in.

> ### 🧭 The **Windows Native** app is the primary and actively developed product.
> The rest of this README focuses on it. A native macOS app is being brought up alongside it — see [macOS app](#macos-app-in-progress).

---

## The memory pipeline

The heart of the app is a single flow, from voice to timeline:

```
🎙  Record            →  ⏱  Queue           →  📝  Transcribe        →  🤖  Extract
 Windows MediaCapture     recording_queue       Whisper (local)          Claude → pending events
 (16 kHz mono WAV)        (pending → done)      offline, on-device       structured, reviewable
                                                                                  │
                                                                                  ▼
🗓  Timeline          ←  ✅  Approve          ←  👀  Review
 event appears,          atomic write:            edit / approve / reject
 connections update      event + tags + people    per extracted event
                         + locations, in one txn
```

Every hop persists its state, so a failure at any stage is recoverable and visible in the UI rather than silently swallowed. **Text joins audio in the same queue**: paste or type a memory (Ctrl+Shift+V) and it enters the queue as a text source whose content is stored as its transcript, skipping transcription. Whisper transcripts are persisted too and reused across retries instead of re-transcribing.

---

## Features (Windows Native)

### Home & rediscovery
- **Home page** — the default landing page: **On This Day** anniversary cards (precision-aware — only memories with a real day anchor appear on a specific day), a recall-prompt card, recent activity, and a first-run empty state that walks a new archive through capture.
- **Daily toast** — an optional once-a-day notification when today has an anniversary; clicking it (and JumpList/Windows Timeline entries) deep-links straight to the event.
- **Guided recall prompts** — the app mines the archive for gaps (empty stretches, people with no events, era edges, thin one-line events, anniversary anchors) and asks specific questions on the Home and Queue pages. A dismissed or answered question is **never asked again**; answers can be typed or spoken and flow into the normal extraction queue.

### Timeline
- Interactive canvas with **Year / Month / Week / Day** zoom levels, smooth pan, and keyboard navigation.
- Event bubbles with category icons; era backgrounds for life phases; **duration spans** render as bars; optional **swimlanes** group events into collapsible lanes; **uncertainty bands** visualize vague dates.
- **Honest date precision** — every event carries a precision (exact/day/month/season/year/decade/unknown) and an uncertainty window; displays everywhere say "1998" or "Summer 2003" instead of a fabricated exact day.
- Scroll-driven reloads are **coalesced**, so fast panning no longer piles up redundant queries (a major timeline performance fix).
- Newly created or approved events refresh onto the timeline automatically (via an in-app messenger), and the view jumps to an event's date if it lands outside the current window.

### Capture → events
- **Recording** with pause/resume/cancel using Windows `MediaCapture`; recordings are written under `%LOCALAPPDATA%\MemoryTimeline\AudioRecordings`.
- **Text & paste capture** — Ctrl+Shift+V (or the Queue-page button) captures typed or pasted text through the same queue; transcripts are persisted and reused across retries.
- **Local speech-to-text** via **Whisper** ([Whisper.net](https://github.com/sandrohanea/whisper.net)) — file-based, fully **offline** after a one-time model download. The recorded WAV is transcribed on-device; no audio is sent to the cloud.
- **LLM event extraction** producing structured events with dates *and their precision*, tags, people (alias-aware), and locations, held in a **review queue** with approve / edit / reject.
- **Person suggestions** — extraction captures per-person details (relationship, context); the review queue flags each mentioned person as *new*, *known*, or *update details* with one-click apply into the contact book.
- **Media attachments** — attach photos, video, audio, and documents to events (file picker or drag-drop). Files are copied into a managed media tree, EXIF metadata (taken date, GPS) is read, thumbnails are generated, duplicates are detected by content hash, and images open in a lightbox viewer.

### Ask & narrate
- **Ask your timeline** — a conversational page that answers questions about your own history: hybrid retrieval (keyword + semantic, merged with reciprocal-rank fusion) grounds an LLM answer with **citations to the actual events**; it **refuses honestly** when the archive has nothing relevant, and degrades to keyword-only retrieval when embeddings are unavailable.
- **Narrative stories** — generate grounded prose from a timeline range, an era, or a person (a "Story" action on those pages), with precision-honest dates and citation integrity enforced in code; export as **Markdown or HTML**.

### Discover
- **Search** — full-text and **faceted** search across events, tags, people, locations, and eras, with debounced suggestions and saved searches.
- **Connections (RAG)** — embeddings power semantic similarity and cross-references between memories; the **default embedding provider is a local ONNX model**, so this now works with **no API key** (OpenAI embeddings remain an option).
- **Map** — a fully **offline** canvas map (no map-tile or Azure Maps service): locations gain coordinates from photo **EXIF GPS backfill** or a manual **pin drop**, with an optional, **opt-in** Nominatim geocoder that is **off by default**.
- **Analytics** — category distribution, timeline density, tag cloud, people network, and activity summaries, with real empty-states.

### Organize & manage
- **People hub** — a full contact book: nickname, relationship, email, phone, birthday, company, notes, favorites, and tinted initials avatars, with per-person profiles and event history. **Aliases** ("Bob" → "Robert") resolve during extraction, identity is **case-insensitive**, and **merging** leaves a tombstone so old links keep working.
- **Drafts** — save events, eras, and persons as drafts from their editors and resume them later from a Drafts tab on the Review page.
- **Eras** — define and color life periods that frame the timeline.
- **Backup & restore** — one-click `.mtbak` backup archives (consistent online SQLite backup, optionally including media), **restore with a preview and explicit confirmation**, and optional scheduled daily/weekly backups.
- **Revision history** — every event edit is recorded append-only; a history dialog shows what changed and when.
- **Export / Import** — JSON, CSV, and Markdown export; JSON import with duplicate handling and an optional pre-import database backup.
- **Settings** — API keys, **pluggable AI providers** (Claude or an OpenAI-compatible endpoint such as Ollama/LM Studio; local or OpenAI embeddings with a guarded re-embed flow), a per-session LLM usage counter, backup schedule, a real Clear Cache, default zoom, and theme — all persisted locally and applied without a restart.

### Windows 11 integration
- Toast **notifications** (e.g. "processing complete").
- **JumpList** quick actions and recent events.
- **Windows Timeline** activity publishing (Adaptive Cards).
- Light / Dark / System **theming**; touch and pen friendly.

---

## Tech stack

| Layer | Technology |
|-------|------------|
| UI | **WinUI 3** + XAML, `x:Bind` compiled bindings |
| MVVM | **CommunityToolkit.Mvvm** (`ObservableObject`, `[RelayCommand]`, `WeakReferenceMessenger`) |
| Runtime | **.NET 10** (`net10.0-windows10.0.26100.0` for the UI head; the Core/Data/Sync layers are plain `net10.0`) |
| Data | **SQLite** via **EF Core 10**, WAL mode, `IDbContextFactory` per-operation contexts |
| Audio | `Windows.Media.Capture` (recording), `Windows.Media.Playback` |
| Speech-to-text | **Whisper.net** (ggml `base` model, local/offline) |
| LLM | **Anthropic Claude** (`Anthropic.SDK`) **or** any **OpenAI-compatible endpoint** (Ollama, LM Studio, vLLM), routed per call |
| Embeddings | **Local ONNX** `all-MiniLM-L6-v2` (384-dim, via ONNX Runtime — the default, no key needed) **or** **OpenAI** embeddings |
| Media metadata | **MetadataExtractor** (EXIF taken-date and GPS) + Windows thumbnail generation |
| Geocoding | Optional **Nominatim** (opt-in, off by default) |
| Resilience | `Polly` (referenced), structured `ILogger` logging |

---

## Architecture

Clean, layered separation. Only the UI head targets Windows; everything below it is
plain `net10.0` and builds on any OS:

```
MemoryTimeline            WinUI 3 app  — Views, ViewModels, Controls, Converters,   [net10.0-windows]
                                         platform services (audio, STT, notifications,
                                         jump list, navigation, theme)
MemoryTimeline.Core       Business logic — services (events, timeline, queue, extraction,   [net10.0]
                                         RAG, ask/query, narrative, resurfacing, recall
                                         prompts, media, backup/revisions, export/import,
                                         settings), DTOs, timeline math
MemoryTimeline.Data       Data access  — EF Core DbContext, entity models, repositories,    [net10.0]
                                         SchemaUpgrader
MemoryTimeline.Sync       Sync client  — pairing, push/pull/ack, outbox publisher,          [net10.0]
                                         capture-status publisher, background worker
MemoryTimeline.Tests      xUnit unit, integration, and performance tests            [net10.0-windows]
```

Key architectural decisions (recently reworked — see [Recent engineering](#recent-engineering-work)):

- **Per-operation `DbContext` via `IDbContextFactory`.** A desktop app has no request scope, so every repository and service opens a short-lived context per operation (`await using var ctx = await factory.CreateDbContextAsync()`). This replaced a single app-lifetime context that was shared across all features and was the root cause of intermittent "second operation on this context" failures.
- **`SchemaUpgrader` instead of raw `EnsureCreated`.** On startup the app creates the database from the current model and idempotently repairs schema drift (missing tables/columns) on pre-existing databases — a stopgap for full EF migrations.
- **MVVM with a message bus.** Cross-feature updates (e.g. "event created" → refresh the timeline) flow through `WeakReferenceMessenger` rather than tight coupling between view models.
- **Errors are surfaced, not swallowed.** Failures propagate to visible `InfoBar`/status affordances instead of disappearing into logs.

---

## Getting started

### Prerequisites
- **Windows 11** (22H2 or later).
- **Visual Studio 2026** (18.0+) with the **.NET Desktop Development** and **Windows App SDK** workloads.
- A **.NET SDK**: the repo pins the build to the **.NET 10** SDK via `windows-native/src/global.json`, to `10.0.300` with `rollForward: latestFeature` (any 10.0.3xx-or-newer SDK). Those SDK bands require MSBuild 18, so Visual Studio 2022 can no longer build the repo.

### Build & run
```powershell
git clone <repository-url>
cd memory-line/windows-native/src

# Open the solution in Visual Studio 2026 and press F5,
# or build from the command line for x64 (the solution has no AnyCPU config):
dotnet build MemoryTimeline.sln -c Debug -p:Platform=x64
```

> **Note on `dotnet build` vs. Visual Studio:** WinUI 3 PRI resource generation uses a .NET Framework MSBuild task that loads under **Visual Studio's `msbuild.exe`** but not under the `dotnet` CLI's build engine. Build from **Visual Studio** (or `msbuild`) for a full app build; the CI pipeline does the same on a Windows runner.

### First run
- The app creates its SQLite database at `%LOCALAPPDATA%\MemoryTimeline\memory-timeline.db`.
- The first transcription downloads the Whisper model (~140 MB `ggml-base.bin`) to `%LOCALAPPDATA%\MemoryTimeline\Models\` — one time, then fully offline.
- Add your API key(s) in **Settings** before processing the queue (see below).

---

## Configuration

### API keys & AI providers
| Provider | Purpose | Required? |
|----------|---------|-----------|
| **Anthropic** ([console](https://console.anthropic.com/)) | Claude — extraction, Ask, narratives, recall wording | Needed for the default (Claude) LLM provider; alternatively point the app at a local **OpenAI-compatible endpoint** (Ollama, LM Studio) and no Anthropic key is needed |
| **OpenAI** ([keys](https://platform.openai.com/api-keys)) | Embeddings — Connections / Ask similarity | Optional — the **default embedding provider is a local ONNX model** that needs no key |

The LLM and embedding providers are each selected in **Settings** and routed **per call**, so switching applies live. Changing embedding provider changes vector dimensions (384 local vs. 1536 OpenAI); a dimension guard prevents mixing them, and Settings offers a re-embed flow. Keys and preferences are stored in the local `app_settings` table and can be changed at any time from **Settings** (no restart needed). Encrypting API keys at rest (Windows DPAPI) is a tracked hardening item — see [Roadmap](#roadmap--known-follow-ups).

### Data & model locations
```
%LOCALAPPDATA%\MemoryTimeline\
├── memory-timeline.db          SQLite database (WAL)
├── AudioRecordings\            recorded WAV files
├── Media\                      managed copies of attached media (by year/month)
├── Models\ggml-base.bin        Whisper model (downloaded on first use)
├── Models\all-MiniLM-L6-v2\    local embedding model (downloaded on first local embed)
└── error.log                   startup/diagnostic log
```

Backups (`.mtbak` archives) are written to a destination folder you choose in Settings.

---

## Recent engineering work

### 2026-08 — feature build-out (F1–F12)

Twelve features from a structured feature spec landed in August 2026, in dependency-ordered waves with verification fix passes between them (all CI-green; runtime validation on a real Windows machine is still pending):

1. **Honest dates (F1)** — every event has a date *precision* and uncertainty window; the UI never fabricates an exact day for a vague memory.
2. **Media attachments (F2)** — photos/video/audio/documents on events, with EXIF, thumbnails, dedupe, drag-drop, and a lightbox.
3. **Text & paste capture (F3)** — type or paste memories (Ctrl+Shift+V) through the same queue as audio; transcripts persist and survive retries.
4. **Ask your timeline (F4)** — grounded, cited answers over your own archive via hybrid keyword+semantic retrieval; honest refusal; works keyless in keyword-only mode.
5. **Narrative stories (F5)** — grounded prose from any timeline scope with Markdown/HTML export.
6. **Guided recall (F6)** — gap-driven questions that are never repeated once dismissed or answered.
7. **On This Day + Home (F7)** — a new default landing page with precision-aware anniversaries, a daily toast, and fixed event deep-links.
8. **Spans, swimlanes & uncertainty on the timeline (F8)** — plus coalesced scroll reloads (major perf fix).
9. **People hub (F9)** — aliases, case-insensitive identity (with defensive duplicate-merging migration), merge tombstones, profiles.
10. **Offline map (F10)** — location coordinates from EXIF or pin-drop on a fully offline canvas map; opt-in Nominatim geocoding, off by default.
11. **Pluggable AI (F11)** — local ONNX embeddings (Connections without any API key), OpenAI-compatible LLM endpoints (Ollama/LM Studio), per-call provider routing, dimension guard + re-embed flow, per-session usage counter.
12. **Backup, restore & revisions (F12)** — `.mtbak` backup archives, restore with preview+confirm, scheduled backups, append-only event revision history, a real Clear Cache.

Infrastructure alongside: a read-only-connection fix for the SQLite pragma interceptor, CI triggering on the feature branch, and three-way `app_settings` seed parity (33 keys).

### 2026-07 — feature audit and fix pass

Before the feature wave, the app went through a structured, multi-agent **feature audit and fix pass**. Highlights of what changed:

- **Fixed the core "add event" bug** — a category-casing mismatch was rejecting new events before they were saved, with the error swallowed after the dialog closed. Save now validates case-insensitively, surfaces errors in-dialog, and refreshes the timeline.
- **Fixed "search error after adding an event"** — the shared-`DbContext` concurrency described above, plus un-debounced per-keystroke autocomplete, were colliding. Resolved by the `IDbContextFactory` rework, debounced search, and honest error messages.
- **Made the voice pipeline real** — replaced a speech API that transcribed the *live microphone* (not the recorded file) with local file-based **Whisper**; fixed the unpackaged storage-path crash; persisted transcripts; made **approve** atomic and metadata-complete (tags/people/locations in one transaction).
- **Repaired RAG & settings** — embeddings now read the correct settings key and set provider/dimension; RAG queries use mapped columns and persist cross-references; the settings writer/reader key mismatch that made settings "revert" was unified behind a `SettingKeys` constants class.
- **Export/Import, notifications, navigation, and Windows integration** wiring corrected.

A **CI workflow** (`.github/workflows/windows-native-build.yml`) compiles the full solution and runs tests on a Windows runner for every push to the development branch.

Full details: [`windows-native/FEATURE-AUDIT.md`](./windows-native/FEATURE-AUDIT.md) (findings + root causes) and [`windows-native/HARDENING-FOLLOWUPS.md`](./windows-native/HARDENING-FOLLOWUPS.md) (deferred items).

---

## Roadmap / known follow-ups

- **Runtime validation** of the pipeline and the 2026-08 feature wave end-to-end on a real Windows machine.
- **Encrypt API keys at rest** (Windows DPAPI / `ProtectedData`).
- **Regenerate EF Core migrations** to replace the `SchemaUpgrader` stopgap with a proper migration history.
- **Deferred items from the 2026-08 wave** — photo-import wizard, map tile basemaps, timeline location chip, LLM token streaming, PDF narrative export, and others; see [`windows-native/DEVELOPMENT-STATUS.md`](./windows-native/DEVELOPMENT-STATUS.md).
- **Whisper model options** (larger models for accuracy; language selection UI).
- **Analytics export** and a few remaining UI polish items.
- MSIX packaging and Microsoft Store submission (Phase 7).
- **macOS app** — SwiftUI head under `macos-native/`; see [`docs/design/MACOS-PORT-PLAN.md`](./docs/design/MACOS-PORT-PLAN.md).

See [`windows-native/DEVELOPMENT-STATUS.md`](./windows-native/DEVELOPMENT-STATUS.md) for phase-level status.

---

## Project structure

```
memory-line/
├── windows-native/                 ★ Primary: Windows Native app
│   ├── src/
│   │   ├── MemoryTimeline/          WinUI 3 app (Views, ViewModels, Controls, Services)
│   │   ├── MemoryTimeline.Core/     Business logic & services
│   │   ├── MemoryTimeline.Data/     EF Core context, models, repositories, SchemaUpgrader
│   │   ├── MemoryTimeline.Tests/    xUnit tests
│   │   ├── MemoryTimeline.sln
│   │   └── global.json              pins the .NET SDK for the build
│   ├── README.md                    Windows Native overview
│   ├── FEATURE-AUDIT.md             feature-by-feature audit & root causes
│   ├── HARDENING-FOLLOWUPS.md       deferred hardening items
│   ├── DEVELOPMENT-STATUS.md        phase status
│   ├── DEVELOPMENT-HISTORY.md       consolidated phase reports
│   ├── TESTING.md · DEPLOYMENT.md
│
├── macos-native/                   macOS app (SwiftUI) — in progress
├── ios-companion/                  iOS roadtrip companion (SwiftUI) + widgets
├── services/                       self-hosted sync service (ASP.NET Core)
├── shared-contracts/               wire contracts (OpenAPI, JSON Schema, .NET DTOs)
├── docs/design/                    system designs & port plans
├── docs/reviews/                   code-review and audit reports
├── website/                        documentation website generator + built site
├── .github/workflows/              CI (Windows + macOS builds, sync API, docs site)
└── README.md                       this file
```

---

## Testing

```powershell
cd windows-native/src
dotnet test MemoryTimeline.sln -c Debug -p:Platform=x64
```

Tests cover timeline math, services, repository/integration behavior, and performance. See [`windows-native/TESTING.md`](./windows-native/TESTING.md).

---

## Privacy & security

- **Local-first** — all data is stored on your device in SQLite; media attachments are copied into a local managed folder; backups go to a folder you choose.
- **On-device transcription** — Whisper runs locally; recorded audio is never uploaded.
- **Local embeddings by default** — the default embedding provider is a local ONNX model, so Connections/Ask retrieval can run entirely on-device.
- **Selective cloud calls** — only the transcript text you process is sent to your configured LLM provider (which can itself be a local OpenAI-compatible endpoint), and only the event text you embed is sent to your embedding provider if you choose OpenAI.
- **Geocoding is opt-in and off by default** — coordinates otherwise come only from photo EXIF or manual pin drops; when enabled, only the location name is sent to Nominatim.
- **You own your recordings** — original audio is never automatically deleted.
- **Note:** API keys are currently stored in the local settings database; encrypting them at rest is a tracked follow-up.

---

## macOS app (in progress)

A native **SwiftUI** macOS app is being brought up under [`macos-native/`](./macos-native),
sharing its domain models, sync networking, persistence, and Keychain code with the
existing iOS companion under `ios-companion/MemoryLineCompanion/Shared/`.

Groundwork already in the tree: `MemoryTimeline.Core` and `MemoryTimeline.Sync` were
decoupled from WinUI and now target plain `net10.0`, so the business layer builds off
Windows and the two heads can converge on one set of rules rather than drifting.

See [`docs/design/MACOS-PORT-PLAN.md`](./docs/design/MACOS-PORT-PLAN.md) for the phased
plan and the inventory of what is still Windows-bound.

---

## Documentation

Everything below is also published as a **browsable documentation website** — one place
with a sidebar, cross-links, per-page tables of contents and full-text search. Open
[`website/_site/index.html`](./website/_site/index.html) locally, or build it with
`npm --prefix website install && npm --prefix website run build`.
See [`website/README.md`](./website/README.md).

| Document | Description |
|----------|-------------|
| [`windows-native/README.md`](./windows-native/README.md) | Windows Native overview & setup |
| [`windows-native/FEATURE-AUDIT.md`](./windows-native/FEATURE-AUDIT.md) | Feature-by-feature audit & root-cause analysis |
| [`windows-native/HARDENING-FOLLOWUPS.md`](./windows-native/HARDENING-FOLLOWUPS.md) | Deferred hardening items |
| [`windows-native/DEVELOPMENT-STATUS.md`](./windows-native/DEVELOPMENT-STATUS.md) | Phase-level development status |
| [`windows-native/DEVELOPMENT-HISTORY.md`](./windows-native/DEVELOPMENT-HISTORY.md) | Consolidated phase reports |
| [`windows-native/TESTING.md`](./windows-native/TESTING.md) | Testing guide |
| [`windows-native/DEPLOYMENT.md`](./windows-native/DEPLOYMENT.md) | Packaging & deployment |
| [`docs/design/MACOS-PORT-PLAN.md`](./docs/design/MACOS-PORT-PLAN.md) | macOS port plan & Windows-bound inventory |
| [`docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md`](./docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md) | iOS companion & sync system design |
| [`services/README.md`](./services/README.md) | Sync service operator guide |
| [`claude.md`](./claude.md) | AI-assistant development guide |

---

## Contributing

1. Branch from the active development branch.
2. Make focused changes with tests where practical.
3. Ensure the solution builds (Visual Studio / CI) and update docs as needed.
4. Open a pull request.

For Windows Native work: follow WinUI 3 guidelines, use CommunityToolkit.Mvvm, keep `DbContext` usage per-operation via the factory, and surface errors in the UI rather than swallowing them.

---

## License

MIT License — see [`LICENSE`](./LICENSE).
