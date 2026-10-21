# Memory Line Sync Service (`MemoryTimeline.SyncApi`)

The self-hosted sync service for the iOS roadtrip companion — deployment
**Mode A** from the system design
([`docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md`](../docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md),
§4.1/§4.2): an ASP.NET Core (net10.0) service that runs on your own machine or
home-lab and brokers captures between mobile devices and the Windows
Memory Timeline app. Single owner, SQLite metadata, filesystem artifact store.

Unlike the WinUI solution, this is a **plain cross-platform .NET project**: it
builds, tests, and runs with the ordinary `dotnet` CLI on any OS (no Visual
Studio `msbuild.exe` required). CI builds it on Linux
([`.github/workflows/sync-api-build.yml`](../.github/workflows/sync-api-build.yml)).

Phase 1 scope (design §19):

- device pairing/registration, self-issued JWT access tokens with refresh
  rotation and revocation;
- idempotent capture metadata ingestion;
- chunked artifact upload with byte-length/part-count/SHA-256 validation, and
  artifact download;
- an append-only change log behind cursor-based `/sync/pull`, per-device
  idempotent `/sync/push`, and `/sync/ack`.

Phase 3 added the **capture status handoff** and the **timeline projection
feed** on top of that change log — no new endpoints, only new change types
(see below).

