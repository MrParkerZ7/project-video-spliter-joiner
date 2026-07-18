using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// T-056 — Maps <see cref="WindowState"/> to the Segoe MDL2 glyph shown on the
/// maximize/restore caption button: Maximized -> restore glyph, otherwise -> maximize glyph.
/// </summary>
public sealed class WindowStateToMaxRestoreGlyphConverter : IValueConverter
{
    // Segoe MDL2 Assets: maximize = E922, restore = E923.
    private const string MaximizeGlyph = "\uE922";
    private const string RestoreGlyph = "\uE923";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is WindowState.Maximized ? RestoreGlyph : MaximizeGlyph;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// T-056 — Root-content margin per <see cref="WindowState"/>. A WindowChrome window keeps its
/// (invisible) resize border when maximized, so the client area is inset by the resize-border
/// thickness; content must be nudged in by the same amount only while maximized, else clipped.
/// Normal/Minimized -> zero margin.
/// </summary>
public sealed class WindowStateToContentMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is WindowState.Maximized)
        {
            // WindowResizeBorderThickness keeps content clear of the invisible resize
            // border in the maximized state.
            var t = SystemParameters.WindowResizeBorderThickness;
            return new Thickness(t.Left, t.Top, t.Right, t.Bottom);
        }

        return new Thickness(0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// T-066 — Root-border thickness per <see cref="WindowState"/>. The themed 1px frame line is
/// shown only in the Normal state; in the Maximized state it would otherwise read as a stray
/// floating line inside the window, so it collapses to zero. Mirrors how
/// <see cref="WindowStateToContentMarginConverter"/> switches on the window state.
/// </summary>
public sealed class WindowStateToBorderThicknessConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is WindowState.Maximized ? new Thickness(0) : new Thickness(1);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
