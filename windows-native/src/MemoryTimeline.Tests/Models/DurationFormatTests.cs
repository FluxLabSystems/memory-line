using FluentAssertions;
using MemoryTimeline.Core.Models;
using Xunit;

namespace MemoryTimeline.Tests.Models;

/// <summary>
/// Hour-safe clock durations: the recording timer and queue cards used a
/// "mm:ss" format that wrapped after an hour.
/// </summary>
public class DurationFormatTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59.9, "00:59")]
    [InlineData(61, "01:01")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(36_000 + 5 * 60 + 9, "10:05:09")]
    [InlineData(90_000, "25:00:00")]
    public void Clock_Seconds_ShowsHoursOnlyFromOneHourUp(double seconds, string expected)
    {
        DurationFormat.Clock(seconds).Should().Be(expected);
        DurationFormat.Clock(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Clock_NegativeOrNonFinite_ReadsAsZero(double seconds)
    {
        DurationFormat.Clock(seconds).Should().Be("00:00");
    }

    [Fact]
    public void Clock_NeverWrapsLikeTheOldFormat()
    {
        // 1:02:03 used to render as "02:03".
        var duration = new TimeSpan(1, 2, 3);
        duration.ToString(@"mm\:ss").Should().Be("02:03");
        DurationFormat.Clock(duration).Should().Be("1:02:03");
    }
}
