using FluentAssertions;
using MemoryTimeline.Services;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for the pure level-meter math behind the Queue page's microphone
/// meter (AudioRecordingService feeds it raw capture frames; nothing here
/// touches WinRT).
/// </summary>
public class AudioLevelMeterTests
{
    [Fact]
    public void PeakFromPcm16_Silence_IsZero()
    {
        var samples = new short[256];

        AudioLevelMeter.PeakFromPcm16(samples).Should().Be(0.0);
    }

    [Fact]
    public void PeakFromPcm16_Empty_IsZero()
    {
        AudioLevelMeter.PeakFromPcm16(ReadOnlySpan<short>.Empty).Should().Be(0.0);
    }

    [Fact]
    public void PeakFromPcm16_UsesLargestMagnitudeAndNormalizes()
    {
        var samples = new short[] { 0, 1000, -16384, 8000, -3 };

        AudioLevelMeter.PeakFromPcm16(samples).Should().BeApproximately(16384.0 / 32767.0, 1e-9);
    }

    [Fact]
    public void PeakFromPcm16_MostNegativeSample_ClampsToOneWithoutOverflow()
    {
        var samples = new short[] { 0, short.MinValue, 0 };

        AudioLevelMeter.PeakFromPcm16(samples).Should().Be(1.0);
    }

    [Fact]
    public void PeakFromFloat32_IgnoresNaNAndClamps()
    {
        var samples = new float[] { 0.1f, float.NaN, -0.5f, 1.7f, 0.2f };

        AudioLevelMeter.PeakFromFloat32(samples).Should().Be(1.0);
    }

    [Fact]
    public void PeakFromFloat32_UsesAbsoluteValue()
    {
        var samples = new float[] { 0.1f, -0.25f, 0.2f };

        AudioLevelMeter.PeakFromFloat32(samples).Should().BeApproximately(0.25, 1e-6);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(-0.5, 0.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(4.0, 1.0)]
    public void ToMeterLevel_ClampsTheEnds(double peak, double expected)
    {
        AudioLevelMeter.ToMeterLevel(peak).Should().Be(expected);
    }

    [Fact]
    public void ToMeterLevel_IsDecibelScaled()
    {
        // -20 dBFS sits two thirds of the way up a 60 dB meter; -60 dBFS is empty.
        AudioLevelMeter.ToMeterLevel(0.1).Should().BeApproximately(2.0 / 3.0, 1e-9);
        AudioLevelMeter.ToMeterLevel(0.001).Should().BeApproximately(0.0, 1e-9);
        AudioLevelMeter.ToMeterLevel(0.0001).Should().Be(0.0);
    }

    [Fact]
    public void ToMeterLevel_IsMonotonic()
    {
        var previous = 0.0;
        for (var peak = 0.0005; peak <= 1.0; peak *= 1.5)
        {
            var level = AudioLevelMeter.ToMeterLevel(peak);
            level.Should().BeGreaterThanOrEqualTo(previous);
            previous = level;
        }
    }
}
