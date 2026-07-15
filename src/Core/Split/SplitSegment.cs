namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// One output segment produced by a split. Records both the requested boundary and the
/// keyframe-snapped boundary actually used, plus the signed snap offset, so a caller can
/// show the user how far each cut moved to land on a copyable keyframe.
/// </summary>
/// <param name="Path">Absolute path to the written segment file.</param>
/// <param name="Start">
/// The requested start boundary of this segment (the previous cut, or zero for the first
/// segment). This is the pre-snap value.
/// </param>
/// <param name="End">
/// The requested end boundary of this segment (the next cut, or the file duration for the
/// last segment). Pre-snap.
/// </param>
/// <param name="ActualStart">
/// The keyframe-snapped start actually used for extraction. Equals <see cref="Start"/> for
/// the first segment (always zero) and the snapped previous-cut for later segments.
/// </param>
/// <param name="Delta">
/// Signed snap offset for this segment's START boundary (<c>ActualStart - Start</c>);
/// negative means the boundary snapped earlier. Zero for the first segment.
/// </param>
public sealed record SplitSegment(
    string Path,
    TimeSpan Start,
    TimeSpan End,
    TimeSpan ActualStart,
    TimeSpan Delta);
