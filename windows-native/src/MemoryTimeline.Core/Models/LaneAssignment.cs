using MemoryTimeline.Core.DTOs;

namespace MemoryTimeline.Core.Models;

/// <summary>
/// How events are distributed vertically on the timeline.
/// Auto keeps the classic overlap-driven track stacking; every other mode
/// groups events into named horizontal swimlanes.
/// </summary>
public enum LaneMode
{
    /// <summary>Classic behavior: overlapping events stack upward on tracks.</summary>
    Auto = 0,

    /// <summary>One lane per event category.</summary>
    Category = 1,

    /// <summary>One lane per linked person (first person alphabetically wins).</summary>
    Person = 2,

    /// <summary>One lane per location.</summary>
    Location = 3,

    /// <summary>One lane per era.</summary>
    Era = 4,

    /// <summary>One lane per tag (first tag alphabetically wins).</summary>
    Tag = 5
}

/// <summary>
/// A computed swimlane: a stable key, a display label, and the events that
/// belong to it (in the order they were supplied).
/// </summary>
public sealed class TimelineLane
{
    public string LaneKey { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public IReadOnlyList<TimelineEventDto> Events { get; init; } = Array.Empty<TimelineEventDto>();
}

/// <summary>
/// Pure lane computation for the timeline's swimlane modes. No I/O, no UI -
/// unit-testable over plain DTOs.
/// </summary>
public static class LaneAssignment
{
    /// <summary>Pixel height of an expanded lane row (fits a 40px pin with margin).</summary>
    public const double LaneRowHeight = 48.0;

    /// <summary>Pixel height of a collapsed lane row (label strip only, events hidden).</summary>
    public const double CollapsedLaneRowHeight = 16.0;

    /// <summary>Key and label of the trailing lane for events with no value in the active mode.</summary>
    public const string UnassignedLaneKey = "(unassigned)";
    public const string UnassignedLaneLabel = "(unassigned)";

    // DEFERRED (F8 wave; still open 2026-10): user-reorderable lanes. Lane
    // order is alphabetical by label (case-insensitive, stable) with
    // "(unassigned)" pinned last. Drag-to-reorder needs persisted per-mode
    // ordering (a setting per LaneMode) plus gutter drag interactions in
    // TimelineControl, and is intentionally out of scope here.

    /// <summary>
    /// Groups events into ordered lanes for <paramref name="mode"/>:
    /// - Auto returns an empty lane list (callers keep track-stacked positions)
    ///   and clears each event's lane assignment.
    /// - Events with multiple values (several people/tags) go in the lane of
    ///   their FIRST value alphabetically (case-insensitive).
    /// - Events with no value go in a single "(unassigned)" lane at the end.
    /// - Empty lanes never appear (lanes are built from the events themselves).
    /// - Lane keys are deduplicated case-insensitively; the first-seen casing
    ///   provides the label.
    /// Also stamps <see cref="TimelineEventDto.LaneKey"/> and
    /// <see cref="TimelineEventDto.LaneIndex"/> on every event.
    /// </summary>
    public static IReadOnlyList<TimelineLane> ComputeLanes(IEnumerable<TimelineEventDto> events, LaneMode mode)
    {
        var list = events?.ToList() ?? new List<TimelineEventDto>();

        if (mode == LaneMode.Auto)
        {
            foreach (var evt in list)
            {
                evt.LaneKey = null;
                evt.LaneIndex = 0;
            }

            return Array.Empty<TimelineLane>();
        }

        // Bucket by case-insensitive key; remember the first-seen label.
        var buckets = new Dictionary<string, (string Label, List<TimelineEventDto> Events)>(StringComparer.OrdinalIgnoreCase);
        var unassigned = new List<TimelineEventDto>();

        foreach (var evt in list)
        {
            var (key, label) = SelectLaneValue(evt, mode);
            if (string.IsNullOrWhiteSpace(key))
            {
                unassigned.Add(evt);
                continue;
            }

            if (buckets.TryGetValue(key, out var bucket))
            {
                bucket.Events.Add(evt);
            }
            else
            {
                buckets[key] = (label ?? key, new List<TimelineEventDto> { evt });
            }
        }

        var lanes = buckets
            .Select(kv => new TimelineLane { LaneKey = kv.Key, Label = kv.Value.Label, Events = kv.Value.Events })
            .OrderBy(l => l.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.LaneKey, StringComparer.OrdinalIgnoreCase) // stable tie-break for identical labels
            .ToList();

        if (unassigned.Count > 0)
        {
            lanes.Add(new TimelineLane
            {
                LaneKey = UnassignedLaneKey,
                Label = UnassignedLaneLabel,
                Events = unassigned
            });
        }

        for (var i = 0; i < lanes.Count; i++)
        {
            foreach (var evt in lanes[i].Events)
            {
                evt.LaneKey = lanes[i].LaneKey;
                evt.LaneIndex = i;
            }
        }

        return lanes;
    }

    /// <summary>
    /// Picks the lane key/label for one event in the given mode; (null, null)
    /// means the event goes to the "(unassigned)" lane.
    /// </summary>
    private static (string? Key, string? Label) SelectLaneValue(TimelineEventDto evt, LaneMode mode)
    {
        switch (mode)
        {
            case LaneMode.Category:
            {
                var category = evt.Category?.Trim();
                return string.IsNullOrEmpty(category)
                    ? (null, null)
                    : (category, Capitalize(category));
            }

            case LaneMode.Person:
            {
                var name = FirstAlphabetically(evt.PeopleNames);
                return name == null ? (null, null) : (name, name);
            }

            case LaneMode.Location:
            {
                var location = evt.Location?.Trim();
                return string.IsNullOrEmpty(location) ? (null, null) : (location, location);
            }

            case LaneMode.Era:
            {
                var eraId = evt.EraId?.Trim();
                if (string.IsNullOrEmpty(eraId))
                {
                    return (null, null);
                }

                var label = string.IsNullOrWhiteSpace(evt.EraName) ? eraId : evt.EraName!;
                return (eraId, label);
            }

            case LaneMode.Tag:
            {
                var tag = FirstAlphabetically(evt.TagNames);
                return tag == null ? (null, null) : (tag, tag);
            }

            default:
                return (null, null);
        }
    }

    /// <summary>
    /// First non-blank value alphabetically (case-insensitive), or null.
    /// Multi-value events land in exactly one lane - this one.
    /// </summary>
    private static string? FirstAlphabetically(IEnumerable<string>? values)
    {
        if (values == null)
        {
            return null;
        }

        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
