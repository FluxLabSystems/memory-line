import XCTest

// @testable, not a plain import: every type under test is `internal` — the
// shared sources from ios-companion compile into the app module without any
// `public` annotations, which is the whole reason the synchronized-folder
// approach was chosen over a Swift package (port plan §3.1).
@testable import MemoryLineMac

/// Tests for the macOS-specific parts of the composition root. The shared
/// stores and sync client are covered by the iOS companion's own suite
/// (`ios-companion/MemoryLineCompanionTests`); duplicating that here would test
/// the same source twice, so these cover only what macOS adds or overrides.
final class MacAppEnvironmentTests: XCTestCase {

    /// The Mac reports a device name to the sync service in place of
    /// `UIDevice.current.name`. It must be non-empty (Windows shows it in the
    /// paired-devices list) and must not carry the Bonjour `.local` suffix.
    func testDeviceDisplayNameIsCleanHostName() {
        let name = MacAppEnvironment.deviceDisplayName
        XCTAssertFalse(name.isEmpty, "a paired Mac with a blank name is unidentifiable in Windows Settings")
        XCTAssertFalse(name.hasSuffix(".local"), "Bonjour suffix should be trimmed for display")
    }

    /// The Mac registers as the contract's `macos` platform (v1.5), spelled
    /// exactly as the server's allow-list validates it. Not "other" any more,
    /// and never the DTO's "ios" default.
    func testDevicePlatformIsTheContractsMacosValue() {
        XCTAssertEqual(MacAppEnvironment.devicePlatform, "macos")
        XCTAssertNotEqual(MacAppEnvironment.devicePlatform, DeviceRegisterRequest(pairingCode: "", displayName: "").platform,
                          "the DTO's default is the phone's platform; the Mac must always override it")
    }

    /// The shared database opens at the same Application Support location the
    /// iOS companion uses, and its migrations run. This is the one piece of the
    /// shared stack whose behaviour is genuinely platform-dependent (the
    /// container path differs), so it is worth asserting on macOS directly.
    func testSharedDatabaseOpensAndMigratesInApplicationSupport() throws {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("memoryline-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: url) }

        let dbURL = url.appendingPathComponent("memoryline.db")
        let database = try AppDatabase.open(at: dbURL)
        let store = SQLiteCaptureStore(database: database)

        XCTAssertEqual(try store.allCaptures().count, 0)

        let capture = CaptureRecord.newLocal(type: .unclassified, fileName: "test.m4a")
        try store.insert(capture)

        let all = try store.allCaptures()
        XCTAssertEqual(all.count, 1)
        XCTAssertEqual(all.first?.id, capture.id)
    }

    /// The Phase 3 status projection applies its DDL over the same database.
    func testCaptureStatusStoreOpensOverTheSameDatabase() throws {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("memoryline-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: url) }

        let database = try AppDatabase.open(at: url.appendingPathComponent("memoryline.db"))
        let statuses = try SQLiteCaptureStatusStore(database: database)
        XCTAssertEqual(try statuses.allStatuses().count, 0)
    }
}
