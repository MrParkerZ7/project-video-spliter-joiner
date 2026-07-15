namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// Outcome of a split: the produced segments plus any non-fatal warnings the planner or
/// engine raised (a cut dropped for being out of range, near-duplicate cuts merged, a
/// coarse GOP that forced a large snap, etc.). A genuinely invalid request that yields no
/// segments surfaces as a <see cref="SplitException"/> instead of a result.
/// </summary>
/// <param name="Segments">The output segments, in play order.</param>
/// <param name="Warnings">Human-readable, non-fatal notes about how the request was adjusted.</param>
public sealed record SplitResult(
    IReadOnlyList<SplitSegment> Segments,
    IReadOnlyList<string> Warnings)
{
    /// <summary>An empty result carrying only warnings (no segments produced).</summary>
    public static SplitResult Empty(IReadOnlyList<string> warnings) =>
        new(Array.Empty<SplitSegment>(), warnings);
}
