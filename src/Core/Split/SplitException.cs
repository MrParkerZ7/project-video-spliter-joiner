namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// Thrown for a genuinely invalid split request that cannot yield a friendly, partial
/// result — a null/empty input path, a missing input file, or an unwritable output
/// directory. Ordinary user-fixable problems (out-of-range or duplicate cut points) are
/// NOT exceptions: they are normalized by the planner and surfaced as warnings on
/// <see cref="SplitResult"/>.
/// <para>
/// When the failure came from an ffmpeg run, <see cref="LogFilePath"/> points at the saved full
/// log for that run and <see cref="FullStdErr"/> carries its complete stderr — both optional (null
/// for validation-only failures that never launched ffmpeg) and threaded onto the user-facing error.
/// </para>
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

    /// <summary>Create a split exception carrying the saved full-log path + complete stderr of a failed ffmpeg run.</summary>
    public SplitException(string message, string? logFilePath, string? fullStdErr)
        : base(message)
    {
        LogFilePath = logFilePath;
        FullStdErr = fullStdErr;
    }

    /// <summary>Path of the saved full log for the failed run, or null if none was written / applicable.</summary>
    public string? LogFilePath { get; }

    /// <summary>The complete stderr of the failed ffmpeg run, or null if not applicable.</summary>
    public string? FullStdErr { get; }
}
