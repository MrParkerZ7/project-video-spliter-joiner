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
    /// an ffmpeg numbered pattern (e.g. <c>…/part%03d.mp4</c>), built by <see cref="SegmentPattern"/> so that a
    /// percent sign in the folder is not read as part of it (T-222).
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
    /// T-222: the segment muxer's output pattern for the temp folder <paramref name="dir"/> and the output extension
    /// <paramref name="extension"/> (with its dot): <c>&lt;dir&gt;/part%03d&lt;ext&gt;</c>, where the one placeholder
    /// is the only percent sign left unescaped. ffmpeg expands EVERY percent sign of the pattern it is given
    /// (<c>%03d</c> is the part number, <c>%%</c> a literal percent, anything else an invalid template), so an output
    /// folder such as <c>D:\Videos\100% done</c> used to fail the whole Split with "Invalid segment filename template".
    /// The temp files then land in the real folder, and the engine reads them back by their real names.
    /// </summary>
    internal static string SegmentPattern(string dir, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        ArgumentNullException.ThrowIfNull(extension);

        return Path.Combine(EscapeSegmentPatternLiteral(dir), "part%03d" + EscapeSegmentPatternLiteral(extension));
    }

    /// <summary>
    /// T-222: <paramref name="literal"/> with each percent sign doubled, so ffmpeg's filename-template expansion
    /// (<c>%%</c> becomes <c>%</c>) gives the text back unchanged. A folder named <c>%03d</c> is escaped like any other,
    /// so it cannot be taken for the placeholder.
    /// </summary>
    internal static string EscapeSegmentPatternLiteral(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);

        return literal.Replace("%", "%%", StringComparison.Ordinal);
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
    /// <param name="seekAnchor">
    /// <b>Matroska (T-234), MP4 and QuickTime (T-237).</b> A source timestamp inside the part's first GOP, after
    /// <paramref name="start"/> and before the next keyframe (<see cref="SeekAnchor"/>), that the input seek is aimed at instead of the
    /// keyframe itself: measured with ffmpeg n7.1, an input <c>-ss</c> on a Matroska file whose video has B-frames lands on
    /// the keyframe BEFORE the one it is aimed at, and needs a target about 0.131 s after the keyframe to land on it (none
    /// with no B-frames). The cause inside ffmpeg is not pinned down; the file's cues hold the keyframe's exact pts. Every
    /// other container passes null and seeks to the keyframe. <c>-to</c> is measured from the anchor, the same way it is
    /// from the keyframe.
    /// </param>
    public static FfmpegArgs PerSegment(
        string inputPath,
        TimeSpan start,
        TimeSpan? end,
        TimeSpan sourceStartTime,
        string outputPath,
        TimeSpan? seekAnchor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        // The seek offset from the format's start, clamped to the file's start.
        var seekTarget = seekAnchor is { } anchor && anchor > start ? anchor : start;
        var seekOffset = seekTarget - sourceStartTime;
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

    /// <summary>How far before the next keyframe the Matroska seek anchor sits (<see cref="MatroskaSeekAnchor"/>); Matroska counts in milliseconds.</summary>
    internal static readonly TimeSpan MatroskaSeekMargin = TimeSpan.FromMilliseconds(1);

    /// <summary>True for ffprobe's <c>format_name</c> of a Matroska or WebM file (<c>matroska,webm</c>); null or blank is false.</summary>
    internal static bool IsMatroskaContainer(string? container) =>
        !string.IsNullOrWhiteSpace(container) && container.Contains("matroska", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>seekAnchor</c> of <see cref="PerSegment"/> for a Matroska source (T-234): a point inside the GOP that starts
    /// at <paramref name="start"/>, <see cref="MatroskaSeekMargin"/> before the next keyframe (before
    /// <paramref name="fileEnd"/> when <paramref name="start"/> is the last keyframe). With B-frames an input seek lands
    /// on <paramref name="start"/> for a target from about 0.131 s after it to about 0.131 s after the next keyframe, and
    /// with none for a target from <paramref name="start"/> to the next keyframe; a target just under the next keyframe is
    /// in both windows when the keyframes are at least about 0.131 s apart. <b>Known limits:</b> on a B-frame stream a
    /// keyframe gap under about 0.131 s, and a last GOP of one to three frames, still land one GOP early; a file whose
    /// cues are earlier than its keyframes' pts could land on the next keyframe (T-245). Null when no anchor is needed or
    /// possible: <paramref name="start"/> is at or before the first keyframe (the seek clamps to the first cue, which is
    /// the part's start), or the next keyframe is within the margin. <paramref name="keyframes"/> are source timestamps,
    /// ascending.
    /// </summary>
    internal static TimeSpan? MatroskaSeekAnchor(TimeSpan start, IReadOnlyList<TimeSpan> keyframes, TimeSpan fileEnd)
    {
        ArgumentNullException.ThrowIfNull(keyframes);

        if (keyframes.Count == 0 || start <= keyframes[0])
        {
            return null;
        }

        // The first keyframe after the start.
        var lo = 0;
        var hi = keyframes.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (keyframes[mid] > start)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        var limit = lo < keyframes.Count ? keyframes[lo] : fileEnd;
        var anchor = limit - MatroskaSeekMargin;
        return anchor > start ? anchor : null;
    }

    /// <summary>The shortest GOP, with a later keyframe after it, that gets a MP4/QuickTime seek anchor (<see cref="MovSeekAnchor"/>).</summary>
    internal static readonly TimeSpan MovSeekMinGap = TimeSpan.FromMilliseconds(500);

    /// <summary>The same, in frames: the GOP must be this many frame periods too, the keyframe's B-frame delay being counted in frames.</summary>
    internal const int MovSeekMinFrames = 16;

    /// <summary>The shortest stretch with no later keyframe after it (the file's last GOP, or a span the end of the cut stops) that gets an anchor.</summary>
    internal static readonly TimeSpan MovSeekMinTail = TimeSpan.FromMilliseconds(250);

    /// <summary>The same, in frames.</summary>
    internal const int MovSeekMinTailFrames = 6;

    /// <summary>True for ffprobe's <c>format_name</c> of an MP4 or QuickTime file (<c>mov,mp4,m4a,3gp,3g2,mj2</c>); null or blank is false.</summary>
    internal static bool IsMovContainer(string? container) =>
        !string.IsNullOrWhiteSpace(container)
        && (container.Contains("mp4", StringComparison.OrdinalIgnoreCase) || container.Contains("mov", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The seek anchor of a MP4 or QuickTime source (T-237): the middle of the GOP that starts at <paramref name="start"/>.
    ///
    /// <para><b>Why.</b> The demuxer finds the keyframe to land on by its index time, which is a decoding time of the file's
    /// own, not the presentation time the keyframe list holds. On a constant-frame-rate file the two agree closely enough that a
    /// seek aimed AT the keyframe lands on it. On a variable-frame-rate file with B-frames the gap between them differs from
    /// keyframe to keyframe: measured with ffmpeg n7.1 on a phone-style file (a quarter of the frames dropped at random, 2.5 s
    /// GOP), a seek aimed at a keyframe's presentation time landed on the keyframe BEFORE it for every keyframe of that file, and
    /// needed an aim 0.035 to 0.1 s later; the lossless part then began a whole GOP early (242 frames against 180 planned). The
    /// middle of the GOP is inside the window that lands on the keyframe: later than the keyframe's index gap and earlier than the
    /// next keyframe's index time. That time can precede the next keyframe's presentation time by its B-frame delay, which is
    /// counted in frames (0.8 s at 10 fps with three B-frames, 0.1 s at 30 fps), so a GOP with a later keyframe after it must be
    /// <see cref="MovSeekMinGap"/> and <see cref="MovSeekMinFrames"/> frame periods long, or the middle can fall past that
    /// keyframe's index time and land one GOP LATE (a regression the aim at the keyframe never had). A stretch with no later
    /// keyframe after it (the last GOP, or the span a cut's end stops) has nothing to overshoot onto and needs only to be
    /// <see cref="MovSeekMinTail"/> and <see cref="MovSeekMinTailFrames"/> frame periods long, so that its middle is past the
    /// index gap. Anything shorter gets no anchor and keeps the aim at the keyframe.</para>
    ///
    /// <para>The anchor is used where the keyframe was used: as the input <c>-ss</c> (<see cref="PerSegment"/>, which measures
    /// <c>-to</c> from it) and as the <c>inpoint</c> of the end trim's single-file list. <paramref name="upTo"/> is where the
    /// cut ends, for the end trim: the anchor never passes it, because a concat list whose <c>inpoint</c> is after its
    /// <c>outpoint</c> is an error. Null when none is needed or possible: <paramref name="start"/> is at or before the first
    /// keyframe (the seek clamps to the file's start), or the stretch is short. <paramref name="keyframes"/> are source
    /// timestamps, ascending; <paramref name="fileEnd"/> bounds the last GOP; <paramref name="frameRate"/> is the video's,
    /// in frames per second, null when unknown (only the times then count).</para>
    /// </summary>
    internal static TimeSpan? MovSeekAnchor(
        TimeSpan start, IReadOnlyList<TimeSpan> keyframes, TimeSpan fileEnd, double? frameRate = null, TimeSpan? upTo = null)
    {
        ArgumentNullException.ThrowIfNull(keyframes);

        if (keyframes.Count == 0 || start <= keyframes[0])
        {
            return null;
        }

        // The first keyframe after the start (a millisecond of slack: the start is a keyframe of this list, not a time near one).
        var after = start + TimeSpan.FromMilliseconds(1);
        var lo = 0;
        var hi = keyframes.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (keyframes[mid] > after)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        // What the aim may not pass: the next keyframe (then it must keep clear of that keyframe's index time), the end of the
        // file, or the cut's end, whichever is first; the last two have no keyframe to overshoot onto.
        var limit = lo < keyframes.Count ? keyframes[lo] : fileEnd;
        var bounded = lo < keyframes.Count;
        if (upTo is { } cap && cap < limit)
        {
            limit = cap;
            bounded = false;
        }

        var gap = limit - start;
        var period = frameRate is > 0 ? TimeSpan.FromSeconds(1 / frameRate.Value) : TimeSpan.Zero;
        var need = bounded
            ? TimeSpan.FromTicks(Math.Max(MovSeekMinGap.Ticks, period.Ticks * MovSeekMinFrames))
            : TimeSpan.FromTicks(Math.Max(MovSeekMinTail.Ticks, period.Ticks * MovSeekMinTailFrames));
        return gap >= need ? start + TimeSpan.FromTicks(gap.Ticks / 2) : null;
    }

    /// <summary>
    /// The seek anchor for <paramref name="container"/>: Matroska's (<see cref="MatroskaSeekAnchor"/>, T-234), MP4's and
    /// QuickTime's (<see cref="MovSeekAnchor"/>, T-237), none for any other container (they land on the keyframe the plan names).
    /// </summary>
    internal static TimeSpan? SeekAnchor(
        string? container, TimeSpan start, IReadOnlyList<TimeSpan> keyframes, TimeSpan fileEnd, double? frameRate = null, TimeSpan? upTo = null) =>
        IsMatroskaContainer(container)
            ? MatroskaSeekAnchor(start, keyframes, fileEnd)
            : IsMovContainer(container) ? MovSeekAnchor(start, keyframes, fileEnd, frameRate, upTo) : null;

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
