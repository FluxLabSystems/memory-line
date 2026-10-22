# Shared Contracts

Device-neutral API and data contracts shared by the Windows Native app, the iOS
companion, and the sync service. Source of truth:
[`docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md`](../docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md)
(sections 6, 11, and 12).

## What lives here

| File | Contract |
|------|----------|
| [`openapi/memory-line-sync-v1.yaml`](./openapi/memory-line-sync-v1.yaml) | Memory Line Sync API v1 (OpenAPI 3.0.3): devices, captures, artifacts, sync push/pull/ack (including the timeline projection payloads), assistant, trips, detours. |
| [`json-schema/capture-envelope.v1.json`](./json-schema/capture-envelope.v1.json) | JSON Schema (draft 2020-12) for the remote capture ingestion envelope — the payload handed to Windows ingestion (`RemoteCaptureEnvelope` in `MemoryTimeline.Core`). |
| [`json-schema/capture-status-payload.v1.json`](./json-schema/capture-status-payload.v1.json) | JSON Schema (draft 2020-12) for the `capture_status` change payload — the Windows-authored processing status the phone reads (`CaptureStatusChangePayload`). |
| [`json-schema/assistant-turn-payload.v1.json`](./json-schema/assistant-turn-payload.v1.json) | JSON Schema (draft 2020-12) for the `assistant_turn` change payload — one voice-assistant turn as it rides the feed between the asking device and its responder (`AssistantTurnChangePayload`). |

## Versioning rules

- **v1 evolves only additively.** New optional fields, new endpoints, and new
  enum values may be added to the existing `*-v1` files; existing fields,
  endpoints, required-ness, and enum values are never renamed, removed, or
  repurposed.
- **Breaking changes mean a new versioned file** (e.g.
  `memory-line-sync-v2.yaml`, `capture-envelope.v2.json`) served alongside v1
  during a migration window — never an in-place edit of v1.
- **Clients must tolerate unknown fields.** Deserializers on every platform
  ignore properties they do not recognize, so an older client keeps working
  against a newer additive server.

### Reserved entity types

Two values of the `entityType` enums (`SyncChange`, `SyncPushEntry`, and the
`SyncChangeEntityType` constants) are **reserved**: `recording_queue` and
`capture_artifact`. The Phase 0 contract set them aside for the Windows queue
row and the artifact metadata as change-feed entities, and the capture and
artifact REST endpoints then came to carry that information instead. Nothing
shipped publishes either value and nothing consumes one: the service accepts
them on push (a client built against the v1 enum must not be refused) and
relays them on pull, but the Windows `RemoteChangeApplier` applies only the
types it knows and skips the rest, and the phone and the Mac apply
`capture_status` and the timeline projections only. They stay in the enums
because v1 never removes a value (above). Treat them as reserved, not as a
channel: publishing one needs a documented payload schema, a consumer and a
changelog entry first — an ordinary additive v1.x change.

## Client generation

The C# wire types live in
[`dotnet/MemoryTimeline.SyncContracts`](./dotnet/MemoryTimeline.SyncContracts)
and are used **verbatim** by both the sync service
(`services/MemoryTimeline.SyncApi`) and the Windows sync client
(`windows-native/src/MemoryTimeline.Sync`), serialized camelCase via
System.Text.Json web defaults. The OpenAPI file **mirrors those shipped DTO
wire shapes**, so a client generated from the YAML (e.g. Swift for the iOS
companion) interoperates with the shipped service. Hand-written types
elsewhere (e.g. `RemoteCaptureEnvelope`, the Swift DTOs) must likewise mirror
these files exactly.

## The contract-compatibility gate

The drift between the YAML and the DTOs is now caught by a test, not by
review: `ContractCompatibilityTests` in
`services/MemoryTimeline.SyncApi.Tests` runs with the rest of the service's
suite (and therefore in the `sync-api-build.yml` workflow on every pull
request) and asserts, against the YAML copied into the test output:

