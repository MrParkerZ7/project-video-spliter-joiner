namespace VideoSplitJoiner.Core.Bulk;

/// <summary>The terminal state of a single batch row.</summary>
public enum ItemOutcome
{
    /// <summary>Trimmed successfully; the output landed at <see cref="BulkTrimItemResult.OutputPath"/>.</summary>
    Done,

    /// <summary>The row's trim raised an error (mapped into <see cref="BulkTrimItemResult.Error"/>); the batch continued.</summary>
    Failed,

    /// <summary>Skipped without running — a <see cref="CollisionPolicy.Skip"/> collision, or a no-op trim (nothing would be removed).</summary>
    Skipped,

    /// <summary>This row was in flight when the batch was cancelled; its ffmpeg temp was swept and no partial output was moved into place.</summary>
    Cancelled,

    /// <summary>The batch stopped (cancel or a pre-flight block) before this row ever started.</summary>
    NotStarted,
}
