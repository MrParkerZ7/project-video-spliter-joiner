using System;
using System.Globalization;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Estimates the time remaining for a long-running operation from real elapsed time versus a
/// fractional progress value (0..1). Deliberately WPF-free and wall-clock-free: every sample is fed
/// an explicit <see cref="TimeSpan"/> elapsed value, so it is fully unit-testable with synthetic
/// sequences (production feeds it a <see cref="System.Diagnostics.Stopwatch"/>'s <c>Elapsed</c>).
///
/// <para>The raw estimate is <c>remaining ≈ elapsed × (1 − fraction) / fraction</c>. Because ffmpeg
/// <c>-c copy</c> progress can be sparse / non-linear (the reported <c>time=</c> jumps in bursts),
/// the raw remaining is passed through an exponential moving average (EMA) so the displayed ETA
/// does not lurch wildly between samples. The first usable sample seeds the EMA directly.</para>
/// </summary>
public sealed class EtaEstimator
{
    /// <summary>
    /// Fractions at or below this are treated as "too early" — there isn't enough signal to make a
    /// meaningful estimate, so the ETA is unknown ("estimating…").
    /// </summary>
    private const double MinUsableFraction = 0.01;

    /// <summary>
    /// EMA smoothing factor (0..1). Higher = more responsive to the latest sample, lower = smoother.
    /// 0.4 keeps the estimate reactive enough to trend downward while damping ffmpeg's burst jumps.
    /// </summary>
    private readonly double _alpha;

    private double? _smoothedRemainingSeconds;
    private bool _done;

    public EtaEstimator(double alpha = 0.4)
    {
        if (alpha is <= 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha must be in (0, 1].");
        }

        _alpha = alpha;
    }

    /// <summary>
    /// Feeds one progress sample and returns the current smoothed estimate of time remaining.
    /// Returns <c>null</c> when no estimate is available yet — either the run is too early
    /// (fraction ≤ ~0.01) or already complete (fraction ≥ 1).
    /// </summary>
    /// <param name="elapsed">Wall-clock time elapsed since the operation started.</param>
    /// <param name="fraction">Fractional progress in 0..1.</param>
    public TimeSpan? Update(TimeSpan elapsed, double fraction)
    {
        if (double.IsNaN(fraction))
        {
            return CurrentEstimate();
        }

        if (fraction >= 1d)
        {
            // Complete — nothing remains; latch done so a stray late sample can't resurrect an ETA.
            _done = true;
            _smoothedRemainingSeconds = null;
            return null;
        }

        if (_done || fraction <= MinUsableFraction || elapsed <= TimeSpan.Zero)
        {
            // Too early (or already latched done): not enough signal for a number.
            return CurrentEstimate();
        }

        // remaining ≈ elapsed × (1 − fraction) / fraction
        var rawRemaining = elapsed.TotalSeconds * (1d - fraction) / fraction;

        _smoothedRemainingSeconds = _smoothedRemainingSeconds is { } prev
            ? (_alpha * rawRemaining) + ((1d - _alpha) * prev)
            : rawRemaining; // seed the EMA on the first usable sample

        return CurrentEstimate();
    }

    /// <summary>The current smoothed estimate without feeding a new sample; null if none yet / done.</summary>
    public TimeSpan? CurrentEstimate()
        => _smoothedRemainingSeconds is { } s ? TimeSpan.FromSeconds(s) : null;

    /// <summary>Resets the estimator for reuse on a fresh run.</summary>
    public void Reset()
    {
        _smoothedRemainingSeconds = null;
        _done = false;
    }

    /// <summary>
    /// Formats a remaining-time estimate into a friendly, deliberately imprecise label:
    /// <list type="bullet">
    /// <item><c>null</c> (too early / done) → "estimating…"</item>
    /// <item>under a minute → "~15s left"</item>
    /// <item>a minute or more → "~1m 20s left" (whole "0s" tail dropped → "~2m left")</item>
    /// </list>
    /// Always prefixed "~" and rounded so it never looks like a precise countdown.
    /// </summary>
    public static string FormatEta(TimeSpan? remaining)
    {
        if (remaining is not { } r || r < TimeSpan.Zero)
        {
            return "estimating…";
        }

        // Round to whole seconds — a sub-second ETA still reads as "~1s left", never "~0s".
        var totalSeconds = (long)Math.Round(r.TotalSeconds, MidpointRounding.AwayFromZero);
        if (totalSeconds < 1)
        {
            totalSeconds = 1;
        }

        if (totalSeconds < 60)
        {
            return $"~{totalSeconds}s left";
        }

        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;

        return seconds == 0
            ? string.Format(CultureInfo.InvariantCulture, "~{0}m left", minutes)
            : string.Format(CultureInfo.InvariantCulture, "~{0}m {1}s left", minutes, seconds);
    }
}
