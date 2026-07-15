namespace VideoSplitJoiner.Core.Join;

/// <summary>
/// Outcome of a join: either success carrying the written <see cref="OutputPath"/>, or a
/// refusal carrying the <see cref="Refusal"/> compat report that explains why nothing was
/// written. Exactly one of the two is populated — a refusal NEVER leaves an output file behind.
/// </summary>
/// <param name="Success">True when the join wrote an output file.</param>
/// <param name="OutputPath">The written file path on success; null on refusal.</param>
/// <param name="Refusal">The incompatibility report on refusal; null on success.</param>
public sealed record JoinResult(bool Success, string? OutputPath, CompatReport? Refusal)
{
    /// <summary>Create a success result for a written output.</summary>
    public static JoinResult Ok(string outputPath) => new(true, outputPath, null);

    /// <summary>Create a refusal result carrying the incompatibility report (no file written).</summary>
    public static JoinResult Refused(CompatReport report) => new(false, null, report);
}
