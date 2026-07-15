using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

public class FfmpegBinaryLocatorTests
{
    [Fact]
    public void OverridePathThatExists_ResolvesToThatPath()
    {
        // Use this test assembly's own file as a stand-in "existing binary".
        var existing = typeof(FfmpegBinaryLocatorTests).Assembly.Location;
        File.Exists(existing).Should().BeTrue("test setup requires an existing file");

        var locator = new FfmpegBinaryLocator(ffmpegOverride: existing, ffprobeOverride: existing);

        locator.ResolveFfmpeg().Should().Be(existing);
        locator.ResolveFfprobe().Should().Be(existing);
    }

    [Fact]
    public void BogusOverride_Throws_FfmpegNotFound()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Exists(bogus).Should().BeFalse();

        var locator = new FfmpegBinaryLocator(ffmpegOverride: bogus);

        var act = () => locator.ResolveFfmpeg();

        act.Should().Throw<FfmpegNotFoundException>()
            .WithMessage("*does not exist*");
    }

    [Fact]
    public void NoOverride_UnlikelyToolName_FallsThroughAndThrowsWhenNotOnPath()
    {
        // A locator with no override resolves ffmpeg via app-local + PATH. On a machine
        // without ffmpeg installed on PATH this throws; on one WITH it, it returns "ffmpeg".
        // Either way it must not return a nonexistent explicit path. We assert the typed
        // behavior: when neither app-local nor PATH has it, it throws the typed exception.
        // To make this deterministic we probe via a guaranteed-absent tool name is not
        // possible (the locator is ffmpeg/ffprobe-specific), so we only assert the
        // successful-throw contract when the environment lacks the binary.
        var locator = new FfmpegBinaryLocator();

        try
        {
            var resolved = locator.ResolveFfmpeg();
            // Present on PATH — bare name is acceptable.
            resolved.Should().Be("ffmpeg");
        }
        catch (FfmpegNotFoundException ex)
        {
            // Absent — must be the typed exception with a helpful message.
            ex.Message.Should().Contain("Could not locate 'ffmpeg'");
        }
    }
}
