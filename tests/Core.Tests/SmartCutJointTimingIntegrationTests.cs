using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;
using static VideoSplitJoiner.Core.Tests.SmartCutJointTiming;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-190 (G-059, SPEC-001 I68) — an H.264 Exact cut keeps its joint in time, on real media through the real ffmpeg.
///
/// <para><b>The defect.</b> The shipped engine copied the kept part into a tail file with <c>-ss HeadEnd</c> before
/// <c>-i</c> and <c>-avoid_negative_ts make_zero</c>; with B-frames that gave the tail a video <c>start_time</c> of about
/// 0.075-0.080 s against audio at 0, and the concat demuxer kept that internal offset, so the first kept frame sat two to
/// three frames after the head ended. The same shape wrote the kept bytes twice, and a source whose timestamps do not
/// start at zero (MPEG-TS) made the head and the kept part overlap by <c>start_time</c>.</para>
///
/// <para><b>The fix under test.</b> Two ffmpeg runs for a head re-encode — the head, then a concat of
/// <c>[head, the source itself from the boundary keyframe]</c> — and a stated fall back for a source whose format
/// <c>start_time</c> is not zero. Every case below fails on the shipped engine (the repro) and passes on the fixed one.</para>
///
/// <para>Every Start sits on the source's 30 fps frame grid (10.5 s = frame 315): the first head frame is then at Start
/// itself, which is what makes "presented at <c>HeadEnd − Start</c>" a statement about the joint alone.</para>
/// </summary>
public sealed class SmartCutJointTimingIntegrationTests : IDisposable
{
    private const string StartTimeReason =
        "frame-exact cutting is not supported yet for .ts, .m2ts and .mts files or other files whose timestamps do not start at zero";

    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SmartCutJointTimingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t190-" + Guid.NewGuid().ToString("N"));
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

    private static MediaProbe Probe() => new(new FfprobeRunner(Locator()));

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>One Exact cut through the real engine, with the real runner recorded.</summary>
    private sealed record CutRun(
        SmartCutResult Result, SmartCutPlan Plan, string Source, string Dest, RecordingRealRunner Runner, double Start, double? End);

    private async Task<CutRun> CutAsync(string source, double start, double? end, string destName)
    {
        var runner = new RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept-" + Guid.NewGuid().ToString("N")));
        var probe = Probe();
        var keyframes = await probe.GetKeyframesAsync(source);
        var plan = SmartCutPlanner.Plan(S(start), end is { } e ? S(e) : null, keyframes);
        var dest = Path.Combine(_dir, destName);
        var result = await new SmartCutEngine(runner, probe).CutAsync(source, S(start), end is { } e2 ? S(e2) : null, dest);
        return new CutRun(result, plan, source, dest, runner, start, end);
    }

