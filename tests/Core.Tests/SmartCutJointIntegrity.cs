using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// The joint-integrity check for a frame-exact result (T-189, SPEC-001 I65): does the final decode cleanly, and are its
/// video frames from the joint on the SOURCE's own frames, in order?
///
/// <para><b>Why it exists.</b> An Exact result is a re-encoded head followed by copied source packets. On HEVC the
/// concat output took its codec configuration from the head (libx265's hev1 extradata) while the copied tail's slices
/// referred to parameter sets that exist only in the source's hvc1 extradata, so every frame after the joint decoded
/// against the wrong VPS/SPS/PPS — a file that looked fine until the joint. Duration and probe checks cannot see that;
/// decoding the whole final and hashing its frames can.</para>
///
/// <para><b>What it measures</b> (both through the bundled ffmpeg, run through <see cref="FfmpegRunner"/>):</para>
/// <list type="number">
/// <item>It decodes the whole final with <c>-v error … -f null -</c> and counts the decoder and demuxer error lines.
/// The null muxer's own lines (<c>[null @ …] … non monotonically increasing dts to muxer</c>) are excluded: shipped
/// H.264 finals print them, they are a timing symptom, and T-190's joint-timing test owns them. The full line count is
/// returned too (<see cref="Report.AllErrorLines"/>), for T-190.</item>
/// <item>It writes <c>framemd5</c> of the final's video from <c>HeadEnd − Start</c> (measured from the final's first
/// video frame) and of the source's video from <c>HeadEnd</c>, and compares the hash sequences — content, not
/// timestamps, which are T-190's concern. Both passes run with <c>-copyts</c>, so a frame's time is the file's own
/// timestamp: the same time base as the probe's keyframe times that <c>Start</c> and <c>HeadEnd</c> come from, also on
/// a source whose <c>start_time</c> is not 0 (without it ffmpeg subtracts <c>start_time</c>, and the source tail would be
/// selected <c>start_time</c> late). The two tails must have the SAME number of frames: a final that lost the end of its
/// tail, or gained frames, fails even when every frame it has matches.</item>
/// </list>
///
/// <para><b>Scope.</b> The check is for an open-ended cut (no end): the source tail runs to its last frame, so the final's
/// must too. It looks at the joint and the tail only — never the head's content — so on a source whose
/// <c>start_time</c> is not 0, where the shipped engine's head and tail overlap by <c>start_time</c> (T-190, which makes
/// such a source fall back), a pass says nothing about the head.</para>
///
/// <para>Closed-GOP H.264 passes it on the shipped engine (open-GOP H.264 is known to fail it — SPEC-001 I65); HEVC failed
/// it (the T-189 repro) and now falls back. T-190 to T-192 keep it green, and the later HEVC repair (T-201) must pass it
/// before HEVC goes back in the encoder map.</para>
/// </summary>
internal static class SmartCutJointIntegrity
{
    /// <summary>The leading <c>[context @ address]</c> tag ffmpeg prints on a log line.</summary>
    private static readonly Regex ContextTag = new(@"^\[(?<ctx>[^\]@]+?)\s*@\s*[^\]]*\]", RegexOptions.Compiled);

