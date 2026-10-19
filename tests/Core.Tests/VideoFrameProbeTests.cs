using System.Globalization;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-190, second pass (SPEC-001 I43) - the two frame-level questions the frame-exact cut asks of its source
/// (<see cref="IVideoFrameProbe"/>), and what <see cref="SmartCutEngine"/> does with the answers.
///
/// <para>The first pass joined exactly only for a Start ON the source's frame grid. A Start between two frames left the head's
/// frames on the encoder's 1/fps grid counted from that Start, up to one frame off the copied part's: the joint 33 ms early with
/// a muxer timestamp line, or one repeated frame. The cut now starts at the first source video frame at or after the request,
/// found before the plan is made. And a head that lost the source's video (a Start 25 ms before a keyframe planned a head that
/// held no frame, and the concat took its streams from an audio-only file) is a fall back, never a Done.</para>
/// </summary>
public sealed class VideoFrameProbeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public VideoFrameProbeTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t190f-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private string Placeholder(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "the-original-bytes");
        return p;
    }

    private static string Packets(params string[] ptsTimes) =>
        "{\"packets\":[" + string.Join(",", ptsTimes.Select(t => t == "" ? "{}" : $"{{\"pts_time\":\"{t}\"}}")) + "]}";

    private sealed class ScriptedFfprobe : IFfprobeRunner
    {
        private readonly Func<FfmpegArgs, string> _answer;

        public ScriptedFfprobe(Func<FfmpegArgs, string> answer) => _answer = answer;

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
        {
            Calls.Add(args.ToList());
            return Task.FromResult(_answer(args));
        }
    }

    private sealed class FailingFfprobe : IFfprobeRunner
    {
        public Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default) =>
            throw new FfprobeException(1, new[] { "boom" });
    }

    // ---- FirstVideoFrameAtOrAfterAsync: the packet filter (fake ffprobe) -----------------------------------------------

    // serves-spec: SPEC-004#I40 - ffprobe seeks back to the keyframe BEFORE the interval, so the packets it lists start earlier;
    // the answer is the smallest pts at or after the request, whatever order the packets come in (B-frames: decode order).
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task FirstVideoFrameAtOrAfter_SkipsThePacketsBeforeTheRequest_AndIgnoresDecodeOrder()
    {
        var file = Placeholder("clip.mp4");
        var ffprobe = new ScriptedFfprobe(_ => Packets("6.000000", "6.100000", "6.033333", "6.066667", "7.000000", "7.100000", "7.066667", "7.033333"));

        var first = await new MediaProbe(ffprobe).FirstVideoFrameAtOrAfterAsync(file, S(7.018));

        first.Should().Be(S(7.033333));
    }

    // serves-spec: SPEC-004#I40 - the query: the first video stream, packet times only, an ABSOLUTE interval (a "+" end would
    // count from the first packet read, which is the keyframe before the request).
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task FirstVideoFrameAtOrAfter_AsksForPacketTimesOfTheFirstVideoStream_OverAnAbsoluteInterval()
    {
        var file = Placeholder("clip.mp4");
        var ffprobe = new ScriptedFfprobe(_ => Packets("7.033333"));

        await new MediaProbe(ffprobe).FirstVideoFrameAtOrAfterAsync(file, S(7.018));

        var call = ffprobe.Calls.Should().ContainSingle().Which;
        call.Should().ContainInOrder("-select_streams", "v:0");
        call.Should().ContainInOrder("-show_entries", "packet=pts_time");
        var interval = call[call.ToList().IndexOf("-read_intervals") + 1];
        interval.Should().Be("7.018%9.018", "from the request to two seconds past it, both absolute");
        call.Should().Contain(file);
    }

    // serves-spec: SPEC-004#I40 - a frame within a millisecond BEFORE the request counts as at it (a timestamp is printed to six
    // decimals, so the request 7.0335 s and the frame 7.033333 s are the same frame).
    [Trait("serves-spec", "SPEC-004")]
    [Theory]
    [InlineData(7.0333335)]
    [InlineData(7.0335)]
    [InlineData(7.034)]
    public async Task FirstVideoFrameAtOrAfter_CountsAFrameWithinAMillisecondBeforeTheRequestAsAtIt(double request)
    {
        var file = Placeholder("clip.mp4");
        var ffprobe = new ScriptedFfprobe(_ => Packets("7.000000", "7.033333", "7.066667"));

        var first = await new MediaProbe(ffprobe).FirstVideoFrameAtOrAfterAsync(file, S(request));

        first.Should().Be(S(7.033333));
    }

    // serves-spec: SPEC-004#I40 - but not a frame 2 ms before: that one is before the cut.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task FirstVideoFrameAtOrAfter_DoesNotTakeAFrameTwoMillisecondsBeforeTheRequest()
    {
        var file = Placeholder("clip.mp4");
        var ffprobe = new ScriptedFfprobe(_ => Packets("7.000000", "7.033333", "7.066667"));

        var first = await new MediaProbe(ffprobe).FirstVideoFrameAtOrAfterAsync(file, S(7.0353));

        first.Should().Be(S(7.066667));
    }

    // serves-spec: SPEC-004#I40 - no frame to give: nothing listed, only unparsable or missing times, a probe error, a missing
    // file, a blank path, a negative request - the answer is null (the engine then cuts at the request, as before), never a throw.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task FirstVideoFrameAtOrAfter_IsNullWhenThereIsNoFrameToGive()
    {
        var file = Placeholder("clip.mp4");

        (await new MediaProbe(new ScriptedFfprobe(_ => Packets())).FirstVideoFrameAtOrAfterAsync(file, S(7))).Should().BeNull("no packets");
        (await new MediaProbe(new ScriptedFfprobe(_ => Packets("6.0", "6.5"))).FirstVideoFrameAtOrAfterAsync(file, S(7))).Should().BeNull("only earlier ones");
        (await new MediaProbe(new ScriptedFfprobe(_ => Packets("N/A", "", "x"))).FirstVideoFrameAtOrAfterAsync(file, S(7))).Should().BeNull("no usable time");
        (await new MediaProbe(new ScriptedFfprobe(_ => "{}")).FirstVideoFrameAtOrAfterAsync(file, S(7))).Should().BeNull("no packets key");
        (await new MediaProbe(new FailingFfprobe()).FirstVideoFrameAtOrAfterAsync(file, S(7))).Should().BeNull("ffprobe failed");
        (await new MediaProbe(new ScriptedFfprobe(_ => Packets("7.0"))).FirstVideoFrameAtOrAfterAsync(Path.Combine(_dir, "gone.mp4"), S(7))).Should().BeNull("no file");
        (await new MediaProbe(new ScriptedFfprobe(_ => Packets("7.0"))).FirstVideoFrameAtOrAfterAsync(" ", S(7))).Should().BeNull("blank path");
        (await new MediaProbe(new ScriptedFfprobe(_ => Packets("7.0"))).FirstVideoFrameAtOrAfterAsync(file, S(-1))).Should().BeNull("negative time");
    }

    // serves-spec: SPEC-004#I40 - a cancel is not swallowed with the probe errors.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task FirstVideoFrameAtOrAfter_PropagatesACancellation()
    {
        var file = Placeholder("clip.mp4");

        var act = () => new MediaProbe(new OperationCancelledFfprobeRunner()).FirstVideoFrameAtOrAfterAsync(file, S(7));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- CountVideoPacketsAsync (fake ffprobe) ------------------------------------------------------------------------

    private static string Count(string? n) => n is null
        ? "{\"streams\":[]}"
        : $"{{\"streams\":[{{\"nb_read_packets\":\"{n}\"}}]}}";

    // serves-spec: SPEC-004#I40 - the count of the first video stream's packets, 0 for a file with no video stream.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task CountVideoPackets_ReadsTheFirstVideoStreamsPacketCount_AndZeroWithoutAVideoStream()
    {
        var file = Placeholder("head.mp4");
        var ffprobe = new ScriptedFfprobe(_ => Count("45"));

        (await new MediaProbe(ffprobe).CountVideoPacketsAsync(file)).Should().Be(45);
        var call = ffprobe.Calls.Should().ContainSingle().Which;
        call.Should().ContainInOrder("-select_streams", "v:0").And.Contain("-count_packets").And.Contain(file);
        call.Should().ContainInOrder("-show_entries", "stream=nb_read_packets");

        (await new MediaProbe(new ScriptedFfprobe(_ => Count(null))).CountVideoPacketsAsync(file)).Should().Be(0, "an audio-only file");
        (await new MediaProbe(new ScriptedFfprobe(_ => Count("0"))).CountVideoPacketsAsync(file)).Should().Be(0);
    }

    // serves-spec: SPEC-004#I40 - what it cannot read it does not claim: null, so the engine does not call the head empty.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task CountVideoPackets_IsNullWhenTheCountCannotBeRead()
    {
        var file = Placeholder("head.mp4");

        (await new MediaProbe(new ScriptedFfprobe(_ => Count("N/A"))).CountVideoPacketsAsync(file)).Should().BeNull();
        (await new MediaProbe(new ScriptedFfprobe(_ => "{}")).CountVideoPacketsAsync(file)).Should().BeNull();
        (await new MediaProbe(new ScriptedFfprobe(_ => "not json")).CountVideoPacketsAsync(file)).Should().BeNull();
        (await new MediaProbe(new FailingFfprobe()).CountVideoPacketsAsync(file)).Should().BeNull();
        (await new MediaProbe(new ScriptedFfprobe(_ => Count("45"))).CountVideoPacketsAsync(Path.Combine(_dir, "gone.mp4"))).Should().BeNull();
    }

    // ---- The engine with a frame probe (fakes) ---------------------------------------------------------------------------

    private static MediaInfo H264Aac(string container = "mov,mp4,m4a,3gp,3g2,mj2", bool video = true) => new(
        S(60), container,
        video ? new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/15360") } : Array.Empty<StreamInfo>(),
        new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

    private static IReadOnlyList<TimeSpan> Grid2s() => Enumerable.Range(0, 31).Select(i => S(i * 2)).ToList();

    /// <summary>A probe with the frame questions, scripted: the answer to the first, and the count for each head.</summary>
    private sealed class FrameProbe : IMediaProbe, IVideoFrameProbe
    {
        private readonly MediaInfo _info;
        private readonly IReadOnlyList<TimeSpan> _keyframes;

        public FrameProbe(MediaInfo info, IReadOnlyList<TimeSpan> keyframes, TimeSpan? firstFrame, int? headPackets = 45)
        {
            _info = info;
            _keyframes = keyframes;
            FirstFrame = firstFrame;
            HeadPackets = headPackets;
        }

        public TimeSpan? FirstFrame { get; set; }

        public int? HeadPackets { get; set; }

        public List<TimeSpan> SnapAsked { get; } = new();

        public int CountAsked { get; private set; }

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) => Task.FromResult(ProbeResult.Success(_info));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) => Task.FromResult(_keyframes);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
            throw new NotSupportedException("the smart cutter plans against the raw keyframes and never snaps");

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;

        public Task<TimeSpan?> FirstVideoFrameAtOrAfterAsync(string path, TimeSpan time, CancellationToken ct = default)
        {
            SnapAsked.Add(time);
            return Task.FromResult(FirstFrame);
        }

        public Task<int?> CountVideoPacketsAsync(string path, CancellationToken ct = default)
        {
            CountAsked++;
            return Task.FromResult(HeadPackets);
        }
    }

    private static string Arg(IReadOnlyList<string> tokens, string name) => tokens[tokens.ToList().IndexOf(name) + 1];

    // serves-spec: SPEC-001#I43 / SPEC-001#I44 - the head is built from the first frame at or after the request: -ss and -t,
    // the list's duration and the result's EffectiveStart all say 7.033333, not 7.018.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_StartsTheHeadOnTheFirstFrameAtOrAfterTheRequest()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(7.033333));
        var runner = new ListCapturingRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(7.018), null, Path.Combine(_dir, "cut.mp4"));

        probe.SnapAsked.Should().Equal(new[] { S(7.018) });
        result.FellBack.Should().BeFalse();
        result.EffectiveStart.Should().Be(S(7.033333), "the cut starts on the frame");
        result.ReencodedDuration.Should().Be(S(8) - S(7.033333), "the head runs from that frame to the keyframe");
        var head = runner.Commands[0];
        Arg(head, "-ss").Should().Be("7.033333");
        Arg(head, "-t").Should().Be("0.966667", "8 - 7.033333");
        var list = runner.Lists.Should().ContainSingle().Which.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        list[1].Should().Be("duration 0.966667", "the source leg starts exactly where the head's last frame ends");
        list[3].Should().Be("inpoint 8");
        probe.CountAsked.Should().Be(1, "the head is checked for a video frame");
    }

    // serves-spec: SPEC-001#I43 - a request already on a frame stays as it is.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_LeavesARequestThatIsOnAFrameAlone()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(7.5));
        var runner = new RecordingFakeRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(7.5), null, Path.Combine(_dir, "cut.mp4"));

        result.EffectiveStart.Should().Be(S(7.5));
        Arg(runner.Commands[0], "-ss").Should().Be("7.5");
    }

    // serves-spec: SPEC-001#I43 - a frame a hair BEFORE the request (inside the 1 ms tolerance) is the frame the cut starts on,
    // so -ss keeps it; the request would have dropped it.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_StartsOnAFrameThatSitsAHairBeforeTheRequest()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(7.0333));
        var runner = new RecordingFakeRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(7.0335), null, Path.Combine(_dir, "cut.mp4"));

        result.EffectiveStart.Should().Be(S(7.0333));
        Arg(runner.Commands[0], "-ss").Should().Be("7.0333");
    }

    // serves-spec: SPEC-001#I43 / SPEC-001#I40 - a request within a frame BEFORE a keyframe snaps onto the keyframe, which is
    // PureCopy: the lossless cut is exact there, so the engine falls back with zero ffmpeg runs (11.975 -> the keyframe at 12).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_WhenTheFirstFrameIsAKeyframe_FallsBackAsPureCopy_WithNoFfmpeg()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(12));
        var runner = new RecordingFakeRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(11.975), null, Path.Combine(_dir, "cut.mp4"));

        runner.Commands.Should().BeEmpty();
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Contain("keyframe");
        result.EffectiveStart.Should().BeNull();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I43 - a first frame at or past the requested END is no frame of the range: the request stands.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_DoesNotSnapPastTheEnd()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(7.5), headPackets: 1);
        var runner = new RecordingFakeRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(7.018), S(7.4), Path.Combine(_dir, "cut.mp4"));

        result.EffectiveStart.Should().Be(S(7.018), "the frame at 7.5 lies past the end 7.4");
    }

    // serves-spec: SPEC-001#I43 - nothing to ask (a probe without the frame questions, a file without video, or no answer):
    // the cut runs at the request exactly as before, and the head is not checked.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_WithoutAFrameAnswer_CutsAtTheRequest()
    {
        var input = Placeholder("clip.mp4");

        var noAnswer = new FrameProbe(H264Aac(), Grid2s(), firstFrame: null, headPackets: null);
        var runnerA = new RecordingFakeRunner();
        var a = await new SmartCutEngine(runnerA, noAnswer).CutAsync(input, S(7.018), null, Path.Combine(_dir, "a.mp4"));
        a.EffectiveStart.Should().Be(S(7.018));
        Arg(runnerA.Commands[0], "-ss").Should().Be("7.018");
        a.FellBack.Should().BeFalse("a head whose frames cannot be counted is not called empty");

        var audioOnly = new FrameProbe(H264Aac(video: false), Grid2s(), firstFrame: S(7.033333));
        var b = await new SmartCutEngine(new RecordingFakeRunner(), audioOnly).CutAsync(input, S(7.018), null, Path.Combine(_dir, "b.mp4"));
        audioOnly.SnapAsked.Should().BeEmpty("no video, no frame to snap to");
        audioOnly.CountAsked.Should().Be(0);
        b.EffectiveStart.Should().Be(S(7.018));
    }

    // serves-spec: SPEC-001#I46 - a source Exact refuses costs no frame probe either.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_OnARefusedSource_AsksNoFrameQuestion()
    {
        var input = Placeholder("clip.mkv");
        var probe = new FrameProbe(H264Aac("matroska,webm"), Grid2s(), firstFrame: S(7.033333));
        var runner = new RecordingFakeRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(7.018), null, Path.Combine(_dir, "cut.mkv"));

        result.FellBack.Should().BeTrue();
        probe.SnapAsked.Should().BeEmpty();
        runner.Commands.Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I46 - a head that holds no video frame is a fall back with a reason, not a Done: the concat takes
    // its streams from the head, so the final would have no picture. Nothing is left at the destination or in the temp dir.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_WhenTheHeadHoldsNoVideoFrame_FallsBackWithAReason_AndLeavesNothing()
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: null, headPackets: 0);
        var runner = new RecordingFakeRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(11.975), null, output);

        runner.Commands.Should().ContainSingle("the head ran, nothing after it");
        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Contain("no video frame");
        result.FallbackReason.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
        result.OutputPath.Should().BeNull();
        result.EffectiveStart.Should().BeNull();
        File.Exists(output).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
    }

    // serves-spec: SPEC-001#I46 - the same check on a FullReencode, where the head IS the output.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_WhenAFullReencodeHoldsNoVideoFrame_FallsBack()
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: null, headPackets: 0);

        var result = await new SmartCutEngine(new RecordingFakeRunner(), probe).CutAsync(input, S(9.01), S(9.02), output);

        result.Strategy.Should().Be(SmartCutStrategy.FullReencode);
        result.FellBack.Should().BeTrue();
        File.Exists(output).Should().BeFalse();
    }

    // ---- The container gate ---------------------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I45 / SPEC-001#I46 - Matroska and WebM (ffprobe names both "matroska,webm") fall back with a reason
    // that names the files, before the time base and the codecs: the head file there starts at -21 ms and shifts against
    // the copied part.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("matroska,webm", 0.0)]
    [InlineData("matroska,webm", -0.021)]
    [InlineData("Matroska,WebM", 0.0)]
    public void TryResolveEncoders_RefusesMatroskaAndWebm_WithAReasonThatNamesThem(string container, double startTime)
    {
        var info = new MediaInfo(
            S(30), container,
            new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/1000") },
            new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/1000") },
            S(startTime));

        SmartCutArgsBuilder.TryResolveEncoders(info, out var video, out var audio, out var why).Should().BeFalse();

        video.Should().BeNull();
        audio.Should().BeNull();
        why.Should().Be("frame-exact cutting is not supported yet for .mkv and .webm files");
        why.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
    }

    // serves-spec: SPEC-001#I45 - the MP4 family is untouched by the container gate.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2")]
    [InlineData("mp4")]
    [InlineData("avi")]
    public void TryResolveEncoders_StillResolvesTheOtherContainersAtZero(string container)
    {
        var info = new MediaInfo(
            S(30), container,
            new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
            new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

        SmartCutArgsBuilder.TryResolveEncoders(info, out var video, out _, out var why).Should().BeTrue();

        video.Should().Be("libx264");
        why.Should().BeNull();
    }

    // ---- The probe on real media -------------------------------------------------------------------------------------------

    private static FfmpegBinaryLocator Locator() => new(
        ffmpegOverride: FfmpegTestBinaries.Ffmpeg, ffprobeOverride: FfmpegTestBinaries.Ffprobe);

    // serves-spec: SPEC-004#I40 - the real ffprobe answers agree with the source's own packet list, at times that
    // sit on a frame, between two frames, just before a keyframe, deep in a 10 s GOP (ffprobe seeks back 7 s to its keyframe),
    // and past the last frame; and the count of an audio-only file is 0.
    [Trait("serves-spec", "SPEC-004")]
    [SkippableFact]
    public async Task TheRealProbe_AgreesWithThePacketList()
    {
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg");
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
        var probe = new MediaProbe(new FfprobeRunner(Locator()));
        var g60 = await SmartCutJointTiming.MakeSourceAsync(_dir, "g60.mp4", 10, 60);
        var g300 = await SmartCutJointTiming.MakeSourceAsync(_dir, "g300.mp4", 20, 300);

        foreach (var (file, time) in new[]
        {
            (g60, 0.0), (g60, 0.0035), (g60, 3.0), (g60, 3.0167), (g60, 3.018), (g60, 3.031), (g60, 3.9667), (g60, 3.975), (g60, 9.95),
            (g300, 17.3), (g300, 17.3001), (g300, 10.0), (g300, 19.9),
        })
        {
            var expected = await SmartCutJointTiming.FirstFrameAtOrAfterAsync(file, time);
            var actual = await probe.FirstVideoFrameAtOrAfterAsync(file, S(time));
            _output.WriteLine($"{Path.GetFileName(file)} @ {time.ToString(CultureInfo.InvariantCulture)} -> {actual?.TotalSeconds:0.######} (packet list: {expected:0.######})");
            actual.Should().NotBeNull();
            actual!.Value.TotalSeconds.Should().BeApproximately(expected, 1e-6);
        }

        (await probe.FirstVideoFrameAtOrAfterAsync(g60, S(50))).Should().BeNull("past the last frame");

        var audioOnly = Path.Combine(_dir, "audio.m4a");
        var run = await new FfmpegRunner(Locator()).RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error", "-i", g60, "-vn", "-c:a", "copy").Output(audioOnly));
        run.ExitCode.Should().Be(0, run.StdErrText);
        (await probe.CountVideoPacketsAsync(audioOnly)).Should().Be(0);
        (await probe.CountVideoPacketsAsync(g60)).Should().Be(300);
    }
}
