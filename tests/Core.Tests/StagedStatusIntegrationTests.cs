using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-044: the split / join engines must emit their stage transitions through the optional
/// <see cref="IProgress{OperationStatus}"/> channel, in order, synced to the REAL work — not a
/// timer. A recording reporter captures the sequence and we assert the ordered stage names. Runs
/// against the real ffmpeg fixtures (the split path needs actual segment files on disk to finalize);
/// guard-skips when the binaries are absent.
/// </summary>
[Collection(MediaFixturesCollection.Name)]
public class SplitStagedStatusIntegrationTests
{
    private readonly MediaFixtures _fixtures;
    private readonly ITestOutputHelper _output;

    public SplitStagedStatusIntegrationTests(MediaFixtures fixtures, ITestOutputHelper output)
    {
        _fixtures = fixtures;
        _output = output;
    }

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

    [SkippableFact]
    public async Task Split_ReportsOrderedStages_PreparingSplittingFinalizingDone()
    {
        if (ShouldSkip())
        {
            return;
        }

        var outDir = Path.Combine(Path.GetTempPath(), "vsj-split-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var engine = MakeEngine();
            var recorder = new RecordingStatus();

            var req = new SplitRequest(
                _fixtures.VideoOnlyPath,
                new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) },
                outDir);

            var result = await engine.SplitAsync(req, progress: null, ct: default, status: recorder);

            // 3 segments produced → the Splitting detail should read the part count ("3 parts").
            result.Segments.Should().HaveCount(3);

            var stages = recorder.Stages;
            stages.Should().ContainInOrder("Preparing", "Splitting", "Finalizing", "Done");
            stages.First().Should().Be("Preparing");
            stages.Last().Should().Be("Done");

            var splitting = recorder.Reports.Single(r => r.Stage == "Splitting");
            splitting.Detail.Should().Be("3 parts", "the segment count M is known from the cut plan");

            _output.WriteLine("stages: " + string.Join(" → ", stages));
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

/// <summary>T-044 join-stage sequence over the real ffmpeg join fixtures. Guard-skips without binaries.</summary>
[Collection(JoinFixturesCollection.Name)]
public class JoinStagedStatusIntegrationTests
{
    private readonly JoinFixtures _fixtures;
    private readonly ITestOutputHelper _output;

    public JoinStagedStatusIntegrationTests(JoinFixtures fixtures, ITestOutputHelper output)
    {
        _fixtures = fixtures;
        _output = output;
    }

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

    [SkippableFact]
    public async Task Join_ReportsOrderedStages_CheckingJoiningFinalizingDone()
    {
        if (ShouldSkip())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "vsj-join-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, "joined.mp4");
        try
        {
            var engine = MakeEngine();
            var recorder = new RecordingStatus();

            var req = new JoinRequest(
                new[] { _fixtures.ClipAPath, _fixtures.ClipBPath, _fixtures.ClipCPath },
                outPath);

            var result = await engine.JoinAsync(req, progress: null, ct: default, status: recorder);
            result.Success.Should().BeTrue();

            var stages = recorder.Stages;
            stages.Should().ContainInOrder("Checking compatibility", "Joining", "Finalizing", "Done");
            stages.First().Should().Be("Checking compatibility");
            stages.Last().Should().Be("Done");

            _output.WriteLine("stages: " + string.Join(" → ", stages));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

/// <summary>Records every <see cref="OperationStatus"/> reported by an engine, in order.</summary>
internal sealed class RecordingStatus : IProgress<OperationStatus>
{
    private readonly List<OperationStatus> _reports = new();

    public IReadOnlyList<OperationStatus> Reports => _reports;

    public IReadOnlyList<string> Stages => _reports.Select(r => r.Stage).ToList();

    public void Report(OperationStatus value) => _reports.Add(value);
}
