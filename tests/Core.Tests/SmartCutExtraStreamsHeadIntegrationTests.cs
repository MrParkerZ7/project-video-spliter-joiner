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
/// T-238 (G-059, SPEC-001 I71, SPEC-011) - an Exact cut on a file that carries a subtitle, a data stream or a cover picture.
/// Real ffmpeg, synthetic media.
///
/// <para><b>The defect.</b> The Exact head command maps every stream (<c>-map 0</c>) and gives the head no subtitle codec, so an mp4
/// with a <c>mov_text</c> track made ffmpeg stop with "Error selecting an encoder" and the Bulk row ended Failed, while the lossless
/// cut of the same file worked. Measured on the shipped code with a 12 s source (a keyframe every 2 s) and an intro of 3 s: the
/// subtitle and the cover picture end the row Failed in the head ("re-encoding the leading fragment"), and a timecode data track
/// ends it Failed in the join ("joining the fragments"). The first test is the repro: against the code before the fix it fails with
/// the row Failed.</para>
///
/// <para><b>The fix under test.</b> <see cref="SmartCutArgsBuilder.TryResolveEncoders"/> refuses a source with
/// <see cref="MediaInfo.ExtraStreams"/> above zero, with <see cref="SmartCutArgsBuilder.ExtraStreamsReason"/>, so the engine hands
/// the cut to the lossless cut, which carries every stream, before any ffmpeg run, and the Bulk row says the same thing before Run.</para>
/// </summary>
public sealed class SmartCutExtraStreamsHeadIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SmartCutExtraStreamsHeadIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t238-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// A 12 s, 25 fps source with a closed GOP of 50 frames (a keyframe every 2 s) and B-frames, plus one more stream:
    /// <c>subtitle</c> (a <c>mov_text</c> track made from a two-cue SRT, an MP4), <c>cover</c> (a JPEG as an attached picture, an
    /// MP4), <c>timecode</c> (a timecode data track, a MOV) or <c>audio2</c> (a second audio track: not an extra stream).
    /// </summary>
    private async Task<string> MakeSourceAsync(string kind)
    {
        var mp4 = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "plain-" + kind + ".mp4", 12, 25, "-c:v", "libx264", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-bf", "2");
        var runner = new FfmpegRunner(Locator());
        string path;
        FfmpegArgs args;
        if (kind == "subtitle")
        {
            var srt = Path.Combine(_dir, "text.srt");
            File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:03,000\nfirst line\n\n2\n00:00:05,000 --> 00:00:08,000\nsecond line\n");
            path = Path.Combine(_dir, "subtitle.mp4");
            args = FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error").Input(mp4).Input(srt)
                .Raw("-map", "0", "-map", "1", "-c", "copy", "-c:s", "mov_text").Output(path);
        }
        else if (kind == "cover")
        {
            var jpg = Path.Combine(_dir, "cover.jpg");
            var made = await runner.RunAsync(FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error")
                .Raw("-f", "lavfi", "-i", "testsrc=size=160x120:rate=1:duration=1").Raw("-frames:v", "1").Output(jpg));
            made.ExitCode.Should().Be(0, made.StdErrText);
            path = Path.Combine(_dir, "cover.mp4");
            args = FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error").Input(mp4).Input(jpg)
                .Raw("-map", "0", "-map", "1", "-c", "copy", "-disposition:v:1", "attached_pic").Output(path);
        }
        else if (kind == "audio2")
        {
            path = Path.Combine(_dir, "audio2.mp4");
            args = FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error").Input(mp4)
                .Raw("-map", "0", "-map", "0:a", "-c", "copy").Output(path);
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

    private static async Task<MediaInfo> InfoOfAsync(string path)
    {
        var probed = await Probe().ProbeAsync(path);
        probed.Should().BeOfType<ProbeResult.ProbeSucceeded>();
        return ((ProbeResult.ProbeSucceeded)probed).Info;
    }

    /// <summary>One Exact row through the Bulk engine, with the real Exact engine and the real lossless engine behind it.</summary>
    private async Task<(BulkTrimItemResult Row, string Dest)> RunExactRowAsync(string src, double intro, double? outro)
    {
        var dest = Path.Combine(_dir, "trimmed", "cut" + Path.GetExtension(src));
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

    // serves-spec: SPEC-001#I71 / SPEC-011 - the repro, written first (T-238 acceptance 1 and 2). An Exact row whose intro falls
    // inside a GOP of a file with a subtitle, a cover picture or a timecode track used to end Failed. Now the row is Done by the
    // lossless cut, says why in the Exact fall-back note, and the output carries every stream the source had: no stream is lost.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("subtitle")]
    [InlineData("cover")]
    [InlineData("timecode")]
    public async Task AnExactRow_OnASourceWithAnExtraStream_FallsBackToTheLosslessCut_AndKeepsEveryStream(string kind)
    {
        RequireBinaries();
        var src = await MakeSourceAsync(kind);
        var srcInfo = await InfoOfAsync(src);
        srcInfo.ExtraStreams.Should().Be(1, "precondition: the probe sees the extra stream");

        var (row, dest) = await RunExactRowAsync(src, 3.0, 9.0);

        _output.WriteLine($"{kind}: outcome {row.Outcome}; error {row.Error?.Message}; warnings [{string.Join(" | ", row.Warnings)}]");
        row.Outcome.Should().Be(ItemOutcome.Done, row.Error?.Message);
        row.Warnings.Should().Contain(w =>
            w.StartsWith(BulkTrimEngine.ExactFallbackPrefix) && w.Contains(SmartCutArgsBuilder.ExtraStreamsReason),
            "the row says why it did not cut frame-exact");
        File.Exists(dest).Should().BeTrue();

        var outInfo = await InfoOfAsync(dest);
        _output.WriteLine($"{kind}: output video {outInfo.VideoStreams.Count}, audio {outInfo.AudioStreams.Count}, extra {outInfo.ExtraStreams}");
        outInfo.VideoStreams.Count.Should().Be(srcInfo.VideoStreams.Count, "the lossless cut carries the picture and the cover");
        outInfo.AudioStreams.Count.Should().Be(srcInfo.AudioStreams.Count);
        outInfo.ExtraStreams.Should().Be(srcInfo.ExtraStreams, "the subtitle, cover picture or timecode track is still in the file");
    }

    // serves-spec: SPEC-001#I71 / SPEC-011 - acceptance 3: the reason is the same text before and after Run. The predicate the Bulk
    // row evaluates before Run, the engine's fall back and the row's note all carry one sentence, and the engine runs ffmpeg zero
    // times and creates nothing for it.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("subtitle")]
    [InlineData("cover")]
    [InlineData("timecode")]
    public async Task TheReason_IsTheSameBeforeAndAfterRun_AndTheEngineRunsNothing(string kind)
    {
        RequireBinaries();
        var src = await MakeSourceAsync(kind);
        var info = await InfoOfAsync(src);
        var dest = Path.Combine(_dir, "out", "cut" + Path.GetExtension(src));

        SmartCutArgsBuilder.TryResolveEncoders(info, out _, out _, out var before, src, dest).Should().BeFalse();

        var runner = new SmartCutJointTiming.RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept"));
        var result = await new SmartCutEngine(runner, Probe()).CutAsync(src, S(3), S(9), dest);

        result.FellBack.Should().BeTrue();
        result.Strategy.Should().Be(SmartCutStrategy.HeadReencode, "the start is inside a GOP: the cut that would have failed");
        result.FallbackReason.Should().Be(before).And.Be(SmartCutArgsBuilder.ExtraStreamsReason);
        result.FallbackReason.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
        runner.Commands.Should().BeEmpty("the question is answered before ffmpeg runs");
        File.Exists(dest).Should().BeFalse();
        Directory.Exists(Path.Combine(_dir, "out")).Should().BeFalse("nothing is created before the question is answered");
    }

    // serves-spec: SPEC-001#I71 - the boundary: a second audio track is not an extra stream. The probe counts none, so the gate
    // still lets the file through (the Exact cut of such a file is what it was before this ticket).
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task ASourceWithTwoAudioTracks_HasNoExtraStream_AndPassesTheGate()
    {
        RequireBinaries();
        var src = await MakeSourceAsync("audio2");
        var info = await InfoOfAsync(src);
        info.AudioStreams.Count.Should().Be(2, "precondition: two audio tracks");

        info.ExtraStreams.Should().Be(0);
        SmartCutArgsBuilder.TryResolveEncoders(info, out _, out _, out var why, src, src).Should().BeTrue(why);
    }
}
