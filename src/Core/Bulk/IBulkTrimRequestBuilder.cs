using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// The T-094 seam: turns a <see cref="BulkTrimItem"/> plus its collision-resolved output path into
/// the single-kept-segment <see cref="SplitRequest"/> the engine runs. Delegating request
/// construction keeps <see cref="BulkTrimEngine"/>'s loop thin and probe-free at test time (a test
/// passes a canned builder; production passes <see cref="KeptMiddleRequestBuilder"/>, which probes
/// and calls <see cref="KeptSegmentSelector.ResolveKeptIndex"/>).
/// </summary>
public interface IBulkTrimRequestBuilder
{
    /// <summary>
    /// Build the <see cref="SplitRequest"/> for one row. The request MUST write to
    /// <paramref name="effectiveOutputPath"/> (the runner already resolved collisions and the
    /// source-safety guard) and honor <paramref name="overwrite"/>.
    /// </summary>
    /// <param name="item">The row to trim.</param>
    /// <param name="effectiveOutputPath">The final, collision-resolved output path this row must write to.</param>
    /// <param name="overwrite">Whether the request may replace an existing file at that path.</param>
    /// <param name="ct">Cancellation token (a probe/scan inside the builder observes it).</param>
    /// <returns>A single-kept-segment <see cref="SplitRequest"/> ready for <see cref="ISplitEngine.SplitAsync"/>.</returns>
    /// <exception cref="NoOpTrimException">
    /// The row resolves to a no-op trim (both boundaries collapse) — the runner records it as
    /// <see cref="ItemOutcome.Skipped"/>, not a failure.
    /// </exception>
    /// <exception cref="SplitException">A genuine problem (probe failure, invalid request) — the runner records it as <see cref="ItemOutcome.Failed"/>.</exception>
    Task<SplitRequest> BuildAsync(
        BulkTrimItem item,
        string effectiveOutputPath,
        bool overwrite,
        CancellationToken ct);
}