Phase 4 added the **assistant** endpoints (sessions and turns, routed to a
responder over the same change log) and an idle-session reaper — see
[Assistant](#assistant-design-19-phase-4). Phase 5's **trip and detour**
endpoints are implemented as persistence and lifecycle — see
[Trips and detours](#trips-and-detours-design-19-phase-5); detour
*generation* needs a place provider the service does not ship with.

## Layout

| Path | Contents |
|------|----------|
| `MemoryTimeline.Services.sln` | Solution: the service, its tests, and the shared contracts library. |
| `MemoryTimeline.SyncApi/` | The service — `Domain/` (EF entities), `Application/` (services, options, error codes), `Infrastructure/` (DbContext, artifact store, bootstrapper). |
| `MemoryTimeline.SyncApi.Tests/` | xUnit tests, including `WebApplicationFactory`-based integration tests of the full HTTP surface. |
| `../shared-contracts/dotnet/MemoryTimeline.SyncContracts/` | Wire DTOs shared with the Windows client (referenced by project, camelCase JSON). |

## Run locally

Requires the .NET 10 SDK (the projects target `net10.0`).

```bash
cd services
dotnet build MemoryTimeline.Services.sln
dotnet test  MemoryTimeline.SyncApi.Tests/MemoryTimeline.SyncApi.Tests.csproj
dotnet run --project MemoryTimeline.SyncApi
```

No `launchSettings.json` is checked in, so Kestrel uses its defaults
(`http://localhost:5000`); pass `--urls` to bind elsewhere, e.g.

```bash
dotnet run --project MemoryTimeline.SyncApi --urls http://0.0.0.0:5210
```

On first run the service creates the data directory, generates a token
signing key and a pairing code, and creates the SQLite schema. The pairing
code appears in the startup log and in `{DataDir}/pairing-code.txt`.

### Configuration

All settings live in the `SyncApi` configuration section and can be supplied
via `appsettings.json` or environment variables (`SyncApi__<Name>`); every
value has a working default, so an empty configuration runs.

| Setting | Environment variable | Default | Purpose |
|---------|----------------------|---------|---------|
| `DataDir` | `SyncApi__DataDir` | `data` (relative to the working directory) | Directory holding the SQLite DB, artifact blobs, signing key, and pairing code. |
| `PairingCode` | `SyncApi__PairingCode` | generated on first run | Owner pairing code required by device registration. When configured, it overwrites `{DataDir}/pairing-code.txt`. |
| `AccessTokenLifetimeMinutes` | `SyncApi__AccessTokenLifetimeMinutes` | `60` | JWT access token lifetime. |
| `RefreshTokenLifetimeDays` | `SyncApi__RefreshTokenLifetimeDays` | `30` | Refresh token lifetime. |
| `RefreshTokenGraceSeconds` | `SyncApi__RefreshTokenGraceSeconds` | `300` | How long the previous refresh token stays usable after a rotation, so a client that never received the rotation response can recover. `0` = strict rotation. |
| `MaxPartSizeBytes` | `SyncApi__MaxPartSizeBytes` | `8388608` (8 MiB) | Maximum accepted bytes per uploaded artifact part. |
| `TokenSigningKeyBase64` | `SyncApi__TokenSigningKeyBase64` | generated on first run, persisted to `{DataDir}/signing.key` | Base64 HMAC-SHA256 signing key, at least 32 bytes decoded. |
| `ArtifactRetentionDays` | `SyncApi__ArtifactRetentionDays` | `14` | Days an artifact's bytes are kept after Windows acknowledges archival (a `capture_status` of `completed`) before the retention sweep purges them (design §22 decision 7). |
| `ArtifactStorageBudgetBytes` | `SyncApi__ArtifactStorageBudgetBytes` | `0` (off) | Soft cap on bytes held by complete artifacts; over it, the sweep purges *acknowledged* artifacts oldest-first until back under. Unacknowledged artifacts are never purged for space. |
| `RetentionSweepIntervalMinutes` | `SyncApi__RetentionSweepIntervalMinutes` | `360` | Minutes between retention sweeps; the first runs at startup. |
| `TemporaryFileGraceMinutes` | `SyncApi__TemporaryFileGraceMinutes` | `60` | How long a `*.tmp` part/assembly file may go unwritten before the sweep treats it as crash-orphaned. |
| `AssistantSessionIdleHours` | `SyncApi__AssistantSessionIdleHours` | `24` | Hours without an accepted turn after which an assistant session is reaped (the design doc names no figure). |
| `AssistantReaperIntervalMinutes` | `SyncApi__AssistantReaperIntervalMinutes` | `60` | Minutes between idle-session reaps; the first runs at startup. |

> **Note:** raising `SyncApi:MaxPartSizeBytes` above ~28.6 MB (Kestrel's
> 30,000,000-byte default `MaxRequestBodySize`) additionally requires raising
> Kestrel's request body size limit, or part uploads fail with 413 before the
> service sees them.

## Docker

> **The build context must be the repository root**, not `services/`: the
> image compiles `shared-contracts/dotnet/MemoryTimeline.SyncContracts`
> alongside the service, and that directory lives outside `services/`.

```bash
# From the repository root:
docker build -f services/MemoryTimeline.SyncApi/Dockerfile -t memory-line-sync .

docker run -d --name memory-line-sync \
  -p 8080:8080 \
  -v memory-line-sync-data:/data \
  memory-line-sync
```

- The container listens on port `8080` (the default of the official ASP.NET
  Core 8+ images; the Dockerfile builds on the .NET 10 SDK/ASP.NET images to
  match the projects' `net10.0` target); map it to whatever host port you like
  (`-p 5210:8080`).
- `/data` is the data directory (the image sets `SyncApi__DataDir=/data` and
  declares `VOLUME /data`) — **always mount a volume there** or the
  database, artifacts, signing key, and pairing code vanish with the
  container.
- Read the generated pairing code with
  `docker exec memory-line-sync cat /data/pairing-code.txt`
  (or from `docker logs memory-line-sync`), or pin your own via
  `-e SyncApi__PairingCode=...`.

## Pairing a device

1. Start the service and note the pairing code (startup log, or
   `{DataDir}/pairing-code.txt`).
2. In the Windows app, open **Settings → Sync**, enter the server URL
   (e.g. `http://homeserver:5210`) and the pairing code, and click **Pair**.
   The app registers itself (`POST /api/v1/devices/register`), stores its
   device ID and token pair, and — once sync is enabled — starts the
   background sync worker.
3. The same page can **unpair** this machine, and device revocation
   (`DELETE /api/v1/devices/{deviceId}`) works against *any* device of the
   owner — this is how Windows revokes a lost iPhone. Revoked tokens stop
   working immediately.

The pairing code is compared in constant time; registration is the only
unauthenticated endpoint.

## API surface

Base path `/api/v1`; bearer JWT auth on everything except registration; an
`Idempotency-Key` header on create/mutation calls; camelCase JSON. Full
contract: [`shared-contracts/openapi/memory-line-sync-v1.yaml`](../shared-contracts/openapi/memory-line-sync-v1.yaml).

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/api/v1/devices/register` | Pair a device (pairing code → device ID + access/refresh tokens). Idempotent by `Idempotency-Key`. |
| POST | `/api/v1/devices/{deviceId}/refresh` | Exchange the refresh token for a new access token; the refresh token rotates. |
| DELETE | `/api/v1/devices/{deviceId}` | Revoke a device; its tokens stop working immediately. |
| GET | `/api/v1/devices` | List the owner's devices (additive v1 extension; powers the Windows revocation UI). |
| POST | `/api/v1/captures` | Create capture metadata; idempotent by client-generated `captureId` (replay returns the existing capture). |
| GET | `/api/v1/captures/{captureId}` | Fetch a capture with its artifact summaries. |
| PATCH | `/api/v1/captures/{captureId}` | Update descriptive fields (captureType, titleHint, userNote, tripId); last writer wins. |
| POST | `/api/v1/captures/{captureId}/complete` | Mark the capture fully uploaded. Every declared artifact must be complete (409 `capture_artifacts_incomplete`, retryable, otherwise); a capture still `local_only`/`uploading` advances to `received` and publishes a capture change; one already `received` or beyond is returned unchanged. |
| POST | `/api/v1/captures/{captureId}/retry` | Re-queue a `failed` capture: status back to `received`, revision bumped, capture change with the audio artifact republished for Windows. 409 `capture_not_failed` on any other status; 409 `artifact_purged` when retention has removed the audio. |
| POST | `/api/v1/captures/{captureId}/artifacts/initiate` | Declare an artifact (type, expected byte length, SHA-256) → part size/count plan. |
| PUT | `/api/v1/artifacts/{artifactId}/parts/{partNumber}` | Upload one part; re-uploading a part number replaces it. |
| POST | `/api/v1/artifacts/{artifactId}/complete` | Assemble the parts; validates byte length, part count, and SHA-256 before accepting. |
| GET | `/api/v1/artifacts/{artifactId}/download` | Download the artifact bytes. |
| POST | `/api/v1/sync/push` | Push client outbox entries; idempotent per (device, client sequence) via push receipts. |
| GET | `/api/v1/sync/pull?cursor=&limit=` | Ordered changes after the cursor; the caller's own changes are suppressed. |
| POST | `/api/v1/sync/ack` | Acknowledge the applied cursor (diagnostic — the client's local cursor store is authoritative). |

Errors use the `ApiError` envelope (`code`, `message`, `retryable`,
`correlationId`); `retryable` drives client backoff (design §16.2).

## Capture status handoff (design §19 Phase 3)

The service relays a capture's processing status from Windows to the phone that
recorded it, so the user can follow the whole lifecycle without opening Windows.
It rides the existing change log — there is no new endpoint.

**Direction of flow, one way only:**

```text
Windows queue advances a stage
  → capture_status row in the Windows sync_outbox
  → POST /sync/push                      (LocalOutboxPublisher drains the outbox)
  → sync_changes log + capture_status projection row
  → GET /sync/pull on the originating phone
  → applied to local state; a local notification if it crossed a milestone
```

- **`capture_status` is a change type, not an endpoint.** `entityType` is
  `capture_status`, `entityId` is the `captureId`, `operation` is always
  `upsert`, and `payloadJson` carries a `CaptureStatusChangePayload` —
  status, processing stage, a transcript preview capped at 600 characters, the
  full transcript's character count, pending/approved review counts, and a
  failure reason with its retry classification. Full shape:
  [`shared-contracts/openapi/memory-line-sync-v1.yaml`](../shared-contracts/openapi/memory-line-sync-v1.yaml)
  and
  [`shared-contracts/json-schema/capture-status-payload.v1.json`](../shared-contracts/json-schema/capture-status-payload.v1.json).
- **Windows is the only writer, and nothing here is an instruction.** The
  payload is a read-only projection of Windows-side state; transcript editing
  and event approval stay on Windows. The service does not author status
  either — it only validates, stores, and forwards.
- **The phone still receives it.** Pull suppresses changes published by the
  calling device, keyed on the publishing device, never on the device a capture
  came from — so a phone gets the status Windows published about the phone's own
  captures.
- **One row per capture, latest wins.** `capture_status` holds the current
  state only; the change log keeps the sequence that produced it, and a device
  that pulls receives the payload re-serialized from the stored row, so a pulled
  payload can never disagree with what was persisted. The capture's own
  `status` column is kept in step, so `GET /captures/{captureId}` agrees with the
  latest status change.
- **Validation is per entry, not per request.** A rejected entry comes back in
  the 200 response as `accepted=false` with a code-prefixed `error`, and never
  enters the change log: `validation_error` for a `delete` operation, a
  missing or unparseable payload, a `captureId` that is not a UUID equal to
  `entityId`, a status outside the `local_only | uploading | received |
  processing | review_ready | completed | failed` vocabulary, a
  `transcriptPreview` over 600 characters, or a negative count;
  `capture_not_found` when no capture with that ID belongs to the caller's
  owner. An over-long preview is refused rather than truncated — truncation is
  the publisher's decision, and silently cutting it here would hide a publisher
  bug.
- **Privacy (§14.5).** The preview, the transcript counts, and the failure
  reason are stored but never logged; log lines carry IDs, the status
  vocabulary, and counts only. `failureReason` is fixed publisher text keyed on
  the failure stage, so it cannot leak raw error output, file paths, or
  credentials.
- **No push notifications.** The deployment is self-hosted with no accounts
  (§22.1/§22.2), so there is no APNs certificate and no notification server in
  this design. The phone learns about status by pulling, and posts its own
  local notification when a pull observes a milestone.

## Timeline projections and review decisions (design §19 Phase 3)

The same change log also carries a **read-only projection of the Windows
archive**, so a companion can draw a timeline it does not own: `event`, `era`,
`person` and `pending_event`. One change type travels the other way —
`pending_event_decision`, a companion's approve/reject verdict on an extracted
event. Payload shapes:
[`shared-contracts/dotnet/MemoryTimeline.SyncContracts/TimelineProjectionContracts.cs`](../shared-contracts/dotnet/MemoryTimeline.SyncContracts/TimelineProjectionContracts.cs).

```text
Windows publishes  ──►  event | era | person | pending_event  ──►  every other device
every other device ──►  pending_event_decision                ──►  Windows performs the approval
```

**What `entityId` means — one row's own key, one spelling.** Latest-wins and
tombstoning both key on this string, so it is pinned per type and must be a
UUID in canonical lowercase `"D"` form (`a1b2c3d4-...`). Braces, the `"N"`
form, and upper case are rejected: an upsert of `{A1B2…}` followed by a delete
of `a1b2…` would read as two different rows, the deletion would tombstone
nothing, and the event would stay on the companion's timeline forever.

| `entityType` | `entityId` | Payload |
|--------------|-----------|---------|
| `event` | `eventId` | `EventProjectionPayload` |
| `era` | `eraId` | `EraProjectionPayload` |
| `person` | `personId` | `PersonProjectionPayload` |
| `pending_event` | `pendingEventId` | `PendingEventProjectionPayload` |
| `pending_event_decision` | the `pendingEventId` being decided | `PendingEventDecisionPayload` |

The payload repeats that ID (`eventId`, `eraId`, …) and must agree with
`entityId` as a UUID, or the entry is rejected — a change whose two spellings
disagree would be applied to one row and looked up under another. A decision is
keyed on the review it answers so Windows can match verdict to pending event
without a second identifier.

- **Windows is the only publisher of the four projections.** A push of `event`,
  `era`, `person` or `pending_event` from any other device is rejected with
  `change_not_permitted`, whatever the payload says. A companion holds a
  perfectly valid token — it needs one to upload captures — so nothing else
  stops it writing a memory that never happened into a timeline every other
  device renders. The check is on the pushing device's registered platform, not
  on anything in the payload.
- **`change_not_permitted`, deliberately not `unauthorized`.** The token is
  fine; re-registering or refreshing would produce the same answer. The code
  means *drop this entry from the outbox*, not *re-authenticate*.
- **A decision is the one write a companion may author**, because it is a
  verdict rather than an edit: no field values to merge, and applying it twice
  means what applying it once meant. It must carry `decision` = `approve` or
  `reject` (exact lowercase — an unknown verdict is refused rather than
  defaulting to `approve`, which would write an unreviewed event into the
  timeline), and `decidedByDeviceId` **must be the pushing device**: Windows
  keeps that field as the audit trail of who reviewed what, and a trail a device
  can write another device's name into records fiction. Windows still performs
  the approval, and drops verdicts for reviews it has already resolved.
- **`delete` tombstones a projection; a decision may not be retracted.** A
  deleted event must be able to reach the companion that already drew it, so
  `delete` is accepted for the four projection types and needs no payload (a
  deleted row has nothing left to describe). `delete` on
  `pending_event_decision` is rejected: a verdict is a fact about a review that
  happened, and accepting a retraction would let a device reach back into a
  review Windows may already have completed. Changing one's mind is a new
  decision — which is what a second upsert is.
- **Nothing is stored, and the bytes are relayed unchanged.** Unlike
  `capture_status` and `assistant_turn`, these types add no table: the service
  does not keep a copy of the archive, so there is no row whose
  re-serialization could be more authoritative than the publisher's own
  payload — and passing it through untouched is what lets a newer Windows build
  add a field without this service learning it first. `revision` is therefore
  always `1`; the change ID is the ordering that means anything, and consumers
  apply in cursor order.
- **Validation is per entry** (`accepted=false` with a code-prefixed `error`,
  the rest of the batch unaffected), and rejected entries never enter the change
  log. Beyond the identity rules above: a missing or unparseable payload on an
  upsert; a `datePrecision` outside `exact | day | month | season | year |
  decade | unknown` on `event`, `pending_event`, or a decision's corrections — a
  precision nobody understands is how a consumer ends up inventing an exact day
  for a memory the user dated to a summer; an `era` `colorCode` that is not a
  hex colour (`#RRGGBB`, or `#AARRGGBB` — the contract says the former, the
  Windows column can hold the latter, and losing a whole era over an alpha
  channel would cost more than the stricter check buys); a `person`
  `mergedIntoId` that is not a UUID (consumers are
  expected to follow it); a negative `mediaCount` or `eventCount`; a
  `confidenceScore` outside 0–1; and a `pending_event` `transcriptPreview` over
  600 characters — the same §14.5 bound as the capture status preview, since
  both are excerpts of the same recording leaving the machine that holds it, and
  refused rather than trimmed for the same reason.
- **Pull is unchanged.** The entity-type allow-list gates `POST /sync/push`
  only; `GET /sync/pull` returns whatever is on the owner's log, so these types
  need no pull-side change and echo suppression treats them like everything
  else — a device receives its peers' projections and decisions, never its own.

## Assistant (design §19 Phase 4)

Five endpoints under `/api/v1/assistant` (contract v1.3): open and read a
session, submit a turn, poll a turn, cancel a turn. **The service stores and
routes; it never answers.** Windows is the brain — the archive, the retrieval
index and the prompts live in `MemoryTimeline.Core` — so a submitted question
is persisted as a `pending` turn and published as an `assistant_turn` change on
the feed; whichever responder claims it pushes the answered turn back through
`POST /sync/push`, and the asking device reads the answer either by polling
`GET /assistant/turns/{turnId}` or off its own pull loop. Streaming rides the
feed as ordered `assistant_turn_chunk` changes.

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/api/v1/assistant/sessions` | Open a conversation (preferred responder, optional surface/mode/trip). Only the opening device may use it. |
| GET | `/api/v1/assistant/sessions/{sessionId}` | Read a session (another device's session is a plain 404). |
| POST | `/api/v1/assistant/sessions/{sessionId}/turns` | Ask; idempotent on the client `turnId` (a replay returns the stored turn, answer included, with 200). |
| GET | `/api/v1/assistant/turns/{turnId}` | Poll a turn; `result` is present once completed. |
| POST | `/api/v1/assistant/turns/{turnId}/cancel` | Cancel; terminal and published so a responder mid-answer stops. A late answer for a cancelled turn is discarded. |

- **Responder modes** (`windows` default, `provider`, `on_device`) are chosen
  per turn; the service records which one actually answered and every result
  carries a `grounding` value (`archive`, `client_context`, `none`).
- **Idle sessions are reaped.** `AssistantSessionReaper` runs at startup and
  every `AssistantReaperIntervalMinutes`: a session with no accepted turn for
  `AssistantSessionIdleHours` is deleted with its turns, and any turn still
  waiting on a responder is first marked `cancelled` and published with no
  source device — so both the asker and a responder that wakes later see it,
  and a late answer is dropped by the same terminal-state guard an explicit
  cancel relies on. Questions and answers are personal memory content
  (§14.5); a day-old conversation has no reason to stay on the service.
- **What is not here: the Windows responder.** Nothing in this repository yet
  claims an `assistant_turn` off the feed and answers it — `MemoryTimeline.Sync`
  pulls and publishes capture status and projections, but has no assistant
  responder. Until it exists, a turn asked from a phone or the Mac stays
  `pending` and is eventually reaped. The Windows client's responder is
  tracked separately.

## Retention sweep (design §22 decisions 7 and 9)

A background sweep (`RetentionSweepHostedService` → `ArtifactRetentionService`)
runs at startup and every `RetentionSweepIntervalMinutes`:

- **Artifact bytes are purged 14 days after Windows acknowledges archival**, or
  sooner under storage pressure. "Acknowledged" means the capture's latest
  Windows-authored `capture_status` is `completed`; the clock runs from the
  moment the service received that status. Captures still `processing`,
  `review_ready` or `failed` keep their audio indefinitely — Windows may need to
  download it again, and a failed capture may be retried.
- **Purge is a state, not a deletion.** The artifact row survives with
  `state = purged` and its measured `byteLength`/`sha256`, so a late
  `GET /artifacts/{id}/download` answers **410 `artifact_purged`** (never
  retryable) instead of a retryable 500 a client would hammer forever. The row
  is marked before the blob is deleted, so a crash between the two leaves a
  blob the next sweep removes rather than a "complete" row with no bytes.
- **Storage pressure** (`ArtifactStorageBudgetBytes` > 0): acknowledged
  artifacts are purged oldest-acknowledgement-first until the store is under
  budget; the sweep logs a warning if what remains over budget belongs to
  captures Windows has not acknowledged, because those are never purged.
- **Crash-orphaned temporary files** — the per-request `*.tmp` files part
  uploads and assemblies write through — are deleted once they have gone
  unwritten for `TemporaryFileGraceMinutes`. In-process failures already clean
  up after themselves; this covers a process that died mid-write.

## Security notes (design §14)

- **Auth model:** registration is gated by the owner pairing code; every
  other call requires a short-lived, device-bound, self-issued HMAC-SHA256
  JWT. Refresh tokens rotate on every use — the previous token stays usable
  only for the short `RefreshTokenGraceSeconds` recovery window (`0` for
  strict rotation) — and revocation takes effect immediately.
- **Transport:** the service itself speaks **plain HTTP**. Do not expose it
  to the public internet directly — reach it over a private overlay network
  (**Tailscale/WireGuard recommended**, per the design's Mode A) or put a
  TLS-terminating reverse proxy (Caddy/nginx/Traefik) in front of it.
- **Secrets:** the signing key lives at `{DataDir}/signing.key` unless
  provided via `SyncApi__TokenSigningKeyBase64` (prefer a secret manager for
  real deployments). Deleting/rotating the key invalidates all outstanding
  access tokens. Protect the data directory — it contains personal audio.
- **Logging (§14.5):** logs are structured and carry IDs, counts, and error
  codes only — never transcripts, notes, audio bytes, tokens, or precise
  location.

## Where data lives

```text
{DataDir}/                 # SyncApi__DataDir, default ./data
├── sync.db                # SQLite: owners, devices, captures, capture_artifacts,
│                          #   capture_status (latest Windows-authored status per capture),
│                          #   sync_changes (change log), push_receipts, idempotency_records
├── artifacts/             # filesystem blob store (parts during upload, assembled artifacts after complete;
│                          #   blobs of purged artifacts are gone, their rows stay with state=purged)
├── signing.key            # base64 HMAC token signing key (generated if not configured)
└── pairing-code.txt       # current owner pairing code
```

Back up `{DataDir}` as a unit; `sync.db` and `artifacts/` reference each
other.

**Pre-release schema note:** the service creates its schema with EF
`EnsureCreated` — there are no migrations yet. When upgrading a pre-release
deployment across a schema change, stop the service and delete `sync.db`
(paired devices re-register with the pairing code; completed artifacts under
`artifacts/` are content-addressed by ID and can be removed too). Proper
migrations are planned before any stable release.

> The Phase 3 `capture_status` table is the one exception so far: because
> `EnsureCreated` leaves an existing database untouched, startup issues an
> idempotent `CREATE TABLE IF NOT EXISTS` for it. Upgrading a Phase 1/2
> deployment to Phase 3 therefore does **not** require deleting `sync.db`. That
> is a stopgap, not a migration system — the next schema change still needs
> real migrations.
