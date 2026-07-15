namespace VideoSplitJoiner.Core.Detect;

/// <summary>
/// One auto-detected split-point candidate. <see cref="Time"/> is where the event was
/// detected in the source; <see cref="SnappedTime"/> is that time snapped to the nearest
/// keyframe (via T-003) because any eventual cut lands on a keyframe anyway.
/// </summary>
/// <param name="Time">The detected event time in the source.</param>
/// <param name="SnappedTime">The nearest keyframe to <paramref name="Time"/> — the copyable cut boundary.</param>
/// <param name="Kind">Whether this is a black, white, or scene boundary.</param>
/// <param name="Score">Normalized confidence in 0..1 (1 = strongest).</param>
/// <param name="Rank">1-based rank across all candidates (1 = best/highest combined confidence).</param>
public sealed record Candidate(
    TimeSpan Time,
    TimeSpan SnappedTime,
    CandidateKind Kind,
    double Score,
    int Rank);