    /// <summary>The outcome of one check. <see cref="Passes"/> is the verdict; the counts are for Build logs.</summary>
    /// <param name="DecoderErrorLines">Decoder and demuxer error lines of the whole-file decode (null-muxer lines excluded).</param>
    /// <param name="AllErrorLines">Every line the whole-file decode printed at <c>-v error</c>, null-muxer lines included.</param>
    /// <param name="NullMuxerLines">The null muxer's own lines (<c>[null @ …]</c>) — T-190's timing symptom.</param>
    /// <param name="FinalTailFrames">Video frames of the final from the joint on.</param>
    /// <param name="SourceTailFrames">Video frames of the source from <c>HeadEnd</c> on.</param>
    /// <param name="ComparedFrames">Frames compared — the smaller of the two tails.</param>
    /// <param name="MismatchedFrames">Compared frames whose md5 differs from the source's.</param>
    /// <param name="FirstMismatch">Index (from the joint) of the first differing frame, or -1.</param>
    /// <param name="ErrorLines">The decode's error lines, for the test output.</param>
    internal sealed record Report(
        int DecoderErrorLines,
        int AllErrorLines,
        int NullMuxerLines,
        int FinalTailFrames,
        int SourceTailFrames,
        int ComparedFrames,
        int MismatchedFrames,
        int FirstMismatch,
        IReadOnlyList<string> ErrorLines)
    {
        /// <summary>
        /// Zero decoder and demuxer errors, the final's tail as long as the source's (the same number of frames from the
        /// joint on — SPEC-001 I65), and every frame from the joint on identical to the source's.
        /// </summary>
        public bool Passes =>
            DecoderErrorLines == 0 && TailLengthsMatch && ComparedFrames > 0 && MismatchedFrames == 0;

        /// <summary>The final and the source have the same number of video frames from the joint on.</summary>
        public bool TailLengthsMatch => FinalTailFrames == SourceTailFrames;

        /// <summary>One line for a test's output and a ticket's Build log.</summary>
        public string Describe() => string.Create(
            CultureInfo.InvariantCulture,
            $"decoder/demuxer errors {DecoderErrorLines} (all error lines {AllErrorLines}, [null @ lines {NullMuxerLines}); "
            + $"frames from the joint: final {FinalTailFrames}, source {SourceTailFrames}"
            + $"{(TailLengthsMatch ? string.Empty : " (the tails differ in length)")}, compared {ComparedFrames}, "
            + $"mismatched {MismatchedFrames}{(FirstMismatch >= 0 ? $" (first at {FirstMismatch})" : string.Empty)}");
    }

    /// <summary>
    /// True when <paramref name="line"/> is a decoder or demuxer error line — every non-empty line of a
    /// <c>-v error</c> decode except the null muxer's own (<c>[null @ …]</c>, or <c>[out#N/null @ …]</c>).
    /// </summary>
    internal static bool IsDecoderOrDemuxerError(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return !IsNullMuxerLine(line);
    }

    /// <summary>True when <paramref name="line"/> was printed by the null muxer itself (T-190's timing lines).</summary>
    internal static bool IsNullMuxerLine(string line)
    {
        var m = ContextTag.Match(line.TrimStart());
        if (!m.Success)
        {
            return false;
        }

        var ctx = m.Groups["ctx"].Value.Trim();
        return string.Equals(ctx, "null", StringComparison.Ordinal)
            || (ctx.StartsWith("out#", StringComparison.Ordinal) && ctx.EndsWith("/null", StringComparison.Ordinal));
    }

    /// <summary>
    /// Check <paramref name="finalPath"/> against <paramref name="sourcePath"/> for an Exact cut that started at
    /// <paramref name="start"/> and whose copied part begins at the keyframe <paramref name="headEnd"/>. Intermediate
    /// <c>framemd5</c> files go to <paramref name="workDir"/>. Needs the bundled ffmpeg — callers check
    /// <see cref="FfmpegTestBinaries"/> first.
    /// </summary>
    public static async Task<Report> CheckAsync(
        string finalPath, string sourcePath, TimeSpan start, TimeSpan headEnd, string workDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        var runner = Runner();

        // 1. The whole final, decoded, every stream.
        var (decoderErrors, lines) = await DecodeAsync(finalPath, ct).ConfigureAwait(false);
        var nullMuxer = lines.Count(IsNullMuxerLine);

        // 2. Frame hashes, from the joint on.
        var stamp = Guid.NewGuid().ToString("N");
        var finalFrames = await FrameHashesAsync(runner, finalPath, Path.Combine(workDir, $"final-{stamp}.framemd5"), ct)
            .ConfigureAwait(false);
        var sourceFrames = await FrameHashesAsync(runner, sourcePath, Path.Combine(workDir, $"source-{stamp}.framemd5"), ct)
            .ConfigureAwait(false);

        // The final is measured from its own first video frame (its head starts at Start); the source on its own
        // timestamps, which -copyts keeps in the probe's time base — the base HeadEnd is in.
        var headLength = (headEnd - start).TotalSeconds;
        var finalOrigin = finalFrames.Count > 0 ? finalFrames[0].Seconds : 0.0;
        var finalTail = finalFrames
            .Where(f => f.Seconds - finalOrigin >= headLength - HalfFrame(finalFrames))
            .Select(f => f.Hash)
            .ToList();
        var sourceTail = sourceFrames
            .Where(f => f.Seconds >= headEnd.TotalSeconds - HalfFrame(sourceFrames))
            .Select(f => f.Hash)
            .ToList();

        var compared = Math.Min(finalTail.Count, sourceTail.Count);
        var mismatched = 0;
        var firstMismatch = -1;
        for (var i = 0; i < compared; i++)
        {
            if (!string.Equals(finalTail[i], sourceTail[i], StringComparison.Ordinal))
            {
                mismatched++;
                if (firstMismatch < 0)
                {
                    firstMismatch = i;
                }
            }
        }

        return new Report(
            decoderErrors, lines.Count, nullMuxer, finalTail.Count, sourceTail.Count, compared, mismatched, firstMismatch,
            lines);
    }

