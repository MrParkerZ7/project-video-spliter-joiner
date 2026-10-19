namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// What one frame-exact cut costs, in copy-equivalent seconds (T-191, G-059): the seconds of video the plan re-encodes
/// (<see cref="ReencodedSeconds"/>) and the seconds it stream-copies (<see cref="CopiedSeconds"/>). A second of
/// re-encoded video costs <see cref="SmartCutEngine.EncodeCostFactor"/> times as much wall time as a second of copied
/// video, so <see cref="Total"/> is <c>h·k + c</c>.
///
/// <para>This is the ONE function both progress bars use. <see cref="SmartCutEngine"/> splits a row's fraction between its
/// head encode and its concat by <see cref="ReencodeShare"/> (<c>w = h·k / (h·k + c)</c>), and the Bulk Cut batch weighs an
/// Exact row by <see cref="Total"/> — so the row bar and the batch bar can never weigh the same cut differently, and the
/// batch bar and the time left move during a head encode in proportion to what the head really costs. Pure, UI-free; it
/// shapes how the bars move, never what is cut.</para>
/// </summary>
/// <param name="ReencodedSeconds">Seconds of video the plan re-encodes (<c>h</c>).</param>
/// <param name="CopiedSeconds">Seconds of video the plan stream-copies (<c>c</c>).</param>
public readonly record struct SmartCutCost(double ReencodedSeconds, double CopiedSeconds)
{
    /// <summary><c>h·k + c</c>: the cut's cost in copy-equivalent seconds.</summary>
    public double Total => (ReencodedSeconds * SmartCutEngine.EncodeCostFactor) + CopiedSeconds;

    /// <summary>
    /// The share of the cost that is the re-encode, <c>w = h·k / (h·k + c)</c> in [0, 1]: where the head encode's slice of a row's
    /// fraction ends. 0 for a plan that re-encodes nothing, 1 for one that copies nothing, 0 when the cut costs nothing at all.
    /// </summary>
    public double ReencodeShare
    {
        get
        {
            var total = Total;
            return total > 0 ? Math.Clamp(ReencodedSeconds * SmartCutEngine.EncodeCostFactor / total, 0d, 1d) : 0d;
        }
    }

    /// <summary>
    /// The cost of <paramref name="plan"/> for a source <paramref name="duration"/> long.
    /// <list type="bullet">
    /// <item><description><see cref="SmartCutStrategy.HeadReencode"/>: <c>h</c> = <c>HeadEnd − Start</c> (the plan's
    /// <see cref="SmartCutPlan.ReencodedDuration"/>), <c>c</c> = <c>(End ?? duration) − HeadEnd</c>.</description></item>
    /// <item><description><see cref="SmartCutStrategy.FullReencode"/>: <c>h</c> = <c>(End ?? duration) − Start</c>, <c>c</c> = 0.
    /// <see cref="SmartCutPlan.ReencodedDuration"/> reads 0 here when the range has no end, which is the range to the end of the
    /// file, so the end of the file is used.</description></item>
    /// <item><description><see cref="SmartCutStrategy.PureCopy"/>: <c>h</c> = 0, <c>c</c> = the whole range.</description></item>
    /// </list>
    /// Negative spans (a range that ends before it starts) count as zero.
    /// </summary>
    public static SmartCutCost Of(SmartCutPlan plan, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var end = plan.End ?? duration;
        switch (plan.Strategy)
        {
            case SmartCutStrategy.PureCopy:
                return new SmartCutCost(0d, Seconds(end - plan.Start));

            case SmartCutStrategy.FullReencode:
                return new SmartCutCost(Seconds(end - plan.Start), 0d);

            default:
                var headEnd = plan.HeadEnd ?? plan.Start;
                return new SmartCutCost(Seconds(headEnd - plan.Start), Seconds(end - headEnd));
        }
    }

    private static double Seconds(TimeSpan span) => span > TimeSpan.Zero ? span.TotalSeconds : 0d;
}
