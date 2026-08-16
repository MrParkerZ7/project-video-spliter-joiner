using System;
using System.Collections.Generic;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-045: unit tests for <see cref="EtaEstimator"/>. The estimator takes explicit (elapsed, fraction)
/// samples — no wall-clock — so every case below is driven by synthetic sequences and is fully
/// deterministic. Covers the core math, EMA smoothing, the too-early / done edge cases, a
/// monotonically-sane decrease over a steady sequence, and the friendly formatting.
/// </summary>
public sealed class EtaEstimatorTests
{
    // ---- Core math ---------------------------------------------------------------------------

    [Fact]
    public void Update_TenSecondsAtQuarter_EstimatesRoughlyThirtySecondsRemaining()
    {
        var eta = new EtaEstimator();

        // First usable sample seeds the EMA directly, so 10s at 0.25 → 10 × 0.75/0.25 = 30s exactly.
        var remaining = eta.Update(TimeSpan.FromSeconds(10), 0.25);

        remaining.Should().NotBeNull();
        remaining!.Value.TotalSeconds.Should().BeApproximately(30d, 0.001);
    }

    [Fact]
    public void Update_HalfDoneAtTwentySeconds_EstimatesRoughlyTwentyRemaining()
    {
        var eta = new EtaEstimator();

        var remaining = eta.Update(TimeSpan.FromSeconds(20), 0.5);

        remaining!.Value.TotalSeconds.Should().BeApproximately(20d, 0.001);
    }

    // ---- Edge cases --------------------------------------------------------------------------

    [Fact]
    public void Update_FractionZeroWithNoDurationSeed_ReturnsNull_FormatsAsEstimating()
    {
        var eta = new EtaEstimator();

        // Fraction 0 with no duration seeded → nothing to divide by, no fallback → "estimating…".
        eta.Update(TimeSpan.FromSeconds(5), 0.0).Should().BeNull("fraction 0 with no duration seed is unknowable");
        eta.Update(TimeSpan.FromSeconds(5), double.NaN).Should().BeNull("a NaN fraction with no seed is unknowable");
        EtaEstimator.FormatEta(null).Should().Be("estimating…");
    }

    [Fact]
    public void Update_TinyPositiveFraction_NowSeedsAnEstimate_NoLongerStuckEstimating()
    {
        // T-093: MinUsableFraction lowered to a tiny epsilon so the FIRST real (small) fraction seeds a
        // fraction-based estimate instead of being discarded as "too early" (the stuck-"estimating…" bug).
        var eta = new EtaEstimator();

        var remaining = eta.Update(TimeSpan.FromSeconds(5), 0.005);

        remaining.Should().NotBeNull("a small but real fraction now yields an estimate");
        // 5s at 0.005 → 5 × 0.995/0.005 = 995s.
        remaining!.Value.TotalSeconds.Should().BeApproximately(995d, 1d);
    }

    [Fact]
    public void Update_FractionComplete_ReturnsNull_AndLatchesDone()
    {
        var eta = new EtaEstimator();
        eta.Update(TimeSpan.FromSeconds(10), 0.5).Should().NotBeNull();

        eta.Update(TimeSpan.FromSeconds(20), 1.0).Should().BeNull("fraction ≥ 1 means done — no ETA");

        // A stray late sample after done must NOT resurrect an ETA.
        eta.Update(TimeSpan.FromSeconds(21), 0.6).Should().BeNull("done is latched");
    }

    [Fact]
    public void SteadySequence_ProducesMonotonicallyDecreasingSaneEta()
    {
        var eta = new EtaEstimator();
        // A perfectly linear run: 40s total, sampled every 4s. Remaining should fall each step and
        // never exceed the total run time (a sane, non-crazy trend).
        double? prev = null;
        for (var i = 1; i <= 9; i++)
        {
            var elapsed = TimeSpan.FromSeconds(i * 4);
            var fraction = i / 10d; // 0.1, 0.2, … 0.9
            var remaining = eta.Update(elapsed, fraction);

            remaining.Should().NotBeNull();
            remaining!.Value.TotalSeconds.Should().BeLessThan(45d, "ETA stays bounded by a sane run length");
            if (prev is { } p)
            {
                remaining.Value.TotalSeconds.Should().BeLessThan(p + 0.001,
                    "on a steady run the ETA trends downward, not up");
            }

            prev = remaining.Value.TotalSeconds;
        }
    }

    [Fact]
    public void Smoothing_DampsAJumpySample_RelativeToRaw()
    {
        var eta = new EtaEstimator(alpha: 0.4);
        // Seed with a steady estimate: 10s at 0.5 → 10s remaining.
        eta.Update(TimeSpan.FromSeconds(10), 0.5).Should().NotBeNull();

        // A jumpy next sample whose RAW remaining is much larger (11s at 0.2 → 44s). The EMA must
        // land well below the raw 44s because it blends with the prior ~10s.
        var smoothed = eta.Update(TimeSpan.FromSeconds(11), 0.2);

        smoothed!.Value.TotalSeconds.Should().BeLessThan(44d, "EMA damps the spike below the raw value");
        smoothed.Value.TotalSeconds.Should().BeGreaterThan(10d, "but still trends toward the new signal");
    }

