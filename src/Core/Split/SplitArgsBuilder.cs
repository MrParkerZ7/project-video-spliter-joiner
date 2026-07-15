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
    /// <c>-i in -map 0 -c copy -f segment -segment_times t1,t2,… -reset_timestamps 1 outpattern</c>.
    /// Because every boundary is a keyframe, copy is clean. <paramref name="outputPattern"/> is
    /// an ffmpeg numbered pattern (e.g. <c>…/part%03d.mp4</c>).
    /// </summary>
    public static FfmpegArgs SegmentMuxer(
        string inputPath,
        IReadOnlyList<TimeSpan> interiorSnappedCuts,
        string outputPattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(interiorSnappedCuts);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPattern);

        if (interiorSnappedCuts.Count == 0)
        {
            throw new SplitException("Segment muxer needs at least one interior cut time.");
        }

        return FfmpegArgs.ForFfmpeg()
            .Raw("-y")
            .Input(inputPath)
            .Raw("-map", "0")
            .Raw("-c", "copy")
            .Raw("-f", "segment")
            .Raw("-segment_times", SplitPlanner.ToSegmentTimes(interiorSnappedCuts))
            .Raw("-reset_timestamps", "1")
            .Output(outputPattern);
    }

    /// <summary>
    /// Build the PER-SEGMENT fallback command for one range [start..end] — used for
    /// robustness or arbitrary-subset extraction. Input-seek before <c>-i</c> for speed, then
    /// <c>-to</c> the end, <c>-map 0 -c copy -avoid_negative_ts make_zero</c> so the copied
    /// segment starts at zero. <paramref name="end"/> may be null for "to end of file".
    /// </summary>
    public static FfmpegArgs PerSegment(
        string inputPath,
        TimeSpan start,
        TimeSpan? end,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var args = FfmpegArgs.ForFfmpeg()
            .Raw("-y")
            .Raw("-ss", SplitPlanner.ToFfmpegSeconds(start))
            .Input(inputPath);

        if (end is { } e)
        {
            args.Raw("-to", SplitPlanner.ToFfmpegSeconds(e));
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
