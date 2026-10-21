using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-223 (G-059, SPEC-001 I65 / I70) - an Exact cut on an open-GOP H.264 video no longer writes a file that breaks after the joint.
///
/// <para><b>The defect.</b> When the keyframe the head ends on (<c>HeadEnd</c>) is a NON-IDR I-frame (x264 <c>open-gop=1</c> makes
/// every keyframe but the first one), the copied part is not self-contained: its frames refer to the pictures of the GOP before
/// it, and <c>h264_mp4toannexb</c> puts SPS/PPS only before IDR slices, so the tail decodes against the head's parameter sets. The
/// engine reported Done on a file that printed decoder errors and lost the frames at the joint (measured on the bundled ffmpeg
/// n7.1.5, 320x240, 25 fps, a cut at 3.4 s: 2 decoder errors and 9 frames lost at the joint; with <c>cabac=0:8x8dct=0:ref=1 -bf 2</c>
/// 663 errors and 63 of 200 frames decoded).</para>
///
/// <para><b>The fix under test.</b> The engine asks whether the keyframe at <c>HeadEnd</c> is an IDR; if it is not (or the probe
/// cannot say) the row falls back to the lossless cut with a stated reason and runs no ffmpeg. A closed-GOP source (x264's default:
/// every keyframe is an IDR) is unchanged. The first two tests use only what the shipped code already has (the engine, the real
/// probe and the joint check of T-189), so this file runs against the code before the fix, where the repro fails.</para>
/// </summary>
public sealed class SmartCutOpenGopIntegrationTests : IDisposable
{
    /// <summary>Only the first keyframe is an IDR; the keyframes every 2 s are non-IDR I-frames flagged K.</summary>
    private const string OpenGopParams = "open-gop=1:keyint=50:min-keyint=50:scenecut=0";

    /// <summary>The same with the settings that made the shipped joint decode wrongly for most of the file (663 errors).</summary>
    private const string OpenGopHardParams = "open-gop=1:keyint=50:min-keyint=50:scenecut=0:cabac=0:8x8dct=0:ref=1";

    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SmartCutOpenGopIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t223-" + Guid.NewGuid().ToString("N"));
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