    /// <summary>Every joint criterion of T-190 on one Exact result, reported together so a failure shows all of them.</summary>
    private async Task AssertJointAsync(CutRun cut, string label)
    {
        cut.Result.FellBack.Should().BeFalse($"the source is exact-cuttable (reason: {cut.Result.FallbackReason})");
        cut.Plan.Strategy.Should().Be(SmartCutStrategy.HeadReencode, "precondition: the start is mid-GOP with a keyframe before the end");
        File.Exists(cut.Dest).Should().BeTrue();
        cut.Runner.HeadCopy.Should().NotBeNull("the recording wrapper copies the head out before the engine's sweep");

        var headEnd = cut.Plan.HeadEnd!.Value;
        var headFrames = await FrameCountAsync(cut.Runner.HeadCopy!);
        var m = await MeasureAsync(cut.Dest, cut.Source, S(cut.Start), cut.End is { } e ? S(e) : null, headEnd, headFrames);
        var report = await SmartCutJointIntegrity.CheckAsync(
            cut.Dest, cut.Source, S(cut.Start), headEnd, _dir, default, cut.End is { } e2 ? S(e2) : null,
            cut.End is null ? 0 : m.HasBFrames, headFrames);

        _output.WriteLine($"{label}: Start {cut.Start} s, HeadEnd {headEnd.TotalSeconds:0.######} s, End {(cut.End?.ToString(CultureInfo.InvariantCulture) ?? "end of file")}, head {headFrames} frames");
        _output.WriteLine("  timing: " + m.Describe());
        _output.WriteLine("  integrity: " + report.Describe());
        report.ErrorLines.ToList().ForEach(l => _output.WriteLine("    " + l));
        _output.WriteLine("  runs: " + cut.Runner.Commands.Count + "; temp dir after each: " + string.Join(" | ", cut.Runner.TempDirListings.Select(l => string.Join(",", l))));

        var half = m.FrameInterval / 2;
        var tol = 1e-4;
        using var scope = new AssertionScope(label);

        // The joint: the first frame from the joint on is presented HeadEnd − Start after the final's first video frame.
        m.JointRel.Should().BeApproximately(m.ExpectedJointRel, half + tol,
            "the first kept frame is presented HeadEnd − Start after the final's first video frame, within half a frame");

        // Audio runs on across the joint.
        m.AudioNotIncreasing.Should().Be(0, "audio timestamps increase strictly across the joint");
        Math.Abs(m.AudioWorstGap).Should().BeLessThanOrEqualTo(m.AacFrame + tol, "no gap or overlap longer than one AAC frame");

        // A full -v error decode prints no line, the null muxer's own included.
        m.DecodeLines.Should().Be(0, "a full decode of the final prints no line (the shipped engine printed non monotonically increasing dts lines)");
        report.AllErrorLines.Should().Be(0);
        report.Passes.Should().BeTrue("frames from the joint on are the source's own, in order: " + report.Describe());

        // Durations, against the source's own packets.
        var overrunAllowed = cut.End is null ? 0 : m.HasBFrames;
        m.OverrunFrames.Should().BeInRange(0, overrunAllowed, "the end overrun is the source's has_b_frames at most (T-220)");
        if (cut.End is null)
        {
            m.OutVideoSpan.Should().BeApproximately(m.RefVideoSpan, m.FrameInterval + tol, "the video is End − Start long, within one frame");
            m.OutAv.Should().BeApproximately(m.RefAv, m.AacFrame + tol, "audio-minus-video equals the source's over [Start, End], within one AAC frame");
        }
        else
        {
            var want = m.TargetVideoSpan;
            m.OutVideoSpan.Should().BeInRange(want - m.FrameInterval - tol, want + (m.HasBFrames + 1) * m.FrameInterval + tol,
                "End − Start, plus the end overrun of up to has_b_frames frames");
            m.OutAv.Should().BeInRange(m.RefAv - m.HasBFrames * m.FrameInterval - m.AacFrame - tol, m.RefAv + m.AacFrame + tol,
                "audio-minus-video equals the source's within one AAC frame, lower by the overrun at most");
        }

        // Run shape: head, then one concat that reads the source itself; no tail file is ever written.
        cut.Runner.Commands.Should().HaveCount(2, "a head re-encode is the head encode and one concat");
        cut.Runner.TempDirListings.SelectMany(l => l).Should().NotContain(n => n.StartsWith("tail.", StringComparison.Ordinal),
            "the kept part is read from the source by the concat, never copied into a tail first");
        cut.Runner.ConcatLists.Should().ContainSingle();
        var listLines = cut.Runner.ConcatLists[0].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        listLines.Where(l => l.StartsWith("file ", StringComparison.Ordinal)).Skip(1).Should().ContainSingle()
            .Which.Should().Be("file " + JoinArgsBuilder.QuoteConcatPath(Path.GetFullPath(cut.Source)), "the second entry is the source");
    }

    // ---- The repro: B-frame H.264 + AAC, 2 s GOP and 10 s GOP, a start-only cut and a kept middle ------------------

