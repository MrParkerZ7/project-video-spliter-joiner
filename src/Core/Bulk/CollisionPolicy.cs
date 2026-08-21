namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// How the batch runner resolves an output path that already exists on disk (D-004, edge #6).
/// Applied per item at pre-flight, BEFORE any ffmpeg runs. The source file is NEVER a write
/// target under any policy.
/// </summary>
public enum CollisionPolicy
{
    /// <summary>
    /// Default. Append <c>_2</c>, <c>_3</c>, … to the output stem until a free path is found
    /// (e.g. <c>clip_trimmed_2.mp4</c>). Never overwrites; never loses an existing file.
    /// </summary>
    AutoSuffix,

    /// <summary>Leave the existing output untouched and mark the item <see cref="ItemOutcome.Skipped"/> (no engine call).</summary>
    Skip,

    /// <summary>Replace the existing output (the request runs with <see cref="Split.SplitRequest.Overwrite"/> = true; the source is still never targeted).</summary>
    Overwrite,
}
