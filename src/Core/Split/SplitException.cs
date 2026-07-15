namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// Thrown for a genuinely invalid split request that cannot yield a friendly, partial
/// result — a null/empty input path, a missing input file, or an unwritable output
/// directory. Ordinary user-fixable problems (out-of-range or duplicate cut points) are
/// NOT exceptions: they are normalized by the planner and surfaced as warnings on
/// <see cref="SplitResult"/>.
/// </summary>
public sealed class SplitException : Exception
{
    /// <summary>Create a split exception with a human-readable reason.</summary>
    public SplitException(string message)
        : base(message)
    {
    }

    /// <summary>Create a split exception wrapping an inner cause.</summary>
    public SplitException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
