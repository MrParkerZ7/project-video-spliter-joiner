namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// One per-part progress sample emitted by <see cref="ISplitEngine"/> during a split (T-069):
/// which part is currently being written and how far along that part is. Reported through a
/// dedicated <see cref="IProgress{T}"/> channel ALONGSIDE the existing overall
/// <c>IProgress&lt;double&gt;</c> (0..1) — a lower-churn, typed alternative to overloading the
/// staged <c>OperationStatus</c> text so the UI can drive per-row state on the "Parts to export"
/// list. The overall progress channel is unchanged; this is purely additive.
/// </summary>
/// <param name="PartIndex">
/// The part currently being written, using its ORIGINAL 1-based index in the full plan (so a
/// selected middle part in a subset stays identifiable — still <c>part 2 of 3</c>, never renumbered).
/// </param>
/// <param name="PartCount">Total number of parts in the full plan (selected or not).</param>
/// <param name="PartFraction">
/// Local progress of <see cref="PartIndex"/>, 0..1 — how far through THIS part's own
/// <c>[start,end)</c> the write is. Clamped to [0,1].
/// </param>
public readonly record struct PartProgress(int PartIndex, int PartCount, double PartFraction);

/// <summary>
/// Pure time→part mapping used by the fast single-pass segment-muxer path (T-069). The muxer
/// writes every contiguous part in ONE ffmpeg pass and reports a single monotonic <c>time=</c>
/// into the file; this maps that absolute time onto "which part, how far into it" so the UI can
/// show per-part progress WITHOUT re-extracting anything. No ffmpeg, no I/O — fully unit-testable.
/// </summary>
public static class PartMapping
{
    /// <summary>
    /// Map an absolute <paramref name="time"/> into the file onto the part that contains it and the
    /// local fraction within that part, given the interior cut <paramref name="boundaries"/> and the
    /// total <paramref name="duration"/>.
    ///
    /// <para><paramref name="boundaries"/> are the ascending interior cut times strictly between 0 and
    /// <paramref name="duration"/> (i.e. <c>SplitPlan.InteriorSnappedCuts</c>): with cuts
    /// <c>c1 &lt; c2 &lt; … &lt; cN</c> the parts are <c>[0,c1), [c1,c2), …, [cN,duration)</c> —
    /// <c>N+1</c> parts. No cuts → a single part <c>[0,duration)</c>.</para>
    ///
    /// <para>Returns a 1-based <c>PartIndex</c> and a <c>PartFraction</c> in [0,1]. A time exactly ON a
    /// boundary belongs to the LATER part (the boundary is the start of the next part, so
    /// <c>PartFraction</c> is 0 there — a <c>[start,end)</c> half-open convention). Time at/beyond
    /// <paramref name="duration"/> clamps to the last part fully done (fraction 1); time at/below 0
    /// clamps to the first part at fraction 0. A zero-length part (coincident boundaries — the planner
    /// prevents this, but guard anyway) reports fraction 1 rather than dividing by zero.</para>
    /// </summary>
    /// <param name="time">Absolute elapsed time into the source file.</param>
    /// <param name="boundaries">Ascending interior cut times (0 &lt; c1 &lt; … &lt; cN &lt; duration).</param>
    /// <param name="duration">Total media duration (the end of the last part).</param>
    /// <returns>The containing part's 1-based index and the local 0..1 fraction within it.</returns>
    public static (int PartIndex, double PartFraction) PartAt(
        TimeSpan time,
        IReadOnlyList<TimeSpan> boundaries,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(boundaries);

        var partCount = boundaries.Count + 1;

        // Clamp a time at/below zero to the very start of part 1.
        if (time <= TimeSpan.Zero)
        {
            return (1, 0.0);
        }

        // Clamp a time at/beyond the end to the last part, fully done.
        if (duration > TimeSpan.Zero && time >= duration)
        {
            return (partCount, 1.0);
        }

        // Walk the parts. Part k (1-based) spans [start_k, end_k):
        //   start_1 = 0, start_k = boundaries[k-2]; end_k = boundaries[k-1], end_last = duration.
        // A time ON a boundary falls into the LATER part (half-open [start,end)).
        var start = TimeSpan.Zero;
        for (var i = 0; i < boundaries.Count; i++)
        {
            var end = boundaries[i];
            if (time < end)
            {
                return (i + 1, Fraction(time, start, end));
            }

            start = end;
        }

        // Past every interior cut → the final part [lastBoundary, duration).
        return (partCount, Fraction(time, start, duration));
    }

    /// <summary>Local fraction of <paramref name="time"/> within <c>[start,end)</c>, clamped to [0,1]; a non-positive span → 1.</summary>
    private static double Fraction(TimeSpan time, TimeSpan start, TimeSpan end)
    {
        var span = (end - start).TotalSeconds;
        if (span <= 0.0)
        {
            return 1.0;
        }

        var f = (time - start).TotalSeconds / span;
        if (f < 0.0)
        {
            return 0.0;
        }

        return f > 1.0 ? 1.0 : f;
    }
}
