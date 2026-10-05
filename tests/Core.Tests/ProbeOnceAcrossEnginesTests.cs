using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-193 / G-059 criterion 4 — a file that was already probed and has not changed is not probed again: a Join run, a
/// Split run and a Bulk row spawn no metadata ffprobe for it. The engines are unchanged; their repeat
/// <see cref="IMediaProbe.ProbeAsync"/> calls become <see cref="MediaProbe"/> cache hits. Each test drives the real
/// engines over ONE shared real <see cref="MediaProbe"/> (as the app's composition root shares one) and counts the
/// metadata probes with <see cref="ProbeCountingFfprobeRunner"/>; keyframe runs are not counted.
/// Join is binary-free (a fake ffmpeg that writes the output); Split, Smart Cut and Bulk Cut use the real bundled
/// ffmpeg over the synthetic <see cref="MediaFixtures"/> clip.
/// </summary>
[Collection(MediaFixturesCollection.Name)]
public sealed class ProbeOnceAcrossEnginesTests : IDisposable
{
    private readonly MediaFixtures _fixtures;
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public ProbeOnceAcrossEnginesTests(MediaFixtures fixtures, ITestOutputHelper output)
    {
        _fixtures = fixtures;
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-probe-once-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private ErrorLogWriter Logs() => new(Path.Combine(_dir, "logs")); // never %LOCALAPPDATA%

    private string[] ThreeClips()
    {
        var clips = new string[3];
        for (var i = 0; i < clips.Length; i++)
        {
            clips[i] = Path.Combine(_dir, $"clip{i + 1}.mp4");
            File.WriteAllText(clips[i], $"clip {i + 1}");
        }

        return clips;
    }

    // ---- Join (binary-free) -----------------------------------------------------------------

    // SPEC-004#I33 · SPEC-003 — the add-time compatibility check probes each clip once; the run's pre-flight and its
    // SumDurationsAsync are cache hits. 3 probes in total (9 before T-193).
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task JoinEngine_CheckThenJoin_ThreeClips_ProbesEachClipOnce()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var probe = new MediaProbe(runner);
        var ffmpeg = new OutputWritingFfmpegRunner();
        var engine = new JoinEngine(ffmpeg, probe, Logs());
        var clips = ThreeClips();

        var report = await engine.CheckCompatibilityAsync(clips);
        report.Compatible.Should().BeTrue();

        var result = await engine.JoinAsync(new JoinRequest(clips, Path.Combine(_dir, "joined.mp4")));

        result.Refusal.Should().BeNull();
        result.Success.Should().BeTrue();
        ffmpeg.Runs.Should().Be(1, "one concat");
        runner.MetadataCallCount.Should().Be(3, "each clip is probed once; the run's pre-flight and duration sum are cache hits");
        runner.MetadataInputs.Select(Path.GetFileName).Should().Equal("clip1.mp4", "clip2.mp4", "clip3.mp4");
    }

    // SPEC-004#I33 · SPEC-003 — a run with no earlier check still probes each clip once: SumDurationsAsync re-reads
    // the pre-flight's results from the cache (6 probes before T-193).
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task JoinEngine_RunAlone_ThreeClips_SumDurationsIsACacheHit()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var engine = new JoinEngine(new OutputWritingFfmpegRunner(), new MediaProbe(runner), Logs());

        var result = await engine.JoinAsync(new JoinRequest(ThreeClips(), Path.Combine(_dir, "joined.mp4")));

