using System.Globalization;
using System.Windows;
using FluentAssertions;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// SPEC-015 app-shell-theming gaps (todo-automate): the two pure <see cref="System.Windows.Data.IValueConverter"/>
/// window-state converters. Both are plain value converters (no DispatcherObject / no resource load), so they
/// are trivially unit-testable off any thread. No test file referenced them before.
/// </summary>
public sealed class WindowStateConvertersTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    // SPEC-015#I21 — WindowStateToBorderThicknessConverter: Thickness(0) when Maximized, Thickness(1) otherwise.
    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public void BorderThickness_ZeroWhenMaximized_OneOtherwise()
    {
        var conv = new WindowStateToBorderThicknessConverter();

        conv.Convert(WindowState.Maximized, typeof(Thickness), null, Culture)
            .Should().Be(new Thickness(0), "the themed 1px frame collapses when maximized");
        conv.Convert(WindowState.Normal, typeof(Thickness), null, Culture)
            .Should().Be(new Thickness(1), "the 1px frame line shows in the Normal state");
        conv.Convert(WindowState.Minimized, typeof(Thickness), null, Culture)
            .Should().Be(new Thickness(1), "only the Maximized state collapses the border");
    }

    // SPEC-015#I22 — WindowStateToContentMarginConverter: the resize-border thickness when Maximized,
    // Thickness(0) otherwise.
    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public void ContentMargin_ZeroWhenNormal_ResizeBorderWhenMaximized()
    {
        var conv = new WindowStateToContentMarginConverter();

        conv.Convert(WindowState.Normal, typeof(Thickness), null, Culture)
            .Should().Be(new Thickness(0), "no inset in the Normal state");
        conv.Convert(WindowState.Minimized, typeof(Thickness), null, Culture)
            .Should().Be(new Thickness(0), "no inset while minimized");

        var t = SystemParameters.WindowResizeBorderThickness;
        conv.Convert(WindowState.Maximized, typeof(Thickness), null, Culture)
            .Should().Be(new Thickness(t.Left, t.Top, t.Right, t.Bottom),
                "content is inset by the resize-border thickness when maximized so it clears the invisible border");
    }
}
