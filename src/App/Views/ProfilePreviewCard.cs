using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoSplitJoiner.App.ViewModels;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// T-180 (G-058) — the size of the profile hover card's picture box. The box follows the picture's own width
/// so a picture shows one picture pixel per screen pixel, between a minimum and a cap, and is always 16:9.
///
/// <para>Pure, so it is tested exactly without rendering. The card binds it through
/// <see cref="ProfilePreviewBoxSizeConverter"/>.</para>
/// </summary>
public static class ProfilePreviewBox
{
    /// <summary>The box is 16:9 at every width — every capture is a frame of a 16:9 video.</summary>
    public const double AspectHeightOverWidth = 9d / 16d;

    /// <summary>
    /// The box width in DIPs: <c>clamp(pixelWidth / scale, min, max)</c>, or <paramref name="min"/> when there is no
    /// picture. An unreadable <paramref name="scale"/> (NaN, infinite, zero or negative) counts as 1.0.
    /// </summary>
    public static double Width(int? pixelWidth, double scale, double min, double max)
    {
        if (pixelWidth is not { } pixels || pixels <= 0)
        {
            return min;
        }

        var dips = pixels / NormalizeScale(scale);
        return Math.Clamp(dips, min, Math.Max(min, max));
    }

    /// <summary>The box height for a box <paramref name="width"/> DIPs wide.</summary>
    public static double Height(double width) => width * AspectHeightOverWidth;

    /// <summary>A usable display scale: the value itself when it is a positive finite number, else 1.0.</summary>
    public static double NormalizeScale(double scale) =>
        double.IsFinite(scale) && scale > 0 ? scale : 1.0;
}

/// <summary>
/// Sizes the hover card's picture box and its text column from the loaded picture. Inputs, in order: the card
/// image's <c>Source</c>, the element being sized (for the display scale), the minimum width and the cap.
/// <c>ConverterParameter</c> is <c>width</c> or <c>height</c>.
///
/// <para>The picture's <see cref="BitmapSource.PixelWidth"/> is used, never its DPI-derived <c>Width</c>, so a
/// file tagged 192 DPI is not drawn at half its size (SPEC-007 I109).</para>
/// </summary>
public sealed class ProfilePreviewBoxSizeConverter : IMultiValueConverter
{
    /// <summary>
    /// The horizontal display scale of a visual. The app is not per-monitor DPI aware, so this is the system scale,
    /// fixed for the life of the process. A seam so render tests pin it; each test restores it in a <c>finally</c>.
    /// </summary>
    internal static Func<Visual?, double> DisplayScale = DefaultDisplayScale;

    /// <summary>The default seam: <c>VisualTreeHelper.GetDpi(v).DpiScaleX</c>, or 1.0 when it cannot be read.</summary>
    internal static double DefaultDisplayScale(Visual? visual)
    {
        if (visual is null)
        {
            return 1.0;
        }

        try
        {
            return ProfilePreviewBox.NormalizeScale(VisualTreeHelper.GetDpi(visual).DpiScaleX);
        }
        catch
        {
            return 1.0;
        }
    }

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not { Length: 4 } || values[2] is not double min || values[3] is not double max)
        {
            return DependencyProperty.UnsetValue;
        }

        int? pixelWidth = values[0] is BitmapSource picture ? picture.PixelWidth : null;
        var width = ProfilePreviewBox.Width(pixelWidth, DisplayScale(values[1] as Visual), min, max);

        return string.Equals(parameter as string, "height", StringComparison.OrdinalIgnoreCase)
            ? ProfilePreviewBox.Height(width)
            : width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// T-180 (G-058) — where a profile cuts, in words: the intro cut, and the outro cut measured from the end, or that
/// there is none. Each line is a prefix, an optional time and a suffix, so the card can draw the time apart.
/// </summary>
public static class ProfileCutReadout
{
    /// <summary>One readout line: the words before the time, the time (null when there is none), and the words after it.</summary>
    public readonly record struct Line(string Prefix, string? Time, string Suffix)
    {
        /// <summary>The whole line as one string.</summary>
        public override string ToString() => Prefix + (Time ?? string.Empty) + Suffix;
    }

    /// <summary>
    /// The intro line: <c>cuts at 00:32.0</c>, or <c>none — keeps from the start</c> for an intro that shows as
    /// <c>00:00.0</c> (see <see cref="ShowsAsACut"/>).
    /// </summary>
    public static Line Intro(TimeSpan introFromStart) =>
        ShowsAsACut(introFromStart)
            ? new Line("cuts at ", CutMarkerViewModel.FormatClock(introFromStart), string.Empty)
            : new Line("none — keeps from the start", null, string.Empty);

    /// <summary>
    /// The outro line: <c>cuts 01:30.0 before the end</c>, or <c>none — keeps to the end</c> when there is no outro or
    /// it shows as <c>00:00.0</c> (applying an outro of 0 puts it at the file's end, which trims nothing).
    /// </summary>
    public static Line Outro(TimeSpan? outroFromEnd) =>
        outroFromEnd is { } outro && ShowsAsACut(outro)
            ? new Line("cuts ", CutMarkerViewModel.FormatClock(outro), " before the end")
            : new Line("none — keeps to the end", null, string.Empty);

    /// <summary>
    /// A time is worded as a cut only when it DISPLAYS as more than zero. <c>FormatClock</c> shows tenths, so a
    /// one-frame outro (~0.04 s, what setting the outro on the last frame saves) would otherwise read
    /// "cuts 00:00.0 before the end" in gold — a cut of nothing, contradicting itself (found in the T-180 review).
    /// </summary>
    private static bool ShowsAsACut(TimeSpan time) =>
        time > TimeSpan.Zero
        && CutMarkerViewModel.FormatClock(time) != CutMarkerViewModel.FormatClock(TimeSpan.Zero);
}

/// <summary>
/// Binds one part of a <see cref="ProfileCutReadout"/> line to a <c>Run</c>. The bound value is the profile's
/// <c>IntroFromStart</c> (for <c>intro-*</c>) or <c>OutroFromEnd</c> (for <c>outro-*</c>); <c>ConverterParameter</c>
/// is <c>intro-prefix | intro-time | intro-suffix | outro-prefix | outro-time | outro-suffix</c>.
/// </summary>
public sealed class ProfileCutReadoutConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var part = parameter as string ?? string.Empty;
        ProfileCutReadout.Line? line = part.StartsWith("intro-", StringComparison.Ordinal)
            ? value is TimeSpan intro ? ProfileCutReadout.Intro(intro) : null
            : part.StartsWith("outro-", StringComparison.Ordinal)
                ? ProfileCutReadout.Outro(value as TimeSpan?)
                : null;

        if (line is not { } l)
        {
            return string.Empty;
        }

        return part[(part.IndexOf('-', StringComparison.Ordinal) + 1)..] switch
        {
            "prefix" => l.Prefix,
            "time" => l.Time ?? string.Empty,
            "suffix" => l.Suffix,
            _ => string.Empty,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
