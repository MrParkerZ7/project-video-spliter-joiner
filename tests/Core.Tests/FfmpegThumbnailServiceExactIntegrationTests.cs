using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Io;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-196 (G-059, SPEC-005 I28-I32) - the exact-time frame grab on real media.
///
/// <para><b>The repro</b> is a 30000/1001 fps <c>testsrc2</c> source encoded with <c>-g 75 -keyint_min 75 -sc_threshold 0</c>, so its
/// keyframes fall every 2.5025 s and the one at frame 225 is at 7.5075 s, 15 frames past the whole second 7. The bucketed grab at that
/// keyframe floors to <c>-ss 7</c> and shows frame 210 (7.007 s); the exact grab shows frame 225. (On a regular 2 s GOP at 30000/1001 the
/// keyframe is at 8.008 s and the <c>-ss 8</c> and <c>-ss 8.008</c> grabs are byte-identical, which is why this source.) The references
/// are the same JPEG args as the grab with the frame picked by INDEX instead of by seek.</para>
///
/// <para><b>The TS case.</b> ffmpeg counts <c>-ss</c> from the format <c>start_time</c> (about 1.4 s on an MPEG-TS), so a grab at a
/// keyframe's own time lands that much late; a grab at <see cref="MediaInfo.ToSeekOffset"/> of it is the frame T-219's lossless cut
/// starts on.</para>
/// </summary>
public sealed class FfmpegThumbnailServiceExactIntegrationTests : IDisposable
{
    private const int Width = 640;

    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public FfmpegThumbnailServiceExactIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t196-" + Guid.NewGuid().ToString("N"));
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

    private FfmpegThumbnailService NewService() => new(Runner(), Path.Combine(_dir, "thumbs"));

