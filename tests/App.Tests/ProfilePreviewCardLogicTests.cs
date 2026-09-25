using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-180 (G-058, SPEC-007 I101/I109) — the pure halves of the profile hover card: the picture box's size, and the
/// readout of where the profile cuts. Asserted exactly; <see cref="ProfileHoverPreviewTests"/> renders the card.
/// </summary>
public sealed class ProfilePreviewCardLogicTests
{
    private const double Min = 320;
    private const double Max = 640;

    // ---- the box size -------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(640, 1.0, 640)]
    [InlineData(480, 1.0, 480)]
    [InlineData(320, 1.0, 320)]
    [InlineData(1920, 1.0, 640)] // capped
    [InlineData(64, 1.0, 320)]   // a small picture still fills the minimum box
    [InlineData(640, 2.0, 320)]  // one picture pixel per SCREEN pixel: half the DIPs at 200%
    [InlineData(320, 2.0, 320)]  // never below the minimum
    [InlineData(1280, 2.0, 640)]
    [InlineData(319, 1.0, 320)]
    [InlineData(321, 1.0, 321)]
    [InlineData(639, 1.0, 639)]
    [InlineData(641, 1.0, 640)]
    public void TheBoxIsThePicturesPixelWidthOverTheScale_ClampedToTheMinimumAndTheCap(int pixels, double scale, double expected)
    {
        ProfilePreviewBox.Width(pixels, scale, Min, Max).Should().Be(expected);
    }

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(640, 360)]
    [InlineData(321, 180.5625)]
    [InlineData(360, 202.5)]
    [InlineData(320, 180)]
    public void TheBoxIsAlways16By9(double width, double expectedHeight)
    {
        ProfilePreviewBox.Height(width).Should().Be(expectedHeight);
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void NoPicture_IsTheMinimumWidth()
    {
        ProfilePreviewBox.Width(null, 1.0, Min, Max).Should().Be(Min);
        ProfilePreviewBox.Width(0, 1.0, Min, Max).Should().Be(Min);
    }

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnreadableScale_CountsAs1(double scale)
    {
        ProfilePreviewBox.NormalizeScale(scale).Should().Be(1.0);
        ProfilePreviewBox.Width(480, scale, Min, Max).Should().Be(480, "480 pixels at the fallback scale of 1.0");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheDefaultDisplayScale_IsOneWhenThereIsNoVisual()
    {
        ProfilePreviewBoxSizeConverter.DefaultDisplayScale(null).Should().Be(1.0);
    }

    /// <summary>
    /// The default reads the visual's own DPI. Both sides read the same system DPI, so this holds on any machine —
    /// and it fails if the default ever stops asking (e.g. returns 1.0 outright).
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheDefaultDisplayScale_IsTheVisualsDpiScale()
    {
        double read = 0, expected = -1;
        StaViewHarness.OnSta(() =>
        {
            var element = new Border();
            read = ProfilePreviewBoxSizeConverter.DefaultDisplayScale(element);
            expected = ProfilePreviewBox.NormalizeScale(VisualTreeHelper.GetDpi(element).DpiScaleX);
        });

        read.Should().Be(expected);
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheConverter_ReadsPixelsNotDpiSize_AndAnswersWidthOrHeight()
    {
        // The seam is process-wide and the render tests change it on the shared STA worker, so this test changes
        // it THERE too: xUnit runs test classes in parallel, and a change made from a pool thread could land in
        // the middle of another class's render (found in the T-180 review).
        StaViewHarness.OnSta(() =>
        {
            var saved = ProfilePreviewBoxSizeConverter.DisplayScale;
            try
            {
                ProfilePreviewBoxSizeConverter.DisplayScale = _ => 1.0;
                var converter = new ProfilePreviewBoxSizeConverter();

                // 480 real pixels tagged 192 DPI: WPF's DPI-scaled Width is 240, the box must be 480.
                var picture = Bitmap(480, 270, 192);
                picture.Width.Should().Be(240, "precondition: WPF sizes this bitmap by its DPI metadata");

                var values = new object[] { picture, null!, Min, Max };
                converter.Convert(values, typeof(double), "width", CultureInfo.InvariantCulture).Should().Be(480d);
                converter.Convert(values, typeof(double), "height", CultureInfo.InvariantCulture).Should().Be(270d);

                var none = new object[] { null!, null!, Min, Max };
                converter.Convert(none, typeof(double), "width", CultureInfo.InvariantCulture).Should().Be(Min);

                var malformed = new object[] { picture, null!, "320", Max };
                converter.Convert(malformed, typeof(double), "width", CultureInfo.InvariantCulture)
                    .Should().Be(DependencyProperty.UnsetValue, "a width that is not a number is not guessed");
            }
            finally
            {
                ProfilePreviewBoxSizeConverter.DisplayScale = saved;
            }
        });
    }

    // ---- the readout ------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(32, "cuts at 00:32.0")]
    [InlineData(0, "none — keeps from the start")]
    [InlineData(0.04, "none — keeps from the start")]  // shows as 00:00.0, so it is not worded as a cut
    [InlineData(0.1, "cuts at 00:00.1")]
    [InlineData(3723.4, "cuts at 01:02:03.4")]
    public void TheIntroLine_SaysWhereTheIntroIsCut(double seconds, string expected)
    {
        ProfileCutReadout.Intro(Exactly(seconds)).ToString().Should().Be(expected);
    }

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(90.0, "cuts 01:30.0 before the end")]
    [InlineData(0.0, "none — keeps to the end")]  // an outro of 0 trims nothing, so it reads "none"
    [InlineData(0.04, "none — keeps to the end")]  // one frame shows as 00:00.0: never "cuts 00:00.0 before the end"
    [InlineData(0.1, "cuts 00:00.1 before the end")]
    [InlineData(null, "none — keeps to the end")]
    public void TheOutroLine_IsMeasuredFromTheEnd_AndZeroIsNone(double? seconds, string expected)
    {
        var outro = seconds is { } s ? Exactly(s) : (TimeSpan?)null;
        ProfileCutReadout.Outro(outro).ToString().Should().Be(expected);
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ATimeIsItsOwnPart_SoTheCardCanDrawItApart()
    {
        var intro = ProfileCutReadout.Intro(TimeSpan.FromSeconds(32));
        intro.Prefix.Should().Be("cuts at ");
        intro.Time.Should().Be("00:32.0");
        intro.Suffix.Should().BeEmpty();

        var outro = ProfileCutReadout.Outro(TimeSpan.FromSeconds(90));
        outro.Prefix.Should().Be("cuts ");
        outro.Time.Should().Be("01:30.0");
        outro.Suffix.Should().Be(" before the end");

        ProfileCutReadout.Outro(null).Time.Should().BeNull("'none' has no time to highlight");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData("intro-prefix", "cuts at ")]
    [InlineData("intro-time", "00:32.0")]
    [InlineData("intro-suffix", "")]
    [InlineData("nonsense", "")]
    public void TheReadoutConverter_ReturnsThePartItIsAskedFor_ForTheIntro(string part, string expected)
    {
        new ProfileCutReadoutConverter()
            .Convert(TimeSpan.FromSeconds(32), typeof(string), part, CultureInfo.InvariantCulture)
            .Should().Be(expected);
    }

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData("outro-prefix", "none — keeps to the end")]
    [InlineData("outro-time", "")]
    [InlineData("outro-suffix", "")]
    public void TheReadoutConverter_ReadsANullOutroAsNone(string part, string expected)
    {
        new ProfileCutReadoutConverter()
            .Convert(null, typeof(string), part, CultureInfo.InvariantCulture)
            .Should().Be(expected);
    }

    /// <summary>Seconds to a TimeSpan rounded to the tick, so 3723.4 s is never 3723.3999999 s (which formats as .3).</summary>
    private static TimeSpan Exactly(double seconds) =>
        TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    private static BitmapSource Bitmap(int width, int height, double dpi)
    {
        var stride = width * 4;
        var bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, new byte[stride * height], stride);
        bitmap.Freeze();
        return bitmap;
    }
}
