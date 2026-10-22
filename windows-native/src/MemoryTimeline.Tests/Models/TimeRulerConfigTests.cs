using FluentAssertions;
using MemoryTimeline.Core.Models;
using Xunit;

namespace MemoryTimeline.Tests.Models;

/// <summary>
/// Time-ruler tick generation: the month, quarter, year and five-year rulers
/// must advance by calendar units (a 30-day step drifts a day a month and
/// a 365-day step a day every leap year), while the day and week rulers keep
/// their fixed steps. Every test uses a fixed viewport so the expectations
/// are exact dates, not "roughly monthly".
/// </summary>
public class TimeRulerConfigTests
{
    private static TimelineCoordinateConverter Converter(DateTime start, double pixelsPerDay, double width = 1200)
    {
        return new TimelineCoordinateConverter
        {
            ScrollOffset = start,
            PixelsPerDay = pixelsPerDay,
            ViewportWidth = width
        };
    }

    private static List<TimeRulerTick> Ticks(TimeRulerConfig config, TimelineCoordinateConverter converter)
        => config.GenerateTicks(converter).ToList();

    private static List<DateTime> MajorDates(TimeRulerConfig config, TimelineCoordinateConverter converter)
        => Ticks(config, converter).Where(t => t.IsMajor).Select(t => t.Date).ToList();

    [Fact]
    public void Calculate_DefaultMonthZoom_UsesTheMonthRuler()
    {
        // The app's default zoom (Month, 3 px/day → a 40-day target gap).
        var config = TimeRulerConfig.Calculate(3);

        config.IntervalType.Should().Be(TimeRulerIntervalType.Month);
        config.MinorTicksPerMajor.Should().Be(4);
    }

    [Fact]
    public void GenerateTicks_MonthRuler_MajorTicksLandOnTheFirstOfConsecutiveMonths()
    {
        // Arrange: a 400-day window starting mid-January 2024 (a leap year).
        var config = TimeRulerConfig.Calculate(3);
        var converter = Converter(new DateTime(2024, 1, 15), 3);

        // Act
        var majors = MajorDates(config, converter);

        // Assert: one before the aligned start, then the 1st of every month —
        // February (29 days) must be followed by 1 March, not 2 March.
        majors.First().Should().Be(new DateTime(2023, 12, 1));
        majors.Should().OnlyContain(d => d.Day == 1 && d.TimeOfDay == TimeSpan.Zero);
        majors.Should().ContainInOrder(
            new DateTime(2024, 1, 1), new DateTime(2024, 2, 1), new DateTime(2024, 3, 1), new DateTime(2024, 4, 1));
        for (var i = 1; i < majors.Count; i++)
        {
            majors[i].Should().Be(majors[i - 1].AddMonths(1), "majors are consecutive calendar months");
        }

        majors.Last().Should().BeOnOrAfter(converter.VisibleEndDate, "the ruler is buffered past the visible end");
    }

    [Fact]
    public void GenerateTicks_MonthRuler_MinorTicksSplitTheRealMonthEvenly()
    {
        // Arrange
        var config = TimeRulerConfig.Calculate(3);
        var converter = Converter(new DateTime(2024, 1, 15), 3);

        // Act: the minors between 1 February and 1 March 2024 (29 days).
        var ticks = Ticks(config, converter);
        var february = ticks.FindIndex(t => t.IsMajor && t.Date == new DateTime(2024, 2, 1));
        var minors = ticks.Skip(february + 1).TakeWhile(t => !t.IsMajor).Select(t => t.Date).ToList();

        // Assert: three minors at quarter points of the actual month length.
        minors.Should().HaveCount(3);
        minors[0].Should().Be(new DateTime(2024, 2, 1).AddDays(29 / 4.0));
        minors[2].Should().Be(new DateTime(2024, 2, 1).AddDays(3 * 29 / 4.0));
        ticks[february + 4].Date.Should().Be(new DateTime(2024, 3, 1));
    }