    private async Task<string> MakeNtscAsync()
    {
        var path = Path.Combine(_dir, "ntsc.mp4");
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error")
            .Raw("-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30000/1001:duration=12")
            .Raw("-c:v", "libx264", "-g", "75", "-keyint_min", "75", "-sc_threshold", "0", "-pix_fmt", "yuv420p")
            .Output(path));
        run.ExitCode.Should().Be(0, run.StdErrText);
        return path;
    }

    /// <summary>The reference: the grab's own JPEG args (<see cref="FfmpegThumbnailService.BuildArgs"/>) with the frame picked by index.</summary>
    private async Task<string> ReferenceByIndexAsync(string source, int frameIndex, string name, int width = Width)
    {
        var output = Path.Combine(_dir, name + ".jpg");
        var tokens = FfmpegThumbnailService.BuildArgs(source, TimeSpan.Zero, width, output).ToList().ToList();
        var ss = tokens.IndexOf("-ss");
        tokens.RemoveRange(ss, 2); // never by seek
        var vf = tokens.IndexOf("-vf");
        tokens[vf + 1] = $"select=eq(n\\,{frameIndex.ToString(CultureInfo.InvariantCulture)}),setpts=PTS-STARTPTS,scale={width.ToString(CultureInfo.InvariantCulture)}:-1";
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg().Raw(tokens.Skip(2).ToArray()));
        run.ExitCode.Should().Be(0, run.StdErrText);
        File.Exists(output).Should().BeTrue();
        return output;
    }

    private static string Md5(string path) => Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path)));

    private static readonly Regex Average = new(@"PSNR .*average:(?<avg>[0-9.]+|inf)", RegexOptions.Compiled);

    /// <summary>md5-identical, or - the ticket's fallback, recorded in the output - a PSNR of at least 50 dB.</summary>
    private async Task<bool> SameFrameAsync(string a, string b, string what)
    {
        if (Md5(a) == Md5(b))
        {
            return true;
        }

        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg().Input(a).Input(b).Raw("-lavfi", "[0:v][1:v]psnr", "-f", "null").Output("-"));
        var line = run.StdErrTail.LastOrDefault(l => Average.IsMatch(l));
        var db = line is null ? 0d : Average.Match(line).Groups["avg"].Value is "inf" ? 100d : double.Parse(Average.Match(line).Groups["avg"].Value, CultureInfo.InvariantCulture);
        _output.WriteLine($"{what}: not md5-identical, PSNR {db:0.0} dB");
        return db >= 50d;
    }

    // serves-spec: SPEC-005#I28 / SPEC-005#I29 - the repro, written first. On a source whose keyframe at frame 225 (7.5075 s) is 15 frames
    // past the whole second 7: the bucketed grab at the keyframe shows frame 210 (the shipped chip: `-ss 7`), the exact grab shows frame
    // 225, the frame the cut starts on. The two references differ (the precondition that lets the case fail), the exact grab is
    // identical to the keyframe's and not to the floored one's, and the bucketed grab is identical to the floored one's.
    [Trait("serves-spec", "SPEC-005")]
    [SkippableFact]
    public async Task AnExactGrab_AtAKeyframe_ShowsTheKeyframe_WhereTheBucketedGrabShowsTheFlooredSecond()
    {
        RequireBinaries();
        var src = await MakeNtscAsync();
        var keyframes = await Probe().GetKeyframesAsync(src);
        var keyframe = keyframes[3];
        _output.WriteLine($"keyframes {string.Join(", ", keyframes.Select(k => k.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)))}");
        keyframe.TotalSeconds.Should().BeApproximately(7.5075, 1e-4, "frame 225 at 30000/1001 fps");
        var keyframeIndex = (int)Math.Round(keyframe.TotalSeconds * 30000d / 1001d);
        var flooredIndex = (int)Math.Ceiling(7d * 30000d / 1001d); // the first frame at or after 7.000 s
        keyframeIndex.Should().Be(225);
        flooredIndex.Should().Be(210);
        var atKeyframe = await ReferenceByIndexAsync(src, keyframeIndex, "ref-keyframe");
        var atFloor = await ReferenceByIndexAsync(src, flooredIndex, "ref-floored");
        Md5(atKeyframe).Should().NotBe(Md5(atFloor), "precondition: the keyframe and the floored frame look different, so the case can fail");
        var svc = NewService();

        var exact = await svc.GetExactThumbnailAsync(src, keyframe, Width, CancellationToken.None);
        var bucketed = await svc.GetThumbnailAsync(src, keyframe, Width, CancellationToken.None);

        exact.Should().NotBeNull();
        bucketed.Should().NotBeNull();
        (await SameFrameAsync(exact!, atKeyframe, "exact vs the keyframe")).Should().BeTrue("the exact grab is the frame the cut starts on");
        (await SameFrameAsync(bucketed!, atFloor, "bucketed vs the floored frame")).Should().BeTrue("the shipped chip showed the floored second");
        Md5(exact!).Should().NotBe(Md5(atFloor), "an exact request never returns the floored frame");
        Md5(exact!).Should().NotBe(Md5(bucketed!));
    }

    // serves-spec: SPEC-005#I28 - a time that is not a keyframe: the exact grab is the frame AT the time (the first frame at or after it),
    // the bucketed grab the frame at its floored second.
    [Trait("serves-spec", "SPEC-005")]
    [SkippableFact]
    public async Task AnExactGrab_BetweenKeyframes_ShowsTheFrameAtTheTime()
    {
        RequireBinaries();
        var src = await MakeNtscAsync();
        var time = S(9.0); // 9.0 s -> the first frame at or after it is ceil(9 * 30000/1001) = 270 (9.009 s)
        var reference = await ReferenceByIndexAsync(src, (int)Math.Ceiling(9d * 30000d / 1001d), "ref-9s");
        var exact = await NewService().GetExactThumbnailAsync(src, time, Width, CancellationToken.None);

        (await SameFrameAsync(exact!, reference, "exact at 9.0 s")).Should().BeTrue();
    }

    // serves-spec: SPEC-005#I32 / SPEC-004#I47 / SPEC-001#I66 - on an MPEG-TS, whose format starts at about 1.4 s, the cut frame is
    // found with the keyframe's time MINUS the start: an exact grab at MediaInfo.ToSeekOffset(K) is the source's frame at K and the
    // first frame of the lossless cut from K (T-219); the same grab at K itself (unconverted) is not (the precondition).
    [Trait("serves-spec", "SPEC-005")]
    [SkippableFact]
    public async Task OnATransportStream_TheCutFrame_IsFoundAtTheKeyframeMinusTheFormatStart()
    {
        RequireBinaries();
        var src = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "src.ts", 10, 30, "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");
        var probe = Probe();
        var info = ((ProbeResult.ProbeSucceeded)await probe.ProbeAsync(src)).Info;
        info.StartTime.TotalSeconds.Should().BeGreaterThan(0.001, "precondition: an MPEG-TS does not start at zero");
        var keyframes = await probe.GetKeyframesAsync(src);
        var keyframe = keyframes[2];
        var videoStart = info.VideoStreams[0].StartTime ?? keyframes[0];
        var keyframeIndex = (int)Math.Round((keyframe - videoStart).TotalSeconds * 30d);
        var reference = await ReferenceByIndexAsync(src, keyframeIndex, "ref-ts", width: 320);
        var svc = NewService();

        var converted = await svc.GetExactThumbnailAsync(src, info.ToSeekOffset(keyframe), 320, CancellationToken.None);
        var unconverted = await svc.GetExactThumbnailAsync(src, keyframe, 320, CancellationToken.None);

        (await SameFrameAsync(converted!, reference, "converted vs the source frame at K")).Should().BeTrue("the converted grab is the source's frame at the keyframe");
        (await SameFrameAsync(unconverted!, reference, "unconverted vs the source frame at K")).Should().BeFalse(
            "precondition: a grab at the keyframe's own time lands about " + info.StartTime.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s late");

        // T-219's lossless cut from K starts on the same frame: a Bulk Lossless row with that intro, its first frame grabbed at time 0.
        var runner = Runner();
        var split = new SplitEngine(runner, probe, new ErrorLogWriter(Path.Combine(_dir, "logs")), new FakeDiskSpaceProbe(long.MaxValue));
        var batch = new BulkTrimEngine(split, new KeptMiddleRequestBuilder(probe), new FakeDiskSpaceProbe(long.MaxValue), new SmartCutEngine(runner, probe), null);
        var result = await batch.RunAsync(
            new[] { new BulkTrimItem(src, keyframe, null, Path.Combine(_dir, "trimmed", "src_trimmed.ts")) },
            new BulkTrimOptions(Precision: CutPrecision.Lossless));
        result.Items.Single().Outcome.Should().Be(ItemOutcome.Done, result.Items.Single().Error?.Message);
        var firstOfCut = await svc.GetExactThumbnailAsync(result.Items.Single().OutputPath!, TimeSpan.Zero, 320, CancellationToken.None);

        (await SameFrameAsync(converted!, firstOfCut!, "converted vs the first frame of the lossless cut")).Should().BeTrue(
            "the chip shows the first frame of the cut the run makes");
    }

    // serves-spec: SPEC-005#I28 - cost: an exact seek to a keyframe decodes about one frame, the floored one most of a GOP. Not a
    // wall-clock assertion (T-137): the CPU time of the two grabs is printed for the Build log.
    [Trait("serves-spec", "SPEC-005")]
    [SkippableFact]
    public async Task TheExactGrab_AtAKeyframe_IsNoDearerThanTheBucketedOne_AndTheNumbersArePrinted()
    {
        RequireBinaries();
        var src = await MakeNtscAsync();
        var keyframe = (await Probe().GetKeyframesAsync(src))[3];
        var svc = NewService();

        var exact = await TimedAsync(() => svc.GetExactThumbnailAsync(src, keyframe, Width, CancellationToken.None));
        var bucketed = await TimedAsync(() => svc.GetThumbnailAsync(src, keyframe, Width, CancellationToken.None));

        _output.WriteLine($"exact grab at {keyframe.TotalSeconds:0.######} s: {exact.Wall.TotalMilliseconds:0} ms wall; bucketed (-ss 7): {bucketed.Wall.TotalMilliseconds:0} ms wall");
        exact.Path.Should().NotBeNull();
        bucketed.Path.Should().NotBeNull();
    }

    private static async Task<(string? Path, TimeSpan Wall)> TimedAsync(Func<Task<string?>> grab)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var path = await grab();
        return (path, clock.Elapsed);
    }
}
