using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-192 (G-059, SPEC-001 I45 / I72) - the real-media half of the x264 head settings: the quality floor and the profile rule.
///
/// <para><b>Quality floor.</b> Two heads are built from the same source and start - one with the args the builder emits now, one with
/// today's (the same tokens minus the preset and the crf) - and each is compared with the source frames of its span through ffmpeg's
/// <c>psnr</c> filter. The assertion is relative (the new head no more than 0.5 dB below the default one); no absolute dB figure and
/// no time is hard-coded (T-137). libx264 is deterministic for fixed settings on one machine, so the comparison is stable.</para>
///
/// <para><b>Profile rule.</b> The concat output takes its <c>avcC</c> from the head, so the head's declared profile must cover the
/// copied part: never lower than the source's (<see cref="H264ProfileRank"/>). A 10-bit source's head must be High 10 (that case is
/// skipped, not passed, when the bundled libx264 has no 10-bit support).</para>
/// </summary>
public sealed class SmartCutHeadQualityIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SmartCutHeadQualityIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t192-" + Guid.NewGuid().ToString("N"));
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

    private static FfmpegRunner Runner() => new(Locator());

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>A synthetic source: lavfi testsrc2 + sine, libx264 with <paramref name="videoArgs"/>, in <paramref name="pixFmt"/>.</summary>
    private async Task<string> MakeSourceAsync(string name, double seconds, int fps, string pixFmt, params string[] videoArgs)
    {
        var path = Path.Combine(_dir, name);
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error")
            .Raw("-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=size=320x240:rate={fps}:duration={seconds}"))
            .Raw("-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"sine=frequency=440:duration={seconds}"))
            .Raw("-c:v", "libx264").Raw(videoArgs)
            .Raw("-pix_fmt", pixFmt, "-c:a", "aac", "-shortest")
            .Output(path));
        run.ExitCode.Should().Be(0, "the fixture must be produced before anything is asserted: " + run.StdErrText);
        return path;
    }

    /// <summary>The head the builder emits (<paramref name="withSettings"/>) or today's - the same tokens minus the preset and the crf.</summary>
    private async Task<string> BuildHeadAsync(string source, TimeSpan start, TimeSpan headEnd, bool withSettings, string name)
    {
        var info = ((ProbeResult.ProbeSucceeded)await Probe().ProbeAsync(source)).Info;
        SmartCutArgsBuilder.TryResolveEncoders(info, out var venc, out var aenc, out var why).Should().BeTrue(why);
        var headPath = Path.Combine(_dir, name + Path.GetExtension(source));
        var tokens = SmartCutArgsBuilder.HeadReencode(source, start, headEnd, info, venc!, aenc, headPath).ToList().ToList();
        var crf = SmartCutArgsBuilder.X264HeadCrf.ToString(CultureInfo.InvariantCulture);
        if (withSettings)
        {
            tokens.Should().ContainInOrder("-c:v", "libx264", "-preset", SmartCutArgsBuilder.X264HeadPreset, "-crf", crf);
        }
        else
        {
            var at = tokens.IndexOf("-preset");
            tokens.RemoveRange(at, 4); // -preset veryfast -crf N: what the head was before T-192
        }

        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg().Raw(tokens.Skip(2).ToArray())); // ForFfmpeg() added -hide_banner -nostdin
        run.ExitCode.Should().Be(0, run.StdErrText);
        File.Exists(headPath).Should().BeTrue();
        return headPath;
    }

    private static readonly Regex AveragePsnr = new(@"PSNR .*average:(?<avg>[0-9.]+|inf)", RegexOptions.Compiled);

    /// <summary>The average PSNR (dB) of the head against the source frames of [start, start + length), through the psnr filter.</summary>
    private async Task<double> PsnrAsync(string head, string source, TimeSpan start, TimeSpan length)
    {
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg()
            .Input(head)
            .Raw("-ss", SplitPlanner.ToFfmpegSeconds(start), "-t", SplitPlanner.ToFfmpegSeconds(length))
            .Input(source)
            .Raw("-lavfi", "[0:v][1:v]psnr", "-f", "null")
            .Output("-"));
        run.ExitCode.Should().Be(0, run.StdErrText);
        var line = run.StdErrTail.LastOrDefault(l => AveragePsnr.IsMatch(l));
        line.Should().NotBeNull("the psnr filter prints its summary: " + string.Join(" | ", run.StdErrTail.TakeLast(6)));
        var avg = AveragePsnr.Match(line!).Groups["avg"].Value;
        return avg == "inf" ? 100d : double.Parse(avg, CultureInfo.InvariantCulture);
    }

    private async Task<string?> ProfileOfAsync(string path)
    {
        var json = await new FfprobeRunner(Locator()).RunJsonAsync(FfmpegArgs.ForFfprobe()
            .Raw("-v", "error", "-select_streams", "v:0", "-show_entries", "stream=profile", "-print_format", "json").Input(path));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("streams")[0].GetProperty("profile").GetString();
    }

    // serves-spec: SPEC-001#I72 - the quality floor: on an H.264 source with B-frames and a 10 s GOP (a head of 225 frames) and on one
    // with a 2 s GOP, a head from a start mid-GOP scores no more than 0.5 dB PSNR below a default-settings head of the same frames.
    // veryfast at crf 23 would be 1.2-1.6 dB below (T-192's measurement on the bench's 4K, 1080 and 10 s GOP fixtures), which is what
    // the crf constant is chosen against.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(250, 2.4, 10.0)]   // a 10 s GOP: the head is 7.6 s = 190 frames
    [InlineData(50, 0.5, 2.0)]     // a 2 s GOP: the head is 1.5 s
    public async Task TheVeryfastHead_IsNoMoreThanHalfADecibelBelowTheDefaultHead(int gop, double start, double headEnd)
    {
        RequireBinaries();
        var src = await MakeSourceAsync("q.mp4", 24, 25, "yuv420p", "-g", gop.ToString(CultureInfo.InvariantCulture), "-keyint_min", gop.ToString(CultureInfo.InvariantCulture), "-sc_threshold", "0", "-bf", "2");
        var length = S(headEnd - start);

        var fast = await BuildHeadAsync(src, S(start), S(headEnd), withSettings: true, "head-new");
        var slow = await BuildHeadAsync(src, S(start), S(headEnd), withSettings: false, "head-default");
        var fastDb = await PsnrAsync(fast, src, S(start), length);
        var slowDb = await PsnrAsync(slow, src, S(start), length);

        _output.WriteLine($"GOP {gop}, head [{start}, {headEnd}) s: default head {slowDb:0.00} dB, new head {fastDb:0.00} dB ({fastDb - slowDb:+0.00;-0.00} dB), "
            + $"sizes {new FileInfo(slow).Length / 1e6:0.00} / {new FileInfo(fast).Length / 1e6:0.00} MB");
        slowDb.Should().BeGreaterThan(30, "precondition: a default head resembles its source, or the comparison means nothing");
        fastDb.Should().BeGreaterThanOrEqualTo(slowDb - 0.5, "the veryfast head must not be visibly worse than today's head");
    }

    // serves-spec: SPEC-001#I72 - the comparison can fail: a veryfast head at the DEFAULT crf 23 falls below the floor on the same
    // source, so the assertion above is a real constraint on the constant and not a tautology. (Built with the tokens the builder
    // emits, the crf swapped for 23.)
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task TheFloorCanFail_AVeryfastHeadAtTheDefaultCrfIsBelowIt()
    {
        RequireBinaries();
        var src = await MakeSourceAsync("q.mp4", 24, 25, "yuv420p", "-g", "250", "-keyint_min", "250", "-sc_threshold", "0", "-bf", "2");
        var info = ((ProbeResult.ProbeSucceeded)await Probe().ProbeAsync(src)).Info;
        SmartCutArgsBuilder.TryResolveEncoders(info, out var venc, out var aenc, out _).Should().BeTrue();
        var headPath = Path.Combine(_dir, "head-crf23.mp4");
        var tokens = SmartCutArgsBuilder.HeadReencode(src, S(2.4), S(10), info, venc!, aenc, headPath).ToList().ToList();
        tokens[tokens.IndexOf("-crf") + 1] = "23";
        (await Runner().RunAsync(FfmpegArgs.ForFfmpeg().Raw(tokens.Skip(2).ToArray()))).ExitCode.Should().Be(0);
        var slow = await BuildHeadAsync(src, S(2.4), S(10), withSettings: false, "head-default");

        var crf23 = await PsnrAsync(headPath, src, S(2.4), S(7.6));
        var baseline = await PsnrAsync(slow, src, S(2.4), S(7.6));

        _output.WriteLine($"default head {baseline:0.00} dB, veryfast at crf 23 {crf23:0.00} dB ({crf23 - baseline:+0.00;-0.00} dB)");
        crf23.Should().BeLessThan(baseline - 0.5, "the floor is not met by the x264 default rate factor, which is why the constant is lower");
    }

    // serves-spec: SPEC-001#I72 - the profile rule: the head's H.264 profile is never lower than the source's. A High source and a Main
    // source (x264's -profile:v main has no 8x8 transforms) both get a High head, which covers them.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData("high", "High")]
    [InlineData("main", "Main")]
    public async Task TheHeadsProfile_IsNeverLowerThanTheSources(string sourceProfile, string expectedSource)
    {
        RequireBinaries();
        var src = await MakeSourceAsync("p.mp4", 12, 25, "yuv420p", "-profile:v", sourceProfile, "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-bf", "2");
        (await ProfileOfAsync(src)).Should().Be(expectedSource, "precondition: the fixture has the profile it claims");

        var head = await BuildHeadAsync(src, S(3.4), S(4.0), withSettings: true, "head-p");

        var headProfile = await ProfileOfAsync(head);
        _output.WriteLine($"source {expectedSource}, head {headProfile}");
        H264ProfileRank.Covers(headProfile!, expectedSource).Should().BeTrue($"a {headProfile} head must cover a {expectedSource} source");
    }

    // serves-spec: SPEC-001#I72 - the profile check can fail on real media: an ultrafast head over a High source declares Constrained
    // Baseline, which the comparator rejects - the reason ultrafast is not used although it is faster still (T-192's measurement:
    // 1.47 s against veryfast's 2.16 s for the 4K head at 104.5 s, but a head that does not cover the copied part).
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task AnUltrafastHead_OverAHighSource_IsRejectedByTheProfileRule()
    {
        RequireBinaries();
        var src = await MakeSourceAsync("u.mp4", 12, 25, "yuv420p", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-bf", "2");
        var info = ((ProbeResult.ProbeSucceeded)await Probe().ProbeAsync(src)).Info;
        SmartCutArgsBuilder.TryResolveEncoders(info, out var venc, out var aenc, out _).Should().BeTrue();
        var headPath = Path.Combine(_dir, "head-ultrafast.mp4");
        var tokens = SmartCutArgsBuilder.HeadReencode(src, S(3.4), S(4.0), info, venc!, aenc, headPath).ToList().ToList();
        tokens[tokens.IndexOf("-preset") + 1] = "ultrafast";
        (await Runner().RunAsync(FfmpegArgs.ForFfmpeg().Raw(tokens.Skip(2).ToArray()))).ExitCode.Should().Be(0);

        var headProfile = await ProfileOfAsync(headPath);

        _output.WriteLine($"source High, ultrafast head {headProfile}");
        (await ProfileOfAsync(src)).Should().Be("High");
        H264ProfileRank.Covers(headProfile!, "High").Should().BeFalse("ultrafast drops CABAC, B-frames and 8x8 transforms, so it declares a lower profile");
    }

    // serves-spec: SPEC-001#I72 - a 10-bit source (yuv420p10le): the -pix_fmt token makes libx264 choose High 10, so the head covers it.
    // Skipped, not passed, when the bundled libx264 has no 10-bit support.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableFact]
    public async Task ATenBitSources_HeadIsHigh10()
    {
        RequireBinaries();
        string src;
        try
        {
            src = await MakeSourceAsync("p10.mp4", 12, 25, "yuv420p10le", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-bf", "2");
        }
        catch (Exception ex) when (ex is Xunit.Sdk.XunitException)
        {
            Skip.If(true, "the bundled libx264 cannot encode 10-bit: " + ex.Message.Split('\n')[0]);
            return;
        }

        (await ProfileOfAsync(src)).Should().Be("High 10", "precondition: a 10-bit fixture");
        var head = await BuildHeadAsync(src, S(3.4), S(4.0), withSettings: true, "head-10");

        var headProfile = await ProfileOfAsync(head);
        _output.WriteLine($"source High 10, head {headProfile}");
        H264ProfileRank.Covers(headProfile!, "High 10").Should().BeTrue();
    }
}
