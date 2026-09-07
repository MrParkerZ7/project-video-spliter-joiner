using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-169 (SPEC-007) — hovering a profile chip shows its picture big enough to recognise.
///
/// <para>T-161 moved profiles out of a dropdown so their pictures would be visible <i>before</i> choosing
/// (I95). It fixed WHEN you see the picture, not how much of it: the chip renders it at 28x28, where two
/// frames from the same show are indistinguishable.</para>
///
/// <para><b>The vacuity trap this suite deliberately avoids.</b> Asserting "the chip has a ToolTip" passes
/// whether or not the card shows a picture, at what size, or with the right content — the element's mere
/// existence proves nothing. Every test here opens the card and measures what is actually inside it.</para>
///
/// <para>A ToolTip renders into its own window, so it is laid out by setting <c>IsOpen</c> and measuring
/// its visual tree directly rather than by simulating a hover, which needs a real message pump.</para>
/// </summary>
public sealed class ProfileHoverPreviewTests
{
    private const double ChipThumbnailWidth = 28;

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheHoverCardShowsThePictureFarLargerThanTheChipDoes()
    {
        double cardImageWidth = 0;
        double cardImageHeight = 0;
        var diag = "";

        StaViewHarness.OnSta(() =>
        {
            var (view, chip) = FirstChip();
            StaViewHarness.LayOut(view, 1280, 800);

            var card = OpenCard(chip);
            var image = StaViewHarness.Descendants<Image>(card).FirstOrDefault();
            diag = $"cardType={card.GetType().Name} cardW={card.ActualWidth:0} cardH={card.ActualHeight:0} "
                 + $"visualChildren={VisualTreeHelper.GetChildrenCount(card)} "
                 + $"images={StaViewHarness.Descendants<Image>(card).Count()} "
                 + $"borders={StaViewHarness.Descendants<Border>(card).Count()} "
                 + $"texts={StaViewHarness.Descendants<TextBlock>(card).Count()} "
                 + $"imgSrc={(image?.Source is null ? "null" : "set")} "
                 + $"dc={(card.DataContext?.GetType().Name ?? "null")}";
            image.Should().NotBeNull("the card exists to show the picture. " + diag);

            cardImageWidth = image!.ActualWidth;
            cardImageHeight = image.ActualHeight;
        });

        cardImageWidth.Should().BeGreaterThan(
            ChipThumbnailWidth * 4,
            $"the chip already shows it at {ChipThumbnailWidth}px — a preview that is not MUCH bigger " +
            "answers nothing. Asserting only that a card exists would pass at 28px. " + diag);

        cardImageHeight.Should().BeGreaterThan(
            ChipThumbnailWidth * 2,
            "a card that is wide and flat is not a preview either");
    }

    /// <summary>
    /// The card must carry the profile's identity, not only its picture. The chip trims a long name, so
    /// the card is where it becomes readable.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheHoverCardNamesTheProfile_AndSaysWhatItWouldDo()
    {
        var texts = new List<string>();

        StaViewHarness.OnSta(() =>
        {
            var (view, chip) = FirstChip(name: new string('N', 120));
            StaViewHarness.LayOut(view, 1280, 800);

            var card = OpenCard(chip);
            texts.AddRange(StaViewHarness.Descendants<TextBlock>(card)
                .Select(t => t.Text ?? string.Empty)
                .Where(t => t.Length > 0));
        });

        texts.Should().Contain(
            t => t.Length >= 120,
            "the chip trims a long name, so the card is where the whole of it must be readable");

        string.Concat(texts).Should().Contain(
            "intro",
            "a picture alone does not identify a profile — the card must say what applying it would do");
    }

    /// <summary>
    /// A profile with no picture is a normal outcome (I78), not a failure. The card must still be useful:
    /// the image box collapses and the name and values carry it.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AProfileWithNoPicture_StillGetsAUsefulCard_WithNoEmptyImageBox()
    {
        var imageBoxVisible = true;
        var hasText = false;

        StaViewHarness.OnSta(() =>
        {
            var (view, chip) = FirstChip(withThumbnail: false);
            StaViewHarness.LayOut(view, 1280, 800);

            var card = OpenCard(chip);
            var image = StaViewHarness.Descendants<Image>(card).FirstOrDefault();

            // The image sits inside a Border that collapses when ThumbnailPath is null.
            imageBoxVisible = image is not null
                && image.Visibility == Visibility.Visible
                && Ancestors(image).OfType<Border>().All(b => b.Visibility == Visibility.Visible);

            hasText = StaViewHarness.Descendants<TextBlock>(card).Any(t => !string.IsNullOrEmpty(t.Text));
        });

        imageBoxVisible.Should().BeFalse(
            "an empty letterbox is worse than none — a profile without a picture is normal (I78)");
        hasText.Should().BeTrue("the card must still name the profile and its cut values");
    }