    // serves-spec: SPEC-001#I68 / SPEC-001#I48 / SPEC-001#I44 — the joint lands on time, audio runs on, the decode is
    // silent, the kept part is the source's own packets, and the run is two ffmpeg invocations.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(60, 10.5, null)]   // 2 s GOP, start-only: HeadEnd 12 s (the shipped final prints decode lines here)
    [InlineData(60, 10.5, 20.0)]   // 2 s GOP, kept middle [10.5, 20)
    [InlineData(300, 12.5, null)]  // 10 s GOP, start-only: HeadEnd 20 s
    [InlineData(300, 12.5, 27.0)]  // 10 s GOP, kept middle [12.5, 27)
    public async Task AnH264ExactCut_KeepsItsJointInTime_AndReadsTheKeptPartFromTheSource(int gopFrames, double start, double? end)
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, $"g{gopFrames}.mp4", 30, gopFrames);

        var cut = await CutAsync(src, start, end, "exact.mp4");

        await AssertJointAsync(cut, $"GOP {gopFrames / 30} s, {(end is null ? "start-only" : "kept middle")}");
    }

    // ---- The time base: a source whose timestamps do not start at zero ------------------------------------------

    // serves-spec: SPEC-001#I45 / SPEC-001#I46 / SPEC-001#I68 — an MPEG-TS source (format start_time near 1.4 s) either falls
    // back with the stated reason, or the output passes the integrity and the joint-timing checks. Shipped: the head and the
    // kept part overlapped by start_time.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnExactCut_OnAMpegTsSource_FallsBack_OrPassesTheJointChecks()
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, "src.ts", 12, 60);
        var formatStart = await FormatStartAsync(src);
        _output.WriteLine($"MPEG-TS source: format.start_time {formatStart:0.000000} s");
        formatStart.Should().BeGreaterThan(0.001, "precondition: the muxer starts a TS near 1.4 s, not at 0");

        // 3 s past the video's first frame: mid-GOP on the 2 s grid, and on the frame grid.
        var videoStart = await StreamStartAsync(src, "v:0");
        var cut = await CutAsync(src, videoStart + 3.0, null, "exact.ts");

        if (cut.Result.FellBack)
        {
            _output.WriteLine("fell back: " + cut.Result.FallbackReason);
            cut.Result.FallbackReason.Should().Be(StartTimeReason);
            cut.Runner.Commands.Should().BeEmpty("a source Exact cannot cut costs no ffmpeg run");
            File.Exists(cut.Dest).Should().BeFalse("a fall-back writes nothing; the caller runs the lossless cut");
            return;
        }

        await AssertJointAsync(cut, "MPEG-TS, ran");
    }

    // serves-spec: SPEC-002#I50 / SPEC-002#I51 / SPEC-001#I45 - the whole Bulk row on a real MPEG-TS: Exact falls back with the
    // stated reason, the lossless cut it runs is the keyframe-snapped cut T-219 made correct on a TS, and it decodes clean.
    [Trait("serves-spec", "SPEC-002")]
    [SkippableFact]
    public async Task AnExactBulkRow_OnAMpegTsSource_WritesTheLosslessCut_WithTheReason_ThatDecodesClean()
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, "camera.ts", 12, 60);
        var videoStart = await StreamStartAsync(src, "v:0");
        var runner = new FfmpegRunner(Locator());
        var probe = Probe();
        var split = new SplitEngine(runner, probe, new ErrorLogWriter(Path.Combine(_dir, "logs"))); // never %LOCALAPPDATA%
        var batch = new BulkTrimEngine(
            split, new KeptMiddleRequestBuilder(probe), new FakeDiskSpaceProbe(long.MaxValue), new SmartCutEngine(runner, probe), null);
        var dest = Path.Combine(_dir, "camera_trimmed.ts");
        var item = new BulkTrimItem(src, S(videoStart + 3.0), null, dest); // mid-GOP on the 2 s grid

        var result = await batch.RunAsync(new[] { item }, new BulkTrimOptions(Precision: CutPrecision.Exact));

        var row = result.Items.Single();
        row.Outcome.Should().Be(ItemOutcome.Done, row.Error?.Message);
        row.Warnings.Should().Contain(w => w.StartsWith(BulkTrimEngine.ExactFallbackPrefix, StringComparison.Ordinal) && w.Contains(StartTimeReason, StringComparison.Ordinal));
        File.Exists(row.OutputPath).Should().BeTrue();
        var (errors, lines) = await SmartCutJointIntegrity.DecodeAsync(row.OutputPath!);
        _output.WriteLine($"MPEG-TS Exact row, fell back: output decodes with {errors} decoder/demuxer error line(s), {lines.Count} line(s) in all");
        errors.Should().Be(0, "the lossless cut is the source's own packets from a keyframe on");
        var probed = await probe.ProbeAsync(row.OutputPath!);
        probed.Should().BeOfType<ProbeResult.ProbeSucceeded>();
        ((ProbeResult.ProbeSucceeded)probed).Info.Duration.TotalSeconds.Should().BeApproximately(
            12 - 2.0, 0.5, "3 s past the first frame snaps to the keyframe 2 s in (the lossless cut), not to 3 s (Exact would)");
    }

    // ---- The decision: a source whose VIDEO starts after its audio while its format starts at 0 -----------------------

    // serves-spec: SPEC-001#I68 / SPEC-001#I45 - a file the app's own Join writes (or the bench's 4k10m) has a format
    // start_time of 0 but a video that starts one AAC frame late. The Exact gate reads the FORMAT start only: the joint on
    // such a file is as correct as on any other (measured here), so it is cut, not refused. The Build log has the numbers.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnExactCut_OnASourceWhoseVideoStartsAfterItsAudio_LandsTheJointOnTime()
    {
        RequireBinaries();
        var baseSrc = await MakeSourceAsync(_dir, "base.mp4", 30, 60);
        var late = await MakeLateVideoSourceAsync(_dir, "late.mp4", baseSrc);

        var formatStart = await FormatStartAsync(late);
        var videoStart = await StreamStartAsync(late, "v:0");
        _output.WriteLine($"mp4 source: format.start_time {formatStart:0.000000} s, video start_time {videoStart:0.000000} s");
        Math.Abs(formatStart).Should().BeLessThanOrEqualTo(0.001, "precondition: the format starts at 0");
        videoStart.Should().BeGreaterThan(0.015, "precondition: the video starts one AAC frame after the audio");

        // 10.5 s past the video's first frame: on the frame grid, mid-GOP.
        var cut = await CutAsync(late, videoStart + 10.5, null, "exact.mp4");

        await AssertJointAsync(cut, "video-late mp4");
    }

    // serves-spec: SPEC-001#I68 / SPEC-001#I45 - Matroska (the bench's mkv fixture: format start 0, video 21 ms late). The Exact
    // result there is much better than the shipped one (which wrote a whole extra GOP of frames: 60 more than the source's) but
    // not clean: the head file is a Matroska file whose AAC priming makes it start at -21 ms, so the concat shifts the head by
    // 21 ms against the copied part - the joint is 21 ms early, with one AAC frame of audio overlap and null-muxer lines in a
    // full decode. That is the head's container, not the source's late video (the same source as an MP4 joins exactly, above),
    // so it is not what the gate reads; it is a discovered defect of T-190 (Build log), and this test guards what holds today:
    // the frames from the joint on are the source's own, none lost and none duplicated, and the joint and the audio are off
    // by no more than one AAC frame.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnExactCut_OnAnMkvSource_NeitherLosesNorDuplicatesFrames_AndItsJointIsWithinOneAacFrame()
    {
        RequireBinaries();
        var baseSrc = await MakeSourceAsync(_dir, "base.mp4", 30, 60);
        var late = await RemuxAsync(_dir, "late.mkv", await MakeLateVideoSourceAsync(_dir, "late.mp4", baseSrc));
        var videoStart = await StreamStartAsync(late, "v:0");
        _output.WriteLine($"mkv source: format.start_time {await FormatStartAsync(late):0.000000} s, video start_time {videoStart:0.000000} s");

        var cut = await CutAsync(late, videoStart + 10.5, null, "exact.mkv");

        cut.Result.FellBack.Should().BeFalse($"the format starts at 0 (reason: {cut.Result.FallbackReason})");
        cut.Runner.Commands.Should().HaveCount(2);
        var headEnd = cut.Plan.HeadEnd!.Value;
        var headFrames = await FrameCountAsync(cut.Runner.HeadCopy!);
        var m = await MeasureAsync(cut.Dest, cut.Source, S(cut.Start), null, headEnd, headFrames);
        var report = await SmartCutJointIntegrity.CheckAsync(cut.Dest, cut.Source, S(cut.Start), headEnd, _dir, default, null, 0, headFrames);
        _output.WriteLine("  timing: " + m.Describe());
        _output.WriteLine("  integrity: " + report.Describe());

        using var scope = new AssertionScope("mkv");
        report.TailLengthsMatch.Should().BeTrue("no frame is lost or duplicated from the joint on: " + report.Describe());
        report.MismatchedFrames.Should().Be(0, "the frames from the joint on are the source's own, in order: " + report.Describe());
        report.DecoderErrorLines.Should().Be(0, "no decoder or demuxer error: " + report.Describe());
        m.OutVideoFrames.Should().Be(headFrames + report.SourceTailFrames, "the head plus the source's frames from HeadEnd");
        Math.Abs(m.JointRel - m.ExpectedJointRel).Should().BeLessThanOrEqualTo(m.AacFrame + 1e-4, "within one AAC frame (the head's priming)");
        Math.Abs(m.AudioWorstGap).Should().BeLessThanOrEqualTo(m.AacFrame + 1e-4, "at most one AAC frame of audio overlap");
    }
}
