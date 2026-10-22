# Memory Line iOS Roadtrip Companion

Phase 2 capture MVP (design doc `docs/design/IOS-ROADTRIP-COMPANION-SYSTEM-DESIGN.md`,
§19 Phase 2): one-touch recording, durable offline queue, background upload to the
self-hosted sync service, capture history/status, Lock Screen + Home Screen widgets,
and App Intents for Siri / Shortcuts / the Action Button.

Plus Phase 3 (§19 Phase 3): the processing status Windows reports back, so a capture's
whole lifecycle is readable from the phone — see
[Processing status from Windows](#processing-status-from-windows-phase-3) below.

No external dependencies — system SQLite, CryptoKit, AVFoundation, URLSession only.
Nothing to resolve on first open.

## Building (Mac mini)

1. Requirements: Xcode 16 or newer (the project uses the Xcode 16
   filesystem-synchronized project format), iOS 17+ deployment target.
2. Open `ios-companion/MemoryLineCompanion.xcodeproj`.
3. Select the `MemoryLineCompanion` target → Signing & Capabilities → choose your
   team (a free personal team works). Xcode will register the bundle IDs and the
   `group.ca.fluxology.memoryline.ios` App Group automatically. Do the same for the
   `MemoryLineWidgets` target.
   - If your team can't use that exact group ID, change it consistently in both
     targets' capabilities AND in `Config/*.entitlements`, plus the
     `WidgetSharedState.appGroupId` constants in
     `MemoryLineCompanion/Shared/Domain/Models.swift` and
     `MemoryLineWidgets/WidgetSharedState.swift`.
4. Build/run the `MemoryLineCompanion` scheme on your iPhone.
   - Free personal teams: apps expire after 7 days and need re-deploying; a paid
     developer account signs for a year.

## Pairing with the sync service

1. Run the service (see `services/README.md`); note the pairing code from its
   startup log or `{DataDir}/pairing-code.txt`.
2. In the app: Settings → enter the server URL (e.g. `http://mac-mini.tailnet:8080`
   or wherever the service listens) and the pairing code → Pair.
3. Record something. It saves locally first, then uploads when connectivity allows;
   the Windows app pulls, ingests, and it shows up in review exactly once.

Plain HTTP is allowed by the app's transport policy because the service is designed
to run on a private overlay network (Tailscale/WireGuard, design §14.2). Put TLS in
front if you expose it any other way.

## Processing status from Windows (Phase 3)

After a capture uploads, the PC does the work — download, verify, transcribe,
extract, review. Phase 3 brings that progress back to the phone so you can see
where a capture is without walking over to Windows.

**Direction of flow, one way only:**

```text
Windows queue advances a stage
  → capture_status row in the Windows sync outbox
  → POST /sync/push
  → the service's change log
  → GET /sync/pull here  (Settings → Processing updates → Check for updates,
                          after an upload pass, or a background refresh)
  → local state + a local notification on a milestone
```

What the phone shows: one fused lifecycle position (Saved → Uploading → Uploaded →
Processing → Ready for review → Done, or an upload/processing failure), where the
recording currently exists (this iPhone / your sync server / your PC), a transcript
**preview** with the full transcript's length, how many extracted events are waiting
for review versus already approved, and a plain-language reason when Windows could
not process it.

**The boundary this phase makes legible: editing and approval stay on Windows.**
Everything arriving over `capture_status` is a read-only projection of Windows-side
state. The phone never authors it, never edits a transcript, and never approves an
event — it reads.

**Notifications are local, not push.** The deployment is self-hosted with no
accounts (design §22.1/§22.2), so there is no APNs certificate and no notification
server anywhere in this design. The phone posts its own `UNUserNotification` the
moment a pull observes a capture becoming review-ready, completing, or failing.
The practical consequence: **updates arrive when the app pulls**, not the instant
Windows finishes — foreground, after an upload pass, or on an iOS-scheduled
background refresh, whose timing iOS decides. Authorization is requested lazily on
the first pull after pairing, never at first launch, and `Settings → Processing
updates → Notification settings` links to the system switch.

### What Phase 3 deliberately does not do

- **The full transcript stays on Windows.** The phone receives a leading excerpt of
  at most 600 characters plus a character count, not a replica. There is no phone
  view of the complete text and no local copy of it.
- **The phone cannot edit or approve.** No transcript editing, no event approval or
  rejection, and no way to restart Windows-side processing — a capture that failed
  on the PC is retried on the PC. (The retry button in history is a different
  thing: it retries this phone's own *upload*, which is the phone's job.)
- **Progress does not rewind on the phone.** A pulled status can advance a
  capture's stored lifecycle state but never move it back, so a replayed or
  out-of-order change is harmless — applying one twice is a no-op. A reported
  failure is the one transition allowed to reset that floor, so a Windows-side
  retry can move the capture forward again.
- **Nothing is streamed back from the PC.** History plays the local recording (that
  is Phase 2's audio file, still on the phone); the design's Phase 3 line about
  "playback of the original audio" is satisfied locally, and there is no download
  path for the Windows-side normalized artifact.

## Layout

```text
ios-companion/
├── MemoryLineCompanion.xcodeproj      # hand-authored, 3 targets, synchronized folders
├── Config/                            # Info.plists + entitlements (outside synced folders)
├── MemoryLineCompanion/               # app target (folder-synchronized)
│   ├── App/                           # entry point, environment/DI, root view
│   ├── Features/Capture|History|Settings|Sync|Timeline/
│   ├── Shared/Domain|Persistence|Networking|Security|Support|Timeline/
│   │                                  # compiled into the macOS app too (macos-native/README.md)
│   ├── AppIntents/                    # Siri / Shortcuts / Action Button intents
│   └── Assets.xcassets
├── MemoryLineWidgets/                 # widget extension target
└── MemoryLineCompanionTests/          # unit tests (XCTest, app-hosted)
```

## Not in this phase

Assistant, trips/detours, CarPlay screens (design phases 4–6) have no UI on the
phone. The sync service implements the assistant endpoints (the macOS app has an Ask
pane over them) and the trip/detour endpoints; the phone does not call either yet.
Location is never attached to a capture (`locationContext` is sent as null); the
opt-in snapshot design §14.4 describes is not built. Per the resolved §22 decisions,
CarPlay will be simulator-designed later — rendering on a real head
unit needs an Apple-granted entitlement even for sideloaded builds; Siri, the Lock
Screen widget, and the Action Button carry the in-car experience meanwhile.
