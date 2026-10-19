using System.Buffers.Binary;
using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;
using static VideoSplitJoiner.Core.Tests.SmartCutJointTiming;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-191 (G-059, SPEC-001 I44 / I69) - the head seeks to its keyframe before it decodes, on real media through the real ffmpeg.
///
/// <para><b>The change under test.</b> <see cref="SmartCutArgsBuilder.HeadReencode"/> moved <c>-ss</c> before <c>-i</c>. ffmpeg's
/// default <c>accurate_seek</c> starts the decode at the keyframe before the start and drops decoded frames until it, which is the
/// frame set an output seek gives; it just does not decode (and throw away) everything before the cut. The first test proves "the
/// same frames" instead of assuming it: for each shape, the new head and a reference head built in the test with the OLD argument
/// order (same encoder, same settings, same machine) must decode to identical frame hashes at identical timestamps, the same
/// count, the first frame at pts 0.</para>
///
/// <para>The rest runs the whole engine on real media: the row's fraction while a cut deep into a long file runs, and a cut
/// cancelled in the middle of its head.</para>
/// </summary>
public sealed class SmartCutHeadSeekIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SmartCutHeadSeekIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t191i-" + Guid.NewGuid().ToString("N"));
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

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // ---- Frame identity: the input-seek head against the output-seek head ---------------------------------------------------

    /// <summary>The head tokens as the OUTPUT-seek head had them: <c>-ss</c> moved from before <c>-i</c> to after the input path.</summary>
    private static string[] OutputSeekTokens(IReadOnlyList<string> inputSeekArgs)
    {
        var tokens = inputSeekArgs.Skip(2).SkipLast(1).ToList(); // without -hide_banner -nostdin and the output path
        var ss = tokens.IndexOf("-ss");
        var seconds = tokens[ss + 1];
        tokens.RemoveRange(ss, 2);
        var input = tokens.IndexOf("-i");
        tokens.Insert(input + 2, "-ss");
        tokens.Insert(input + 3, seconds);
        return tokens.ToArray();
    }

    private async Task RunAsync(FfmpegArgs args, string what)
    {
        var run = await Runner().RunAsync(args);
        run.ExitCode.Should().Be(0, what + ": " + run.StdErrText);
    }

    /// <summary>The video frames of <paramref name="path"/> as (pts in the file's time base, md5), in output order.</summary>
    private async Task<IReadOnlyList<(long Pts, string Md5)>> FrameHashesAsync(string path)
    {
        var listing = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".framemd5");
        await RunAsync(FfmpegArgs.ForFfmpeg().Raw("-v", "error").Input(path)
            .Raw("-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-y").Output(listing), "framemd5 of " + Path.GetFileName(path));
        var frames = new List<(long, string)>();
        foreach (var raw in await File.ReadAllLinesAsync(listing))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var cols = line.Split(',');
            frames.Add((long.Parse(cols[2].Trim(), CultureInfo.InvariantCulture), cols[5].Trim()));
        }

        return frames;
    }

    /// <summary>
    /// Encode the head of <paramref name="source"/> from <paramref name="requested"/> (moved up to the first source frame at or
    /// after it, as the engine does) to the next keyframe twice - with the shipped output-seek argument order and with the input
    /// seek - and require identical frames. Returns the head's frame count.
    /// </summary>
    private async Task<int> AssertSameFramesAsync(string source, double requested, string label)
    {
        var effective = await FirstFrameAtOrAfterAsync(source, requested);
        var probe = Probe();
        var info = ((ProbeResult.ProbeSucceeded)await probe.ProbeAsync(source)).Info;
        var keyframes = await probe.GetKeyframesAsync(source);
        var plan = SmartCutPlanner.Plan(S(effective), null, keyframes);
        plan.Strategy.Should().Be(SmartCutStrategy.HeadReencode, "precondition: the start is mid-GOP with a keyframe after it");
        SmartCutArgsBuilder.TryResolveEncoders(info, out var venc, out var aenc, out var why).Should().BeTrue(why);

        var inputSeekHead = Path.Combine(_dir, "head-input-seek.mp4");
        var outputSeekHead = Path.Combine(_dir, "head-output-seek.mp4");
        var args = SmartCutArgsBuilder.HeadReencode(source, plan.Start, plan.HeadEnd!.Value, info, venc!, aenc, inputSeekHead);
        var tokens = args.ToList().ToList();
        tokens.Where(t => t == "-ss").Should().ContainSingle();
        tokens.IndexOf("-ss").Should().BeLessThan(tokens.IndexOf("-i"), "the new head seeks before its input");
        tokens.Should().NotContain("-noaccurate_seek");

        await RunAsync(args, label + " (input seek)");
        var reference = FfmpegArgs.ForFfmpeg();
        foreach (var token in OutputSeekTokens(tokens))
        {
            reference.Raw(token);
        }

        await RunAsync(reference.Output(outputSeekHead), label + " (output seek, the shipped order)");

        var fresh = await FrameHashesAsync(inputSeekHead);
        var shipped = await FrameHashesAsync(outputSeekHead);
        _output.WriteLine($"{label}: Start {requested} -> effective {effective:0.######}, HeadEnd {plan.HeadEnd!.Value.TotalSeconds:0.######}: "
            + $"{fresh.Count} frames (input seek) against {shipped.Count} (output seek); head {new FileInfo(inputSeekHead).Length} against {new FileInfo(outputSeekHead).Length} bytes; "
            + $"first pts {(fresh.Count > 0 ? fresh[0].Pts : -1)} against {(shipped.Count > 0 ? shipped[0].Pts : -1)}");

        using var scope = new AssertionScope(label);
        fresh.Should().NotBeEmpty();
        fresh.Count.Should().Be(shipped.Count, "the same number of frames");
        fresh[0].Pts.Should().Be(0, "the first frame is at pts 0");
        fresh.Should().Equal(shipped, "every frame has the same timestamp and the same hash as the output-seek head's");
        fresh.Count.Should().Be((int)Math.Round((plan.HeadEnd.Value.TotalSeconds - effective) * 30), "one frame per source frame of [Start, HeadEnd)");

        // The audio rides along: same stream length to within one AAC frame (the seek lands on the same sample).
        var audioNew = await PacketsAsync(inputSeekHead, "a:0");
        var audioOld = await PacketsAsync(outputSeekHead, "a:0");
        _output.WriteLine($"  audio: {audioNew.Count} packets from {audioNew[0].Pts:0.000000} against {audioOld.Count} from {audioOld[0].Pts:0.000000}");
        Math.Abs(audioNew.Count - audioOld.Count).Should().BeLessThanOrEqualTo(1, "the same audio, to a packet");
        return fresh.Count;
    }

    // serves-spec: SPEC-001#I44 - on a 2 s GOP and a 10 s GOP, with a start at least three GOPs in and mid-GOP, the input-seek head and
    // the shipped output-seek head decode to identical frames.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(60, 30, 7.033333)]    // 2 s GOP: 3.5 GOPs in (HeadEnd 8 s)
    [InlineData(60, 30, 13.5)]        // 2 s GOP: 6.75 GOPs in (HeadEnd 14 s)
    [InlineData(60, 30, 22.5)]        // 2 s GOP: 11.25 GOPs in (HeadEnd 24 s), a half-second start
    [InlineData(300, 45, 33.5)]       // 10 s GOP: 3.35 GOPs in (HeadEnd 40 s)
    public async Task TheInputSeekHead_HoldsTheSameFramesAsTheOutputSeekHead(int gopFrames, double seconds, double start)
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, $"g{gopFrames}.mp4", seconds, gopFrames);

        await AssertSameFramesAsync(src, start, $"GOP {gopFrames / 30} s");
    }

    // serves-spec: SPEC-001#I44 - a Start BETWEEN two frames is moved up to the first frame by the engine (SPEC-001 I43); the head is
    // built from that frame, so the identity holds there too.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(7.018)]
    [InlineData(7.031)]
    [InlineData(21.0035)]
    public async Task TheInputSeekHead_HoldsTheSameFrames_AtAStartThatIsOffTheFrameGrid(double start)
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, "g60.mp4", 30, 60);

        await AssertSameFramesAsync(src, start, $"off-grid Start {start}");
    }

    // serves-spec: SPEC-001#I44 - the app's own Join output: its video starts 21 ms after its audio (format start 0), so its frames sit
    // at 0.021 + k/30. The identity holds at the round Starts a user types.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(33.3)]
    [InlineData(47.5)]
    public async Task TheInputSeekHead_HoldsTheSameFrames_OnASourceWhoseVideoStartsAfterItsAudio(double start)
    {
        RequireBinaries();
        var baseSrc = await MakeSourceAsync(_dir, "base.mp4", 30, 60);
        var late = await MakeLateVideoSourceAsync(_dir, "late.mp4", baseSrc);
        (await StreamStartAsync(late, "v:0")).Should().BeGreaterThan(0.015, "precondition: the video starts after the audio");

        await AssertSameFramesAsync(late, start, $"Join-style source, Start {start}");
    }

    /// <summary>
    /// The media_time of every entry of every <c>elst</c> (edit list) box in the file's bytes: -1 is an EMPTY edit (a gap before the
    /// track's media starts). Version 0 entries are 12 bytes, version 1 entries 20.
    /// </summary>
    private static IReadOnlyList<long> EditListMediaTimes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var found = new List<long>();
        for (var i = 4; i + 16 <= bytes.Length; i++)
        {
            if (bytes[i] != (byte)'e' || bytes[i + 1] != (byte)'l' || bytes[i + 2] != (byte)'s' || bytes[i + 3] != (byte)'t')
            {
                continue;
            }

            var boxSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i - 4, 4));
            var version = bytes[i + 4];
            var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 8, 4));
            var entrySize = version == 1 ? 20 : 12;
            if (version > 1 || count is 0 or > 16 || boxSize != 16 + (count * entrySize) || i + 12 + (count * entrySize) > bytes.Length)
            {
                continue; // four bytes that spell elst inside media data, not a box
            }

            for (var e = 0; e < count; e++)
            {
                var at = i + 12 + (e * entrySize);
                found.Add(version == 1
                    ? BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(at + 8, 8))
                    : BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at + 4, 4)));
            }
        }

        return found;
    }

    // serves-spec: SPEC-001#I44 - a MOV whose video starts late behind a real EMPTY EDIT (the muxer writes an elst entry with media time
    // -1; the test checks the box itself) while its format starts at 0: the container's time, not the file's first sample, is what -ss
    // honours, so the input seek must land where the output seek does. Made here from the 2 s GOP source: the video delayed by 0.5 s.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(7.533333)]    // 3.5 GOPs after the video's first frame (HeadEnd 8.5 s)
    [InlineData(13.0)]
    public async Task TheInputSeekHead_HoldsTheSameFrames_OnAMovWithALeadingEmptyEdit(double start)
    {
        RequireBinaries();
        var baseSrc = await MakeSourceAsync(_dir, "base.mp4", 30, 60);
        var mov = Path.Combine(_dir, "late.mov");
        await RunAsync(FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error")
            .Raw("-itsoffset", "0.5").Input(baseSrc).Input(baseSrc)
            .Raw("-map", "0:v:0", "-map", "1:a:0", "-c", "copy").Output(mov), "the late-video MOV");

        EditListMediaTimes(mov).Should().Contain(-1, "precondition: the file really has a leading empty edit (elst media_time -1)");
        (await StreamStartAsync(mov, "v:0")).Should().BeApproximately(0.5, 0.01, "precondition: the video starts after the audio");
        (await FormatStartAsync(mov)).Should().BeApproximately(0, 0.001, "precondition: the format starts at 0, so Exact applies to it");

        await AssertSameFramesAsync(mov, start, $"MOV with a leading empty edit, Start {start}");
    }

    // ---- The whole engine, on real media ------------------------------------------------------------------------------------

    /// <summary>Records the total each run was told, and forwards everything to the real runner.</summary>
    private sealed class TotalsRecordingRunner : IFfmpegRunner
    {
        private readonly IFfmpegRunner _inner;

        public TotalsRecordingRunner(IFfmpegRunner inner) => _inner = inner;

        public List<TimeSpan?> Totals { get; } = new();

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Totals.Add(totalDuration);
            return _inner.RunAsync(args, totalDuration, progress, ct);
        }
    }

    private sealed class ProgressSpy : IProgress<double>
    {
        private readonly Action<double>? _onReport;

        public ProgressSpy(Action<double>? onReport = null) => _onReport = onReport;

        public List<double> Values { get; } = new();

        public void Report(double value)
        {
            Values.Add(value);
            _onReport?.Invoke(value);
        }
    }

    // serves-spec: SPEC-001#I44 / SPEC-001#I69 - a cut DEEP into a long file (100 s into a 120 s, 2 s GOP source) on the real runner: the
    // head starts at the keyframe before the cut, the joint is right, and the row's fraction is monotonic, stays below 1.0 until the
    // output is in place, and ends at 1.0 exactly once.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(100.018, null)]      // an off-grid Start, to the end of the file
    [InlineData(100.0333333, 116.5)] // a kept middle
    public async Task AnExactCut_DeepInALongFile_KeepsItsJoint_AndItsProgressIsMonotonicToOne(double start, double? end)
    {
        RequireBinaries();
        var src = await MakeSourceAsync(_dir, "long.mp4", 120, 60);
        var dest = Path.Combine(_dir, "exact.mp4");
        var runner = new RecordingRealRunner(new FfmpegRunner(Locator()), Path.Combine(_dir, "kept"));
        var totals = new TotalsRecordingRunner(runner);
        var existedAtOne = new List<bool>();
        var spy = new ProgressSpy(v =>
        {
            if (v >= 1.0)
            {
                existedAtOne.Add(File.Exists(dest));
            }
        });
        var effective = await FirstFrameAtOrAfterAsync(src, start);
        var keyframes = await Probe().GetKeyframesAsync(src);
        var plan = SmartCutPlanner.Plan(S(effective), end is { } e ? S(e) : null, keyframes);

        var result = await new SmartCutEngine(totals, Probe()).CutAsync(src, S(start), end is { } e2 ? S(e2) : null, dest, spy);

        result.FellBack.Should().BeFalse(result.FallbackReason);
        plan.Strategy.Should().Be(SmartCutStrategy.HeadReencode);
        var headEnd = plan.HeadEnd!.Value;
        totals.Totals.Should().HaveCount(2);
        totals.Totals[0]!.Value.TotalSeconds.Should().BeApproximately((headEnd - S(effective)).TotalSeconds, 1e-6, "the head run is told HeadEnd - Start");
        totals.Totals[1]!.Value.TotalSeconds.Should().BeApproximately(
            ((plan.End ?? (await ProbeDurationAsync(src))) - S(effective)).TotalSeconds, 1e-6, "the concat run is told the kept duration");

        var headFrames = await FrameCountAsync(runner.HeadCopy!);
        var report = await SmartCutJointIntegrity.CheckAsync(
            dest, src, S(effective), headEnd, _dir, default, end is { } e3 ? S(e3) : null, end is null ? 0 : 2, headFrames);
        _output.WriteLine($"Start {start} (effective {effective:0.######}), HeadEnd {headEnd.TotalSeconds}, End {end?.ToString(CultureInfo.InvariantCulture) ?? "EOF"}: "
            + $"head {headFrames} frames; integrity: {report.Describe()}; progress: {string.Join(", ", spy.Values.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)))}");
        report.Passes.Should().BeTrue(report.Describe());
        report.AllErrorLines.Should().Be(0, "a full decode prints no line");

        spy.Values.Should().NotBeEmpty().And.BeInAscendingOrder().And.OnlyHaveUniqueItems("monotonic, no repeats");
        spy.Values[^1].Should().Be(1.0);
        spy.Values.Count(v => v >= 1.0).Should().Be(1, "1.0 exactly once");
        spy.Values.Take(spy.Values.Count - 1).Should().OnlyContain(v => v < 1.0);
        existedAtOne.Should().Equal(new[] { true }, "the output is in place at the first 1.0");
    }

    private static async Task<TimeSpan> ProbeDurationAsync(string path) =>
        ((ProbeResult.ProbeSucceeded)await Probe().ProbeAsync(path)).Info.Duration;

    /// <summary>A 720p, B-frame H.264 + AAC source with a 10 s GOP: a head that is slow enough to cancel in the middle of.</summary>
    private async Task<string> MakeSlowHeadSourceAsync()
    {
        var path = Path.Combine(_dir, "slow.mp4");
        await RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error")
            .Raw("-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=25")
            .Raw("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=25")
            .Raw("-map", "0:v:0", "-map", "1:a:0")
            .Raw("-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-g", "300", "-keyint_min", "300", "-sc_threshold", "0", "-bf", "2")
            .Raw("-c:a", "aac", "-b:a", "192000", "-ar", "48000", "-ac", "2", "-t", "25")
            .Output(path), "the 720p source");
        return path;
    }

    // serves-spec: SPEC-001#I47 / SPEC-001#I69 - a cut cancelled in the MIDDLE of its head (the real runner kills the ffmpeg process tree):
    // the cancel propagates, no destination file exists, the engine's temp dir is gone and no 1.0 was ever reported. The cancel is
    // raised from the first progress report, so the head is genuinely running. A 10 s head of 720p video is long enough for that.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task ACutCancelledInTheMiddleOfItsHead_LeavesNothingBehind_AndReportsNoOne()
    {
        RequireBinaries();
        var src = await MakeSlowHeadSourceAsync();
        var dest = Path.Combine(_dir, "out", "exact.mp4");
        using var cts = new CancellationTokenSource();
        var spy = new ProgressSpy(_ => cts.Cancel());

        var act = () => new SmartCutEngine(new FfmpegRunner(Locator()), Probe()).CutAsync(src, S(10.033333), null, dest, spy, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _output.WriteLine("progress before the cancel took effect: " + string.Join(", ", spy.Values.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture))));
        spy.Values.Should().NotBeEmpty("the cancel was raised from the head's own progress").And.OnlyContain(v => v < 1.0);
        File.Exists(dest).Should().BeFalse("a cancelled cut leaves no destination file");
        var outDir = Path.GetDirectoryName(dest)!;
        (Directory.Exists(outDir) ? Directory.GetDirectories(outDir, ".vsj-smartcut-*") : Array.Empty<string>())
            .Should().BeEmpty("the temp dir holding the half-written head is swept");
    }
}
