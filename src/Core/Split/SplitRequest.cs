namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// A request to split one media file at a set of cut points into contiguous segments,
/// using lossless stream-copy (<c>-c copy</c>) with every cut snapped to a keyframe.
/// </summary>
/// <param name="InputPath">Absolute or relative path to the media file to split.</param>
/// <param name="CutPoints">
/// Requested cut times, measured from the start of the file. Order and duplicates do not
/// matter — the planner sorts, dedupes (within a small epsilon), and drops any cut at or
/// beyond the file bounds, reporting each adjustment as a warning.
/// </param>
/// <param name="OutputDir">Directory the segments are written into (created if absent).</param>
/// <param name="NamingPattern">
/// Segment filename template. Tokens: <c>{name}</c> = input filename without extension,
/// <c>{ext}</c> = input extension including the dot, <c>{index}</c> = 1-based segment number
/// (supports a <c>:00</c>-style zero-pad, e.g. <c>{index:00}</c>).
/// </param>
/// <param name="Overwrite">
/// When false (default), a split that would clobber an existing output file is rejected
/// before any ffmpeg runs. When true, existing files are replaced.
/// </param>
public sealed record SplitRequest(
    string InputPath,
    IReadOnlyList<TimeSpan> CutPoints,
    string OutputDir,
    string NamingPattern = SplitRequest.DefaultNamingPattern,
    bool Overwrite = false)
{
    /// <summary>Default segment filename template: <c>&lt;name&gt;_part01.mp4</c>, etc.</summary>
    public const string DefaultNamingPattern = "{name}_part{index:00}{ext}";
}
