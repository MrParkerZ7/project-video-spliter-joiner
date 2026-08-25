using System;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>Which Bulk-row scrub handle a pointer is over (SPEC-014 I33).</summary>
public enum ScrubHandle
{
    None,
    Intro,
    Outro,
}

/// <summary>
/// Pure, WPF-free geometry for the Bulk Cut row scrub strip (SPEC-014 I32/I33) — the seconds→pixel
/// mapping, the keep-span extents, and the handle hit-test. Extracted from <c>BulkRowScrubView</c>
/// so the drag/snap math is unit-testable without a WPF visual; the view supplies live widths and
/// applies its own drag-override + drawing.
/// </summary>
public static class BulkScrubMath
{
    /// <summary>
    /// Pixel X of a time <paramref name="seconds"/> along a strip of <paramref name="width"/> px over
    /// <paramref name="totalSeconds"/>, clamped to [0, width]. Matches the view's inline
    /// <c>Clamp(seconds / total) × width</c> (callers guard <paramref name="totalSeconds"/> &gt; 0).
    /// </summary>
    public static double SecondsToX(double seconds, double totalSeconds, double width)
        => Math.Clamp(seconds / totalSeconds, 0d, 1d) * width;

    /// <summary>The kept-middle span extents — <c>(min, max)</c> of the two handle X's (SPEC-014 I32).</summary>
    public static (double KeepLeft, double KeepRight) KeepSpan(double introX, double outroX)
        => (Math.Min(introX, outroX), Math.Max(introX, outroX));

    /// <summary>
    /// Pick the nearer handle within <paramref name="radiusPx"/> of the pointer; an equidistant tie
    /// (&lt; 0.5px apart) is broken by vertical position — top half = intro, bottom half = outro
    /// (SPEC-014 I33). <paramref name="outroX"/> null = no outro handle present.
    /// </summary>
    public static ScrubHandle PickHandle(double introX, double? outroX, double posX, double posY, double height, double radiusPx)
    {
        var dIntro = Math.Abs(posX - introX);
        var dOutro = outroX is { } ox ? Math.Abs(posX - ox) : double.PositiveInfinity;

        var nearest = Math.Min(dIntro, dOutro);
        if (nearest > radiusPx)
        {
            return ScrubHandle.None;
        }

        if (Math.Abs(dIntro - dOutro) < 0.5)
        {
            return posY <= height / 2d ? ScrubHandle.Intro : ScrubHandle.Outro;
        }

        return dIntro <= dOutro ? ScrubHandle.Intro : ScrubHandle.Outro;
    }
}
