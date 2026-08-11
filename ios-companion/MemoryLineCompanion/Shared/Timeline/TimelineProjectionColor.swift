import SwiftUI

extension Color {
    /// Builds a colour from a projection's colour string.
    ///
    /// Colours cross the wire as text and each client builds its own value —
    /// which is what lets `TimelineProjectionModels.swift` stay free of AppKit
    /// and lets the same payload drive a Windows brush and a SwiftUI `Color`.
    ///
    /// **Two lengths, and the eight-digit form is ARGB, not RGBA.** Six digits
    /// is the contract's documented `"#RRGGBB"`, but `EraProjectionPayload`
    /// carries Windows' `EffectiveColor`, which is a user override when there is
    /// one, and the Windows colour picker writes `#AARRGGBB` — alpha *first*,
    /// the .NET/WinUI convention. Reading those eight digits as RGBA instead
    /// would silently rotate every channel: an opaque deep red `#FFCC0000`
    /// becomes a near-transparent teal. The failure looks like a theming bug
    /// rather than a parsing one, which is exactly why it is worth spelling out
    /// here.
    ///
    /// Returns nil rather than a default for anything else, so the caller
    /// decides what an unreadable colour looks like in its own context.
    init?(projectionHex hex: String) {
        var digits = hex.trimmingCharacters(in: .whitespaces)
        if digits.hasPrefix("#") {
            digits.removeFirst()
        }

        guard digits.count == 6 || digits.count == 8,
              let value = UInt32(digits, radix: 16) else {
            return nil
        }

        let alpha: Double
        let red: Double
        let green: Double
        let blue: Double

        if digits.count == 8 {
            alpha = Double((value >> 24) & 0xFF) / 255
            red = Double((value >> 16) & 0xFF) / 255
            green = Double((value >> 8) & 0xFF) / 255
            blue = Double(value & 0xFF) / 255
        } else {
            alpha = 1
            red = Double((value >> 16) & 0xFF) / 255
            green = Double((value >> 8) & 0xFF) / 255
            blue = Double(value & 0xFF) / 255
        }

        // .sRGB explicitly: the hex came from another platform's colour picker,
        // so it is a device-independent sRGB triple, not something to reinterpret
        // in the display's working space.
        self.init(.sRGB, red: red, green: green, blue: blue, opacity: alpha)
    }
}

/// The calendar the timeline groups and labels by.
///
/// UTC, deliberately, and it is not a detail. `startDate` is a calendar date
/// with no time zone — "Summer 2003" is not an instant — and the publisher pins
/// such dates to UTC midnight purely so they can cross a wire that demands an
/// offset (see `TimelineProjectionPublisher.Utc`). Grouping them with the user's
/// local calendar would read that midnight back in local time and file a 14 July
/// memory under 13 July for every user west of Greenwich.
///
/// The same reasoning is why every row renders the publisher's `displayDate`
/// rather than formatting `startDate` itself: precision ("Summer 2003" vs
/// "14 Jul 2003") is Windows' to decide, and re-deriving it is how two screens
/// showing the same memory start to disagree.
enum TimelineCalendar {
    static let utc: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC") ?? .gmt
        return calendar
    }()

    static func year(of date: Date) -> Int {
        utc.component(.year, from: date)
    }
}
