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

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
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

    // ---- Selectable segments (T-049) --------------------------------------------------------

    [SkippableFact]
    public async Task Split_SelectOnlyMiddlePart_WritesOnlyThatFile_OthersNotCreated_ReprobesPlayable()
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

            // Cuts at 3s & 6s on the 10s / 1s-GOP fixture → 3 parts: [0..3],[3..6],[6..10].
            // Select ONLY the middle part (index 2). The default pattern is {name}_part{index:00}{ext},
            // so the expected file keeps its ORIGINAL index: video_only_part02.mp4.
            var req = new SplitRequest(
                _fixtures.VideoOnlyPath,
                new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) },
                outDir,
                SelectedSegmentIndices: new[] { 2 });

            var result = await engine.SplitAsync(req);

            // Exactly one produced segment — the middle part — and it is _part02 (original index kept).
            result.Segments.Should().ContainSingle();
            var only = result.Segments[0];
            Path.GetFileName(only.Path).Should().Be("video_only_part02.mp4");
            File.Exists(only.Path).Should().BeTrue();

            // The OTHER parts must NOT have been written.
            File.Exists(Path.Combine(outDir, "video_only_part01.mp4")).Should().BeFalse(
                "part 1 was not selected and must never be written");
            File.Exists(Path.Combine(outDir, "video_only_part03.mp4")).Should().BeFalse(
                "part 3 was not selected and must never be written");

            // Only ONE mp4 exists on disk in the output dir.
            Directory.GetFiles(outDir, "*.mp4").Should().ContainSingle();

            // The written middle part re-probes as a clean, playable video ~3s long ([3..6]).
            var probed = await probe.ProbeAsync(only.Path);
            probed.IsSuccess.Should().BeTrue("the selected middle part must probe cleanly");
            var info = ((ProbeResult.ProbeSucceeded)probed).Info;
            info.HasVideo.Should().BeTrue();
            info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300));

            only.ActualStart.Should().BeCloseTo(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(80));
        }
        finally
        {
            TryDelete(outDir);
        }
    }

    [SkippableFact]
    public async Task Split_SelectAll_SameAsMuxerPath_AllPartsProduced()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outDir = NewOutDir();
        try
        {
            var engine = MakeEngine();

            // Explicitly selecting ALL three parts is the full contiguous set → the muxer fast path,
            // identical to a null selection: all parts written.
            var req = new SplitRequest(
                _fixtures.VideoOnlyPath,
                new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) },
                outDir,
                SelectedSegmentIndices: new[] { 1, 2, 3 });

            var result = await engine.SplitAsync(req);

            result.Segments.Should().HaveCount(3);
            result.Segments.Should().OnlyContain(s => File.Exists(s.Path));
            Directory.GetFiles(outDir, "*.mp4").Should().HaveCount(3);
        }
        finally
        {
            TryDelete(outDir);
        }
    }

    [SkippableFact]
    public async Task Split_SelectFirstAndLast_WritesOnlyThoseTwo_MiddleSkipped()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outDir = NewOutDir();
        try
        {
            var engine = MakeEngine();

            // Select parts 1 and 3 (skip the middle) — the non-contiguous subset exercises the
            // per-segment path for both the first part ([0..3]) and the FINAL part ([6..end], to EOF).
            var req = new SplitRequest(
                _fixtures.VideoOnlyPath,
                new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) },
                outDir,
                SelectedSegmentIndices: new[] { 1, 3 });

            var result = await engine.SplitAsync(req);

            result.Segments.Should().HaveCount(2);
            Path.GetFileName(result.Segments[0].Path).Should().Be("video_only_part01.mp4");
            Path.GetFileName(result.Segments[1].Path).Should().Be("video_only_part03.mp4");

            File.Exists(Path.Combine(outDir, "video_only_part02.mp4")).Should().BeFalse(
                "the middle part was not selected");
            Directory.GetFiles(outDir, "*.mp4").Should().HaveCount(2);
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
