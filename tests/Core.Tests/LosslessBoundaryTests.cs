using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-219, binary-free: the copy commands' cut-time arguments (SPEC-001 I14, I16, I17), the split engine's conversion
/// of the plan's source timestamps to ffmpeg's time base (SPEC-001 I67), and the probe fields it reads (SPEC-004
/// I37-I39). The real-ffmpeg proof that the parts land on their keyframes is
/// <see cref="LosslessBoundaryIntegrationTests"/> (SPEC-001 I66).
/// </summary>
public sealed class LosslessBoundaryTests
{
    private const string In = @"C:\in.ts";
    private const string Out = @"C:\out\seg.ts";

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static string ValueAfter(IReadOnlyList<string> tokens, string flag)
    {
        var i = tokens.ToList().IndexOf(flag);
        i.Should().BeGreaterThanOrEqualTo(0, $"the command must carry {flag}");
        return tokens[i + 1];
    }

    // ---- SplitArgsBuilder.PerSegment: -ss is an offset from the format start_time (SPEC-001 I16/I17) ----------

    // serves-spec: SPEC-001#I16 — the measured TS value: start_time 1.445333 s, keyframe 5.466667 s → -ss 4.021334.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_SourceStartTime_SeeksToTheKeyframeMinusTheStartTime()
    {
        var tokens = SplitArgsBuilder.PerSegment(In, S(5.466667), S(7.466667), S(1.445333), Out).ToList();

        ValueAfter(tokens, "-ss").Should().Be("4.021334");
        ValueAfter(tokens, "-to").Should().Be("2", "-to stays the part's length, end − start, when the seek is not clamped");
        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    // serves-spec: SPEC-001#I16 — a zero start time emits exactly the pre-T-219 command.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_ZeroStartTime_EmitsTodaysTokens()
    {
        var tokens = SplitArgsBuilder.PerSegment(@"C:\in.mp4", S(3), S(6), TimeSpan.Zero, @"C:\out\seg.mp4").ToList();

        tokens.Should().Equal(
            "-hide_banner", "-nostdin", "-y", "-ss", "3", "-i", @"C:\in.mp4", "-to", "3",
            "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", @"C:\out\seg.mp4");
    }

    // serves-spec: SPEC-001#I16/I17 (boundary) — a part planned from 0 on a TS: start − start_time < 0 clamps to -ss 0
    // (the file's start), and -to is measured from that seek point, so the part does not run start_time past its end.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_StartBeforeTheStartTime_ClampsTheSeekToZero_AndMeasuresToFromTheFileStart()
    {
        var tokens = SplitArgsBuilder.PerSegment(In, TimeSpan.Zero, S(3.466667), S(1.443444), Out).ToList();

        ValueAfter(tokens, "-ss").Should().Be("0");
        ValueAfter(tokens, "-to").Should().Be("2.023223", "the seek point is the file's start, 1.443444 s");
    }

    // serves-spec: SPEC-001#I17 (boundary) — an end at or before the clamped seek point gives -to 0, never negative.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_EndBeforeTheClampedSeekPoint_ClampsToToZero()
    {
        var tokens = SplitArgsBuilder.PerSegment(In, TimeSpan.Zero, S(1.0), S(1.443444), Out).ToList();

        ValueAfter(tokens, "-ss").Should().Be("0");
        ValueAfter(tokens, "-to").Should().Be("0");
    }

    // serves-spec: SPEC-001#I16 (boundary) — a negative format start (an MP4 whose edit list starts before 0) moves the
    // offset the other way: ffmpeg seeks to offset + start_time, which is the keyframe again.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_NegativeStartTime_AddsItsMagnitudeToTheOffset()
    {
        var tokens = SplitArgsBuilder.PerSegment(@"C:\in.mp4", S(3), S(6), S(-0.1), @"C:\out\seg.mp4").ToList();

        ValueAfter(tokens, "-ss").Should().Be("3.1");
        ValueAfter(tokens, "-to").Should().Be("3");
    }

    // serves-spec: SPEC-001#I18 — the final part keeps no -to with a start time, and its seek is converted.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_FinalPart_WithStartTime_OmitsTo_AndConvertsTheSeek()
    {
        var tokens = SplitArgsBuilder.PerSegment(In, S(5.466667), null, S(1.445333), Out).ToList();

        tokens.Should().NotContain("-to");
        ValueAfter(tokens, "-ss").Should().Be("4.021334");
        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    // ---- SplitArgsBuilder.SegmentMuxer: the half-frame tolerance (SPEC-001 I14) ---------------------------------

    // serves-spec: SPEC-001#I14 — the delta is emitted as given, formatted by ToFfmpegSeconds, right after the
    // segment times; the copy invariant is unchanged.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void SegmentMuxer_WithDelta_EmitsSegmentTimeDeltaAfterTheSegmentTimes()
    {
        var tokens = SplitArgsBuilder.SegmentMuxer(
            @"C:\in.mp4", new[] { S(3), S(6) }, @"C:\out\part%03d.mp4", S(0.016683)).ToList();

        var list = tokens.ToList();
        var times = list.IndexOf("-segment_times");
        list[times + 1].Should().Be("3,6");
        list[times + 2].Should().Be("-segment_time_delta");
        list[times + 3].Should().Be("0.016683");
        list[times + 4].Should().Be("-reset_timestamps");
        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    // serves-spec: SPEC-001#I14 — a zero delta (unknown frame rate) emits no -segment_time_delta: the pre-T-219 command.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void SegmentMuxer_ZeroDelta_EmitsTodaysTokens()
    {
        var tokens = SplitArgsBuilder.SegmentMuxer(
            @"C:\in.mp4", new[] { S(3), S(6) }, @"C:\out\part%03d.mp4", TimeSpan.Zero).ToList();

        tokens.Should().Equal(
            "-hide_banner", "-nostdin", "-y", "-i", @"C:\in.mp4", "-map", "0", "-c", "copy", "-f", "segment",
            "-segment_times", "3,6", "-reset_timestamps", "1", @"C:\out\part%03d.mp4");
    }

    // serves-spec: SPEC-001#I14 — a negative delta is a caller bug.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void SegmentMuxer_NegativeDelta_Throws()
    {
        var act = () => SplitArgsBuilder.SegmentMuxer(@"C:\in.mp4", new[] { S(3) }, @"C:\out\part%03d.mp4", S(-0.01));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- SplitEngine's pure conversions (SPEC-001 I67) ------------------------------------------------------------

    // serves-spec: SPEC-001#I67 — d = 1 / (2 × frame rate), formatted as ffmpeg sees it.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(30000.0 / 1001.0, "0.016683")]
    [InlineData(30.0, "0.016667")]
    [InlineData(25.0, "0.02")]
    [InlineData(60.0, "0.008333")]
    public void HalfFrameDelta_IsHalfTheFrameDuration(double fps, string expected)
    {
        SplitPlanner.ToFfmpegSeconds(SplitEngine.HalfFrameDelta(fps)).Should().Be(expected);
    }

    // serves-spec: SPEC-001#I67 — an unknown or unusable frame rate gives no delta (today's command).
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-30.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void HalfFrameDelta_UnknownFrameRate_IsZero(double? fps)
    {
        SplitEngine.HalfFrameDelta(fps).Should().Be(TimeSpan.Zero);
    }

    // serves-spec: SPEC-001#I67 — the muxer's boundaries are keyframe − video start, clamped at 0; a zero start leaves
    // the plan's list unchanged.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ToMuxerSegmentTimes_SubtractsTheVideoStart_AndClampsAtZero()
    {
        SplitEngine.ToMuxerSegmentTimes(new[] { S(3), S(6) }, TimeSpan.Zero).Should().Equal(S(3), S(6));
        SplitPlanner.ToSegmentTimes(SplitEngine.ToMuxerSegmentTimes(new[] { S(3.466667), S(5.466667) }, S(1.466667)))
            .Should().Be("2,4");
        SplitEngine.ToMuxerSegmentTimes(new[] { S(1.0), S(5.0) }, S(1.466667))[0].Should().Be(TimeSpan.Zero);

        var act = () => SplitEngine.ToMuxerSegmentTimes(null!, TimeSpan.Zero);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- SplitEngine, fake runner and fake probe (SPEC-001 I67) ---------------------------------------------------

    private static readonly IReadOnlyList<TimeSpan> TsKeyframes =
        new[] { 1.466667, 3.466667, 5.466667, 7.466667, 9.466667 }.Select(S).ToList();

    private static StreamInfo Video(double? avg = null, double? r = null, TimeSpan? start = null) =>
        new(0, "h264", "video", 320, 240, "yuv420p", null, null, "1/90000", avg, r, start);

    private static StreamInfo Audio(TimeSpan? start = null) =>
        new(1, "aac", "audio", null, null, null, 44100, 2, "1/44100", null, null, start);

    private static MediaInfo Info(TimeSpan duration, TimeSpan startTime, params StreamInfo[] streams) =>
        new(duration, "mpegts", streams.Where(s => s.IsVideo).ToList(), streams.Where(s => s.IsAudio).ToList(), startTime);

    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Dir = Path.Combine(Path.GetTempPath(), "vsj-t219-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Input = Path.Combine(Dir, "src.ts");
            File.WriteAllText(Input, "placeholder");
            OutDir = Path.Combine(Dir, "out");
            Directory.CreateDirectory(OutDir);
        }

        public string Dir { get; }

        public string Input { get; }

        public string OutDir { get; }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async Task<RecordingTimingRunner> FullSplitAsync(MediaInfo info, IReadOnlyList<TimeSpan> keyframes, params double[] cuts)
    {
        using var ws = new Workspace();
        var runner = new RecordingTimingRunner();
        await new SplitEngine(runner, new FixedInfoProbe(info, keyframes)).SplitAsync(
            new SplitRequest(ws.Input, cuts.Select(S).ToList(), ws.OutDir));
        return runner;
    }

    // serves-spec: SPEC-001#I67 — a full split passes half the video stream's frame duration to the muxer.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(30000.0 / 1001.0, "0.016683")]
    [InlineData(30.0, "0.016667")]
    [InlineData(25.0, "0.02")]
    public async Task FullSplit_EmitsHalfTheVideoFrameDuration(double fps, string expected)
    {
        var info = Info(S(30), TimeSpan.Zero, Video(avg: fps, start: TimeSpan.Zero), Audio(TimeSpan.Zero));
        var keyframes = Enumerable.Range(0, 31).Select(i => S(i)).ToList();

        var runner = await FullSplitAsync(info, keyframes, 10, 20);

        var tokens = runner.Runs.Single().Tokens;
        ValueAfter(tokens, "-segment_time_delta").Should().Be(expected);
        ValueAfter(tokens, "-segment_times").Should().Be("10,20", "a zero video start leaves the plan's times unchanged");
        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    // serves-spec: SPEC-001#I67 — avg_frame_rate unknown (0/0) falls back to r_frame_rate.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task FullSplit_AvgFrameRateUnknown_UsesTheRFrameRate()
    {
        var info = Info(S(30), TimeSpan.Zero, Video(avg: null, r: 25, start: TimeSpan.Zero));
        var keyframes = Enumerable.Range(0, 31).Select(i => S(i)).ToList();

        var runner = await FullSplitAsync(info, keyframes, 10, 20);

        ValueAfter(runner.Runs.Single().Tokens, "-segment_time_delta").Should().Be("0.02");
    }

    // serves-spec: SPEC-001#I67 — no usable frame rate (or no video stream): no delta token, today's command.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task FullSplit_UnknownFrameRate_OrNoVideo_EmitsNoDelta()
    {
        var keyframes = Enumerable.Range(0, 31).Select(i => S(i)).ToList();

        var unknown = await FullSplitAsync(Info(S(30), TimeSpan.Zero, Video(start: TimeSpan.Zero)), keyframes, 10, 20);
        var audioOnly = await FullSplitAsync(Info(S(30), TimeSpan.Zero, Audio(TimeSpan.Zero)), keyframes, 10, 20);

        foreach (var runner in new[] { unknown, audioOnly })
        {
            var tokens = runner.Runs.Single().Tokens;
            tokens.Should().NotContain("-segment_time_delta");
            ValueAfter(tokens, "-segment_times").Should().Be("10,20");
        }
    }

    // serves-spec: SPEC-001#I67 — on a TS the muxer counts from the VIDEO stream's start (1.466667 s), not the format's
    // (1.443444 s, the audio's): keyframes 3.466667 and 5.466667 become 2 and 4.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task FullSplit_OnTs_ConvertsTheSegmentTimesByTheVideoStart()
    {
        var info = Info(S(10.023223), S(1.443444), Video(avg: 30, start: S(1.466667)), Audio(S(1.443444)));

        var runner = await FullSplitAsync(info, TsKeyframes, 3.5, 5.5);

        ValueAfter(runner.Runs.Single().Tokens, "-segment_times").Should().Be("2,4");
    }

    // serves-spec: SPEC-001#I67 (boundary) — a file with no video stream converts by the format start, with no delta.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task FullSplit_NoVideoStream_ConvertsByTheFormatStart()
    {
        var info = Info(S(10.023223), S(1.443444), Audio(S(1.443444)));

        var runner = await FullSplitAsync(info, TsKeyframes, 3.5, 5.5);

        var tokens = runner.Runs.Single().Tokens;
        ValueAfter(tokens, "-segment_times").Should().Be("2.023223,4.023223");
        tokens.Should().NotContain("-segment_time_delta");
    }

    // serves-spec: SPEC-001#I67 + I58 — with a format StartTime of 1.445333 s and no video start, the muxer's times
    // are the keyframes minus 1.445333, and the per-part progress boundaries are that same list: a runner time= just
    // past the first converted boundary (2.021334 s) reports part 2, not part 1 (the unconverted 3.466667 s would).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task FullSplit_OnTs_PartProgressBoundaries_AreTheConvertedSegmentTimes()
    {
        using var ws = new Workspace();
        var duration = S(10.023223);
        var info = Info(duration, S(1.445333), Video(avg: 30));
        double At(double seconds) => seconds / duration.TotalSeconds;

        // Just before / just after each converted boundary (2.021334 and 4.021334).
        var runner = new RecordingTimingRunner(At(2.0), At(2.05), At(4.0), At(4.05));
        var samples = new List<PartProgress>();
        await new SplitEngine(runner, new FixedInfoProbe(info, TsKeyframes)).SplitAsync(
            new SplitRequest(ws.Input, new[] { S(3.5), S(5.5) }, ws.OutDir),
            progress: null,
            ct: default,
            status: null,
            partProgress: new CollectingProgress<PartProgress>(samples.Add));

        ValueAfter(runner.Runs.Single().Tokens, "-segment_times").Should().Be("2.021334,4.021334");
        samples.Take(4).Select(p => p.PartIndex).Should().Equal(new[] { 1, 2, 2, 3 },
            "the PartAt boundaries are the converted -segment_times, token for token");
        samples[^1].Should().Be(new PartProgress(3, 3, 1.0));
    }

    // serves-spec: SPEC-001#I67 — the per-segment path converts its seek by the FORMAT start (ffmpeg's -ss counts from
    // it), not the video's, and measures each run's progress length from its seek point.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(3, "4.023223", null, 1.443444 + 10.023223 - 5.466667)] // the final part: to the file's last timestamp
    [InlineData(2, "2.023223", "2", 2.0)]                                // an interior part: end − start
    [InlineData(1, "0", "2.023223", 3.466667 - 1.443444)]               // planned from 0: clamped to the file's start
    public async Task Subset_OnTs_SeeksFromTheFormatStart_AndReportsTheRunLengthFromTheSeekPoint(
        int part, string expectedSs, string? expectedTo, double expectedRunSeconds)
    {
        using var ws = new Workspace();
        var info = Info(S(10.023223), S(1.443444), Video(avg: 30, start: S(1.466667)), Audio(S(1.443444)));
        var runner = new RecordingTimingRunner();

        await new SplitEngine(runner, new FixedInfoProbe(info, TsKeyframes)).SplitAsync(
            new SplitRequest(ws.Input, new[] { S(3.5), S(5.5) }, ws.OutDir, SelectedSegmentIndices: new[] { part }));

        var run = runner.Runs.Single();
        ValueAfter(run.Tokens, "-ss").Should().Be(expectedSs);
        if (expectedTo is null)
        {
            run.Tokens.Should().NotContain("-to");
        }
        else
        {
            ValueAfter(run.Tokens, "-to").Should().Be(expectedTo);
        }

        run.TotalDuration.Should().NotBeNull();
        run.TotalDuration!.Value.TotalSeconds.Should().BeApproximately(expectedRunSeconds, 1e-6);
        SplitArgsBuilder.SatisfiesCopyInvariant(run.Tokens).Should().BeTrue();
    }

    // serves-spec: SPEC-001#I67 — a zero start time keeps the per-segment commands and run lengths of today.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_ZeroStartTime_KeepsTodaysSeekAndRunLength()
    {
        using var ws = new Workspace();
        var info = Info(S(30), TimeSpan.Zero, Video(avg: 30, start: TimeSpan.Zero));
        var runner = new RecordingTimingRunner();

        await new SplitEngine(runner, new FixedInfoProbe(info, Enumerable.Range(0, 31).Select(i => S(i)).ToList())).SplitAsync(
            new SplitRequest(ws.Input, new[] { S(10), S(20) }, ws.OutDir, SelectedSegmentIndices: new[] { 2, 3 }));

        runner.Runs.Should().HaveCount(2);
        ValueAfter(runner.Runs[0].Tokens, "-ss").Should().Be("10");
        ValueAfter(runner.Runs[0].Tokens, "-to").Should().Be("10");
        runner.Runs[0].TotalDuration.Should().Be(S(10));
        ValueAfter(runner.Runs[1].Tokens, "-ss").Should().Be("20");
        runner.Runs[1].Tokens.Should().NotContain("-to");
        runner.Runs[1].TotalDuration.Should().Be(S(10));
    }

    // ---- MediaProbe mapping, canned JSON (SPEC-004 I37-I39) -------------------------------------------------------

    private static async Task<MediaInfo> ProbeJsonAsync(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), "vsj-t219-probe-" + Guid.NewGuid().ToString("N") + ".ts");
        await File.WriteAllTextAsync(file, "placeholder");
        try
        {
            var result = await new MediaProbe(new FakeFfprobeRunner(json)).ProbeAsync(file);
            return result.Should().BeOfType<ProbeResult.ProbeSucceeded>().Which.Info;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string Json(string formatExtra, string videoExtra, string audioExtra = "") => $$$"""
        {"streams":[
          {"index":0,"codec_name":"h264","codec_type":"video","width":320,"height":240,"pix_fmt":"yuv420p","time_base":"1/90000"{{{videoExtra}}}},
          {"index":1,"codec_name":"aac","codec_type":"audio","sample_rate":"44100","channels":2,"time_base":"1/44100"{{{audioExtra}}}}
        ],"format":{"format_name":"mpegts","duration":"10.023223"{{{formatExtra}}}}}
        """;

    // serves-spec: SPEC-004#I37 — format.start_time maps to MediaInfo.StartTime.
    [Trait("serves-spec", "SPEC-004")]
    [Theory]
    [InlineData(",\"start_time\":\"1.400000\"", 1.4)]
    [InlineData(",\"start_time\":\"1.443444\"", 1.443444)]
    [InlineData(",\"start_time\":\"-0.033333\"", -0.033333)]
    public async Task Probe_FormatStartTime_MapsToStartTime(string formatExtra, double expected)
    {
        var info = await ProbeJsonAsync(Json(formatExtra, ""));

        info.StartTime.TotalSeconds.Should().BeApproximately(expected, 1e-6);
    }

    // serves-spec: SPEC-004#I37 — an absent, N/A or unparsable start_time maps to zero (today's time base).
    [Trait("serves-spec", "SPEC-004")]
    [Theory]
    [InlineData("")]
    [InlineData(",\"start_time\":\"N/A\"")]
    [InlineData(",\"start_time\":\"abc\"")]
    [InlineData(",\"start_time\":\"\"")]
    public async Task Probe_FormatStartTime_AbsentOrUnparsable_MapsToZero(string formatExtra)
    {
        var info = await ProbeJsonAsync(Json(formatExtra, ""));

        info.StartTime.Should().Be(TimeSpan.Zero);
    }

    // serves-spec: SPEC-004#I38 — avg_frame_rate "30000/1001" maps to 29.97003 fps, and FrameRate takes it.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task Probe_AvgFrameRate_Ntsc_MapsTo29_97003()
    {
        var info = await ProbeJsonAsync(Json("", ",\"avg_frame_rate\":\"30000/1001\",\"r_frame_rate\":\"60000/1001\""));

        var v = info.VideoStreams.Single();
        v.AvgFrameRate.Should().BeApproximately(29.97003, 1e-5);
        v.RFrameRate.Should().BeApproximately(59.94006, 1e-5);
        v.FrameRate.Should().BeApproximately(29.97003, 1e-5, "avg_frame_rate wins when both are known");
    }

    // serves-spec: SPEC-004#I38 — avg_frame_rate 0/0 is unknown, so FrameRate falls back to r_frame_rate.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task Probe_AvgFrameRateZeroOverZero_FallsBackToRFrameRate()
    {
        var info = await ProbeJsonAsync(Json("", ",\"avg_frame_rate\":\"0/0\",\"r_frame_rate\":\"30/1\""));

        var v = info.VideoStreams.Single();
        v.AvgFrameRate.Should().BeNull();
        v.RFrameRate.Should().Be(30);
        v.FrameRate.Should().Be(30);
    }

    // serves-spec: SPEC-004#I38 — both missing, or both 0/0, is an unknown frame rate (null); audio rates map to null.
    [Trait("serves-spec", "SPEC-004")]
    [Theory]
    [InlineData("")]
    [InlineData(",\"avg_frame_rate\":\"0/0\",\"r_frame_rate\":\"0/0\"")]
    public async Task Probe_FrameRateUnknown_IsNull_AndAudioRatesAreNull(string videoExtra)
    {
        var info = await ProbeJsonAsync(Json("", videoExtra, ",\"avg_frame_rate\":\"0/0\",\"r_frame_rate\":\"0/0\""));

        info.VideoStreams.Single().FrameRate.Should().BeNull();
        var a = info.AudioStreams.Single();
        a.AvgFrameRate.Should().BeNull();
        a.RFrameRate.Should().BeNull();
        a.FrameRate.Should().BeNull();
    }

    // serves-spec: SPEC-004#I38 — the rational parser's whole range.
    [Trait("serves-spec", "SPEC-004")]
    [Theory]
    [InlineData("30000/1001", 29.97003)]
    [InlineData("30/1", 30.0)]
    [InlineData("25", 25.0)]
    [InlineData("24000/1001", 23.976024)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("N/A", null)]
    [InlineData("0/0", null)]
    [InlineData("30/0", null)]
    [InlineData("0/1", null)]
    [InlineData("-30/1", null)]
    [InlineData("abc", null)]
    [InlineData("30/x", null)]
    [InlineData("1e400/1", null)]
    public void ParseFrameRate_ParsesRationals_AndRejectsUnknownOrBadOnes(string? raw, double? expected)
    {
        var fps = MediaProbe.ParseFrameRate(raw);

        if (expected is null)
        {
            fps.Should().BeNull();
        }
        else
        {
            fps.Should().BeApproximately(expected.Value, 1e-5);
        }
    }

    // serves-spec: SPEC-004#I39 — each stream's start_time maps to StreamInfo.StartTime; absent or N/A is null.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task Probe_StreamStartTime_MapsPerStream_NullWhenAbsentOrNa()
    {
        var both = await ProbeJsonAsync(Json(
            ",\"start_time\":\"1.443444\"", ",\"start_time\":\"1.466667\"", ",\"start_time\":\"1.443444\""));
        both.VideoStreams.Single().StartTime!.Value.TotalSeconds.Should().BeApproximately(1.466667, 1e-6);
        both.AudioStreams.Single().StartTime!.Value.TotalSeconds.Should().BeApproximately(1.443444, 1e-6);
        both.StartTime.TotalSeconds.Should().BeApproximately(1.443444, 1e-6, "the format's start is the earliest stream's");

        var none = await ProbeJsonAsync(Json("", "", ",\"start_time\":\"N/A\""));
        none.VideoStreams.Single().StartTime.Should().BeNull();
        none.AudioStreams.Single().StartTime.Should().BeNull();
    }

    // ---- The new fields are not Join compatibility fields (SPEC-004 I38/I39) -------------------------------------

    // serves-spec: SPEC-004#I38 — two clips that differ only in frame rate (and stream start) are compatible.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public void CompatChecker_FrameRateOrStartTimeOnlyDifference_IsNoMismatch()
    {
        var a = new MediaInfo(S(5), "mov,mp4,m4a,3gp,3g2,mj2", new[] { Video(avg: 30, r: 30, start: TimeSpan.Zero) },
            new[] { Audio(TimeSpan.Zero) });
        var b = new MediaInfo(S(5), "mov,mp4,m4a,3gp,3g2,mj2", new[] { Video(avg: 30000.0 / 1001, r: 60, start: S(0.021029)) },
            new[] { Audio(S(0.000001)) }, S(0.5));

        var report = CompatChecker.Compare(new[] { a, b });

        report.Compatible.Should().BeTrue();
        report.Mismatches.Should().BeEmpty();
    }

    // serves-spec: SPEC-004#I38 — FrameRate is AvgFrameRate, else RFrameRate, else null; the defaults keep every
    // pre-T-219 construction compiling with an unknown rate and no start.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public void StreamInfo_FrameRate_IsAvgThenR_AndTheNewFieldsDefaultToUnknown()
    {
        Video(avg: 25, r: 50).FrameRate.Should().Be(25);
        Video(avg: null, r: 50).FrameRate.Should().Be(50);
        Video().FrameRate.Should().BeNull();

        var legacy = new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30");
        legacy.AvgFrameRate.Should().BeNull();
        legacy.RFrameRate.Should().BeNull();
        legacy.StartTime.Should().BeNull();
        new MediaInfo(S(1), "mp4", new[] { legacy }, Array.Empty<StreamInfo>()).StartTime.Should().Be(TimeSpan.Zero);
    }
}

/// <summary>A probe that returns one fixed <see cref="MediaInfo"/> and keyframe list (real snapping), no binary.</summary>
internal sealed class FixedInfoProbe : IMediaProbe
{
    private readonly MediaInfo _info;
    private readonly IReadOnlyList<TimeSpan> _keyframes;
    private readonly MediaProbe _real = new(new FakeFfprobeRunner("{}"));

    public FixedInfoProbe(MediaInfo info, IReadOnlyList<TimeSpan> keyframes)
    {
        _info = info;
        _keyframes = keyframes;
    }

    public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(ProbeResult.Success(_info));

    public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(_keyframes);

    public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
        _real.SnapToNearestKeyframe(keyframes, requested);

    public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => _real.AverageGop(keyframes);
}

/// <summary>
/// Records each command's tokens and the run length the engine hands the runner (its progress denominator), creates
/// the temp outputs the engine moves into place (the muxer pattern expanded from <c>-segment_times</c>), and reports
/// a scripted list of overall fractions on each run.
/// </summary>
internal sealed class RecordingTimingRunner : IFfmpegRunner
{
    private readonly double[] _fractions;

    public RecordingTimingRunner(params double[] fractions) => _fractions = fractions;

    public List<(IReadOnlyList<string> Tokens, TimeSpan? TotalDuration)> Runs { get; } = new();

    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tokens = args.ToList().ToList();
        Runs.Add((tokens, totalDuration));

        var output = tokens[^1];
        if (output.Contains("%03d", StringComparison.Ordinal))
        {
            var idx = tokens.IndexOf("-segment_times");
            var count = tokens[idx + 1].Split(',').Length + 1;
            for (var i = 0; i < count; i++)
            {
                File.WriteAllText(output.Replace("%03d", i.ToString("000")), "seg");
            }
        }
        else
        {
            File.WriteAllText(output, "seg");
        }

        foreach (var f in _fractions)
        {
            progress?.Report(f);
        }

        return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
    }
}
