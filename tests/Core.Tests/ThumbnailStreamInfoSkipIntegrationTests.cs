using System.Globalization;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-226 (G-059, SPEC-005 I27 / I33) on real media, synthetic clips this class generates itself: the frame grab of an MP4/MOV whose probed
/// format start time is exactly 0 skips the stream-info decode and shows the SAME picture as today's grab (decoded md5), H.264 and HEVC, on
/// a keyframe and mid-GOP, rotated by a display matrix; and the clips that start later - the first part of the app's own lossless split, an
/// <c>-output_ts_offset</c> MOV, MPEG-TS content named <c>.mp4</c> - get one run with no flag and today's picture, because there a skipping
/// grab exits 0 with another frame (T-195's round-1 finding). The start time comes from the real <see cref="MediaProbe"/>, as in the app.
/// </summary>
public sealed class ThumbnailStreamInfoSkipIntegrationTests : IDisposable
{
    private const string Flag = "-nofind_stream_info";
    private const int Width = 160;

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-t226-real-" + Guid.NewGuid().ToString("N"));

    public ThumbnailStreamInfoSkipIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private void Require()
    {
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg");
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
    }

    private static FfmpegBinaryLocator Locator() => new(
        ffmpegOverride: FfmpegTestBinaries.FfmpegRequired, ffprobeOverride: FfmpegTestBinaries.FfprobeRequired);

    private static FfmpegRunner Real() => new(Locator());

    private static MediaProbe Probe() => new(new FfprobeRunner(Locator()));

    private async Task RunAsync(FfmpegArgs args)
    {
        var run = await Real().RunAsync(args);
        run.ExitCode.Should().Be(0, "fixture step failed: " + run.StdErrText);
    }

    private FfmpegThumbnailService Service(IFfmpegRunner runner)
    {
        var probe = Probe();
        return new FfmpegThumbnailService(
            runner,
            Path.Combine(_dir, "cache-" + Guid.NewGuid().ToString("N")),
            TimeSpan.FromSeconds(1),
            128,
            async (path, ct) => await probe.ProbeAsync(path, ct) is ProbeResult.ProbeSucceeded ok ? ok.Info.StartTime : null);
    }

    private static async Task<TimeSpan> StartOfAsync(string clip) =>
        ((ProbeResult.ProbeSucceeded)await Probe().ProbeAsync(clip)).Info.StartTime;

