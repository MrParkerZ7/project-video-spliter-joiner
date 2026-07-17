namespace VideoSplitJoiner.Core.Join;

/// <summary>
/// Outcome of a join: either success carrying the written <see cref="OutputPath"/>, or a
/// refusal carrying the <see cref="Refusal"/> compat report that explains why nothing was
/// written. Exactly one of the two is populated — a refusal NEVER leaves an output file behind.
/// <para>
/// When the refusal came from a failed ffmpeg concat run, <see cref="LogFilePath"/> points at the
/// saved full log and <see cref="FullStdErr"/> carries the complete stderr — both null for
/// pre-flight refusals (incompatible inputs, empty output) that never launched ffmpeg.
/// </para>
/// </summary>
/// <param name="Success">True when the join wrote an output file.</param>
/// <param name="OutputPath">The written file path on success; null on refusal.</param>
/// <param name="Refusal">The incompatibility report on refusal; null on success.</param>
/// <param name="LogFilePath">Saved full-log path when an ffmpeg run failed; otherwise null.</param>
/// <param name="FullStdErr">Complete stderr of the failed ffmpeg run; otherwise null.</param>
public sealed record JoinResult(
    bool Success,
    string? OutputPath,
    CompatReport? Refusal,
    string? LogFilePath = null,
    string? FullStdErr = null)
{
    /// <summary>Create a success result for a written output.</summary>
    public static JoinResult Ok(string outputPath) => new(true, outputPath, null);

    /// <summary>Create a refusal result carrying the incompatibility report (no file written).</summary>
    public static JoinResult Refused(CompatReport report) => new(false, null, report);

    /// <summary>Create a refusal from a failed ffmpeg run, carrying the saved log path + full stderr.</summary>
    public static JoinResult RefusedWithLog(CompatReport report, string? logFilePath, string? fullStdErr) =>
        new(false, null, report, logFilePath, fullStdErr);
}
