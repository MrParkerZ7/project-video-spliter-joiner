namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// The result of a Bulk Cut batch run: the overall <see cref="BatchOutcome"/> plus one
/// <see cref="BulkTrimItemResult"/> per input item, IN INPUT ORDER (ledger-completeness invariant —
/// <c>Items.Count == items.Count</c>). Convenience tallies drive the end-of-run report and the
/// "Retry failed (N)" relabel.
/// </summary>
/// <param name="Outcome">The batch-level terminal state.</param>
/// <param name="Items">One entry per input row, in input order.</param>
public sealed record BatchResult(
    BatchOutcome Outcome,
    IReadOnlyList<BulkTrimItemResult> Items)
{
    /// <summary>Number of rows that finished <see cref="ItemOutcome.Done"/>.</summary>
    public int DoneCount => Count(ItemOutcome.Done);

    /// <summary>Number of rows that finished <see cref="ItemOutcome.Failed"/>.</summary>
    public int FailedCount => Count(ItemOutcome.Failed);

    /// <summary>Number of rows that finished <see cref="ItemOutcome.Skipped"/>.</summary>
    public int SkippedCount => Count(ItemOutcome.Skipped);

    /// <summary>The failed rows, in input order — the subset the UI offers to retry.</summary>
    public IReadOnlyList<BulkTrimItemResult> FailedItems =>
        Items.Where(r => r.Outcome == ItemOutcome.Failed).ToList();

    private int Count(ItemOutcome outcome)
    {
        var n = 0;
        foreach (var r in Items)
        {
            if (r.Outcome == outcome)
            {
                n++;
            }
        }

        return n;
    }
}
