using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-190 (G-059, SPEC-001 I44/I45/I46/I47/I48) - the unit half of the two-run Exact cut: the joint list, the time-base gate,
/// the run shape and the concat failure paths, all against fakes (the real-media half is
/// <see cref="SmartCutJointTimingIntegrationTests"/>).
///
/// <para>The shipped engine copied the kept part into a tail file with a third ffmpeg run and concatenated head + tail; its
/// tail started its video 75-80 ms after its audio and the concat kept that offset. The engine now writes the kept bytes once:
/// the head encode, then one concat of <c>[head, the source with inpoint/outpoint]</c>. And a source whose format
/// <c>start_time</c> is not zero - MPEG-TS, AVCHD - is a stated fall back, because its keyframe times and ffmpeg's
/// <c>-ss</c> offsets do not coincide.</para>
/// </summary>
public sealed class SmartCutTwoRunTests : IDisposable
{
    private const string StartTimeReason =
        "frame-exact cutting is not supported yet for .ts, .m2ts and .mts files or other files whose timestamps do not start at zero";

    private readonly string _dir;

    public SmartCutTwoRunTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t190u-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static MediaInfo Info(double formatStart, string video = "h264", string audio = "aac", double? videoStreamStart = null) => new(
        S(60),
        "mov,mp4,m4a,3gp,3g2,mj2",
        new[] { new StreamInfo(0, video, "video", 1920, 1080, "yuv420p", null, null, "1/30", StartTime: videoStreamStart is { } v ? S(v) : null) },
        new[] { new StreamInfo(1, audio, "audio", null, null, null, 48000, 2, "1/48000") },
        S(formatStart));

    private static IReadOnlyList<TimeSpan> Grid2s() => Enumerable.Range(0, 31).Select(i => S(i * 2)).ToList();

    private sealed class FixedProbe : IMediaProbe
    {
        private readonly MediaInfo _info;
        private readonly IReadOnlyList<TimeSpan> _keyframes;

        public FixedProbe(MediaInfo info, IReadOnlyList<TimeSpan> keyframes)
        {
            _info = info;
            _keyframes = keyframes;
        }

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(ProbeResult.Success(_info));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(_keyframes);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
            throw new NotSupportedException("the smart cutter plans against the raw keyframes and never snaps");

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    private string Placeholder(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "the-original-bytes");
        return p;
    }

    // ---- RenderJointList (pure) ----------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I44 - the joint list is exactly these lines, in order, seconds by ToFfmpegSeconds.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RenderJointList_EmitsExactlyTheLinesInOrder_AndNoOutpointWithoutAnEnd()
    {
        var list = SmartCutArgsBuilder.RenderJointList(
            @"C:\work\head.mp4", S(3.5), @"C:\videos\clip.mp4", S(8.0166667), end: null);

        list.Should().Be(
            "file 'C:\\work\\head.mp4'\nduration 3.5\nfile 'C:\\videos\\clip.mp4'\ninpoint 8.016667\n",
            "head, its duration (HeadEnd - Start), the source, the boundary keyframe; seconds rounded to 6 places");
    }

    // serves-spec: SPEC-001#I44 - a kept middle adds the outpoint, last.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RenderJointList_AddsTheOutpoint_WhenTheKeptRangeHasAnEnd()
    {
        var list = SmartCutArgsBuilder.RenderJointList(
            @"C:\work\head.mp4", S(3), @"C:\videos\clip.mp4", S(8), end: S(22.5));

        list.Should().Be(
            "file 'C:\\work\\head.mp4'\nduration 3\nfile 'C:\\videos\\clip.mp4'\ninpoint 8\noutpoint 22.5\n");
    }

    // serves-spec: SPEC-001#I44 - a path holding a single quote is escaped the concat-demuxer way, in both entries.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RenderJointList_EscapesASingleQuoteInAPath()
    {
        var list = SmartCutArgsBuilder.RenderJointList(
            @"C:\work\it's head.mp4", S(1), @"C:\videos\Dad's clip.mp4", S(2), end: null);

        list.Should().Contain("file 'C:\\work\\it'\\''s head.mp4'\n");
        list.Should().Contain("file 'C:\\videos\\Dad'\\''s clip.mp4'\n");
    }

