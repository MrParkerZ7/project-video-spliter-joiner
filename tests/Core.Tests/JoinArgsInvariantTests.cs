using FluentAssertions;
using VideoSplitJoiner.Core.Join;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// The KEY GUARD for T-005: every ffmpeg command the join engine builds must be a pure
/// stream-copy (<c>-c copy</c>) via the concat demuxer, with NO encoder token. Plus the
/// concat-list escaping. All inspect built values directly — no binary needed.
/// </summary>
public class JoinArgsInvariantTests
{
    [Fact]
    public void ConcatCopy_Command_ContainsCopy_AndConcatDemuxer_AndNoEncoderFlag()
    {
        var args = JoinArgsBuilder.ConcatCopy(@"C:\tmp\list.txt", @"C:\out\joined.mp4");
        var tokens = args.ToList();

        tokens.Should().Contain("copy");
        tokens.Should().Contain("-f");
        tokens.Should().Contain("concat");
        tokens.Should().Contain("-safe");
        tokens.Should().Contain("0");
        tokens.Should().Contain("-map");
        tokens.Should().Contain(@"C:\out\joined.mp4");

        foreach (var forbidden in JoinArgsBuilder.ForbiddenEncoderTokens)
        {
            tokens.Should().NotContain(forbidden, $"join must never re-encode ('{forbidden}' is an encoder flag)");
        }

        JoinArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    [Fact]
    public void SatisfiesCopyInvariant_RejectsEncoderContamination()
    {
        var contaminated = new[] { "-f", "concat", "-i", "list.txt", "-c", "copy", "-c:v", "libx264", "out.mp4" };
        JoinArgsBuilder.SatisfiesCopyInvariant(contaminated).Should().BeFalse();
    }

    [Fact]
    public void SatisfiesCopyInvariant_RejectsMissingCopy()
    {
        var noCopy = new[] { "-f", "concat", "-i", "list.txt", "-map", "0", "out.mp4" };
        JoinArgsBuilder.SatisfiesCopyInvariant(noCopy).Should().BeFalse();
    }

    [Fact]
    public void RenderConcatList_EmitsAbsoluteFileLines_InOrder()
    {
        var body = JoinArgsBuilder.RenderConcatList(new[] { @"C:\a.mp4", @"C:\b.mp4" });

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("file '").And.Contain("a.mp4");
        lines[1].Should().Contain("b.mp4");
    }

    [Fact]
    public void RenderConcatList_EscapesSpaceAndSingleQuoteInPath()
    {
        // A path with a space and a single quote must be single-quoted with the quote escaped
        // as '\'' (close, escaped-quote, reopen). GetFullPath keeps rooted paths as-is.
        var tricky = @"C:\dir with space\it's a clip.mp4";
        var body = JoinArgsBuilder.RenderConcatList(new[] { tricky });

        // The single quote in "it's" becomes '\'' inside the surrounding single quotes.
        body.Should().Contain(@"it'\''s a clip.mp4");
        // Whole line is single-quote wrapped.
        body.Should().StartWith("file '").And.EndWith("'\n");
        // Space is preserved verbatim inside the quotes (no extra escaping).
        body.Should().Contain("dir with space");
    }

    [Fact]
    public void QuoteConcatPath_WrapsAndEscapes()
    {
        JoinArgsBuilder.QuoteConcatPath("plain.mp4").Should().Be("'plain.mp4'");
        JoinArgsBuilder.QuoteConcatPath("a'b.mp4").Should().Be(@"'a'\''b.mp4'");
    }
}
