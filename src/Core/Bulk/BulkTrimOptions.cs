namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// Batch-level options for a Bulk Cut run. Kept a record so future knobs (parallelism, min-kept
/// span policy, …) can be added without breaking callers. v1 carries only the collision policy.
/// </summary>
/// <param name="Collision">How to resolve an output path that already exists (default <see cref="CollisionPolicy.AutoSuffix"/>).</param>
/// <param name="Output">
/// WHERE to write (T-121): a new <c>_trimmed</c> file (default, non-destructive) or over the original.
/// <see cref="OutputMode.ReplaceOriginal"/> takes precedence and <paramref name="Collision"/> is ignored,
/// because the destination is always occupied - by the source itself.
/// </param>
/// <param name="Precision">
/// How exactly the cut times are honoured (T-125): <see cref="CutPrecision.Lossless"/> snaps to
/// keyframes and copies every byte (default); <see cref="CutPrecision.Exact"/> re-encodes ~1 GOP so the
/// cut lands where it was set.
/// </param>
public sealed record BulkTrimOptions(
    CollisionPolicy Collision = CollisionPolicy.AutoSuffix,
    OutputMode Output = OutputMode.NewFile,
    CutPrecision Precision = CutPrecision.Lossless);
