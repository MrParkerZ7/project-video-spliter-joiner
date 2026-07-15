namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// Result of snapping a requested time to the nearest keyframe.
/// </summary>
/// <param name="Snapped">The chosen keyframe time.</param>
/// <param name="Delta">Signed offset <c>Snapped - Requested</c> (negative = snapped earlier).</param>
public readonly record struct KeyframeSnap(TimeSpan Snapped, TimeSpan Delta);
