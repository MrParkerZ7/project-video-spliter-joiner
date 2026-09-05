using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-162 (SPEC-010) — the Split footer's destructive control must not land on Run's pixels.
///
/// <para>Split gained a "Delete original" button, and it appears exactly when a split finishes — under
/// the cursor of someone who has just been pressing Run. Bulk Cut learned what that costs (T-146): the
/// first attempt there appended the destructive button after Run inside Run's horizontal
/// <c>StackPanel</c> with <c>HorizontalAlignment="Left"</c>, which a StackPanel ignores along its
/// stacking axis — so it rendered immediately right of Run and revealing it shoved Run sideways. The
/// ticket's criterion was ticked from the XAML comment rather than the markup.</para>
///
/// <para>Split's action row is therefore a Grid with the danger button in its own column, and that is
/// asserted here rather than commented. Laying the view out also proves the promoted
/// <c>DangerButton</c> style actually resolves on this screen — a `StaticResource` that does not
/// resolve builds green and dies at first render.</para>
/// </summary>
public sealed class SplitViewLayoutTests
{
    private static (double W, double H)[] Sizes => StaViewHarness.Sizes;

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void TheSplitScreenLaysOutAtEveryRealisticSize_WithItsRealThemes()
    {
        var failures = new List<string>();

        StaViewHarness.OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                try
                {
                    var view = new SplitView();
                    StaViewHarness.LayOut(view, w, h);
                }
                catch (Exception ex)
                {
                    // The class of bug this catches: a brush or style key that only fails at render.
                    failures.Add($"{w}x{h}: laying SplitView out threw — {ex.GetType().Name}: {ex.Message}");
                }
            }
        });

        failures.Should().BeEmpty(
            "the Split screen must render with the app's real theme dictionaries:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void DeleteOriginalSitsAtTheOppositeEndFromRun_AndRevealingItDoesNotMoveRun()
    {
        var failures = new List<string>();

        StaViewHarness.OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var view = new SplitView();
                StaViewHarness.LayOut(view, w, h);   // the visual tree does not exist until a layout pass

                var del = StaViewHarness.Find<Button>(view, "DeleteOriginalButton");
                var run = StaViewHarness.Find<Button>(view, "RunSplitButton");

                if (del is null || run is null)
                {
                    failures.Add($"{w}x{h}: footer buttons not found — the view's shape changed");
                    continue;
                }

                // Before a split finishes the destructive button is not on screen at all.
                del.Visibility = Visibility.Collapsed;
                StaViewHarness.LayOut(view, w, h);
                var runBefore = run.TranslatePoint(new Point(0, 0), view).X;

                // The split finishes: the button appears.
                del.Visibility = Visibility.Visible;
                StaViewHarness.LayOut(view, w, h);
                var runAfter = run.TranslatePoint(new Point(0, 0), view).X;
                var delLeft = del.TranslatePoint(new Point(0, 0), view).X;
                var delRight = delLeft + del.ActualWidth;

                if (Math.Abs(runAfter - runBefore) > 1)
                {
                    failures.Add(
                        $"{w}x{h}: revealing Delete original moved Run by {runAfter - runBefore:0.#}px " +
                        $"({runBefore:0} → {runAfter:0}) — the button people press repeatedly must not " +
                        "shift when a destructive one appears");
                }

                if (delRight > runBefore)
                {
                    failures.Add(
                        $"{w}x{h}: Delete original (ends at x={delRight:0}) overlaps the pixels Run held " +
                        $"before the split finished (x={runBefore:0}) — that is the misclick this " +
                        "placement exists to prevent");
                }

                // "Far left" means the far left of THIS ROW, not of the window. Split's footer lives
                // inside the right-hand tool panel of the OrientedSplitPanel, so the row itself begins
                // most of the way across the screen — the first version of this assertion compared
                // against the window midpoint and failed at every size for a reason that had nothing to
                // do with the placement being wrong.
                var row = StaViewHarness.Find<Grid>(view, "SplitActionRow");
                if (row is null)
                {
                    failures.Add($"{w}x{h}: SplitActionRow not found — the footer's shape changed");
                    continue;
                }

                var rowLeft = row.TranslatePoint(new Point(0, 0), view).X;
                if (delLeft > rowLeft + row.ActualWidth / 2)
                {
                    failures.Add(
                        $"{w}x{h}: Delete original starts at x={delLeft:0}, past the middle of its own row " +
                        $"(row spans {rowLeft:0}..{rowLeft + row.ActualWidth:0}) — it belongs at the row's " +
                        "FAR left, away from Run");
                }
            }
        });

        failures.Should().BeEmpty(
            "the irreversible action and the one people press repeatedly must stay apart:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }
}
