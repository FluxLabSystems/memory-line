namespace MemoryTimeline.Services;

/// <summary>
/// Pure sample math behind the recording level meter. Kept free of WinRT
/// types so it can be unit-tested: <see cref="AudioRecordingService"/> pulls
/// the raw capture frames and hands the samples here.
/// </summary>
public static class AudioLevelMeter
{
    /// <summary>
    /// Dynamic range of the meter: a peak this many dB below full scale (or
    /// quieter) shows as empty. 60 dB keeps normal speech (-30..-10 dBFS) in
    /// the visible middle instead of hugging the bottom of a linear bar.
    /// </summary>
    public const double MeterRangeDb = 60.0;

    /// <summary>
    /// Peak absolute amplitude of 16-bit PCM samples, normalized to 0..1.
    /// </summary>
    public static double PeakFromPcm16(ReadOnlySpan<short> samples)
    {
        var peak = 0;
        foreach (var sample in samples)
        {
            // Widen before negating: -short.MinValue overflows a short.
            var magnitude = sample < 0 ? -(int)sample : sample;
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return Math.Clamp(peak / 32767.0, 0.0, 1.0);
    }

    /// <summary>
    /// Peak absolute amplitude of 32-bit float samples (nominal range -1..1),
    /// clamped to 0..1. NaN samples are ignored.
    /// </summary>
    public static double PeakFromFloat32(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            if (float.IsNaN(sample))
            {
                continue;
            }

            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return Math.Clamp((double)peak, 0.0, 1.0);
    }

    /// <summary>
    /// Maps a linear peak (0..1) onto the meter (0..1) on a decibel scale with
    /// <see cref="MeterRangeDb"/> of range: full scale is 1, silence (or
    /// anything at or below -60 dBFS) is 0, -20 dBFS is two thirds.
    /// </summary>
    public static double ToMeterLevel(double peak)
    {
        if (double.IsNaN(peak) || peak <= 0.0)
        {
            return 0.0;
        }

        if (peak >= 1.0)
        {
            return 1.0;
        }

        var decibels = 20.0 * Math.Log10(peak); // negative for peak < 1
        return Math.Clamp(1.0 + decibels / MeterRangeDb, 0.0, 1.0);
    }
}
