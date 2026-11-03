using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-227 (G-059, SPEC-005 I34) - the hover picture on an MPEG-TS source shows the frame at the hovered time, on real media.
///
/// <para><b>The repro</b> is a 10 s, 30 fps <c>testsrc</c> + AAC clip remuxed as <c>.ts</c> with <c>-g 60</c>, so its keyframes fall every 2 s
/// (frames 0, 60, 120, ...) and the format starts at about 1.44 s. An input <c>-ss</c> on it lands on the first keyframe AFTER the target
/// (the mpegts demuxer's seek), so the shipped grab at 1 s showed frame 60 (the 2 s keyframe), not frame 30. On MP4 and MKV the same
/// args show the frame at the time. The references are the same JPEG args as the grab with the frame picked by INDEX instead of by seek.</para>
/// </summary>
public sealed class FfmpegThumbnailServiceTransportStreamIntegrationTests : IDisposable
{
    private const int Width = 320;
    private const int Fps = 30;

    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public FfmpegThumbnailServiceTransportStreamIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t227-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>The service as the app wires it: the shared probe is the start-time source (<c>MainViewModel</c>).</summary>
    private FfmpegThumbnailService NewService(MediaProbe probe, string name = "thumbs") => new(
        Runner(),
        Path.Combine(_dir, name),
        startTimeOf: async (path, ct) => (await probe.ProbeAsync(path, ct)) is ProbeResult.ProbeSucceeded ok ? ok.Info.StartTime : null);

    private Task<string> MakeAsync(string name) => SmartCutJointIntegrityTests.MakeFixtureAsync(
        _dir, name, 10, Fps, "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");

    /// <summary>The first frame at or after <c>start + time</c>, as an index from the video stream's first frame.</summary>
    private static int ExpectedFrame(MediaInfo info, TimeSpan time)
    {
        var videoStart = info.VideoStreams[0].StartTime ?? info.StartTime;
        var seconds = (info.StartTime + time - videoStart).TotalSeconds;
        return seconds <= 0 ? 0 : (int)Math.Ceiling(seconds * Fps - 1e-3);
    }

