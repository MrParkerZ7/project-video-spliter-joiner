using System;
using System.Collections.Generic;
using FluentAssertions;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Unit tests for the pure sample-layout formatting helpers (T-059): the info-card meta line, the
/// header format badge, the Join estimated-result sums, and the shared size/duration formatters.
/// No ffmpeg, no GUI — all inputs are constructed records.
/// </summary>
public sealed class MediaFormatTests
{
    private static MediaInfo Info(TimeSpan duration, string container, params StreamInfo[] video) =>
        new(duration, container, video, Array.Empty<StreamInfo>());

    private static StreamInfo Video(string codec, int w = 1920, int h = 1080) =>
        new(0, codec, "video", w, h, "yuv420p", null, null, "1/30");

    // ---- FormatSize -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-5, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1536, "2 KB")]            // 1.5 KB rounds to 2 KB (no decimals for KB)
    [InlineData(1048576, "1 MB")]         // exactly 1 MB
    [InlineData(1503238553, "1.4 GB")]    // the sample's "1.4 GB"
    [InlineData(1099511627776, "1 TB")]
    public void FormatSize_ProducesBinaryUnits(long bytes, string expected)
    {
        MediaFormat.FormatSize(bytes).Should().Be(expected);
    }

    // ---- FormatDuration ---------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(600, "10:00")]            // the sample's "10:00"
    [InlineData(3661, "1:01:01")]         // past an hour → H:MM:SS
    public void FormatDuration_IsCompactClock(int seconds, string expected)
    {
        MediaFormat.FormatDuration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void FormatDuration_NegativeUsesMagnitude()
    {
        MediaFormat.FormatDuration(TimeSpan.FromSeconds(-65)).Should().Be("1:05");
    }

    // ---- ShortContainer ---------------------------------------------------------------------

    [Theory]
    [InlineData("matroska", "matroska")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "mov")]
    [InlineData("  mp4 , mov ", "mp4")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ShortContainer_TakesFirstAlias(string? input, string expected)
    {
        MediaFormat.ShortContainer(input).Should().Be(expected);
    }

    // ---- FriendlyCodec ----------------------------------------------------------------------

    [Theory]
    [InlineData("hevc", "HEVC")]
    [InlineData("h265", "HEVC")]
    [InlineData("h264", "H.264")]
    [InlineData("av1", "AV1")]
    [InlineData("somethingnew", "somethingnew")]
    public void FriendlyCodec_MapsKnownAndPassesThroughUnknown(string codec, string expected)
    {
        MediaFormat.FriendlyCodec(codec).Should().Be(expected);
    }

    // ---- MetaLine ---------------------------------------------------------------------------

    [Fact]
    public void MetaLine_MatchesSample()
    {
        var info = Info(TimeSpan.FromMinutes(10), "matroska", Video("hevc"));
        MediaFormat.MetaLine(info, 1503238553).Should().Be("matroska · 10:00 · 1.4 GB");
    }

    [Fact]
    public void MetaLine_DropsSizeWhenUnknown()
    {
        var info = Info(TimeSpan.FromMinutes(10), "matroska", Video("hevc"));
        MediaFormat.MetaLine(info, 0).Should().Be("matroska · 10:00");
    }

    [Fact]
    public void MetaLine_ShortensCommaJoinedContainer()
    {
        var info = Info(TimeSpan.FromSeconds(65), "mov,mp4,m4a", Video("h264"));
        MediaFormat.MetaLine(info, 2048).Should().Be("mov · 1:05 · 2 KB");
    }

    // ---- Badge ------------------------------------------------------------------------------

    [Fact]
    public void Badge_HevcMatroska()
    {
        // ffprobe reports MKV's container format_name as "matroska"; the badge upper-cases the short
        // container. (The sample's "HEVC · MKV" is the same shape with a friendlier container label.)
        var info = Info(TimeSpan.FromMinutes(10), "matroska", Video("hevc"));
        MediaFormat.Badge(info).Should().Be("HEVC · MATROSKA");
    }

    [Fact]
    public void Badge_UsesShortContainerUpperCased()
    {
        var info = Info(TimeSpan.FromSeconds(30), "mov,mp4,m4a", Video("h264"));
        MediaFormat.Badge(info).Should().Be("H.264 · MOV");
    }

    [Fact]
    public void Badge_NoVideoStream_FallsBackToContainerOnly()
    {
        var audioOnly = new MediaInfo(TimeSpan.FromSeconds(30), "matroska", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>());
        MediaFormat.Badge(audioOnly).Should().Be("MATROSKA");
    }

    [Fact]
    public void Badge_NullInfo_IsNull()
    {
        MediaFormat.Badge(null).Should().BeNull();
    }

    // ---- Estimate ---------------------------------------------------------------------------

    [Fact]
    public void Estimate_SumsDurationAndSize()
    {
        var clips = new List<(TimeSpan, long)>
        {
            (TimeSpan.FromMinutes(5), 1_000_000),
            (TimeSpan.FromMinutes(3), 2_000_000),
        };

        var (total, bytes) = MediaFormat.Estimate(clips);

        total.Should().Be(TimeSpan.FromMinutes(8));
        bytes.Should().Be(3_000_000);
    }

    [Fact]
    public void Estimate_TreatsUnknownDurationAndSizeAsZero()
    {
        var clips = new List<(TimeSpan, long)>
        {
            (TimeSpan.FromMinutes(5), 1_000_000),
            (TimeSpan.Zero, 0),            // not-yet-probed clip contributes nothing
            (TimeSpan.FromSeconds(-1), -50),
        };

        var (total, bytes) = MediaFormat.Estimate(clips);

        total.Should().Be(TimeSpan.FromMinutes(5));
        bytes.Should().Be(1_000_000);
    }

    [Fact]
    public void Estimate_EmptyIsZero()
    {
        var (total, bytes) = MediaFormat.Estimate(Array.Empty<(TimeSpan, long)>());
        total.Should().Be(TimeSpan.Zero);
        bytes.Should().Be(0);
    }
}
