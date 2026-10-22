# Memory Line iOS Roadtrip Companion — System Design

**Status:** Accepted — Phases 0–3 implemented (Windows, sync service, iOS, macOS); Phase 4 implemented end to end (service sessions, turns and idle-session reaper; Ask clients on macOS and iOS; the Windows client's `AssistantTurnResponder` answers — only the `windows` responder mode is deployed, and `provider`/`on_device` turns fail at once with a reason); Phase 5 trip/detour endpoints implemented on the service, with Google Places and Apple Maps adapters selectable by configuration (default: none); §22 decisions resolved 2026-08-07; status last updated 2026-10-22  
**Date:** 2026-08-06  
**Repository:** `FluxLabSystems/memory-line`  
**Target branch:** `main` (the original `design/ios-roadtrip-companion` branch has long been merged)

---

## 1. Executive summary

Memory Line currently operates as a local-first Windows desktop application: it records audio, queues recordings, transcribes locally with Whisper, extracts structured memories with an LLM, presents pending events for review, and stores approved events in a local SQLite timeline.

The proposed iOS companion extends that system into a roadtrip-focused, low-distraction capture and interaction surface. The iPhone becomes the mobile capture, voice interaction, navigation-context, and detour-discovery client. The Windows application remains the authoritative long-form review, organization, analytics, and archival environment.

The central architectural change is the introduction of a **Memory Line Sync Service** and a device-neutral domain protocol. The iOS application must not write directly into the Windows SQLite database or depend on Windows file paths. Instead, both applications exchange versioned domain commands and artifacts through an authenticated API with durable, idempotent synchronization.

The first release should prioritize:

1. One-touch voice recording from the app, Lock Screen widgets, Control Center controls, and supported hands-free entry points.
2. On-device recording with durable offline storage and deferred upload.
3. Server or Windows-hosted ingestion into the existing transcription and extraction pipeline.
4. One-touch conversational voice interaction with a retrieval-augmented Memory Line assistant.
5. A roadtrip dashboard combining recording, assistant, navigation status, media controls, and generated detour cards.
6. Context-aware detour suggestions verified against current opening hours, route impact, admission pricing, and user constraints.
7. Safe driving UX: glanceable information, audio-first interaction, and strict suppression of editing-heavy flows while moving.

---

## 2. Product goals

### 2.1 Primary goals

- Capture spontaneous memories, observations, ideas, and travel narration with one deliberate action.
- Preserve recordings when cellular data is unavailable or unreliable.
- Convert mobile recordings into the same pending-event workflow used by the Windows application.
- Allow voice questions against the user’s memory corpus and current trip context.
- Surface worthwhile detours without requiring manual research while driving.
- Make the iPhone experience useful independently while preserving Windows as the richer review and archive station.
- Maintain local-first ownership: raw audio and the canonical memory database remain under user control.

### 2.2 Secondary goals

- Attach road context to captures: trip, route segment, coarse location, heading, speed state, and nearby point of interest.
- Let a user mark a recording as memory, idea, trip log, reminder, question, or unclassified capture.
- Support CarPlay-compatible experiences where Apple platform rules permit them.
- Support background transfer and eventual consistency across multiple devices.
- Allow future iPad, macOS, web, and Android clients to reuse the same protocol.

### 2.3 Non-goals for the first release

- Rebuilding the entire Windows timeline editor on iOS.
- Direct replication of the SQLite database file between devices.
- Continuous ambient recording.
- Replacing Apple Maps, Google Maps, or the system media player.
- Autonomous route modification without explicit user confirmation.
- Full offline LLM inference on iPhone in the first release.
- Real-time collaborative multi-user timelines.

---

## 3. Existing architecture and required changes

### 3.1 Existing Windows pipeline

The current Windows application has a persistent queue and recoverable pipeline:

```text
Record -> recording_queue -> local Whisper transcription -> LLM extraction
       -> pending_events -> user review -> approved timeline events
```

The current `RecordingQueue` model stores an `audio_file_path` that points to a Windows-local file. `QueueService` accepts an `AudioRecordingDto`, persists a queue item, and processes pending items through `IEventExtractionService`. This works well on one machine but is not a device-neutral ingestion contract.

### 3.2 Architectural constraints

The iOS design must account for the following realities:

- The Windows SQLite file cannot safely be opened remotely by iOS.
- A Windows path has no meaning on iOS.
- Mobile recordings may arrive in chunks, may be retried, and may be duplicated by background transfer.
- Mobile devices frequently lose network connectivity.
- iOS background execution is opportunistic and cannot be treated as an always-running service.
- Voice interaction requires streaming or short-turn request handling distinct from the existing batch extraction pipeline.
- Detour information is time-sensitive and must be revalidated near presentation time.

### 3.3 Required system changes

Memory Line should add:

- Device-neutral IDs and globally unique capture IDs.
- A `capture_artifacts` abstraction that replaces assumptions about direct local file paths.
- A versioned sync protocol with idempotency keys and cursors.
- An authenticated HTTP API and optional streaming channel.
- A sync outbox/inbox on Windows and iOS.
- Trip, route-context, detour, assistant-session, and device entities.
- A retrieval service that can answer questions over approved events, pending transcripts where allowed, and trip context.
- A provider abstraction for maps/place search, opening hours, pricing, and route estimates.

---

## 4. Proposed topology

```text
+---------------------------+                     +----------------------------+
| iOS Roadtrip Companion    |                     | Windows Memory Line        |
|---------------------------|                     |----------------------------|
| SwiftUI                   |                     | WinUI 3                    |
| App Intents / Widgets     |                     | Existing timeline/review   |
| AVAudioEngine             |                     | Existing queue/extraction  |
| Core Location             |                     | New sync client            |
| MapKit / provider adapter |                     | New artifact cache         |
| Local encrypted store     |                     | New assistant UI           |
| Sync outbox               |                     | Existing SQLite + schema   |
+-------------+-------------+                     +--------------+-------------+
              | HTTPS / WebSocket or SSE                         |
              +----------------------+---------------------------+
                                     |
                         +-----------v------------+
                         | Memory Line Sync/API   |
                         |------------------------|
                         | Authentication         |
                         | Device registry        |
                         | Capture ingestion      |
                         | Artifact object store  |
                         | Sync cursor/change log |
                         | Assistant orchestration|
                         | Detour agent           |
                         | Push notification fanout|
                         +------+-----------+-----+
                                |           |
                  +-------------+           +------------------+
                  |                                            |
        +---------v---------+                        +---------v----------+
        | LLM / embeddings  |                        | Places/maps/pricing |
        | provider adapters |                        | provider adapters   |
        +-------------------+                        +---------------------+
```

### 4.1 Deployment modes

The architecture should support two modes behind the same API contract.

#### Mode A — Self-hosted home service

The sync/API service runs on the user’s home-lab or Windows machine and is exposed through a private overlay network or reverse proxy. This best matches Memory Line’s local-first positioning.

Recommended characteristics:

- ASP.NET Core service.
- PostgreSQL or SQLite for service metadata depending on scale.
- S3-compatible object storage or a filesystem-backed artifact store.
- Tailscale/WireGuard/private access preferred.
- Windows application connects as another authenticated client.

#### Mode B — Managed relay

A hosted service stores encrypted artifacts and synchronization metadata. The Windows machine downloads and becomes the canonical archive when online.

This improves reliability away from home but increases operational and privacy burden. It should be optional, not required by the domain model.

### 4.2 Recommended first implementation

Implement an ASP.NET Core sync service in the same repository and allow it to run:

- locally on the Windows machine for development;
- in a Docker container on the user’s home-lab;
- later as a managed deployment without changing the client contract.

---

## 5. Repository structure

Proposed additions:

```text
memory-line/
├── ios-companion/
│   ├── MemoryLineCompanion.xcodeproj
│   ├── App/
│   ├── Features/
│   │   ├── Capture/
│   │   ├── Assistant/
│   │   ├── Roadtrip/
│   │   ├── Detours/
│   │   ├── Sync/
│   │   └── Settings/
│   ├── Shared/
│   │   ├── Domain/
│   │   ├── Persistence/
│   │   ├── Networking/
│   │   └── Security/
│   ├── Widgets/
│   ├── AppIntents/
│   ├── Tests/
│   └── README.md
├── services/
│   └── MemoryTimeline.SyncApi/
│       ├── Api/
│       ├── Application/
│       ├── Domain/
│       ├── Infrastructure/
│       ├── Workers/
│       └── Tests/
├── shared-contracts/
│   ├── openapi/
│   │   └── memory-line-sync-v1.yaml
│   └── json-schema/
├── windows-native/
│   └── src/
│       ├── MemoryTimeline/
│       ├── MemoryTimeline.Core/
│       ├── MemoryTimeline.Data/
│       ├── MemoryTimeline.Sync/
│       └── MemoryTimeline.Tests/
└── docs/design/
    └── IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md
```

The iOS project may live in the same monorepo initially. A separate repository is reasonable later, but shared contracts and coordinated schema evolution are simpler in one repository during early development.

---

## 6. Domain model

### 6.1 Capture

A `Capture` is the device-neutral unit of user input. Audio is one artifact type; text and images may be added later.

```text
Capture
- capture_id: UUID
- owner_id: UUID
- source_device_id: UUID
- source_platform: ios | windows | other
- capture_type: memory | idea | trip_log | reminder | question | unclassified
- created_at_utc
- captured_at_utc
- timezone_id
- local_offset_minutes
- status: local_only | uploading | received | processing | review_ready | completed | failed
- title_hint: nullable string
- user_note: nullable string
- trip_id: nullable UUID
- route_segment_id: nullable UUID
- location_context_id: nullable UUID
- client_schema_version
- server_revision
- deleted_at_utc: nullable
```

### 6.2 CaptureArtifact

```text
CaptureArtifact
- artifact_id: UUID
- capture_id: UUID
- artifact_type: audio_original | audio_normalized | transcript | waveform | image | metadata
- storage_locator: opaque string
- media_type
- byte_length
- sha256
- encryption_scheme
- created_at_utc
- upload_state
```

The queue should reference an `artifact_id`, not rely exclusively on `audio_file_path`.

### 6.3 Transcript

```text
Transcript
- transcript_id: UUID
- capture_id: UUID
- text
- language
- provider
- model
- confidence_summary
- segments_json
- created_at_utc
- revision
```

### 6.4 Trip and route context

```text
Trip
- trip_id: UUID
- name
- started_at_utc
- ended_at_utc
- origin_label
- destination_label
- status: planned | active | paused | completed | archived
- preferences_json

RouteContext
- route_context_id: UUID
- trip_id: UUID
- captured_at_utc
- latitude
- longitude
- horizontal_accuracy_m
- heading_degrees
- speed_mps
- route_progress
- remaining_distance_m
- remaining_duration_s
- current_road_name
- destination_eta_utc
- provider
```

Location sampling must be configurable and minimized. A capture should store one contextual snapshot by default, not a continuous trail unless the user explicitly enables trip logging.

### 6.5 DetourSuggestion

```text
DetourSuggestion
- suggestion_id: UUID
- trip_id: UUID
- generated_at_utc
- expires_at_utc
- place_provider
- provider_place_id
- name
- category
- reason
- latitude
- longitude
- route_added_distance_m
- route_added_duration_s
- open_status: open | closed | closing_soon | unknown
- next_open_interval
- price_summary
- admission_currency
- confidence
- verification_sources_json
- state: proposed | heard | saved | accepted | dismissed | expired
```

### 6.6 AssistantSession and AssistantTurn

```text
AssistantSession
- session_id: UUID
- device_id: UUID
- trip_id: nullable UUID
- started_at_utc
- ended_at_utc
- mode: memory_query | trip_assistant | capture_followup

AssistantTurn
- turn_id: UUID
- session_id: UUID
- sequence
- user_audio_artifact_id: nullable UUID
- user_text
- assistant_text
- assistant_audio_artifact_id: nullable UUID
- tool_calls_json
- citations_json
- created_at_utc
```

### 6.7 Device and sync entities

```text
Device
- device_id: UUID
- owner_id: UUID
- platform
- display_name
- public_key
- push_token_encrypted
- app_version
- last_seen_at_utc
- revoked_at_utc

SyncChange
- change_id: monotonically ordered value
- owner_id
- entity_type
- entity_id
- operation: upsert | delete
- revision
- changed_at_utc
```

---

## 7. Windows application modifications

### 7.1 Replace path-only queue ingestion

Extend `RecordingQueue` with device-neutral fields:

```text
source_capture_id        nullable UUID/string, unique
source_device_id         nullable UUID/string
source_platform          default "windows"
audio_artifact_id        nullable UUID/string
original_file_name       nullable string
content_sha256           nullable string
sync_state               nullable string
received_at              nullable timestamp
```

Keep `audio_file_path` for local processing, but treat it as a resolved cache path rather than the identity of the recording.

### 7.2 New ingestion service

Add an abstraction:

```csharp
public interface ICaptureIngestionService
{
    Task<IngestionResult> IngestLocalRecordingAsync(
        AudioRecordingDto recording,
        CancellationToken cancellationToken = default);

    Task<IngestionResult> IngestRemoteCaptureAsync(
        RemoteCaptureEnvelope capture,
        CancellationToken cancellationToken = default);
}
```

Responsibilities:

- enforce idempotency using `source_capture_id` and artifact hash;
- download or resolve the audio artifact;
- verify byte length and SHA-256;
- normalize format if required;
- create or update the queue item;
- acknowledge durable receipt to the sync service;
- schedule existing queue processing;
- never create duplicate pending events on retries.

### 7.3 Queue service changes

`IQueueService` should gain cancellation support and explicit processing stages:

```csharp
Task<RecordingQueue> AddToQueueAsync(
    AudioRecordingDto recording,
    CancellationToken cancellationToken = default);

Task ProcessCaptureAsync(
    string queueId,
    CancellationToken cancellationToken = default);
```

Recommended queue status model:

```text
pending_download
verifying
ready_for_transcription
transcribing
extracting
review_ready
completed
failed_retryable
failed_configuration
failed_permanent
```

The current four-state model can remain externally compatible while an additional `processing_stage` column provides better mobile progress reporting.

### 7.4 Sync worker

Add `MemoryTimeline.Sync` with:

- `ISyncClient`
- `IArtifactTransferClient`
- `ISyncCursorStore`
- `SyncBackgroundWorker`
- `RemoteChangeApplier`
- `LocalOutboxPublisher`

The Windows client should:

1. Pull changes after its stored cursor.
2. Download unclaimed mobile artifacts.
3. Ingest captures idempotently.
4. Push processing state, transcript availability, pending-event state, and approved-event changes.
5. Resolve conflicts using revision checks.

### 7.5 Database schema

Add tables:

- `devices`
- `captures`
- `capture_artifacts`
- `transcripts`
- `sync_outbox`
- `sync_state`
- `trips`
- `route_contexts`
- `detour_suggestions`
- `assistant_sessions`
- `assistant_turns`

Do not put remote synchronization logic directly into EF repositories for timeline entities. Use an outbox written in the same database transaction as the domain change.

### 7.6 Outbox pattern

When Windows approves or edits an event:

```text
BEGIN TRANSACTION
  update event and metadata
  insert sync_outbox event_changed payload
COMMIT
```

A worker publishes outbox records and marks them delivered. This prevents the local database and remote sync state from silently diverging.

### 7.7 Review UX

Pending events created from iOS should display:

- source device;
- capture time and timezone;
- trip name;
- optional location context;
- original audio playback;
- transcript;
- synchronization and processing history.

The Windows review queue remains the canonical editing surface in the first release.

---

## 8. iOS application architecture

### 8.1 Technology choices

- Swift 6+
- SwiftUI
- Swift Concurrency actors and `async/await`
- AVFoundation / AVAudioEngine for recording and playback
- SwiftData or GRDB/SQLite for durable local state
- BackgroundTasks and background `URLSession` for deferred transfer
- App Intents for actions exposed to Siri, Shortcuts, widgets, and supported controls
- WidgetKit for Home Screen and Lock Screen widgets
- ActivityKit for active trip/capture Live Activities where useful
- Core Location for capture context
- MapKit plus provider adapters for route and place functions
- Keychain and Secure Enclave-backed key material where practical
- UserNotifications for completed processing and detour alerts

GRDB is preferable if precise migrations, outbox queries, WAL tuning, and deterministic synchronization behavior outweigh SwiftData convenience.

### 8.2 Modules

```text
AppShell
CaptureFeature
AssistantFeature
RoadtripFeature
DetourFeature
SyncFeature
MediaIntegration
NavigationIntegration
Persistence
Security
Telemetry
```

Each feature owns presentation and use cases; shared domain models are immutable value types where practical.

### 8.3 Local persistence

Required local tables:

- captures
- artifacts
- upload_parts
- sync_outbox
- sync_cursor
- trips
- route_context_snapshots
- detour_cache
- assistant_sessions
- assistant_turns
- settings

Every capture is committed locally before the recording UI reports success.

### 8.4 Capture state machine

```text
idle
  -> preparing
  -> recording
  -> paused
  -> finalizing
  -> saved_local
  -> queued_upload
  -> uploading
  -> uploaded
  -> processing_remote
  -> review_ready
  -> completed

Any state -> failed_recoverable
Any active recording state -> cancelled
```

The audio file should be written incrementally. A crash must not destroy the entire recording; recoverable partial files should be surfaced on next launch.

### 8.5 Audio format

Recommended original capture:

- AAC-LC in `.m4a`, mono, 44.1 or 48 kHz for efficient mobile storage; or
- lossless PCM only when explicitly selected.

The ingestion service normalizes to the Windows transcription format. Avoid forcing iPhone to record large WAV files during long drives.

Store:

- original mobile artifact;
- normalized transcription artifact if generated;
- duration and waveform summary;
- SHA-256 hash.

### 8.6 One-touch record

Entry points:

- prominent in-app record button;
- Lock Screen widget;
- Home Screen widget;
- Control Center control through supported App Intent/control APIs;
- Action Button assignment through Shortcuts/App Intents;
- Siri phrase;
- optional headset/steering control behavior where platform APIs permit.

A one-touch action should begin capture immediately after required permissions are already granted. First-run permission education happens before driving mode use.

### 8.7 One-touch LLM voice interaction

The assistant interaction uses push-to-talk by default:

1. User invokes assistant.
2. App records a question.
3. Speech is transcribed locally or remotely.
4. Assistant service classifies intent.
5. Retrieval and tools run.
6. Response is streamed as text and synthesized audio.
7. The user can interrupt playback with a new turn.

Initial tools:

- search approved memories;
- summarize memories about a person/place/time period;
- ask follow-up questions about the current capture;
- query current trip status;
- find and verify detours;
- save a detour;
- create a new voice capture;
- retrieve pending processing status.

No assistant tool should silently modify navigation. The assistant may propose a destination and hand it to the chosen navigation app after confirmation.

---

## 9. Widgets, controls, and integrations

### 9.1 Capture widget

Compact controls:

- start/stop recording;
- open assistant;
- show last capture upload state.

The widget delegates to App Intents and the containing app. It should not attempt unrestricted long-running recording inside the widget extension.

### 9.2 Roadtrip dashboard widget

Displays:

- current destination and ETA when available;
- next saved detour;
- latest generated suggestion;
- recording state;
- sync state.

### 9.3 Music controls

The application should not recreate a universal media player. Use system Now Playing controls and supported media integrations. In-app controls may expose play/pause/skip where the active media session and platform APIs permit.

### 9.4 Maps controls

The application should treat navigation providers as integrations:

- show current trip/destination state;
- open Apple Maps or another selected provider;
- calculate detour route impact;
- pass an accepted detour as a destination or waypoint where supported.

Avoid embedding an inferior turn-by-turn system in the first release.

### 9.5 Live Activity

During an active trip, a Live Activity may show:

- capture status and elapsed time;
- latest suggestion name and added time;
- current sync/upload state;
- tap targets to reopen the relevant app surface.

---

## 10. Detour agent design

### 10.1 Inputs

- active route polyline or origin/destination approximation;
- current location and direction of travel;
- remaining trip time;
- user-defined maximum added time/distance;
- interests and exclusions;
- time of day and date;
- current weather when available;
- accessibility and mobility constraints;
- prior visited/saved/dismissed locations;
- charging/fuel/meal/rest needs;
- opening hours and closing-soon threshold;
- admission price and reservation requirement.

### 10.2 Candidate generation

The service queries place providers within a route corridor rather than a simple radius. Candidate categories include:

- scenic viewpoints;
- unusual roadside attractions;
- museums and historical sites;
- architecture and infrastructure;
- parks and short walks;
- specialty food;
- technology/industrial sites open to visitors;
- weather phenomena or seasonal events;
- practical stops.

### 10.3 Verification pipeline

```text
Generate candidate set
-> remove route-incompatible candidates
-> fetch authoritative place metadata
-> verify hours for arrival time
-> verify price/admission where available
-> estimate route delta
-> check freshness and confidence
-> rank
-> produce concise spoken rationale
```

Every suggestion must include a freshness timestamp and confidence. Unknown opening hours or prices must be labeled unknown, not invented.

### 10.4 Ranking

Example score:

```text
score =
  interest_match * 0.30
+ uniqueness * 0.20
+ route_efficiency * 0.20
+ timing_fit * 0.15
+ data_confidence * 0.10
+ novelty_vs_history * 0.05
```

Weights should be user-adjustable later.

### 10.5 Generation cadence

Generate suggestions:

- when a trip starts;
- when route context changes materially;
- every configurable 30–60 minutes during long trips;
- when the user asks;
- before a saved suggestion becomes impractical;
- after a suggestion is dismissed to avoid repetition.

Do not issue frequent unsolicited prompts. A default maximum of one spoken suggestion per hour is reasonable unless the user requests more.

### 10.6 Spoken card

Example:

> “There’s an aviation museum 11 minutes off your route. It is open until 5:00 PM, arrival would be around 2:40, and admission is listed at 14 dollars. It has several restored Cold War aircraft. Save it, navigate there, or skip?”

The visual card contains source/freshness details, but the driving interaction remains three simple choices.

---

## 11. API design

### 11.1 Conventions

- Base path: `/api/v1`
- JSON for metadata
- Presigned or direct multipart upload for large artifacts
- UUID identifiers
- UTC timestamps in ISO 8601
- `Idempotency-Key` header on all create/mutation calls
- `If-Match`/ETag or explicit revision for updates
- cursor-based synchronization
- OAuth 2.1/OIDC or device-bound token exchange

### 11.2 Core endpoints

#### Device registration

```http
POST /api/v1/devices/register
POST /api/v1/devices/{deviceId}/refresh
DELETE /api/v1/devices/{deviceId}
```

#### Captures

```http
POST /api/v1/captures
GET  /api/v1/captures/{captureId}
PATCH /api/v1/captures/{captureId}
POST /api/v1/captures/{captureId}/complete
POST /api/v1/captures/{captureId}/retry
```

#### Artifacts

```http
POST /api/v1/captures/{captureId}/artifacts/initiate
PUT  /api/v1/artifacts/{artifactId}/parts/{partNumber}
POST /api/v1/artifacts/{artifactId}/complete
GET  /api/v1/artifacts/{artifactId}/download
```

#### Sync

```http
POST /api/v1/sync/push
GET  /api/v1/sync/pull?cursor={cursor}&limit={limit}
POST /api/v1/sync/ack
```

#### Assistant

```http
POST /api/v1/assistant/sessions
POST /api/v1/assistant/sessions/{sessionId}/turns
GET  /api/v1/assistant/sessions/{sessionId}/stream
```

#### Trips and detours

```http
POST /api/v1/trips
PATCH /api/v1/trips/{tripId}
POST /api/v1/trips/{tripId}/route-context
POST /api/v1/trips/{tripId}/detours/generate
GET  /api/v1/trips/{tripId}/detours
POST /api/v1/detours/{suggestionId}/accept
POST /api/v1/detours/{suggestionId}/dismiss
POST /api/v1/detours/{suggestionId}/save
```

### 11.3 Capture creation example

```json
{
  "captureId": "8d3afab4-bd0c-43db-9b43-18cc4ddc49aa",
  "sourceDeviceId": "5ef474dd-02f8-48e5-9616-a48b27017466",
  "captureType": "trip_log",
  "capturedAtUtc": "2026-08-06T19:46:00Z",
  "timezoneId": "America/Toronto",
  "localOffsetMinutes": -240,
  "tripId": "fb811275-7cf4-490f-a908-16657d1f92cb",
  "locationContext": {
    "latitude": 43.6532,
    "longitude": -79.3832,
    "horizontalAccuracyM": 18.0,
    "headingDegrees": 255.0,
    "speedMps": 21.4
  },
  "clientSchemaVersion": 1
}
```

### 11.4 Idempotency

The server stores the response associated with each `(owner_id, device_id, idempotency_key)` tuple for a retention window. Repeated requests return the original result.

Artifact completion additionally validates:

- expected byte length;
- expected part count;
- SHA-256;
- media type;
- capture ownership.

---

## 12. Synchronization and conflict handling

### 12.1 Model

Use eventual consistency with one canonical revision per entity. Clients maintain:

- local outbox sequence;
- last acknowledged server cursor;
- per-entity server revision;
- tombstones for deletions.

### 12.2 Conflict rules

- Immutable artifacts: content-addressed; duplicate hash means reuse.
- Capture metadata: last writer wins only for non-critical descriptive fields.
- Approved events: revision conflict requires merge or explicit resolution.
- Pending event review state: Windows is authoritative in release one.
- Trip status: latest valid transition wins according to state-machine rules.
- Detour disposition: accepted/dismissed states use monotonic transitions; an accepted suggestion is not overwritten by a stale proposed state.
- Deletions: tombstone with retention period; never silently resurrect from an old device.

### 12.3 Offline operation

While offline, iOS can:

- record and classify captures;
- attach cached route context;
- review local upload queue;
- access cached detours;
- use limited local commands;
- queue assistant questions, though responses requiring server tools wait for connectivity.

When connectivity resumes, background `URLSession` transfers artifacts and the sync outbox drains.

---

## 13. Assistant and retrieval architecture

### 13.1 Retrieval sources

The assistant may retrieve from:

- approved timeline events;
- event metadata, people, locations, tags, and eras;
- embeddings and cross-references;
- user-approved pending transcripts;
- active trip context;
- saved/dismissed detours;
- current capture transcript during a follow-up session.

### 13.2 Retrieval flow

```text
voice input
-> STT
-> intent classification
-> permission and driving-mode policy check
-> hybrid retrieval: FTS + metadata filters + embeddings
-> optional external tools
-> response generation with source references
-> TTS
```

### 13.3 Grounding

Responses about personal history should carry internal source references to event IDs and transcript spans. The iOS UI can show “From 3 memories” and allow later inspection. Spoken output should not read citation IDs aloud.

### 13.4 Privacy boundary

Only the minimum required text should be sent to external LLM providers. Retrieval should occur inside the trusted service; the provider receives selected context, not the entire database.

---

## 14. Security and privacy

### 14.1 Authentication

- User account or self-hosted owner identity.
- Device registration with a per-device key pair.
- Short-lived access tokens.
- Refresh token stored in Keychain on iOS and protected storage on Windows.
- Device revocation from Windows or a management endpoint.

### 14.2 Transport and artifact encryption

- TLS 1.3 where available.
- Certificate pinning is optional and must support rotation.
- Artifacts encrypted at rest.
- For managed relay mode, support client-side envelope encryption so the relay cannot read raw audio.
- Each artifact uses a random data-encryption key wrapped to authorized device/service keys.

### 14.3 Secrets

- Remove API keys from plain application settings over time.
- Windows: DPAPI/`ProtectedData` or OS credential vault.
- iOS: Keychain.
- Sync service: environment/secret manager; never database plaintext.

### 14.4 Location privacy

- Location is opt-in and scoped separately from microphone permission.
- Default capture stores a single coarse snapshot.
- Precise location can be disabled.
- Continuous trip logging is a separate explicit setting.
- Retention controls allow route-context deletion without deleting the memory.

### 14.5 Logging

Never log:

- raw transcripts;
- assistant prompts containing personal memories;
- audio bytes;
- tokens or keys;
- exact location unless diagnostic mode is explicitly enabled.

Use correlation IDs and structured redacted diagnostics.

---

## 15. Driving-mode UX requirements

### 15.1 Motion detection

Driving mode may be inferred from:

- active navigation;
- speed threshold sustained over time;
- connected CarPlay;
- explicit user toggle.

The user can override incorrect detection.

### 15.2 Restrictions while moving

While driving:

- prioritize voice and large controls;
- no transcript editing;
- no timeline browsing requiring dense interaction;
- no long scrolling lists;
- read detours aloud only at controlled cadence;
- require short confirmation for navigation changes;
- defer complex review to “Review when parked.”

### 15.3 Capture confirmation

A successful capture should use a short tone/haptic and spoken confirmation such as “Saved locally” or “Uploaded.” Avoid long status narration.

---

## 16. Reliability requirements

### 16.1 Data durability

- A completed recording must be fsynced/closed before the UI reports it saved.
- Local metadata and outbox insertion occur atomically.
- Uploads support resume and retry.
- Server ingestion is idempotent.
- Windows processing never deletes the original artifact automatically.

### 16.2 Retry policy

Classify failures:

- retryable network;
- retryable server;
- authentication required;
- configuration required;
- invalid artifact;
- unsupported media;
- permanent domain error.

Use exponential backoff with jitter and respect iOS background scheduling.

### 16.3 Observability

Metrics:

- capture save success rate;
- upload latency and retry count;
- ingestion deduplication count;
- transcription duration;
- extraction duration;
- assistant first-audio latency;
- detour verification failure rate;
- stale suggestion rate;
- sync cursor lag;
- crash-free sessions.

Distributed traces should connect mobile request, artifact upload, server ingestion, Windows processing, and status callback without storing personal content.

---

## 17. Performance targets

Initial targets:

- Record action to active recording indication: under 500 ms after warm launch.
- Local finalize for a 10-minute recording: under 2 seconds.
- Capture visible in local history: immediate after finalize.
- Upload resumes automatically after connectivity returns.
- Assistant partial response audio: under 2.5 seconds on healthy connectivity.
- Detour card from cached candidates: under 1 second.
- Fresh detour generation: under 8 seconds typical.
- Windows sync detection after server receipt: under 30 seconds while Windows is online.
- Duplicate capture rate after retries: zero.

---

## 18. Testing strategy

### 18.1 Contract tests

- OpenAPI schema validation.
- Swift and C# generated client compatibility.
- Backward compatibility for at least one prior API version.
- Golden payload tests for captures, artifacts, sync changes, assistant turns, and detours.

### 18.2 iOS tests

- Capture state-machine tests.
- Crash recovery from partial recording.
- Offline outbox and resumed upload.
- Background transfer completion.
- Permission denial/revocation.
- Widget/App Intent invocation.
- driving-mode UI restrictions.
- location precision settings.
- detour expiration and dismissal behavior.

### 18.3 Windows tests

- remote capture ingestion idempotency;
- artifact hash validation;
- queue stage transitions;
- duplicate upload handling;
- outbox atomicity;
- sync conflict handling;
- mobile-origin pending-event review;
- retry classification.

### 18.4 Service tests

- authentication and device revocation;
- multipart upload integrity;
- cursor ordering;
- concurrent push/pull;
- artifact authorization;
- assistant tool permission checks;
- detour verification freshness;
- provider outage fallback.

### 18.5 End-to-end scenarios

1. Record offline for 30 minutes, regain signal, upload, process on Windows, approve event, see status on iOS.
2. Kill the app during recording, relaunch, recover partial audio.
3. Upload the same capture repeatedly and verify one queue item.
4. Revoke the iPhone from Windows and verify access stops.
5. Ask a memory question and verify cited source events.
6. Generate a detour that closes before arrival and verify it is suppressed or marked invalid.
7. Accept a detour and pass it to the selected navigation provider.
8. Use the capture control from Lock Screen and Action Button.

---

## 19. Migration plan

### Phase 0 — Contract and schema groundwork

- Define shared IDs, revisions, timestamps, and artifact contracts.
- Add migrations and `SchemaUpgrader` compatibility for new fields.
- Add `source_capture_id` uniqueness.
- Introduce `ICaptureIngestionService` without changing existing UI behavior.
- Add local Windows outbox.

**Exit criterion:** Existing Windows recordings pass through the new ingestion abstraction with no regression.

### Phase 1 — Sync service and Windows client

- Build device registration and authentication.
- Implement capture metadata and artifact upload.
- Implement cursor-based pull/push.
- Build Windows sync worker.
- Ingest remote audio into current queue.

**Exit criterion:** A test client can upload an audio capture and it appears once in Windows review.

### Phase 2 — iOS capture MVP

- SwiftUI shell.
- One-touch recording.
- Local durable queue.
- Background upload.
- Capture history and status.
- Basic widget/App Intent.

**Exit criterion:** Road recording works offline and reliably reaches Windows later.

### Phase 3 — Processing status and review handoff

- Sync queue progress, transcript status, pending-event status, and approval state.
- Push notifications.
- Playback of original audio and transcript preview on iOS.

**Exit criterion:** User can understand the full lifecycle without opening Windows, while edits remain on Windows.

### Phase 4 — Voice assistant

- Assistant sessions and streaming turns.
- STT/TTS.
- Memory retrieval.
- Current trip context.
- interruption and cancellation.

**Exit criterion:** User can ask grounded questions over their timeline and receive audio responses.

### Phase 5 — Roadtrip and detour agent

- Trip lifecycle.
- route context snapshots.
- provider adapters.
- verified detour generation.
- save/dismiss/accept flows.
- navigation handoff.

**Exit criterion:** Suggestions include verified timing, route cost, and price confidence.

### Phase 6 — Car and control surfaces

- Lock Screen and Home Screen widgets.
- Control Center/App Intent support.
- Live Activity.
- CarPlay surfaces permitted by entitlement/category.
- Action Button and Siri workflows.

**Exit criterion:** Core capture and assistant functions are available with minimal visual interaction.

### Phase 7 — Hardening

- end-to-end encryption option;
- device management;
- load and soak tests;
- privacy controls;
- telemetry review;
- App Store packaging and policy validation.

---

## 20. Work breakdown

### Epic A — Domain and contracts

- Define Capture and CaptureArtifact schemas.
- Define sync envelope and cursor protocol.
- Generate Swift/C# clients.
- Add contract compatibility CI.

### Epic B — Windows ingestion refactor

- Add remote-source fields to queue.
- Implement artifact resolver.
- Implement idempotent ingestion.
- Expand processing stages.
- Add source context to review UI.

### Epic C — Sync API

- Device auth.
- Artifact upload/download.
- Change log.
- Outbox consumers.
- Push notifications.

### Epic D — iOS capture

- Recorder actor.
- local database.
- upload coordinator.
- capture history.
- App Intents/widgets.

### Epic E — Assistant

- session service.
- retrieval adapter.
- streaming audio.
- tool execution.
- source references.

### Epic F — Roadtrip agent

- trip model.
- route context.
- place provider.
- hours/price verifier.
- ranking and cadence.
- navigation handoff.

### Epic G — Security and operations

- key management.
- encryption.
- revocation.
- redacted telemetry.
- deployment documentation.

---

## 21. Key decisions

1. **Windows remains the canonical review and archive client for release one.**
2. **The iOS app never directly accesses the Windows SQLite file.**
3. **A sync/API service is a first-class component, not a thin file-transfer script.**
4. **Captures and artifacts receive device-neutral IDs and hashes.**
5. **All mutations are idempotent and revisioned.**
6. **Original audio is retained unless the user explicitly deletes it.**
7. **Detours require verified route impact and time-sensitive metadata.**
8. **Navigation changes always require user confirmation.**
9. **Driving mode is audio-first and blocks dense editing interactions.**
10. **Self-hosted deployment is supported from the beginning.**

---

## 22. Resolved decisions

Resolved by the owner on 2026-08-07 (originally "open decisions to resolve
before Phase 1"):

1. **Deployment: self-hosted only.** No managed relay. Mode B (§4.1) is
   dropped from the roadmap; the sync service runs on the user's own machine
   or home-lab (Docker), reached over a private network (Tailscale/WireGuard
   or a TLS reverse proxy).

2. **Authentication: no user accounts or logins.** Single-owner identity with
   device pairing, exactly as Phase 1 implemented: a server-held pairing code
   admits devices, each device gets rotating tokens, and any device can be
   revoked. Account recovery = re-pair using the pairing code from the
   server's data directory; no email/password/recovery flows will be built.

3. **Transcription: the Windows machine, by default.** Windows performs
   transcription with its local Whisper pipeline, using GPU/NPU acceleration
   when the hardware and an appropriate model are installed (CPU otherwise).
   The sync service stores and forwards audio; it does not transcribe.

4. **iOS on-device Whisper: yes, as an option.** A small on-device model is
   an optional low-latency path (e.g. immediate rough transcript for the
   assistant); Windows remains the authoritative transcription.

5. **Places/maps: Google preferred, Apple selectable.** Detour/place lookups
   use Google APIs by default with request caps sized to stay inside the free
   tier. When the cap is reached, the app informs the user and offers to fail
   over to Apple (MapKit) for the remainder of the period. Apple is also
   available as a user-selectable primary provider.

6. **CarPlay: design the screens for v1; entitlement deferred.** The app is
   not intended for App Store publication. CarPlay screens are designed and
   validated in the CarPlay Simulator (no entitlement needed there), but
   rendering on a real head unit requires Apple to grant the CarPlay
   entitlement to the signing developer account even for sideloaded personal
   builds — so the in-car experience for v1 is carried by Siri, the Lock
   Screen widget, and the Action Button (none need approval), with the
   "driving task" category entitlement application as an optional later step.

7. **Artifact retention on the sync service: 14 days after Windows
   acknowledges archival, or sooner under storage pressure.** (Implemented:
   the service's retention sweep — `ArtifactRetentionService`, configured by
   `ArtifactRetentionDays` and `ArtifactStorageBudgetBytes` — purges an
   acknowledged artifact's bytes and keeps its row as `purged`, so a late
   download answers 410 rather than a retryable error.)

8. **Pending transcripts and the assistant: excluded by default.** The
   assistant searches approved events only; pending transcripts participate
   only when the user deliberately includes them via an explicit UI option.

9. **Route-context retention: 14 days by default.** Trip location trails
   auto-delete after 14 days; a capture's single location snapshot is
   retained with the memory itself (§14.4 controls still allow deleting it
   independently).

---

## 23. Acceptance criteria for the first usable release

The first production-usable version is complete when:

- The user can start and stop a recording from the iOS app and at least one system control surface.
- A recording made without connectivity is preserved and uploaded later without user repair.
- Repeated upload attempts cannot create duplicate queue or pending-event records.
- The Windows application downloads, verifies, transcribes, and extracts the mobile recording through its normal review workflow.
- iOS displays processing and approval status.
- Authentication tokens and local secrets use platform secure storage.
- The user can revoke a device.
- Location attachment is optional and independently configurable.
- Failure states are visible and actionable on both clients.
- Automated end-to-end tests cover offline capture, resumed transfer, idempotent ingestion, processing, and status synchronization.

The assistant and detour features then build on this reliable capture/sync foundation rather than bypassing it.
