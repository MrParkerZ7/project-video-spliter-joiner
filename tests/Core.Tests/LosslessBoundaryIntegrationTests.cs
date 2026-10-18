using System.Collections.Concurrent;
using System.Globalization;
using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Io;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// The synthetic sources of <see cref="LosslessBoundaryIntegrationTests"/>, generated once per class run in a temp
/// folder of their own (never a user's video) and deleted afterwards.
/// </summary>
public sealed class LosslessBoundaryFixtures : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _made = new();

    public LosslessBoundaryFixtures()
    {
        Dir = Path.Combine(Path.GetTempPath(), "vsj-lossless-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
    }

    public string Dir { get; }

    /// <summary>The shape's source file, made on first use. Callers check the binaries first.</summary>
    public Task<string> GetAsync(string shape) =>
        _made.GetOrAdd(shape, key => new Lazy<Task<string>>(() => MakeAsync(key))).Value;

    private async Task<string> MakeAsync(string shape)
    {
        switch (shape)
        {
            // The MediaFixtures recipe (VideoWithAudioPath): libx264 with its default B-frames (has_b_frames 2), 1 s GOP.
            case "bframes":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "bframes.mp4", 10, 30, "-c:v", "libx264", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0");

            // The same recipe with no B-frames: the control the segment muxer always got right.
            case "bf0":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "bf0.mp4", 10, 30, "-c:v", "libx264", "-bf", "0", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0");

            // H.264 + AAC MPEG-TS, 2 s GOP with B-frames. The mpegts muxer starts it at about 1.4 s (format.start_time
            // 1.443444 with this 44.1 kHz AAC; the video stream starts at 1.466667), keyframes at 1.4667 + 2k s.
            case "ts":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "src.ts", 10, 30, "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");

            // That TS remuxed to MP4 with -c copy: format.start_time 0, but its video starts at 0.023 s (the AAC
            // priming carried over), keyframes at 0.023 + 2k s.
            case "tsremux":
            {
                var ts = await GetAsync("ts");
                var path = Path.Combine(Dir, "tsremux.mp4");
                var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg()
                    .Raw("-y", "-loglevel", "error").Input(ts).Raw("-map", "0", "-c", "copy").Output(path));
                run.ExitCode.Should().Be(0, run.StdErrText);
                return path;
            }

            // HEVC with B-frames (libx265 defaults), closed 2 s GOP.
            case "hevc":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "hevc.mp4", 12, 30, "-c:v", "libx265", "-tag:v", "hvc1",
                    "-x265-params", "keyint=60:min-keyint=60:scenecut=0:open-gop=0:log-level=error");

            // A long GOP: 10 s between keyframes.
            case "longgop":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "longgop.mp4", 30, 30, "-c:v", "libx264", "-g", "300", "-keyint_min", "300", "-sc_threshold", "0");

            // The tiny source from T-188's review: 3 s, 1 s GOP, B-frames. Its last cut snaps to the final keyframe,
            // which made the shipped Split fail outright (one segment short).
            case "tiny":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "tiny.mp4", 3, 30, "-c:v", "libx264", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0");

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown fixture shape");
        }
    }

    internal static FfmpegRunner Runner() => new(new FfmpegBinaryLocator(
        ffmpegOverride: FfmpegTestBinaries.FfmpegRequired, ffprobeOverride: FfmpegTestBinaries.FfprobeRequired));

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>
/// T-219 — a lossless (<c>-c copy</c>) part starts on its planned keyframe, on a B-frame source and on a source whose
/// timestamps do not start at zero (SPEC-001 I66). Each part's first decoded frame is compared by md5 with the source's
/// frame at the planned keyframe, and a segment-muxer part, or a part that runs to the end of the file, must hold
/// exactly the source's frames of its planned range. A per-segment part that has an end is checked for its start only:
/// stream copy applies <c>-to</c> to decoding timestamps, so on a B-frame source it keeps the next keyframe and a frame
/// coded after it (T-220); its count is written to the output, not asserted equal.
/// </summary>
public sealed class LosslessBoundaryIntegrationTests : IClassFixture<LosslessBoundaryFixtures>, IDisposable
{
    private readonly LosslessBoundaryFixtures _fx;
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public LosslessBoundaryIntegrationTests(LosslessBoundaryFixtures fx, ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
        _dir = Path.Combine(fx.Dir, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private void RequireBinaries()
    {
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg");
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
    }

    private static FfmpegBinaryLocator Locator() => new(
        ffmpegOverride: FfmpegTestBinaries.Ffmpeg, ffprobeOverride: FfmpegTestBinaries.Ffprobe);

    private SplitEngine Engine(MediaProbe probe) =>
        new(new FfmpegRunner(Locator()), probe, new ErrorLogWriter(Path.Combine(_dir, "logs"))); // never %LOCALAPPDATA%

    private static MediaProbe Probe() => new(new FfprobeRunner(Locator()));

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>One written part against its plan: frame count, first frame, and the planned values.</summary>
    private sealed record PartCheck(int Index, int Frames, int PlannedFrames, string FirstFrame, string PlannedFirstFrame, bool StartsRight)
    {
        public bool CountRight => Frames == PlannedFrames;

        public string Describe() => string.Create(
            CultureInfo.InvariantCulture,
            $"part {Index}: {Frames} frames (planned {PlannedFrames}), first frame = {FirstFrame} (planned {PlannedFirstFrame})");
    }

    /// <summary>
    /// Measure each written part of <paramref name="result"/> against <paramref name="plan"/>: the part whose planned
    /// range is <c>[SnappedStart, SnappedEnd)</c> (the first from the source's first frame, the last to its last).
    /// </summary>
    private async Task<IReadOnlyList<PartCheck>> CheckPartsAsync(
        string source, IReadOnlyList<LosslessBoundary.Frame> sourceFrames, IReadOnlyList<(int OneBased, TimeSpan Start, TimeSpan? End, string Path)> parts)
    {
        var checks = new List<PartCheck>();
        foreach (var (oneBased, start, end, path) in parts)
        {
            var frames = await LosslessBoundary.FramesAsync(path, _dir);
            frames.Should().NotBeEmpty($"part {oneBased} must decode");
            var counted = await LosslessBoundary.CountFramesAsync(path);
            var plannedFirst = start == TimeSpan.Zero ? 0 : LosslessBoundary.IndexAt(sourceFrames, start);
            var plannedCount = LosslessBoundary.CountIn(sourceFrames, start == TimeSpan.Zero ? null : start, end);
            checks.Add(new PartCheck(
                oneBased,
                counted,
                plannedCount,
                LosslessBoundary.Describe(sourceFrames, frames[0].Hash),
                LosslessBoundary.Describe(sourceFrames, sourceFrames[plannedFirst].Hash),
                frames[0].Hash == sourceFrames[plannedFirst].Hash));
        }

        foreach (var c in checks)
        {
            _output.WriteLine($"{Path.GetFileName(source)}: {c.Describe()}");
        }

        return checks;
    }

    /// <summary>The planned ranges of a full split, from the engine's own snap of the requested cuts.</summary>
    private static async Task<IReadOnlyList<(TimeSpan Start, TimeSpan? End)>> PlanAsync(MediaProbe probe, string source, IReadOnlyList<TimeSpan> cuts)
    {
        var keyframes = await probe.GetKeyframesAsync(source);
        var snapped = cuts.Select(c => probe.SnapToNearestKeyframe(keyframes, c).Snapped).ToList();
        var ranges = new List<(TimeSpan, TimeSpan?)>();
        var start = TimeSpan.Zero;
        foreach (var s in snapped)
        {
            ranges.Add((start, s));
            start = s;
        }

        ranges.Add((start, null));
        return ranges;
    }

    private async Task<(IReadOnlyList<PartCheck> Checks, IReadOnlyList<LosslessBoundary.Frame> Source)> FullSplitAsync(string source, params double[] cuts)
    {
        var probe = Probe();
        var cutTimes = cuts.Select(S).ToList();
        var plan = await PlanAsync(probe, source, cutTimes);
        var sourceFrames = await LosslessBoundary.FramesAsync(source, _dir);

        var outDir = Path.Combine(_dir, "full-" + Path.GetFileNameWithoutExtension(source));
        var result = await Engine(probe).SplitAsync(new SplitRequest(source, cutTimes, outDir));

        result.Segments.Should().HaveCount(plan.Count);
        var parts = result.Segments.Select((s, i) => (i + 1, plan[i].Start, plan[i].End, s.Path)).ToList();
        return (await CheckPartsAsync(source, sourceFrames, parts), sourceFrames);
    }

    private static void AssertEveryPartExact(IReadOnlyList<PartCheck> checks)
    {
        checks.Should().AllSatisfy(c =>
        {
            c.StartsRight.Should().BeTrue($"every lossless part starts on its planned keyframe (SPEC-001 I66): {c.Describe()}");
            c.CountRight.Should().BeTrue($"a segment-muxer part holds exactly its planned frames (SPEC-001 I66): {c.Describe()}");
        });
    }

    // ---- (a) the segment muxer on a B-frame source -------------------------------------------------------------

    // serves-spec: SPEC-001#I66 — repro (a): the MediaFixtures B-frame recipe split at 3 s and 6 s. Shipped code wrote
    // 120/90/90 frames, each boundary on the next keyframe.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task FullSplit_BFrameSource_EachPartStartsOnItsKeyframe_AndHoldsItsPlannedFrames()
    {
        RequireBinaries();
        var src = await _fx.GetAsync("bframes");

        var (checks, _) = await FullSplitAsync(src, 3, 6);

        checks.Select(c => c.PlannedFrames).Should().Equal(new[] { 90, 90, 120 }, "precondition: the plan is 3 s / 3 s / 4 s at 30 fps");
        AssertEveryPartExact(checks);
    }

    // serves-spec: SPEC-001#I66 — the same check on the other shapes the drain named (T-188's bench): HEVC, a 10 s GOP,
    // the tiny clip whose last cut snaps to its final keyframe (shipped Split threw "got fewer segments than planned"),
    // a TS (its video starts 1.47 s in), that TS remuxed to MP4 (its video starts 0.023 s in, format start 0), and the
    // -bf 0 control.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("bf0", 3.0, 6.0)]
    [InlineData("hevc", 4.0, 8.0)]
    [InlineData("longgop", 10.0, 20.0)]
    [InlineData("tiny", 1.0, 2.0)]
    [InlineData("ts", 3.5, 5.5)]
    [InlineData("tsremux", 2.0, 4.0)]
    public async Task FullSplit_EveryShape_EachPartStartsOnItsKeyframe_AndHoldsItsPlannedFrames(string shape, double cut1, double cut2)
    {
        RequireBinaries();
        if (shape == "hevc")
        {
            Skip.IfNot(await SmartCutJointIntegrityTests.HasEncoderAsync("libx265"), "the bundled ffmpeg has no libx265");
        }

        var src = await _fx.GetAsync(shape);
        if (shape == "ts")
        {
            (await LosslessBoundary.FormatStartTimeAsync(src)).Should().BeGreaterThan(0.001, "precondition: a TS starts after 0");
        }

        var (checks, _) = await FullSplitAsync(src, cut1, cut2);

        AssertEveryPartExact(checks);
    }

    // ---- (b) the per-segment path on a source whose start_time is not zero -------------------------------------

    private async Task<(PartCheck Check, string Path)> SubsetAsync(string shape, int part, params double[] cuts)
    {
        var src = await _fx.GetAsync(shape);
        var probe = Probe();
        var cutTimes = cuts.Select(S).ToList();
        var plan = await PlanAsync(probe, src, cutTimes);
        var sourceFrames = await LosslessBoundary.FramesAsync(src, _dir);

        var outDir = Path.Combine(_dir, $"sub{part}-" + Path.GetFileNameWithoutExtension(src));
        var result = await Engine(probe).SplitAsync(new SplitRequest(src, cutTimes, outDir, SelectedSegmentIndices: new[] { part }));

        result.Segments.Should().ContainSingle();
        var checks = await CheckPartsAsync(src, sourceFrames, new[] { (part, plan[part - 1].Start, plan[part - 1].End, result.Segments[0].Path) });
        return (checks[0], result.Segments[0].Path);
    }

    // serves-spec: SPEC-001#I66 — repro (b): the final part of a TS (it runs to the end, so PerSegment emits no -to)
    // starts on its planned keyframe and holds every frame from there. Shipped code seeked to the keyframe's source pts,
    // which ffmpeg reads as an offset from start_time, and started one GOP late.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task SubsetFinalPart_OnTs_StartsOnItsKeyframe_AndHoldsEveryFrameToTheEnd()
    {
        RequireBinaries();
        (await LosslessBoundary.FormatStartTimeAsync(await _fx.GetAsync("ts"))).Should().BeGreaterThan(0.001, "precondition: a TS starts after 0");

        var (check, _) = await SubsetAsync("ts", 3, 3.5, 5.5);

        check.StartsRight.Should().BeTrue(check.Describe());
        check.CountRight.Should().BeTrue("a part that runs to the end of the file is exact: " + check.Describe());
    }

    // serves-spec: SPEC-001#I66 — repro (b): an interior TS part (it has an end) starts on its planned keyframe. Its
    // count keeps stream copy's end overrun (T-220) and is written to the output, not asserted equal.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task SubsetInteriorPart_OnTs_StartsOnItsKeyframe()
    {
        RequireBinaries();

        var (check, _) = await SubsetAsync("ts", 2, 3.5, 5.5);

        check.StartsRight.Should().BeTrue(check.Describe());
        _output.WriteLine($"end overrun (T-220): {check.Frames - check.PlannedFrames} frame(s)");
    }

    // serves-spec: SPEC-001#I66 (boundary) — the first part of a TS planned from 0: the seek clamps to the file's start
    // and its -to is measured from there, so the part ends at its planned keyframe (plus at most T-220's reorder
    // overrun). Shipped code measured -to from 0 and ran start_time (1.44 s, 46 frames) past the end.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task SubsetFirstPart_OnTs_StartsAtTheFirstFrame_AndEndsAtItsKeyframe()
    {
        RequireBinaries();

        var (check, _) = await SubsetAsync("ts", 1, 3.5, 5.5);

        check.StartsRight.Should().BeTrue(check.Describe());
        (check.Frames - check.PlannedFrames).Should().BeInRange(0, 3,
            "the part may keep only stream copy's reorder overrun past its end (T-220), not start_time's worth: " + check.Describe());
    }

    // Controls: the per-segment path on sources whose format start_time is 0 — unchanged tokens, right before and after.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("bframes", 3.0, 6.0)]
    [InlineData("tsremux", 2.0, 4.0)]
    public async Task SubsetFinalPart_ZeroStartControls_StartOnTheirKeyframe_AndHoldEveryFrame(string shape, double cut1, double cut2)
    {
        RequireBinaries();

        var (check, _) = await SubsetAsync(shape, 3, cut1, cut2);

        check.StartsRight.Should().BeTrue(check.Describe());
        check.CountRight.Should().BeTrue(check.Describe());
    }

    // ---- The probe fields the engine converts by (SPEC-004 I37-I39), on the real files -------------------------

    // serves-spec: SPEC-004#I37/I38/I39 — on the real TS the probe reads the format start (the audio's, about 1.44 s),
    // the later video start (1.466667 s) and the frame rate; on the MP4 remux the format starts at 0 and the video
    // 0.023 s in. The format start is checked against ffprobe read directly, not through the code under test.
    [Trait("serves-spec", "SPEC-004")]
    [SkippableTheory]
    [InlineData("ts", 1.466667)]
    [InlineData("tsremux", 0.023)]
    [InlineData("bframes", 0.0)]
    public async Task Probe_MapsTheFormatStart_TheVideoStart_AndTheFrameRate(string shape, double videoStart)
    {
        RequireBinaries();
        var src = await _fx.GetAsync(shape);

        var result = await Probe().ProbeAsync(src);

        var info = result.Should().BeOfType<ProbeResult.ProbeSucceeded>().Which.Info;
        info.StartTime.TotalSeconds.Should().BeApproximately(await LosslessBoundary.FormatStartTimeAsync(src), 1e-6);
        var video = info.VideoStreams[0];
        video.StartTime.Should().NotBeNull();
        video.StartTime!.Value.TotalSeconds.Should().BeApproximately(videoStart, 0.0005);
        video.FrameRate.Should().BeApproximately(30, 1e-6);
        info.AudioStreams[0].FrameRate.Should().BeNull("ffprobe prints 0/0 for an audio stream");
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{shape}: format start {info.StartTime.TotalSeconds:0.######} s, video start {video.StartTime.Value.TotalSeconds:0.######} s, {video.FrameRate:0.######} fps"));
    }

    // ---- Bulk Cut: a lossless row on a TS ----------------------------------------------------------------------

    // serves-spec: SPEC-001#I66 — a Bulk Cut lossless row on the TS (intro and outro set) writes a kept middle whose
    // first frame is the source's frame at the snapped intro (BulkTrimEngine → KeptMiddleRequestBuilder → SplitEngine's
    // per-segment path).
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task BulkLosslessRow_OnTs_KeptMiddleStartsAtTheSnappedIntro()
    {
        RequireBinaries();
        var src = await _fx.GetAsync("ts");
        var probe = Probe();
        var runner = new FfmpegRunner(Locator());
        var disposer = new KeepOriginalBackupDisposer();
        var split = new SplitEngine(runner, probe, new ErrorLogWriter(Path.Combine(_dir, "logs")), new FakeDiskSpaceProbe(long.MaxValue), disposer);
        var batch = new BulkTrimEngine(
            split, new KeptMiddleRequestBuilder(probe), new FakeDiskSpaceProbe(long.MaxValue), new SmartCutEngine(runner, probe), disposer);

        var keyframes = await probe.GetKeyframesAsync(src);
        var intro = probe.SnapToNearestKeyframe(keyframes, S(3.5)).Snapped;
        var outro = probe.SnapToNearestKeyframe(keyframes, S(7.5)).Snapped;
        var desired = Path.Combine(_dir, "src_trimmed.ts");

        var result = await batch.RunAsync(
            new[] { new BulkTrimItem(src, S(3.5), S(7.5), desired) },
            new BulkTrimOptions(Precision: CutPrecision.Lossless));

        result.Items.Single().Outcome.Should().Be(ItemOutcome.Done);
        var written = result.Items.Single().OutputPath!;
        var sourceFrames = await LosslessBoundary.FramesAsync(src, _dir);
        var check = (await CheckPartsAsync(src, sourceFrames, new[] { (2, intro, (TimeSpan?)outro, written) }))[0];

        check.StartsRight.Should().BeTrue("the kept middle starts at the snapped intro: " + check.Describe());
        _output.WriteLine($"kept middle end overrun (T-220): {check.Frames - check.PlannedFrames} frame(s)");
    }
}
