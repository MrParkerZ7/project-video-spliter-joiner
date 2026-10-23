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
        // T-219 contract change: SegmentMuxer takes the half-frame delta; the copy assertions are unchanged.
        var args = SplitArgsBuilder.SegmentMuxer(@"C:\in.mp4", Cuts, @"C:\out\part%03d.mp4", TimeSpan.FromSeconds(1.0 / 60));
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
        var args = SplitArgsBuilder.SegmentMuxer(@"F:\broadcast\映像.ts", Cuts, @"F:\out\part%03d.ts", TimeSpan.FromSeconds(1.0 / 60));
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
        // T-219 contract change: PerSegment takes the source start time; the copy assertions are unchanged.
        var args = SplitArgsBuilder.PerSegment(
            @"C:\in.mp4", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1.445333), @"C:\out\seg.mp4");
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
        var args = SplitArgsBuilder.PerSegment(@"C:\in.mp4", TimeSpan.FromSeconds(6), null, TimeSpan.Zero, @"C:\out\seg.mp4");
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
        var act = () => SplitArgsBuilder.SegmentMuxer(@"C:\in.mp4", Array.Empty<TimeSpan>(), @"C:\out\part%03d.mp4", TimeSpan.Zero);
        act.Should().Throw<SplitException>().WithMessage("*at least one interior cut time*");
    }

    // SPEC-001#I16 — PerSegment places -ss BEFORE -i (an input-side seek).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_PlacesSsBeforeInput()
    {
        var tokens = SplitArgsBuilder.PerSegment(
            @"C:\in.mp4", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.Zero, @"C:\out\seg.mp4").ToList().ToList();

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
            @"C:\in.mp4", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.Zero, @"C:\out\seg.mp4").ToList().ToList();

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
            @"C:\in.mp4", TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(3), TimeSpan.Zero, @"C:\out\seg.mp4").ToList().ToList();

        var to = tokens.IndexOf("-to");
        to.Should().BeGreaterThanOrEqualTo(0);
        tokens[to + 1].Should().Be("0", "a negative (end - start) duration clamps to 0");
    }

    // SPEC-001#I19 — the PerSegment copy command maps ALL streams (`-map 0`), copies (`-c copy`), and
    // zeroes negative timestamps (`-avoid_negative_ts make_zero`). The coverage above asserts those
    // FLAGS are present but never their VALUES — a `-map 1` (video only, audio silently dropped) or an
    // `-avoid_negative_ts auto` would sail straight through the existing guards.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_MapsAllStreams_AndZeroesNegativeTimestamps()
    {
        var tokens = SplitArgsBuilder.PerSegment(
            @"C:\in.mp4", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.Zero, @"C:\out\seg.mp4").ToList().ToList();

        var map = tokens.IndexOf("-map");
        map.Should().BeGreaterThanOrEqualTo(0);
        tokens[map + 1].Should().Be("0", "-map 0 keeps EVERY stream — video, audio, subtitles — not just the first");

        var codec = tokens.IndexOf("-c");
        codec.Should().BeGreaterThanOrEqualTo(0);
        tokens[codec + 1].Should().Be("copy", "the segment is a pure stream copy, never a re-encode");

        var avoid = tokens.IndexOf("-avoid_negative_ts");
        avoid.Should().BeGreaterThanOrEqualTo(0);
        tokens[avoid + 1].Should().Be("make_zero", "the copied segment must start at zero, not at a negative pts");

        tokens[^1].Should().Be(@"C:\out\seg.mp4", "the output path is the last positional token");

        // …and the bare `copy` token is present with no encoder contamination.
        tokens.Should().Contain("copy");
        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    // ---- T-222: the segment muxer's output pattern -------------------------------------------------------------

    // serves-spec: SPEC-001#I73 - ffmpeg expands every percent sign of the pattern; the folder and the extension are literal text.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(@"D:\Videos\100% done", @"D:\Videos\100%% done")]
    [InlineData(@"D:\Videos\plain folder", @"D:\Videos\plain folder")]
    [InlineData(@"D:\out\%03d", @"D:\out\%%03d")]
    [InlineData(@"D:\out\100%%", @"D:\out\100%%%%")]
    [InlineData(@"\\server\share\50% off", @"\\server\share\50%% off")]
    [InlineData(".mp4", ".mp4")]
    [InlineData(".m%p4", ".m%%p4")]
    [InlineData("", "")]
    public void EscapeSegmentPatternLiteral_DoublesEveryPercentSign(string literal, string expected)
    {
        SplitArgsBuilder.EscapeSegmentPatternLiteral(literal).Should().Be(expected);
    }

    // serves-spec: SPEC-001#I73 - the pattern keeps exactly one live placeholder, the part number, and gives the real folder back.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(@"D:\Videos\out", ".mp4")]
    [InlineData(@"D:\Videos\100% done\.vsj-split-abc", ".mp4")]
    [InlineData(@"D:\Videos\%03d\.vsj-split-abc", ".ts")]
    [InlineData(@"D:\Videos\100%%\.vsj-split-abc", ".mkv")]
    [InlineData(@"D:\Videos\out", ".m%p4")]
    public void SegmentPattern_HasOneLivePlaceholder_AndExpandsToTheRealFolder(string dir, string extension)
    {
        var pattern = SplitArgsBuilder.SegmentPattern(dir, extension);

        pattern.Should().EndWith(@"\part%03d" + extension.Replace("%", "%%", StringComparison.Ordinal),
            "the placeholder itself is never escaped, or ffmpeg would write a file literally named part%03d");
        ExpandLikeFfmpeg(pattern, 7).Should().Be(Path.Combine(dir, "part007" + extension),
            "ffmpeg's expansion of the pattern is the real folder and the real file name");
    }

    // serves-spec: SPEC-001#I73 - the whole command carries the escaped pattern as its last token, the copy invariant intact.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void SegmentMuxer_WithAPercentFolder_PassesTheEscapedPattern_AndStaysPureCopy()
    {
        var pattern = SplitArgsBuilder.SegmentPattern(@"D:\Videos\100% done\.vsj-split-abc", ".mp4");
        var tokens = SplitArgsBuilder.SegmentMuxer(@"C:\in.mp4", Cuts, pattern, TimeSpan.FromSeconds(1.0 / 60)).ToList();

        tokens[^1].Should().Be(@"D:\Videos\100%% done\.vsj-split-abc\part%03d.mp4");
        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    /// <summary>
    /// A model of the template expansion ffmpeg's segment muxer applies to its output name (libavformat
    /// <c>av_get_frame_filename</c>): <c>%%</c> is a percent sign, <c>%03d</c> the zero-padded number, any other
    /// percent sign an invalid template.
    /// </summary>
    private static string ExpandLikeFfmpeg(string pattern, int number)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '%')
            {
                sb.Append(pattern[i]);
            }
            else if (i + 1 < pattern.Length && pattern[i + 1] == '%')
            {
                sb.Append('%');
                i++;
            }
            else if (string.CompareOrdinal(pattern, i, "%03d", 0, 4) == 0)
            {
                sb.Append(number.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
                i += 3;
            }
            else
            {
                throw new InvalidOperationException($"Invalid segment filename template '{pattern}' (a stray percent sign at {i})");
            }
        }

        return sb.ToString();
    }
}
