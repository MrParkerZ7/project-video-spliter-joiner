namespace VideoSplitJoiner.Core.Bulk;

/// <summary>Which phase of a batch run a <see cref="BulkTrimProgress"/> sample belongs to.</summary>
public enum BulkTrimPhase
{
    /// <summary>Batch disk pre-flight — reported once at the very start, before any row runs.</summary>
    Preflight,

    /// <summary>Batch-level running transitions (entering the loop, terminal completion).</summary>
    Running,

    /// <summary>A per-item progress sample forwarded from the engine for the current row.</summary>
    Item,
}

/// <summary>
/// A progress sample for a batch run. <see cref="OverallFraction"/> is monotonic non-decreasing
/// across a run and reaches <c>1.0</c> on normal completion; <see cref="ItemFraction"/> is the
/// current row's local 0..1 fraction forwarded from the engine.
/// </summary>
/// <param name="ItemIndex">0-based index of the current row in the input list (also the index into <see cref="BatchResult.Items"/>).</param>
/// <param name="ItemCount">Total number of rows in the batch.</param>
/// <param name="CurrentFileName">File name of the current row's source (empty for batch-level samples).</param>
/// <param name="ItemFraction">The current row's local progress, 0..1.</param>
/// <param name="OverallFraction">Whole-batch progress, 0..1 — monotonic non-decreasing.</param>
/// <param name="Phase">Which phase this sample belongs to.</param>
public readonly record struct BulkTrimProgress(
    int ItemIndex,
    int ItemCount,
    string CurrentFileName,
    double ItemFraction,
    double OverallFraction,
    BulkTrimPhase Phase);
