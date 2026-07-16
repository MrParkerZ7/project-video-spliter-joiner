using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using VideoSplitJoiner.Core.Detect;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Collapses an element when its bound value is null (or an empty string); visible otherwise.
/// Used to show status/error/warning/results areas only when they carry content.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasContent = value is not null && !(value is string s && string.IsNullOrWhiteSpace(s));
        return hasContent ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a bool to Visibility (true → Visible, false → Collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>
/// Two-way maps a <see cref="TimeSpan"/> to its total seconds (double) — used so a
/// <c>Slider</c> whose Value is a double can bind against the player's TimeSpan
/// <c>Position</c>/<c>Duration</c> directly.
/// </summary>
public sealed class TimeSpanToSecondsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TimeSpan t ? t.TotalSeconds : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? TimeSpan.FromSeconds(d) : TimeSpan.Zero;
}

/// <summary>Scales a 0..1 progress fraction to a 0..100 percentage for a <c>ProgressBar</c>.</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? Math.Clamp(d, 0d, 1d) * 100d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? d / 100d : 0d;
}

/// <summary>
/// Maps a compatibility bool to a banner background/foreground brush pair via the converter
/// parameter: <c>bg</c> → green when true, red when false; <c>fg</c> → the matching text colour.
/// Used by the Join screen's compat banner so a compatible verdict reads green and a refusal red.
/// </summary>
public sealed class BoolToCompatBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBackground = Freeze("#E6F4EA");
    private static readonly SolidColorBrush GreenForeground = Freeze("#1E7E34");
    private static readonly SolidColorBrush GreenBorder = Freeze("#7CC28A");
    private static readonly SolidColorBrush RedBackground = Freeze("#FDECEA");
    private static readonly SolidColorBrush RedForeground = Freeze("#B71C1C");
    private static readonly SolidColorBrush RedBorder = Freeze("#E57373");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var compatible = value is true;
        var role = (parameter as string)?.ToLowerInvariant() ?? "bg";
        return role switch
        {
            "fg" => compatible ? GreenForeground : RedForeground,
            "border" => compatible ? GreenBorder : RedBorder,
            _ => compatible ? GreenBackground : RedBackground,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Maps a detected candidate <see cref="CandidateKind"/> to its timeline tick colour (T-014):
/// Black → grey, White → light blue, Scene → orange. Used by the timeline legend; the track ticks
/// themselves are drawn in code-behind against the same palette (<c>TimelineView.BrushFor</c>).
/// </summary>
public sealed class CandidateKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CandidateKind kind ? Views.TimelineView.BrushFor(kind) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
