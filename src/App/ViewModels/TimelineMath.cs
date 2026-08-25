using System;
using System.Collections.Generic;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Pure, WPF-free mapping between a media time and its normalized position on the timeline strip
/// [0,1] (T-014). <see cref="ToNormalized"/> powers rendering (a tick's X = normalized × width) and
/// <see cref="FromNormalized"/> powers a track click (click X / width → the time to cut at). The two
/// are inverse within rounding. Both clamp their inputs so a click past either edge, or a zero /
/// unknown duration, can never divide by zero or escape the [0,1] × [0,duration] box.
/// </summary>
public static class TimelineMath
{
    /// <summary>
    /// Map <paramref name="t"/> to its fraction of <paramref name="duration"/>, clamped to [0,1].
    /// A non-positive duration (unknown / degenerate) maps to 0 — never divides by zero.
    /// </summary>
    public static double ToNormalized(TimeSpan t, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0d;
        }

        var x = (double)t.Ticks / duration.Ticks;
        return Math.Clamp(x, 0d, 1d);
    }

    /// <summary>
    /// Map a normalized position <paramref name="x"/> (clamped to [0,1]) back to a time in
    /// [0, <paramref name="duration"/>]. A non-positive duration yields <see cref="TimeSpan.Zero"/>.
    /// Inverse of <see cref="ToNormalized"/> within rounding.
    /// </summary>
    public static TimeSpan FromNormalized(double x, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var clamped = Math.Clamp(x, 0d, 1d);
        return TimeSpan.FromTicks((long)Math.Round(duration.Ticks * clamped));
    }

    /// <summary>
    /// Index of the entry in <paramref name="normalized"/> whose pixel position
    /// (<c>value × <paramref name="width"/></c>) is nearest to <paramref name="xPx"/> and within
    /// <paramref name="radiusPx"/>, or <c>-1</c> when none is in range. On a tie the later entry
    /// wins — matching the timeline's marker-tick hit test (T-014 / SPEC-014 I31).
    /// </summary>
    public static int NearestNormalizedIndex(IReadOnlyList<double> normalized, double xPx, double width, double radiusPx)
    {
        var bestIndex = -1;
        var bestDist = radiusPx;
        for (var i = 0; i < normalized.Count; i++)
        {
            var dist = Math.Abs(normalized[i] * width - xPx);
            if (dist <= bestDist)
            {
                bestIndex = i;
                bestDist = dist;
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// The normalized waveform peak to draw at pixel column <paramref name="column"/> of
    /// <paramref name="columns"/>: the MAX peak over the source window mapped to that column (so
    /// downsampling to fewer pixels than peaks keeps the loudest sample per column rather than
    /// dropping it); when there are fewer peaks than columns the nearest source peak is used
    /// (T-084 / SPEC-014 I35).
    /// </summary>
    public static float PeakForColumn(float[] peaks, int column, int columns)
    {
        var start = (int)((long)column * peaks.Length / columns);
        var end = (int)((long)(column + 1) * peaks.Length / columns);
        if (end <= start)
        {
            var idx = Math.Min(peaks.Length - 1, start);
            return peaks[idx];
        }

        var max = 0f;
        for (var i = start; i < end; i++)
        {
            if (peaks[i] > max)
            {
                max = peaks[i];
            }
        }

        return max;
    }
}
