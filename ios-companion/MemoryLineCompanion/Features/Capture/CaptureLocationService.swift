import CoreLocation
import Foundation
import os

/// What the recorder needs from location: one best-effort snapshot, requested
/// after recording has already started and never awaited by it. Main-actor
/// bound like the recorder, so the seam needs no `Sendable` juggling.
@MainActor
protocol CaptureLocationProviding: AnyObject {
    /// A snapshot, or nil when the user has not allowed location, services
    /// are off, the fix timed out or failed. Never throws.
    func snapshot(precise: Bool) async -> LocationContext?
}

/// One-shot CoreLocation snapshot for captures (design §14.4: location is
/// opt-in and scoped separately from the microphone; the default is a single
/// coarse snapshot; precise can be disabled). Off unless
/// `AppSettingsKey.captureLocation` is on — the recorder does not even
/// construct a request otherwise — and coarse (rounded to about a kilometre,
/// heading and speed dropped) unless `captureLocationPrecise` is on too.
/// Asks for when-in-use authorization the first time it is needed, never for
/// always-on, and never logs a coordinate (§14.5).
@MainActor
final class CaptureLocationService: NSObject, CaptureLocationProviding, CLLocationManagerDelegate {
    /// How long a fix may take before the capture simply goes without one.
    private static let fixTimeout: Duration = .seconds(12)
    /// How long the authorization prompt may stay open before giving up on this capture.
    private static let authorizationTimeout: Duration = .seconds(60)

    private let manager = CLLocationManager()
    private let log = AppLog.logger(category: "location")
    private var authorizationWaiters: [CheckedContinuation<Void, Never>] = []
    private var fixWaiters: [CheckedContinuation<CLLocation?, Never>] = []

    override init() {
        super.init()
        manager.delegate = self
    }

    /// Prompts for when-in-use access if the user has not been asked yet.
    /// Settings calls this when the opt-in is switched on, so the system
    /// prompt appears there, in context, rather than during a capture.
    func requestAuthorizationIfNeeded() {
        if manager.authorizationStatus == .notDetermined {
            manager.requestWhenInUseAuthorization()
        }
    }

    /// True when the user has allowed location for this app.
    var isAuthorized: Bool {
        switch manager.authorizationStatus {
        case .authorizedWhenInUse, .authorizedAlways: return true
        default: return false
        }
    }

    func snapshot(precise: Bool) async -> LocationContext? {
        if manager.authorizationStatus == .notDetermined {
            manager.requestWhenInUseAuthorization()
            await authorizationChange(timeout: Self.authorizationTimeout)
        }
        guard isAuthorized else {
            log.info("location snapshot skipped: not authorized")
            return nil
        }

        manager.desiredAccuracy = precise ? kCLLocationAccuracyBest : kCLLocationAccuracyKilometer
        guard let fix = await requestFix(timeout: Self.fixTimeout) else {
            log.info("location snapshot unavailable")
            return nil
        }
        log.info("location snapshot taken precise=\(precise)")
        return Self.context(from: fix, precise: precise)
    }

    /// The wire snapshot for a fix. Coarse means rounded to two decimals
    /// (about a kilometre), an accuracy no better than a kilometre, and no
    /// heading or speed — a position, not a track.
    nonisolated static func context(from fix: CLLocation, precise: Bool) -> LocationContext {
        let coordinate = fix.coordinate
        if precise {
            return LocationContext(
                latitude: coordinate.latitude,
                longitude: coordinate.longitude,
                horizontalAccuracyM: fix.horizontalAccuracy >= 0 ? fix.horizontalAccuracy : nil,
                headingDegrees: fix.course >= 0 ? fix.course : nil,
                speedMps: fix.speed >= 0 ? fix.speed : nil)
        }
        return LocationContext(
            latitude: (coordinate.latitude * 100).rounded() / 100,
            longitude: (coordinate.longitude * 100).rounded() / 100,
            horizontalAccuracyM: max(1_000, fix.horizontalAccuracy),
            headingDegrees: nil,
            speedMps: nil)
    }

    // MARK: - Waiting on the delegate

    private func authorizationChange(timeout: Duration) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            authorizationWaiters.append(continuation)
            Task { [weak self] in
                try? await Task.sleep(for: timeout)
                self?.resumeAuthorizationWaiters()
            }
        }
    }

    private func requestFix(timeout: Duration) async -> CLLocation? {
        await withCheckedContinuation { (continuation: CheckedContinuation<CLLocation?, Never>) in
            fixWaiters.append(continuation)
            manager.requestLocation()
            Task { [weak self] in
                try? await Task.sleep(for: timeout)
                self?.resumeFixWaiters(with: nil)
            }
        }
    }

    /// Resumes every waiter once; a continuation resumed by the delegate is
    /// no longer in the array when its timeout fires, and vice versa.
    private func resumeAuthorizationWaiters() {
        let waiters = authorizationWaiters
        authorizationWaiters = []
        for waiter in waiters {
            waiter.resume()
        }
    }

    private func resumeFixWaiters(with fix: CLLocation?) {
        let waiters = fixWaiters
        fixWaiters = []
        for waiter in waiters {
            waiter.resume(returning: fix)
        }
    }

    // MARK: - CLLocationManagerDelegate (delivered on the main thread; hop to the actor)

    nonisolated func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        Task { @MainActor in
            self.resumeAuthorizationWaiters()
        }
    }

    nonisolated func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        let fix = locations.last
        Task { @MainActor in
            self.resumeFixWaiters(with: fix)
        }
    }

    nonisolated func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {
        Task { @MainActor in
            self.resumeFixWaiters(with: nil)
        }
    }
}
