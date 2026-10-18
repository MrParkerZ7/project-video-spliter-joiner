using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// The joint-timing check for a frame-exact result (T-190, SPEC-001 I68): where in time the kept part begins, whether
/// the audio runs on across the joint, and how long each stream is — measured on the final's own packet timestamps with
/// ffprobe, never on durations alone. <see cref="SmartCutJointIntegrity"/> (T-189) answers "are the frames from the joint
/// on the source's frames, and does the file decode cleanly"; this answers "are they presented at the right time".
///
/// <para>Every run goes through the app's own <see cref="FfmpegRunner"/> / <see cref="FfprobeRunner"/> with typed
/// <see cref="FfmpegArgs"/>; the media is synthetic (callers generate it in their own temp folder).</para>
///
/// <para><b>The time base of every reading.</b> The head's AAC priming packet has a negative timestamp and the
/// concat-to-mp4 shifts every stream to make it non-negative, so the final's video does not start at 0 (about 21 ms in
/// practice, T-190 § Design, <i>joint time base</i>). "Presented at <c>HeadEnd − Start</c>" therefore always means
/// <b>relative to the final's first video frame</b>: a frame's pts minus the final video stream's <c>start_time</c>. It
/// holds for a Start on the source's frame grid: the first head frame is then at Start itself. A Start off the grid
/// leaves the head's frames on a grid up to one frame off the tail's (discovered by T-190, not fixed by it), so these
/// checks are for on-grid Starts.</para>
/// </summary>
internal static class SmartCutJointTiming
{
    /// <summary>One packet: presentation and decoding time, duration, all in seconds.</summary>
    internal readonly record struct Pkt(double Pts, double Dts, double Duration);

    private static FfmpegBinaryLocator Locator() => new(
        ffmpegOverride: FfmpegTestBinaries.FfmpegRequired, ffprobeOverride: FfmpegTestBinaries.FfprobeRequired);

    private static FfmpegRunner Runner() => new(Locator());

    private static FfprobeRunner Prober() => new(Locator());

