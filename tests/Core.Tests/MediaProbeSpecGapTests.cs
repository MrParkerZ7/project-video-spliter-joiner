using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// todo-automate gap coverage for <see cref="MediaProbe"/> (SPEC-004): ProbeAsync input/parse/
/// cancellation guards, GetKeyframesAsync path guards + cache invalidation, and the
/// SnapToNearestKeyframe null guard. All binary-free — a canned/counting/throwing
/// <see cref="IFfprobeRunner"/> (reused from the sibling probe tests) stands in for ffprobe.
/// </summary>
public class MediaProbeSpecGapTests
{
    private static MediaProbe MakeProbe(IFfprobeRunner? runner = null) =>
        new(runner ?? new FakeFfprobeRunner("{}"));

    private static string NewTempFile(string tag, string content = "placeholder")
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"vsj-{tag}-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(tmp, content);
        return tmp;
    }

    // SPEC-004#I1 — an empty/whitespace path returns ProbeFailed (no throw).
    [Trait("serves-spec", "SPEC-004")]
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProbeAsync_EmptyPath_ReturnsFailure_NoThrow(string path)
    {
        var result = await MakeProbe().ProbeAsync(path);

        result.Should().BeOfType<ProbeResult.ProbeFailed>();
        ((ProbeResult.ProbeFailed)result).Reason.Should().NotBeNullOrEmpty();
    }

    // SPEC-004#I2 — a non-existent path returns ProbeFailed ("File does not exist").
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_NonExistentPath_ReturnsFailure_FileDoesNotExist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "vsj-missing-" + Guid.NewGuid().ToString("N") + ".mp4");

        var result = await MakeProbe().ProbeAsync(missing);

        result.Should().BeOfType<ProbeResult.ProbeFailed>();
        ((ProbeResult.ProbeFailed)result).Reason.Should().Contain("File does not exist");
    }

    // SPEC-004#I4 — unparseable ffprobe JSON returns ProbeFailed (invalid JSON).
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_InvalidJson_ReturnsFailure()
    {
        var file = NewTempFile("badjson");
        try
        {
            var probe = MakeProbe(new FakeFfprobeRunner("this is not json"));

            var result = await probe.ProbeAsync(file);

            result.Should().BeOfType<ProbeResult.ProbeFailed>();
            ((ProbeResult.ProbeFailed)result).Reason.Should().Contain("valid JSON");
        }
        finally { File.Delete(file); }
    }

    // SPEC-004#I5 — a payload with zero streams returns ProbeFailed ("No media streams").
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_NoStreams_ReturnsFailure()
    {
        var file = NewTempFile("nostreams");
        try
        {
            var probe = MakeProbe(new FakeFfprobeRunner("""{"streams":[]}"""));

            var result = await probe.ProbeAsync(file);

            result.Should().BeOfType<ProbeResult.ProbeFailed>();
            ((ProbeResult.ProbeFailed)result).Reason.Should().Contain("No media streams");
        }
        finally { File.Delete(file); }
    }

    // SPEC-004#I7 — Duration falls back to the LONGEST stream duration when format.duration is absent.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_NoFormatDuration_FallsBackToLongestStream()
    {
        // No "format" block; two streams of 5s and 8s → resolved duration = 8s (the longest).
        const string json = """
            {"streams":[
              {"index":0,"codec_type":"video","codec_name":"h264","duration":"5.000000"},
              {"index":1,"codec_type":"audio","codec_name":"aac","duration":"8.000000"}
            ]}
            """;
        var file = NewTempFile("fallbackdur");
        try
        {
            var probe = MakeProbe(new FakeFfprobeRunner(json));

            var result = await probe.ProbeAsync(file);

            result.Should().BeOfType<ProbeResult.ProbeSucceeded>();
            ((ProbeResult.ProbeSucceeded)result).Info.Duration.Should().Be(TimeSpan.FromSeconds(8));
        }
        finally { File.Delete(file); }
    }

    // SPEC-004#I8 — cancellation surfaces as OperationCanceledException (not swallowed into a failure).
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_Cancellation_Propagates_NotSwallowed()
    {
        var file = NewTempFile("cancel");
        try
        {
            var probe = MakeProbe(new OperationCancelledFfprobeRunner());

            Func<Task> act = () => probe.ProbeAsync(file);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally { File.Delete(file); }
    }

    // SPEC-004#I9 — GetKeyframesAsync on an empty path throws ArgumentException.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task GetKeyframesAsync_EmptyPath_ThrowsArgumentException()
    {
        Func<Task> act = () => MakeProbe().GetKeyframesAsync("");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // SPEC-004#I10 — GetKeyframesAsync on a non-existent path throws FileNotFoundException.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task GetKeyframesAsync_NonExistentPath_ThrowsFileNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), "vsj-kf-missing-" + Guid.NewGuid().ToString("N") + ".mp4");

        Func<Task> act = () => MakeProbe().GetKeyframesAsync(missing);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // SPEC-004#I17 — a file whose mtime/length changes yields a new cache key → a fresh re-scan
    // (not a stale cache hit). A query-aware fake counts the underlying packet queries.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task GetKeyframesAsync_FileChanged_InvalidatesCache_ReScans()
    {
        const string packetsJson = """
            {"packets":[
              {"pts_time":"0.000000","dts_time":"0.000000","flags":"K__"},
              {"pts_time":"1.000000","dts_time":"1.000000","flags":"K__"}
            ]}
            """;
        var fake = new QueryAwareFfprobeRunner(packetsPayload: packetsJson, framesPayload: "{}");
        var probe = MakeProbe(fake);
        var file = NewTempFile("kfcache", "short");
        try
        {
            await probe.GetKeyframesAsync(file);
            fake.PacketCallCount.Should().Be(1, "the first call scans once");

            // Change length + last-write-time → a different (path, mtime, length) cache key.
            await File.WriteAllTextAsync(file, "a-much-longer-content-body-that-changes-length");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));

            await probe.GetKeyframesAsync(file);
            fake.PacketCallCount.Should().Be(2, "a changed file forces a fresh scan rather than a stale cache hit");
        }
        finally { File.Delete(file); }
    }

    // SPEC-004#I23 — SnapToNearestKeyframe(null, …) throws ArgumentNullException.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public void SnapToNearestKeyframe_NullKeyframes_ThrowsArgumentNullException()
    {
        var act = () => MakeProbe().SnapToNearestKeyframe(null!, TimeSpan.FromSeconds(1));

        act.Should().Throw<ArgumentNullException>();
    }

    // SPEC-004#I6 — on success every StreamInfo field is mapped from the ffprobe stream, streams
    // are partitioned by codec_type into VideoStreams/AudioStreams IN CONTAINER ORDER (anything
    // that is neither video nor audio lands in neither list), and a payload with no format block
    // resolves Container to the literal "unknown".
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_MapsEveryStreamField_AndPartitionsByCodecType()
    {
        // Deliberately NO "format" block → Container must fall back to "unknown".
        // Stream order is video(0) · audio(1) · video(2) · subtitle(3) so container order is visible.
        const string json = """
            {"streams":[
              {"index":0,"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"pix_fmt":"yuv420p","time_base":"1/30"},
              {"index":1,"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"time_base":"1/48000"},
              {"index":2,"codec_type":"video","codec_name":"hevc","width":640,"height":360,"pix_fmt":"yuv420p10le","time_base":"1/25"},
              {"index":3,"codec_type":"subtitle","codec_name":"subrip"}
            ]}
            """;
        var file = NewTempFile("streammap");
        try
        {
            var probe = MakeProbe(new FakeFfprobeRunner(json));

            var result = await probe.ProbeAsync(file);

            result.Should().BeOfType<ProbeResult.ProbeSucceeded>();
            var info = ((ProbeResult.ProbeSucceeded)result).Info;

            info.Container.Should().Be("unknown", "an absent format block falls back to the literal unknown container");
            info.HasVideo.Should().BeTrue("HasVideo reflects the video stream count");
            info.HasAudio.Should().BeTrue("HasAudio reflects the audio stream count");

            // Partitioned by codec_type, in container order; the subtitle stream lands in neither list.
            info.VideoStreams.Select(s => s.Index).Should().Equal(new[] { 0, 2 }, "video streams keep container order");
            info.AudioStreams.Select(s => s.Index).Should().Equal(new[] { 1 }, "the single audio stream is partitioned out on its own");
            (info.VideoStreams.Count + info.AudioStreams.Count).Should().Be(3, "a subtitle stream is neither video nor audio");

            var video = info.VideoStreams[0];
            video.CodecName.Should().Be("h264");
            video.Type.Should().Be("video");
            video.IsVideo.Should().BeTrue();
            video.Width.Should().Be(1920);
            video.Height.Should().Be(1080);
            video.PixFmt.Should().Be("yuv420p");
            video.TimeBase.Should().Be("1/30");
            video.SampleRate.Should().BeNull("a video stream reports no sample rate");
            video.Channels.Should().BeNull("a video stream reports no channel count");

            var audio = info.AudioStreams[0];
            audio.CodecName.Should().Be("aac");
            audio.Type.Should().Be("audio");
            audio.IsAudio.Should().BeTrue();
            audio.SampleRate.Should().Be(48000, "ffprobe emits sample_rate as a string and it is parsed to an int");
            audio.Channels.Should().Be(2);
            audio.TimeBase.Should().Be("1/48000");
            audio.Width.Should().BeNull("an audio stream reports no dimensions");
            audio.Height.Should().BeNull("an audio stream reports no dimensions");
            audio.PixFmt.Should().BeNull("an audio stream reports no pixel format");
        }
        finally { File.Delete(file); }
    }
}

/// <summary>Ffprobe runner that throws <see cref="OperationCanceledException"/> — models a cancelled probe.</summary>
internal sealed class OperationCancelledFfprobeRunner : IFfprobeRunner
{
    public Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default) =>
        throw new OperationCanceledException();
}
