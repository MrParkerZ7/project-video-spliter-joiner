using System.Windows;
using System.Windows.Media;
using FluentAssertions;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-076 — The caption min / maximize / restore / close buttons now draw crisp vector
/// <c>Path</c> icons instead of Segoe MDL2 font glyphs. The geometries live as resources in
/// <c>Themes/Controls.xaml</c> (<c>MinimizeCaptionGeometry</c>, <c>MaximizeCaptionGeometry</c>,
/// <c>RestoreCaptionGeometry</c>, <c>CloseCaptionGeometry</c>). These tests parse the same path
/// data to guard against malformed geometry (typos in the mini-language) and to pin the
/// pixel-aligned 10×10 icon field the design calls for. Pure geometry math — no STA / no
/// ResourceDictionary load required.
/// </summary>
public sealed class CaptionIconGeometryTests
{
    // Keep these strings in lock-step with the *CaptionGeometry resources in Controls.xaml.
    private const string Minimize = "M0.5,5.5 L10.5,5.5";
    private const string Restore = "M2.5,2.5 L2.5,0.5 L10.5,0.5 L10.5,8.5 L8.5,8.5 M0.5,2.5 L8.5,2.5 L8.5,10.5 L0.5,10.5 Z";
    private const string Close = "M0.5,0.5 L10.5,10.5 M10.5,0.5 L0.5,10.5";

    // Maximize is authored as a RectangleGeometry Rect="0.5,0.5,10,10" in XAML.
    private static readonly Rect MaximizeRect = new(0.5, 0.5, 10, 10);

    // T-086 layout-toggle glyphs: each is a GeometryGroup of two RectangleGeometry panes on the same
    // 10x10 .5-aligned field (LayoutVerticalCaptionGeometry / LayoutHorizontalCaptionGeometry in
    // Controls.xaml). Kept in lock-step with those resources, like the path strings above.
    private static readonly Rect[] LayoutVerticalPanes = { new(0.5, 0.5, 10, 4), new(0.5, 6.5, 10, 4) };
    private static readonly Rect[] LayoutHorizontalPanes = { new(0.5, 0.5, 4, 10), new(6.5, 0.5, 4, 10) };

    [Theory]
    [InlineData(Minimize)]
    [InlineData(Restore)]
    [InlineData(Close)]
    public void Path_geometry_parses_to_a_non_empty_geometry(string data)
    {
        var geometry = Geometry.Parse(data);

        geometry.Should().NotBeNull();
        geometry.IsEmpty().Should().BeFalse();
    }

    [Fact]
    public void All_icons_fit_within_the_10x10_pixel_aligned_field()
    {
        // Every icon (including the 1px-stroke-straddling .5 offsets) stays inside a 0..11 box.
        var box = new Rect(0, 0, 11, 11);

        foreach (var data in new[] { Minimize, Restore, Close })
        {
            var bounds = Geometry.Parse(data).Bounds;
            box.Contains(bounds).Should().BeTrue($"'{data}' should fit the caption icon field");
        }

        box.Contains(MaximizeRect).Should().BeTrue();

        // The layout-toggle panes share the very same field.
        foreach (var pane in new[]
                 {
                     LayoutVerticalPanes[0], LayoutVerticalPanes[1],
                     LayoutHorizontalPanes[0], LayoutHorizontalPanes[1],
                 })
        {
            box.Contains(pane).Should().BeTrue($"pane {pane} should fit the caption icon field");
        }
    }

    [Fact]
    public void Minimize_is_a_single_horizontal_line()
    {
        var bounds = Geometry.Parse(Minimize).Bounds;

        bounds.Height.Should().Be(0, "the minimize icon is a horizontal line");
        bounds.Width.Should().Be(10);
    }

    [Fact]
    public void Maximize_is_a_10_by_10_square_outline()
    {
        MaximizeRect.Width.Should().Be(10);
        MaximizeRect.Height.Should().Be(10);
    }

    // SPEC-015#I20 (the layout-toggle clause) — the min/max/restore/close glyphs are pinned above;
    // the two layout-toggle glyphs are the remaining members of the same icon family and must read
    // as TWO-PANE SPLIT RECTANGLES (D5: the icon shows the mode the click switches TO), not as a
    // single box: two panes, full-width stacked for vertical, full-height side-by-side for
    // horizontal, separated by the 2px gap that makes the split legible at 10x10.
    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public void Layout_toggle_glyphs_are_two_pane_split_rectangles()
    {
        LayoutVerticalPanes.Should().HaveCount(2, "the vertical-layout glyph is a two-pane split");
        LayoutVerticalPanes.Should().OnlyContain(r => r.Width == 10,
            "vertical mode = two full-width STACKED panes");
        (LayoutVerticalPanes[1].Top - LayoutVerticalPanes[0].Bottom).Should().Be(2,
            "a 2px gap keeps the stacked panes readable as two panes");

        LayoutHorizontalPanes.Should().HaveCount(2, "the horizontal-layout glyph is a two-pane split");
        LayoutHorizontalPanes.Should().OnlyContain(r => r.Height == 10,
            "horizontal mode = two full-height SIDE-BY-SIDE panes");
        (LayoutHorizontalPanes[1].Left - LayoutHorizontalPanes[0].Right).Should().Be(2,
            "a 2px gap keeps the side-by-side panes readable as two panes");
    }
}
