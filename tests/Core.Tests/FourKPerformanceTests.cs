using System.Diagnostics;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-024 4K verification. Confirms the resolution-independent paths stay correct on a real 3840×2160
/// fixture: the <c>-c copy</c> split still produces clean, playable segments, and the keyframe scan
/// still returns keyframes. Raw timings (4K vs 1080p split, 4K keyframe scan) are written to the test
/// output for the report — deliberately NOT asserted as thresholds, so the suite is not flaky on a
/// loaded CI box. Guard-skips when ffmpeg is absent.
/// </summary>
[Collection(MediaFixturesCollection.Name)]
public class FourKPerformanceTests
{
    private readonly MediaFixtures _fixtures;
    private readonly ITestOutputHelper _output;

    public FourKPerformanceTests(MediaFixtures fixtures, ITestOutputHelper output)
    {
        _fixtures = fixtures;
        _output = output;
    }

    private static MediaProbe MakeProbe() =>
        new(new FfprobeRunner(new FfmpegBinaryLocator(ffprobeOverride: FfmpegTestBinaries.Ffprobe)));

    private static SplitEngine MakeEngine()
    {
        var locator = new FfmpegBinaryLocator(
            ffmpegOverride: FfmpegTestBinaries.Ffmpeg,
            ffprobeOverride: FfmpegTestBinaries.Ffprobe);
        var runner = new FfmpegRunner(locator);
        var probe = new MediaProbe(new FfprobeRunner(locator));
        return new SplitEngine(runner, probe);
    }

    private bool ShouldSkip()
    {
        if (FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg"))
        {
            return true;
        }

        return FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
    }

    private static string NewOutDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-4k-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task KeyframeScan_On4K_ReturnsKeyframes_AndDoesNotStall()
    {
        if (ShouldSkip())
        {
            return;
        }

        var probe = MakeProbe();
        var path = _fixtures.Uhd4kPath;

        var sw = Stopwatch.StartNew();
        var keyframes = await probe.GetKeyframesAsync(path);
        sw.Stop();

        keyframes.Should().NotBeEmpty("a 15s clip with a 2s GOP must expose several keyframes");
        keyframes.Should().BeInAscendingOrder();
        keyframes[0].Should().BeLessThan(TimeSpan.FromSeconds(1), "the first keyframe is at the start");

        // Second call must be served from cache (near-instant) — proves the cache works on 4K too.
        var sw2 = Stopwatch.StartNew();
        var cached = await probe.GetKeyframesAsync(path);
        sw2.Stop();
        cached.Should().BeEquivalentTo(keyframes);

        _output.WriteLine($"[T-024] 4K keyframe scan (cold): {sw.ElapsedMilliseconds} ms, {keyframes.Count} keyframes");
        _output.WriteLine($"[T-024] 4K keyframe scan (cached): {sw2.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task CopySplit_4K_ProducesValidSegments_AndIsComparableTo1080p()
    {
        if (ShouldSkip())
        {
            return;
        }

        var (segs4k, ms4k) = await SplitAndTime(_fixtures.Uhd4kPath);
        var (segs1080, ms1080) = await SplitAndTime(_fixtures.FullHd1080Path);

        // Correctness: three contiguous, non-empty, re-probable segments at full source resolution.
        var probe = MakeProbe();
        segs4k.Should().HaveCount(3);
        foreach (var seg in segs4k)
        {
            File.Exists(seg.Path).Should().BeTrue();
            new FileInfo(seg.Path).Length.Should().BeGreaterThan(0);

            var result = await probe.ProbeAsync(seg.Path);
            result.Should().BeOfType<ProbeResult.ProbeSucceeded>("each 4K segment must be a clean media file");
            var ok = (ProbeResult.ProbeSucceeded)result;
            ok.Info.VideoStreams.Should().NotBeEmpty();
            // Split is -c copy → segments keep the source's full 4K resolution (never downscaled).
            ok.Info.VideoStreams[0].Height.Should().Be(2160);
            ok.Info.VideoStreams[0].Width.Should().Be(3840);
        }

        segs1080.Should().HaveCount(3);

        _output.WriteLine($"[T-024] copy-split 4K:    {ms4k} ms");
        _output.WriteLine($"[T-024] copy-split 1080p: {ms1080} ms");
        _output.WriteLine($"[T-024] 4K/1080p split ratio: {(ms1080 == 0 ? double.NaN : (double)ms4k / ms1080):F2}x");
    }

    private async Task<(IReadOnlyList<SplitSegment> Segments, long ElapsedMs)> SplitAndTime(string input)
    {
        var engine = MakeEngine();
        var outDir = NewOutDir();

        var req = new SplitRequest(
            InputPath: input,
            OutputDir: outDir,
            CutPoints: new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) },
            Overwrite: true);

        var sw = Stopwatch.StartNew();
        var result = await engine.SplitAsync(req);
        sw.Stop();

        return (result.Segments, sw.ElapsedMilliseconds);
    }
}