    private Task<string> MakeAsync(string name, string x264Params, params string[] more) =>
        SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, name, 12, 25, new[] { "-c:v", "libx264", "-x264-params", x264Params }.Concat(more).ToArray());

    /// <summary>One Exact cut through the real engine; returns the result, the recording runner and the plan it was made from.</summary>
    private async Task<(SmartCutResult Result, SmartCutPlan Plan, TimeSpan Start, string Dest, SmartCutJointTiming.RecordingRealRunner Runner)>
        CutAsync(string src, double start, string destName = "cut.mp4")
    {
        var keyframes = await Probe().GetKeyframesAsync(src);
        var plan = SmartCutPlanner.Plan(S(start), null, keyframes);
        var dest = Path.Combine(_dir, "out", destName);
        var runner = new SmartCutJointTiming.RecordingRealRunner(
            new FfmpegRunner(Locator()), Path.Combine(_dir, "kept-" + Guid.NewGuid().ToString("N")));
        var result = await new SmartCutEngine(runner, Probe()).CutAsync(src, S(start), null, dest);
        return (result, plan, S(start), dest, runner);
    }

    // serves-spec: SPEC-001#I65 / SPEC-001#I70 - the repro, written first. An Exact cut at 3.4 s of an open-GOP H.264 file (the
    // keyframe the head ends on, 4 s, is a non-IDR I-frame) either falls back, writing nothing, or writes a final that passes the
    // joint check (0 decoder errors, the frames from the joint md5-identical, equal tail counts). On the code before the fix it
    // wrote a final with decoder errors and lost frames, and reported Done.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(OpenGopParams)]
    [InlineData(OpenGopHardParams)]
    public async Task AnOpenGopCut_NeverWritesAFileThatBreaksAfterTheJoint(string x264Params)
    {
        RequireBinaries();
        var src = await MakeAsync("open.mp4", x264Params, "-bf", "2");

        var (result, plan, start, dest, _) = await CutAsync(src, 3.4);

        plan.Strategy.Should().Be(SmartCutStrategy.HeadReencode, "3.4 s is inside the GOP that ends at the 4 s keyframe: a head and a joint");
        if (result.FellBack)
        {
            _output.WriteLine($"fell back: {result.FallbackReason}");
            result.OutputPath.Should().BeNull();
            File.Exists(dest).Should().BeFalse("a cut that fell back writes nothing");
            return;
        }

        File.Exists(dest).Should().BeTrue();
        var report = await SmartCutJointIntegrity.CheckAsync(dest, src, start, plan.HeadEnd!.Value, Path.Combine(_dir, "check"));
        _output.WriteLine(report.Describe());
        report.ErrorLines.ToList().ForEach(_output.WriteLine);
        report.Passes.Should().BeTrue("a result the engine reports Done is the source's own frames from the joint on: " + report.Describe());
    }

    // serves-spec: SPEC-001#I45 - the control that must not change: x264's default is a closed GOP (every keyframe an IDR), and
    // the same cut is frame-exact and passes the joint check.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AClosedGopCut_StillGetsAFrameExactResult_ThatPassesTheJointCheck()
    {
        RequireBinaries();
        var src = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "closed.mp4", 12, 25, "-c:v", "libx264", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-bf", "2");

        var (result, plan, start, dest, _) = await CutAsync(src, 3.4);

        result.FellBack.Should().BeFalse("every keyframe of a closed-GOP file is an IDR: " + result.FallbackReason);
        var report = await SmartCutJointIntegrity.CheckAsync(dest, src, start, plan.HeadEnd!.Value, Path.Combine(_dir, "check"));
        _output.WriteLine(report.Describe());
        report.Passes.Should().BeTrue(report.Describe());
    }

    // serves-spec: SPEC-001#I70 - the fix under test on real media: an open-GOP cut falls back with the keyframe-type reason, runs NO
    // ffmpeg (the question is one ffprobe read) and leaves no temp dir; through the lossless path the file the user gets decodes clean.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnOpenGopCut_FallsBackWithTheReason_RunsNoFfmpeg_AndLeavesNothingBehind()
    {
        RequireBinaries();
        var src = await MakeAsync("open.mp4", OpenGopParams, "-bf", "2");

        var (result, _, _, dest, runner) = await CutAsync(src, 3.4);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(SmartCutEngine.NotAnIdrKeyframeReason);
        result.FallbackReason.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
        runner.Commands.Should().BeEmpty("no head is encoded and nothing is joined for a keyframe that is not an IDR");
        File.Exists(dest).Should().BeFalse();
        var outDir = Path.GetDirectoryName(dest)!;
        (Directory.Exists(outDir) ? Directory.GetDirectories(outDir, ".vsj-smartcut-*") : Array.Empty<string>())
            .Should().BeEmpty("the question comes before the temp dir exists");
    }

    // serves-spec: SPEC-001#I70 - the probe on real packets: the first keyframe of an open-GOP file is an IDR, the later ones are not;
    // every keyframe of a closed-GOP file is; a time that is not a keyframe, and a missing file, have no answer.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task TheProbe_TellsAnIdrKeyframeFromAnOpenGopOne_AndAnswersNullWhenItCannotSay()
    {
        RequireBinaries();
        var open = await MakeAsync("open.mp4", OpenGopParams, "-bf", "2");
        var closed = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "closed.mp4", 12, 25, "-c:v", "libx264", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-bf", "2");
        var probe = Probe();

        (await probe.IsIdrKeyframeAsync(open, S(0))).Should().BeTrue("the first picture of the file is an IDR");
        (await probe.IsIdrKeyframeAsync(open, S(2))).Should().BeFalse("x264 open-gop=1 makes every later keyframe a non-IDR I-frame");
        (await probe.IsIdrKeyframeAsync(open, S(4))).Should().BeFalse();
        (await probe.IsIdrKeyframeAsync(closed, S(0))).Should().BeTrue();
        (await probe.IsIdrKeyframeAsync(closed, S(2))).Should().BeTrue();
        (await probe.IsIdrKeyframeAsync(closed, S(8))).Should().BeTrue();

        (await probe.IsIdrKeyframeAsync(closed, S(3))).Should().BeNull("3 s is no keyframe: the packet the seek lands on is the one at 2 s");
        (await probe.IsIdrKeyframeAsync(Path.Combine(_dir, "missing.mp4"), S(2))).Should().BeNull();
        (await probe.IsIdrKeyframeAsync(closed, TimeSpan.FromSeconds(-1))).Should().BeNull();
    }

    // serves-spec: SPEC-001#I70 - decided per HeadEnd, not per file: an open-GOP file whose keyframe at 4 s is a forced IDR. A cut
    // that ends its head at 4 s is frame-exact and passes the joint check; a cut that ends its head at the non-IDR keyframe at 2 s
    // falls back.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AFileThatMixesIdrAndNonIdrKeyframes_IsDecidedAtTheKeyframeTheHeadEndsOn()
    {
        RequireBinaries();
        var src = await MakeAsync("mixed.mp4", OpenGopParams, "-bf", "2", "-force_key_frames", "4", "-forced-idr", "1");
        var probe = Probe();
        (await probe.IsIdrKeyframeAsync(src, S(4))).Should().BeTrue("a forced keyframe is an IDR");
        (await probe.IsIdrKeyframeAsync(src, S(2))).Should().BeFalse();

        var (idr, idrPlan, idrStart, idrDest, _) = await CutAsync(src, 3.4, "to-the-idr.mp4");
        idr.FellBack.Should().BeFalse("the head ends on the IDR at 4 s: " + idr.FallbackReason);
        var report = await SmartCutJointIntegrity.CheckAsync(idrDest, src, idrStart, idrPlan.HeadEnd!.Value, Path.Combine(_dir, "check"));
        _output.WriteLine(report.Describe());
        report.Passes.Should().BeTrue(report.Describe());

        var (open, openPlan, _, openDest, _) = await CutAsync(src, 1.4, "to-the-open.mp4");
        openPlan.HeadEnd.Should().Be(S(2));
        open.FellBack.Should().BeTrue("the head ends on the non-IDR keyframe at 2 s");
        open.FallbackReason.Should().Be(SmartCutEngine.NotAnIdrKeyframeReason);
        File.Exists(openDest).Should().BeFalse();
    }
}
