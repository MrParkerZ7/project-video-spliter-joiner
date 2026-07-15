using FluentAssertions;
using VideoSplitJoiner.Core.Detect;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Unit tests asserting the T-006 DECODE-ONLY invariant on the built ffmpeg args: every
/// detection command targets the <c>null</c> muxer and never a real output file or encoder.
/// </summary>
public class DetectArgsBuilderTests
{
    private const string In = @"C:\videos\in.mp4";

    [Fact]
    public void Black_TargetsNullMuxer_NoOutputFile()
    {
        var tokens = DetectArgsBuilder.Black(In, TimeSpan.FromSeconds(0.1), 0.98).ToList();

        DetectArgsBuilder.SatisfiesDecodeOnlyInvariant(tokens).Should().BeTrue();
        tokens.Should().ContainInConsecutiveOrder("-f", "null", "-");
        tokens.Should().Contain(t => t.Contains("blackdetect", StringComparison.Ordinal));
        tokens[^1].Should().Be("-"); // last positional is the null sink, not a file
    }

    [Fact]
    public void White_UsesNegateThenBlackdetect_DecodeOnly()
    {
        var tokens = DetectArgsBuilder.White(In, TimeSpan.FromSeconds(0.1), 0.98).ToList();

        DetectArgsBuilder.SatisfiesDecodeOnlyInvariant(tokens).Should().BeTrue();
        tokens.Should().Contain(t => t.Contains("negate", StringComparison.Ordinal)
                                     && t.Contains("blackdetect", StringComparison.Ordinal));
    }

    [Fact]
    public void Scene_UsesSelectSceneAndMetadataPrint_DecodeOnly()
    {
        var tokens = DetectArgsBuilder.Scene(In, 0.4).ToList();

        DetectArgsBuilder.SatisfiesDecodeOnlyInvariant(tokens).Should().BeTrue();
        tokens.Should().Contain(t => t.Contains("select=", StringComparison.Ordinal)
                                     && t.Contains("scene", StringComparison.Ordinal)
                                     && t.Contains("metadata=print", StringComparison.Ordinal));
    }

    [Fact]
    public void AllPasses_ContainNoRealOutputPathOrEncoder()
    {
        foreach (var tokens in new[]
                 {
                     DetectArgsBuilder.Black(In, TimeSpan.FromSeconds(0.1), 0.98).ToList(),
                     DetectArgsBuilder.White(In, TimeSpan.FromSeconds(0.1), 0.98).ToList(),
                     DetectArgsBuilder.Scene(In, 0.4).ToList(),
                 })
        {
            // No token names a file with a media extension as an OUTPUT (the only path is the -i input).
            tokens.Count(t => t.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
            tokens.Should().NotContain("libx264");
            tokens.Should().NotContain("-c:v");
        }
    }

    [Fact]
    public void SatisfiesDecodeOnlyInvariant_RejectsAFileOutput()
    {
        var bad = new[] { "-hide_banner", "-i", In, "-vf", "blackdetect", @"C:\out\real.mp4" };
        DetectArgsBuilder.SatisfiesDecodeOnlyInvariant(bad).Should().BeFalse();
    }

    [Fact]
    public void SatisfiesDecodeOnlyInvariant_RejectsEncoderToken()
    {
        var bad = new[] { "-hide_banner", "-i", In, "-c:v", "libx264", "-f", "null", "-" };
        DetectArgsBuilder.SatisfiesDecodeOnlyInvariant(bad).Should().BeFalse();
    }
}