- every `SyncChangeEntityType` constant appears in the `entityType` enums of
  both `SyncChange` and `SyncPushEntry`, and nothing unknown does;
- every public DTO in `MemoryTimeline.SyncContracts` maps to a schema whose
  property names equal the DTO's camelCased properties, in both directions —
  a DTO without a schema, a schema property without a DTO property, or a new
  DTO missing from the test's type→schema map all fail;
- every path+method the document describes is routed by the running service
  (it would 404 otherwise), and every route the service registers is
  documented;
- `info.version` is semver and this changelog carries a heading for its
  major.minor.

The rule is therefore mechanical: **change the DTOs and the YAML in the same
commit**, add the changelog entry when `info.version` moves, and the gate
proves the three stayed together.

## Changelog

### v1.5 (2026-10-22) — capture location and the `macos` platform

Purely additive. Lands together with the `info.version` bump to 1.5.0 in
`openapi/memory-line-sync-v1.yaml` and the matching DTO changes in
[`CaptureContracts.cs`](./dotnet/MemoryTimeline.SyncContracts/CaptureContracts.cs)
and [`DeviceContracts.cs`](./dotnet/MemoryTimeline.SyncContracts/DeviceContracts.cs);
the shipped DTOs are the implemented truth, as before.

- **`location` on `CaptureResponse` and `CaptureChangePayload`.** The single
  location snapshot a capture may carry — the `LocationContext` schema
  (`latitude`, `longitude`, optional `horizontalAccuracyM`, `headingDegrees`,
  `speedMps`) that `CaptureCreateRequest.locationContext` has accepted since
  v1.0 — is now returned on the capture and relayed on its change payload so
  Windows can keep it with the memory. Nullable, and null when the phone sent
  none: the iOS companion attaches one only when its location opt-in is on
  (design §14.4, off by default), coarse unless the precise option is also on,
  and never logs it (§14.5).
- **`macos` joins the device `platform` enum** on `DeviceRegisterRequest` and
  `DeviceInfo`. The Mac app registers as `macos` instead of `other`
  (`docs/design/MACOS-PORT-PLAN.md` §3.4); `other` remains valid, and a Mac
  paired before this change keeps the `other` its device row was stored with.

### v1.4 (2026-10-21) — the YAML catches up with the shipped service

Purely additive. Nothing that existed was renamed, removed or made required
in a request; the one tightening is on the trip/detour schemas that never had
a client (see the last bullet).

- **Three `entityType` values the DTOs already carried are now in both enums**:
  `era`, `person` and `pending_event_decision`. They had shipped in
  `SyncChangeEntityType` with the Phase 3 timeline projection and were being
  served and accepted by the service while the YAML still listed eight
  values.
- **Six schemas for the timeline projection payloads**, mirroring
  [`dotnet/MemoryTimeline.SyncContracts/TimelineProjectionContracts.cs`](./dotnet/MemoryTimeline.SyncContracts/TimelineProjectionContracts.cs)
  property for property: `EventProjectionPayload`, `EraProjectionPayload`,
  `PersonProjectionPayload`, `PendingEventProjectionPayload`,
  `PendingEventDecisionPayload` and `PendingEventCorrections`. The
  `payloadJson` descriptions on `SyncChange` and `SyncPushEntry` name which
  payload each entity type carries, and `SyncPushEntry.entityType` states the
  authority rule the service enforces (only Windows publishes the four
  projections; a decision is the one companion-authored write).
- **`ArtifactSummary.state` gains `purged`** and
  `GET /artifacts/{artifactId}/download` gains **410 `artifact_purged`**: the
  service's retention sweep removes an artifact's bytes 14 days after Windows
  acknowledges archival (design §22 decision 7), keeping the metadata row. A
  client that only downloads `complete` artifacts needs no change.
- **`POST /captures/{captureId}/complete` and `/retry` describe what they do
  now** instead of "Phase 1 behavior": complete refuses with 409
  `capture_artifacts_incomplete` (retryable) until every declared artifact
  is complete, and advances a `local_only`/`uploading` capture to `received`;
  retry re-queues a `failed` capture and refuses anything else with 409
  `capture_not_failed` (a new response on that path) or 409 `artifact_purged`.