        result.Success.Should().BeTrue();
        runner.MetadataCallCount.Should().Be(3);
    }

    // SPEC-004#I34 · SPEC-003 — the honest re-check stays honest: a clip edited between the add-time check and Run has
    // a new key, is probed again at Run, and an incompatible change is refused with a named mismatch; nothing is written.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task JoinEngine_ClipEditedBetweenCheckAndRun_IsProbedAgain_AndTheMismatchIsRefused()
    {
        var runner = new ProbeCountingFfprobeRunner();
        runner.ThenMetadata(ProbeCountingFfprobeRunner.MetadataJson(10));
        runner.ThenMetadata(ProbeCountingFfprobeRunner.MetadataJson(10));
        runner.ThenMetadata(ProbeCountingFfprobeRunner.MetadataJson(10));
        runner.ThenMetadata(ProbeCountingFfprobeRunner.MetadataJson(10, videoCodec: "hevc")); // clip 2 after its edit
        var engine = new JoinEngine(new ThrowingFfmpegRunner(), new MediaProbe(runner), Logs());
        var clips = ThreeClips();
        var output = Path.Combine(_dir, "joined.mp4");

        (await engine.CheckCompatibilityAsync(clips)).Compatible.Should().BeTrue();

        File.WriteAllText(clips[1], "clip 2, re-encoded by another tool");
        File.SetLastWriteTimeUtc(clips[1], DateTime.UtcNow.AddMinutes(5));

        var result = await engine.JoinAsync(new JoinRequest(clips, output));

        result.Success.Should().BeFalse("compatibility is refused, not fixed");
        result.Refusal!.Mismatches.Should().Contain(m => m.Field == "codec" && m.Detail.Contains("clip 2"));
        File.Exists(output).Should().BeFalse();
        runner.MetadataCallCount.Should().Be(4, "only the edited clip is probed again");
        runner.MetadataInputs[^1].Should().Be(clips[1]);
    }

    // ---- Split / Smart Cut / Bulk Cut (real ffmpeg, synthetic fixture) ------------------------

    private (MediaProbe Probe, ProbeCountingFfprobeRunner Counter, FfmpegRunner Ffmpeg) Real()
    {
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg");
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
        var locator = new FfmpegBinaryLocator(
            ffmpegOverride: FfmpegTestBinaries.Ffmpeg, ffprobeOverride: FfmpegTestBinaries.Ffprobe);
        var counter = ProbeCountingFfprobeRunner.Over(new FfprobeRunner(locator));
        return (new MediaProbe(counter), counter, new FfmpegRunner(locator));
    }

    private string OutDir(string name)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // SPEC-004#I33 · SPEC-001 — Split's Preparing probe after the load's probe is a cache hit, on both extraction
    // paths (all parts → segment muxer; a subset → per-segment). Zero extra metadata probes (one before T-193).
    [Trait("serves-spec", "SPEC-004")]
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitEngine_AfterTheLoadsProbe_SplitsWithNoFurtherMetadataProbe(bool subset)
    {
        var (probe, counter, ffmpeg) = Real();
        var source = _fixtures.VideoWithAudioPath;

        // SplitViewModel.LoadAsync: the probe gate, then the background keyframe index — on the shared probe.
        (await probe.ProbeAsync(source)).IsSuccess.Should().BeTrue();
        await probe.GetKeyframesAsync(source);
        counter.MetadataCallCount.Should().Be(1);

        var engine = new SplitEngine(ffmpeg, probe, Logs(), new FakeDiskSpaceProbe(long.MaxValue));
        var result = await engine.SplitAsync(new SplitRequest(
            source,
            new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) },
            OutDir("split"),
            SelectedSegmentIndices: subset ? new[] { 2 } : null));

        result.Segments.Should().HaveCount(subset ? 1 : 3);
        result.Segments.Should().OnlyContain(s => File.Exists(s.Path));
        counter.MetadataCallCount.Should().Be(1, "the engine's Preparing probe of the loaded file is a cache hit");
    }

    // SPEC-004#I33 · SPEC-002 — the Bulk request builder's probe and SmartCutEngine's probe of the same row are one
    // ffprobe in total (two before T-193); KeptMiddleRequestBuilder's "cached" comment is now true for the probe too.
    [Trait("serves-spec", "SPEC-004")]
    [SkippableFact]
    public async Task KeptMiddleRequestBuilder_ThenSmartCut_OnOneRow_ProbeOnceInTotal()
    {
        var (probe, counter, ffmpeg) = Real();
        var source = _fixtures.VideoWithAudioPath;
        var outDir = OutDir("smart");
        var item = new BulkTrimItem(source, TimeSpan.FromSeconds(3.4), null, Path.Combine(outDir, "row_trimmed.mp4"));

        var request = await new KeptMiddleRequestBuilder(probe)
            .BuildAsync(item, item.DesiredOutputPath, overwrite: true, CancellationToken.None);
        request.SelectedSegmentIndices.Should().ContainSingle();

        var cut = await new SmartCutEngine(ffmpeg, probe)
            .CutAsync(source, item.IntroEnd, null, Path.Combine(outDir, "row_exact.mp4"));
        _output.WriteLine($"smart cut: strategy {cut.Strategy}, fell back {cut.FellBack} ({cut.FallbackReason})");

        counter.MetadataCallCount.Should().Be(1, "the second probe of the unchanged row is a cache hit");
    }

    // SPEC-004#I33 · SPEC-002 — G-059 criterion 4: a Bulk row probed on add makes NO further metadata probe when it
    // runs — Lossless (builder + split engine: 2 before T-193), Exact (smart cut: 1 before), and an Exact row that
    // falls back to the lossless cut because its cut is already on a keyframe (smart cut + builder + split: 3 before).
    [Trait("serves-spec", "SPEC-004")]
    [SkippableTheory]
    [InlineData("lossless")]
    [InlineData("exact")]
    [InlineData("exact-on-keyframe")]
    public async Task BulkRow_ProbedOnAdd_RunsWithNoFurtherMetadataProbe(string row)
    {
        var (probe, counter, ffmpeg) = Real();
        var source = _fixtures.VideoWithAudioPath; // 10 s, keyframes every 1 s
        var precision = row == "lossless" ? CutPrecision.Lossless : CutPrecision.Exact;
        var intro = row == "exact-on-keyframe" ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(3.4);

        // BulkCutViewModel.AddFilesAsync: the row's probe, then its keyframe scan — on the shared probe.
        (await probe.ProbeAsync(source)).IsSuccess.Should().BeTrue();
        await probe.GetKeyframesAsync(source);
        counter.MetadataCallCount.Should().Be(1);

        var split = new SplitEngine(ffmpeg, probe, Logs(), new FakeDiskSpaceProbe(long.MaxValue));
        var engine = new BulkTrimEngine(
            split, new KeptMiddleRequestBuilder(probe), new FakeDiskSpaceProbe(long.MaxValue), new SmartCutEngine(ffmpeg, probe));
        var item = new BulkTrimItem(source, intro, TimeSpan.FromSeconds(7), Path.Combine(OutDir("bulk"), "row_trimmed.mp4"));

        var batch = await engine.RunAsync(new[] { item }, new BulkTrimOptions(CollisionPolicy.Overwrite, OutputMode.NewFile, precision));

        batch.Outcome.Should().Be(BatchOutcome.Completed);
        batch.Items.Should().ContainSingle().Which.Outcome.Should().Be(ItemOutcome.Done);
        File.Exists(item.DesiredOutputPath).Should().BeTrue();
        counter.MetadataCallCount.Should().Be(1, "every probe the run makes of the row it already probed is a cache hit");
    }

    /// <summary>An ffmpeg runner that "succeeds" by writing the output path (the last argument) and counts runs.</summary>
    private sealed class OutputWritingFfmpegRunner : IFfmpegRunner
    {
        public int Runs { get; private set; }

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            Runs++;
            File.WriteAllText(args.ToList()[^1], "joined");
            return Task.FromResult(new FfmpegResult(0, Array.Empty<string>()));
        }
    }

    /// <summary>An ffmpeg runner that must never run (a refused join writes nothing).</summary>
    private sealed class ThrowingFfmpegRunner : IFfmpegRunner
    {
        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("ffmpeg must not run for a refused join.");
    }
}