    /// <summary>The reference: the grab's own JPEG args (<see cref="FfmpegThumbnailService.BuildArgs"/>) with the frame picked by index.</summary>
    private async Task<string> ReferenceByIndexAsync(string source, int frameIndex, string name)
    {
        var output = Path.Combine(_dir, name + ".jpg");
        var tokens = FfmpegThumbnailService.BuildArgs(source, TimeSpan.Zero, Width, output).ToList().ToList();
        var ss = tokens.IndexOf("-ss");
        tokens.RemoveRange(ss, 2); // never by seek
        var vf = tokens.IndexOf("-vf");
        tokens[vf + 1] = $"select=eq(n\\,{frameIndex.ToString(CultureInfo.InvariantCulture)}),setpts=PTS-STARTPTS,scale={Width.ToString(CultureInfo.InvariantCulture)}:-1";
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg().Raw(tokens.Skip(2).ToArray()));
        run.ExitCode.Should().Be(0, run.StdErrText);
        File.Exists(output).Should().BeTrue();
        return output;
    }

    private static string Md5(string path) => Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path)));

    private static readonly Regex Average = new(@"PSNR .*average:(?<avg>[0-9.]+|inf)", RegexOptions.Compiled);

    /// <summary>md5-identical, or a PSNR of at least 50 dB (the same fallback as the exact-grab tests).</summary>
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

    // serves-spec: SPEC-005#I34 - the repro, written first. The format starts at about 1.44 s and the video at 1.47 s, so a hover at 1, 3 and 5 s
    // falls between the keyframes at frames 0, 60, 120, 180 (video times 0, 2, 4, 6 s): the shipped grab showed the NEXT keyframe (frames 60,
    // 120 and 180), the fix shows the frame at the time (frames 30, 90 and 150). 0, 2, 4 and 8 s are the boundary cells - the first frame and
    // times whose first frame at or after them IS a keyframe (frames 0, 60, 120, 240) - which were already right and must stay so.
    [Trait("serves-spec", "SPEC-005")]
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public async Task OnATransportStream_AHoverBetweenKeyframes_ShowsTheFrameAtTheTime_NotTheNextKeyframe(int seconds)
    {
        RequireBinaries();
        var src = await MakeAsync("src.ts");
        var probe = Probe();
        var info = ((ProbeResult.ProbeSucceeded)await probe.ProbeAsync(src)).Info;
        info.StartTime.TotalSeconds.Should().BeGreaterThan(0.001, "precondition: an MPEG-TS does not start at zero");
        var time = TimeSpan.FromSeconds(seconds);
        var wanted = ExpectedFrame(info, time);
        var nextKeyframe = (int)Math.Ceiling(wanted / 60d) * 60;
        _output.WriteLine($"hover at {seconds} s: the frame at the time is {wanted}, the next keyframe is {nextKeyframe}");
        var reference = await ReferenceByIndexAsync(src, wanted, "ref-time");

        var picture = await NewService(probe).GetThumbnailAsync(src, time, Width, CancellationToken.None);

        picture.Should().NotBeNull();
        (await SameFrameAsync(picture!, reference, $"hover at {seconds} s vs frame {wanted}")).Should().BeTrue(
            $"the picture is the frame at {seconds} s (frame {wanted}), not the keyframe after it (frame {nextKeyframe})");
    }

    // serves-spec: SPEC-005#I34 - the other extensions of the family (.m2ts, .mts) take the same path; one mid-GOP time each.
    [Trait("serves-spec", "SPEC-005")]
    [SkippableTheory]
    [InlineData("src.m2ts")]
    [InlineData("src.mts")]
    public async Task OnAnM2tsOrMtsStream_AHoverBetweenKeyframes_ShowsTheFrameAtTheTime(string name)
    {
        RequireBinaries();
        var src = await MakeAsync(name);
        var probe = Probe();
        var info = ((ProbeResult.ProbeSucceeded)await probe.ProbeAsync(src)).Info;
        var time = TimeSpan.FromSeconds(3);
        var wanted = ExpectedFrame(info, time);
        var reference = await ReferenceByIndexAsync(src, wanted, "ref-time");

        var picture = await NewService(probe).GetThumbnailAsync(src, time, Width, CancellationToken.None);

        picture.Should().NotBeNull();
        (await SameFrameAsync(picture!, reference, $"{name} hover at 3 s vs frame {wanted}")).Should().BeTrue($"the picture is frame {wanted}");
    }

    // serves-spec: SPEC-005#I2 / SPEC-005#I34 - the other containers are untouched: an MKV and an MP4 remux of the same clip show the frame
    // at the time with today's args (the control that says the TS path is the only one that changed).
    [Trait("serves-spec", "SPEC-005")]
    [SkippableTheory]
    [InlineData("src.mkv")]
    [InlineData("src.mp4")]
    public async Task OnAnMkvOrMp4_AHoverBetweenKeyframes_ShowsTheFrameAtTheTime_WithTodaysArgs(string name)
    {
        RequireBinaries();
        var src = await MakeAsync(name);
        var probe = Probe();
        var info = ((ProbeResult.ProbeSucceeded)await probe.ProbeAsync(src)).Info;
        var time = TimeSpan.FromSeconds(3);
        var wanted = ExpectedFrame(info, time);
        var reference = await ReferenceByIndexAsync(src, wanted, "ref-time");

        var picture = await NewService(probe).GetThumbnailAsync(src, time, Width, CancellationToken.None);

        picture.Should().NotBeNull();
        (await SameFrameAsync(picture!, reference, $"{name} hover at 3 s vs frame {wanted}")).Should().BeTrue($"the picture is frame {wanted}");
    }
}
