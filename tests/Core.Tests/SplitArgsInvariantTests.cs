using FluentAssertions;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// The KEY GUARD for T-004: every ffmpeg command the split engine builds must be a pure
/// stream-copy (<c>-c copy</c>) and must contain NO encoder flag. These tests inspect the
/// built <c>FfmpegArgs</c> token list directly — no binary needed.
/// </summary>
public class SplitArgsInvariantTests
{
    private static readonly IReadOnlyList<TimeSpan> Cuts = new[]
    {
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6),
    };

    [Fact]
    public void SegmentMuxer_Command_ContainsCopy_AndNoEncoderFlag()
    {
        var args = SplitArgsBuilder.SegmentMuxer(@"C:\in.mp4", Cuts, @"C:\out\part%03d.mp4");
        var tokens = args.ToList();

        tokens.Should().Contain("copy");
        tokens.Should().Contain("-f");
        tokens.Should().Contain("segment");
        tokens.Should().Contain("-map");
        tokens.Should().Contain("0");
        tokens.Should().Contain("-segment_times");
        tokens.Should().Contain("3,6");

        // No encoder token of any kind.
        foreach (var forbidden in SplitArgsBuilder.ForbiddenEncoderTokens)
        {
            tokens.Should().NotContain(forbidden, $"split must never re-encode ('{forbidden}' is an encoder flag)");
        }

        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    [Fact]
    public void SegmentMuxer_ForTsInput_StillPureCopy_NoEncoderFlag()
    {
        // T-035: mpegts/.ts inputs go through the SAME builder — the copy invariant must hold for
        // the .ts path exactly as for mp4 (no mpegts-specific re-encode ever leaks in).
        var args = SplitArgsBuilder.SegmentMuxer(@"F:\broadcast\映像.ts", Cuts, @"F:\out\part%03d.ts");
        var tokens = args.ToList();

        tokens.Should().Contain("copy");
        tokens.Should().Contain("-f");
        tokens.Should().Contain("segment");

        foreach (var forbidden in SplitArgsBuilder.ForbiddenEncoderTokens)
        {
            tokens.Should().NotContain(forbidden, $"the .ts split path must never re-encode ('{forbidden}')");
        }

        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    [Fact]
    public void PerSegmentFallback_Command_ContainsCopy_AndNoEncoderFlag()
    {
        var args = SplitArgsBuilder.PerSegment(
            @"C:\in.mp4", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), @"C:\out\seg.mp4");
        var tokens = args.ToList();

        tokens.Should().Contain("copy");
        tokens.Should().Contain("-ss");
        tokens.Should().Contain("-to");
        tokens.Should().Contain("-avoid_negative_ts");

        foreach (var forbidden in SplitArgsBuilder.ForbiddenEncoderTokens)
        {
            tokens.Should().NotContain(forbidden);
        }

        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    [Fact]
    public void PerSegmentFallback_ToEndOfFile_OmitsTo()
    {
        var args = SplitArgsBuilder.PerSegment(@"C:\in.mp4", TimeSpan.FromSeconds(6), null, @"C:\out\seg.mp4");
        var tokens = args.ToList();

        tokens.Should().NotContain("-to");
        tokens.Should().Contain("copy");
        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    [Fact]
    public void SatisfiesCopyInvariant_RejectsEncoderContamination()
    {
        // A hand-built list that copies but ALSO carries an encoder flag must be rejected.
        var contaminated = new[] { "-i", "in.mp4", "-c", "copy", "-c:v", "libx264", "out.mp4" };
        SplitArgsBuilder.SatisfiesCopyInvariant(contaminated).Should().BeFalse();
    }

    [Fact]
    public void SatisfiesCopyInvariant_RejectsMissingCopy()
    {
        var noCopy = new[] { "-i", "in.mp4", "-map", "0", "out.mp4" };
        SplitArgsBuilder.SatisfiesCopyInvariant(noCopy).Should().BeFalse();
    }

    // ---- todo-automate gap coverage (SPEC-001) ----

    // SPEC-001#I15 — SegmentMuxer with zero interior cuts throws SplitException.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void SegmentMuxer_ZeroInteriorCuts_Throws()
    {
        var act = () => SplitArgsBuilder.SegmentMuxer(@"C:\in.mp4", Array.Empty<TimeSpan>(), @"C:\out\part%03d.mp4");
        act.Should().Throw<SplitException>().WithMessage("*at least one interior cut time*");
    }

    // SPEC-001#I16 — PerSegment places -ss BEFORE -i (an input-side seek).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_PlacesSsBeforeInput()
    {
        var tokens = SplitArgsBuilder.PerSegment(
            @"C:\in.mp4", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), @"C:\out\seg.mp4").ToList().ToList();

        var ss = tokens.IndexOf("-ss");
        var i = tokens.IndexOf("-i");
        ss.Should().BeGreaterThanOrEqualTo(0);
        i.Should().BeGreaterThan(ss, "-ss must precede -i so the seek is an input-side seek");
    }

    // SPEC-001#I17 — PerSegment emits -to == (end - start) as a DURATION relative to the seek, not the
    // absolute source end (emitting the absolute end would over-run by `start`).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_To_IsDurationRelativeToSeek_NotAbsoluteEnd()
    {
        var tokens = SplitArgsBuilder.PerSegment(
            @"C:\in.mp4", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), @"C:\out\seg.mp4").ToList().ToList();

        var to = tokens.IndexOf("-to");
        to.Should().BeGreaterThanOrEqualTo(0);
        tokens[to + 1].Should().Be("3", "-to is the duration (6 - 3), not the absolute end 6");
    }

    // SPEC-001#I17 (boundary) — an end earlier than start clamps the emitted -to duration to 0.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_EndBeforeStart_ClampsToZeroDuration()
    {
        var tokens = SplitArgsBuilder.PerSegment(
            @"C:\in.mp4", TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(3), @"C:\out\seg.mp4").ToList().ToList();

        var to = tokens.IndexOf("-to");
        to.Should().BeGreaterThanOrEqualTo(0);
        tokens[to + 1].Should().Be("0", "a negative (end - start) duration clamps to 0");
    }
}
