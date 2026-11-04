using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Io;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-224 (G-059, SPEC-001 I40 / I71) - an Exact cut that trims only the end keeps the end you set.
///
/// <para><b>The defect.</b> <c>SmartCutPlanner.Plan</c> returns <c>PureCopy</c> whenever the START is on a keyframe (or at 0) and
/// ignores the END; the engine then handed the row to the lossless cut, which snaps a mid-GOP outro to a keyframe, with no
/// warning - while the Bulk Cut row showed and judged the outro as requested. Measured on a <c>g=50</c>, 25 fps source: an
/// outro-only cut (intro 0, outro 7.1 s) came out 8.10 s long, the lossless cut's length.</para>
///
/// <para><b>The fix under test.</b> A <c>PureCopy</c> plan with a mid-GOP end is the Exact joint without its head: one concat of
/// the source itself, <c>inpoint</c> the keyframe the cut starts on, <c>outpoint</c> the requested end - a stream copy, no
/// encoder. The first two tests use only what the shipped code already has (the Bulk engine with the real Exact engine and the
/// joint check), so this file runs against the code before the fix, where the repro fails with the snapped length.</para>
/// </summary>
public sealed class SmartCutEndTrimIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SmartCutEndTrimIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t224-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>The ticket's source: 12 s, 25 fps, a closed GOP of 50 frames (a keyframe every 2 s), with B-frames (has_b_frames 2).</summary>
    private Task<string> MakeSourceAsync(string name = "g50.mp4") =>
        SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, name, 12, 25, "-c:v", "libx264", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-bf", "2");

    /// <summary>One Exact row through the Bulk engine, with the real Exact engine and the real lossless engine behind it.</summary>
    private async Task<(BulkTrimItemResult Row, string Dest)> RunExactRowAsync(string src, double intro, double? outro, string? destName = null)
    {
        var dest = Path.Combine(_dir, "trimmed", destName ?? "g50_trimmed" + Path.GetExtension(src));
        var runner = new FfmpegRunner(Locator());
        var probe = Probe();
        var logs = new ErrorLogWriter(Path.Combine(_dir, "logs")); // never %LOCALAPPDATA%
        var split = new SplitEngine(runner, probe, logs, new FakeDiskSpaceProbe(long.MaxValue));
        var batch = new BulkTrimEngine(
            split, new KeptMiddleRequestBuilder(probe), new FakeDiskSpaceProbe(long.MaxValue), new SmartCutEngine(runner, probe), null);

        var result = await batch.RunAsync(
            new[] { new BulkTrimItem(src, S(intro), outro is { } o ? S(o) : null, dest) },
            new BulkTrimOptions(Precision: CutPrecision.Exact));
        return (result.Items.Single(), dest);
    }

    // serves-spec: SPEC-001#I71 / SPEC-011#I158 - the repro, written first. An Exact row that trims only the end (intro 0), or an
    // intro on a keyframe and a mid-GOP outro, ends where the outro says: the video is End - Start long (within a frame short, and
    // the source's has_b_frames long: the concat's outpoint is a decoding time, T-220), decodes with no error, and is the source's own
    // frames in order from the keyframe. On the code before the fix the row fell back to the lossless cut and came out 8.10 s long for
    // an outro of 7.1 s, with only an unrelated bounds note.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(0.0, 7.1)]
    [InlineData(4.0, 9.1)]
    [InlineData(2.0, 11.3)]
    public async Task AnExactRow_ThatTrimsTheEnd_KeepsTheEndYouSet(double intro, double outro)
    {
        RequireBinaries();
        var src = await MakeSourceAsync();

        var (row, dest) = await RunExactRowAsync(src, intro, outro);

        row.Outcome.Should().Be(ItemOutcome.Done, row.Error?.Message);
        row.Warnings.Should().BeEmpty("the cut is exact, so there is nothing to warn about");
        File.Exists(dest).Should().BeTrue();

        var interval = await SmartCutJointTiming.FrameIntervalAsync(dest);
        var hasB = await SmartCutJointTiming.HasBFramesAsync(src);
        var frames = await SmartCutJointTiming.FrameCountAsync(dest);
        var span = frames * interval;
        var want = outro - intro;
        _output.WriteLine($"intro {intro} s, outro {outro} s: {frames} frames = {span:0.000} s (wanted {want:0.000} s, has_b_frames {hasB})");
        span.Should().BeInRange(
            want - interval - 1e-4, want + ((hasB + 1) * interval) + 1e-4,
            "End - Start, within a frame short and the end overrun of up to has_b_frames frames long (T-220); the lossless cut it replaces was 1-2 s too long");

        // The oracle counts the source's frames before End minus half a frame, so a request that falls between two frames (7.1 s, the
        // frames at 7.08 and 7.12 s) leaves the frame it falls in out of the source's tail: the allowance is has_b_frames + 1 here.
        // Sound and picture stay together. The copied audio begins at the packet the demuxer's seek lands on, up to the source's
        // reorder delay (has_b_frames frames, plus the AAC frame it falls in) BEFORE the keyframe's picture - the same shift for both
        // streams, so they stay in sync - and never after it; it ends where the picture does.
        var videoStart = await SmartCutJointTiming.StreamStartAsync(dest, "v:0");
        var audioStart = await SmartCutJointTiming.StreamStartAsync(dest, "a:0");
        var audioDuration = await SmartCutJointTiming.StreamDurationAsync(dest, "a:0");
        _output.WriteLine($"video starts {videoStart:0.000000} s, audio starts {audioStart:0.000000} s, audio lasts {audioDuration:0.000} s");
        (audioStart - videoStart).Should().BeLessThanOrEqualTo(0.001, "the sound never starts after the picture");
        (videoStart - audioStart).Should().BeLessThan((hasB + 2) * interval, "the picture trails the sound by the reorder delay at most");
        audioDuration.Should().BeInRange(want - 0.05, want + ((hasB + 1) * interval) + 0.05, "the audio ends where the picture does");

        var report = await SmartCutJointIntegrity.CheckAsync(dest, src, S(intro), S(intro), _dir, default, S(outro), hasB + 1);
        _output.WriteLine(report.Describe());
        report.ErrorLines.ToList().ForEach(_output.WriteLine);
        report.Passes.Should().BeTrue("0 decode errors, and the source's own frames from the keyframe on, in order: " + report.Describe());
    }

    // serves-spec: SPEC-001#I71 - the same cut through the engine itself: one ffmpeg run, a concat of the SOURCE ITSELF (inpoint the
    // keyframe, outpoint the end) with no encoder, no head and no tail file; nothing is left in the output folder.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task TheEndTrim_IsOneStreamCopyConcat_OfTheSourceItself()
    {
        RequireBinaries();
        var src = await MakeSourceAsync();
        var dest = Path.Combine(_dir, "out", "cut.mp4");
        var runner = new SmartCutJointTiming.RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept"));

        var result = await new SmartCutEngine(runner, Probe()).CutAsync(src, S(4), S(9.1), dest);

        result.FellBack.Should().BeFalse(result.FallbackReason);
        result.OutputPath.Should().Be(dest);
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy, "the start is on a keyframe: nothing is re-encoded");
        result.ReencodedDuration.Should().Be(TimeSpan.Zero);
        result.EffectiveStart.Should().Be(S(4));
        runner.Commands.Should().ContainSingle("one concat, no head encode");
        JoinArgsBuilder.SatisfiesCopyInvariant(runner.Commands.Single()).Should().BeTrue("a stream copy: no encoder token");
        runner.ConcatLists.Should().ContainSingle().Which.Should().Be(
            "file '" + Path.GetFullPath(src) + "'\ninpoint 5\noutpoint 9.1\n",
            "the source itself, from the middle of its keyframe's GOP (the 4-6 s one, T-237) to the requested end");
        File.Exists(dest).Should().BeTrue();
        Directory.GetDirectories(Path.GetDirectoryName(dest)!, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept");
    }

    // serves-spec: SPEC-001#I71 - without B-frames the outpoint is the end: the file holds exactly the source's frames of [4 s, 9.1 s)
    // (128 at 25 fps: 4.00 .. 9.08 s), none past the end. Pins the exactness the has_b_frames allowance of the first test hides.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnEndTrimOnASourceWithoutBFrames_HoldsExactlyTheFramesOfTheRange()
    {
        RequireBinaries();
        var src = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "bf0.mp4", 12, 25, "-c:v", "libx264", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-bf", "0");
        (await SmartCutJointTiming.HasBFramesAsync(src)).Should().Be(0, "precondition: the fixture has no B-frames");

        var (row, dest) = await RunExactRowAsync(src, 4.0, 9.1);

        row.Outcome.Should().Be(ItemOutcome.Done, row.Error?.Message);
        row.Warnings.Should().BeEmpty();
        (await SmartCutJointTiming.FrameCountAsync(dest)).Should().Be(128, "the frames at 4.00 .. 9.08 s, and not the one at 9.12 s");
        var report = await SmartCutJointIntegrity.CheckAsync(dest, src, S(4), S(4), _dir, default, S(9.1), allowedOverrunFrames: 1);
        _output.WriteLine(report.Describe());
        report.Passes.Should().BeTrue(report.Describe());
    }

    /// <summary>The ticket's source with one more stream: a cover picture (an MP4), or a timecode data track (a MOV).</summary>
    private async Task<string> MakeSourceWithExtraStreamAsync(string kind)
    {
        var mp4 = await MakeSourceAsync("plain-" + kind + ".mp4");
        var runner = new FfmpegRunner(Locator());
        string path;
        FfmpegArgs args;
        if (kind == "cover")
        {
            var jpg = Path.Combine(_dir, "cover.jpg");
            var made = await runner.RunAsync(FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error")
                .Raw("-f", "lavfi", "-i", "testsrc=size=160x120:rate=1:duration=1").Raw("-frames:v", "1").Output(jpg));
            made.ExitCode.Should().Be(0, made.StdErrText);
            path = Path.Combine(_dir, "cover.mp4");
            args = FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error").Input(mp4).Input(jpg)
                .Raw("-map", "0", "-map", "1", "-c", "copy", "-disposition:v:1", "attached_pic").Output(path);
        }
        else
        {
            path = Path.Combine(_dir, "timecode.mov");
            args = FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error").Input(mp4)
                .Raw("-map", "0", "-c", "copy", "-timecode", "01:00:00:00").Output(path);
        }

        var run = await runner.RunAsync(args);
        run.ExitCode.Should().Be(0, run.StdErrText);
        return path;
    }

    // serves-spec: SPEC-001#I71 / SPEC-002#I51 - a source with a cover picture (the concat demuxer gave the file a 4 s empty start:
    // video at 4.0 s, format duration 9.2 s for 5.1 s of content) or a timecode data track (the concat fails: "Cannot map stream
    // #0:2 - unsupported type") keeps the lossless cut, which carries every stream, and the row says the end snaps. Both are found
    // by the review of the first version, which mapped every stream through the concat.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("cover")]
    [InlineData("timecode")]
    public async Task AnEndTrimOnASourceWithACoverPictureOrADataTrack_KeepsTheLosslessCut_AndTheRowSaysSo(string kind)
    {
        RequireBinaries();
        var src = await MakeSourceWithExtraStreamAsync(kind);
        (await Probe().ProbeAsync(src) is ProbeResult.ProbeSucceeded ok).Should().BeTrue();
        ((ProbeResult.ProbeSucceeded)await Probe().ProbeAsync(src)).Info.ExtraStreams.Should().BeGreaterThan(0, "precondition: the probe sees the extra stream");

        var runner = new SmartCutJointTiming.RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept"));
        var result = await new SmartCutEngine(runner, Probe()).CutAsync(src, S(4), S(9.1), Path.Combine(_dir, "out", "cut" + Path.GetExtension(src)));
        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(SmartCutArgsBuilder.ExtraStreamsReason);
        result.EndSnapped.Should().BeTrue();
        runner.Commands.Should().BeEmpty();

        var (row, dest) = await RunExactRowAsync(src, 4.0, 9.1, "extra_trimmed" + Path.GetExtension(src));
        row.Outcome.Should().Be(ItemOutcome.Done, row.Error?.Message);
        row.Warnings.Should().Contain(w =>
            w.StartsWith(BulkTrimEngine.ExactFallbackPrefix) && w.Contains(SmartCutArgsBuilder.ExtraStreamsReason));
        File.Exists(dest).Should().BeTrue("the lossless cut produced the file");
    }

    // serves-spec: SPEC-001#I71 - the boundary: an end that is itself on a keyframe is where the lossless cut ends, exactly, so the
    // engine hands it back with no run (the same answer as before the fix); and so is a cut with no end at all.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(0.0, 8.0)]
    [InlineData(4.0, 10.0)]
    [InlineData(4.0, double.NaN)]
    public async Task AnEndOnAKeyframe_OrNoEnd_IsStillTheLosslessCut(double intro, double outro)
    {
        RequireBinaries();
        var src = await MakeSourceAsync();
        var dest = Path.Combine(_dir, "out", "cut.mp4");
        var runner = new SmartCutJointTiming.RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept"));

        var result = await new SmartCutEngine(runner, Probe()).CutAsync(src, S(intro), double.IsNaN(outro) ? null : S(outro), dest);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Contain("already on a keyframe");
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        result.EndSnapped.Should().BeFalse("the lossless cut is exact here, so there is nothing to tell the user");
        runner.Commands.Should().BeEmpty();
        File.Exists(dest).Should().BeFalse();
    }

    // serves-spec: SPEC-001#I71 / SPEC-002#I50 - a source Exact does not cut (an MPEG-TS file: its timestamps do not start at zero)
    // keeps the lossless cut with a mid-GOP end too, and now SAYS the end snaps: the result carries it and the row's warning names
    // the reason. (Before the fix the engine handed a start on a keyframe back before it asked, so this was silent.)
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnEndTrimOnASourceExactDoesNotCut_FallsBack_AndTheRowSaysSo()
    {
        RequireBinaries();
        var mp4 = await MakeSourceAsync();
        var ts = Path.Combine(_dir, "g50.ts");
        var copy = await new FfmpegRunner(Locator()).RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error").Input(mp4).Raw("-map", "0", "-c", "copy").Output(ts));
        copy.ExitCode.Should().Be(0, copy.StdErrText);
        var keyframes = await Probe().GetKeyframesAsync(ts);
        keyframes.Count.Should().BeGreaterThan(4);
        var start = keyframes[2];
        var end = keyframes[4] + S(1.1);

        var runner = new SmartCutJointTiming.RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept"));
        var result = await new SmartCutEngine(runner, Probe()).CutAsync(ts, start, end, Path.Combine(_dir, "out", "cut.ts"));
        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(SmartCutArgsBuilder.NonZeroStartTimeReason);
        result.EndSnapped.Should().BeTrue("the lossless cut ends at a keyframe, not 1.1 s into the next GOP");
        runner.Commands.Should().BeEmpty();

        var (row, dest) = await RunExactRowAsync(ts, start.TotalSeconds, end.TotalSeconds);
        row.Outcome.Should().Be(ItemOutcome.Done, row.Error?.Message);
        row.Warnings.Should().Contain(w =>
            w.StartsWith(BulkTrimEngine.ExactFallbackPrefix) && w.Contains(SmartCutArgsBuilder.NonZeroStartTimeReason));
        File.Exists(dest).Should().BeTrue();
    }
}
