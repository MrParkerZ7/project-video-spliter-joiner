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

    /// <summary>Packets with both times: <c>(pts, dts)</c>, either null = key absent (ffprobe's JSON drops an N/A field).</summary>
    private static string PacketTimes(params (string? Pts, string? Dts)[] packets) =>
        "{\"packets\":[" + string.Join(",", packets.Select(p =>
            "{" + string.Join(",", new[]
            {
                p.Pts is null ? null : $"\"pts_time\":\"{p.Pts}\"",
                p.Dts is null ? null : $"\"dts_time\":\"{p.Dts}\"",
            }.Where(x => x is not null)) + "}")) + "]}";

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
        call.Should().ContainInOrder("-show_entries", "packet=pts_time,dts_time");
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

    // serves-spec: SPEC-004#I40 - the query asks for the decoding time as well (T-190, third pass).
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task FirstVideoFrameAtOrAfter_AsksForTheDecodingTimeToo()
    {
        var file = Placeholder("clip.avi");
        var ffprobe = new ScriptedFfprobe(_ => PacketTimes(("4.033333", "4.033333")));

        await new MediaProbe(ffprobe).FirstVideoFrameAtOrAfterAsync(file, S(4.018));

        ffprobe.Calls.Should().ContainSingle().Which.Should().ContainInOrder("-show_entries", "packet=pts_time,dts_time");
    }

    // serves-spec: SPEC-004#I40 - an AVI with B-frames lists about a third of its video packets without a pts, only a dts on the
    // 1/fps grid (pts == dts wherever both exist): the frame at 4.033333 has no pts, and skipping it picked 4.066667 for a
    // request at 4.018. The packet takes its dts, as the keyframe scan does.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task FirstVideoFrameAtOrAfter_UsesTheDecodingTime_OfAPacketWithoutAPts_WhenTheStreamShowsNoReordering()
    {
        var file = Placeholder("clip.avi");
        var ffprobe = new ScriptedFfprobe(_ => PacketTimes(
            (null, "3.966667"), ("4.000000", "4.000000"), (null, "4.033333"), ("4.066667", "4.066667"), ("4.100000", "4.100000")));

        var first = await new MediaProbe(ffprobe).FirstVideoFrameAtOrAfterAsync(file, S(4.018));

        first.Should().Be(S(4.033333));
    }

    // serves-spec: SPEC-004#I40 - but a packet without a pts in a stream that DOES show re-ordering (a packet whose pts and dts
    // differ) has no frame time to give: its dts is a decode time. The answer is null, never a decode time passed off as a
    // display time. A packet with no time at all is the same.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task FirstVideoFrameAtOrAfter_IsNull_WhenAPacketHasNoPts_AndTheStreamShowsReordering_OrNoTimeAtAll()
    {
        var file = Placeholder("clip.avi");

        var reordered = new ScriptedFfprobe(_ => PacketTimes(("4.100000", "4.033333"), (null, "4.066667"), ("4.133333", "4.100000")));
        (await new MediaProbe(reordered).FirstVideoFrameAtOrAfterAsync(file, S(4.018))).Should().BeNull();

        var noTime = new ScriptedFfprobe(_ => PacketTimes(("4.033333", "4.033333"), (null, null)));
        (await new MediaProbe(noTime).FirstVideoFrameAtOrAfterAsync(file, S(4.018))).Should().BeNull();
    }

    // serves-spec: SPEC-004#I40 - a normal re-ordered stream (every packet has both times, pts != dts) is untouched by the fallback.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task FirstVideoFrameAtOrAfter_StillUsesThePts_OfAStreamWithReordering()
    {
        var file = Placeholder("clip.mp4");
        var ffprobe = new ScriptedFfprobe(_ => PacketTimes(
            ("4.000000", "3.933333"), ("4.100000", "3.966667"), ("4.033333", "4.000000"), ("4.066667", "4.033333")));

        (await new MediaProbe(ffprobe).FirstVideoFrameAtOrAfterAsync(file, S(4.018))).Should().Be(S(4.033333));
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

    // ---- GetVideoFrameTimesAsync (fake ffprobe) -----------------------------------------------------------------------

    // serves-spec: SPEC-004#I40 - the frames of a window, ascending whatever the decode order, from a read that starts at the
    // keyframe before it: the packets before `from` and at or after `to` are dropped.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task GetVideoFrameTimes_ReturnsTheFramesOfTheWindow_Ascending()
    {
        var file = Placeholder("clip.mp4");
        // decode order of a closed GOP: 6.0 I, 6.1 P, 6.033333 B, 6.066667 B, then the next GOP 7.0 I ...
        var ffprobe = new ScriptedFfprobe(_ => Packets("6.000000", "6.100000", "6.033333", "6.066667", "7.000000", "7.100000", "7.066667", "7.033333"));

        var times = await new MediaProbe(ffprobe).GetVideoFrameTimesAsync(file, S(6.033333), S(7.066667));

        times.Should().Equal(S(6.033333), S(6.066667), S(6.1), S(7.0), S(7.033333));
    }

    // serves-spec: SPEC-004#I40 - the tolerance at both ends: 1 ms before `from` is in, 1 ms before `to` is out.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task GetVideoFrameTimes_CountsAFrameWithinAMillisecondBeforeTheStartAsIn_AndBeforeTheEndAsOut()
    {
        var file = Placeholder("clip.mp4");
        var ffprobe = new ScriptedFfprobe(_ => Packets("6.9995", "7.033333", "7.9995", "8.000000"));

        var times = await new MediaProbe(ffprobe).GetVideoFrameTimesAsync(file, S(7.0), S(8.0));

        times.Should().Equal(S(6.9995), S(7.033333));
        times.Should().NotContain(S(7.9995), "within 1 ms before `to` is at `to`, out of [from, to)");
    }

    // serves-spec: SPEC-004#I40 - the OPEN-GOP case the question exists for: the key frame that ends the window (8.0) is decoded
    // BEFORE the B-frames displayed ahead of it (7.933333, 7.966667), so a read that stopped at it would miss them. The read goes
    // 2 s past `to`, and the window then holds them.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task GetVideoFrameTimes_ReadsTwoSecondsPastTheEnd_SoLeadingPicturesOfAnOpenGopAreInTheWindow()
    {
        var file = Placeholder("og.mp4");
        var ffprobe = new ScriptedFfprobe(_ => Packets("7.866667", "7.900000", "8.000000", "7.933333", "7.966667", "8.100000", "8.033333", "8.066667"));
        var probe = new MediaProbe(ffprobe);

        var times = await probe.GetVideoFrameTimesAsync(file, S(7.9), S(8.0));

        times.Should().Equal(S(7.9), S(7.933333), S(7.966667));
        var call = ffprobe.Calls.Should().ContainSingle().Which;
        call[call.ToList().IndexOf("-read_intervals") + 1].Should().Be("7.9%10", "from the window start to two seconds past its end, both absolute");
        call.Should().ContainInOrder("-select_streams", "v:0").And.ContainInOrder("-show_entries", "packet=pts_time,dts_time");
    }

    // serves-spec: SPEC-004#I40 - a file with no video stream, or none in the window, is an EMPTY list (the engine reads it as "no
    // picture"); what cannot be read is null (the engine then does not call the head wrong): garbage, a probe error, a missing file,
    // a blank path, a negative start, a packet with no time at all.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task GetVideoFrameTimes_IsEmptyWithoutFrames_AndNullWhenItCannotSay()
    {
        var file = Placeholder("head.mp4");

        (await new MediaProbe(new ScriptedFfprobe(_ => "{}")).GetVideoFrameTimesAsync(file, S(0), S(1))).Should().NotBeNull().And.BeEmpty("no video stream");
        (await new MediaProbe(new ScriptedFfprobe(_ => Packets())).GetVideoFrameTimesAsync(file, S(0), S(1))).Should().NotBeNull().And.BeEmpty();
        (await new MediaProbe(new ScriptedFfprobe(_ => Packets("5.0", "5.5"))).GetVideoFrameTimesAsync(file, S(0), S(1))).Should().BeEmpty("none in the window");

        (await new MediaProbe(new ScriptedFfprobe(_ => "not json")).GetVideoFrameTimesAsync(file, S(0), S(1))).Should().BeNull();
        (await new MediaProbe(new FailingFfprobe()).GetVideoFrameTimesAsync(file, S(0), S(1))).Should().BeNull("ffprobe failed");
        (await new MediaProbe(new ScriptedFfprobe(_ => Packets("0.0"))).GetVideoFrameTimesAsync(Path.Combine(_dir, "gone.mp4"), S(0), S(1))).Should().BeNull("no file");
        (await new MediaProbe(new ScriptedFfprobe(_ => Packets("0.0"))).GetVideoFrameTimesAsync(" ", S(0), S(1))).Should().BeNull("blank path");
        (await new MediaProbe(new ScriptedFfprobe(_ => Packets("0.0"))).GetVideoFrameTimesAsync(file, S(-1), S(1))).Should().BeNull("negative start");
        (await new MediaProbe(new ScriptedFfprobe(_ => PacketTimes(("0.0", "0.0"), (null, null)))).GetVideoFrameTimesAsync(file, S(0), S(1))).Should().BeNull("a packet with no time");
    }

    // serves-spec: SPEC-004#I40 - the same pts/dts rules as the first question: a packet without a pts takes its dts while the
    // stream shows no re-ordering, and the answer is null once it does.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task GetVideoFrameTimes_UsesTheDecodingTimeOfAPacketWithoutAPts_OnlyWhileTheStreamShowsNoReordering()
    {
        var file = Placeholder("clip.avi");
        var plain = new ScriptedFfprobe(_ => PacketTimes(
            (null, "3.966667"), ("4.000000", "4.000000"), (null, "4.033333"), ("4.066667", "4.066667")));
        (await new MediaProbe(plain).GetVideoFrameTimesAsync(file, S(4.0), S(4.1))).Should().Equal(S(4.0), S(4.033333), S(4.066667));

        var reordered = new ScriptedFfprobe(_ => PacketTimes(("4.100000", "4.033333"), (null, "4.066667"), ("4.133333", "4.100000")));
        (await new MediaProbe(reordered).GetVideoFrameTimesAsync(file, S(4.0), S(4.2))).Should().BeNull();
    }

    // serves-spec: SPEC-004#I40 - a cancel is not swallowed with the probe errors.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task GetVideoFrameTimes_PropagatesACancellation()
    {
        var file = Placeholder("clip.mp4");

        var act = () => new MediaProbe(new OperationCancelledFfprobeRunner()).GetVideoFrameTimesAsync(file, S(0), S(1));

        await act.Should().ThrowAsync<OperationCanceledException>();
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

        public FrameProbe(MediaInfo info, IReadOnlyList<TimeSpan> keyframes, TimeSpan? firstFrame, int? headPackets = 45, int? sourceFrames = -1)
        {
            _info = info;
            _keyframes = keyframes;
            FirstFrame = firstFrame;
            HeadPackets = headPackets;
            SourceFrames = sourceFrames == -1 ? headPackets : sourceFrames; // by default the source holds as many frames as the head
        }

        public TimeSpan? FirstFrame { get; set; }

        /// <summary>How many frames the head file holds (null = the probe cannot say).</summary>
        public int? HeadPackets { get; set; }

        /// <summary>How many frames the source holds in the span the head was cut from (null = the probe cannot say).</summary>
        public int? SourceFrames { get; set; }

        /// <summary>The time of the head's first frame (a head starts at 0).</summary>
        public TimeSpan HeadFirst { get; set; } = TimeSpan.Zero;

        public List<TimeSpan> SnapAsked { get; } = new();

        /// <summary>How many times the HEAD file's frames were read.</summary>
        public int CountAsked { get; private set; }

        /// <summary>The windows asked of the SOURCE, as (from, to).</summary>
        public List<(TimeSpan From, TimeSpan To)> SourceAsked { get; } = new();

        /// <summary>The window asked of the head.</summary>
        public (TimeSpan From, TimeSpan To)? HeadAsked { get; private set; }

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

        public Task<IReadOnlyList<TimeSpan>?> GetVideoFrameTimesAsync(string path, TimeSpan from, TimeSpan to, CancellationToken ct = default)
        {
            // The engine names its head "head.<ext>" in its temp dir; anything else is the source.
            var isHead = Path.GetFileName(path).StartsWith("head.", StringComparison.Ordinal);
            if (isHead)
            {
                CountAsked++;
                HeadAsked = (from, to);
            }
            else
            {
                SourceAsked.Add((from, to));
            }

            var count = isHead ? HeadPackets : SourceFrames;
            IReadOnlyList<TimeSpan>? times = count is { } n
                ? Enumerable.Range(0, n).Select(i => (isHead ? HeadFirst : from) + S(i / 30.0)).ToList()
                : null;
            return Task.FromResult(times);
        }

        /// <summary>The answer to "is the keyframe at HeadEnd an IDR?" (T-223): true (a closed GOP) unless a test scripts another.</summary>
        public bool? IdrAnswer { get; set; } = true;

        /// <summary>The keyframe times the engine asked about.</summary>
        public List<TimeSpan> IdrAsked { get; } = new();

        /// <summary>A probe whose IDR question throws (a bug or an I/O error the real probe does not catch).</summary>
        public bool IdrThrows { get; set; }

        public Task<bool?> IsIdrKeyframeAsync(string path, TimeSpan keyframeTime, CancellationToken ct = default)
        {
            IdrAsked.Add(keyframeTime);
            return IdrThrows ? throw new InvalidOperationException("scripted") : Task.FromResult(IdrAnswer);
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
        probe.CountAsked.Should().Be(1, "the head is read for its frames");
        probe.HeadAsked.Should().Be((TimeSpan.Zero, S(8) - S(7.033333) + S(1)), "the head is read from its time 0 over its own length and a second more");
        probe.SourceAsked.Should().ContainSingle().Which.Should().Be((S(7.033333), S(8)), "the source over the span the head was cut from: [Start, HeadEnd)");
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

    // ---- T-223: the keyframe the head ends on must be an IDR (the probe's answer is scripted here) -------------------------

    private const string NotAnIdr = "frame-exact cutting is not supported yet for this video's keyframe type";

    private static MediaInfo Mpeg4Aac() => new(
        S(60), "mov,mp4,m4a,3gp,3g2,mj2",
        new[] { new StreamInfo(0, "mpeg4", "video", 1920, 1080, "yuv420p", null, null, "1/15360") },
        new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

    // serves-spec: SPEC-001#I70 - a keyframe that is not an IDR (false), or that the probe cannot classify (null), is a fall back with
    // the keyframe-type reason BEFORE anything is written or run: no ffmpeg, no temp dir, no head. Asked once, at HeadEnd (8 s).
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(false, false)]
    [InlineData(null, false)]
    [InlineData(true, true)]   // a probe that throws: it cannot say, which is the same
    public async Task SmartCutEngine_WhenTheHeadEndKeyframeIsNotProvablyAnIdr_FallsBackBeforeAnyRun(bool? answer, bool throws)
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "sub", "cut.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(7.5)) { IdrAnswer = answer, IdrThrows = throws };
        var runner = new RecordingFakeRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(7.5), null, output);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(NotAnIdr);
        result.FallbackReason.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
        result.OutputPath.Should().BeNull();
        result.ReencodedDuration.Should().Be(TimeSpan.Zero);
        runner.Commands.Should().BeEmpty("nothing is re-encoded for a joint that cannot be clean");
        probe.IdrAsked.Should().Equal(new[] { S(8) }, "asked once, about the keyframe the head ends on");
        File.Exists(output).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(output)).Should().BeFalse(
            "the question comes before anything is created: not the output folder, so not a temp dir the finally would sweep");
    }

    // serves-spec: SPEC-001#I70 / SPEC-001#I45 - an IDR keyframe (a closed GOP, the common case) cuts exactly as before.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_WhenTheHeadEndKeyframeIsAnIdr_CutsExactlyAsBefore()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(7.5)) { IdrAnswer = true };
        var runner = new ListCapturingRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(7.5), null, Path.Combine(_dir, "cut.mp4"));

        result.FellBack.Should().BeFalse(result.FallbackReason);
        runner.Commands.Should().HaveCount(2, "the head and the one concat");
        probe.IdrAsked.Should().Equal(new[] { S(8) });
    }

    // serves-spec: SPEC-001#I70 - the question is only about a JOINT on H.264: not asked when there is no copied part (a start on a
    // keyframe, or a start in the last GOP), nor for another codec (MPEG-4 part 2 is T-202's).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_AsksNoIdrQuestion_WhereThereIsNoJointOrNoH264()
    {
        var input = Placeholder("clip.mp4");

        var onKeyframe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(8)) { IdrAnswer = false };
        (await new SmartCutEngine(new RecordingFakeRunner(), onKeyframe).CutAsync(input, S(8), null, Path.Combine(_dir, "a.mp4")))
            .Strategy.Should().Be(SmartCutStrategy.PureCopy);
        onKeyframe.IdrAsked.Should().BeEmpty("a start on a keyframe is the lossless cut: no head, no joint");

        var lastGop = new FrameProbe(H264Aac(), Grid2s().Take(30).ToList(), firstFrame: S(59)) { IdrAnswer = false }; // the last keyframe is 58 s
        var lastResult = await new SmartCutEngine(new ListCapturingRunner(), lastGop).CutAsync(input, S(59), null, Path.Combine(_dir, "b.mp4"));
        lastResult.Strategy.Should().Be(SmartCutStrategy.FullReencode);
        lastResult.FallbackReason.Should().NotBe(NotAnIdr, "the head is the whole output: nothing is copied behind a keyframe");
        lastGop.IdrAsked.Should().BeEmpty();

        var mpeg4 = new FrameProbe(Mpeg4Aac(), Grid2s(), firstFrame: S(7.5)) { IdrAnswer = false };
        var mpegResult = await new SmartCutEngine(new ListCapturingRunner(), mpeg4).CutAsync(input, S(7.5), null, Path.Combine(_dir, "c.mp4"));
        mpegResult.FallbackReason.Should().NotBe(NotAnIdr);
        mpeg4.IdrAsked.Should().BeEmpty("IDR is an H.264 notion");
    }

    // ---- T-224: an end trim asks no frame question -------------------------------------------------------------------------

    // serves-spec: SPEC-001#I71 - a start on a keyframe with an end inside a GOP has no head and no joint: the frame snap, the IDR
    // question and the head's frame count are never asked, whatever the probe would answer (the kept part is the source's own packets
    // from the keyframe, as the lossless cut's is).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_AnEndTrim_AsksNoFrameQuestion()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(8.02)) { IdrAnswer = false };
        var runner = new ListCapturingRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(8), S(13), Path.Combine(_dir, "cut.mp4"));

        result.FellBack.Should().BeFalse(result.FallbackReason);
        runner.Commands.Should().ContainSingle();
        probe.SnapAsked.Should().BeEmpty("the keyframe is a frame already");
        probe.IdrAsked.Should().BeEmpty("no head, no joint, nothing to be an IDR for");
        probe.CountAsked.Should().Be(0);
        probe.SourceAsked.Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I71 / SPEC-001#I43 - a request a hair BEFORE a keyframe (11.975, the keyframe at 12) is snapped onto it by
    // the frame snap, which turns the plan into PureCopy; with an end inside a GOP that is an end trim from 12 s, not the lossless cut.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_ASnappedStartOnAKeyframe_WithAnEndInsideAGop_IsAnEndTrim()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(12)) { IdrAnswer = false };
        var runner = new ListCapturingRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(11.975), S(17.1), Path.Combine(_dir, "cut.mp4"));

        result.FellBack.Should().BeFalse(result.FallbackReason);
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        result.EffectiveStart.Should().Be(S(12));
        probe.SnapAsked.Should().Equal(new[] { S(11.975) });
        probe.IdrAsked.Should().BeEmpty();
        runner.Commands.Should().ContainSingle();
        runner.Lists.Should().ContainSingle().Which.Should().EndWith("inpoint 12\noutpoint 17.1\n");
    }

    // serves-spec: SPEC-001#I71 - the same snapped start with an end that is itself on a keyframe stays the quiet lossless fall back.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_ASnappedStartOnAKeyframe_WithAnEndOnAKeyframe_StaysTheLosslessFallBack()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(12));
        var runner = new RecordingFakeRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(11.975), S(18), Path.Combine(_dir, "cut.mp4"));

        result.FellBack.Should().BeTrue();
        result.EndSnapped.Should().BeFalse();
        result.FallbackReason.Should().Contain("keyframe");
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

    // ---- The head must be the source's frames (T-191) ----------------------------------------------------------------------

    private const string NotFrameExact = "the cut cannot be made frame-exact for this video yet";

    // serves-spec: SPEC-001#I46 - the input seek lands on the key frame by its decoding time; where that key frame has leading
    // pictures that need the GOP before it (an open GOP, MPEG-4 part 2 with B-frames) the head loses frames and the exit code is
    // still 0. A head with fewer frames than the source has in [Start, HeadEnd) - measured: 1 against 2 at Start = K-2 on an
    // x264 open-gop=1 file and on an MPEG-4 file - is a fall back, not a Done: no concat, nothing at the destination, no temp dir.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(44, 45)]   // one frame short
    [InlineData(1, 2)]     // the measured shape: 1 frame instead of 2
    [InlineData(46, 45)]   // one frame too many (a repeated frame)
    public async Task SmartCutEngine_WhenTheHeadHoldsADifferentNumberOfFramesThanTheSource_FallsBack(int headFrames, int sourceFrames)
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: null, headPackets: headFrames, sourceFrames: sourceFrames);
        var runner = new RecordingFakeRunner();
        var progress = new List<double>();

        var result = await new SmartCutEngine(runner, probe).CutAsync(
            input, S(7.5), null, output, new Progress<double>(progress.Add));

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(NotFrameExact);
        result.FallbackReason.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
        result.OutputPath.Should().BeNull();
        result.EffectiveStart.Should().BeNull();
        runner.Commands.Should().ContainSingle("the head ran, no concat after a head that is not the source's frames");
        File.Exists(output).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
        probe.SourceAsked.Should().ContainSingle().Which.Should().Be((S(7.5), S(8)), "the span the head was cut from: [Start, HeadEnd)");
    }

    // serves-spec: SPEC-001#I46 - the same number of frames is not enough when the first of them is late: a head whose first
    // frame is at 2 frames (66 ms) instead of 0 starts the final's video late against its audio.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(66.0)]
    [InlineData(34.0)]
    public async Task SmartCutEngine_WhenTheHeadsFirstFrameIsLate_FallsBack_EvenWithTheRightNumberOfFrames(double firstMs)
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: null, headPackets: 15, sourceFrames: 15)
        {
            HeadFirst = TimeSpan.FromMilliseconds(firstMs),
        };
        var runner = new RecordingFakeRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(7.5), null, output);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(NotFrameExact);
        runner.Commands.Should().ContainSingle();
        File.Exists(output).Should().BeFalse();
        probe.SourceAsked.Should().BeEmpty("the late start is enough; the source is not read");
    }

    // serves-spec: SPEC-001#I46 - a first frame within half a frame of 0 (the encoder's time base) is on time.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_AcceptsAHeadWhoseFirstFrameIsInsideHalfAFrame()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: null, headPackets: 15, sourceFrames: 15)
        {
            HeadFirst = TimeSpan.FromMilliseconds(0.1),
        };

        var result = await new SmartCutEngine(new RecordingFakeRunner(), probe).CutAsync(input, S(7.5), null, Path.Combine(_dir, "cut.mp4"));

        result.FellBack.Should().BeFalse();
    }

    // serves-spec: SPEC-001#I46 - what the probe cannot say is not held against the head: no answer for the source (an AVI whose
    // frame times cannot be told) or for the head leaves the cut as it was.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(15, null)]   // the source cannot be read
    [InlineData(null, 15)]   // the head cannot be read
    [InlineData(null, null)]
    public async Task SmartCutEngine_DoesNotCallAHeadWrong_ThatTheProbeCannotCount(int? headFrames, int? sourceFrames)
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: null, headPackets: headFrames, sourceFrames: sourceFrames);

        var result = await new SmartCutEngine(new RecordingFakeRunner(), probe).CutAsync(input, S(7.5), null, output);

        result.FellBack.Should().BeFalse();
        File.Exists(output).Should().BeTrue();
    }

    // serves-spec: SPEC-001#I46 - a head with NO frame keeps its own reason (the picture is gone), and the source is not read for it.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_AHeadWithNoFrame_KeepsItsOwnReason_AndTheSourceIsNotRead()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: null, headPackets: 0, sourceFrames: 2);

        var result = await new SmartCutEngine(new RecordingFakeRunner(), probe).CutAsync(input, S(7.5), null, Path.Combine(_dir, "cut.mp4"));

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(SmartCutEngine.NoPictureReason);
        probe.SourceAsked.Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I46 - the same end-to-end check on a FullReencode, where the head IS the output: the span is
    // [Start, End).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_AFullReencodeWhoseHeadIsNotTheSourcesFrames_FallsBack()
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: null, headPackets: 1, sourceFrames: 2);

        var result = await new SmartCutEngine(new RecordingFakeRunner(), probe).CutAsync(input, S(9.01), S(9.08), output);

        result.Strategy.Should().Be(SmartCutStrategy.FullReencode);
        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(NotFrameExact);
        File.Exists(output).Should().BeFalse();
        probe.SourceAsked.Should().ContainSingle().Which.Should().Be((S(9.01), S(9.08)));
    }

    // serves-spec: SPEC-001#I46 / SPEC-011#I164 - in a Bulk batch such a row is cut on the nearest keyframe by the lossless pass, and
    // says why in the engine's own words, after the run (the row cannot know before: it needs the head).
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ABulkRow_WhoseHeadIsNotTheSourcesFrames_IsCutLosslessly_WithTheReasonInItsWarning()
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: null, headPackets: 1, sourceFrames: 2);
        var split = new FakeSplitEngine();
        var smart = new SmartCutEngine(new RecordingFakeRunner(), probe);

        var result = await new VideoSplitJoiner.Core.Bulk.BulkTrimEngine(
                split, new FakeRequestBuilder(), new FakeDiskSpaceProbe(long.MaxValue), smart, null)
            .RunAsync(
                new[] { new VideoSplitJoiner.Core.Bulk.BulkTrimItem(input, S(7.5), null, Path.Combine(_dir, "clip_trimmed.mp4")) },
                new VideoSplitJoiner.Core.Bulk.BulkTrimOptions(Precision: VideoSplitJoiner.Core.Bulk.CutPrecision.Exact));

        split.CallCount.Should().Be(1, "the lossless pass took the row over");
        var row = result.Items.Single();
        row.Outcome.Should().Be(VideoSplitJoiner.Core.Bulk.ItemOutcome.Done);
        row.Warnings.Should().ContainSingle().Which.Should().Be(
            $"{VideoSplitJoiner.Core.Bulk.BulkTrimEngine.ExactFallbackPrefix} ({NotFrameExact}) - cut snapped to the nearest keyframe");
    }

    // ---- The composition (T-190, third pass) ------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I43 / SPEC-004#I40 - the production graph (the app builds `new MediaProbe(ffprobeRunner)` and hands that
    // same probe to `new SmartCutEngine(ffmpegRunner, probe)`) has the frame snap and the head check ACTIVE. `probe as
    // IVideoFrameProbe` makes a probe without the interface switch both off silently, so this is the guard that would fail.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheProductionGraph_HasTheFrameSnapAndTheHeadCheckActive()
    {
        var locator = Locator();
        var ffprobeRunner = new FfprobeRunner(locator);
        var ffmpegRunner = new FfmpegRunner(locator);
        var probe = new MediaProbe(ffprobeRunner);

        typeof(IVideoFrameProbe).IsAssignableFrom(typeof(MediaProbe)).Should().BeTrue("MediaProbe answers the frame questions");
        new SmartCutEngine(ffmpegRunner, probe).FrameQuestionsActive.Should().BeTrue();
    }

    // serves-spec: SPEC-001#I43 - the other side of the seam, so the guard above can fail: a probe without the interface leaves
    // the engine without the snap and the head check (it cuts at the request, as before the snap).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AProbeWithoutTheFrameInterface_LeavesTheEngineWithoutTheSnapAndTheHeadCheck()
    {
        var plain = new PlainProbe();

        typeof(IVideoFrameProbe).IsAssignableFrom(typeof(PlainProbe)).Should().BeFalse("precondition");
        new SmartCutEngine(new RecordingFakeRunner(), plain).FrameQuestionsActive.Should().BeFalse();
    }

    private sealed class PlainProbe : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) => throw new NotSupportedException();

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    // ---- The snap's reach (T-190, third pass) -----------------------------------------------------------------------------

    private static MediaInfo H264At(double? fps) => new(
        S(60), "mov,mp4,m4a,3gp,3g2,mj2",
        new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/15360", fps, fps) },
        new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

    // serves-spec: SPEC-001#I43 - the furthest the snap may move the start: three frame intervals of the first video stream's
    // frame rate, 250 ms when the rate is unknown or unusable.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(30.0, 100)]
    [InlineData(24.0, 125)]
    [InlineData(60.0, 50)]
    [InlineData(25.0, 120)]
    [InlineData(null, 250)]
    [InlineData(0.0, 250)]
    [InlineData(-5.0, 250)]
    [InlineData(double.NaN, 250)]
    [InlineData(double.PositiveInfinity, 250)]
    public void MaxSnapShift_IsThreeFrameIntervals_OrAQuarterSecondWhenTheRateIsUnknown(double? fps, double expectedMs)
    {
        SmartCutEngine.MaxSnapShift(H264At(fps)).TotalMilliseconds.Should().BeApproximately(expectedMs, 0.5);
    }

    // serves-spec: SPEC-001#I43 - no video stream, no rate: the unknown-rate bound.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void MaxSnapShift_WithoutAVideoStream_IsTheUnknownRateBound()
    {
        SmartCutEngine.MaxSnapShift(H264Aac(video: false)).Should().Be(TimeSpan.FromMilliseconds(250));
    }

    // serves-spec: SPEC-001#I43 / SPEC-001#I46 - a frame further away than the reach is a GAP in the picture, not the next frame:
    // the cut would start there and silently drop the audio in between. The engine falls back with a reason, runs nothing, and
    // leaves nothing behind - at 30 fps the reach is 100 ms: 99 ms is a snap, 110 ms is a fall back.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(7.099, true)]
    [InlineData(7.110, false)]
    [InlineData(8.5, false)]
    public async Task SmartCutEngine_FallsBack_WhenTheNextFrameIsBeyondTheSnapsReach(double firstFrame, bool snaps)
    {
        var input = Placeholder("clip.mp4");
        var output = Path.Combine(_dir, "cut.mp4");
        var probe = new FrameProbe(H264At(30), Grid2s(), firstFrame: S(firstFrame));
        var runner = new RecordingFakeRunner();

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(7.0), null, output);

        if (snaps)
        {
            result.FellBack.Should().BeFalse();
            result.EffectiveStart.Should().Be(S(firstFrame));
            return;
        }

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be("the video has no frame within a few frames after the requested time");
        result.FallbackReason.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
        result.Strategy.Should().NotBe(SmartCutStrategy.PureCopy, "BulkTrimEngine warns the row for any fall back that is not PureCopy");
        result.EffectiveStart.Should().BeNull();
        runner.Commands.Should().BeEmpty();
        File.Exists(output).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I43 - a source whose frame rate is unknown gets the quarter-second reach.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(7.2, true)]
    [InlineData(7.3, false)]
    public async Task SmartCutEngine_UsesAQuarterSecondReach_WhenTheFrameRateIsUnknown(double firstFrame, bool snaps)
    {
        var input = Placeholder("clip.mp4");
        var probe = new FrameProbe(H264Aac(), Grid2s(), firstFrame: S(firstFrame));

        var result = await new SmartCutEngine(new RecordingFakeRunner(), probe).CutAsync(input, S(7.0), null, Path.Combine(_dir, "cut.mp4"));

        result.FellBack.Should().Be(!snaps);
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

    // serves-spec: SPEC-001#I45 / SPEC-001#I46 - the gate reads FILE NAMES as well as the container (T-190, third pass): the head
    // and final take their container from the OUTPUT extension, so MP4 content named .mkv is refused by its name, and any source
    // cut into a .mkv/.webm/.mka output is refused by the output's. Matroska names get the Matroska sentence, transport-stream
    // names the sentence that names them; case does not matter; no name, an empty name and an extension-less name are fine.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("a.mkv", "frame-exact cutting is not supported yet for .mkv and .webm files")]
    [InlineData("a.WEBM", "frame-exact cutting is not supported yet for .mkv and .webm files")]
    [InlineData(@"C:\clips\a.b.Mka", "frame-exact cutting is not supported yet for .mkv and .webm files")]
    [InlineData("a.ts", "frame-exact cutting is not supported yet for .ts, .m2ts and .mts files or other files whose timestamps do not start at zero")]
    [InlineData("a.M2TS", "frame-exact cutting is not supported yet for .ts, .m2ts and .mts files or other files whose timestamps do not start at zero")]
    [InlineData("a.mts", "frame-exact cutting is not supported yet for .ts, .m2ts and .mts files or other files whose timestamps do not start at zero")]
    [InlineData("a.mp4", null)]
    [InlineData("a.m4v", null)]
    [InlineData("a.mov", null)]
    [InlineData("mkv", null)]
    [InlineData("a.mkv.mp4", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void UnsupportedFileNameReason_NamesTheMatroskaAndTransportStreamExtensions(string? path, string? expected)
    {
        SmartCutArgsBuilder.UnsupportedFileNameReason(path).Should().Be(expected);
    }

    // serves-spec: SPEC-001#I45 / SPEC-001#I46 - on an MP4-container source the names decide: the source's name, the output's
    // name, either; a codec Exact cannot reproduce is still reported as itself; no names = the container-level answer.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("a.mp4", "b.mp4", true)]
    [InlineData(null, null, true)]
    [InlineData("a.mkv", "b.mkv", false)]
    [InlineData("a.mkv", "b.mp4", false)]
    [InlineData("a.mp4", "b.mkv", false)]
    [InlineData("a.mp4", "b.webm", false)]
    [InlineData("a.mp4", "b.mka", false)]
    [InlineData("a.mp4", "b.ts", false)]
    [InlineData("a.mp4", "b.m2ts", false)]
    public void TryResolveEncoders_ReadsTheSourceAndOutputNames_OnAnMp4Container(string? source, string? output, bool resolves)
    {
        var info = new MediaInfo(
            S(30), "mov,mp4,m4a,3gp,3g2,mj2",
            new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
            new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

        SmartCutArgsBuilder.TryResolveEncoders(info, out var video, out var audio, out var why, source, output).Should().Be(resolves);

        if (resolves)
        {
            video.Should().Be("libx264");
            why.Should().BeNull();
        }
        else
        {
            video.Should().BeNull();
            audio.Should().BeNull();
            why.Should().NotBeNullOrWhiteSpace();
            why.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
        }
    }

    // serves-spec: SPEC-001#I46 - a codec Exact cannot reproduce wins over a Matroska NAME, as it does over the Matroska container.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TryResolveEncoders_ReportsAnUnmappableCodecBeforeAMatroskaName()
    {
        var info = new MediaInfo(
            S(30), "mov,mp4,m4a,3gp,3g2,mj2",
            new[] { new StreamInfo(0, "hevc", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
            Array.Empty<StreamInfo>());

        SmartCutArgsBuilder.TryResolveEncoders(info, out _, out _, out var why, "a.mkv", "b.mkv").Should().BeFalse();

        why.Should().Contain("HEVC");
    }

    // ---- The probe on real media -------------------------------------------------------------------------------------------

    private static FfmpegBinaryLocator Locator() => new(
        ffmpegOverride: FfmpegTestBinaries.Ffmpeg, ffprobeOverride: FfmpegTestBinaries.Ffprobe);

    // serves-spec: SPEC-004#I40 - on a REAL AVI with B-frames (about a third of its video packets have no pts) the probe agrees
    // with the 1/fps grid the source's own decoding times give, where reading pts alone skipped the frame at 4.033333.
    [Trait("serves-spec", "SPEC-004")]
    [SkippableFact]
    public async Task TheRealProbe_FindsTheFrameOnAnAviWhosePacketsHaveNoPts()
    {
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg");
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
        var avi = await SmartCutJointTiming.MakeAviWithoutPtsAsync(_dir, "bframes.avi");
        var missing = await SmartCutJointTiming.VideoPacketsWithoutPtsAsync(avi);
        _output.WriteLine($"{missing} of the AVI's video packets report no pts");
        missing.Should().BeGreaterThan(50, "precondition: the fixture must show the shape");
        var dts = (await SmartCutJointTiming.PacketsAsync(avi, "v:0")).Select(p => p.Dts).OrderBy(d => d).ToList();
        var probe = new MediaProbe(new FfprobeRunner(Locator()));

        foreach (var time in new[] { 4.018, 4.0, 4.031, 4.0334, 1.0, 0.01, 9.9 })
        {
            var expected = dts.First(d => d >= time - 0.001);
            var actual = await probe.FirstVideoFrameAtOrAfterAsync(avi, S(time));
            _output.WriteLine($"avi @ {time.ToString(CultureInfo.InvariantCulture)} -> {actual?.TotalSeconds:0.######} (decoding-time grid: {expected:0.######})");
            actual.Should().NotBeNull();
            actual!.Value.TotalSeconds.Should().BeApproximately(expected, 1e-6);
        }
    }

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
        (await probe.GetVideoFrameTimesAsync(audioOnly, S(0), S(10))).Should().NotBeNull().And.BeEmpty("an audio-only file has no frames");
        (await probe.GetVideoFrameTimesAsync(g60, S(0), S(10))).Should().HaveCount(300);

        // The window question against the same packet list: every window, whatever the keyframe it starts behind, holds exactly
        // the packets whose pts lies in it.
        var all = (await SmartCutJointTiming.PacketsAsync(g60, "v:0")).Select(p => p.Pts).OrderBy(x => x).ToList();
        foreach (var (from, to) in new[] { (0.0, 1.0), (3.0, 3.0667), (3.9667, 4.0), (3.9667, 4.0333), (4.0, 6.0), (9.5, 10.0), (2.5, 9.9) })
        {
            var expectedWindow = all.Where(t => t >= from - 0.001 && t < to - 0.001).ToList();
            var actualWindow = await probe.GetVideoFrameTimesAsync(g60, S(from), S(to));
            _output.WriteLine($"g60 [{from}, {to}) -> {actualWindow?.Count} frames (packet list: {expectedWindow.Count})");
            actualWindow.Should().NotBeNull();
            actualWindow!.Select(t => t.TotalSeconds).Should().Equal(expectedWindow, (a, b) => Math.Abs(a - b) < 1e-6);
        }
    }
}
