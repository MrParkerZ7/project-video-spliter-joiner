using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// The SINGLE choke-point that builds every ffmpeg command the split engine runs. Both the
/// segment-muxer path and the per-segment fallback go through here, and neither ever emits
/// an encoder flag — the INVARIANT is that split is pure stream-copy (<c>-c copy</c>), never
/// a re-encode. Factored out so a unit test can inspect the produced token list and assert
/// <c>copy</c> is present and no encoder token (<c>-c:v x264</c>, <c>libx264</c>, <c>-crf</c>,
/// <c>-preset</c>, …) leaks in.
/// </summary>
public static class SplitArgsBuilder
{
    /// <summary>
    /// Encoder-ish tokens that must NEVER appear in a split invocation. Used both to build
    /// safe args and (in tests) to assert the invariant on any produced token list.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenEncoderTokens = new[]
    {
        "-c:v", "-c:a", "-vcodec", "-acodec", "-codec:v", "-codec:a",
        "-crf", "-preset", "-b:v", "-b:a", "-qp", "-x264-params", "-x265-params",
        "libx264", "libx265", "h264", "hevc", "aac", "libmp3lame", "mpeg4", "vp9", "libvpx",
        "-filter:v", "-filter:a", "-vf", "-af", "-filter_complex",
    };

    /// <summary>
    /// Build the SEGMENT-MUXER single-pass command for a contiguous full-file split:
    /// <c>-i in -map 0 -c copy -f segment -segment_times t1,t2,… [-segment_time_delta d] -reset_timestamps 1 outpattern</c>.
    /// Because every boundary is a keyframe, copy is clean. <paramref name="outputPattern"/> is
    /// an ffmpeg numbered pattern (e.g. <c>…/part%03d.mp4</c>).
    ///
    /// <para><b>The muxer's time base (T-219).</b> ffmpeg's segment muxer counts <c>-segment_times</c> from the first
    /// packet of its reference (video) stream and starts a segment at the first keyframe whose pts is at or after
    /// <c>time + that first pts − delta</c>, comparing in microseconds (libavformat <c>segment.c</c>). So the caller
    /// passes each boundary as keyframe time − the video stream's start (<see cref="SplitEngine"/> does), and a
    /// <paramref name="segmentTimeDelta"/> of half a frame: without it a keyframe whose pts rounds a fraction of a
    /// microsecond below the target is skipped and the part ends one GOP late (every B-frame H.264/HEVC source, whose
    /// first packet sits a frame or two after zero). Half a frame can never reach an earlier keyframe, because keyframes
    /// are at least one frame apart. <see cref="TimeSpan.Zero"/> emits no <c>-segment_time_delta</c> token.</para>
    /// </summary>
    public static FfmpegArgs SegmentMuxer(
        string inputPath,
        IReadOnlyList<TimeSpan> interiorSnappedCuts,
        string outputPattern,
        TimeSpan segmentTimeDelta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(interiorSnappedCuts);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPattern);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentTimeDelta, TimeSpan.Zero);

        if (interiorSnappedCuts.Count == 0)
        {
            throw new SplitException("Segment muxer needs at least one interior cut time.");
        }

        var args = FfmpegArgs.ForFfmpeg()
            .Raw("-y")
            .Input(inputPath)
            .Raw("-map", "0")
            .Raw("-c", "copy")
            .Raw("-f", "segment")
            .Raw("-segment_times", SplitPlanner.ToSegmentTimes(interiorSnappedCuts));

        if (segmentTimeDelta > TimeSpan.Zero)
        {
            args.Raw("-segment_time_delta", SplitPlanner.ToFfmpegSeconds(segmentTimeDelta));
        }

        return args
            .Raw("-reset_timestamps", "1")
            .Output(outputPattern);
    }

    /// <summary>
    /// Build the PER-SEGMENT command for one range <c>[start..end]</c> (T-049 subset extraction) —
    /// input-seek before <c>-i</c> for speed, then <c>-map 0 -c copy -avoid_negative_ts make_zero</c>
    /// so the copied segment starts at zero. <paramref name="end"/> may be null for "to end of file".
    ///
    /// <para><b>The seek's time base (T-219).</b> <paramref name="start"/> and <paramref name="end"/> are source
    /// timestamps (keyframe times), but ffmpeg's input <c>-ss</c> is an offset from the format's
    /// <paramref name="sourceStartTime"/> (<see cref="Media.MediaInfo.StartTime"/>, about 1.4 s on an MPEG-TS). The
    /// builder emits <c>-ss max(0, start − sourceStartTime)</c>; passing the raw keyframe time on such a source seeked
    /// one GOP late. A start before the file's own start clamps to <c>-ss 0</c>, the file's start.</para>
    ///
    /// <para>IMPORTANT ffmpeg semantics: because <c>-ss</c> is placed BEFORE <c>-i</c> (an INPUT
    /// seek), the input timeline is reset to zero at the seek point, so <c>-to</c> is measured from
    /// the seek point — i.e. it is effectively a DURATION, not an absolute source timestamp. This
    /// builder therefore emits <c>-to (end − seek point)</c>: <c>end − start</c> whenever the seek is not clamped
    /// (emitting the absolute end would over-run by <c>start</c>), and <c>end − sourceStartTime</c> when it is, so a part
    /// planned from 0 on a TS does not run <c>sourceStartTime</c> past its end. Under stream copy <c>-to</c> acts on
    /// decoding timestamps, so on a B-frame source a part that has an end still keeps the next keyframe and the frame
    /// coded after it (T-220).</para>
    /// </summary>
    public static FfmpegArgs PerSegment(
        string inputPath,
        TimeSpan start,
        TimeSpan? end,
        TimeSpan sourceStartTime,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        // The seek offset from the format's start, clamped to the file's start.
        var seekOffset = start - sourceStartTime;
        if (seekOffset < TimeSpan.Zero)
        {
            seekOffset = TimeSpan.Zero;
        }

        var args = FfmpegArgs.ForFfmpeg()
            .Raw("-y")
            .Raw("-ss", SplitPlanner.ToFfmpegSeconds(seekOffset))
            .Input(inputPath);

        if (end is { } e)
        {
            // -to is relative to the input-seek point (sourceStartTime + seekOffset, i.e. max(start, sourceStartTime))
            // → pass the DURATION from there to the end, clamped ≥ 0.
            var duration = e - (sourceStartTime + seekOffset);
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            args.Raw("-to", SplitPlanner.ToFfmpegSeconds(duration));
        }

        return args
            .Raw("-map", "0")
            .Raw("-c", "copy")
            .Raw("-avoid_negative_ts", "make_zero")
            .Output(outputPath);
    }

    /// <summary>
    /// True if the token list satisfies the split invariant: it contains a bare <c>copy</c>
    /// token AND none of <see cref="ForbiddenEncoderTokens"/>. The engine asserts this on
    /// every command before launching; tests assert it on the built args directly.
    /// </summary>
    public static bool SatisfiesCopyInvariant(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var hasCopy = tokens.Any(t => string.Equals(t, "copy", StringComparison.Ordinal));
        if (!hasCopy)
        {
            return false;
        }

        foreach (var token in tokens)
        {
            foreach (var forbidden in ForbiddenEncoderTokens)
            {
                if (string.Equals(token, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
