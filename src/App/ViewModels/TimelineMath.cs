using System;

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
}
