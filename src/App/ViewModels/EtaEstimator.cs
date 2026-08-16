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
///
/// <para>T-093 — converge fast, never "estimating…" forever. A fast single-pass <c>-c copy</c>
/// reports <c>time=</c> sparsely, so the fraction can sit near 0 for most/all of the run and the old
/// <c>MinUsableFraction = 0.01</c> gate left the ETA null ("estimating…") the whole time. Two changes
/// fix that: (1) <see cref="MinUsableFraction"/> is dropped to a tiny epsilon so the FIRST real
/// positive fraction seeds a fraction-based estimate; (2) when a total run duration is supplied via
/// <see cref="SeedDuration"/>, a DURATION-BASED fallback produces a decreasing estimate the moment
/// the op is clearly running (elapsed &gt; 0) even before any usable fraction arrives — so the label
/// converges within a couple of samples instead of staying stuck. A genuinely instant op still shows
/// nothing (the run completes → done latched → null → the UI hides the label).</para>
/// </summary>
public sealed class EtaEstimator
{
    /// <summary>
    /// Fractions at or below this are treated as "no usable fraction signal yet" — the fraction-based
    /// estimate needs a positive value to divide by. T-093 lowers this from 0.01 to a tiny epsilon so
    /// the FIRST real <c>time=</c> sample (however small) seeds a real fraction-based estimate rather
    /// than being discarded as "too early"; the duration-based fallback covers the gap before then.
    /// </summary>
    private const double MinUsableFraction = 1e-6;

    /// <summary>
    /// T-093: assumed lossless-copy throughput relative to real time, used ONLY by the duration-based
    /// fallback (before a usable fraction arrives). A stream copy runs many× faster than realtime, so
    /// projecting the whole job to take the media's own duration is a deliberately CONSERVATIVE
    /// (over-)estimate: the ETA it yields is an upper bound that shrinks with elapsed and is superseded
    /// the instant a real fraction seeds the accurate fraction-based estimate. Kept as a named constant
    /// so the intent is explicit; it never overrides a fraction-based number.
    /// </summary>
    private const double AssumedCopyDurationFactor = 1.0;

    /// <summary>
    /// EMA smoothing factor (0..1). Higher = more responsive to the latest sample, lower = smoother.
    /// 0.4 keeps the estimate reactive enough to trend downward while damping ffmpeg's burst jumps.
    /// </summary>
    private readonly double _alpha;

    private double? _smoothedRemainingSeconds;
    private bool _done;

    // T-093: optional total run duration for the duration-based fallback (null until seeded). Set once
    // per run via SeedDuration before the first Update; cleared by Reset.
    private TimeSpan? _totalDuration;

    // T-093: true once a real fraction-based estimate has seeded the EMA. From that point the
    // duration-based fallback is disabled — the accurate fraction signal owns the estimate.
    private bool _haveFractionEstimate;

    public EtaEstimator(double alpha = 0.4)
    {
        if (alpha is <= 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha must be in (0, 1].");
        }

        _alpha = alpha;
    }

    /// <summary>
    /// T-093: seed the total run duration so the duration-based fallback can produce an estimate before
    /// any usable fraction arrives. Optional — with no seed the estimator behaves as fraction-only
    /// (returns null until the first positive fraction). Call once at the start of a run, before the
    /// first <see cref="Update"/>. A non-positive duration is ignored (leaves the fallback disabled).
    /// </summary>
    public void SeedDuration(TimeSpan totalDuration)
    {
        _totalDuration = totalDuration > TimeSpan.Zero ? totalDuration : null;
    }

    /// <summary>
    /// Feeds one progress sample and returns the current smoothed estimate of time remaining.
    /// Returns <c>null</c> only when no estimate is available — the run is complete (fraction ≥ 1),
    /// OR it is genuinely too early AND no run-duration fallback was seeded. With a seeded duration a
    /// running op (elapsed &gt; 0) always yields a (decreasing) number rather than "estimating…".
    /// </summary>
    /// <param name="elapsed">Wall-clock time elapsed since the operation started.</param>
    /// <param name="fraction">Fractional progress in 0..1.</param>
    public TimeSpan? Update(TimeSpan elapsed, double fraction)
    {
        if (double.IsNaN(fraction))
        {
            // No fraction signal this sample — try the duration-based fallback, else current estimate.
            return UpdateFromDurationFallback(elapsed) ?? CurrentEstimate();
        }

        if (fraction >= 1d)
        {
            // Complete — nothing remains; latch done so a stray late sample can't resurrect an ETA.
            _done = true;
            _smoothedRemainingSeconds = null;
            return null;
        }

        if (_done)
        {
            return null;
        }

        if (fraction <= MinUsableFraction || elapsed <= TimeSpan.Zero)
        {
            // No usable fraction yet (or time hasn't advanced) → duration-based fallback if we can,
            // otherwise the current (possibly still-null) estimate = "estimating…".
            return UpdateFromDurationFallback(elapsed) ?? CurrentEstimate();
        }

        // remaining ≈ elapsed × (1 − fraction) / fraction
        var rawRemaining = elapsed.TotalSeconds * (1d - fraction) / fraction;

        // T-093: the first real fraction seeds the EMA directly. If the fallback had already been
        // priming a duration-based estimate, blend the accurate fraction value in via the normal EMA
        // step so the number transitions smoothly rather than snapping.
        _smoothedRemainingSeconds = _smoothedRemainingSeconds is { } prev && _haveFractionEstimate
            ? (_alpha * rawRemaining) + ((1d - _alpha) * prev)
            : rawRemaining; // seed on the first usable fraction (ignores any fallback priming value)

        _haveFractionEstimate = true;
        return CurrentEstimate();
    }

    /// <summary>
    /// T-093: the duration-based fallback. Before any usable fraction has seeded the fraction-based
    /// estimate, and while a total run duration is known and time has advanced, estimate remaining as
    /// <c>max(0, projectedTotal − elapsed)</c> where <c>projectedTotal = totalDuration ×
    /// AssumedCopyDurationFactor</c>. Fed through the same EMA so it reads smoothly and DECREASES as
    /// elapsed grows. Returns null (no fallback applied) once a fraction estimate exists, when no
    /// duration was seeded, or when the run is complete — leaving the caller on the accurate path.
    /// </summary>
    private TimeSpan? UpdateFromDurationFallback(TimeSpan elapsed)
    {
        if (_done || _haveFractionEstimate || _totalDuration is not { } total || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var projectedTotalSeconds = total.TotalSeconds * AssumedCopyDurationFactor;
        var remaining = Math.Max(0d, projectedTotalSeconds - elapsed.TotalSeconds);

        _smoothedRemainingSeconds = _smoothedRemainingSeconds is { } prev
            ? (_alpha * remaining) + ((1d - _alpha) * prev)
            : remaining; // seed the fallback on its first application

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
        _totalDuration = null;
        _haveFractionEstimate = false;
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
