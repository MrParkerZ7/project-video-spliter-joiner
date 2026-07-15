namespace VideoSplitJoiner.Core.Ffmpeg;

/// <summary>
/// Outcome of an ffmpeg run. Returned for ANY exit code — a non-zero exit is a
/// normal, expected result for ffmpeg (bad input, unsupported codec, etc.), not an
/// exception. Carries the tail of stderr for diagnostics.
/// </summary>
public sealed record FfmpegResult(int ExitCode, IReadOnlyList<string> StdErrTail)
{
    /// <summary>True when ffmpeg exited 0.</summary>
    public bool Success => ExitCode == 0;

    /// <summary>The captured stderr tail joined into a single newline-delimited string.</summary>
    public string StdErrText => string.Join(Environment.NewLine, StdErrTail);
}
