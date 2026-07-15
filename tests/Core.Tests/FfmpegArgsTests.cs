using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

public class FfmpegArgsTests
{
    [Fact]
    public void ForFfmpeg_AlwaysEmitsHideBannerAndNostdin()
    {
        var list = FfmpegArgs.ForFfmpeg().ToList();

        list.Should().Contain("-hide_banner");
        list.Should().Contain("-nostdin");
    }

    [Fact]
    public void ForFfprobe_EmitsHideBanner()
    {
        var list = FfmpegArgs.ForFfprobe().ToList();

        list.Should().Contain("-hide_banner");
    }

    [Fact]
    public void Input_PathWithSpacesAndUnicode_IsASingleUnsplitElement()
    {
        // Space AND a non-ASCII char in the path.
        const string path = @"C:\My Videos\clip — final.mp4";

        var list = FfmpegArgs.ForFfmpeg().Input(path).ToList();

        list.Should().Contain(path, "the path must survive as one argument token, unsplit");
        list.Should().ContainInOrder("-i", path);
    }

    [Fact]
    public void Output_PathWithSpacesAndUnicode_IsASingleUnsplitElement()
    {
        const string path = @"D:\out dir\résultat 01.mkv";

        var list = FfmpegArgs.ForFfmpeg().Output(path).ToList();

        list.Should().Contain(path);
    }

    [Fact]
    public void Raw_KeepsEachArgumentAsItsOwnToken()
    {
        var list = FfmpegArgs.ForFfmpeg().Raw("-t", "00:00:10", "-c", "copy").ToList();

        list.Should().ContainInOrder("-t", "00:00:10", "-c", "copy");
    }

    [Fact]
    public void FluentChain_PreservesOrder()
    {
        const string input = @"C:\in\a b.mp4";
        const string output = @"C:\out\c d.mp4";

        var list = FfmpegArgs.ForFfmpeg()
            .Input(input)
            .Raw("-c", "copy")
            .Output(output)
            .ToList();

        // -hide_banner, -nostdin, -i, input, -c, copy, output
        list.Should().ContainInOrder("-hide_banner", "-nostdin", "-i", input, "-c", "copy", output);
    }
}
