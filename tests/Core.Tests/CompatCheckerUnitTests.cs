using FluentAssertions;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Unit tests for the PURE concat-compatibility comparison — hand-built
/// <see cref="MediaInfo"/> / <see cref="StreamInfo"/>, no ffmpeg. Confirms identical inputs
/// are compatible and each differing field (resolution, pix_fmt, codec, audio) yields a
/// mismatch naming the offending clip.
/// </summary>
public class CompatCheckerUnitTests
{
    private static StreamInfo Video(int w = 1920, int h = 1080, string codec = "h264", string pix = "yuv420p", string tb = "1/30") =>
        new(0, codec, "video", w, h, pix, null, null, tb);

    private static StreamInfo Audio(string codec = "aac", int sampleRate = 48000, int channels = 2) =>
        new(1, codec, "audio", null, null, null, sampleRate, channels, "1/48000");

    private static MediaInfo Clip(StreamInfo? video = null, StreamInfo? audio = null, double dur = 5.0)
    {
        var v = video is null ? Array.Empty<StreamInfo>() : new[] { video };
        var a = audio is null ? Array.Empty<StreamInfo>() : new[] { audio };
        return new MediaInfo(TimeSpan.FromSeconds(dur), "mov,mp4,m4a,3gp,3g2,mj2", v, a);
    }

    [Fact]
    public void IdenticalVideoClips_AreCompatible()
    {
        var report = CompatChecker.Compare(new[] { Clip(Video()), Clip(Video()), Clip(Video()) });

        report.Compatible.Should().BeTrue();
        report.Mismatches.Should().BeEmpty();
    }

    [Fact]
    public void DifferingWidth_ProducesResolutionMismatch_NamingClip2()
    {
        var report = CompatChecker.Compare(new[] { Clip(Video(1920, 1080)), Clip(Video(1280, 720)) });

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().ContainSingle(m => m.Field == "resolution");
        report.Mismatches.Single(m => m.Field == "resolution").Detail
            .Should().Contain("clip 2").And.Contain("1280x720").And.Contain("1920x1080");
    }

    [Fact]
    public void DifferingPixFmt_ProducesPixFmtMismatch()
    {
        var report = CompatChecker.Compare(new[] { Clip(Video(pix: "yuv420p")), Clip(Video(pix: "yuv444p")) });

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Field == "pix_fmt" && m.Detail.Contains("yuv444p"));
    }

    [Fact]
    public void DifferingVideoCodec_ProducesCodecMismatch()
    {
        var report = CompatChecker.Compare(new[] { Clip(Video(codec: "h264")), Clip(Video(codec: "hevc")) });

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Field == "codec" && m.Detail.Contains("hevc"));
    }

    [Fact]
    public void DifferingAudioSampleRate_ProducesAudioMismatch()
    {
        var report = CompatChecker.Compare(new[]
        {
            Clip(Video(), Audio(sampleRate: 48000)),
            Clip(Video(), Audio(sampleRate: 44100)),
        });

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Field == "audio_sample_rate" && m.Detail.Contains("44100"));
    }

    [Fact]
    public void AudioPresenceDifference_IsAMismatch()
    {
        var report = CompatChecker.Compare(new[]
        {
            Clip(Video(), Audio()),
            Clip(Video()), // no audio
        });

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Field == "audio_presence");
    }

    [Fact]
    public void IdenticalVideoAndAudio_AreCompatible()
    {
        var report = CompatChecker.Compare(new[]
        {
            Clip(Video(), Audio()),
            Clip(Video(), Audio()),
        });

        report.Compatible.Should().BeTrue();
    }

    [Fact]
    public void EmptyList_IsRejected_NoCrash()
    {
        var report = CompatChecker.Compare(Array.Empty<MediaInfo>());

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().ContainSingle(m => m.Field == "input_count");
    }

    [Fact]
    public void SingleInput_IsSelfCompatible()
    {
        var report = CompatChecker.Compare(new[] { Clip(Video()) });

        report.Compatible.Should().BeTrue();
        report.Mismatches.Should().BeEmpty();
    }

    [Fact]
    public void MultipleDifferingFields_ReportEachMismatch()
    {
        // clip 2 differs in resolution AND pix_fmt AND codec.
        var report = CompatChecker.Compare(new[]
        {
            Clip(Video(1920, 1080, "h264", "yuv420p")),
            Clip(Video(1280, 720, "hevc", "yuv444p")),
        });

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Field == "resolution");
        report.Mismatches.Should().Contain(m => m.Field == "pix_fmt");
        report.Mismatches.Should().Contain(m => m.Field == "codec");
    }

    // ---- todo-automate gap coverage (SPEC-003) ----

    // SPEC-003#I9 — a video time-base difference yields a "time_base" mismatch.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public void DifferingTimeBase_ProducesTimeBaseMismatch()
    {
        var report = CompatChecker.Compare(new[] { Clip(Video(tb: "1/30")), Clip(Video(tb: "1/25")) });

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Field == "time_base");
    }

    // SPEC-003#I10 — a video-stream presence difference yields a "video_presence" mismatch.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public void VideoPresenceDifference_IsAMismatch()
    {
        // Reference has video+audio; clip 2 is audio-only → the video presence differs.
        var report = CompatChecker.Compare(new[] { Clip(Video(), Audio()), Clip(audio: Audio()) });

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Field == "video_presence");
    }

    // SPEC-003#I11 — an audio-codec difference yields an "audio_codec" mismatch.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public void DifferingAudioCodec_ProducesAudioCodecMismatch()
    {
        var report = CompatChecker.Compare(new[]
        {
            Clip(Video(), Audio(codec: "aac")),
            Clip(Video(), Audio(codec: "mp3")),
        });

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Field == "audio_codec" && m.Detail.Contains("mp3"));
    }

    // SPEC-003#I13 — an audio channel-count difference yields an "audio_channels" mismatch.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public void DifferingAudioChannels_ProducesAudioChannelsMismatch()
    {
        var report = CompatChecker.Compare(new[]
        {
            Clip(Video(), Audio(channels: 2)),
            Clip(Video(), Audio(channels: 6)),
        });

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Field == "audio_channels");
    }

    // SPEC-003#I17 — stream-field string comparisons are case-insensitive, so clips differing only in
    // case (codec/pix_fmt/audio-codec) are Compatible.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public void CaseOnlyFieldDifferences_AreCompatible()
    {
        var reference = Clip(Video(codec: "H264", pix: "YUV420P"), Audio(codec: "AAC"));
        var other = Clip(Video(codec: "h264", pix: "yuv420p"), Audio(codec: "aac"));

        var report = CompatChecker.Compare(new[] { reference, other });

        report.Compatible.Should().BeTrue("codec/pix_fmt/audio-codec comparisons are OrdinalIgnoreCase");
        report.Mismatches.Should().BeEmpty();
    }
}