- **Trips and detours are implemented**, so the eight operations that were
  documented but unrouted now answer. Additively: `GET /trips` and
  `GET /trips/{tripId}` (a trip nobody could read back was not usable), a
  200 replay response on `POST /trips/{tripId}/route-context`, and
  `createdAtUtc`/`updatedAtUtc` on `Trip`. The state machines the service
  enforces are written into the operation descriptions, and the `Conflict`
  and `NotFound` responses list the trip/detour codes.
- **The trip/detour schemas now mirror shipped DTOs**
  ([`TripContracts.cs`](./dotnet/MemoryTimeline.SyncContracts/TripContracts.cs)):
  optional properties are marked `nullable: true`, because responses do not
  strip nulls, and `RouteContext.routeContextId`/`tripId` are documented as
  optional on write and present on read. These schemas had no client and
  were a sketch; this is the v1.1 precedent again — the shipped DTOs are the
  implemented truth.
- **The contract-compatibility gate exists** (see above); the "still planned"
  note is gone.

### v1.3 (2026-08-07) — assistant sessions and turns

Added the Phase 4 voice assistant (design doc §19 Phase 4). Additive to
everything outside the assistant, but NOT additive within it — see the note at
the end.

- **Five endpoints** under `/assistant`: create and read a session, post a turn,
  poll a turn, cancel a turn. Cancellation is explicit rather than implied,
  because "interruption and cancellation" is a stated Phase 4 requirement.
- **Two change types**, `assistant_turn` and `assistant_turn_chunk`, added to
  the `entityType` enum on both `SyncChange` and `SyncPushEntry`. Unlike every
  other entity type these flow in BOTH directions over one feed: the service
  publishes a pending turn toward Windows, and Windows publishes the answered
  turn back. Consumers therefore apply by revision rather than assuming a
  direction.
- **Nine schemas**, mirrored by
  [`dotnet/MemoryTimeline.SyncContracts/AssistantContracts.cs`](./dotnet/MemoryTimeline.SyncContracts/AssistantContracts.cs)
  and, for the change payload, by
  [`json-schema/assistant-turn-payload.v1.json`](./json-schema/assistant-turn-payload.v1.json).

**The architecture this encodes.** Windows is the brain by default: the
archive, the retrieval index and the extraction prompts live in
`MemoryTimeline.Core`, and Phase 3 already made Windows the only writer. The
service therefore stores and routes but never retrieves or generates. Two
alternatives are first-class, because a companion whose answers stop when the
PC sleeps is not much of a companion: `provider` (the service calls an LLM
directly) and `on_device` (the phone pre-processes and supplies context). Every
result carries a `grounding` value so a client can never pass general knowledge
off as the user's own history. Responder is chosen per turn, not per session,
so a client can fall back mid-conversation.

**Streaming rides the feed as ordered chunks**, not a long-lived connection:
the phone's transport is already a pull loop that survives suspension and
network changes. A responder that cannot stream publishes no chunks and the
client renders the final result.

**Not additive within the assistant block.** v1.2 carried a speculative
assistant design sketched from design doc §6.6 that had never been implemented.
It could not coexist with the shipped DTOs — YAML cannot hold two
`AssistantTurnResponse` keys — so it was reconciled, following the v1.1
precedent that the shared DTOs are the implemented truth. `mode`, `tripId` and
the audio artifact ids from that sketch are retained; the SSE streaming
endpoint is not, having been replaced by feed-carried chunks. No client existed
against the removed shapes.

### v1.2 (2026-08-07) — `capture_status`

Added the Phase 3 processing-status handoff (design doc §19 Phase 3), purely
additively: no existing field, endpoint, or enum value changed.

- **New change type `capture_status`**, added to the `entityType` enum on both
  `SyncChange` and `SyncPushEntry`. Its `entityId` is the `captureId`, so a
  capture has exactly one status entity, and only `upsert` is accepted — a
  latest-wins projection has nothing to tombstone.
