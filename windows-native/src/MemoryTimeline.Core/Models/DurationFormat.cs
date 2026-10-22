namespace MemoryTimeline.Core.Models;

/// <summary>
/// Clock-style duration text for recordings and queue items. A
/// <c>TimeSpan.ToString(@"mm\:ss")</c> silently wraps once a recording
/// passes an hour (1:02:03 reads as "02:03"); this shows the hours whenever
/// there are any — "mm:ss" under an hour, "h:mm:ss" from one hour up, with
/// hours never capped at 24. Negative or non-finite inputs read as zero.
/// </summary>
public static class DurationFormat
{
    /// <summary>Formats a duration as "mm:ss", or "h:mm:ss" once it reaches an hour.</summary>
    public static string Clock(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var totalSeconds = (long)Math.Floor(duration.TotalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;

        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
    }

    /// <summary>Formats a duration given in seconds; NaN/infinite values read as zero.</summary>
    public static string Clock(double seconds)
    {
        return Clock(double.IsFinite(seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);
    }
}
