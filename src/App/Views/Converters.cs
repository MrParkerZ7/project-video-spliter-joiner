using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
/// Maps a bool to Visibility INVERTED (true → Collapsed, false → Visible). Used to show an
/// empty-state placeholder only while a bound "HasFile"/"HasClips" flag is false (T-059).
/// </summary>
public sealed class BoolToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not Visibility.Visible;
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

/// <summary>
/// Loads a temp jpg PATH (string) into a FROZEN <see cref="BitmapImage"/> for the scrub-bar hover
/// thumbnail (T-078). Uses <see cref="BitmapCacheOption.OnLoad"/> so the file is read fully at load
/// time and NOT kept locked (the service may delete/overwrite the temp file behind us), then
/// <see cref="System.Windows.Freezable.Freeze"/>s the result so it is safe to hand to the UI even
/// though it was decoded off a background-produced path. A null / empty / missing path yields null
/// (the bound <c>Image</c> simply shows nothing). Best-effort — any decode failure also yields null.
/// </summary>
public sealed class PathToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;      // read now → don't lock the temp file
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // a re-grabbed bucket path re-reads
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();                                    // cross-thread-safe, immutable
            return bitmap;
        }
        catch
        {
            // Best-effort — a mid-delete / corrupt frame shows nothing rather than crashing.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
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
///
/// <para>Dark+gold theme (T-054): colours are tuned for the dark surfaces — a low-alpha green/red
/// wash for the background and the readable <c>OkColor #3FB950</c> / <c>DangerColor #E5484D</c>
/// tokens (from <c>Themes/Tokens.xaml</c>) for text + border, so the green/red compatibility
/// semantics survive on a near-black panel.</para>
/// </summary>
public sealed class BoolToCompatBrushConverter : IValueConverter
{
    // Backgrounds: the Ok/Danger token colour at low alpha, so it reads as a subtle tint on dark.
    private static readonly SolidColorBrush GreenBackground = Freeze("#243FB950");
    private static readonly SolidColorBrush GreenForeground = Freeze("#FF3FB950"); // OkColor
    private static readonly SolidColorBrush GreenBorder = Freeze("#FF3FB950");     // OkColor
    private static readonly SolidColorBrush RedBackground = Freeze("#24E5484D");
    private static readonly SolidColorBrush RedForeground = Freeze("#FFE5484D");   // DangerColor
    private static readonly SolidColorBrush RedBorder = Freeze("#FFE5484D");       // DangerColor

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
