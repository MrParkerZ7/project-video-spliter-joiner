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
}

/// <summary>Ffprobe runner that throws <see cref="OperationCanceledException"/> — models a cancelled probe.</summary>
internal sealed class OperationCancelledFfprobeRunner : IFfprobeRunner
{
    public Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default) =>
        throw new OperationCanceledException();
}