    /// <summary>
    /// Decode every stream of <paramref name="mediaPath"/> with <c>-v error … -f null -</c>: the decoder and demuxer
    /// error-line count (null-muxer lines excluded) and every line printed. Used on its own to check that a lossless
    /// fall-back output decodes cleanly (T-189).
    /// </summary>
    public static async Task<(int DecoderErrorLines, IReadOnlyList<string> Lines)> DecodeAsync(
        string mediaPath, CancellationToken ct = default)
    {
        var decode = await Runner().RunAsync(
            FfmpegArgs.ForFfmpeg().Raw("-v", "error").Input(mediaPath).Raw("-map", "0", "-f", "null").Output("-"),
            null, null, ct).ConfigureAwait(false);
        var lines = decode.StdErrTail.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return (lines.Count(IsDecoderOrDemuxerError), lines);
    }

    private static FfmpegRunner Runner() => new(new FfmpegBinaryLocator(
        ffmpegOverride: FfmpegTestBinaries.FfmpegRequired, ffprobeOverride: FfmpegTestBinaries.Ffprobe));

    private readonly record struct Frame(double Seconds, string Hash);

    /// <summary>Half the typical frame interval of <paramref name="frames"/> — the tolerance for "at or after" a time.</summary>
    private static double HalfFrame(IReadOnlyList<Frame> frames)
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

        if (deltas.Count == 0)
        {
            return 0.001;
        }

        deltas.Sort();
        return deltas[deltas.Count / 2] / 2;
    }

    /// <summary>
    /// Decode the first video stream to a <c>framemd5</c> file and parse it into (pts seconds, hash), in output order.
    /// <c>-copyts</c> keeps each frame's own timestamp (ffmpeg would otherwise subtract the file's <c>start_time</c>), so
    /// the times are in the probe's time base; <c>-enc_time_base:v demux</c> writes them in the stream's own time base,
    /// not in 1/fps — which would round a frame at 1.30 s on a 25 fps grid to 1.32 s, half a frame off, exactly at the
    /// "at or after" tolerance.
    /// </summary>
    private static async Task<IReadOnlyList<Frame>> FrameHashesAsync(
        FfmpegRunner runner, string mediaPath, string outputPath, CancellationToken ct)
    {
        var run = await runner.RunAsync(
            FfmpegArgs.ForFfmpeg()
                .Raw("-v", "error", "-copyts")
                .Input(mediaPath)
                .Raw("-map", "0:v:0", "-fps_mode", "passthrough", "-enc_time_base:v", "demux", "-f", "framemd5", "-y")
                .Output(outputPath),
            null, null, ct).ConfigureAwait(false);

        if (run.ExitCode != 0 || !File.Exists(outputPath))
        {
            return Array.Empty<Frame>();
        }

        // "#tb 0: 1/30" then data lines "0,        0,        0,        1,   115200, <md5>".
        long tbNum = 1, tbDen = 1;
        var frames = new List<Frame>();
        foreach (var raw in await File.ReadAllLinesAsync(outputPath, ct).ConfigureAwait(false))
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
}
