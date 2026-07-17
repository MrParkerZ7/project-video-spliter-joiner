using System;
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
    public void Update_FractionTooEarly_ReturnsNull_FormatsAsEstimating()
    {
        var eta = new EtaEstimator();

        eta.Update(TimeSpan.FromSeconds(5), 0.0).Should().BeNull("fraction 0 is too early to estimate");
        eta.Update(TimeSpan.FromSeconds(5), 0.005).Should().BeNull("≤ ~0.01 is too early");
        EtaEstimator.FormatEta(null).Should().Be("estimating…");
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