- **New payload schema `CaptureStatusChangePayload`** in the OpenAPI document,
  mirrored by
  [`json-schema/capture-status-payload.v1.json`](./json-schema/capture-status-payload.v1.json)
  and by the shipped DTO
  [`dotnet/MemoryTimeline.SyncContracts/CaptureStatusContracts.cs`](./dotnet/MemoryTimeline.SyncContracts/CaptureStatusContracts.cs).
  Required on the wire: `captureId`, `status`, `updatedAtUtc`,
  `transcriptAvailable`; everything else is nullable and — because payloads are
  serialized with camelCase web defaults and no null stripping — is present as
  `null` rather than absent. `transcriptPreview` is bounded at **600
  characters** (`TranscriptPreviewMaxChars`).
- **Direction of flow is one-way and documented as such**: Windows authors the
  status into its own sync outbox → `POST /sync/push` → the service change log
  → `GET /sync/pull` on the capture's phone. Echo suppression keys on the
  *publishing* device, so a phone still receives status about its own captures.
  Nothing in the payload is an instruction; editing and approval stay on
  Windows.
- **Documented the push-time validation errors.** They are per-entry results
  (HTTP 200 with `accepted=false` and a code-prefixed `error`), not HTTP
  errors: `validation_error` for a delete operation, a missing/unparseable
  payload, a `captureId` that is not a UUID equal to `entityId`, a status
  outside the vocabulary, a preview over 600 characters, or a negative count;
  `capture_not_found` when no capture with that ID belongs to the caller's
  owner. An over-long preview is refused rather than truncated, so truncation
  stays the publisher's decision.

### v1.1 (2026-08-07)

Reconciled `openapi/memory-line-sync-v1.yaml` with the wire protocol
implemented in Phase 1 (the shared DTOs are the implemented truth):

- **Sync**: push carries `entries` of `SyncPushEntry` and returns per-entry
  `SyncPushEntryResult` receipts (`clientSequence`/`accepted`/`duplicate`/
  `serverChangeId`/`error`); pulled `SyncChange` carries `changeId`,
  `revision`, `sourceDeviceId`, and the payload as a JSON **string**
  (`payloadJson`, documented by the `CaptureChangePayload` schema incl.
  `audioArtifact`); every cursor (`cursor` query param, `nextCursor`, ack
  `cursor`, `changeId`) is `integer`/`int64`, not string.
- **Devices**: registration requires `pairingCode`, `platform`, and
  `displayName` (`publicKey` is optional); the response gains `ownerId`;
  refresh takes a `TokenRefreshRequest` body, returns a `TokenRefreshResponse`
  with a rotated refresh token, and answers 401 — never 404 — for unknown or
  revoked devices (anti-enumeration); added `GET /devices` returning
  `DeviceInfo` entries.
- **Artifacts**: initiate response is `{ artifactId, partSizeBytes,
  expectedPartCount }`; complete takes `{ partCount, totalByteLength, sha256 }`
  and returns `ArtifactCompleteResponse`; `ArtifactSummary` replaces the old
  `CaptureArtifact` schema; documented the server artifact lifecycle
  (`pending | uploading | complete | failed`, distinct from the Windows-local
  `upload_state` vocabulary) and the actually-emitted 409/413/422 responses
  with their error codes (`artifact_conflict`, `artifact_incomplete_parts`,
  `artifact_part_too_large`, `artifact_*_mismatch`, `artifact_not_complete`,
  `artifact_not_found`).
- **Captures**: `CaptureCreateRequest` gains optional `titleHint` and
  `userNote`; `POST /captures` documents the 200 idempotent-replay response;
  the `Capture` response schema now matches the `CaptureResponse` DTO
  (embedded `artifacts` summaries).
- **Idempotency-Key** is now documented as optional everywhere — an opt-in
  replay mechanism for authenticated mutations; sync push idempotency is
  carried by `clientSequence` receipts instead of the header.
