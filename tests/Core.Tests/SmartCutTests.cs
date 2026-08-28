using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-124 (epic G-042) — frame-exact "smart" cutting. The user set the intro at 5s on a ~4s keyframe
/// grid and got 4s, because a stream-copied segment must START on a keyframe. Smart cutting honours the
/// requested time by re-encoding only the fragment between the request and the next keyframe and
/// stream-copying the rest, so the cost is bounded by one GOP rather than the whole file.
/// </summary>
public sealed class SmartCutTests
{
    /// <summary>The grid from the bug report: keyframes every 4s.</summary>
    private static List<TimeSpan> Grid4s(double totalSeconds = 60) =>
        Enumerable.Range(0, (int)(totalSeconds / 4) + 1).Select(i => TimeSpan.FromSeconds(i * 4)).ToList();

    private static MediaInfo H264Aac() => new(
        TimeSpan.FromSeconds(60),
        "mov,mp4,m4a,3gp,3g2,mj2",
        new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/12800") },
        new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

    // ---- The planner: the reported case now lands exactly ------------------------------------

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RequestAt5s_OnA4sGrid_ReencodesOnlyUpToTheNextKeyframe()
    {
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), null, Grid4s());

        plan.Strategy.Should().Be(SmartCutStrategy.HeadReencode);
        plan.Start.Should().Be(TimeSpan.FromSeconds(5), "the cut honours EXACTLY what the user asked for");
        plan.HeadEnd.Should().Be(TimeSpan.FromSeconds(8), "the copyable tail begins at the next keyframe");
        plan.ReencodedDuration.Should().Be(TimeSpan.FromSeconds(3), "only the head fragment is re-encoded");
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RequestAt6s_ProducesADifferentPlanThan5s_TheReportedSymptomIsGone()
    {
        var at5 = SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), null, Grid4s());
        var at6 = SmartCutPlanner.Plan(TimeSpan.FromSeconds(6), null, Grid4s());

        at5.Start.Should().Be(TimeSpan.FromSeconds(5));
        at6.Start.Should().Be(TimeSpan.FromSeconds(6));
        at6.Should().NotBe(at5, "moving the playhead now genuinely changes the result — the whole complaint");
        at6.ReencodedDuration.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RequestAlreadyOnAKeyframe_TakesThePureCopyPath_NoReencode()
    {
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(8), null, Grid4s());

        plan.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        plan.HasReencode.Should().BeFalse("nothing is re-encoded when the lossless cut is already exact");
        plan.ReencodedDuration.Should().Be(TimeSpan.Zero);
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ARequestWithinToleranceOfAKeyframe_CountsAsOnIt()
    {
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(8).Add(TimeSpan.FromMilliseconds(4)), null, Grid4s());

        plan.Strategy.Should().Be(SmartCutStrategy.PureCopy, "float/UI rounding must not force a pointless re-encode");
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ARequestInsideTheFinalGop_HasNoCopyableTail_SoTheRangeIsFullyReencoded()
    {
        // Keyframes end at 8s; a request at 9s with end 10s has no keyframe ahead of it.
        var keyframes = new List<TimeSpan> { TimeSpan.Zero, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) };

        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10), keyframes);

        plan.Strategy.Should().Be(SmartCutStrategy.FullReencode);
        plan.HeadEnd.Should().BeNull();
        plan.ReencodedDuration.Should().Be(TimeSpan.FromSeconds(1), "still bounded — it is a sub-GOP range");
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AKeyframeAtOrAfterTheRequestedEnd_IsNotAUsableTailBoundary()
    {
        // Next keyframe (8s) is at/after the requested end (8s) → nothing left to copy.
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), Grid4s());

        plan.Strategy.Should().Be(SmartCutStrategy.FullReencode);
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void EmptyKeyframes_FallBackToFullReencode_NeverACrash()
    {
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), Array.Empty<TimeSpan>());

        plan.Strategy.Should().Be(SmartCutStrategy.FullReencode);
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void InvalidRanges_AreRejected()
    {
        var act1 = () => SmartCutPlanner.Plan(TimeSpan.FromSeconds(-1), null, Grid4s());
        act1.Should().Throw<ArgumentOutOfRangeException>();

        var act2 = () => SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), Grid4s());
        act2.Should().Throw<ArgumentException>("an empty or inverted range is not a cut");
    }

    // ---- The args: the head matches the source, the tail stays a pure copy ---------------------

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void HeadReencode_MatchesTheSourceStreams_SoTheConcatWillAccept()
    {
        var args = SmartCutArgsBuilder.HeadReencode(
            @"C:\v\clip.mp4", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8),
            H264Aac(), "libx264", "aac", @"C:\v\head.mp4").ToList();

        args.Should().Contain("libx264").And.Contain("yuv420p").And.Contain("1920x1080");
        args.Should().Contain("aac").And.Contain("48000").And.Contain("2");
        // Output seek (-ss AFTER -i) is what makes the cut frame-exact.
        var list = args.ToList();
        list.FindIndex(t => t == "-ss").Should().BeGreaterThan(list.FindIndex(t => t.EndsWith("clip.mp4")),
            "the seek must come after the input for a frame-exact start");
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TailCopy_IsStillAPureStreamCopy_NoEncoderLeaksIn()
    {
        var args = SmartCutArgsBuilder.TailCopy(
            @"C:\v\clip.mp4", TimeSpan.FromSeconds(8), null, @"C:\v\tail.mp4").ToList();

        args.Should().Contain("copy");
        SplitArgsBuilder.SatisfiesCopyInvariant(args).Should().BeTrue(
            "the tail is exactly what the lossless path would have produced");
    }

    // ---- The fallback: never guess an encoder --------------------------------------------------

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void KnownCodecs_ResolveToEncoders()
    {
        SmartCutArgsBuilder.TryResolveEncoders(H264Aac(), out var v, out var a, out var why)
            .Should().BeTrue();
        v.Should().Be("libx264");
        a.Should().Be("aac");
        why.Should().BeNull();
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AnUnmappableCodec_ReportsAFallback_RatherThanGuessing()
    {
        var exotic = new MediaInfo(
            TimeSpan.FromSeconds(10), "matroska",
            new[] { new StreamInfo(0, "prores_raw_hq", "video", 1920, 1080, "yuv422p10le", null, null, null) },
            Array.Empty<StreamInfo>());

        SmartCutArgsBuilder.TryResolveEncoders(exotic, out _, out _, out var why).Should().BeFalse();
        why.Should().Contain("prores_raw_hq",
            "the caller must be told WHY, so it can fall back to the lossless cut instead of shipping a corrupt file");
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ASourceWithNoStreams_CannotBeSmartCut()
    {
        var empty = new MediaInfo(
            TimeSpan.FromSeconds(10), "matroska", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>());

        SmartCutArgsBuilder.TryResolveEncoders(empty, out _, out _, out var why).Should().BeFalse();
        why.Should().NotBeNull();
    }
}
