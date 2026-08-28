namespace VideoSplitJoiner.Core.Split;

/// <summary>How a requested cut range will be produced (T-124, epic G-042).</summary>
public enum SmartCutStrategy
{
    /// <summary>
    /// The requested start already sits on a keyframe, so the whole range is a pure stream copy —
    /// byte-identical to the lossless path, zero re-encoding. Always preferred when available.
    /// </summary>
    PureCopy,

    /// <summary>
    /// The requested start falls mid-GOP: re-encode the short head fragment (start → next keyframe)
    /// and stream-copy the remainder, then concatenate. Only the head is re-encoded.
    /// </summary>
    HeadReencode,

    /// <summary>
    /// The requested range lies entirely inside the final GOP (no keyframe between start and end), so
    /// there is no copyable tail — the whole (short) range is re-encoded. Rare, and bounded by one GOP.
    /// </summary>
    FullReencode,
}

/// <summary>
/// The resolved plan for one frame-exact cut. <see cref="HeadEnd"/> is the keyframe the copyable tail
/// starts on; it is null when there is no tail (<see cref="SmartCutStrategy.FullReencode"/>) or no head
/// (<see cref="SmartCutStrategy.PureCopy"/>).
/// </summary>
/// <param name="Strategy">Which production path this range needs.</param>
/// <param name="Start">The user's requested start — honoured EXACTLY, which is the point of this feature.</param>
/// <param name="End">The requested end, or null for end-of-file.</param>
/// <param name="HeadEnd">Boundary keyframe: the head is [Start, HeadEnd), the tail is [HeadEnd, End).</param>
public sealed record SmartCutPlan(
    SmartCutStrategy Strategy,
    TimeSpan Start,
    TimeSpan? End,
    TimeSpan? HeadEnd)
{
    /// <summary>True when this plan re-encodes anything at all.</summary>
    public bool HasReencode => Strategy != SmartCutStrategy.PureCopy;

    /// <summary>Duration of the re-encoded fragment (zero for a pure copy) — the cost the user pays.</summary>
    public TimeSpan ReencodedDuration => Strategy switch
    {
        SmartCutStrategy.PureCopy => TimeSpan.Zero,
        SmartCutStrategy.HeadReencode => (HeadEnd ?? Start) - Start,
        _ => (End ?? Start) - Start,
    };
}

/// <summary>
/// Pure decision logic for frame-exact ("smart") cutting (T-124, epic G-042).
///
/// <para>A stream-copied segment must START on a keyframe — that is what makes the lossless path
/// instant and quality-preserving, and it is why a request at 5s on a 4s keyframe grid lands at 4s.
/// Smart cutting honours the requested time instead by re-encoding only the fragment between the
/// request and the next keyframe, then copying everything after it. The planner decides which shape
/// applies; it never re-encodes more than one GOP.</para>
/// </summary>
public static class SmartCutPlanner
{
    /// <summary>
    /// A request within this tolerance of a keyframe is treated as being ON it, so floating-point
    /// noise (or a UI-rounded time) does not trigger a pointless re-encode.
    /// </summary>
    public static readonly TimeSpan OnKeyframeTolerance = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Plan a frame-exact cut of <c>[start, end)</c> against the file's <paramref name="keyframes"/>.
    /// </summary>
    /// <param name="start">The user's requested start. Honoured exactly.</param>
    /// <param name="end">The requested end, or null for end-of-file.</param>
    /// <param name="keyframes">Ascending keyframe times (may be empty — then nothing can be copied).</param>
    public static SmartCutPlan Plan(TimeSpan start, TimeSpan? end, IReadOnlyList<TimeSpan> keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);

        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Start must not be negative.");
        }

        if (end is { } e && e <= start)
        {
            throw new ArgumentException("End must be after start.", nameof(end));
        }

        // Already on a keyframe → the lossless path produces exactly what was asked for.
        if (IsOnKeyframe(start, keyframes))
        {
            return new SmartCutPlan(SmartCutStrategy.PureCopy, start, end, null);
        }

        // The first keyframe STRICTLY after the requested start is where a copyable tail can begin.
        var next = NextKeyframeAfter(start, keyframes);

        // No keyframe ahead (or it lies at/after the requested end) → nothing is copyable; re-encode
        // the whole (necessarily short) range.
        if (next is not { } k || (end is { } endTime && k >= endTime))
        {
            return new SmartCutPlan(SmartCutStrategy.FullReencode, start, end, null);
        }

        return new SmartCutPlan(SmartCutStrategy.HeadReencode, start, end, k);
    }

    /// <summary>True when <paramref name="t"/> sits on a keyframe within <see cref="OnKeyframeTolerance"/>.</summary>
    public static bool IsOnKeyframe(TimeSpan t, IReadOnlyList<TimeSpan> keyframes)
    {
        foreach (var k in keyframes)
        {
            var delta = k - t;
            if (delta < TimeSpan.Zero)
            {
                delta = delta.Negate();
            }

            if (delta <= OnKeyframeTolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The earliest keyframe strictly after <paramref name="t"/> (beyond the on-keyframe tolerance), or
    /// null when none exists. Does not assume the list is sorted.
    /// </summary>
    public static TimeSpan? NextKeyframeAfter(TimeSpan t, IReadOnlyList<TimeSpan> keyframes)
    {
        TimeSpan? best = null;
        foreach (var k in keyframes)
        {
            if (k - t <= OnKeyframeTolerance)
            {
                continue; // at or before the request
            }

            if (best is null || k < best.Value)
            {
                best = k;
            }
        }

        return best;
    }
}
