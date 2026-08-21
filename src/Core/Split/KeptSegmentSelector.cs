using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// Pure Core helper for the Bulk Cut feature (D-004): a bulk "trim" IS a
/// <see cref="SplitEngine"/> that keeps exactly ONE middle segment
/// <c>[introEnd → outroStart | EOF]</c>. This selector adds NO new ffmpeg path — it
/// resolves which planned segment is the kept middle and assembles the single-kept-segment
/// <see cref="SplitRequest"/> that the EXISTING per-segment stream-copy path already handles
/// (temp-then-move cancel-safety, disk pre-flight, copy-invariant assertion — all inherited
/// for free). No I/O, no ffmpeg, no WPF — trivially unit-testable.
/// </summary>
public static class KeptSegmentSelector
{
    /// <summary>
    /// Segment filename template for a bulk trim: <c>&lt;name&gt;_trimmed&lt;ext&gt;</c>, written
    /// into the SOURCE folder (the original is never touched). Deliberately has NO <c>{index}</c>
    /// token, so every planned segment resolves to the SAME output path — which is safe ONLY
    /// because <see cref="BuildKeptMiddleRequest"/> always selects EXACTLY ONE part
    /// (the engine writes / collision-checks only the SELECTED outputs). A future ticket that ever
    /// selects &gt;1 kept part MUST reintroduce a <c>{index}</c> token to avoid a real collision.
    /// </summary>
    public const string TrimmedNamingPattern = "{name}_trimmed{ext}";

    /// <summary>
    /// Resolve the 1-based index of the kept MIDDLE segment for a bulk trim, by running the REAL
    /// <see cref="SplitPlanner.Plan"/> (never re-deriving its drop/merge/snap rules) and reading
    /// back which planned segment starts at the (snapped) intro-end boundary.
    ///
    /// <para>Because the planner drops a cut that snaps to ~0 or ~duration, the kept index is
    /// <b>not</b> always 2: it is <b>1</b> when the intro cut snaps to ~0 and is dropped (kept part
    /// begins at file start <c>[0..outro]</c>), and <b>2</b> when the intro survives
    /// (<c>[introEnd..outro|EOF]</c>). Feed the returned index to
    /// <see cref="BuildKeptMiddleRequest"/> → <see cref="ISplitEngine.SplitAsync"/>.</para>
    /// </summary>
    /// <param name="duration">Total media duration from the probe.</param>
    /// <param name="keyframes">Sorted keyframe times from the probe (may be empty — raw times then).</param>
    /// <param name="snap">Nearest-keyframe snapper — pass <c>probe.SnapToNearestKeyframe</c>.</param>
    /// <param name="averageGop">Mean GOP from the probe (planner uses it only for coarse-snap warnings).</param>
    /// <param name="introEnd">Requested intro-end (the start of the kept middle).</param>
    /// <param name="outroStart">Optional requested outro-start; <c>null</c> ⇒ keep runs to EOF.</param>
    /// <returns>The 1-based index of the kept middle segment in the planned contiguous set.</returns>
    /// <exception cref="SplitException">
    /// Propagated from <see cref="SplitPlanner.Plan"/> when BOTH boundaries collapse (e.g. intro
    /// snaps to ~0 and there is no outro) — no cut survives, so the trim is a no-op and no bogus
    /// index is returned. The upstream VM gates this case; Core stays honest.
    /// </exception>
    public static int ResolveKeptIndex(
        TimeSpan duration,
        IReadOnlyList<TimeSpan> keyframes,
        Func<IReadOnlyList<TimeSpan>, TimeSpan, KeyframeSnap> snap,
        TimeSpan averageGop,
        TimeSpan introEnd,
        TimeSpan? outroStart)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        ArgumentNullException.ThrowIfNull(snap);

        // The requested cuts a bulk trim asks for: the intro-end, plus the outro-start when present.
        var requestedCuts = outroStart is { } outro
            ? new[] { introEnd, outro }
            : new[] { introEnd };

        // Defer to the REAL planner (drop/merge/snap rules + both-collapse SplitException) — a
        // throwaway pathFor because only the index math matters here, never the output path. If no
        // cut survives, Plan throws SplitException and we let it propagate.
        var plan = SplitPlanner.Plan(
            duration,
            requestedCuts,
            keyframes,
            snap,
            averageGop,
            static _ => "unused");

        // The kept middle begins at the SNAPPED intro-end. Mirror the planner's own drop rule: an
        // intro cut that snaps to ~0 (or out of bounds) is dropped, so the kept part begins at file
        // start (0). Guard the empty-keyframes case where snapping is a no-op (SnapToNearestKeyframe
        // throws on an empty list, so use the raw time).
        var introSnap = keyframes.Count == 0 ? introEnd : snap(keyframes, introEnd).Snapped;
        if (introSnap <= TimeSpan.Zero || introSnap >= duration)
        {
            introSnap = TimeSpan.Zero;
        }

        // Return the 1-based index of the segment whose SnappedStart is nearest the expected kept
        // start. Segments are contiguous with distinct snapped starts, so this is an exact match:
        // index 1 when the intro was dropped ([0..outro]); index 2 when the intro survived.
        var bestIndex = 0;
        var bestDistance = (plan.Segments[0].SnappedStart - introSnap).Duration();
        for (var i = 1; i < plan.Segments.Count; i++)
        {
            var distance = (plan.Segments[i].SnappedStart - introSnap).Duration();
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex + 1;
    }

    /// <summary>
    /// Assemble the single-kept-segment <see cref="SplitRequest"/> for a bulk trim: same-folder
    /// output, <see cref="TrimmedNamingPattern"/> naming, and a single-element
    /// <see cref="SplitRequest.SelectedSegmentIndices"/> so the engine's per-segment
    /// <c>-ss/-to -c copy</c> path writes ONLY the kept middle. Resolve <paramref name="keptIndex"/>
    /// with <see cref="ResolveKeptIndex"/> first, from the same probed keyframes.
    /// </summary>
    /// <param name="inputPath">Path to the source media file (its extension is inherited by the output).</param>
    /// <param name="introEnd">Requested intro-end — the first cut point.</param>
    /// <param name="outroStart">Optional requested outro-start; <c>null</c> ⇒ single cut, keep runs to EOF.</param>
    /// <param name="keptIndex">1-based index of the kept middle (from <see cref="ResolveKeptIndex"/>).</param>
    /// <param name="overwrite">When true, replaces an existing <c>_trimmed</c> output; default false.</param>
    /// <returns>A <see cref="SplitRequest"/> ready to hand to <see cref="ISplitEngine.SplitAsync"/>.</returns>
    public static SplitRequest BuildKeptMiddleRequest(
        string inputPath,
        TimeSpan introEnd,
        TimeSpan? outroStart,
        int keptIndex,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        // Same-folder rule: outputs land next to the source (GetFullPath so a relative input still
        // resolves to a concrete directory).
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;

        IReadOnlyList<TimeSpan> cutPoints = outroStart is { } outro
            ? new[] { introEnd, outro }
            : new[] { introEnd };

        return new SplitRequest(
            InputPath: inputPath,
            CutPoints: cutPoints,
            OutputDir: outputDir,
            NamingPattern: TrimmedNamingPattern,
            Overwrite: overwrite,
            SelectedSegmentIndices: new[] { keptIndex });
    }
}