    [Fact]
    public void GenerateTicks_QuarterRuler_MajorsOnQuarterStartsWithMonthlyMinors()
    {
        // Arrange: 0.5 px/day → a 240-day target gap → the quarterly ruler.
        var config = TimeRulerConfig.Calculate(0.5);
        config.IntervalType.Should().Be(TimeRulerIntervalType.Quarter);
        var converter = Converter(new DateTime(2023, 5, 20), 0.5);

        // Act
        var ticks = Ticks(config, converter);
        var majors = ticks.Where(t => t.IsMajor).Select(t => t.Date).ToList();

        // Assert: Jan/Apr/Jul/Oct 1st, with the two minors in between on the 1st of the months.
        majors.First().Should().Be(new DateTime(2023, 1, 1));
        majors.Should().OnlyContain(d => d.Day == 1 && (d.Month - 1) % 3 == 0);
        var q2 = ticks.FindIndex(t => t.IsMajor && t.Date == new DateTime(2023, 4, 1));
        ticks[q2 + 1].Date.Should().Be(new DateTime(2023, 5, 1));
        ticks[q2 + 2].Date.Should().Be(new DateTime(2023, 6, 1));
        ticks[q2 + 3].Date.Should().Be(new DateTime(2023, 7, 1));
    }

    [Fact]
    public void GenerateTicks_YearRuler_MajorsOnJanuaryFirstAcrossLeapYearsWithQuarterlyMinors()
    {
        // Arrange: 0.25 px/day → a 480-day target gap → the yearly ruler; the
        // window spans 2023-2026, which includes the 366-day 2024.
        var config = TimeRulerConfig.Calculate(0.25);
        config.IntervalType.Should().Be(TimeRulerIntervalType.Year);
        var converter = Converter(new DateTime(2023, 6, 1), 0.25, width: 1200); // 4800 days

        // Act
        var ticks = Ticks(config, converter);
        var majors = ticks.Where(t => t.IsMajor).Select(t => t.Date).ToList();

        // Assert: a 365-day step would have put 2025 on 31 December 2024.
        majors.Should().ContainInOrder(
            new DateTime(2022, 1, 1), new DateTime(2023, 1, 1), new DateTime(2024, 1, 1),
            new DateTime(2025, 1, 1), new DateTime(2026, 1, 1));
        majors.Should().OnlyContain(d => d.Month == 1 && d.Day == 1);

        var y2024 = ticks.FindIndex(t => t.IsMajor && t.Date == new DateTime(2024, 1, 1));
        ticks.Skip(y2024 + 1).Take(3).Select(t => t.Date).Should().Equal(
            new DateTime(2024, 4, 1), new DateTime(2024, 7, 1), new DateTime(2024, 10, 1));
        ticks.Skip(y2024 + 1).Take(3).Should().OnlyContain(t => !t.IsMajor);
    }

    [Fact]
    public void GenerateTicks_FiveYearRuler_MajorsOnMultiplesOfFiveWithYearlyMinors()
    {
        // Arrange: 0.1 px/day → a 1200-day target gap → the five-year ruler.
        var config = TimeRulerConfig.Calculate(0.1);
        config.IntervalType.Should().Be(TimeRulerIntervalType.FiveYear);
        var converter = Converter(new DateTime(2021, 3, 3), 0.1); // 12,000 days ≈ 33 years

        // Act
        var ticks = Ticks(config, converter);
        var majors = ticks.Where(t => t.IsMajor).Select(t => t.Date).ToList();

        // Assert
        majors.First().Should().Be(new DateTime(2015, 1, 1));
        majors.Should().ContainInOrder(new DateTime(2020, 1, 1), new DateTime(2025, 1, 1), new DateTime(2030, 1, 1));
        majors.Should().OnlyContain(d => d.Year % 5 == 0 && d.Month == 1 && d.Day == 1);

        var y2020 = ticks.FindIndex(t => t.IsMajor && t.Date == new DateTime(2020, 1, 1));
        ticks.Skip(y2020 + 1).Take(4).Select(t => t.Date).Should().Equal(
            new DateTime(2021, 1, 1), new DateTime(2022, 1, 1), new DateTime(2023, 1, 1), new DateTime(2024, 1, 1));
    }