    [Fact]
    public void Reset_ClearsEstimate_ReseedsOnNextSample()
    {
        var eta = new EtaEstimator();
        eta.Update(TimeSpan.FromSeconds(10), 0.5).Should().NotBeNull();

        eta.Reset();
        eta.CurrentEstimate().Should().BeNull("reset clears the smoothed state");

        // After reset, the next usable sample seeds fresh (exact math again).
        eta.Update(TimeSpan.FromSeconds(10), 0.25)!.Value.TotalSeconds.Should().BeApproximately(30d, 0.001);
    }

    // ---- T-093: duration-based fallback (never "estimating…" forever) ------------------------

    [Fact]
    public void DurationFallback_RunningWithNoUsableFraction_ConvergesToDecreasingEstimate()
    {
        // A sparse -c copy pass: the fraction sits at ~0 for the whole run. With a seeded duration the
        // estimator must fall back to a duration-based estimate that is non-null and DECREASES with
        // elapsed — instead of returning null ("estimating…") the entire run.
        var eta = new EtaEstimator();
        eta.SeedDuration(TimeSpan.FromSeconds(40));

        var first = eta.Update(TimeSpan.FromSeconds(2), 0.0);
        var later = eta.Update(TimeSpan.FromSeconds(10), 0.0);

        first.Should().NotBeNull("a running op with a seeded duration yields a fallback estimate, not null");
        later.Should().NotBeNull();
        later!.Value.Should().BeLessThan(first!.Value, "the fallback estimate shrinks as elapsed grows");
        EtaEstimator.FormatEta(later).Should().NotBe("estimating…", "the label is a real ETA, never stuck estimating");
    }

    [Fact]
    public void DurationFallback_ConvergesWithinAFewSamples_NotNullTheWholeRun()
    {
        // Acceptance (c): with only sparse near-zero fractions, the estimate must become non-null within
        // a few samples (here: the very first running sample) and stay non-null, given a seeded duration.
        var eta = new EtaEstimator();
        eta.SeedDuration(TimeSpan.FromSeconds(30));

        var estimates = new List<TimeSpan?>();
        for (var i = 1; i <= 5; i++)
        {
            estimates.Add(eta.Update(TimeSpan.FromSeconds(i), 0.0));
        }

        estimates.Should().OnlyContain(e => e != null, "the seeded-duration fallback never leaves the ETA null while running");
    }

    [Fact]
    public void DurationFallback_IsSupersededByRealFraction_WhenItArrives()
    {
        // The duration fallback primes an estimate; once a real usable fraction arrives, the accurate
        // fraction-based number takes over (seeded on that first usable fraction, then EMA-blended).
        var eta = new EtaEstimator();
        eta.SeedDuration(TimeSpan.FromSeconds(100));

        // Fallback while fraction is ~0.
        eta.Update(TimeSpan.FromSeconds(1), 0.0).Should().NotBeNull();

        // A real fraction: 10s at 0.5 → 10s remaining (fraction-based). This must dominate the ~99s
        // the crude duration fallback would have implied.
        var real = eta.Update(TimeSpan.FromSeconds(10), 0.5);

        real.Should().NotBeNull();
        real!.Value.TotalSeconds.Should().BeApproximately(10d, 0.001,
            "the first usable fraction seeds the accurate estimate, replacing the crude fallback");
    }

    [Fact]
    public void DurationFallback_NotSeeded_StaysNullUntilFraction()
    {
        // No SeedDuration call → fraction-only behaviour: null until a usable fraction arrives.
        var eta = new EtaEstimator();

        eta.Update(TimeSpan.FromSeconds(5), 0.0).Should().BeNull("no duration seed → no fallback");
        eta.Update(TimeSpan.FromSeconds(10), 0.25)!.Value.TotalSeconds.Should().BeApproximately(30d, 0.001);
    }

    [Fact]
    public void Reset_ClearsDurationSeed_AndFractionState()
    {
        var eta = new EtaEstimator();
        eta.SeedDuration(TimeSpan.FromSeconds(50));
        eta.Update(TimeSpan.FromSeconds(2), 0.0).Should().NotBeNull("seeded fallback active");

        eta.Reset();

        // After reset the duration seed is gone → fraction-only again (null on a 0 fraction).
        eta.Update(TimeSpan.FromSeconds(2), 0.0).Should().BeNull("reset clears the duration seed");
    }

    // ---- Formatting --------------------------------------------------------------------------

    [Theory]
    [InlineData(30, "~30s left")]
    [InlineData(15, "~15s left")]
    [InlineData(59, "~59s left")]
    [InlineData(80, "~1m 20s left")]
    [InlineData(120, "~2m left")]
    [InlineData(125, "~2m 5s left")]
    public void FormatEta_ProducesFriendlyGranularity(int seconds, string expected)
    {
        EtaEstimator.FormatEta(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void FormatEta_Null_IsEstimating()
    {
        EtaEstimator.FormatEta(null).Should().Be("estimating…");
    }

    [Fact]
    public void FormatEta_SubSecond_RoundsUpToOneSecond_NeverZero()
    {
        EtaEstimator.FormatEta(TimeSpan.FromMilliseconds(200)).Should().Be("~1s left",
            "a tiny remaining still reads as ~1s, never ~0s");
    }

    [Fact]
    public void FormatEta_NegativeGuard_IsEstimating()
    {
        EtaEstimator.FormatEta(TimeSpan.FromSeconds(-5)).Should().Be("estimating…");
    }
}
