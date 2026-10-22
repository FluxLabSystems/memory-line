# Memory Line — macOS

A native **SwiftUI** macOS app. It pairs with the sync service, records and uploads
captures (with input-device selection and a menu-bar quick capture), pulls the change feed on
a timer, renders the read-only archive projection Windows publishes (timeline, eras, people,
search, review queue), sends approve/reject verdicts, and asks the assistant through the
service. See [`docs/design/MACOS-PORT-PLAN.md`](../docs/design/MACOS-PORT-PLAN.md) for the
plan, the decisions behind it, and the open questions.

**Platform:** macOS 14 (Sonoma) or later
**Toolchain:** Xcode 16+ (the project uses `objectVersion = 77` synchronized folder groups)
**Status:** Port-plan phases 0–4 are in the tree (skeleton, Keychain + sync pull, upload,
capture, timeline projection, review decisions); phase 5's Ask pane is a thin client over the
service's assistant endpoints and waits on a Windows responder that is not built yet; phase 6
(packaging) is not started. Builds and tests green in CI; not runtime-validated against a live
sync server.

---

## Opening it

```bash
open macos-native/MemoryLineMac.xcodeproj
```

Build and run the `MemoryLineMac` scheme. There is no `.xcworkspace` and no package
resolution step — the project has no external dependencies.

---

## How code is shared with the iOS companion

The Mac app does not have its own domain, networking, persistence, or Keychain layer. It
compiles the iOS companion's:

```
ios-companion/MemoryLineCompanion/Shared/
├── Domain/         CaptureRecord, lifecycle states, settings keys, protocol seams
├── Networking/     SyncAPIClient + the v1 wire DTOs (captures, sync, projections, assistant)
├── Persistence/    SQLiteDatabase, SQLiteCaptureStore, SQLiteSettingsStore
├── Security/       KeychainTokenStore
├── Support/        AppLog, AudioStorage, FileHasher, CaptureStatusStore (+ two iOS-only files)
└── Timeline/       the read-only archive projection: models, SQLite store, applier, colours
```

That directory is referenced from `MemoryLineMac.xcodeproj` as an Xcode **synchronized
folder group**, so its `.swift` files compile straight into the Mac app target. Adding a
file there picks it up automatically in both apps — there is no file list to update.

Two files in `Shared/Support/` are iOS-only and explicitly excluded from this target:

| File | Why |
|---|---|
| `ConfirmationFeedback.swift` | UIKit haptics |
| `WidgetStatusPublisher.swift` | WidgetKit bridge for the iOS widget extension |

The exclusions live in the project's `PBXFileSystemSynchronizedBuildFileExceptionSet`.

### Why a synchronized folder and not a Swift package

Every type in `Shared/` is `internal`. A package would mean adding `public` to roughly 50
declarations across a shipping iOS app for no immediate benefit. The synchronized folder
gets real sharing with zero changes to the iOS project. The trade and the conditions for
revisiting it are written up in the port plan, §3.1.

**If Xcode does not resolve the Shared group on first open** (its path escapes the project
directory, which is unusual), delete the group and drag
`ios-companion/MemoryLineCompanion/Shared` back in as a folder reference. Nothing else in
the repository depends on that reference working.

---

## Layout

```
macos-native/
├── MemoryLineMac.xcodeproj/
├── Config/
│   ├── MemoryLineMac-Info.plist       ATS, memoryline:// URL scheme, termination policy
│   └── MemoryLineMac.entitlements     sandbox, network client, mic, Keychain group
├── MemoryLineMac/
│   ├── App/
│   │   ├── MemoryLineMacApp.swift     @main; window, menu-bar and Settings scenes,
│   │   │                              File ▸ New Recording (⌘N), Sync Now (⌘R), deep links
│   │   ├── MacAppEnvironment.swift    composition root (mirrors the iOS AppEnvironment)
│   │   └── RootView.swift             sidebar navigation
│   └── Features/
│       ├── Ask/        AskView, MacAskCoordinator, AskSeams — questions to the service, answers from Windows
│       ├── Capture/    CaptureView, MacAudioRecorderService (AVAudioEngine), MacAudioDeviceEnumerator,
│       │               MenuBarCapture, CaptureSeams
│       ├── Eras/       ErasView (projection)
│       ├── Library/    LibraryView — this Mac's captures, upload state, Windows-authored status
│       ├── People/     PeopleView (projection)
│       ├── Review/     ReviewView, MacReviewCoordinator, ReviewDecisionStore, ReviewDecisionSeams
│       ├── Search/     SearchView (projection)
│       ├── Sync/       MacSyncCoordinator (pull → apply → ack), MacUploadCoordinator (drain), SettingsScene
│       └── Timeline/   ArchiveTimelineView (projection)
└── MemoryLineMacTests/   environment, sync, upload and review coordinator tests
```

`MacAppEnvironment` builds its own recorder and uploader rather than sharing the iOS
ones: `AudioRecorderService` and `UploadCoordinator` are built on `AVAudioSession` and
`BGTaskScheduler`, neither of which exists on macOS. `MacAudioRecorderService` records on
`AVAudioEngine` so the input device can be chosen per process, and `MacUploadCoordinator`
drains pending uploads on a foreground loop; every finalised recording enters it at once.
Their persistence discipline and request sequences mirror the iOS types exactly.

`MacSyncCoordinator` replaces the iOS `StatusSyncCoordinator`. It is a reimplementation
rather than shared code — that one needs `BackgroundTasks` and reconciles against capture
records the phone created — but the pull/ack semantics are a server contract, so they are
mirrored exactly and pinned by tests. Both apply the timeline projection through the shared
`TimelineProjectionApplier`.

The Mac applies `capture_status` and the projection but ignores `capture` and
`capture_artifact` changes: recordings made on the phone go to Windows for processing and
never appear in this Mac's Library, which lists only what this Mac recorded.

---

## Relationship to the other apps

|  | Role |
|---|---|
| **Windows Native** (`windows-native/`) | The primary product. Owns extraction, review, and approval. |
| **iOS companion** (`ios-companion/`) | Capture device. Records, uploads, shows Windows-authored status. |
| **macOS** (this) | Intended as a full peer of the Windows app, not a companion. Today it captures, uploads, reads the archive projection, sends review verdicts and asks the assistant; extraction, editing and approval still run on Windows. |

All three meet at the self-hosted sync service (`services/`), never at a shared filesystem.

---

## Testing

```bash
cd macos-native
xcodebuild test -project MemoryLineMac.xcodeproj -scheme MemoryLineMac -destination 'platform=macOS'
```

`MemoryLineMacTests` covers only what macOS adds or overrides — the shared stores and sync
client are already covered by `ios-companion/MemoryLineCompanionTests`, and running the
same tests against the same sources twice buys nothing.

CI: `.github/workflows/macos-native-build.yml`.
