using System.Globalization;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Thumbnails;
using VideoSplitJoiner.Core.Waveform;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-195 / G-059 real-ffmpeg checks, on synthetic clips this class generates itself (never a user file). The waveform
/// skips ffmpeg's stream-info decode on MP4/MOV, and its PCM is byte-identical with and without the skip — on a clip
/// that starts at timestamp 0 and on clips that do not (the first part of the app's own lossless split of a B-frame +
/// AAC MP4 starts at about 0.043 s; an <c>-output_ts_offset 10</c> MOV at about 10 s). The frame grab keeps the
/// stream-info pass on every input: skipping it also drops the format start time an input <c>-ss</c> counts from, and on
/// those clips the picture would be off (SPEC-005 I27), so on them the service's grab must decode to today's picture.
/// MKV and TS still get their picture and peaks, a no-audio MP4 waveform returns null after the retry, and TS content
/// named <c>.mp4</c> gets today's picture and today's peaks. The fake-runner rules are in <see cref="StreamInfoSkipTests"/>.
/// </summary>
public sealed class StreamInfoSkipIntegrationTests : IDisposable
{
    private const string Flag = "-nofind_stream_info";
    private const int Width = 160;

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-nofsi-real-" + Guid.NewGuid().ToString("N"));

    public StreamInfoSkipIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // SPEC-005#I27 — the T-195 round-1 regression. On the first part of the app's own lossless split (the
    // SplitArgsBuilder.SegmentMuxer recipe on an H.264 B-frame + AAC MP4) and on an -output_ts_offset MOV, the format
    // start time is not 0. A grab that skipped the stream-info pass there exited 0 with a picture one frame early (the
    // split part) or the file's first frame (the offset MOV). The service's grab must be one run with no flag and decode
    // to the same picture as today's grab args, at 1 s and 3 s.
    [Trait("serves-spec", "SPEC-005")]
    [SkippableFact]
    public async Task NonzeroStartMp4AndMov_TheServicesGrab_IsTodaysPicture_OneRunWithNoFlag()
    {
        RequireFfmpegAndFfprobe();
        foreach (var input in await MakeNonzeroStartFixturesAsync())
        {
            foreach (var seconds in new[] { 1, 3 })
            {
                var runner = new RecordingRunner(Real());
                var service = new FfmpegThumbnailService(runner, Path.Combine(_dir, "cache-" + Guid.NewGuid().ToString("N")));

                var picture = await service.GetThumbnailAsync(input, TimeSpan.FromSeconds(seconds), Width, CancellationToken.None);

                picture.Should().NotBeNull();
                var serviceMd5 = await DecodedMd5Async(picture!);
                var todaysMd5 = await DecodedMd5Async(await GrabTodayAsync(input, seconds));
                _output.WriteLine($"{Path.GetFileName(input)} @{seconds}s: service {serviceMd5} · today {todaysMd5}");
                serviceMd5.Should().Be(todaysMd5, $"{Path.GetFileName(input)} at {seconds} s must show today's picture");
                runner.Commands.Should().ContainSingle().Which.Should().NotContain(Flag);
            }
        }
    }

    // SPEC-006#I3 + I24 — the waveform does not seek, so skipping the stream-info decode is safe on the same nonzero-start
    // clips: the PCM is byte-identical with and without the flag, and the service (flagged, one run) returns the peaks of
    // today's unflagged PCM.
    [Trait("serves-spec", "SPEC-006")]
    [SkippableFact]
    public async Task NonzeroStartMp4AndMov_Waveform_PcmIsByteIdentical_WithAndWithoutTheFlag()
    {
        RequireFfmpegAndFfprobe();
        foreach (var input in await MakeNonzeroStartFixturesAsync())
        {
            var flagged = await ExtractPcmAsync(input, skipStreamInfo: true);
            var unflagged = await ExtractPcmAsync(input, skipStreamInfo: false);
            _output.WriteLine($"{Path.GetFileName(input)} PCM bytes: flagged {flagged.Length}, unflagged {unflagged.Length}");
            unflagged.Length.Should().BeGreaterThan(2, "precondition: the clip has audio");
            flagged.Should().Equal(unflagged, Path.GetFileName(input));

            var runner = new RecordingRunner(Real());
            var peaks = await new FfmpegWaveformService(runner, Path.Combine(_dir, "wf-" + Guid.NewGuid().ToString("N")))
                .GetPeaksAsync(input, 400, CancellationToken.None);

            runner.Commands.Should().ContainSingle().Which.Should().Contain(Flag);
            peaks.Should().Equal(FfmpegWaveformService.ComputePeaks(unflagged, 400)!);
        }
    }