    // ---- Fixtures ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The T-190 repro source: H.264 with B-frames (libx264, <c>-bf 2</c>, so <c>has_b_frames</c> 2) at 30 fps and a
    /// <paramref name="gopFrames"/>-frame GOP (60 = 2 s, 300 = 10 s), 320x240, plus 48 kHz stereo AAC at 192 kbit/s —
    /// the bench's <c>4k</c> recipe in miniature. The audio shape matters: with 44.1 kHz mono AAC the shipped engine's
    /// final decodes without a single log line, with this one it prints the null muxer's
    /// <c>non monotonically increasing dts</c> lines (measured on the bundled ffmpeg n7.1.5: 2 lines for a cut at 10.5 s
    /// on a 30 s source, none at 3.3 s), which is what the zero-line criterion needs a fixture to show.
    /// </summary>
    public static async Task<string> MakeSourceAsync(string dir, string name, double seconds, int gopFrames)
    {
        var path = Path.Combine(dir, name);
        var s = seconds.ToString(CultureInfo.InvariantCulture);
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error")
            .Raw("-f", "lavfi", "-i", $"testsrc2=size=320x240:rate=30:duration={s}")
            .Raw("-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={s}")
            .Raw("-map", "0:v:0", "-map", "1:a:0")
            .Raw("-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p")
            .Raw("-g", gopFrames.ToString(CultureInfo.InvariantCulture), "-keyint_min", gopFrames.ToString(CultureInfo.InvariantCulture))
            .Raw("-sc_threshold", "0", "-bf", "2")
            .Raw("-c:a", "aac", "-b:a", "192000", "-ar", "48000", "-ac", "2")
            .Raw("-t", s)
            .Output(path));
        run.ExitCode.Should().Be(0, "the fixture must be produced before anything is asserted: " + run.StdErrText);
        File.Exists(path).Should().BeTrue();
        return path;
    }

    /// <summary>
    /// A source whose video starts one AAC frame AFTER its audio while its format starts at 0 — the shape of a file the
    /// app's own Join writes, and of the bench's <c>4k10m</c> and <c>mkv</c> fixtures: a <c>-c copy</c> concat or remux
    /// carries the head clip's AAC priming packet over and shifts every stream so the audio starts at 0. Made here the
    /// way <c>4k10m</c> is: <paramref name="source"/> concatenated with itself, stream-copied.
    /// </summary>
    public static async Task<string> MakeLateVideoSourceAsync(string dir, string name, string source)
    {
        var list = Path.Combine(dir, name + ".txt");
        await File.WriteAllTextAsync(list, VideoSplitJoiner.Core.Join.JoinArgsBuilder.RenderConcatList(new[] { source, source }));
        var path = Path.Combine(dir, name);
        var run = await Runner().RunAsync(VideoSplitJoiner.Core.Join.JoinArgsBuilder.ConcatCopy(list, path));
        run.ExitCode.Should().Be(0, "the concatenated fixture must be produced first: " + run.StdErrText);
        return path;
    }

    /// <summary>Remux <paramref name="source"/> to <paramref name="name"/> (its extension picks the container), <c>-c copy</c>.</summary>
    public static async Task<string> RemuxAsync(string dir, string name, string source)
    {
        var path = Path.Combine(dir, name);
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error").Input(source).Raw("-map", "0", "-c", "copy").Output(path));
        run.ExitCode.Should().Be(0, "the remuxed fixture must be produced first: " + run.StdErrText);
        return path;
    }

    // ---- Reading a file ---------------------------------------------------------------------------------------

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : double.NaN;

    /// <summary>
    /// The packets of one stream, in file order. The source's reference is every packet read and then filtered by pts
    /// (<see cref="MeasureAsync"/>) — the same packets <c>ffprobe -read_intervals Start%End</c> reports, which seeks back to a
    /// keyframe before the interval and so needs the same filter, without a seek whose result depends on the container.
    /// </summary>
    public static async Task<IReadOnlyList<Pkt>> PacketsAsync(string path, string selector)
    {
        var args = FfmpegArgs.ForFfprobe().Raw("-v", "error", "-select_streams", selector)
            .Raw("-show_entries", "packet=pts_time,dts_time,duration_time", "-print_format", "json").Input(path);
        using var doc = JsonDocument.Parse(await Prober().RunJsonAsync(args));
        var list = new List<Pkt>();
        foreach (var p in doc.RootElement.GetProperty("packets").EnumerateArray())
        {
            list.Add(new Pkt(Num(p, "pts_time"), Num(p, "dts_time"), Num(p, "duration_time")));
        }

        return list;
    }

    private static async Task<double> EntryAsync(string path, string selector, string scope, string key)
    {
        using var doc = JsonDocument.Parse(await Prober().RunJsonAsync(FfmpegArgs.ForFfprobe()
            .Raw("-v", "error", "-select_streams", selector, "-show_entries", $"{scope}={key}", "-print_format", "json").Input(path)));
        var streams = doc.RootElement.GetProperty("streams");
        return Num(streams[0], key);
    }

    /// <summary>The stream's own <c>start_time</c> (seconds), as ffprobe reports it.</summary>
    public static Task<double> StreamStartAsync(string path, string selector) => EntryAsync(path, selector, "stream", "start_time");

    /// <summary>The video stream's <c>has_b_frames</c>: how many frames the decoder reorders.</summary>
    public static async Task<int> HasBFramesAsync(string path)
    {
        using var doc = JsonDocument.Parse(await Prober().RunJsonAsync(FfmpegArgs.ForFfprobe()
            .Raw("-v", "error", "-select_streams", "v:0", "-show_entries", "stream=has_b_frames", "-print_format", "json").Input(path)));
        return doc.RootElement.GetProperty("streams")[0].GetProperty("has_b_frames").GetInt32();
    }

    /// <summary>
    /// The video stream's frame interval in seconds, from its <c>r_frame_rate</c> (1/30 for these fixtures). The median of
    /// the packet timestamps' differences is not used: Matroska counts in milliseconds, so it reads 0.033 for 1/30.
    /// </summary>
    public static async Task<double> FrameIntervalAsync(string path)
    {
        using var doc = JsonDocument.Parse(await Prober().RunJsonAsync(FfmpegArgs.ForFfprobe()
            .Raw("-v", "error", "-select_streams", "v:0", "-show_entries", "stream=r_frame_rate", "-print_format", "json").Input(path)));
        var rate = doc.RootElement.GetProperty("streams")[0].GetProperty("r_frame_rate").GetString()!.Split('/');
        return double.Parse(rate[1], CultureInfo.InvariantCulture) / double.Parse(rate[0], CultureInfo.InvariantCulture);
    }

    /// <summary>ffprobe's <c>format.start_time</c> (seconds).</summary>
    public static Task<double> FormatStartAsync(string path) => LosslessBoundary.FormatStartTimeAsync(path);

    /// <summary>The number of video frames in the file (<c>-count_frames</c>).</summary>
    public static Task<int> FrameCountAsync(string path) => LosslessBoundary.CountFramesAsync(path);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v) && v > 0).OrderBy(v => v).ToList();
        sorted.Should().NotBeEmpty("a stream needs at least two packets to have a frame interval");
        return sorted[sorted.Count / 2];
    }

    /// <summary>An audio stream's span: first pts to last pts plus that packet's duration (an AAC packet's duration is its own).</summary>
    private static double Span(IReadOnlyList<Pkt> sortedByPts) =>
        sortedByPts.Count == 0 ? 0 : sortedByPts[^1].Pts + sortedByPts[^1].Duration - sortedByPts[0].Pts;

    /// <summary>
    /// A video stream's length: its frame count times the frame interval (a constant-rate stream's duration). Not first pts to
    /// last pts: a cut with an end keeps the next keyframe and the P frame coded after it (the concat <c>outpoint</c> is a
    /// decoding timestamp, T-220), and the B-frames between them are not there, so that pts range is two frames longer than
    /// the frames it holds. Not the last packet's own duration either: the muxer gives the cut's last sample a longer one.
    /// </summary>
    private static double VideoSpan(IReadOnlyList<Pkt> sortedByPts, double frameInterval) => sortedByPts.Count * frameInterval;

    // ---- The measurement --------------------------------------------------------------------------------------

    /// <summary>What <see cref="MeasureAsync"/> read from one final, for the asserts and the Build log.</summary>
    internal sealed record Measurement(
        double FinalVideoStart,
        double FrameInterval,
        double AacFrame,
        int HeadFrames,
        double ExpectedJointRel,
        double JointAbs,
        double JointRel,
        int AudioNotIncreasing,
        double AudioWorstGap,
        double AudioFirstPts,
        double AudioStartAfterDrop,
        double OutVideoSpan,
        double OutAudioSpan,
        double RefVideoSpan,
        double RefAudioSpan,
        int OutVideoFrames,
        int RefVideoFrames,
        int RefAudioPackets,
        int HasBFrames,
        TimeSpan Start,
        TimeSpan? End,
        int DecodeLines)
    {
        /// <summary>The final's video frames beyond the source's frames in <c>[Start, End)</c> — the T-220 end overrun.</summary>
        public int OverrunFrames => OutVideoFrames - RefVideoFrames;

        /// <summary>Output audio-minus-video duration (audio counted from its first packet at or after the video's start).</summary>
        public double OutAv => OutAudioSpan - OutVideoSpan;

        /// <summary>The source's audio-minus-video duration over <c>[Start, End)</c>.</summary>
        public double RefAv => RefAudioSpan - RefVideoSpan;

        /// <summary>The joint frame's offset from <c>HeadEnd − Start</c>, in frames.</summary>
        public double JointOffsetFrames => (JointRel - ExpectedJointRel) / FrameInterval;

        /// <summary>The target length of the video: <c>End − Start</c>, or the source's reference span for a start-only cut.</summary>
        public double TargetVideoSpan => End is { } e ? (e - Start).TotalSeconds : RefVideoSpan;

        public string Describe() => string.Create(
            CultureInfo.InvariantCulture,
            $"joint frame {HeadFrames}: rel {JointRel:0.000000} s (abs {JointAbs:0.000000}, final video starts {FinalVideoStart:0.000000}), "
            + $"expected {ExpectedJointRel:0.000000} s ({JointOffsetFrames:+0.00;-0.00;0.00} frames); "
            + $"audio: first pts {AudioFirstPts:0.000000}, counted from {AudioStartAfterDrop:0.000000}, {AudioNotIncreasing} non-increasing, worst gap/overlap {AudioWorstGap * 1000:0.00} ms (AAC frame {AacFrame * 1000:0.00} ms); "
            + $"video span {OutVideoSpan:0.000} s ({OutVideoFrames} frames; source [Start, End) {RefVideoSpan:0.000} s, {RefVideoFrames} frames, overrun {OverrunFrames}), "
            + $"A-V {OutAv * 1000:+0.0;-0.0;0.0} ms (source {RefAv * 1000:+0.0;-0.0;0.0} ms, {RefAudioPackets} audio packets, span {RefAudioSpan:0.000} s); full decode printed {DecodeLines} line(s)");
    }

    /// <summary>
    /// Measure <paramref name="finalPath"/> — the Exact result of cutting <paramref name="sourcePath"/> from
    /// <paramref name="start"/> (to <paramref name="end"/>, or to the end of the file when null), whose copied part
    /// begins at the keyframe <paramref name="headEnd"/> — against the source's own packet timestamps.
    /// <paramref name="headFrames"/> is the head's frame count (read from the head the engine wrote), so the joint frame
    /// is the one at that index in presentation order.
    /// </summary>
    public static async Task<Measurement> MeasureAsync(
        string finalPath, string sourcePath, TimeSpan start, TimeSpan? end, TimeSpan headEnd, int headFrames)
    {
        // --- the final ---
        var videoStart = await StreamStartAsync(finalPath, "v:0");
        var video = (await PacketsAsync(finalPath, "v:0")).OrderBy(p => p.Pts).ToList();
        var frame = await FrameIntervalAsync(finalPath);
        headFrames.Should().BeLessThan(video.Count, "the joint frame must exist in the final");

        // Audio in file order: the order the player reads it in.
        var audio = await PacketsAsync(finalPath, "a:0");
        var aac = Median(audio.Select(p => p.Duration));
        var notIncreasing = 0;
        var worstGap = 0.0;
        for (var i = 1; i < audio.Count; i++)
        {
            if (audio[i].Pts <= audio[i - 1].Pts)
            {
                notIncreasing++;
            }

            var gap = audio[i].Pts - (audio[i - 1].Pts + audio[i - 1].Duration);
            if (Math.Abs(gap) > Math.Abs(worstGap))
            {
                worstGap = gap;
            }
        }

        // Count the audio from its first packet at or after the final's first video pts: that drops the head's AAC
        // priming packet, which the source's own packets in [Start, End) do not have.
        var audioCounted = audio.Where(p => p.Pts >= videoStart - 1e-6).OrderBy(p => p.Pts).ToList();

        // --- the reference: the source's own video and audio packets whose pts falls in [Start, End) ---
        var startS = start.TotalSeconds;
        var endS = end?.TotalSeconds;
        bool InRange(Pkt p) => p.Pts >= startS - 1e-6 && (endS is not { } e || p.Pts < e - 1e-6);
        var refVideo = (await PacketsAsync(sourcePath, "v:0")).Where(InRange).OrderBy(p => p.Pts).ToList();
        var refAudio = (await PacketsAsync(sourcePath, "a:0")).Where(InRange).OrderBy(p => p.Pts).ToList();

        var (_, lines) = await SmartCutJointIntegrity.DecodeAsync(finalPath);

        return new Measurement(
            videoStart,
            frame,
            aac,
            headFrames,
            (headEnd - start).TotalSeconds,
            video[headFrames].Pts,
            video[headFrames].Pts - videoStart,
            notIncreasing,
            worstGap,
            audio[0].Pts,
            audioCounted.Count > 0 ? audioCounted[0].Pts : double.NaN,
            VideoSpan(video, frame),
            Span(audioCounted),
            VideoSpan(refVideo, frame),
            Span(refAudio),
            video.Count,
            refVideo.Count,
            refAudio.Count,
            await HasBFramesAsync(sourcePath),
            start,
            end,
            lines.Count);
    }

    // ---- The recording wrapper ----------------------------------------------------------------------------------

    /// <summary>
    /// The real runner, recorded: every command's tokens, the engine's temp directory listing after each run, a copy of
    /// the head file (the engine sweeps its temp dir before the test can look), and the concat list the second run read.
    /// </summary>
    internal sealed class RecordingRealRunner : IFfmpegRunner
    {
        private readonly IFfmpegRunner _inner;
        private readonly string _keepDir;

        public RecordingRealRunner(IFfmpegRunner inner, string keepDir)
        {
            _inner = inner;
            _keepDir = keepDir;
            Directory.CreateDirectory(keepDir);
        }

        /// <summary>The tokens of every command, in order.</summary>
        public List<IReadOnlyList<string>> Commands { get; } = new();

        /// <summary>After run N, the file names in the engine's <c>.vsj-smartcut-*</c> temp dir.</summary>
        public List<IReadOnlyList<string>> TempDirListings { get; } = new();

        /// <summary>The body of every concat list a run read (the <c>-f concat</c> input).</summary>
        public List<string> ConcatLists { get; } = new();

        /// <summary>A copy of the head the first run wrote, or null.</summary>
        public string? HeadCopy { get; private set; }

        public async Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var tokens = args.ToList().ToList();
            Commands.Add(tokens);

            var concatAt = tokens.IndexOf("concat");
            if (concatAt >= 0)
            {
                var input = tokens[tokens.IndexOf("-i") + 1];
                ConcatLists.Add(await File.ReadAllTextAsync(input, ct));
            }

            var result = await _inner.RunAsync(args, totalDuration, progress, ct);

            var output = tokens[^1];
            var dir = Path.GetDirectoryName(output);
            if (dir is not null && Path.GetFileName(dir).StartsWith(".vsj-smartcut-", StringComparison.Ordinal))
            {
                TempDirListings.Add(Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>().ToList());
                if (HeadCopy is null && Path.GetFileName(output).StartsWith("head.", StringComparison.Ordinal) && File.Exists(output))
                {
                    HeadCopy = Path.Combine(_keepDir, Path.GetFileName(output));
                    File.Copy(output, HeadCopy, overwrite: true);
                }
            }

            return result;
        }
    }
}
