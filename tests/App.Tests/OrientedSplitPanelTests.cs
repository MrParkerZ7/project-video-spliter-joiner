using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// SPEC-015 app-shell-theming gaps (todo-automate) for <see cref="OrientedSplitPanel"/>. The panel is a
/// code-only <see cref="Grid"/> subclass, so it can be constructed + exercised on a dedicated STA thread
/// with NO Application / no XAML resource load — the axis flip (I16) and the clamped per-axis star sizing
/// + region-min + splitter thickness (I17) are checked directly off its definitions.
/// <para>
/// I18 (a splitter drag writing back ONLY the active axis's ratio) is NOT covered here: it requires a real
/// WPF layout pass (Measure/Arrange so <c>ActualWidth</c>/<c>ActualHeight</c> are non-zero) plus a simulated
/// GridSplitter drag, which cannot be done cheaply/robustly without a windowed harness — documented as a
/// follow-up. I23 (native WM_GETMINMAXINFO P/Invoke) and I24 (App.xaml.cs crash-handler wiring) need a src
/// refactor to be testable and are likewise out of scope.
/// </para>
/// </summary>
public sealed class OrientedSplitPanelTests
{
    /// <summary>Run <paramref name="body"/> on a dedicated STA thread; rethrow any assertion failure here.</summary>
    private static void RunSta(Action body)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { captured = ex; }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    private static OrientedSplitPanel BuiltPanel(out UIElement first, out UIElement second)
    {
        var panel = new OrientedSplitPanel();
        var a = new Border();
        var b = new Border();
        panel.FirstChild = a;   // setting both children builds the panel (horizontal default)
        panel.SecondChild = b;
        first = a;
        second = b;
        return panel;
    }

    // SPEC-015#I16 — OrientedSplitPanel flips axis from IsVertical: 3 ColumnDefinitions horizontal / 3
    // RowDefinitions vertical, the SAME child instances re-placed, and the splitter's ResizeDirection swaps.
    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public void AxisFlip_RebuildsDefinitions_ReplacesSameChildren_SwapsSplitterDirection()
    {
        RunSta(() =>
        {
            var panel = BuiltPanel(out var first, out var second);
            var splitter = panel.Children.OfType<GridSplitter>().Single();

            // Horizontal (default): 3 columns, no rows, splitter resizes Columns.
            panel.ColumnDefinitions.Count.Should().Be(3, "horizontal mode lays out 3 columns");
            panel.RowDefinitions.Count.Should().Be(0);
            splitter.ResizeDirection.Should().Be(GridResizeDirection.Columns);
            Grid.GetColumn(first).Should().Be(0);
            Grid.GetColumn(second).Should().Be(2);

            // Flip to vertical: 3 rows, no columns, splitter resizes Rows — SAME child instances re-placed.
            panel.IsVertical = true;
            panel.RowDefinitions.Count.Should().Be(3, "vertical mode lays out 3 rows");
            panel.ColumnDefinitions.Count.Should().Be(0);
            splitter.ResizeDirection.Should().Be(GridResizeDirection.Rows);
            Grid.GetRow(first).Should().Be(0);
            Grid.GetRow(second).Should().Be(2);

            panel.FirstChild.Should().BeSameAs(first, "children are re-placed, not re-created, across the flip");
            panel.SecondChild.Should().BeSameAs(second);
            panel.Children.OfType<GridSplitter>().Should().ContainSingle().Which.Should().BeSameAs(splitter);
        });
    }

    // SPEC-015#I17 — the active-axis ratio drives clamped star sizing (Math.Clamp(ratio, 0.05, 0.95),
    // second = 1 - first) with a RegionMinLength (80px) minimum and a 6px absolute splitter.
    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public void ActiveAxisRatio_IsClampedStarSizing_WithRegionMin_AndSixPxSplitter()
    {
        RunSta(() =>
        {
            var panel = BuiltPanel(out _, out _); // horizontal → the horizontal ratio is the active axis

            // In-range 0.3 → first 0.3*, second 0.7*.
            panel.HorizontalRatio = 0.3;
            panel.ColumnDefinitions[0].Width.GridUnitType.Should().Be(GridUnitType.Star);
            panel.ColumnDefinitions[0].Width.Value.Should().BeApproximately(0.3, 1e-9);
            panel.ColumnDefinitions[2].Width.Value.Should().BeApproximately(0.7, 1e-9, "second = 1 - first");
            panel.ColumnDefinitions[0].MinWidth.Should().Be(80, "each region keeps an 80px minimum");
            panel.ColumnDefinitions[2].MinWidth.Should().Be(80);
            panel.ColumnDefinitions[1].Width.Should().Be(new GridLength(6), "the splitter column is a 6px absolute band");

            // Over-range 5.0 → clamps to the 0.95 upper bound.
            panel.HorizontalRatio = 5.0;
            panel.ColumnDefinitions[0].Width.Value.Should().BeApproximately(0.95, 1e-9, "ratio clamps to the 0.95 upper bound");
            panel.ColumnDefinitions[2].Width.Value.Should().BeApproximately(0.05, 1e-9);

            // Negative -1.0 → clamps to the 0.05 lower bound.
            panel.HorizontalRatio = -1.0;
            panel.ColumnDefinitions[0].Width.Value.Should().BeApproximately(0.05, 1e-9, "ratio clamps to the 0.05 lower bound");
            panel.ColumnDefinitions[2].Width.Value.Should().BeApproximately(0.95, 1e-9);
        });
    }
}
