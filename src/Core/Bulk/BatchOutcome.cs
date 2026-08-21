namespace VideoSplitJoiner.Core.Bulk;

/// <summary>The terminal state of a whole batch run.</summary>
public enum BatchOutcome
{
    /// <summary>Every row finished <see cref="ItemOutcome.Done"/>.</summary>
    Completed,

    /// <summary>The batch ran to the end but at least one row was <see cref="ItemOutcome.Failed"/> or <see cref="ItemOutcome.Skipped"/>.</summary>
    CompletedWithFailures,

    /// <summary>The user cancelled mid-batch: the in-flight row is <see cref="ItemOutcome.Cancelled"/>, later rows are <see cref="ItemOutcome.NotStarted"/>, earlier <see cref="ItemOutcome.Done"/> rows are kept.</summary>
    Cancelled,

    /// <summary>A batch disk pre-flight found a clear shortfall and blocked the whole batch BEFORE any ffmpeg ran — every row is <see cref="ItemOutcome.NotStarted"/>.</summary>
    Blocked,
}