    [Fact]
    public void GenerateTicks_DayRuler_KeepsFixedDaySteps()
    {
        // Arrange: 60 px/day → a 2-day target gap → the day ruler (6-hour minors).
        var config = TimeRulerConfig.Calculate(60);
        config.IntervalType.Should().Be(TimeRulerIntervalType.Day);
        var converter = Converter(new DateTime(2024, 2, 28, 13, 0, 0), 60);

        // Act
        var ticks = Ticks(config, converter);
        var majors = ticks.Where(t => t.IsMajor).Select(t => t.Date).ToList();

        // Assert: consecutive midnights through the leap day; minors every 6 h.
        majors.Should().ContainInOrder(new DateTime(2024, 2, 28), new DateTime(2024, 2, 29), new DateTime(2024, 3, 1));
        majors.Should().OnlyContain(d => d.TimeOfDay == TimeSpan.Zero);
        var leapDay = ticks.FindIndex(t => t.IsMajor && t.Date == new DateTime(2024, 2, 29));
        ticks[leapDay + 1].Date.Should().Be(new DateTime(2024, 2, 29, 6, 0, 0));
        ticks[leapDay + 2].Date.Should().Be(new DateTime(2024, 2, 29, 12, 0, 0));
    }

    [Fact]
    public void GenerateTicks_WeekRuler_AlignsToSundayAndStepsSevenDays()
    {
        // Arrange: 20 px/day → a 6-day target gap → the week ruler.
        var config = TimeRulerConfig.Calculate(20);
        config.IntervalType.Should().Be(TimeRulerIntervalType.Week);
        var converter = Converter(new DateTime(2024, 1, 17), 20); // a Wednesday

        // Act
        var majors = MajorDates(config, converter);

        // Assert: Sundays, a week apart, starting one week before the aligned start.
        majors.First().Should().Be(new DateTime(2024, 1, 7));
        majors.Should().OnlyContain(d => d.DayOfWeek == DayOfWeek.Sunday);
        for (var i = 1; i < majors.Count; i++)
        {
            (majors[i] - majors[i - 1]).Should().Be(TimeSpan.FromDays(7));
        }
    }

    [Fact]
    public void GenerateTicks_LabelsAndScreenPositionsComeFromTheTickDate()
    {
        // Arrange
        var config = TimeRulerConfig.Calculate(3);
        var converter = Converter(new DateTime(2024, 1, 15), 3);

        // Act
        var ticks = Ticks(config, converter);

        // Assert
        var march = ticks.Single(t => t.IsMajor && t.Date == new DateTime(2024, 3, 1));
        march.Label.Should().Be(new DateTime(2024, 3, 1).ToString(config.LabelFormat));
        march.ScreenX.Should().Be(converter.DateToScreen(new DateTime(2024, 3, 1)));
        ticks.Where(t => !t.IsMajor).Should().OnlyContain(t => t.Label == null);
        ticks.Select(t => t.Date).Should().BeInAscendingOrder();
    }

    [Fact]
    public void GenerateTicks_NearTheEndOfTheDateRange_DoesNotThrow()
    {
        // Arrange: a viewport whose buffered end would overflow DateTime.MaxValue.
        var config = TimeRulerConfig.Calculate(0.1);
        var converter = Converter(DateTime.MaxValue.AddDays(-30), 0.1);

        // Act
        var act = () => Ticks(config, converter);

        // Assert
        act.Should().NotThrow();
        act().Should().NotBeEmpty();
    }
}
