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
                return await RemuxAsync(await GetAsync("ts"), "tsremux.mp4");

            // The B-frame MP4 remuxed to Matroska with -c copy (T-234): format.start_time 0 and the audio at 0, but the
            // video starts 0.023 s in (the AAC priming, the shape of the bench's mkv fixture: 0.021 s), so the engine's
            // muxer conversion relies on StreamInfo.StartTime here too. Keyframes at 0.023 + k s.
            case "mkv":
                return await RemuxAsync(await GetAsync("bframes"), "bframes.mkv");

            // The same remux to QuickTime: the mov muxer writes an edit list, so both streams start at 0 as in the MP4.
            case "mov":
                return await RemuxAsync(await GetAsync("bframes"), "bframes.mov");

            // A Matroska file encoded straight to .mkv (what a recorder writes): the video starts at 0 and the AAC audio
            // at -0.023 s, so the format start_time is negative; keyframes at k s. B-frames, 1 s GOP.
            case "mkvnative":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "native.mkv", 10, 30, "-c:v", "libx264", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0");

            // The B-frame MP4 remuxed to Matroska with its timestamps shifted by 1.5 s (-output_ts_offset, what a capture muxed
            // with a sync offset looks like): the format starts at 1.477 s (the audio), the video at 1.5 s, the first keyframe is
            // at 1.5 s and the others at 1.5 + k s. The Matroska counterpart of the TS (a first keyframe above 0, T-233).
            case "mkvlate":
                return await RemuxAsync(await GetAsync("bframes"), "late.mkv", "-output_ts_offset", "1.5");

            // The same without B-frames: a seek aimed AT the next keyframe lands on it (with B-frames ffmpeg's seek lands about
            // 0.13 s earlier than its target, so the same target still lands before it). The control that pins the margin
            // before the next keyframe.
            case "mkvbf0":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "nativebf0.mkv", 10, 30, "-c:v", "libx264", "-bf", "0", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0");

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

            // A variable-frame-rate source (T-237, SPEC-001 I68's known limit): the MediaFixtures B-frame recipe, 20 s at 30 fps, 2 s GOP
            // in source frames, with every fifth frame dropped before the encoder (what a phone or screen recording does when it
            // skips frames): 24 frames per second on average, 60 frames (2.5 s) per GOP, a 1/30 s gap and a 2/30 s gap alternating.
            case "vfr":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "vfr.mp4", 20, 30, "-vf", "select='not(eq(mod(n,5),4))'", "-fps_mode", "vfr",
                    "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");

            // Frames dropped at random (a quarter of them, a fixed seed): the gaps are 1/30, 2/30, 3/30 ... s with no pattern.
            case "vfrrandom":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "vfrrandom.mp4", 20, 30, "-vf", "select='gt(random(7),0.25)'", "-fps_mode", "vfr",
                    "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");

            // The random-drop file remuxed (-c copy) to the other containers a VFR recording comes in.
            case "vfrrandommov":
                return await RemuxAsync(await GetAsync("vfrrandom"), "vfrrandom.mov");
            case "vfrrandommkv":
                return await RemuxAsync(await GetAsync("vfrrandom"), "vfrrandom.mkv");
            case "vfrrandomts":
                return await RemuxAsync(await GetAsync("vfrrandom"), "vfrrandom.ts");

            // Two regimes: 10 s at 30 fps, then 14 s at 5 fps (one frame in six): the rate varies by 6x, the GOP is 2 s, then 12 s.
            case "vfr2x":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "vfr2x.mp4", 24, 30, "-vf", "select='lt(n,300)+gte(n,300)*eq(mod(n,6),0)'", "-fps_mode", "vfr",
                    "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");

            // The random-drop file cut short so that its last GOP is a tail of under half a second (T-237 review: the last keyframe of a
            // file has no later keyframe to overshoot onto, and its short tail started one GOP early with the first version).
            case "vfrtail":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "vfrtail.mp4", 18.8, 30, "-vf", "select='gt(random(7),0.25)'", "-fps_mode", "vfr",
                    "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");

            // A low-frame-rate file with three B-frames and 30 % of its frames dropped, GOP 6 frames (T-237 review): the next keyframe's
            // index time can be 0.8 s before its presentation time, so the middle of a 1.2 s GOP lands one GOP LATE (the first version).
            case "vfrlowfps":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "vfrlowfps.mp4", 30, 10, "-vf", "select='gt(mod(abs(sin(n*12.9898+105))*43758.5453,1),0.3)+eq(n,0)'", "-fps_mode", "vfr",
                    "-c:v", "libx264", "-g", "6", "-keyint_min", "6", "-sc_threshold", "0", "-bf", "3");

            // A screen-recording-style file (T-237 review): 20 frames of every 120 are kept, so each GOP is a burst of 0.67 s and 3.3 s of
            // nothing, with three B-frames. The next keyframe's index time sits right after the last frame of the burst, so a seek aimed
            // 2 s past a keyframe (the middle of the GOP) lands one GOP LATE while the keyframe's own aim is right: no rule on times
            // is safe here, the demuxer has to be asked.
            case "vfrburst":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "vfrburst.mp4", 60, 30, "-vf", "select='lt(mod(n,120),20)'", "-fps_mode", "vfr",
                    "-c:v", "libx264", "-g", "20", "-keyint_min", "20", "-sc_threshold", "0", "-bf", "3");

            // A constant-frame-rate MP4 with a 2.5 s GOP and B-frames (T-237 review: an end trim whose end is before the middle of the GOP).
            case "cfr25":
                return await SmartCutJointIntegrityTests.MakeFixtureAsync(
                    Dir, "cfr25.mp4", 20, 30, "-c:v", "libx264", "-g", "75", "-keyint_min", "75", "-sc_threshold", "0");

            // The 2.5 s GOP file re-cut with the options a trimming tool uses (ffmpeg -ss 3.3 -c copy -avoid_negative_ts disabled), T-237
            // review 3: the first keyframe is at -0.8 s, the others at 1.7, 4.2, 6.7 ... s, and the first part starts at 0, which is no keyframe.
            case "negkf":
                {
                    var path = Path.Combine(Dir, "negkf.mp4");
                    var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg()
                        .Raw("-y", "-loglevel", "error", "-ss", "3.3").Input(await GetAsync("cfr25"))
                        .Raw("-map", "0", "-c", "copy", "-avoid_negative_ts", "disabled").Output(path));
                    run.ExitCode.Should().Be(0, run.StdErrText);
                    return path;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown fixture shape");
        }
    }

    /// <summary>
    /// Every stream of <paramref name="source"/> copied (<c>-c copy</c>) into <paramref name="name"/>, whose extension picks the
    /// muxer, with <paramref name="outputOptions"/> before the output.
    /// </summary>
    private async Task<string> RemuxAsync(string source, string name, params string[] outputOptions)
    {
        var path = Path.Combine(Dir, name);
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error").Input(source).Raw("-map", "0", "-c", "copy").Raw(outputOptions).Output(path));
        run.ExitCode.Should().Be(0, run.StdErrText);
        return path;
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

    /// <summary>Precondition of the Matroska shape: the format starts at 0 but its video stream starts after it (the bench's mkv: 0.021 s).</summary>
    private async Task AssertVideoStartsAfterTheFormatAsync(string source)
    {
        (await LosslessBoundary.FormatStartTimeAsync(source)).Should().Be(0, "precondition: the format starts at 0");
        var info = (await Probe().ProbeAsync(source)).Should().BeOfType<ProbeResult.ProbeSucceeded>().Which.Info;
        info.VideoStreams[0].StartTime.Should().NotBeNull();
        info.VideoStreams[0].StartTime!.Value.TotalSeconds.Should().BeInRange(0.020, 0.024,
            "precondition: the video starts after the audio, so the muxer conversion depends on StreamInfo.StartTime");
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
    // a TS (its video starts 1.47 s in), that TS remuxed to MP4 (its video starts 0.023 s in, format start 0), the
    // -bf 0 control, and (T-234) the B-frame source remuxed to Matroska (video 0.023 s in, audio and format at 0) and to
    // QuickTime (both streams at 0).
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("bf0", 3.0, 6.0)]
    [InlineData("hevc", 4.0, 8.0)]
    [InlineData("longgop", 10.0, 20.0)]
    [InlineData("tiny", 1.0, 2.0)]
    [InlineData("ts", 3.5, 5.5)]
    [InlineData("tsremux", 2.0, 4.0)]
    [InlineData("mkv", 3.0, 6.0)]
    [InlineData("mov", 3.0, 6.0)]
    [InlineData("mkvnative", 3.0, 6.0)]
    [InlineData("mkvbf0", 3.0, 6.0)]
    [InlineData("mkvlate", 3.5, 5.5)]
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

        if (shape == "mkv")
        {
            await AssertVideoStartsAfterTheFormatAsync(src);
        }

        if (shape == "mkvlate")
        {
            (await LosslessBoundary.FormatStartTimeAsync(src)).Should().BeGreaterThan(1, "precondition: the file starts after 0");
        }

        var (checks, _) = await FullSplitAsync(src, cut1, cut2);

        AssertEveryPartExact(checks);
    }

    // serves-spec: SPEC-001#I7 - T-233 repro: a cut near the very start of a TS snaps to the file's FIRST keyframe (1.47 s, a source pts;
    // nothing is before it). Shipped code kept it as an interior boundary, so the plan had an empty part 1 and the segment muxer, which never
    // cuts before its first packet, ignored the boundary and shifted every later part (60/60/180 frames against the plan's empty/120/180).
    // The snap to the file's start is the file's start: that cut is dropped, and the remaining parts match the plan.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task FullSplit_Ts_ACutThatSnapsToTheFirstKeyframe_IsDropped_AndTheOtherPartsMatchThePlan()
    {
        RequireBinaries();
        await AssertACutOnTheFirstKeyframeIsDroppedAsync("ts");
    }

    // serves-spec: SPEC-001#I7 - T-234 (the shape T-233's note asked for): the same on a Matroska file whose first keyframe is
    // above 0 (1.5 s): the cut at 1.0 s snaps to it and is dropped, and the other two parts match the plan.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task FullSplit_Mkv_StartingAfterZero_ACutThatSnapsToTheFirstKeyframe_IsDropped_AndTheOtherPartsMatchThePlan()
    {
        RequireBinaries();
        await AssertACutOnTheFirstKeyframeIsDroppedAsync("mkvlate");
    }

    private async Task AssertACutOnTheFirstKeyframeIsDroppedAsync(string shape)
    {
        var src = await _fx.GetAsync(shape);
        var probe = Probe();
        var keyframes = await probe.GetKeyframesAsync(src);
        keyframes[0].Should().BeGreaterThan(S(1), "precondition: the first keyframe is a source pts above 0");
        probe.SnapToNearestKeyframe(keyframes, S(1.0)).Snapped.Should().Be(keyframes[0], "precondition: a cut at 1.0 s snaps to the first keyframe");
        var second = probe.SnapToNearestKeyframe(keyframes, S(5.5)).Snapped;
        second.Should().BeGreaterThan(keyframes[0], "precondition: the second cut snaps to an interior keyframe");

        var cutTimes = new[] { S(1.0), S(5.5) };
        var sourceFrames = await LosslessBoundary.FramesAsync(src, _dir);
        var outDir = Path.Combine(_dir, "first-kf-" + shape);
        var result = await Engine(probe).SplitAsync(new SplitRequest(src, cutTimes, outDir));

        result.Segments.Should().HaveCount(2, "the cut on the file's start is dropped: one cut remains, two parts");
        result.Warnings.Should().Contain(w => w.Contains("start of the file") && w.Contains("dropped"));
        var checks = await CheckPartsAsync(
            src,
            sourceFrames,
            new[] { (1, TimeSpan.Zero, (TimeSpan?)second, result.Segments[0].Path), (2, second, (TimeSpan?)null, result.Segments[1].Path) });
        AssertEveryPartExact(checks);
    }

    // serves-spec: SPEC-001#I73 - T-222 repro: the segment muxer's output is an ffmpeg numbered pattern, and ffmpeg expands every
    // percent sign in it, not only the part%03d. Shipped code put the output folder into that pattern as it was, so a folder such as
    // "out 100% done" made the pattern invalid and the whole Split failed. The folder part of the pattern is now escaped; the parts
    // land in the real folder, each on its planned keyframe, and the temp folder is gone afterwards. The folder named %03d would be
    // taken for the pattern itself if it were not escaped; the last one checks that a Unicode name beside the sign is unaffected.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("out 100% done")]
    [InlineData("%03d")]
    [InlineData("100%% sure")]
    [InlineData("\u0e44\u0e17\u0e22 50%")]
    public async Task FullSplit_OutputFolderWithAPercentSign_WritesEveryPart_AndLeavesNoTempFolder(string folderName)
    {
        RequireBinaries();
        var src = await _fx.GetAsync("bframes");
        var probe = Probe();
        var cutTimes = new[] { S(3), S(6) };
        var plan = await PlanAsync(probe, src, cutTimes);
        var sourceFrames = await LosslessBoundary.FramesAsync(src, _dir);
        var outDir = Path.Combine(_dir, folderName);

        var result = await Engine(probe).SplitAsync(new SplitRequest(src, cutTimes, outDir));

        result.Segments.Should().HaveCount(plan.Count);
        result.Segments.Should().AllSatisfy(s => Path.GetDirectoryName(s.Path).Should().Be(outDir, "the parts land in the real folder, not an escaped one"));
        var parts = result.Segments.Select((s, i) => (i + 1, plan[i].Start, plan[i].End, s.Path)).ToList();
        AssertEveryPartExact(await CheckPartsAsync(src, sourceFrames, parts));
        Directory.GetDirectories(outDir, ".vsj-split-*").Should().BeEmpty("the temp folder is removed once the parts are in place");
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

    // serves-spec: SPEC-001#I75 - T-237 review 3: the first part of an MP4 whose first keyframe is before zero starts at 0, which is no
    // keyframe, so no seek could "land on" it; the first version asked anyway and warned that the part "may start before its keyframe"
    // (a made-up finding: the part is written as before). Part 2 starts on a keyframe and is asked as usual, and is right.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task SubsetFirstPart_WhenTheFirstKeyframeIsBeforeZero_HasNoSeekWarning()
    {
        RequireBinaries();
        var src = await _fx.GetAsync("negkf");
        var probe = Probe();
        (await probe.GetKeyframesAsync(src))[0].Should().BeLessThan(TimeSpan.Zero, "precondition: the file's first keyframe is before zero");
        var cuts = new[] { 1.7, 4.2, 6.7 }.Select(S).ToList();

        var first = await Engine(probe).SplitAsync(new SplitRequest(src, cuts, Path.Combine(_dir, "neg1"), SelectedSegmentIndices: new[] { 1 }));
        first.Warnings.Should().NotContain(w => w.StartsWith("Part ", StringComparison.Ordinal));

        var (check, _) = await SubsetAsync("negkf", 2, 1.7, 4.2, 6.7);
        check.StartsRight.Should().BeTrue(check.Describe());
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
    // T-234 adds Matroska: the remux of the B-frame MP4 has a video that starts 0.023 s in while the format starts at 0
    // (like the MP4 remux of the TS), and the natively encoded .mkv starts its audio at -0.023 s, so its format start is
    // negative. Shipped code started the Matroska parts one GOP early (150 frames against the planned 120): on a B-frame
    // stream ffmpeg's Matroska input seek lands about 0.13 s before its target, so a seek to the keyframe lands on the one
    // before it. The no-B-frame file was right. The QuickTime remux starts both streams at 0 and was always right.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("bframes", 3.0, 6.0)]
    [InlineData("tsremux", 2.0, 4.0)]
    [InlineData("mkv", 3.0, 6.0)]
    [InlineData("mov", 3.0, 6.0)]
    [InlineData("mkvnative", 3.0, 6.0)]
    [InlineData("mkvbf0", 3.0, 6.0)]
    public async Task SubsetFinalPart_ZeroStartControls_StartOnTheirKeyframe_AndHoldEveryFrame(string shape, double cut1, double cut2)
    {
        RequireBinaries();

        var (check, _) = await SubsetAsync(shape, 3, cut1, cut2);

        check.StartsRight.Should().BeTrue(check.Describe());
        check.CountRight.Should().BeTrue(check.Describe());
    }

    // serves-spec: SPEC-001#I74 - T-234: the other parts of a Matroska file. The first part (from the file's start) and an
    // interior part (with an end) start on their planned keyframes, on the remux (video 0.023 s in) and on the natively
    // encoded file (format start -0.023 s); the QuickTime remux is the control. An interior part keeps stream copy's end
    // overrun (T-220), so only its start is asserted.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("mkv", 1)]
    [InlineData("mkv", 2)]
    [InlineData("mkvnative", 1)]
    [InlineData("mkvnative", 2)]
    [InlineData("mkvbf0", 2)]
    [InlineData("mov", 2)]
    public async Task SubsetFirstAndInteriorParts_OnMatroskaAndQuickTime_StartOnTheirKeyframe(string shape, int part)
    {
        RequireBinaries();

        var (check, _) = await SubsetAsync(shape, part, 3.0, 6.0);

        check.StartsRight.Should().BeTrue(check.Describe());
        (check.Frames - check.PlannedFrames).Should().BeInRange(0, 3, "an end overrun of stream copy (T-220) at most: " + check.Describe());
    }

    // serves-spec: SPEC-001#I74 - T-234: a Matroska file whose timestamps start after 0 (video 1.5 s, format start 1.477 s, keyframes at
    // 1.5 + k s): the first, an interior and the final part of a selection start on their keyframe, and the final part holds every frame
    // to the end. The seek is converted by the positive format start (I16) and the anchor aims inside the GOP.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SubsetParts_OnAMatroskaFileThatStartsAfterZero_StartOnTheirKeyframe(int part)
    {
        RequireBinaries();

        var (check, _) = await SubsetAsync("mkvlate", part, 3.5, 5.5);

        check.StartsRight.Should().BeTrue(check.Describe());
        if (part == 3)
        {
            check.CountRight.Should().BeTrue("a part that runs to the end of the file is exact: " + check.Describe());
        }
        else
        {
            (check.Frames - check.PlannedFrames).Should().BeInRange(0, 3, "an end overrun of stream copy (T-220) at most: " + check.Describe());
        }
    }

    // serves-spec: SPEC-001#I74 - T-234 (boundary): a final part that starts on the LAST keyframe has no next keyframe to
    // aim before, so it aims before the end of the file: it holds the last GOP, 30 frames, and not the one before it.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("mkv")]
    [InlineData("mkvnative")]
    [InlineData("mkvbf0")]
    public async Task SubsetFinalPart_OnMatroska_ThatStartsOnTheLastKeyframe_HoldsOnlyTheLastGop(string shape)
    {
        RequireBinaries();

        var (check, _) = await SubsetAsync(shape, 2, 8.8);

        check.PlannedFrames.Should().Be(30, "precondition: the cut at 8.8 s snaps to the last keyframe, 1 s before the end");
        check.StartsRight.Should().BeTrue(check.Describe());
        check.CountRight.Should().BeTrue(check.Describe());
    }

    // ---- The probe fields the engine converts by (SPEC-004 I37-I39), on the real files -------------------------

    // serves-spec: SPEC-004#I37/I38/I39 — on the real TS the probe reads the format start (the audio's, about 1.44 s),
    // the later video start (1.466667 s) and the frame rate; on the MP4 remux the format starts at 0 and the video
    // 0.023 s in; the Matroska remux of the B-frame MP4 (T-234) does the same (the bench's mkv: 0.021 s) and the QuickTime
    // remux starts both at 0. The format start is checked against ffprobe read directly, not through the code under test,
    // and against the value each shape is known to have.
    [Trait("serves-spec", "SPEC-004")]
    [SkippableTheory]
    [InlineData("ts", 1.466667, 1.443444)]
    [InlineData("tsremux", 0.023, 0.0)]
    [InlineData("bframes", 0.0, 0.0)]
    [InlineData("mkv", 0.023, 0.0)]
    [InlineData("mov", 0.0, 0.0)]
    [InlineData("mkvnative", 0.0, -0.023)]
    [InlineData("mkvbf0", 0.0, -0.023)]
    [InlineData("mkvlate", 1.5, 1.477)]
    public async Task Probe_MapsTheFormatStart_TheVideoStart_AndTheFrameRate(string shape, double videoStart, double formatStart)
    {
        RequireBinaries();
        var src = await _fx.GetAsync(shape);

        var result = await Probe().ProbeAsync(src);

        var info = result.Should().BeOfType<ProbeResult.ProbeSucceeded>().Which.Info;
        info.StartTime.TotalSeconds.Should().BeApproximately(await LosslessBoundary.FormatStartTimeAsync(src), 1e-6);
        info.StartTime.TotalSeconds.Should().BeApproximately(formatStart, 0.0005);
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
    // T-234 runs the same row on the Matroska remux: before the fix its kept middle started a GOP before the intro.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("ts", ".ts")]
    [InlineData("mkv", ".mkv")]
    public async Task BulkLosslessRow_KeptMiddleStartsAtTheSnappedIntro(string shape, string extension)
    {
        RequireBinaries();
        var src = await _fx.GetAsync(shape);
        var probe = Probe();
        var runner = new FfmpegRunner(Locator());
        var disposer = new KeepOriginalBackupDisposer();
        var split = new SplitEngine(runner, probe, new ErrorLogWriter(Path.Combine(_dir, "logs")), new FakeDiskSpaceProbe(long.MaxValue), disposer);
        var batch = new BulkTrimEngine(
            split, new KeptMiddleRequestBuilder(probe), new FakeDiskSpaceProbe(long.MaxValue), new SmartCutEngine(runner, probe), disposer);

        var keyframes = await probe.GetKeyframesAsync(src);
        var intro = probe.SnapToNearestKeyframe(keyframes, S(3.5)).Snapped;
        var outro = probe.SnapToNearestKeyframe(keyframes, S(7.5)).Snapped;
        var desired = Path.Combine(_dir, "src_trimmed" + extension);

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

    // ---- T-237: a variable-frame-rate source -------------------------------------------------------------------
    //
    // The shipped code, on a phone-style file (B-frames, a quarter of the frames dropped at random, 2.5 s GOP): a per-segment
    // lossless part began a whole GOP early (242 frames against 180 planned, its first frame the previous keyframe's) in MP4 and
    // QuickTime, because ffmpeg's seek to a keyframe's own time landed on the keyframe before it; an Exact cut that only trims the
    // end (T-224) did the same through the concat demuxer's inpoint (202 frames against 140) and was reported Done. The segment
    // muxer (a full split), Matroska (T-234's anchor) and MPEG-TS were right. An Exact cut with a head is refused for such a file
    // before it writes anything (its keyframe cannot be read where the seek lands, SPEC-001 I70).

    private async Task<IReadOnlyList<LosslessBoundary.Frame>> AssertVfrPreconditionAsync(string src)
    {
        var sourceFrames = await LosslessBoundary.FramesAsync(src, _dir);
        var gaps = sourceFrames.Zip(sourceFrames.Skip(1), (a, b) => Math.Round(b.Seconds - a.Seconds, 3)).Distinct().Order().ToList();
        gaps.Count.Should().BeGreaterThan(1, "precondition: the frame gaps differ, the source is variable-frame-rate: " + string.Join(", ", gaps));
        var keyframes = await Probe().GetKeyframesAsync(src);
        _output.WriteLine("keyframes: " + string.Join(", ", keyframes.Select(k => k.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture))));
        keyframes.Count.Should().BeGreaterThan(4, "precondition: several keyframes");
        return sourceFrames;
    }

    // serves-spec: SPEC-001#I66 - the full split (segment muxer) on a variable-frame-rate source: every part starts on its planned
    // keyframe and holds its planned frames. (Right before T-237 too: the control the per-segment path is held to.)
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("vfr", 6.0, 13.0)]
    [InlineData("vfrrandom", 6.0, 13.0)]
    [InlineData("vfr2x", 6.0, 15.0)]
    [InlineData("vfr2x", 9.0, 11.0)]
    public async Task Vfr_FullSplit_EachPartStartsOnItsKeyframe_AndHoldsItsPlannedFrames(string shape, double cut1, double cut2)
    {
        RequireBinaries();
        var src = await _fx.GetAsync(shape);
        await AssertVfrPreconditionAsync(src);

        var (checks, _) = await FullSplitAsync(src, cut1, cut2);

        AssertEveryPartExact(checks);
    }

    // serves-spec: SPEC-001#I75 - T-237 repro: the per-segment path (selected parts) on a variable-frame-rate source. Shipped code
    // wrote parts 2 and 3 of the random-drop MP4 and QuickTime one GOP early (242/180 and 212/152 frames, the first frame the
    // previous keyframe's); Matroska and MPEG-TS were right. Parts 1 and 2 keep stream copy's end overrun (T-220), which is
    // bounded here, not asserted equal.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("vfr", 1)]
    [InlineData("vfr", 2)]
    [InlineData("vfr", 3)]
    [InlineData("vfrrandom", 1)]
    [InlineData("vfrrandom", 2)]
    [InlineData("vfrrandom", 3)]
    [InlineData("vfrrandommov", 2)]
    [InlineData("vfrrandommov", 3)]
    [InlineData("vfrrandommkv", 2)]
    [InlineData("vfrrandommkv", 3)]
    [InlineData("vfrrandomts", 2)]
    [InlineData("vfrrandomts", 3)]
    [InlineData("vfr2x", 1)]
    [InlineData("vfr2x", 2)]
    [InlineData("vfr2x", 3)]
    public async Task Vfr_SubsetParts_StartOnTheirKeyframe(string shape, int part)
    {
        RequireBinaries();
        await AssertVfrPreconditionAsync(await _fx.GetAsync(shape));

        var (check, _) = await SubsetAsync(shape, part, 6.0, shape == "vfr2x" ? 15.0 : 13.0);

        check.StartsRight.Should().BeTrue(check.Describe());
        if (part == 3)
        {
            check.CountRight.Should().BeTrue("a part that runs to the end of the file is exact: " + check.Describe());
        }
        else
        {
            (check.Frames - check.PlannedFrames).Should().BeInRange(0, 3, "an end overrun of stream copy (T-220) at most: " + check.Describe());
        }
    }

    // serves-spec: SPEC-001#I75 - the Bulk Cut lossless row (BulkTrimEngine -> KeptMiddleRequestBuilder -> the per-segment path) on
    // the random-drop MP4 and QuickTime: the kept middle starts at the snapped intro (shipped code: a GOP before it).
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("vfrrandom", ".mp4")]
    [InlineData("vfrrandommov", ".mov")]
    public async Task Vfr_BulkLosslessRow_KeptMiddleStartsAtTheSnappedIntro(string shape, string extension)
    {
        RequireBinaries();
        var src = await _fx.GetAsync(shape);
        await AssertVfrPreconditionAsync(src);
        var probe = Probe();
        var runner = new FfmpegRunner(Locator());
        var disposer = new KeepOriginalBackupDisposer();
        var split = new SplitEngine(runner, probe, new ErrorLogWriter(Path.Combine(_dir, "logs")), new FakeDiskSpaceProbe(long.MaxValue), disposer);
        var batch = new BulkTrimEngine(
            split, new KeptMiddleRequestBuilder(probe), new FakeDiskSpaceProbe(long.MaxValue), new SmartCutEngine(runner, probe), disposer);

        var keyframes = await probe.GetKeyframesAsync(src);
        var intro = probe.SnapToNearestKeyframe(keyframes, S(5.5)).Snapped;
        var outro = probe.SnapToNearestKeyframe(keyframes, S(12.0)).Snapped;
        intro.Should().BeGreaterThan(S(2.8), "precondition: the intro is an interior keyframe, not the first");
        var desired = Path.Combine(_dir, "vfr_trimmed" + extension);

        var result = await batch.RunAsync(
            new[] { new BulkTrimItem(src, S(5.5), S(12.0), desired) },
            new BulkTrimOptions(Precision: CutPrecision.Lossless));

        result.Items.Single().Outcome.Should().Be(ItemOutcome.Done);
        var written = result.Items.Single().OutputPath!;
        var sourceFrames = await LosslessBoundary.FramesAsync(src, _dir);
        var check = (await CheckPartsAsync(src, sourceFrames, new[] { (2, intro, (TimeSpan?)outro, written) }))[0];

        check.StartsRight.Should().BeTrue("the kept middle starts at the snapped intro: " + check.Describe());
        (check.Frames - check.PlannedFrames).Should().BeInRange(0, 3, "an end overrun of stream copy (T-220) at most: " + check.Describe());
    }

    // serves-spec: SPEC-001#I75 - T-237 repro: an Exact cut that only trims the end (the start is a keyframe, the end is inside a
    // GOP) writes the source's frames from that keyframe on. Shipped code, on the random-drop MP4 and QuickTime, wrote the
    // previous GOP too (202 frames against 140) and reported Done, the concat demuxer's inpoint having landed on the keyframe
    // before. The first frame is the keyframe's, the planned frames follow in the source's order, and the file holds at most the
    // end overrun of the other Exact cuts (T-220) beyond them. The B-frame and QuickTime constant-frame-rate files are the controls.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("vfr", 5.0, 11.3)]
    [InlineData("vfrrandom", 5.233008, 11.0)]
    [InlineData("vfrrandom", 7.699674, 14.0)]
    [InlineData("vfrrandommov", 5.233008, 11.0)]
    [InlineData("vfr2x", 6.0, 14.2)]
    [InlineData("bframes", 3.0, 6.3)]
    [InlineData("mov", 3.0, 6.5)]
    // T-237 review: an end BEFORE the middle of the GOP. The first version wrote inpoint 6.25 for the end 5.3 on this plain 2.5 s GOP
    // MP4 and ffmpeg stopped on the list (inpoint after outpoint): the row failed. The anchor never passes the end now.
    [InlineData("cfr25", 5.0, 5.3)]
    [InlineData("cfr25", 5.0, 6.2)]
    [InlineData("cfr25", 5.0, 6.3)]
    [InlineData("cfr25", 2.5, 2.6)]
    [InlineData("bframes", 3.0, 3.4)]
    [InlineData("vfrrandom", 5.233008, 5.6)]
    [InlineData("vfrrandom", 5.233008, 6.0)]
    // T-237 review (N1): the idle-burst file, where the keyframe's own aim is right and the middle of the GOP lands one GOP late.
    [InlineData("vfrburst", 8.0, 10.0)]
    [InlineData("vfrburst", 8.0, 8.4)]
    [InlineData("vfrburst", 20.0, 23.9)]
    public async Task Vfr_ExactEndTrim_StartsOnItsKeyframe_AndHoldsTheSourcesFramesFromThere(string shape, double keyframe, double end)
    {
        RequireBinaries();
        var src = await _fx.GetAsync(shape);
        var dest = Path.Combine(_dir, $"endtrim-{shape}-{keyframe}.mp4");
        var sourceFrames = await LosslessBoundary.FramesAsync(src, _dir);
        var first = LosslessBoundary.IndexAt(sourceFrames, S(keyframe));
        var planned = sourceFrames.Skip(first).Count(f => f.Seconds < end - LosslessBoundary.HalfFrame(sourceFrames));

        var result = await new SmartCutEngine(new FfmpegRunner(Locator()), Probe()).CutAsync(src, S(keyframe), S(end), dest);

        result.FellBack.Should().BeFalse(result.FallbackReason);
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy, "the start is a keyframe: an end trim, nothing re-encoded");
        var outFrames = await LosslessBoundary.FramesAsync(dest, _dir);
        _output.WriteLine($"{shape}: {outFrames.Count} frames, planned {planned}, first frame = {LosslessBoundary.Describe(sourceFrames, outFrames[0].Hash)}");
        outFrames[0].Hash.Should().Be(sourceFrames[first].Hash, "the file starts on the keyframe the cut was asked to start on");
        outFrames.Count.Should().BeInRange(planned, planned + 3, "the planned frames, and stream copy's end overrun (T-220) at most");
        outFrames.Take(planned).Select(f => f.Hash).Should().Equal(
            sourceFrames.Skip(first).Take(planned).Select(f => f.Hash), "the source's frames from the keyframe, in order");
    }

    // serves-spec: SPEC-001#I75 - T-237 review 3: a real file in which no aim fits. The cut ends 17 ms past its keyframe, so the last aim in
    // reach (12 ms past it) still lands on the keyframe before; the engine says so and writes nothing, where the shipped code wrote the
    // previous GOP too and reported Done. (Until now only a fake demuxer reached this path.)
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task Vfr_ExactEndTrim_WhereNoAimFits_FallsBackBeforeAnythingIsWritten()
    {
        RequireBinaries();
        var src = await _fx.GetAsync("vfrrandom");
        var probe = Probe();
        const double keyframe = 5.233008;
        var atKeyframe = await probe.GetSeekLandingAsync(src, S(keyframe));
        var atLastAim = await probe.GetSeekLandingAsync(src, S(keyframe + 0.012));
        atKeyframe.Should().NotBeNull();
        atLastAim.Should().NotBeNull();
        atKeyframe!.Value.TotalSeconds.Should().BeLessThan(keyframe - 0.002, "precondition: a seek aimed at this keyframe lands on the one before it");
        atLastAim!.Value.TotalSeconds.Should().BeLessThan(keyframe - 0.002, "precondition: so does the last aim in reach");
        var dest = Path.Combine(_dir, "endtrim-nofit.mp4");

        var result = await new SmartCutEngine(new FfmpegRunner(Locator()), probe).CutAsync(src, S(keyframe), S(keyframe + 0.017), dest);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(SmartCutEngine.SeekNotExactReason);
        result.OutputPath.Should().BeNull();
        File.Exists(dest).Should().BeFalse("nothing is written when the seek cannot be made exact");
        Directory.GetDirectories(Path.GetDirectoryName(dest)!, ".vsj-*").Should().BeEmpty("no temp folder is left");
    }

    // serves-spec: SPEC-001#I70 / I75 - an Exact cut with a head on a variable-frame-rate source either is refused (the lossless cut
    // is then what the caller runs) or is the source's frames: the copied part from the boundary keyframe is the source's, in
    // number and in picture, and the whole file holds one frame per source frame of the range. Never a Done with a GOP too many.
    // The random-drop MP4 and QuickTime are refused (the keyframe the copy would start from cannot be read where the seek lands);
    // the periodic and two-rate files are cut.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("vfr", 6.3, null)]
    [InlineData("vfr", 6.3, 14.2)]
    [InlineData("vfr2x", 6.3, null)]
    [InlineData("vfr2x", 6.3, 14.2)]
    [InlineData("vfr2x", 11.3, null)]
    [InlineData("vfrrandom", 6.3, null)]
    [InlineData("vfrrandom", 6.3, 14.2)]
    [InlineData("vfrrandommov", 8.9, null)]
    public async Task Vfr_ExactCutWithAHead_IsRefusedOrIsTheSourcesFrames(string shape, double start, double? end)
    {
        RequireBinaries();
        var src = await _fx.GetAsync(shape);
        var probe = Probe();
        var dest = Path.Combine(_dir, $"exact-{shape}-{start}-{end}.mp4");

        var result = await new SmartCutEngine(new FfmpegRunner(Locator()), probe).CutAsync(src, S(start), end is null ? null : S(end.Value), dest);

        _output.WriteLine($"{shape} {start}->{end}: fellBack={result.FellBack} ({result.FallbackReason}), strategy {result.Strategy}");
        if (result.FellBack)
        {
            File.Exists(dest).Should().BeFalse("a refused cut writes nothing");
            return;
        }

        var sourceFrames = await LosslessBoundary.FramesAsync(src, _dir);
        var outFrames = await LosslessBoundary.FramesAsync(dest, _dir);
        var effective = result.EffectiveStart!.Value;
        var half = LosslessBoundary.HalfFrame(sourceFrames);
        var range = sourceFrames.Where(f => f.Seconds >= effective.TotalSeconds - half && (end is null || f.Seconds < end.Value - half)).ToList();
        outFrames.Count.Should().BeInRange(range.Count, range.Count + (end is null ? 0 : 3), "one frame per source frame of the range (T-220's overrun with an end)");

        // From the boundary keyframe on the file is the source's pictures, byte for byte, in order.
        var keyframes = await probe.GetKeyframesAsync(src);
        var boundary = keyframes.First(k => k > effective).TotalSeconds;
        var headFrames = range.Count(f => f.Seconds < boundary - half);
        var tailStart = range.FindIndex(f => f.Seconds >= boundary - half);
        var tailLength = Math.Min(range.Count - tailStart, outFrames.Count - headFrames);
        tailLength.Should().BeGreaterThan(5, "precondition: there is a copied part to compare");
        outFrames.Skip(headFrames).Take(tailLength).Select(f => f.Hash).Should().Equal(
            range.Skip(tailStart).Take(tailLength).Select(f => f.Hash), "the copied part is the source's frames from the boundary keyframe");
    }

    // serves-spec: SPEC-001#I75 - every keyframe of the file, one after the other, as the start of the final selected part (a cut ON the
    // keyframe, part 2 of 2): it starts on that keyframe and holds every frame to the end. This is the sweep that finds a keyframe
    // the anchor misses, in either direction: the first version of the anchor, reviewed (T-237), started the LAST keyframe's part one GOP
    // early when its tail was under half a second (vfrtail) and one GOP LATE on a 10 fps file with three B-frames (vfrlowfps), where
    // the aim at the keyframe had been right.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("vfr")]
    [InlineData("vfrrandom")]
    [InlineData("vfrrandommov")]
    [InlineData("vfrtail")]
    [InlineData("vfrlowfps")]
    [InlineData("vfrburst")]
    [InlineData("cfr25")]
    public async Task Vfr_EveryKeyframeAsAPartStart_LandsOnItself(string shape)
    {
        RequireBinaries();
        var src = await _fx.GetAsync(shape);
        var probe = Probe();
        IReadOnlyList<LosslessBoundary.Frame> sourceFrames;
        if (shape == "cfr25")
        {
            sourceFrames = await LosslessBoundary.FramesAsync(src, _dir);
            sourceFrames.Zip(sourceFrames.Skip(1), (a, b) => Math.Round(b.Seconds - a.Seconds, 3)).Distinct().Count()
                .Should().BeLessThan(4, "precondition: the control is constant-frame-rate");
        }
        else
        {
            sourceFrames = await AssertVfrPreconditionAsync(src);
        }

        var keyframes = await probe.GetKeyframesAsync(src);
        if (shape == "vfrtail")
        {
            var info = ((ProbeResult.ProbeSucceeded)await probe.ProbeAsync(src)).Info;
            (info.Duration - keyframes[^1]).Should().BeLessThan(S(0.5), "precondition: the last GOP is a tail of under half a second");
        }

        var wrong = new List<string>();
        foreach (var keyframe in keyframes.Skip(1))
        {
            var outDir = Path.Combine(_dir, $"sweep-{shape}-{keyframe.TotalSeconds:0.###}");
            var result = await Engine(probe).SplitAsync(new SplitRequest(src, new[] { keyframe }, outDir, SelectedSegmentIndices: new[] { 2 }));
            var check = (await CheckPartsAsync(src, sourceFrames, new[] { (2, keyframe, (TimeSpan?)null, result.Segments.Single().Path) }))[0];
            if (!check.StartsRight || !check.CountRight)
            {
                wrong.Add($"keyframe {keyframe.TotalSeconds:0.###} s: {check.Describe()}");
            }
        }

        wrong.Should().BeEmpty($"{keyframes.Count - 1} keyframes of {shape} were tried, each as the start of the final part");
    }
}
