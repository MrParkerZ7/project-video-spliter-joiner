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
/// <param name="SelectedSegmentIndices">
/// The 1-based indices of the contiguous segments to actually write (T-049). The planner always
/// computes the full contiguous set <c>[0..s1],[s1..s2],…,[sN..end]</c>; this restricts which of
/// those parts are extracted. <c>null</c> (the default) means "all segments" — today's behaviour,
/// so the field is fully backward-compatible. When a strict SUBSET is selected, the engine writes
/// ONLY those parts (via the per-segment <c>-ss/-to -c copy</c> path) and keeps each part's
/// ORIGINAL index in its filename (a selected middle part is still <c>_part02</c>). Indices are
/// deduped and clamped to the planned range; any out-of-range index is ignored. An empty (non-null)
/// list selects nothing and is rejected as an invalid request.
/// </param>
public sealed record SplitRequest(
    string InputPath,
    IReadOnlyList<TimeSpan> CutPoints,
    string OutputDir,
    string NamingPattern = SplitRequest.DefaultNamingPattern,
    bool Overwrite = false,
    IReadOnlyList<int>? SelectedSegmentIndices = null)
{
    /// <summary>Default segment filename template: <c>&lt;name&gt;_part01.mp4</c>, etc.</summary>
    public const string DefaultNamingPattern = "{name}_part{index:00}{ext}";
}
