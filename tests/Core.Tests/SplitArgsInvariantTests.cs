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
}