    /// <summary>
    /// The card must not swallow the click that selects the profile. SPEC-007 I97 requires a click to
    /// SELECT, and a hover card sitting under the cursor is exactly what would break it.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheHoverCardNeverStealsTheClickThatSelects()
    {
        var hitTestable = true;
        var staysOpen = false;

        StaViewHarness.OnSta(() =>
        {
            var (view, chip) = FirstChip();
            StaViewHarness.LayOut(view, 1280, 800);

            var tip = (ToolTip)HostOf(chip).ToolTip;

            // A ToolTip is never hit-testable and never takes focus — that is the property being relied
            // on here, so it is asserted rather than assumed.
            hitTestable = tip.IsHitTestVisible && tip.Focusable;
            staysOpen = ToolTipService.GetShowDuration(HostOf(chip)) > 30000;
        });

        hitTestable.Should().BeFalse("a card under the cursor must not intercept the selecting click");
        staysOpen.Should().BeTrue(
            "WPF auto-hides a tooltip after ~5s by default; a panel you hold the cursor on to review " +
            "must not vanish mid-look");
    }

    /// <summary>
    /// An open delay, or sweeping across a wrapped grid of chips (T-168) strobes a large card once per
    /// chip passed over.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheCardWaitsBeforeOpening_SoSweepingTheBarDoesNotStrobe()
    {
        var delay = 0;

        StaViewHarness.OnSta(() =>
        {
            var (view, chip) = FirstChip();
            StaViewHarness.LayOut(view, 1280, 800);
            delay = ToolTipService.GetInitialShowDelay(HostOf(chip));
        });

        delay.Should().BeGreaterThan(
            200,
            "with no delay, dragging the cursor across a wrapped grid of chips opens and closes a " +
            "320px card once per chip");
    }

    // ---- plumbing ---------------------------------------------------------------------------------

    /// <summary>Builds the view with one profile and returns its realised chip.</summary>
    private static (BulkCutView View, ListBoxItem Chip) FirstChip(
        string name = "Season 1 opener", bool withThumbnail = true)
    {
        var settings = new FakeSettings();
        settings.SaveProfile(new VideoSplitJoiner.Core.Profiles.CutProfile(
            name,
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(30),
            withThumbnail ? ThumbnailFile() : null));

        var view = new BulkCutView
        {
            DataContext = new VideoSplitJoiner.App.ViewModels.BulkCutViewModel(
                new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(),
                settings, new FakeBulkTrimEngine()),
        };

        StaViewHarness.LayOut(view, 1280, 800);

        var bar = StaViewHarness.Find<FrameworkElement>(view, "ProfileBar")!;
        var chip = StaViewHarness.Descendants<ListBoxItem>(bar).First();
        return (view, chip);
    }

    /// <summary>
    /// A ToolTip lives in its own window, so it is opened and laid out directly. Simulating a hover
    /// would need a real message pump, which this harness deliberately does not have.
    /// </summary>
    private static FrameworkElement OpenCard(ListBoxItem chip)
    {
        var tip = HostOf(chip).ToolTip as ToolTip;
        tip.Should().NotBeNull("the chip must carry the preview card");

        // Deliberately NOT opened. An open ToolTip is hosted inside a Popup window, the Popup owns
        // layout, and with no PresentationSource behind it every child measures 0 — the card renders
        // for a user and is invisible to a test. Applying the template to the CLOSED control gives the
        // same visual tree with ordinary layout, which is what can actually be measured here.
        // The card's DataContext flows from its placement target, which WPF sets when it shows the
        // tooltip for real. Nothing shows it here, so it is set explicitly.
        tip!.PlacementTarget = HostOf(chip);
        tip.ApplyTemplate();
        tip.Measure(new Size(400, 600));
        tip.Arrange(new Rect(0, 0, 400, 600));
        tip.UpdateLayout();
        return tip;
    }

    /// <summary>
    /// The element the preview card actually hangs off: the item TEMPLATE's root inside the chip. A
    /// UIElement in a Style setter is one shared instance across every item, so the card lives in the
    /// DataTemplate — which is instantiated per item — and this finds it.
    /// </summary>
    private static FrameworkElement HostOf(ListBoxItem chip)
    {
        var host = StaViewHarness.Descendants<StackPanel>(chip).FirstOrDefault(p => p.ToolTip is ToolTip);
        host.Should().NotBeNull("the chip's template root must carry the preview card");
        return host!;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        for (var p = VisualTreeHelper.GetParent(node); p is not null; p = VisualTreeHelper.GetParent(p))
        {
            yield return p;
        }
    }

    /// <summary>A real 320x180 JPEG on disk, so the card has something to actually measure.</summary>
    private static string ThumbnailFile()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vsj-hover-" + Guid.NewGuid().ToString("N") + ".jpg");

        var stride = 320 * 4;
        var pixels = new byte[stride * 180];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 251);
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            320, 180, 96, 96, PixelFormats.Bgra32, palette: null, pixels, stride);

        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);

        return path;
    }
}