    /// <summary>Today's grab, spelled out here (not taken from <c>BuildArgs</c>), so a flag added to the service's args never reaches the reference.</summary>
    private async Task<string> GrabTodayAsync(string input, double seconds)
    {
        var path = Path.Combine(_dir, "today-" + Guid.NewGuid().ToString("N") + ".jpg");
        await RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-ss", seconds.ToString("0.######", CultureInfo.InvariantCulture))
            .Input(input)
            .Raw("-frames:v", "1", "-vf", $"scale={Width.ToString(CultureInfo.InvariantCulture)}:-1", "-y")
            .Output(path));
        File.Exists(path).Should().BeTrue();
        return path;
    }

    /// <summary>The md5 of the decoded picture (ffmpeg's md5 muxer over the raw frame), not of the JPEG bytes.</summary>
    private async Task<string> DecodedMd5Async(string jpeg)
    {
        var md5File = Path.Combine(_dir, "md5-" + Guid.NewGuid().ToString("N") + ".txt");
        await RunAsync(FfmpegArgs.ForFfmpeg().Raw("-v", "error").Input(jpeg).Raw("-f", "md5", "-y").Output(md5File));
        return (await File.ReadAllTextAsync(md5File)).Trim();
    }

    private sealed class RecordingRunner : IFfmpegRunner
    {
        private readonly IFfmpegRunner _inner;

        public RecordingRunner(IFfmpegRunner inner) => _inner = inner;

        public List<List<string>> Commands { get; } = new();

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Commands.Add(args.ToList().ToList());
            return _inner.RunAsync(args, totalDuration, progress, ct);
        }
    }

    /// <summary>One grab through the service (bucketed, or exact) and the same picture today: asserts the flag and the decoded md5.</summary>
    private async Task AssertGrabAsync(string clip, double seconds, bool exact, bool flagged)
    {
        var runner = new RecordingRunner(Real());
        var service = Service(runner);
        var time = TimeSpan.FromSeconds(seconds);

        var picture = exact
            ? await service.GetExactThumbnailAsync(clip, time, Width, CancellationToken.None)
            : await service.GetThumbnailAsync(clip, time, Width, CancellationToken.None);

        picture.Should().NotBeNull($"{Path.GetFileName(clip)} @{seconds} s");
        // The bucketed request seeks to the floored second; the exact one to the time itself: today's reference follows the request.
        var seek = exact ? seconds : Math.Floor(seconds);
        var serviceMd5 = await DecodedMd5Async(picture!);
        var todaysMd5 = await DecodedMd5Async(await GrabTodayAsync(clip, seek));
        _output.WriteLine($"{Path.GetFileName(clip)} @{seconds}s {(exact ? "exact" : "bucketed")}: service {serviceMd5} · today {todaysMd5} · runs {runner.Commands.Count}");
        serviceMd5.Should().Be(todaysMd5, $"{Path.GetFileName(clip)} at {seconds} s must show today's picture");
        var run = runner.Commands.Should().ContainSingle("one run, no retry").Subject;
        if (flagged)
        {
            run.Should().Contain(Flag);
        }
        else
        {
            run.Should().NotContain(Flag);
        }
    }

    // serves-spec: SPEC-005#I27 - the repro written first: a zero-start H.264 MP4's grab (bucketed and exact, on a keyframe and mid-GOP)
    // is ONE run carrying the flag - T-195's grab was unflagged, so this fails on it - and decodes to the same picture as today's grab.
    [Trait("serves-spec", "SPEC-005")]
    [SkippableFact]
    public async Task AZeroStartH264Mp4_IsGrabbedWithTheFlag_AndShowsTodaysPicture()
    {
        Require();
        var clip = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "zero.mp4", 12, 30, "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-bf", "2");
        (await StartOfAsync(clip)).Should().Be(TimeSpan.Zero, "precondition: the ffmpeg default MP4 starts at exactly 0");

        foreach (var seconds in new[] { 2.0, 3.0, 5.0 })
        {
            await AssertGrabAsync(clip, seconds, exact: false, flagged: true);
        }

        foreach (var seconds in new[] { 4.0, 5.5 })
        {
            await AssertGrabAsync(clip, seconds, exact: true, flagged: true);
        }
    }

    // serves-spec: SPEC-005#I27 - HEVC is correct too (the skip gains it nothing, but it must not change the picture).
    [Trait("serves-spec", "SPEC-005")]
    [SkippableFact]
    public async Task AZeroStartHevcMp4_ShowsTodaysPicture_WithTheFlag()
    {
        Require();
        Skip.IfNot(await SmartCutJointIntegrityTests.HasEncoderAsync("libx265"), "the bundled ffmpeg has no libx265");
        var clip = await SmartCutJointIntegrityTests.MakeHevcFixtureAsync(_dir);
        (await StartOfAsync(clip)).Should().Be(TimeSpan.Zero, "precondition");

        foreach (var seconds in new[] { 2.0, 3.0 })
        {
            await AssertGrabAsync(clip, seconds, exact: false, flagged: true);
        }
    }

    // serves-spec: SPEC-005#I27 - a 90-degree display matrix (a phone's portrait clip) is still the same picture, rotation applied or not as today.
    [Trait("serves-spec", "SPEC-005")]
    [SkippableFact]
    public async Task ARotatedZeroStartMp4_ShowsTodaysPicture_WithTheFlag()
    {
        Require();
        var src = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "plain.mp4", 8, 30, "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");
        var rotated = Path.Combine(_dir, "rotated.mp4");
        await RunAsync(FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error", "-display_rotation", "90").Input(src).Raw("-c", "copy").Output(rotated));
        (await StartOfAsync(rotated)).Should().Be(TimeSpan.Zero, "precondition");

        await AssertGrabAsync(rotated, 3.0, exact: false, flagged: true);
    }

    // serves-spec: SPEC-005#I27 - T-195's round-1 regression, kept: the first part of the app's own lossless split (SplitArgsBuilder.SegmentMuxer on
    // an H.264 B-frame + AAC MP4, format start about 0.043 s), an -output_ts_offset 10 MOV (9.976 s) and MPEG-TS content named .mp4
    // (about 1.44 s) are ONE run with no flag and today's picture, at 1 s and 3 s - with the start-time source wired, as in the app.
    [Trait("serves-spec", "SPEC-005")]
    [SkippableFact]
    public async Task ClipsThatStartLater_AreGrabbedUnflagged_WithTodaysPicture()
    {
        Require();
        var src = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "bframes.mp4", 12, 30, "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-bf", "2");
        await RunAsync(SplitArgsBuilder.SegmentMuxer(
            src, new[] { TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) }, Path.Combine(_dir, "part%03d.mp4"), SplitEngine.HalfFrameDelta(30)));
        var firstPart = Path.Combine(_dir, "part000.mp4");
        var offset = Path.Combine(_dir, "offset10.mov");
        await RunAsync(FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error").Input(src).Raw("-c", "copy", "-output_ts_offset", "10").Output(offset));
        var mislabelled = Path.Combine(_dir, "mislabelled.mp4");
        await RunAsync(FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error").Input(src).Raw("-c", "copy", "-f", "mpegts").Output(mislabelled));

        foreach (var clip in new[] { firstPart, offset, mislabelled })
        {
            var start = await StartOfAsync(clip);
            _output.WriteLine($"{Path.GetFileName(clip)}: format start {start.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)} s");
            start.Should().BeGreaterThan(TimeSpan.Zero, $"precondition: {Path.GetFileName(clip)} does not start at 0");
            foreach (var seconds in new[] { 1.0, 3.0 })
            {
                await AssertGrabAsync(clip, seconds, exact: false, flagged: false);
            }
        }
    }
}
