using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-189 — is an Exact result correct after its joint? <see cref="SmartCutJointIntegrity"/> decodes the whole final and
/// compares its frames from the joint on with the source's (SPEC-001 I65).
///
/// <para><b>The repro.</b> An HEVC source whose parameter sets differ from the libx265 head's defaults: on the shipped
/// engine the final decoded with errors and every frame after the joint differed from the source — the concat kept the
/// head's hev1 configuration and the copied tail's slices decoded against it. Since T-189 HEVC is not in the encoder
/// map, so the engine falls back (no output) and the caller runs the lossless cut.</para>
///
/// <para><b>The baseline.</b> H.264 passes the check on the shipped engine (the concat demuxer's automatic
/// <c>h264_mp4toannexb</c> puts the source's SPS/PPS in-band before the tail's IDR), on a 1 s GOP and on a 4 s GOP. T-190
/// to T-192 must keep these green. Both are closed-GOP (libx264's default), so every keyframe is an IDR: open-GOP H.264,
/// whose boundary keyframe can be a non-IDR I-frame, is known to fail the check (SPEC-001 I65) and is not a baseline.</para>
///
/// <para>Synthetic fixtures only, generated in this test's temp folder through <see cref="FfmpegRunner"/>.</para>
/// </summary>
public sealed class SmartCutJointIntegrityTests : IDisposable
{
    /// <summary>
    /// The HEVC source recipe (T-189). <c>ctu=32</c> is what makes the source's parameter sets incompatible with the
    /// libx265 head's defaults (64x64 CTUs): with <c>ref=1:bframes=0</c> alone the joint happened to decode cleanly
    /// (0 error lines, identical frames), so that recipe could not show the defect. Measured on the bundled ffmpeg
    /// n7.1.5: the shipped engine's final printed 3 <c>[hevc @ …] The cu_qp_delta … is outside the valid range</c>
    /// lines and all 240 frames after the joint differed from the source.
    /// </summary>
    internal const string HevcX265Params = "ref=1:bframes=0:keyint=60:min-keyint=60:scenecut=0:ctu=32:log-level=error";

    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SmartCutJointIntegrityTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-joint-" + Guid.NewGuid().ToString("N"));
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

    private static FfmpegRunner Runner() => new(Locator());

    private static MediaProbe Probe() => new(new FfprobeRunner(Locator()));

    private static SmartCutEngine Engine() => new(Runner(), Probe());

    /// <summary>Generate a synthetic test-pattern clip with a sine track. Fixture construction only — never under test.</summary>
    internal static async Task<string> MakeFixtureAsync(string dir, string name, double seconds, int fps, params string[] videoArgs)
    {
        var path = Path.Combine(dir, name);
        var args = FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error")
            .Raw("-f", "lavfi", "-i", $"testsrc=size=320x240:rate={fps}:duration={seconds}")
            .Raw("-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}")
            .Raw(videoArgs)
            .Raw("-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest")
            .Output(path);

        var run = await Runner().RunAsync(args);
        run.ExitCode.Should().Be(0, "the fixture must be produced before anything is asserted: " + run.StdErrText);
        File.Exists(path).Should().BeTrue();
        return path;
    }

    /// <summary>A 2 s GOP HEVC (hvc1) + AAC clip with <see cref="HevcX265Params"/>.</summary>
    internal static Task<string> MakeHevcFixtureAsync(string dir, string name = "hevc.mp4") =>
        MakeFixtureAsync(dir, name, 12, 30, "-c:v", "libx265", "-tag:v", "hvc1", "-x265-params", HevcX265Params);

