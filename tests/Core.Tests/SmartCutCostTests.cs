using FluentAssertions;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-191 (G-059, SPEC-001 I69, SPEC-011 I165) - <see cref="SmartCutCost"/>, the ONE pure function that turns a
/// <see cref="SmartCutPlan"/> into the seconds it re-encodes (<c>h</c>), the seconds it copies (<c>c</c>) and the cost
/// <c>h·k + c</c>. The engine's row slices (<c>w = h·k / (h·k + c)</c>) and the Bulk Cut batch weight both use it, so the row bar and
/// the batch bar can never weigh the same cut differently.
/// </summary>
public sealed class SmartCutCostTests
{
    private static readonly double K = SmartCutEngine.EncodeCostFactor;

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static IReadOnlyList<TimeSpan> Grid2s() => Enumerable.Range(0, 31).Select(i => S(i * 2)).ToList();

    // serves-spec: SPEC-001#I69 - a head re-encode: h = HeadEnd - Start (the plan's own ReencodedDuration), c = End - HeadEnd.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AHeadReencode_CostsItsHeadTimesTheFactor_PlusTheRangeItCopies()
    {
        var plan = SmartCutPlanner.Plan(S(5), S(22), Grid2s()); // head [5, 6), copy [6, 22)

        var cost = SmartCutCost.Of(plan, S(60));

        cost.ReencodedSeconds.Should().Be(plan.ReencodedDuration.TotalSeconds).And.Be(1);
        cost.CopiedSeconds.Should().Be(16, "End - HeadEnd");
        cost.Total.Should().BeApproximately((1 * K) + 16, 1e-9);
        cost.ReencodeShare.Should().BeApproximately(K / (K + 16), 1e-12);
    }

    // serves-spec: SPEC-001#I69 - with no end the copied range runs to the end of the file: c = duration - HeadEnd.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AHeadReencodeWithNoEnd_CopiesToTheEndOfTheFile()
    {
        var plan = SmartCutPlanner.Plan(S(5), null, Grid2s());

        var cost = SmartCutCost.Of(plan, S(60));

        cost.ReencodedSeconds.Should().Be(1);
        cost.CopiedSeconds.Should().Be(54, "duration - HeadEnd = 60 - 6");
        cost.Total.Should().BeApproximately(K + 54, 1e-9);
    }

    // serves-spec: SPEC-001#I69 - a full re-encode copies nothing: c = 0, and everything is re-encode (share 1). With no end the range
    // runs to the end of the file; SmartCutPlan.ReencodedDuration reads 0 there, so the cost does not take h from it.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(9.0, 10.0, 1.0)]    // a range with an end
    [InlineData(59.0, null, 1.0)]   // no end: [59, end of the 60 s file)
    [InlineData(57.5, null, 2.5)]
    public void AFullReencode_CostsOnlyItsReencode(double start, double? end, double expectedSeconds)
    {
        var plan = SmartCutPlanner.Plan(S(start), end is { } e ? S(e) : null, new[] { S(0), S(4), S(8) });
        plan.Strategy.Should().Be(SmartCutStrategy.FullReencode, "precondition");

        var cost = SmartCutCost.Of(plan, S(60));

        cost.ReencodedSeconds.Should().BeApproximately(expectedSeconds, 1e-9);
        cost.CopiedSeconds.Should().Be(0);
        cost.Total.Should().BeApproximately(expectedSeconds * K, 1e-9);
        cost.ReencodeShare.Should().Be(1);
    }

    // serves-spec: SPEC-001#I69 - a pure copy re-encodes nothing: h = 0, c = the whole range, share 0.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(8.0, null, 52.0)]
    [InlineData(8.0, 30.0, 22.0)]
    public void APureCopy_CostsItsWholeRangeAsACopy(double start, double? end, double expectedCopied)
    {
        var plan = SmartCutPlanner.Plan(S(start), end is { } e ? S(e) : null, Grid2s());
        plan.Strategy.Should().Be(SmartCutStrategy.PureCopy, "precondition");

        var cost = SmartCutCost.Of(plan, S(60));

        cost.ReencodedSeconds.Should().Be(0);
        cost.CopiedSeconds.Should().Be(expectedCopied);
        cost.Total.Should().Be(expectedCopied);
        cost.ReencodeShare.Should().Be(0);
    }

    // serves-spec: SPEC-001#I40 / SPEC-001#I69 - the planner's 10 ms tolerance is part of the answer: a start 5 ms from a keyframe is a
    // PureCopy (h = 0), 11 ms from it is a head re-encode of that sliver. No second "first keyframe at or after" search can disagree.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ARequestWithinThePlannersToleranceOfAKeyframe_HasNoReencode()
    {
        var near = SmartCutCost.Of(SmartCutPlanner.Plan(S(8.005), null, Grid2s()), S(60));
        var far = SmartCutCost.Of(SmartCutPlanner.Plan(S(8.011), null, Grid2s()), S(60));

        near.ReencodedSeconds.Should().Be(0, "8.005 s is within 10 ms of the keyframe at 8 s: the lossless cut is exact");
        near.CopiedSeconds.Should().BeApproximately(60 - 8.005, 1e-6);
        far.ReencodedSeconds.Should().BeApproximately(2 - 0.011, 1e-6, "11 ms past the keyframe is mid-GOP: the head runs to the keyframe at 10 s");
        far.CopiedSeconds.Should().BeApproximately(50, 1e-6);
    }

    // serves-spec: SPEC-001#I69 - degenerate input never produces a negative or NaN cost: spans that run backwards count as zero, and a
    // cut that costs nothing has no share.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void NegativeSpans_CountAsZero_AndAnEmptyCostHasNoShare()
    {
        // An end past the duration is the planner's request, kept as it is; a duration before the start leaves nothing to copy.
        var plan = SmartCutPlanner.Plan(S(5), null, Grid2s());
        var beyond = SmartCutCost.Of(plan, S(4));

        beyond.CopiedSeconds.Should().Be(0, "duration 4 s ends before the head does");
        beyond.ReencodedSeconds.Should().Be(1);
        new SmartCutCost(0, 0).ReencodeShare.Should().Be(0);
        new SmartCutCost(0, 0).Total.Should().Be(0);
        new SmartCutCost(2, 0).ReencodeShare.Should().Be(1);
        new SmartCutCost(0, 5).ReencodeShare.Should().Be(0);
    }

    // serves-spec: SPEC-001#I69 - the share is a fraction in [0, 1] and grows with the re-encode: more head per copied second, a bigger w.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheShare_GrowsWithTheHead_AndStaysAFraction()
    {
        var shares = new[] { 0.1, 0.5, 1.0, 5.0, 50.0 }
            .Select(h => new SmartCutCost(h, 100).ReencodeShare).ToList();

        shares.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        shares.Should().OnlyContain(s => s > 0 && s < 1);
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ANullPlan_IsRefused()
    {
        var act = () => SmartCutCost.Of(null!, S(60));

        act.Should().Throw<ArgumentNullException>();
    }

    // serves-spec: SPEC-001#I69 - the factor is a named public constant, positive: a second of re-encoded video always costs more than
    // a second of copied video.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheEncodeCostFactor_IsPositive_AndMakesAHeadCostMoreThanACopyOfTheSameLength()
    {
        SmartCutEngine.EncodeCostFactor.Should().BeGreaterThan(1);
        new SmartCutCost(1, 0).Total.Should().BeGreaterThan(new SmartCutCost(0, 1).Total);
    }
}
