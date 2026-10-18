using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// The T-219 lossless-boundary check (SPEC-001 I66): where a <c>-c copy</c> part really starts and how many frames it
/// holds, measured on the decoded pictures, never on durations. A part starts on its planned keyframe when its first
/// decoded frame is md5-identical to the source's frame at that keyframe; it holds its planned frames when its
/// <c>-count_frames</c> count equals the number of source frames whose presentation time lies in the part's planned
/// range. Every run goes through the app's own <see cref="FfmpegRunner"/> / <see cref="FfprobeRunner"/> with typed
/// <see cref="FfmpegArgs"/>; the media is synthetic (callers generate it in their own temp folder).
/// </summary>
internal static class LosslessBoundary
{
    /// <summary>One decoded video frame: its presentation time (in the file's own time base) and its md5.</summary>
    internal readonly record struct Frame(double Seconds, string Hash);

    private static FfmpegBinaryLocator Locator() => new(
        ffmpegOverride: FfmpegTestBinaries.FfmpegRequired, ffprobeOverride: FfmpegTestBinaries.FfprobeRequired);

    /// <summary>
    /// Decode the first video stream of <paramref name="mediaPath"/> to <c>framemd5</c> and parse it, in presentation
    /// order. <c>-copyts</c> keeps each frame's own timestamp, so a source's times are the keyframe list's time base
    /// (source pts, also on a TS whose <c>start_time</c> is not zero); <c>-enc_time_base:v demux</c> writes them in the
    /// stream's own time base and <c>-fps_mode passthrough</c> neither drops nor duplicates a frame.
    /// </summary>
    public static async Task<IReadOnlyList<Frame>> FramesAsync(string mediaPath, string workDir)
    {
        var outputPath = Path.Combine(workDir, Path.GetFileName(mediaPath) + "-" + Guid.NewGuid().ToString("N") + ".framemd5");
        var run = await new FfmpegRunner(Locator()).RunAsync(
            FfmpegArgs.ForFfmpeg()
                .Raw("-v", "error", "-copyts")
                .Input(mediaPath)
                .Raw("-map", "0:v:0", "-fps_mode", "passthrough", "-enc_time_base:v", "demux", "-f", "framemd5", "-y")
                .Output(outputPath));
        run.ExitCode.Should().Be(0, $"framemd5 of '{mediaPath}' must decode: {run.StdErrText}");

        long tbNum = 1, tbDen = 1;
        var frames = new List<Frame>();
        foreach (var raw in await File.ReadAllLinesAsync(outputPath))
        {
            var line = raw.Trim();
            if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
            {
                var parts = line.Substring("#tb 0:".Length).Trim().Split('/');
                tbNum = long.Parse(parts[0], CultureInfo.InvariantCulture);
                tbDen = long.Parse(parts[1], CultureInfo.InvariantCulture);
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var cols = line.Split(',');
            if (cols.Length < 6 || cols[0].Trim() != "0")
            {
                continue;
            }

            var pts = long.Parse(cols[2].Trim(), CultureInfo.InvariantCulture);
            frames.Add(new Frame(pts * (double)tbNum / tbDen, cols[5].Trim()));
        }

        return frames;
    }

    /// <summary>The first video stream's frame count, decoded and counted by ffprobe (<c>-count_frames</c>).</summary>
    public static async Task<int> CountFramesAsync(string mediaPath)
    {
        var json = await new FfprobeRunner(Locator()).RunJsonAsync(
            FfmpegArgs.ForFfprobe()
                .Raw("-v", "error", "-count_frames", "-select_streams", "v:0")
                .Raw("-show_entries", "stream=nb_read_frames", "-print_format", "json")
                .Input(mediaPath));
        using var doc = JsonDocument.Parse(json);
        var value = doc.RootElement.GetProperty("streams")[0].GetProperty("nb_read_frames").GetString();
        return int.Parse(value!, CultureInfo.InvariantCulture);
    }

    /// <summary>ffprobe's <c>format.start_time</c> in seconds, read directly (not through the code under test).</summary>
    public static async Task<double> FormatStartTimeAsync(string mediaPath)
    {
        var json = await new FfprobeRunner(Locator()).RunJsonAsync(
            FfmpegArgs.ForFfprobe()
                .Raw("-v", "error", "-show_entries", "format=start_time", "-print_format", "json")
                .Input(mediaPath));
        using var doc = JsonDocument.Parse(json);
        return double.Parse(
            doc.RootElement.GetProperty("format").GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture);
    }

    /// <summary>Half the typical frame interval of <paramref name="frames"/>: the tolerance for "the frame at a time".</summary>
    public static double HalfFrame(IReadOnlyList<Frame> frames)
    {
        var deltas = new List<double>();
        for (var i = 1; i < frames.Count && deltas.Count < 64; i++)
        {
            var d = frames[i].Seconds - frames[i - 1].Seconds;
            if (d > 0)
            {
                deltas.Add(d);
            }
        }

        deltas.Should().NotBeEmpty("the source must have at least two frames");
        deltas.Sort();
        return deltas[deltas.Count / 2] / 2;
    }

    /// <summary>The index of the source frame presented at <paramref name="time"/> (within half a frame).</summary>
    public static int IndexAt(IReadOnlyList<Frame> source, TimeSpan time)
    {
        var half = HalfFrame(source);
        var t = time.TotalSeconds;
        for (var i = 0; i < source.Count; i++)
        {
            if (Math.Abs(source[i].Seconds - t) < half)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"no source frame is presented at {t:0.######} s");
    }

    /// <summary>
    /// How many source frames are presented in <c>[from, to)</c> — a null <paramref name="from"/> is the first frame, a
    /// null <paramref name="to"/> runs to the last. This is the part's planned frame count.
    /// </summary>
    public static int CountIn(IReadOnlyList<Frame> source, TimeSpan? from, TimeSpan? to)
    {
        var half = HalfFrame(source);
        var lo = from?.TotalSeconds - half ?? double.NegativeInfinity;
        var hi = to?.TotalSeconds - half ?? double.PositiveInfinity;
        return source.Count(f => f.Seconds >= lo && f.Seconds < hi);
    }

    /// <summary>"frame 90 (md5 …)" or "no frame with that md5" — for a failure message and the Build log.</summary>
    public static string Describe(IReadOnlyList<Frame> source, string hash)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (source[i].Hash == hash)
            {
                return string.Create(CultureInfo.InvariantCulture, $"source frame {i} ({source[i].Seconds:0.######} s)");
            }
        }

        return "no source frame";
    }
}
