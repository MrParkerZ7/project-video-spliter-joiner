using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// End-to-end split tests against the real ffmpeg over the synthetic 10s / 1s-GOP fixtures.
/// Each output is re-probed with the T-003 <see cref="MediaProbe"/> to confirm it is clean,
/// keyframe-aligned, and preserves its streams. Guarded to skip when the binaries are absent.
/// </summary>
[Collection(MediaFixturesCollection.Name)]
public class SplitEngineIntegrationTests
{
    private readonly MediaFixtures _fixtures;
    private readonly ITestOutputHelper _output;

    public SplitEngineIntegrationTests(MediaFixtures fixtures, ITestOutputHelper output)
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
        var d = Path.Combine(Path.GetTempPath(), "vsj-split-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task Split_At3And6_ProducesThreeCleanSegments_DurationsSumToWhole()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outDir = NewOutDir();
        try
        {
            var engine = MakeEngine();
            var probe = MakeProbe();

            var req = new SplitRequest(
                _fixtures.VideoOnlyPath,
                new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) },
                outDir);

            var result = await engine.SplitAsync(req);

            result.Segments.Should().HaveCount(3);

            TimeSpan total = TimeSpan.Zero;
            foreach (var seg in result.Segments)
            {
                File.Exists(seg.Path).Should().BeTrue();

                var probed = await probe.ProbeAsync(seg.Path);
                probed.IsSuccess.Should().BeTrue($"segment '{seg.Path}' must probe cleanly");
                var info = ((ProbeResult.ProbeSucceeded)probed).Info;
                info.HasVideo.Should().BeTrue();
                total += info.Duration;

                _output.WriteLine(
                    $"{Path.GetFileName(seg.Path)}: dur={info.Duration.TotalSeconds:F3}s " +
                    $"actualStart={seg.ActualStart.TotalSeconds:F3}s delta={seg.Delta.TotalSeconds:F3}s");
            }

            total.Should().BeCloseTo(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300));

            // Each interior boundary (ActualStart of segments 2 and 3) must be a keyframe of the source.
            var kf = await probe.GetKeyframesAsync(_fixtures.VideoOnlyPath);
            foreach (var seg in result.Segments.Skip(1))
            {
                kf.Should().Contain(
                    k => (k - seg.ActualStart).Duration() < TimeSpan.FromMilliseconds(60),
                    $"segment boundary {seg.ActualStart.TotalSeconds:F3}s must land on a source keyframe");
            }

            // Cuts on integer seconds with a 1s GOP → snap deltas ≈ 0.
            foreach (var seg in result.Segments.Skip(1))
            {
                seg.Delta.Duration().Should().BeLessThan(TimeSpan.FromMilliseconds(120));
            }
        }
        finally
        {
            TryDelete(outDir);
        }
    }

    [Fact]
    public async Task Split_SingleCutAt5_ProducesTwoParts()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outDir = NewOutDir();
        try
        {
            var engine = MakeEngine();
            var result = await engine.SplitAsync(new SplitRequest(
                _fixtures.VideoOnlyPath, new[] { TimeSpan.FromSeconds(5) }, outDir));

            result.Segments.Should().HaveCount(2);
            result.Segments.Should().OnlyContain(s => File.Exists(s.Path));
        }
        finally
        {
            TryDelete(outDir);
        }
    }

    [Fact]
    public async Task Split_NonKeyframeAlignedCut_SnapsToThree_ReportsNegativeDelta()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outDir = NewOutDir();
        try
        {
            var engine = MakeEngine();

            // 3.4s on a 1s-GOP file snaps to 3.0s → the second segment starts at 3.0 with delta ≈ -0.4s.
            var result = await engine.SplitAsync(new SplitRequest(
                _fixtures.VideoOnlyPath, new[] { TimeSpan.FromSeconds(3.4) }, outDir));

            result.Segments.Should().HaveCount(2);
            var second = result.Segments[1];
            second.ActualStart.Should().BeCloseTo(TimeSpan.FromSeconds(3.0), TimeSpan.FromMilliseconds(80));
            second.Delta.Should().BeCloseTo(TimeSpan.FromSeconds(-0.4), TimeSpan.FromMilliseconds(120));

            _output.WriteLine($"snap delta observed = {second.Delta.TotalSeconds:F3}s");
        }
        finally
        {
            TryDelete(outDir);
        }
    }

    [Fact]
    public async Task Split_FixtureWithAudio_OutputsPreserveAudioStream()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outDir = NewOutDir();
        try
        {
            var engine = MakeEngine();
            var probe = MakeProbe();

            var result = await engine.SplitAsync(new SplitRequest(
                _fixtures.VideoWithAudioPath, new[] { TimeSpan.FromSeconds(4) }, outDir));

            result.Segments.Should().HaveCount(2);

            foreach (var seg in result.Segments)
            {
                var probed = await probe.ProbeAsync(seg.Path);
                var info = ((ProbeResult.ProbeSucceeded)probed).Info;
                info.HasVideo.Should().BeTrue();
                info.HasAudio.Should().BeTrue($"segment '{seg.Path}' must keep the audio stream (-map 0)");
            }
        }
        finally
        {
            TryDelete(outDir);
        }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