    // serves-spec: SPEC-001#I44 - a relative path is made absolute (the list is read from the temp dir, not the cwd).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RenderJointList_MakesRelativePathsAbsolute()
    {
        var list = SmartCutArgsBuilder.RenderJointList("head.mp4", S(1), @"sub\clip.mp4", S(2), end: null);

        list.Should().Contain("file '" + Path.GetFullPath("head.mp4") + "'\n");
        list.Should().Contain("file '" + Path.GetFullPath(@"sub\clip.mp4") + "'\n");
    }

    // serves-spec: SPEC-001#I44 - a blank path is a programming error, not a list.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("", "clip.mp4")]
    [InlineData("head.mp4", " ")]
    public void RenderJointList_RejectsABlankPath(string head, string source)
    {
        var act = () => SmartCutArgsBuilder.RenderJointList(head, S(1), source, S(2), end: null);

        act.Should().Throw<ArgumentException>();
    }

    // ---- The time-base gate: TryResolveEncoders ----------------------------------------------------------------------

    // serves-spec: SPEC-001#I45 - zero, +-0.5 ms and exactly 1 ms are zero (the mp4/mkv timestamp rounding): Exact resolves.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.0005)]
    [InlineData(-0.0005)]
    [InlineData(0.001)]
    [InlineData(-0.001)]
    public void TryResolveEncoders_ResolvesWhenTheFormatStartsAtZero(double startTime)
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info(startTime), out var video, out var audio, out var why)
            .Should().BeTrue("a format start_time within 1 ms of zero is zero");

        video.Should().Be("libx264");
        audio.Should().Be("aac");
        why.Should().BeNull();
    }

    // serves-spec: SPEC-001#I45 / SPEC-001#I46 - past 1 ms either way Exact refuses with the stated reason: 1.5 ms, -1.5 ms,
    // the negative composition offset (-21 ms, no edit list) and an MPEG-TS (about 1.4 s). The gate uses the absolute value.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(0.0015)]
    [InlineData(-0.0015)]
    [InlineData(-0.021)]
    [InlineData(0.021)]
    [InlineData(1.4)]
    [InlineData(1.445333)]
    public void TryResolveEncoders_RefusesAFormatThatDoesNotStartAtZero_WithTheReason(double startTime)
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info(startTime), out var video, out var audio, out var why)
            .Should().BeFalse("Exact assumes the probe's keyframe times and ffmpeg's -ss offsets coincide, which needs a zero start_time");

        video.Should().BeNull();
        audio.Should().BeNull();
        why.Should().Be(StartTimeReason);
    }

    // serves-spec: SPEC-001#I46 - the sentence is the row's: it names the extensions, says the cause, and (T-189's copy rule)
    // carries no parentheses, because the row warning wraps the reason in its own.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheStartTimeReason_NamesTheExtensions_AndHasNoParentheses()
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info(1.4), out _, out _, out var why).Should().BeFalse();

        why.Should().Contain(".ts").And.Contain(".m2ts").And.Contain(".mts").And.Contain("do not start at zero");
        why.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
        why.Should().NotContain("start_time", "no internal terms in a sentence the row shows");
    }

    // serves-spec: SPEC-001#I45 - the time base is a property of the file, so it is checked before the codec: an HEVC TS says
    // the TS reason (the reason that stays true when the HEVC repair T-201 lands), and an unmappable codec on a TS does too.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("hevc", "aac")]
    [InlineData("h264", "pcm_s24le")]
    [InlineData("prores_raw_hq", "aac")]
    public void TheTimeBaseGate_IsCheckedBeforeTheCodecs(string videoCodec, string audioCodec)
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info(1.4, videoCodec, audioCodec), out _, out _, out var why).Should().BeFalse();

        why.Should().Be(StartTimeReason);
    }

    // serves-spec: SPEC-001#I45 - the gate reads the FORMAT start, not the video stream's: a file whose video starts one AAC
    // frame after its audio (the app's own Join output, the bench's 4k10m and mkv) joins correctly (I68), so it is cut.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(0.021029)]
    [InlineData(0.021)]
    public void TryResolveEncoders_DoesNotReadTheVideoStreamsOwnStart(double videoStreamStart)
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info(0, videoStreamStart: videoStreamStart), out var video, out _, out var why)
            .Should().BeTrue("the measured joint on such a source is as correct as on any other (SPEC-001 I68)");

        video.Should().Be("libx264");
        why.Should().BeNull();
    }

    // serves-spec: SPEC-001#I45 - an audio-only source with a zero start is unchanged; one that does not start at zero refuses.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AnAudioOnlySource_IsGatedOnItsStartTimeToo()
    {
        MediaInfo AudioOnly(double start) => new(
            S(30), "mp4", Array.Empty<StreamInfo>(),
            new[] { new StreamInfo(0, "aac", "audio", null, null, null, 48000, 2, "1/48000") }, S(start));

        SmartCutArgsBuilder.TryResolveEncoders(AudioOnly(0), out var video, out var audio, out _).Should().BeTrue();
        video.Should().BeNull();
        audio.Should().Be("aac");

        SmartCutArgsBuilder.TryResolveEncoders(AudioOnly(1.4), out _, out _, out var why).Should().BeFalse();
        why.Should().Be(StartTimeReason);
    }

    // ---- The engine on such a source: a stated fall back, no ffmpeg ---------------------------------------------------

    // serves-spec: SPEC-001#I45 / SPEC-001#I46 / SPEC-001#I47 - a TS-shaped source (format start 1.4 s) falls back with the
    // stated reason, runs ZERO ffmpeg invocations, writes nothing and creates no temp dir.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_OnASourceThatDoesNotStartAtZero_FallsBackWithTheReason_AndRunsNoFfmpeg()
    {
        var input = Placeholder("camera.mts");
        var output = Path.Combine(_dir, "camera_trimmed.mts");
        var runner = new RecordingFakeRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(1.4), Grid2s()));

        var result = await engine.CutAsync(input, S(5), null, output);

        runner.Commands.Should().BeEmpty("nothing is re-encoded, copied or joined for a source Exact cannot cut");
        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(StartTimeReason);
        result.OutputPath.Should().BeNull();
        result.ReencodedDuration.Should().Be(TimeSpan.Zero);
        File.Exists(output).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty("no temp dir is even created");
    }

    // serves-spec: SPEC-001#I40 - a start already on a keyframe is PureCopy before any gate, also on a TS: the lossless cut is
    // exact there, so the answer is "already exact", not the time-base reason.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_OnASourceThatDoesNotStartAtZero_StartOnAKeyframe_IsPureCopy()
    {
        var input = Placeholder("camera.mts");
        var runner = new RecordingFakeRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(1.4), Grid2s()));

        var result = await engine.CutAsync(input, S(4), null, Path.Combine(_dir, "out.mts"));

        runner.Commands.Should().BeEmpty();
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Contain("already on a keyframe");
    }

    // serves-spec: SPEC-001#I48 - a PureCopy makes zero runs (the lossless cut is the answer).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_PureCopy_MakesZeroRuns()
    {
        var input = Placeholder("clip.mp4");
        var runner = new RecordingFakeRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(0), Grid2s()));

        var result = await engine.CutAsync(input, S(8), null, Path.Combine(_dir, "out.mp4"));

        runner.Commands.Should().BeEmpty();
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        result.FellBack.Should().BeTrue();
    }

    // ---- T-224: a start on a keyframe with the end inside a GOP is the joint without its head ------------------------------

    // serves-spec: SPEC-001#I71 - the end-trim list is exactly these three lines: the source, the keyframe it starts on, the end.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RenderEndTrimList_EmitsTheSourceItsInpointAndItsOutpoint_AndNothingElse()
    {
        var list = SmartCutArgsBuilder.RenderEndTrimList(@"C:\videos\clip.mp4", S(8), S(13.0166667));

        list.Should().Be("file 'C:\\videos\\clip.mp4'\ninpoint 8\noutpoint 13.016667\n", "seconds rounded to 6 places, like the joint list");
    }

    // serves-spec: SPEC-001#I71 - a single quote is escaped the concat-demuxer way, and a relative path is made absolute.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RenderEndTrimList_EscapesASingleQuote_AndMakesARelativePathAbsolute()
    {
        SmartCutArgsBuilder.RenderEndTrimList(@"C:\videos\Dad's clip.mp4", S(2), S(3)).Should().Contain("file 'C:\\videos\\Dad'\\''s clip.mp4'\n");
        SmartCutArgsBuilder.RenderEndTrimList(@"sub\clip.mp4", S(2), S(3)).Should().Contain("file '" + Path.GetFullPath(@"sub\clip.mp4") + "'\n");
    }

    // serves-spec: SPEC-001#I71 - a blank source is a programming error, not a list.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RenderEndTrimList_RejectsABlankSource(string source)
    {
        var act = () => SmartCutArgsBuilder.RenderEndTrimList(source, S(2), S(3));

        act.Should().Throw<ArgumentException>();
    }

    // serves-spec: SPEC-001#I71 - the keyframe at 8 s, an end at 13 s inside the GOP of 12-14 s: ONE run, the concat of the source
    // itself (no head, no encoder, no tail file), the list says inpoint 8 / outpoint 13, the result is a produced file that
    // re-encoded nothing and started on the keyframe.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_AStartOnAKeyframeWithAnEndInsideAGop_IsOneConcatOfTheSourceItself()
    {
        var input = Placeholder("it's clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        var runner = new ListCapturingRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(0), Grid2s()));

        var result = await engine.CutAsync(input, S(8), S(13), output);

        result.FellBack.Should().BeFalse(result.FallbackReason);
        result.OutputPath.Should().Be(output);
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        result.ReencodedDuration.Should().Be(TimeSpan.Zero);
        result.EffectiveStart.Should().Be(S(8));
        result.EndSnapped.Should().BeFalse();
        runner.Commands.Should().ContainSingle("no head encode");
        JoinArgsBuilder.SatisfiesCopyInvariant(runner.Commands[0]).Should().BeTrue("a stream copy through the unchanged ConcatCopy");
        runner.Lists.Should().ContainSingle().Which.Should().Be(
            "file '" + Path.GetFullPath(input).Replace("'", "'\\''", StringComparison.Ordinal) + "'\ninpoint 8\noutpoint 13\n");
        File.Exists(output).Should().BeTrue();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
    }

    // serves-spec: SPEC-001#I71 / SPEC-001#I69 - its one run owns the whole row's fraction: never backwards, 1.0 once, at the end.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_TheEndTrim_ReportsItsOneRunAcrossTheRow_AndOneAtTheEnd()
    {
        var input = Placeholder("clip.mp4");
        var progress = new RecordingProgress<double>();
        var runner = new ListCapturingRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(0), Grid2s()));

        await engine.CutAsync(input, S(8), S(13), Path.Combine(_dir, "cut.mp4"), progress);

        progress.Reports.Should().NotBeEmpty();
        progress.Reports.Should().BeInAscendingOrder("a row's fraction never goes backwards");
        progress.Reports.Count(p => p >= 1d).Should().Be(1, "1.0 is reported once, after the output is in place");
        progress.Reports[^1].Should().Be(1d);
    }

    // serves-spec: SPEC-001#I71 / SPEC-001#I40 - where the lossless cut IS exact the engine hands the row back with zero runs and
    // says nothing about the end: an end on a keyframe (2 s grid: 12), an end at the file's end, within the planner's 10 ms of it
    // or past it, and no end at all.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(8.0, 12.0)]
    [InlineData(8.0, 12.005)]
    [InlineData(8.0, 59.995)]
    [InlineData(8.0, 60.0)]
    [InlineData(8.0, double.NaN)]
    public async Task SmartCutEngine_AnEndOnAKeyframeOrAtTheEndOfTheFile_IsStillPureCopy_WithZeroRuns(double start, double end)
    {
        var input = Placeholder("clip.mp4");
        var runner = new RecordingFakeRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(0), Grid2s()));

        var result = await engine.CutAsync(input, S(start), double.IsNaN(end) ? null : S(end), Path.Combine(_dir, "cut.mp4"));

        runner.Commands.Should().BeEmpty();
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Contain("already on a keyframe");
        result.EndSnapped.Should().BeFalse("the lossless cut ends where it was asked to");
        result.EffectiveStart.Should().BeNull();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I71 / SPEC-001#I40 - a start up to 10 ms past a keyframe is PureCopy (the planner's tolerance): the copy
    // starts on THAT keyframe, and the list names it, not the request.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_ARequestJustPastAKeyframe_StartsTheCopyOnThatKeyframe()
    {
        var input = Placeholder("clip.mp4");
        var runner = new ListCapturingRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(0), Grid2s()));

        var result = await engine.CutAsync(input, S(8.004), S(13), Path.Combine(_dir, "cut.mp4"));

        result.FellBack.Should().BeFalse(result.FallbackReason);
        result.EffectiveStart.Should().Be(S(8), "the file starts on the keyframe, up to 10 ms before the request");
        runner.Lists.Should().ContainSingle().Which.Should().Contain("inpoint 8\n").And.Contain("outpoint 13\n");
    }

    // serves-spec: SPEC-001#I71 / SPEC-001#I46 - a source Exact does not cut keeps the lossless cut with an end inside a GOP too, and
    // now says the end snaps: the same gate and the same reason as every other Exact fall back, zero runs, nothing created.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(1.4, "h264", "aac")]
    [InlineData(0.0, "hevc", "aac")]
    [InlineData(0.0, "h264", "pcm_s24le")]
    public async Task SmartCutEngine_AnEndTrimOnASourceExactDoesNotCut_FallsBackWithTheReason_AndSaysTheEndSnaps(
        double formatStart, string video, string audio)
    {
        var input = Placeholder("camera.mov");
        var runner = new RecordingFakeRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(formatStart, video, audio), Grid2s()));

        var result = await engine.CutAsync(input, S(4), S(9), Path.Combine(_dir, "sub", "cut.mov"));

        result.FellBack.Should().BeTrue();
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        result.EndSnapped.Should().BeTrue("the lossless cut ends at a keyframe, not at 9 s");
        result.FallbackReason.Should().NotBeNullOrWhiteSpace().And.NotContain("already on a keyframe");
        if (formatStart != 0)
        {
            result.FallbackReason.Should().Be(StartTimeReason);
        }

        runner.Commands.Should().BeEmpty();
        Directory.Exists(Path.Combine(_dir, "sub")).Should().BeFalse("nothing is created for a source Exact refuses");
    }

    // serves-spec: SPEC-001#I71 / SPEC-001#I47 - a start on a keyframe with NO end on a source Exact does not cut is the silent
    // fall back it always was: the lossless cut is exact, so there is nothing to say.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_ARefusedSourceWithNoEnd_StaysAQuietPureCopy()
    {
        var input = Placeholder("camera.mov");
        var engine = new SmartCutEngine(new RecordingFakeRunner(), new FixedProbe(Info(1.4), Grid2s()));

        var result = await engine.CutAsync(input, S(4), null, Path.Combine(_dir, "cut.mov"));

        result.EndSnapped.Should().BeFalse();
        result.FallbackReason.Should().Contain("already on a keyframe");
    }

    private sealed class EndTrimRunner : IFfmpegRunner
    {
        private readonly bool _exitNonZero;
        private readonly bool _cancel;

        public EndTrimRunner(bool exitNonZero = false, bool cancel = false)
        {
            _exitNonZero = exitNonZero;
            _cancel = cancel;
        }

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (_cancel)
            {
                throw new OperationCanceledException(ct);
            }

            return Task.FromResult(_exitNonZero
                ? new FfmpegResult(1, new List<string> { "concat: boom" }.AsReadOnly())
                : new FfmpegResult(0, new List<string>().AsReadOnly())); // exits 0 and writes no final file
        }
    }

    // serves-spec: SPEC-001#I71 / SPEC-001#I47 - the concat fails (non-zero, or 0 with no final file): a SplitException that names the
    // step, nothing at the destination, an existing destination untouched, no temp dir.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SmartCutEngine_WhenTheEndTrimFails_ThrowsAnError_AndLeavesNothingBehind(bool exitNonZero)
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        await File.WriteAllTextAsync(output, "an earlier result");
        var engine = new SmartCutEngine(new EndTrimRunner(exitNonZero), new FixedProbe(Info(0), Grid2s()));

        var act = () => engine.CutAsync(input, S(8), S(13), output);

        (await act.Should().ThrowAsync<SplitException>()).Which.Message.Should().Contain("while cutting the end");
        File.ReadAllText(output).Should().Be("an earlier result", "the final is moved into place only after it exists");
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
    }

    // serves-spec: SPEC-001#I71 / SPEC-001#I47 - a cancelled end trim leaves no temp dir and no output.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_WhenTheEndTrimIsCancelled_LeavesNothingBehind()
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var engine = new SmartCutEngine(new EndTrimRunner(cancel: true), new FixedProbe(Info(0), Grid2s()));

        var act = () => engine.CutAsync(input, S(8), S(13), output, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(output).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty();
    }

    // ---- The batch: a lossless cut, the row's warning ---------------------------------------------------------------------

    // serves-spec: SPEC-002#I50 / SPEC-002#I51 / SPEC-001#I46 - an Exact row on a source that does not start at zero runs the
    // ordinary lossless cut and its warning names the reason (the same engine, the same sentence as an HEVC row).
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task BulkTrimEngine_ExactRowOnASourceThatDoesNotStartAtZero_RunsTheLosslessCut_WithTheReasonInItsWarning()
    {
        var input = Placeholder("camera.mts");
        var split = new FakeSplitEngine();
        var item = new BulkTrimItem(input, S(3.5), null, Path.Combine(_dir, "camera_trimmed.mts"));
        var runner = new RecordingFakeRunner();
        var smart = new SmartCutEngine(runner, new FixedProbe(Info(1.4), Grid2s()));

        var result = await new BulkTrimEngine(split, new FakeRequestBuilder(), new FakeDiskSpaceProbe(long.MaxValue), smart, null)
            .RunAsync(new[] { item }, new BulkTrimOptions(Precision: CutPrecision.Exact));

        runner.Commands.Should().BeEmpty("Exact ran no ffmpeg for this source");
        split.CallCount.Should().Be(1, "the fall-back row takes the ordinary lossless path");
        var row = result.Items.Single();
        row.Outcome.Should().Be(ItemOutcome.Done);
        row.Warnings.Should().ContainSingle()
            .Which.Should().Be($"{BulkTrimEngine.ExactFallbackPrefix} ({StartTimeReason}) - cut snapped to the nearest keyframe");
    }

    // ---- The run shape with an end ----------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I44 / SPEC-001#I48 - a kept middle is still two runs, and its list ends with the outpoint.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_HeadReencodeWithAnEnd_IsTwoRuns_AndTheListCarriesTheOutpoint()
    {
        var input = Placeholder("it's clip.mp4");
        var runner = new ListCapturingRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(0), Grid2s()));

        var result = await engine.CutAsync(input, S(5), S(22), Path.Combine(_dir, "middle.mp4"));

        result.FellBack.Should().BeFalse();
        runner.Commands.Should().HaveCount(2);
        var lines = runner.Lists.Should().ContainSingle().Which.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(5);
        lines[1].Should().Be("duration 1", "5 s to the 6 s keyframe");
        lines[2].Should().Be("file '" + Path.GetFullPath(input).Replace("'", "'\\''", StringComparison.Ordinal) + "'");
        lines[3].Should().Be("inpoint 6");
        lines[4].Should().Be("outpoint 22");
    }

    // ---- The concat run fails -----------------------------------------------------------------------------------------

    public enum ConcatOutcome
    {
        ExitsNonZero,
        ExitsZeroWithNoFinalFile,
        IsCancelled,
    }

    /// <summary>Run 1 (the head) succeeds and writes its file; run 2 (the concat) does what <paramref name="outcome"/> says.</summary>
    private sealed class ConcatFailingRunner : IFfmpegRunner
    {
        private readonly ConcatOutcome _outcome;

        public ConcatFailingRunner(ConcatOutcome outcome) => _outcome = outcome;

        public int Calls { get; private set; }

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            var tokens = args.ToList();
            if (Calls == 1)
            {
                File.WriteAllText(tokens[^1], "head");
                return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
            }

            return _outcome switch
            {
                ConcatOutcome.ExitsNonZero => Task.FromResult(new FfmpegResult(1, new List<string> { "concat: boom" }.AsReadOnly())),
                ConcatOutcome.ExitsZeroWithNoFinalFile => Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly())),
                _ => throw new OperationCanceledException(ct),
            };
        }
    }

    // serves-spec: SPEC-001#I47 - the concat (run 2, which now reads the source directly) exits non-zero, or exits 0 with no
    // final file: a SplitException that names the joining step, no output at the destination, no temp dir left.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(ConcatOutcome.ExitsNonZero)]
    [InlineData(ConcatOutcome.ExitsZeroWithNoFinalFile)]
    public async Task SmartCutEngine_WhenTheConcatFails_ThrowsAJoiningError_AndLeavesNothingBehind(ConcatOutcome outcome)
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        var runner = new ConcatFailingRunner(outcome);
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(0), Grid2s()));

        var act = () => engine.CutAsync(input, S(5), null, output);

        (await act.Should().ThrowAsync<SplitException>())
            .Which.Message.Should().Contain("while joining the fragments");
        runner.Calls.Should().Be(2, "the head ran, then the concat failed: no retry, no tail run");
        File.Exists(output).Should().BeFalse("a failed cut leaves no output at the destination");
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
    }

    // serves-spec: SPEC-001#I47 - a failed concat never touches a file already at the destination (the final is moved into
    // place only after it exists).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_WhenTheConcatFails_LeavesAnExistingDestinationUntouched()
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        await File.WriteAllTextAsync(output, "an earlier result");
        var engine = new SmartCutEngine(new ConcatFailingRunner(ConcatOutcome.ExitsNonZero), new FixedProbe(Info(0), Grid2s()));

        var act = () => engine.CutAsync(input, S(5), null, output);

        await act.Should().ThrowAsync<SplitException>();
        (await File.ReadAllTextAsync(output)).Should().Be("an earlier result");
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I47 - a cancel that lands on the concat leaves no output and no temp dir either.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_WhenTheConcatIsCancelled_LeavesNoOutputAndNoTempDir()
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        var runner = new ConcatFailingRunner(ConcatOutcome.IsCancelled);
        var engine = new SmartCutEngine(runner, new FixedProbe(Info(0), Grid2s()));

        var act = () => engine.CutAsync(input, S(5), null, output);

        await act.Should().ThrowAsync<OperationCanceledException>();
        runner.Calls.Should().Be(2);
        File.Exists(output).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty();
    }
}

/// <summary>
/// <see cref="RecordingFakeRunner"/> that also reads the concat list at the moment the concat run starts: the engine sweeps
/// its temp dir before a test could look, so a test asserts the list the engine really wrote.
/// </summary>
internal sealed class ListCapturingRunner : IFfmpegRunner
{
    private readonly RecordingFakeRunner _inner = new();

    public List<IReadOnlyList<string>> Commands => _inner.Commands;

    public List<string> Lists { get; } = new();

    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var tokens = args.ToList();
        if (tokens.Contains("concat"))
        {
            Lists.Add(File.ReadAllText(tokens[tokens.ToList().IndexOf("-i") + 1]));
        }

        return _inner.RunAsync(args, totalDuration, progress, ct);
    }
}