    /// <summary>True when the bundled ffmpeg has the encoder (a libx265-less build skips the HEVC tests, never passes them).</summary>
    internal static async Task<bool> HasEncoderAsync(string encoder)
    {
        // `-encoders` and `-h encoder=` print to stdout, which the runner discards; encoding one frame fails when absent.
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-v", "error", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=1:duration=1")
            .Raw("-frames:v", "1", "-c:v", encoder, "-f", "null")
            .Output("-"));
        return run.ExitCode == 0;
    }

    private async Task<(SmartCutResult Result, SmartCutPlan Plan)> ExactCutAsync(string src, TimeSpan start, string dest)
    {
        var keyframes = await Probe().GetKeyframesAsync(src);
        var plan = SmartCutPlanner.Plan(start, null, keyframes);
        var result = await Engine().CutAsync(src, start, null, dest);
        return (result, plan);
    }

    // ---- The line filter (unit) ------------------------------------------------------------------------

    // serves-spec: SPEC-001#I65 — the null muxer's timing lines are T-190's; decoder and demuxer lines count.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheNullMuxersTimingLines_AreNotDecoderErrors_DecoderAndDemuxerLinesAre()
    {
        SmartCutJointIntegrity.IsDecoderOrDemuxerError(
                "[null @ 000001d2c5e4f8c0] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 1536 >= 1536")
            .Should().BeFalse("a muxer timestamp line is a timing symptom T-190 owns, not a decode error");
        SmartCutJointIntegrity.IsDecoderOrDemuxerError(
                "[out#0/null @ 000001d2c5e4f8c0] Application provided invalid, non monotonically increasing dts to muxer")
            .Should().BeFalse("ffmpeg 7 can tag the same muxer line with the output's own context");

        SmartCutJointIntegrity.IsDecoderOrDemuxerError(
                "[hevc @ 000002c0c4720c00] The cu_qp_delta -44 is outside the valid range [-26, 25].")
            .Should().BeTrue("the T-189 HEVC symptom is a decoder error");
        SmartCutJointIntegrity.IsDecoderOrDemuxerError("[h264 @ 0000020a] error while decoding MB 3 7, bytestream -5")
            .Should().BeTrue();
        SmartCutJointIntegrity.IsDecoderOrDemuxerError(
                "[mov,mp4,m4a,3gp,3g2,mj2 @ 0000020a] stream 0, offset 0x30: partial file")
            .Should().BeTrue("a demuxer error line counts too");
        SmartCutJointIntegrity.IsDecoderOrDemuxerError(
                "[vist#0:0/hevc @ 0000020a] [dec:hevc @ 0000020b] Error submitting packet to decoder: Invalid data found")
            .Should().BeTrue("ffmpeg 7's decoder-thread wrapper is still a decoder line");
        SmartCutJointIntegrity.IsDecoderOrDemuxerError("   ").Should().BeFalse();
        SmartCutJointIntegrity.IsNullMuxerLine("[null @ 0x1] x").Should().BeTrue();
        SmartCutJointIntegrity.IsNullMuxerLine("[h264 @ 0x1] x").Should().BeFalse();
    }

    // serves-spec: SPEC-001#I65 — the frames from the joint on are the source's "over the same number of frames": a final
    // whose tail is shorter (or longer) than the source's fails even when every frame it has matches.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AReportWhoseTailsDifferInLength_DoesNotPass()
    {
        var truncated = new SmartCutJointIntegrity.Report(
            DecoderErrorLines: 0, AllErrorLines: 0, NullMuxerLines: 0, FinalTailFrames: 34, SourceTailFrames: 200,
            ComparedFrames: 34, MismatchedFrames: 0, FirstMismatch: -1, ErrorLines: Array.Empty<string>());
        var padded = truncated with { FinalTailFrames = 201, ComparedFrames = 200 };
        var whole = truncated with { FinalTailFrames = 200, ComparedFrames = 200 };

        truncated.Passes.Should().BeFalse("a final that lost the end of its tail is not the source's frames from the joint on");
        truncated.Describe().Should().Contain("the tails differ in length");
        padded.Passes.Should().BeFalse("a final with frames the source does not have fails too");
        whole.Passes.Should().BeTrue();
        whole.Describe().Should().NotContain("differ in length");
    }

    // ---- The check itself, on finals made without the engine ------------------------------------------------

    // serves-spec: SPEC-001#I65 — a final that lost the end of its tail fails the check (it used to pass: only the
    // shorter tail was compared). The "final" is the source's own first 6 s, stream-copied — every frame it has is the
    // source's, so only the length rule can catch it.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task TheCheck_FailsAFinalThatLostTheEndOfItsTail()
    {
        RequireBinaries();
        var src = await MakeFixtureAsync(
            _dir, "h264-nob.mp4", 12, 25, "-c:v", "libx264", "-bf", "0", "-g", "25", "-keyint_min", "25", "-sc_threshold", "0");
        var truncated = Path.Combine(_dir, "h264-nob-first6s.mp4");
        var cut = await Runner().RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error").Input(src).Raw("-t", "6", "-map", "0", "-c", "copy").Output(truncated));
        cut.ExitCode.Should().Be(0, "the truncated fixture must be produced first: " + cut.StdErrText);
        var keyframes = await Probe().GetKeyframesAsync(src);
        var origin = keyframes[0];

        var report = await SmartCutJointIntegrity.CheckAsync(truncated, src, origin, origin + TimeSpan.FromSeconds(2), _dir);
        _output.WriteLine("the source's first 6 s against the whole source: " + report.Describe());

        report.DecoderErrorLines.Should().Be(0, "precondition: the truncated copy decodes cleanly");
        report.MismatchedFrames.Should().Be(0, "precondition: every frame it has is the source's");
        report.FinalTailFrames.Should().BeLessThan(report.SourceTailFrames);
        report.Passes.Should().BeFalse("the final lost the end of its tail");
    }

    // serves-spec: SPEC-001#I65 — the source's tail is selected on its own timestamps, the probe's time base, also when
    // its start_time is not 0 (MPEG-TS, AVCHD — and this mp4 written 1.3 s late). The "final" is the source itself, with
    // Start at its first frame, so a correct check must pass it; before -copyts the source tail was taken 1.3 s late and
    // every compared frame differed.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task TheCheck_SelectsTheSourceTailOnItsOwnTimestamps_WhenItsStartTimeIsNotZero()
    {
        RequireBinaries();
        var src = await MakeFixtureAsync(
            _dir, "h264-late.mp4", 12, 25, "-c:v", "libx264", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0",
            "-output_ts_offset", "1.3");
        var keyframes = await Probe().GetKeyframesAsync(src);
        keyframes.Count.Should().BeGreaterThan(2);
        keyframes[0].TotalSeconds.Should().BeApproximately(1.3, 0.05, "precondition: the probe's times carry the 1.3 s offset");

        var report = await SmartCutJointIntegrity.CheckAsync(src, src, keyframes[0], keyframes[2], _dir);
        _output.WriteLine("a start_time 1.3 s source against itself, joint at " + keyframes[2] + ": " + report.Describe());
        report.ErrorLines.ToList().ForEach(_output.WriteLine);

        report.TailLengthsMatch.Should().BeTrue();
        report.MismatchedFrames.Should().Be(0);
        report.Passes.Should().BeTrue("the source's frames from HeadEnd are its frames from HeadEnd, whatever its start_time");
    }

    // ---- The H.264 baseline (shipped engine) -------------------------------------------------------------

    // serves-spec: SPEC-001#I65 — an H.264 Exact result decodes with zero decoder and demuxer errors and its frames from
    // the joint on are the source's, in order. 1 s GOP, start mid-GOP.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnH264ExactResult_PassesTheJointCheck_OnA1sGop()
    {
        RequireBinaries();
        var src = await MakeFixtureAsync(
            _dir, "h264-g1.mp4", 12, 25, "-c:v", "libx264", "-g", "25", "-keyint_min", "25", "-sc_threshold", "0");
        var dest = Path.Combine(_dir, "h264-g1-exact.mp4");

        var (result, plan) = await ExactCutAsync(src, TimeSpan.FromSeconds(3.4), dest);

        result.FellBack.Should().BeFalse($"H.264/AAC is exact-cuttable (reason: {result.FallbackReason})");
        plan.Strategy.Should().Be(SmartCutStrategy.HeadReencode);
        var report = await SmartCutJointIntegrity.CheckAsync(dest, src, plan.Start, plan.HeadEnd!.Value, _dir);
        _output.WriteLine("H.264, 1 s GOP, start 3.4 s: " + report.Describe());
        report.ErrorLines.ToList().ForEach(_output.WriteLine);

        report.DecoderErrorLines.Should().Be(0, "an H.264 joint decodes cleanly");
        report.ComparedFrames.Should().BeGreaterThan(0);
        report.FinalTailFrames.Should().Be(report.SourceTailFrames, "an open-ended cut keeps every source frame from HeadEnd");
        report.MismatchedFrames.Should().Be(0, "the copied frames are the source's own");
        report.Passes.Should().BeTrue();
    }

    // serves-spec: SPEC-001#I65 — the same on a longer (4 s) GOP, start mid-GOP.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnH264ExactResult_PassesTheJointCheck_OnA4sGop()
    {
        RequireBinaries();
        var src = await MakeFixtureAsync(
            _dir, "h264-g4.mp4", 20, 25, "-c:v", "libx264", "-g", "100", "-keyint_min", "100", "-sc_threshold", "0");
        var dest = Path.Combine(_dir, "h264-g4-exact.mp4");

        var (result, plan) = await ExactCutAsync(src, TimeSpan.FromSeconds(5), dest);

        result.FellBack.Should().BeFalse($"H.264/AAC is exact-cuttable (reason: {result.FallbackReason})");
        plan.HeadEnd.Should().Be(TimeSpan.FromSeconds(8), "5 s on a 4 s grid re-encodes up to the 8 s keyframe");
        var report = await SmartCutJointIntegrity.CheckAsync(dest, src, plan.Start, plan.HeadEnd!.Value, _dir);
        _output.WriteLine("H.264, 4 s GOP, start 5 s: " + report.Describe());
        report.ErrorLines.ToList().ForEach(_output.WriteLine);

        report.DecoderErrorLines.Should().Be(0);
        report.FinalTailFrames.Should().Be(report.SourceTailFrames, "an open-ended cut keeps every source frame from HeadEnd");
        report.MismatchedFrames.Should().Be(0);
        report.Passes.Should().BeTrue();
    }

    // ---- The HEVC repro -----------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I46 / SPEC-001#I65 — an HEVC Exact cut never writes a final that fails the joint check: it
    // either falls back with no output, or (after a future repair, T-201) passes. On the shipped engine it wrote a final
    // with decoder errors and every frame after the joint wrong — this test failed there.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnHevcExactCut_EitherFallsBackWithNoOutput_OrPassesTheJointCheck()
    {
        RequireBinaries();
        Skip.IfNot(await HasEncoderAsync("libx265"), "the bundled ffmpeg has no libx265, so no HEVC fixture can be made");

        var src = await MakeHevcFixtureAsync(_dir);
        var dest = Path.Combine(_dir, "hevc-exact.mp4");

        var (result, plan) = await ExactCutAsync(src, TimeSpan.FromSeconds(3.5), dest);
        plan.Strategy.Should().Be(SmartCutStrategy.HeadReencode, "precondition: 3.5 s is mid-GOP on a 2 s grid");

        if (result.FellBack)
        {
            _output.WriteLine("HEVC Exact fell back: " + result.FallbackReason);
            File.Exists(dest).Should().BeFalse("a fall-back writes no output; the caller runs the lossless cut");
            result.FallbackReason.Should().Contain("HEVC");
            return;
        }

        var report = await SmartCutJointIntegrity.CheckAsync(dest, src, plan.Start, plan.HeadEnd!.Value, _dir);
        _output.WriteLine("HEVC Exact wrote a final: " + report.Describe());
        report.ErrorLines.Take(20).ToList().ForEach(_output.WriteLine);
        report.Passes.Should().BeTrue(
            "an HEVC Exact result must never be corrupt after its joint (SPEC-001 I46): " + report.Describe());
    }
}
