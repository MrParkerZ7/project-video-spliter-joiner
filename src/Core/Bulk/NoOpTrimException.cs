namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// Signals that a batch row resolves to a NO-OP trim — both requested boundaries collapse so no
/// cut survives and nothing would be removed (D-004 edge #3). Raised by an
/// <see cref="IBulkTrimRequestBuilder"/> when kept-index resolution (T-094) reports the plan is
/// empty. The batch runner treats it as <see cref="ItemOutcome.Skipped"/> (a deliberate no-op, not
/// a failure) — distinct from a genuine <see cref="Split.SplitException"/>, which maps to
/// <see cref="ItemOutcome.Failed"/>.
/// </summary>
public sealed class NoOpTrimException : Exception
{
    /// <summary>Create a no-op-trim signal with a human-readable reason.</summary>
    public NoOpTrimException(string message)
        : base(message)
    {
    }

    /// <summary>Create a no-op-trim signal wrapping the underlying planner cause.</summary>
    public NoOpTrimException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