    // SPEC-006#I3 + I24 — the waveform PCM of a zero-start MP4 is byte-identical with and without the flag, and the
    // service (flagged, one run) returns the peaks of today's unflagged PCM.
    [Trait("serves-spec", "SPEC-006")]
    [SkippableFact]
    public async Task Mp4Waveform_PcmIsByteIdentical_WithAndWithoutTheFlag()
    {
        RequireFfmpeg();
        var mp4 = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "audio.mp4", 8, 30, "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");

        var flagged = await ExtractPcmAsync(mp4, skipStreamInfo: true);
        var unflagged = await ExtractPcmAsync(mp4, skipStreamInfo: false);
        _output.WriteLine($"PCM bytes: flagged {flagged.Length}, unflagged {unflagged.Length}");
        flagged.Length.Should().BeGreaterThan(2, "precondition: the clip has audio");
        flagged.Should().Equal(unflagged);

        var runner = new RecordingRunner(Real());
        var peaks = await new FfmpegWaveformService(runner, Path.Combine(_dir, "wf-cache"))
            .GetPeaksAsync(mp4, 1800, CancellationToken.None);

        runner.Commands.Should().ContainSingle().Which.Should().Contain(Flag);
        peaks.Should().Equal(FfmpegWaveformService.ComputePeaks(unflagged, 1800)!);
    }

    // SPEC-005#I27 / SPEC-006#I3 — MKV and TS keep today's args: one unflagged run each, and they still yield a
    // picture and peaks.
    [Trait("serves-spec", "SPEC-006")]
    [SkippableFact]
    public async Task MkvAndTs_StillYieldAPictureAndPeaks_WithOneUnflaggedRunEach()
    {
        RequireFfmpeg();
        var mp4 = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "base.mp4", 6, 30, "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");

        foreach (var name in new[] { "clip.mkv", "clip.ts" })
        {
            var input = await RemuxAsync(mp4, name);
            var runner = new RecordingRunner(Real());

            var picture = await new FfmpegThumbnailService(runner, Path.Combine(_dir, "th-" + name))
                .GetThumbnailAsync(input, TimeSpan.FromSeconds(3), Width, CancellationToken.None);
            var peaks = await new FfmpegWaveformService(runner, Path.Combine(_dir, "wf-" + name))
                .GetPeaksAsync(input, 100, CancellationToken.None);

            picture.Should().NotBeNull(name);
            JpegWidth.Read(picture!).Should().Be(Width);
            peaks.Should().NotBeNull(name).And.HaveCount(100);
            runner.Commands.Should().HaveCount(2, "one grab and one extraction, no retry");
            runner.Commands.Should().AllSatisfy(c => c.Should().NotContain(Flag));
        }
    }

    // SPEC-006#I24 — a no-audio MP4 fails the flagged extraction and the retry: exactly two runs, then null.
    [Trait("serves-spec", "SPEC-006")]
    [SkippableFact]
    public async Task NoAudioMp4_Waveform_IsNullAfterTheRetry_TwoRuns()
    {
        RequireFfmpeg();
        var mp4 = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "with-audio.mp4", 4, 30, "-c:v", "libx264", "-g", "30");
        var silent = Path.Combine(_dir, "no-audio.mp4");
        await RunAsync(FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error").Input(mp4).Raw("-an", "-c", "copy").Output(silent));
        var runner = new RecordingRunner(Real());

        var peaks = await new FfmpegWaveformService(runner, Path.Combine(_dir, "wf-silent"))
            .GetPeaksAsync(silent, 100, CancellationToken.None);

        peaks.Should().BeNull("the App hides the band, as before");
        runner.Commands.Should().HaveCount(2);
        runner.Commands[0].Should().Contain(Flag);
        runner.Commands[1].Should().NotContain(Flag);
    }

    // SPEC-005#I27 + SPEC-006#I24 — MPEG-TS content in a file named .mp4 (the edge the waveform retry exists for). With
    // the bundled ffmpeg the flagged waveform run fails here ("Output file does not contain any stream"), so the peaks
    // come from the retry and equal today's. The grab is one unflagged run and shows today's picture (a flagged grab
    // there would count -ss from 0 instead of from the TS's ~1.4 s start).
    [Trait("serves-spec", "SPEC-006")]
    [SkippableFact]
    public async Task TsContentNamedMp4_GetsTodaysPictureAndTodaysPeaks()
    {
        RequireFfmpeg();
        var mp4 = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "real.mp4", 6, 30, "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0");
        var mislabelled = Path.Combine(_dir, "mislabelled.mp4");
        await RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error").Input(mp4).Raw("-c", "copy", "-f", "mpegts").Output(mislabelled));

        var thumbRunner = new RecordingRunner(Real());
        var picture = await new FfmpegThumbnailService(thumbRunner, Path.Combine(_dir, "th-mis"))
            .GetThumbnailAsync(mislabelled, TimeSpan.FromSeconds(3), Width, CancellationToken.None);

        var waveRunner = new RecordingRunner(Real());
        var peaks = await new FfmpegWaveformService(waveRunner, Path.Combine(_dir, "wf-mis"))
            .GetPeaksAsync(mislabelled, 100, CancellationToken.None);
        var unflaggedPeaks = FfmpegWaveformService.ComputePeaks(await ExtractPcmAsync(mislabelled, skipStreamInfo: false), 100);

        _output.WriteLine($"TS named .mp4: grab runs {thumbRunner.Commands.Count}, waveform runs {waveRunner.Commands.Count}");
        picture.Should().NotBeNull("best-effort: the mislabelled file still gets a picture");
        thumbRunner.Commands.Should().ContainSingle().Which.Should().NotContain(Flag);
        (await DecodedMd5Async(picture!)).Should().Be(await DecodedMd5Async(await GrabTodayAsync(mislabelled, 3)));

        peaks.Should().NotBeNull().And.Equal(unflaggedPeaks!);
        waveRunner.Commands[0].Should().Contain(Flag, "the extension is all the waveform's gate reads");
        waveRunner.Commands.Should().HaveCountLessThanOrEqualTo(2, "at most one retry");
        if (waveRunner.Commands.Count == 2)
        {
            waveRunner.Commands[1].Should().NotContain(Flag);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private void RequireFfmpeg() => FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg");

    private void RequireFfmpegAndFfprobe()
    {
        RequireFfmpeg();
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
    }

    private static FfmpegRunner Real() =>
        new(new FfmpegBinaryLocator(ffmpegOverride: FfmpegTestBinaries.FfmpegRequired));

    private static async Task RunAsync(FfmpegArgs args)
    {
        var run = await Real().RunAsync(args);
        run.ExitCode.Should().Be(0, "fixture step failed: " + run.StdErrText);
    }

    private async Task<string> RemuxAsync(string source, string name)
    {
        var path = Path.Combine(_dir, name);
        await RunAsync(FfmpegArgs.ForFfmpeg().Raw("-y", "-loglevel", "error").Input(source).Raw("-c", "copy").Output(path));
        return path;
    }

    /// <summary>
    /// Two clips whose format start time is not 0, each asserted so by ffprobe: the first part of the app's own lossless
    /// split (the exact <see cref="SplitArgsBuilder.SegmentMuxer"/> tokens, cuts at 4 s and 8 s) of a 12 s H.264 B-frame
    /// + AAC MP4, and that MP4 remuxed to a MOV with <c>-output_ts_offset 10</c>.
    /// </summary>
    private async Task<string[]> MakeNonzeroStartFixturesAsync()
    {
        var src = await SmartCutJointIntegrityTests.MakeFixtureAsync(
            _dir, "bframes.mp4", 12, 30, "-c:v", "libx264", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-bf", "2");

        var pattern = Path.Combine(_dir, "part%03d.mp4");
        // The app's half-frame -segment_time_delta at 30 fps since T-219 (SplitEngine.HalfFrameDelta).
        await RunAsync(SplitArgsBuilder.SegmentMuxer(
            src, new[] { TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) }, pattern, SplitEngine.HalfFrameDelta(30)));
        var firstPart = Path.Combine(_dir, "part000.mp4");

        var offset = Path.Combine(_dir, "offset10.mov");
        await RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error").Input(src).Raw("-c", "copy", "-output_ts_offset", "10").Output(offset));

        foreach (var clip in new[] { firstPart, offset })
        {
            var start = await FormatStartTimeAsync(clip);
            _output.WriteLine($"{Path.GetFileName(clip)}: format start_time {start.ToString(CultureInfo.InvariantCulture)} s");
            start.Should().BeGreaterThan(0, $"precondition: {Path.GetFileName(clip)} must not start at timestamp 0");
        }

        return new[] { firstPart, offset };
    }

    private static async Task<double> FormatStartTimeAsync(string clip)
    {
        var ffprobe = new FfprobeRunner(new FfmpegBinaryLocator(ffprobeOverride: FfmpegTestBinaries.FfprobeRequired));
        var text = await ffprobe.RunJsonAsync(FfmpegArgs.ForFfprobe()
            .Raw("-v", "error", "-show_entries", "format=start_time", "-of", "csv=p=0")
            .Input(clip));
        return double.Parse(text.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Grab <paramref name="seconds"/> with today's frame-grab tokens, spelled out here rather than taken from
    /// <c>BuildArgs</c>, so a flag added to the service's args can never also reach this reference.
    /// </summary>
    private async Task<string> GrabTodayAsync(string input, int seconds)
    {
        var path = Path.Combine(_dir, "today-" + Guid.NewGuid().ToString("N") + ".jpg");
        await RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-ss", seconds.ToString(CultureInfo.InvariantCulture))
            .Input(input)
            .Raw("-frames:v", "1", "-vf", $"scale={Width.ToString(CultureInfo.InvariantCulture)}:-1", "-y")
            .Output(path));
        File.Exists(path).Should().BeTrue();
        return path;
    }

    private async Task<byte[]> ExtractPcmAsync(string input, bool skipStreamInfo)
    {
        var path = Path.Combine(_dir, "pcm-" + Guid.NewGuid().ToString("N") + ".pcm");
        await RunAsync(FfmpegWaveformService.BuildArgs(
            input, FfmpegWaveformService.DefaultSampleRateHz, path, skipStreamInfo));
        return await File.ReadAllBytesAsync(path);
    }

    /// <summary>The md5 of the decoded picture (ffmpeg's md5 muxer over the raw frame), not of the JPEG bytes.</summary>
    private async Task<string> DecodedMd5Async(string jpeg)
    {
        var md5File = Path.Combine(_dir, "md5-" + Guid.NewGuid().ToString("N") + ".txt");
        await RunAsync(FfmpegArgs.ForFfmpeg().Raw("-v", "error").Input(jpeg).Raw("-f", "md5", "-y").Output(md5File));
        return (await File.ReadAllTextAsync(md5File)).Trim();
    }

    /// <summary>Forwards every run to the real runner and records its tokens.</summary>
    private sealed class RecordingRunner : IFfmpegRunner
    {
        private readonly IFfmpegRunner _inner;

        public RecordingRunner(IFfmpegRunner inner) => _inner = inner;

        public List<List<string>> Commands { get; } = new();

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            Commands.Add(args.ToList().ToList());
            return _inner.RunAsync(args, totalDuration, progress, ct);
        }
    }
}
