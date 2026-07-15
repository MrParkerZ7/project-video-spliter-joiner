namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// Outcome of a probe attempt: either a successful <see cref="MediaInfo"/> or a typed
/// failure carrying a human-readable reason. A corrupt or non-media file yields a
/// <see cref="ProbeFailed"/> rather than a thrown exception, so callers branch on the
/// result instead of catching.
/// </summary>
public abstract record ProbeResult
{
    private ProbeResult()
    {
    }

    /// <summary>True when this result carries a <see cref="MediaInfo"/>.</summary>
    public bool IsSuccess => this is ProbeSucceeded;

    /// <summary>Create a success result.</summary>
    public static ProbeResult Success(MediaInfo info) => new ProbeSucceeded(info);

    /// <summary>Create a failure result with the given reason.</summary>
    public static ProbeResult Failure(string reason) => new ProbeFailed(reason);

    /// <summary>Successful probe — carries the parsed <see cref="MediaInfo"/>.</summary>
    public sealed record ProbeSucceeded(MediaInfo Info) : ProbeResult;

    /// <summary>Failed probe — carries the reason (bad path, non-media, ffprobe error, …).</summary>
    public sealed record ProbeFailed(string Reason) : ProbeResult;
}
