using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

public class FfmpegProgressTests
{
    [Fact]
    public void Feed_KnownTotal_ComputesCorrectFraction()
    {
        // 10-second total; time=00:00:05.000 => 0.5
        var parser = new FfmpegProgress(TimeSpan.FromSeconds(10));

        var f = parser.Feed("frame=  120 fps= 24 q=28.0 size=  256kB time=00:00:05.000 bitrate= 1.0kbits/s speed=1x");

        f.Should().NotBeNull();
        f!.Value.Should().BeApproximately(0.5, 1e-6);
        parser.Current.Should().BeApproximately(0.5, 1e-6);
    }

    [Fact]
    public void Feed_ElapsedBeyondTotal_ClampsToOne()
    {
        var parser = new FfmpegProgress(TimeSpan.FromSeconds(10));

        var f = parser.Feed("time=00:00:15.000");

        f.Should().NotBeNull();
        f!.Value.Should().Be(1.0);
        parser.Current.Should().Be(1.0);
    }

    [Fact]
    public void Feed_NonIncreasingTime_DoesNotDecreaseReportedValue()
    {
        var parser = new FfmpegProgress(TimeSpan.FromSeconds(100));

        var a = parser.Feed("time=00:00:80.000"); // 0.8
        var b = parser.Feed("time=00:00:40.000"); // would be 0.4 — must be ignored

        a.Should().NotBeNull();
        a!.Value.Should().BeApproximately(0.8, 1e-6);
        b.Should().BeNull("progress is monotonic and must not go backwards");
        parser.Current.Should().BeApproximately(0.8, 1e-6, "the lower later value must not reduce current");
    }

    [Fact]
    public void Feed_Advances_ReportsIncreasingValues()
    {
        var parser = new FfmpegProgress(TimeSpan.FromSeconds(60));

        parser.Feed("time=00:00:15.000")!.Value.Should().BeApproximately(0.25, 1e-6);
        parser.Feed("time=00:00:30.000")!.Value.Should().BeApproximately(0.50, 1e-6);
        parser.Feed("time=00:00:45.000")!.Value.Should().BeApproximately(0.75, 1e-6);
        parser.Current.Should().BeApproximately(0.75, 1e-6);
    }

    [Fact]
    public void Feed_UnknownTotal_StaysZeroAndReportsNothing()
    {
        var parser = new FfmpegProgress(null);

        parser.Feed("time=00:00:05.000").Should().BeNull();
        parser.Current.Should().Be(0.0);
    }

    [Fact]
    public void Feed_LineWithoutTimeToken_ReturnsNull()
    {
        var parser = new FfmpegProgress(TimeSpan.FromSeconds(10));

        parser.Feed("Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':").Should().BeNull();
        parser.Current.Should().Be(0.0);
    }
}
