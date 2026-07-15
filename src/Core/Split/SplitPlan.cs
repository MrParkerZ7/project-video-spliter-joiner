using System.Globalization;
using System.Text;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// One planned segment BEFORE extraction: the requested and snapped boundaries plus the
/// resolved output path. The engine turns this into a <see cref="SplitSegment"/> after the
/// file is written.
/// </summary>
/// <param name="RequestedStart">Requested (pre-snap) start of this segment.</param>
/// <param name="RequestedEnd">Requested (pre-snap) end of this segment.</param>
/// <param name="SnappedStart">Keyframe-snapped start actually used.</param>
/// <param name="SnappedEnd">Keyframe-snapped end actually used.</param>
/// <param name="StartDelta">Signed snap offset of the START boundary (SnappedStart - RequestedStart).</param>
/// <param name="OutputPath">Absolute destination path for this segment.</param>
public sealed record PlannedSegment(
    TimeSpan RequestedStart,
    TimeSpan RequestedEnd,
    TimeSpan SnappedStart,
    TimeSpan SnappedEnd,
    TimeSpan StartDelta,
    string OutputPath);

/// <summary>
/// The full, validated plan for a split: the ordered segments to extract, the interior
/// snapped cut times (used as <c>-segment_times</c> for the segment muxer), and any
/// warnings raised while normalizing the request. Pure data — no ffmpeg, no I/O.
/// </summary>
/// <param name="Segments">Contiguous planned segments covering [0 .. duration].</param>
/// <param name="InteriorSnappedCuts">
/// The snapped cut times strictly between 0 and duration, ascending — i.e. the segment
/// boundaries fed to <c>-f segment -segment_times</c>. Count == Segments.Count - 1.
/// </param>
/// <param name="Warnings">Non-fatal notes about how the request was adjusted.</param>
public sealed record SplitPlan(
    IReadOnlyList<PlannedSegment> Segments,
    IReadOnlyList<TimeSpan> InteriorSnappedCuts,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Pure planning for a split — no ffmpeg, no file writes, fully unit-testable. Takes the
/// requested cuts, the probed duration, and the file's keyframe list, and produces the
/// contiguous, keyframe-snapped segment plan. Cut-point validation lives entirely here:
/// sort, dedupe near-equal cuts, drop out-of-range cuts (with a friendly warning), snap
/// each surviving cut to the nearest keyframe, then build [0..s1],[s1..s2],…,[sN..end].
/// </summary>
public static class SplitPlanner
{
    /// <summary>
    /// Cuts closer together than this (after sorting) are treated as duplicates and merged.
    /// Also the minimum distance a cut must sit inside (0, duration) to be considered valid.
    /// </summary>
    public static readonly TimeSpan Epsilon = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Build the split plan. <paramref name="snap"/> is the T-003 nearest-keyframe snapper
    /// (pass <c>probe.SnapToNearestKeyframe</c>) so this stays free of ffmpeg while reusing
    /// the real snapping logic. When <paramref name="keyframes"/> is empty, cuts are left
    /// unsnapped (a warning is raised) — the caller can still split on requested times.
    /// </summary>
    /// <param name="duration">Total media duration from the probe.</param>
    /// <param name="requestedCuts">Raw cut points from the request (any order, may dupe).</param>
    /// <param name="keyframes">Sorted keyframe times from the probe.</param>
    /// <param name="snap">Nearest-keyframe snap function (keyframes, requested) → snap.</param>
    /// <param name="averageGop">Optional mean GOP, used only to warn on coarse snapping.</param>
    /// <param name="pathFor">Maps a 1-based segment index to its output path.</param>
    public static SplitPlan Plan(
        TimeSpan duration,
        IReadOnlyList<TimeSpan> requestedCuts,
        IReadOnlyList<TimeSpan> keyframes,
        Func<IReadOnlyList<TimeSpan>, TimeSpan, KeyframeSnap> snap,
        TimeSpan averageGop,
        Func<int, string> pathFor)
    {
        ArgumentNullException.ThrowIfNull(requestedCuts);
        ArgumentNullException.ThrowIfNull(keyframes);
        ArgumentNullException.ThrowIfNull(snap);
        ArgumentNullException.ThrowIfNull(pathFor);

        if (duration <= TimeSpan.Zero)
        {
            throw new SplitException(
                $"Cannot split: probed duration is {duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s (must be positive).");
        }

        var warnings = new List<string>();

        // 1. Sort ascending. 2. Drop cuts at/outside (0, duration). 3. Dedupe within epsilon.
        var sorted = requestedCuts.OrderBy(c => c).ToList();
        var kept = new List<TimeSpan>();

        foreach (var cut in sorted)
        {
            if (cut <= TimeSpan.Zero || cut >= duration)
            {
                warnings.Add(
                    $"Cut at {Fmt(cut)} is outside the file bounds (0 .. {Fmt(duration)}) and was ignored.");
                continue;
            }

            if (kept.Count > 0 && (cut - kept[^1]) < Epsilon)
            {
                warnings.Add(
                    $"Cut at {Fmt(cut)} is within {Epsilon.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}ms of an earlier cut and was merged.");
                continue;
            }

            kept.Add(cut);
        }

        if (kept.Count == 0)
        {
            throw new SplitException(
                "No valid cut points remain after validation — every requested cut was at or beyond the file bounds. Provide at least one cut strictly between 0 and the duration.");
        }

        // Snap each surviving cut to a keyframe. If no keyframes were probed, keep the raw
        // times (still a legal split, just not guaranteed clean at the copy boundary).
        var snappedCuts = new List<TimeSpan>();
        var snapDeltas = new List<TimeSpan>();

        var coarse = averageGop > TimeSpan.FromSeconds(2);

        foreach (var cut in kept)
        {
            if (keyframes.Count == 0)
            {
                snappedCuts.Add(cut);
                snapDeltas.Add(TimeSpan.Zero);
                continue;
            }

            var s = snap(keyframes, cut);
            var snapped = s.Snapped;

            // Guard against a snap that collides with the previous snapped cut (two nearby
            // requested cuts snapping to the same keyframe): skip the duplicate boundary.
            if (snappedCuts.Count > 0 && (snapped - snappedCuts[^1]) < Epsilon)
            {
                warnings.Add(
                    $"Cut at {Fmt(cut)} snapped to {Fmt(snapped)}, colliding with an earlier snapped cut — dropped.");
                continue;
            }

            // A snapped cut can land at/after the duration if the last keyframe ≈ end; drop it.
            if (snapped <= TimeSpan.Zero || snapped >= duration)
            {
                warnings.Add(
                    $"Cut at {Fmt(cut)} snapped to {Fmt(snapped)}, which is outside the file bounds — dropped.");
                continue;
            }

            snappedCuts.Add(snapped);
            snapDeltas.Add(s.Delta);

            if (coarse && (s.Delta.Duration() > TimeSpan.FromSeconds(0.5)))
            {
                warnings.Add(
                    $"Cut at {Fmt(cut)} moved {Fmt(s.Delta.Duration())} to the nearest keyframe ({Fmt(snapped)}) — this file has a coarse GOP (~{Fmt(averageGop)}), so cuts cannot be precise.");
            }
        }

        if (snappedCuts.Count == 0)
        {
            throw new SplitException(
                "No valid cut points remain after keyframe snapping. The requested cuts all collapsed onto the file bounds.");
        }

        // Build contiguous segments: [0..s1], [s1..s2], …, [sN..duration].
        var segments = new List<PlannedSegment>(snappedCuts.Count + 1);
        var reqStart = TimeSpan.Zero;
        var snapStart = TimeSpan.Zero;

        for (var i = 0; i < snappedCuts.Count; i++)
        {
            var reqEnd = kept[i];      // requested boundary at this cut
            var snapEnd = snappedCuts[i];
            var startDelta = i == 0 ? TimeSpan.Zero : snapDeltas[i - 1];

            segments.Add(new PlannedSegment(
                RequestedStart: reqStart,
                RequestedEnd: reqEnd,
                SnappedStart: snapStart,
                SnappedEnd: snapEnd,
                StartDelta: startDelta,
                OutputPath: pathFor(i + 1)));

            reqStart = reqEnd;
            snapStart = snapEnd;
        }

        // Final segment runs to the end of the file.
        var lastStartDelta = snapDeltas[^1];
        segments.Add(new PlannedSegment(
            RequestedStart: reqStart,
            RequestedEnd: duration,
            SnappedStart: snapStart,
            SnappedEnd: duration,
            StartDelta: lastStartDelta,
            OutputPath: pathFor(segments.Count + 1)));

        return new SplitPlan(segments, snappedCuts.AsReadOnly(), warnings);
    }

    private static string Fmt(TimeSpan t)
    {
        var sign = t < TimeSpan.Zero ? "-" : string.Empty;
        return sign + t.Duration().TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
    }

    /// <summary>
    /// Format a keyframe/cut time as a plain seconds token for ffmpeg's
    /// <c>-segment_times</c> / <c>-ss</c> / <c>-to</c> (invariant, no thousands separators).
    /// </summary>
    public static string ToFfmpegSeconds(TimeSpan t) =>
        t.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Join snapped interior cuts into ffmpeg's comma-separated segment-times list.</summary>
    public static string ToSegmentTimes(IReadOnlyList<TimeSpan> cuts)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cuts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(ToFfmpegSeconds(cuts[i]));
        }

        return sb.ToString();
    }
}
