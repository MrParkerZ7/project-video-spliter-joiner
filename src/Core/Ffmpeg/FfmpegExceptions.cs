namespace VideoSplitJoiner.Core.Ffmpeg;

/// <summary>
/// Thrown by <see cref="IFfmpegBinaryLocator"/> when neither an explicit override,
/// an app-local <c>ffmpeg/</c> folder, nor <c>PATH</c> yields a usable binary.
/// </summary>
public sealed class FfmpegNotFoundException : Exception
{
    public FfmpegNotFoundException(string message)
        : base(message)
    {
    }

    public FfmpegNotFoundException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Thrown by <see cref="IFfprobeRunner"/> when ffprobe exits non-zero. ffprobe is a
/// query — a failure there is exceptional (unlike an ffmpeg conversion, whose
/// non-zero exit is reported as a <see cref="FfmpegResult"/>). Carries the stderr tail.
/// </summary>
public sealed class FfprobeException : Exception
{
    public FfprobeException(int exitCode, IReadOnlyList<string> stdErrTail)
        : base($"ffprobe exited with code {exitCode}.{Environment.NewLine}{string.Join(Environment.NewLine, stdErrTail)}")
    {
        ExitCode = exitCode;
        StdErrTail = stdErrTail;
    }

    /// <summary>ffprobe's exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Tail of ffprobe's stderr output.</summary>
    public IReadOnlyList<string> StdErrTail { get; }
}
