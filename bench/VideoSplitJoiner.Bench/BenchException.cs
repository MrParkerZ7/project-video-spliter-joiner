namespace VideoSplitJoiner.Bench;

/// <summary>The bench's exit codes. Anything but <see cref="Ok"/> is a failure.</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Unexpected = 1;
    public const int Usage = 2;
    public const int PathRefused = 3;
    public const int FfmpegMissing = 4;
    public const int NoFixture = 5;
    public const int CheckFailed = 6;
    public const int DiskSpace = 7;
    public const int Environment = 8;
}

/// <summary>
/// A refusal or failure the bench reports with a message and a non-zero exit code. Commands throw it; the
/// CLI prints the message and exits with <see cref="Code"/>; the selftest catches it in-process, so a refused
/// path can be checked to have started no process and read no byte.
/// </summary>
internal sealed class BenchException : Exception
{
    public BenchException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The process exit code this failure maps to (never 0).</summary>
    public int Code { get; }
}
