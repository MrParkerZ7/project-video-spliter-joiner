namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// Runs a Bulk Cut batch (D-004): a UI-free, sequential, failure-isolated orchestrator over
/// <see cref="Split.ISplitEngine"/>. Each item is one single-kept-segment trim; one bad row is
/// caught and recorded while the loop continues, cancel tears down the in-flight ffmpeg without
/// moving a partial, and a batch disk pre-flight can block the whole run before any ffmpeg starts.
/// </summary>
public interface IBulkTrimEngine
{
    /// <summary>
    /// Run <paramref name="items"/> head-to-tail. Returns a <see cref="BatchResult"/> with exactly
    /// one entry per input item, in input order.
    /// </summary>
    /// <param name="items">The rows to trim (null/empty ⇒ a no-op <see cref="BatchOutcome.Completed"/>).</param>
    /// <param name="options">Batch options (null ⇒ defaults; <see cref="CollisionPolicy.AutoSuffix"/>).</param>
    /// <param name="progress">Optional per-item + overall progress sink.</param>
    /// <param name="ct">Cancellation token — observed before each row; an in-flight row ends <see cref="ItemOutcome.Cancelled"/>.</param>
    Task<BatchResult> RunAsync(
        IReadOnlyList<BulkTrimItem> items,
        BulkTrimOptions options,
        IProgress<BulkTrimProgress>? progress = null,
        CancellationToken ct = default);
}
