using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// End-to-end join tests against the real ffmpeg over synthetic clips. Compatible sets join to
/// one playable mp4 (re-probed via T-003, duration ≈ sum, order preserved); an incompatible set
/// is refused with a named reason and NO output file on disk. Guarded to skip when the binaries
/// are absent.
/// </summary>
[Collection(JoinFixturesCollection.Name)]
public class JoinEngineIntegrationTests
{
    private readonly JoinFixtures _fixtures;
    private readonly ITestOutputHelper _output;

    public JoinEngineIntegrationTests(JoinFixtures fixtures, ITestOutputHelper output)
    {
        _fixtures = fixtures;
        _output = output;
    }

    private static MediaProbe MakeProbe() =>
        new(new FfprobeRunner(new FfmpegBinaryLocator(ffprobeOverride: FfmpegTestBinaries.Ffprobe)));

    private static JoinEngine MakeEngine()
    {
        var locator = new FfmpegBinaryLocator(
            ffmpegOverride: FfmpegTestBinaries.Ffmpeg,
            ffprobeOverride: FfmpegTestBinaries.Ffprobe);
        var runner = new FfmpegRunner(locator);
        var probe = new MediaProbe(new FfprobeRunner(locator));
        return new JoinEngine(runner, probe);
    }

    private bool ShouldSkip()
    {
        if (FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg"))
        {
            return true;
        }

        return FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
    }

    private static string NewOutPath(string ext = ".mp4")
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-join-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return Path.Combine(d, "joined" + ext);
    }

    [SkippableFact]
    public async Task Join_ThreeCompatibleClips_ProducesOnePlayableFile_DurationSumsPreserved()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outPath = NewOutPath();
        try
        {
            var engine = MakeEngine();
            var probe = MakeProbe();

            var req = new JoinRequest(
                new[] { _fixtures.ClipAPath, _fixtures.ClipBPath, _fixtures.ClipCPath }, // 3s + 2s + 2s = 7s
                outPath);

            var result = await engine.JoinAsync(req);

            result.Success.Should().BeTrue($"join should succeed for compatible clips; refusal={Describe(result.Refusal)}");
            result.OutputPath.Should().NotBeNull();
            File.Exists(result.OutputPath!).Should().BeTrue();

            var probed = await probe.ProbeAsync(result.OutputPath!);
            probed.IsSuccess.Should().BeTrue("joined output must probe cleanly");
            var info = ((ProbeResult.ProbeSucceeded)probed).Info;
            info.HasVideo.Should().BeTrue();
            info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(7), TimeSpan.FromMilliseconds(400));

            _output.WriteLine($"joined duration = {info.Duration.TotalSeconds:F3}s (expected ≈ 7s)");
        }
        finally
        {
            TryDeleteParent(outPath);
        }
    }

    [SkippableFact]
    public async Task CheckCompatibility_OnCompatibleSet_ReturnsCompatibleNoMismatches()
    {
        if (ShouldSkip())
        {
            return;
        }

        var engine = MakeEngine();
        var report = await engine.CheckCompatibilityAsync(
            new[] { _fixtures.ClipAPath, _fixtures.ClipBPath, _fixtures.ClipCPath });

        report.Compatible.Should().BeTrue($"mismatches: {Describe(report)}");
        report.Mismatches.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Join_WithIncompatibleResolution_Refuses_NamesClip_NoFileWritten()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outPath = NewOutPath();
        try
        {
            var engine = MakeEngine();

            // clipDifferentRes (640x480) is clip 2 in the set; A is the 320x240 reference.
            var req = new JoinRequest(
                new[] { _fixtures.ClipAPath, _fixtures.ClipDifferentResPath, _fixtures.ClipCPath },
                outPath);

            var result = await engine.JoinAsync(req);

            result.Success.Should().BeFalse();
            result.OutputPath.Should().BeNull();
            result.Refusal.Should().NotBeNull();
            result.Refusal!.Mismatches.Should().Contain(m => m.Field == "resolution");
            result.Refusal!.Mismatches.Single(m => m.Field == "resolution").Detail
                .Should().Contain("clip 2").And.Contain("640x480");

            // CRITICAL: nothing may be written on an incompatible join.
            File.Exists(outPath).Should().BeFalse("a refused (incompatible) join must NOT write an output file");

            _output.WriteLine($"refusal: {Describe(result.Refusal)}");
        }
        finally
        {
            TryDeleteParent(outPath);
        }
    }

    [SkippableFact]
    public async Task Join_TwoCompatibleClipsWithAudio_OutputKeepsAudio()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outPath = NewOutPath();
        try
        {
            var engine = MakeEngine();
            var probe = MakeProbe();

            var result = await engine.JoinAsync(new JoinRequest(
                new[] { _fixtures.ClipAWithAudioPath, _fixtures.ClipBWithAudioPath }, outPath));

            result.Success.Should().BeTrue($"refusal={Describe(result.Refusal)}");

            var probed = await probe.ProbeAsync(result.OutputPath!);
            var info = ((ProbeResult.ProbeSucceeded)probed).Info;
            info.HasVideo.Should().BeTrue();
            info.HasAudio.Should().BeTrue("joined output must keep the audio stream (-map 0)");
            info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(400));
        }
        finally
        {
            TryDeleteParent(outPath);
        }
    }

    [SkippableFact]
    public async Task Join_SingleInput_PassthroughCopy_ProducesPlayableFile()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outPath = NewOutPath();
        try
        {
            var engine = MakeEngine();
            var probe = MakeProbe();

            var result = await engine.JoinAsync(new JoinRequest(new[] { _fixtures.ClipAPath }, outPath));

            result.Success.Should().BeTrue($"single-input passthrough should succeed; refusal={Describe(result.Refusal)}");
            File.Exists(result.OutputPath!).Should().BeTrue();

            var probed = await probe.ProbeAsync(result.OutputPath!);
            probed.IsSuccess.Should().BeTrue();
            var info = ((ProbeResult.ProbeSucceeded)probed).Info;
            info.HasVideo.Should().BeTrue();
            info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(400));
        }
        finally
        {
            TryDeleteParent(outPath);
        }
    }

    private static string Describe(CompatReport? report) =>
        report is null ? "<none>" : string.Join("; ", report.Mismatches.Select(m => $"[{m.Field}] {m.Detail}"));

    private static void TryDeleteParent(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
