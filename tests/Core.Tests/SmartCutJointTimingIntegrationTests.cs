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
/// <para><b>The effective start.</b> An Exact cut starts at the first source video frame at or after the requested Start
/// (SPEC-001 I43): the engine moves its Start up to that frame before it plans and builds the head, so the head's frame grid
/// and the copied part's grid coincide. Before that (T-190's second pass) only a Start ON the 30 fps frame grid joined
/// exactly; a Start between two frames left the head's frames on the encoder's grid counted from Start, up to one frame off
/// the copied part's: the joint 33 ms early with a null-muxer line, or one repeated frame. "Presented at
/// <c>HeadEnd − Start</c>" below means the EFFECTIVE start, which these tests find from the source's own packets, not from
/// the engine.</para>
/// </summary>
public sealed class SmartCutJointTimingIntegrationTests : IDisposable
{
    private const string StartTimeReason =
        "frame-exact cutting is not supported yet for .ts, .m2ts and .mts files or other files whose timestamps do not start at zero";

    private const string MatroskaReason = "frame-exact cutting is not supported yet for .mkv and .webm files";

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
        SmartCutResult Result, SmartCutPlan Plan, string Source, string Dest, RecordingRealRunner Runner, double Start, double? End,
        double EffectiveStart);

    private async Task<CutRun> CutAsync(string source, double start, double? end, string destName)
    {
        var runner = new RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept-" + Guid.NewGuid().ToString("N")));
        var probe = Probe();
        var keyframes = await probe.GetKeyframesAsync(source);

        // The effective start, from the source's own packets: the first video frame at or after the requested Start.
        var effective = await FirstFrameAtOrAfterAsync(source, start);
        var plan = SmartCutPlanner.Plan(S(effective), end is { } e ? S(e) : null, keyframes);
        var dest = Path.Combine(_dir, destName);
        var result = await new SmartCutEngine(runner, probe).CutAsync(source, S(start), end is { } e2 ? S(e2) : null, dest);
        return new CutRun(result, plan, source, dest, runner, start, end, effective);
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
        var m = await MeasureAsync(cut.Dest, cut.Source, S(cut.EffectiveStart), cut.End is { } e ? S(e) : null, headEnd, headFrames);
        var report = await SmartCutJointIntegrity.CheckAsync(
            cut.Dest, cut.Source, S(cut.EffectiveStart), headEnd, _dir, default, cut.End is { } e2 ? S(e2) : null,
            cut.End is null ? 0 : m.HasBFrames, headFrames);

        _output.WriteLine($"{label}: Start {cut.Start} s (effective {cut.EffectiveStart:0.######} s), HeadEnd {headEnd.TotalSeconds:0.######} s, End {(cut.End?.ToString(CultureInfo.InvariantCulture) ?? "end of file")}, head {headFrames} frames");
        _output.WriteLine("  timing: " + m.Describe());
        _output.WriteLine("  integrity: " + report.Describe());
        report.ErrorLines.ToList().ForEach(l => _output.WriteLine("    " + l));
        _output.WriteLine("  runs: " + cut.Runner.Commands.Count + "; temp dir after each: " + string.Join(" | ", cut.Runner.TempDirListings.Select(l => string.Join(",", l))));

        // The tolerances (T-190, third pass). The joint, the audio gap and a start-only video span are measured to the microsecond
        // (ffprobe prints six decimals) and every case below lands on 0.00 ms, so they are held to 1 ms, not to half a frame or an
        // AAC frame: a joint that is a fraction of a frame off, or an audio overlap of a few milliseconds, is a defect this test
        // must catch. What stays at the documented allowance is only what genuinely needs it: the audio-minus-video duration
        // (the output's audio is counted from its first packet at or after the final's first video frame, which drops the head's
        // AAC priming; what is left is the gap between the Start and the source's first audio packet after it, under one AAC frame
        // and measured up to 17 ms here), and the end overrun of a kept middle (has_b_frames frames, T-220).
        const double ExactTolerance = 0.001;
        var tol = 1e-4;
        using var scope = new AssertionScope(label);

        // The joint: the first frame from the joint on is presented HeadEnd − Start after the final's first video frame.
        m.JointRel.Should().BeApproximately(m.ExpectedJointRel, ExactTolerance,
            "the first kept frame is presented HeadEnd − Start after the final's first video frame, within 1 ms");

        // The head holds exactly the source's frames of [Start, HeadEnd): an extra (repeated) or a missing frame at the joint
        // moves every frame after it by one INDEX, which the by-index md5 check below cannot see.
        m.HeadFrames.Should().Be(m.ExpectedHeadFrames,
            "the head holds one frame per source frame of [effective Start, HeadEnd), neither a repeated nor a lost one");

        // Audio runs on across the joint.
        m.AudioNotIncreasing.Should().Be(0, "audio timestamps increase strictly across the joint");
        Math.Abs(m.AudioWorstGap).Should().BeLessThanOrEqualTo(ExactTolerance, "no audio gap or overlap across the joint longer than 1 ms");

        // A full -v error decode prints no line, the null muxer's own included.
        m.DecodeLines.Should().Be(0, "a full decode of the final prints no line (the shipped engine printed non monotonically increasing dts lines)");
        report.AllErrorLines.Should().Be(0);
        report.Passes.Should().BeTrue("frames from the joint on are the source's own, in order: " + report.Describe());

        // Durations, against the source's own packets.
        var overrunAllowed = cut.End is null ? 0 : m.HasBFrames;
        m.OverrunFrames.Should().BeInRange(0, overrunAllowed, "the end overrun is the source's has_b_frames at most (T-220)");
        if (cut.End is null)
        {
            m.OutVideoSpan.Should().BeApproximately(m.RefVideoSpan, ExactTolerance, "the video is the source's [Start, End) long, within 1 ms");
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

    // ---- A Start BETWEEN two frames (the normal case in use) ---------------------------------------------------------

    // serves-spec: SPEC-001#I43 / SPEC-001#I68 — a Start that is not on the source's frame grid. The shipped engine placed the
    // head's frames on the encoder's 1/fps grid counted from Start: about half of these Starts showed the first kept frame
    // 33 ms early with one `non monotonically increasing dts` line, the other half ONE REPEATED FRAME (7.018: the head ended
    // with a re-encoded frame 240 and the copied keyframe 240 followed). The engine now starts at the first frame at or after
    // Start, so the grids coincide.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(7.0035)]
    [InlineData(7.0100)]
    [InlineData(7.0180)]
    [InlineData(7.0250)]
    [InlineData(7.0310)]
    public async Task AnExactCut_AtAStartBetweenTwoFrames_KeepsItsJointInTime(double start)
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, "g60.mp4", 30, 60);

        var cut = await CutAsync(src, start, null, "exact.mp4");

        await AssertJointAsync(cut, $"off-grid Start {start}");
    }

    // serves-spec: SPEC-001#I43 / SPEC-001#I68 — the same with an end (a kept middle), the repeated-frame Start included.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(7.0180, 15.5)]
    [InlineData(7.0310, 15.5)]
    public async Task AnExactCut_AtAStartBetweenTwoFrames_WithAnEnd_KeepsItsJointInTime(double start, double end)
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, "g60.mp4", 30, 60);

        var cut = await CutAsync(src, start, end, "exact.mp4");

        await AssertJointAsync(cut, $"off-grid Start {start}, End {end}");
    }

    // serves-spec: SPEC-001#I43 / SPEC-001#I68 — the app's own Join output (video starts 21 ms after its audio, frames at
    // 0.021029 + k/30) cut at the round Starts a user types: 3.3, 10.5, 25.5 and 33.3 s are all OFF that file's grid. The
    // shipped engine gave -33 ms and a decode line at each.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(3.3)]
    [InlineData(10.5)]
    [InlineData(25.5)]
    [InlineData(33.3)]
    public async Task AnExactCut_OnTheAppsOwnJoinOutput_AtARoundStart_KeepsItsJointInTime(double start)
    {
        RequireBinaries();
        var baseSrc = await MakeSourceAsync(_dir, "base.mp4", 30, 60);
        var late = await MakeLateVideoSourceAsync(_dir, "late.mp4", baseSrc);
        (await StreamStartAsync(late, "v:0")).Should().BeGreaterThan(0.015, "precondition: the video starts after the audio");

        var cut = await CutAsync(late, start, null, "exact.mp4");

        await AssertJointAsync(cut, $"Join output, Start {start}");
    }

    // ---- A picture that stops while the sound goes on -----------------------------------------------------------------

    // serves-spec: SPEC-001#I43 / SPEC-001#I46 - the snap moves the start up to the next frame, a frame or so, not across a gap in
    // the picture (T-190, third pass). A 20 s source with video only before 10.2 s and from 12.5 s and continuous audio: a Start
    // at 11.0 s used to become 12.5 s, the file started there and the 1.5 s of sound in between was silently dropped. Now the
    // engine falls back with a stated reason (zero ffmpeg runs, nothing written), and a Start inside the contiguous picture
    // still snaps to its frame.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnExactCut_WhoseNextVideoFrameIsAGapAway_FallsBack_InsteadOfDroppingTheAudioInBetween()
    {
        RequireBinaries();
        var src = await MakeGappedSourceAsync(_dir, "gap.mp4");
        var dest = Path.Combine(_dir, "exact.mp4");
        var runner = new RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept"));

        var result = await new SmartCutEngine(runner, Probe()).CutAsync(src, S(11.0), null, dest);

        _output.WriteLine($"fell back: {result.FellBack}; reason: {result.FallbackReason}; effective start: {result.EffectiveStart?.TotalSeconds:0.######}; runs: {runner.Commands.Count}");
        if (!result.FellBack)
        {
            var written = await Probe().ProbeAsync(dest);
            _output.WriteLine($"NOT refused: Done, output {((ProbeResult.ProbeSucceeded)written).Info.Duration.TotalSeconds:0.0} s for a request at 11.0 s of a 20 s source");
        }

        result.FellBack.Should().BeTrue("the next video frame is 1.5 s after the request");
        result.FallbackReason.Should().Be("the video has no frame within a few frames after the requested time");
        runner.Commands.Should().BeEmpty();
        File.Exists(dest).Should().BeFalse();

        var inside = await new SmartCutEngine(runner, Probe()).CutAsync(src, S(5.018), null, Path.Combine(_dir, "inside.mp4"));
        inside.FellBack.Should().BeFalse("a Start inside the contiguous picture still snaps, by a frame");
        inside.EffectiveStart!.Value.TotalSeconds.Should().BeApproximately(5.033333, 1e-6);
    }

    // ---- A container that lists no pts for some packets ----------------------------------------------------------------

    // serves-spec: SPEC-001#I43 / SPEC-004#I40 - an AVI with B-frames (about a third of its video packets report no pts, only a dts
    // on the 1/fps grid) is accepted by the gate. The snap read pts alone and skipped the frame at 4.033333, so a Start at 4.018
    // began the cut at 4.066667; it now takes the frame's dts like the keyframe scan, and the cut starts on 4.033333 with every
    // frame of the source from there on present and a clean decode. (Whether AVI joints are right in general is T-202's.)
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnExactCut_OnAnAviWhosePacketsHaveNoPts_StartsOnTheFirstFrameAtOrAfterTheRequest()
    {
        RequireBinaries();
        var avi = await MakeAviWithoutPtsAsync(_dir, "bframes.avi");
        (await VideoPacketsWithoutPtsAsync(avi)).Should().BeGreaterThan(50, "precondition: the fixture shows the shape");
        var runner = new RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept"));
        var dest = Path.Combine(_dir, "exact.avi");

        var result = await new SmartCutEngine(runner, Probe()).CutAsync(avi, S(4.018), null, dest);

        _output.WriteLine($"fell back: {result.FellBack} {result.FallbackReason}; effective {result.EffectiveStart?.TotalSeconds:0.######}; runs {runner.Commands.Count}");
        result.FellBack.Should().BeFalse(result.FallbackReason);
        result.EffectiveStart!.Value.TotalSeconds.Should().BeApproximately(4.033333, 1e-6, "the frame at 4.033333 has no pts, only a dts");
        var sourceFrames = (await PacketsAsync(avi, "v:0")).Count(p => p.Dts >= 4.033333 - 0.001);
        (await VideoPacketCountAsync(dest)).Should().Be(sourceFrames, "every frame of the source from the effective start on, none lost or repeated");
        var (errors, lines) = await SmartCutJointIntegrity.DecodeAsync(dest);
        _output.WriteLine($"output decode: {errors} decoder/demuxer error line(s), {lines.Count} line(s) in all");
        errors.Should().Be(0);
        lines.Count.Should().Be(0);
    }

    // ---- A Start that leaves no video frame in the head ---------------------------------------------------------------

    // serves-spec: SPEC-001#I46 / SPEC-001#I40 — a Start 25 ms before a keyframe (the planner's tolerance is 10 ms) used to
    // plan a head [11.975, 12) that holds NO video frame (the frames are at 11.9667 and 12.0): the head was audio only, the
    // concat took its streams from it, and the cut was reported Done with an audio-only file. The engine never reports an
    // Exact result that lost the source's video: it falls back (the start is within a frame of the keyframe, where the
    // lossless cut is exact), and if it ever writes a file, the file holds the video.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnExactCut_WhoseStartIsJustBeforeAKeyframe_NeverReportsDoneWithoutVideo()
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, "g60.mp4", 30, 60);
        var dest = Path.Combine(_dir, "exact.mp4");
        var runner = new RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept"));

        var result = await new SmartCutEngine(runner, Probe()).CutAsync(src, S(11.975), null, dest);

        _output.WriteLine($"fell back: {result.FellBack}; reason: {result.FallbackReason}; output exists: {File.Exists(dest)}");
        if (result.FellBack)
        {
            File.Exists(dest).Should().BeFalse("a fall-back writes nothing; the caller runs the lossless cut");
            result.FallbackReason.Should().NotBeNullOrWhiteSpace();
            return;
        }

        (await VideoPacketCountAsync(dest)).Should().BeGreaterThan(0, "an Exact result of a source with video holds its video");
    }

    /// <summary>The real probe with the frame snap switched off: the engine then plans from the request itself, as it did before the snap.</summary>
    private sealed class NoSnapProbe : IMediaProbe, IVideoFrameProbe
    {
        private readonly MediaProbe _inner = Probe();

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) => _inner.ProbeAsync(path, ct);

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) => _inner.GetKeyframesAsync(path, ct);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) => _inner.SnapToNearestKeyframe(keyframes, requested);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => _inner.AverageGop(keyframes);

        public Task<TimeSpan?> FirstVideoFrameAtOrAfterAsync(string path, TimeSpan time, CancellationToken ct = default) => Task.FromResult<TimeSpan?>(null);

        public Task<IReadOnlyList<TimeSpan>?> GetVideoFrameTimesAsync(string path, TimeSpan from, TimeSpan to, CancellationToken ct = default) =>
            _inner.GetVideoFrameTimesAsync(path, from, to, ct);
    }

    // serves-spec: SPEC-001#I46 - the head check on real media, independent of the snap: the planned head [11.975, 12) holds no
    // source frame (the frames are at 11.9667 and 12.0), so the real head file is audio only. The engine reads that off the file
    // it wrote and falls back with a reason; shipped, it reported Done with an audio-only file. Nothing is left behind.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnExactCut_WhoseHeadHoldsNoVideoFrame_FallsBack_InsteadOfWritingAnAudioOnlyFile()
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, "g60.mp4", 30, 60);
        var dest = Path.Combine(_dir, "exact.mp4");
        var runner = new RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept"));

        var result = await new SmartCutEngine(runner, new NoSnapProbe()).CutAsync(src, S(11.975), null, dest);

        _output.WriteLine($"fell back: {result.FellBack}; reason: {result.FallbackReason}; runs: {runner.Commands.Count}");
        result.FellBack.Should().BeTrue("the head the engine wrote has no picture");
        result.FallbackReason.Should().Contain("no video frame");
        runner.Commands.Should().ContainSingle("the head ran, the concat did not");
        File.Exists(dest).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
        (await VideoPacketCountAsync(runner.HeadCopy!)).Should().Be(0, "precondition: the head file really holds no video frame");
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

    // serves-spec: SPEC-001#I45 / SPEC-001#I46 / SPEC-001#I68 - Matroska and WebM fall back, whatever their format start_time.
    // Measured on the bench's mkv fixture shape (format start 0, video 21 ms late) AFTER the frame snap, so the Start is not the
    // cause: at Starts on and off the frame grid (10.5, 10.518, 10.531, 3.3, 25.5 s) the Exact result lost and repeated no
    // frame, but its joint was 21 ms early (-0.63 frames) with 4-5 non-increasing audio packets (one AAC frame of overlap) and
    // 3-4 lines in a full decode. The head file is Matroska too and starts at -21 ms (the AAC priming packet, which an MP4 head
    // hides in an edit list), so the concat shifts it 21 ms against the copied part. No small fix held, and an ffmpeg-made .mkv
    // starts below zero anyway (-21 ms h264+aac), so the whole container is refused with a reason that names the files.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("remuxed-from-mp4")]
    [InlineData("encoded-as-mkv")]
    public async Task AnExactCut_OnAMatroskaSource_FallsBackWithTheContainerReason_AndRunsNothing(string shape)
    {
        RequireBinaries();
        string source;
        if (shape == "encoded-as-mkv")
        {
            source = await MakeSourceAsync(_dir, "direct.mkv", 12, 60);
        }
        else
        {
            var baseSrc = await MakeSourceAsync(_dir, "base.mp4", 30, 60);
            source = await RemuxAsync(_dir, "late.mkv", await MakeLateVideoSourceAsync(_dir, "late.mp4", baseSrc));
        }

        _output.WriteLine($"{shape}: format.start_time {await FormatStartAsync(source):0.000000} s, video start_time {await StreamStartAsync(source, "v:0"):0.000000} s");

        var cut = await CutAsync(source, 3.3, null, "exact.mkv");

        cut.Result.FellBack.Should().BeTrue();
        cut.Result.FallbackReason.Should().Be(MatroskaReason, "the reason names the files the user sees (.mkv and .webm), not timestamps");
        cut.Runner.Commands.Should().BeEmpty("a source Exact cannot cut costs no ffmpeg run");
        File.Exists(cut.Dest).Should().BeFalse("a fall-back writes nothing; the caller runs the lossless cut");
    }

    // serves-spec: SPEC-001#I45 / SPEC-001#I46 - the gate is the FILE NAME as well as the container (T-190, third pass). The head
    // and the final take their container from the OUTPUT extension, so MP4 content (ffprobe: mov,mp4) named .mkv, or any source
    // cut into a .mkv/.webm/.mka output, got a Matroska head and final while the container gate saw an MP4 and let it through.
    // Measured at the second review on a 20 s source copied to mislabeled.mkv, Start 7.018: the final was 622.4 s long, its audio
    // packets 1.024 s apart, and the cut was reported Done. Now the source's name and the output's name both count.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("mislabeled.mkv", "exact.mkv")]
    [InlineData("plain.mp4", "exact.mkv")]
    [InlineData("plain.mp4", "exact.webm")]
    [InlineData("plain.mp4", "exact.mka")]
    [InlineData("mislabeled.mkv", "exact.mp4")]
    public async Task AnExactCut_WhoseSourceOrOutputIsNamedMatroska_FallsBack_WhateverTheContentIs(string sourceName, string outputName)
    {
        RequireBinaries();
        var plain = await MakeSourceAsync(_dir, "plain.mp4", 20, 60);
        var source = sourceName == "plain.mp4" ? plain : Path.Combine(_dir, sourceName);
        if (source != plain)
        {
            File.Copy(plain, source);
        }

        var probed = await Probe().ProbeAsync(source);
        _output.WriteLine($"{sourceName} -> {outputName}: container '{((ProbeResult.ProbeSucceeded)probed).Info.Container}'");

        var cut = await CutAsync(source, 7.018, null, outputName);

        if (!cut.Result.FellBack)
        {
            var written = await Probe().ProbeAsync(cut.Dest);
            var length = written is ProbeResult.ProbeSucceeded w ? w.Info.Duration.TotalSeconds : double.NaN;
            _output.WriteLine($"NOT refused: reported Done, output {length:0.0} s long for a 20 s source, {cut.Runner.Commands.Count} runs");
        }

        cut.Result.FellBack.Should().BeTrue("a Matroska/WebM name on the source or the output makes the head and the final Matroska, whose joint is wrong");
        cut.Result.FallbackReason.Should().Be(MatroskaReason);
        cut.Runner.Commands.Should().BeEmpty("a source Exact cannot cut costs no ffmpeg run");
        File.Exists(cut.Dest).Should().BeFalse("a fall-back writes nothing; the caller runs the lossless cut");
    }

    // serves-spec: SPEC-001#I45 / SPEC-001#I46 - the same name rule for the transport-stream names the Exact note lists. MP4
    // content cut into a .ts / .m2ts / .mts output got a transport-stream head and final (measured: the first kept frame three
    // frames early, 547 ms of audio overlap, 2 non-increasing audio packets, reported Done), so the name falls back with the
    // sentence that names those extensions.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("exact.ts")]
    [InlineData("exact.m2ts")]
    [InlineData("exact.MTS")]
    public async Task AnExactCut_IntoATransportStreamName_FallsBack_WhateverTheContentIs(string outputName)
    {
        RequireBinaries();
        var plain = await MakeSourceAsync(_dir, "plain.mp4", 20, 60);

        var cut = await CutAsync(plain, 7.5, null, outputName);

        cut.Result.FellBack.Should().BeTrue();
        cut.Result.FallbackReason.Should().Be(StartTimeReason);
        cut.Runner.Commands.Should().BeEmpty("a name Exact cannot cut costs no ffmpeg run");
        File.Exists(cut.Dest).Should().BeFalse();
    }

    // serves-spec: SPEC-002#I50 / SPEC-002#I51 / SPEC-001#I45 - the whole Bulk row on MP4 content named .mkv (a mislabeled
    // file): the row's output keeps the .mkv name, so Exact falls back with the Matroska sentence and the lossless cut it
    // runs decodes clean. Shipped, this was the case that wrote a 622 s file for a 20 s source and reported Done.
    [Trait("serves-spec", "SPEC-002")]
    [SkippableFact]
    public async Task AnExactBulkRow_OnMp4ContentNamedMkv_WritesTheLosslessCut_WithTheReason_ThatDecodesClean()
    {
        RequireBinaries();
        var plain = await MakeSourceAsync(_dir, "plain.mp4", 20, 60);
        var src = Path.Combine(_dir, "mislabeled.mkv");
        File.Copy(plain, src);
        var runner = new FfmpegRunner(Locator());
        var probe = Probe();
        var split = new SplitEngine(runner, probe, new ErrorLogWriter(Path.Combine(_dir, "logs"))); // never %LOCALAPPDATA%
        var batch = new BulkTrimEngine(
            split, new KeptMiddleRequestBuilder(probe), new FakeDiskSpaceProbe(long.MaxValue), new SmartCutEngine(runner, probe), null);
        var dest = Path.Combine(_dir, "mislabeled_trimmed.mkv");

        var result = await batch.RunAsync(
            new[] { new BulkTrimItem(src, S(7.018), null, dest) }, new BulkTrimOptions(Precision: CutPrecision.Exact));

        var row = result.Items.Single();
        row.Outcome.Should().Be(ItemOutcome.Done, row.Error?.Message);
        row.Warnings.Should().Contain(w => w.StartsWith(BulkTrimEngine.ExactFallbackPrefix, StringComparison.Ordinal) && w.Contains(MatroskaReason, StringComparison.Ordinal));
        var written = await probe.ProbeAsync(row.OutputPath!);
        var length = ((ProbeResult.ProbeSucceeded)written).Info.Duration.TotalSeconds;
        _output.WriteLine($"mislabeled .mkv Exact row, fell back: output {length:0.0} s for a 20 s source, 7.018 s start");
        length.Should().BeInRange(10, 14, "the lossless cut keeps about 20 − 7 s (snapped to a keyframe), not 622 s");
        var (errors, _) = await SmartCutJointIntegrity.DecodeAsync(row.OutputPath!);
        errors.Should().Be(0);
    }

    // serves-spec: SPEC-002#I50 / SPEC-002#I51 / SPEC-001#I45 - the whole Bulk row on a real .mkv: Exact falls back with the
    // stated reason and the lossless cut it runs decodes clean.
    [Trait("serves-spec", "SPEC-002")]
    [SkippableFact]
    public async Task AnExactBulkRow_OnAMatroskaSource_WritesTheLosslessCut_WithTheReason_ThatDecodesClean()
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, "camera.mkv", 12, 60);
        var runner = new FfmpegRunner(Locator());
        var probe = Probe();
        var split = new SplitEngine(runner, probe, new ErrorLogWriter(Path.Combine(_dir, "logs"))); // never %LOCALAPPDATA%
        var batch = new BulkTrimEngine(
            split, new KeptMiddleRequestBuilder(probe), new FakeDiskSpaceProbe(long.MaxValue), new SmartCutEngine(runner, probe), null);
        var dest = Path.Combine(_dir, "camera_trimmed.mkv");
        var item = new BulkTrimItem(src, S(3.0), null, dest); // mid-GOP on the 2 s grid

        var result = await batch.RunAsync(new[] { item }, new BulkTrimOptions(Precision: CutPrecision.Exact));

        var row = result.Items.Single();
        row.Outcome.Should().Be(ItemOutcome.Done, row.Error?.Message);
        row.Warnings.Should().Contain(w => w.StartsWith(BulkTrimEngine.ExactFallbackPrefix, StringComparison.Ordinal) && w.Contains(MatroskaReason, StringComparison.Ordinal));
        File.Exists(row.OutputPath).Should().BeTrue();
        var (errors, lines) = await SmartCutJointIntegrity.DecodeAsync(row.OutputPath!);
        _output.WriteLine($"Matroska Exact row, fell back: output decodes with {errors} decoder/demuxer error line(s), {lines.Count} line(s) in all");
        errors.Should().Be(0, "the lossless cut is the source's own packets from a keyframe on");
    }
}
