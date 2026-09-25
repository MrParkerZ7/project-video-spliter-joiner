using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
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
/// <para><b>T-180 (G-058)</b> — the card shows the picture at its own size, one picture pixel per screen pixel,
/// from a 320-DIP minimum to a 640-DIP cap in a 16:9 box, and states in words where the profile cuts.</para>
///
/// <para><b>The vacuity trap this suite deliberately avoids.</b> Asserting "the chip has a ToolTip" passes
/// whether or not the card shows a picture, at what size, or with the right content — the element's mere
/// existence proves nothing. Every test here opens the card and measures what is actually inside it.</para>
///
/// <para>A ToolTip renders into its own window, so it is laid out by applying its template and measuring
/// its visual tree directly rather than by simulating a hover, which needs a real message pump.</para>
///
/// <para><b>Every render test pins the display scale</b> (<see cref="ProfilePreviewBoxSizeConverter.DisplayScale"/>)
/// inside its STA body and restores it in a <c>finally</c>, so no result depends on the DPI of the machine
/// running the suite and a 2.0 cannot leak into another render. All card renders live in this one class.</para>
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

        StaViewHarness.OnSta(() => AtScale(1.0, () =>
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
        }));

        cardImageWidth.Should().BeGreaterThan(
            ChipThumbnailWidth * 4,
            $"the chip already shows it at {ChipThumbnailWidth}px — a preview that is not MUCH bigger " +
            "answers nothing. Asserting only that a card exists would pass at 28px. " + diag);

        cardImageHeight.Should().BeGreaterThan(
            ChipThumbnailWidth * 2,
            "a card that is wide and flat is not a preview either");
    }

    /// <summary>
    /// The card must carry the profile's identity, not only its picture. The chip trims a long name, so the card is
    /// where it becomes readable — and it states where the profile cuts, in words (T-180; was a small muted
    /// "intro … · outro to end" line, which never said that no outro is cut).
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheHoverCardNamesTheProfile_AndSaysWhereItCuts()
    {
        var texts = new List<string>();
        Readout? readout = null;

        StaViewHarness.OnSta(() => AtScale(1.0, () =>
        {
            var (view, chip) = FirstChip(name: new string('N', 120), introSeconds: 32, outroSeconds: null);
            StaViewHarness.LayOut(view, 1280, 800);

            var card = OpenCard(chip);
            texts.AddRange(StaViewHarness.Descendants<TextBlock>(card)
                .Select(t => t.Text ?? string.Empty)
                .Where(t => t.Length > 0));
            readout = ReadReadout(card);
        }));

        texts.Should().Contain(
            t => t.Length >= 120,
            "the chip trims a long name, so the card is where the whole of it must be readable");

        readout!.IntroLabel.Should().Be("Intro");
        readout.IntroValue.Should().Be("cuts at 00:32.0");
        readout.OutroLabel.Should().Be("Outro");
        readout.OutroValue.Should().Be(
            "none — keeps to the end",
            "an intro-only profile keeps the whole tail, and the card says so rather than 'outro to end'");
    }

    /// <summary>T-180 — the readout, rendered, for the three shapes of profile the user actually has or can make.</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(32.0, null, "cuts at 00:32.0", "none — keeps to the end")]
    [InlineData(32.0, 90.0, "cuts at 00:32.0", "cuts 01:30.0 before the end")]
    [InlineData(0.0, 90.0, "none — keeps from the start", "cuts 01:30.0 before the end")]
    public void TheReadoutIsRendered_ForEachShapeOfProfile(double intro, double? outro, string introValue, string outroValue)
    {
        Readout? readout = null;

        StaViewHarness.OnSta(() => AtScale(1.0, () =>
        {
            var (view, chip) = FirstChip(introSeconds: intro, outroSeconds: outro);
            StaViewHarness.LayOut(view, 1280, 800);
            readout = ReadReadout(OpenCard(chip));
        }));

        readout!.IntroValue.Should().Be(introValue);
        readout.OutroValue.Should().Be(outroValue);
    }

    /// <summary>
    /// T-180 — the readout is body text, not a footnote. A time is set apart by colour role AND weight, never a tint
    /// alone: gold <c>AccentBrush</c>, SemiBold. The values themselves are asserted, not resource references, and
    /// both the times and the words must be READABLE on the card: at least 4.5:1 against its background. (The spec
    /// first named <c>AccentTextBrush</c> — the dark text colour for text ON gold — which rendered the times dark
    /// on the dark card, near-invisible; the render review caught it.) The low-resolution note keeps its small,
    /// muted style: the rule covers the readout only.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheReadoutIsBodySize_TimesStandOut_AndNothingInItIsMuted()
    {
        var runs = new List<(string Role, double Size, Color Foreground, FontWeight Weight)>();
        Color accent = default, secondary = default, muted = default, cardBackground = default;
        double body = 0, small = 0, noteSize = 0;
        Color noteForeground = default;

        StaViewHarness.OnSta(() => AtScale(1.0, () =>
        {
            var (view, chip) = FirstChip(thumbnailPath: ThumbnailFile(64, 36), introSeconds: 32, outroSeconds: 90);
            StaViewHarness.LayOut(view, 1280, 800);
            var card = OpenCard(chip);

            accent = BrushColor(view, "AccentBrush");
            cardBackground = BrushColor(view, "Surface0Brush");
            secondary = BrushColor(view, "TextSecondaryBrush");
            muted = BrushColor(view, "TextMutedBrush");
            body = (double)view.FindResource("FontSizeBody");
            small = (double)view.FindResource("FontSizeSmall");

            foreach (var name in new[] { "IntroReadout", "OutroReadout" })
            {
                var line = (TextBlock)Part(card, name);
                var inlines = line.Inlines.OfType<Run>().ToList();
                inlines.Should().HaveCount(5, "label · spacer · prefix · time · suffix");
                for (var i = 0; i < inlines.Count; i++)
                {
                    var r = inlines[i];
                    runs.Add((i == 3 ? "time" : "words", r.FontSize, ((SolidColorBrush)r.Foreground).Color, r.FontWeight));
                }
            }

            var note = StaViewHarness.Descendants<TextBlock>(card)
                .Single(t => (t.Text ?? string.Empty).Contains("low resolution", StringComparison.OrdinalIgnoreCase));
            noteSize = note.FontSize;
            noteForeground = ((SolidColorBrush)note.Foreground).Color;
        }));

        runs.Should().OnlyContain(r => r.Size == body, "every readout run is body size ({0})", body);
        runs.Where(r => r.Role == "time").Should().OnlyContain(r => r.Foreground == accent && r.Weight == FontWeights.SemiBold);
        runs.Where(r => r.Role == "words").Should().OnlyContain(r => r.Foreground == secondary && r.Weight == FontWeights.Normal);
        runs.Should().NotContain(r => r.Foreground == muted || r.Size == small, "nothing in the readout is a muted footnote");
        runs.Should().OnlyContain(
            r => Contrast(r.Foreground, cardBackground) >= 4.5,
            "every readout run must be readable on the card's background (WCAG 1.4.3, 4.5:1)");

        noteSize.Should().Be(small, "the low-resolution note keeps its small style");
        noteForeground.Should().Be(muted, "and its muted colour");
    }

    /// <summary>
    /// A profile with no picture is a normal outcome (I78), not a failure. The card must still be useful: the image
    /// box collapses, the name and values carry it, and the text column is the minimum width.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AProfileWithNoPicture_StillGetsAUsefulCard_WithNoEmptyImageBox()
    {
        var card = MeasureCard(withThumbnail: false);

        card.BoxVisible.Should().BeFalse(
            "an empty letterbox is worse than none — a profile without a picture is normal (I78)");
        card.HasText.Should().BeTrue("the card must still name the profile and its cut values");
        card.ColumnWidth.Should().BeApproximately(320, 1, "with no picture the column is the minimum width");
    }

    /// <summary>
    /// T-180 — a path whose file is not a picture collapses the box too. It used to bind the box to the PATH, so a
    /// missing or undecodable file showed an empty frame.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AFileThatIsNotAPicture_CollapsesTheBox_InsteadOfShowingAnEmptyFrame()
    {
        var junk = TestFiles.MakeNonImage(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vsj-hover-junk-" + Guid.NewGuid().ToString("N")),
            "notes.jpg");

        var card = MeasureCard(thumbnailPath: junk);

        card.SourceLoaded.Should().BeFalse("a 1-byte text file named .jpg does not decode");
        card.BoxVisible.Should().BeFalse("an undecodable file must not leave an empty frame on the card");
        card.ColumnWidth.Should().BeApproximately(320, 1);
    }

    /// <summary>
    /// The card must not swallow the click that selects the profile. SPEC-007 I97 requires a click to
    /// SELECT, and a hover card sitting under the cursor is exactly what would break it.
    ///
    /// <para>What keeps the click is WHERE the card opens: above the chip (<c>Placement=Top</c>; WPF flips it
    /// below when there is no room above), so it is never between the cursor and the chip. I105 used to say "a
    /// ToolTip is never hit-testable" and asserted <c>IsHitTestVisible &amp;&amp; Focusable</c> is false — which
    /// passed only because Focusable is false. A ToolTip IS hit-testable; asserting the two separately (T-180
    /// review) showed it, so the placement is what is asserted now.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheHoverCardNeverStealsTheClickThatSelects()
    {
        var placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
        var focusable = true;
        var staysOpen = false;

        StaViewHarness.OnSta(() => AtScale(1.0, () =>
        {
            var (view, chip) = FirstChip();
            StaViewHarness.LayOut(view, 1280, 800);

            var tip = (ToolTip)HostOf(chip).ToolTip;

            placement = ToolTipService.GetPlacement(HostOf(chip));
            focusable = tip.Focusable;
            staysOpen = ToolTipService.GetShowDuration(HostOf(chip)) > 30000;
        }));

        placement.Should().Be(System.Windows.Controls.Primitives.PlacementMode.Top,
            "the card opens beside the chip, never under the cursor, so it cannot intercept the selecting click");
        focusable.Should().BeFalse("and it must not take focus from the bar");
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

        StaViewHarness.OnSta(() => AtScale(1.0, () =>
        {
            var (view, chip) = FirstChip();
            StaViewHarness.LayOut(view, 1280, 800);
            delay = ToolTipService.GetInitialShowDelay(HostOf(chip));
        }));

        delay.Should().BeGreaterThan(
            200,
            "with no delay, dragging the cursor across a wrapped grid of chips opens and closes a " +
            "large card once per chip");
    }

    // ---- T-180: the picture at its own size, 320-640 DIPs, 16:9 ------------------------------------

    /// <summary>
    /// T-180 (G-058) — the box follows the picture's own width, one picture pixel per screen pixel, between the
    /// 320 minimum and the 640 cap, and is 16:9. A picture narrower than the minimum still fills it and says so
    /// (T-172, I109). Rendered sizes allow ±1 DIP for layout rounding.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(640, 360, 640, false)]
    [InlineData(480, 270, 480, false)]
    [InlineData(320, 180, 320, false)]
    [InlineData(1920, 1080, 640, false)] // capped
    [InlineData(64, 36, 320, true)]      // today's small pictures: fill the minimum box, with the note
    public void APictureShowsAtItsOwnSize_BetweenTheMinimumAndTheCap(int width, int height, double box, bool note)
    {
        var card = MeasureCard(width, height);

        card.BoxWidth.Should().BeApproximately(box, 1, "{0}px at 100% is {1} DIPs, clamped to 320-640", width, box);
        card.BoxHeight.Should().BeApproximately(box * 9 / 16, 1, "the box is 16:9");
        card.ImageWidth.Should().BeApproximately(box, 1, "a 16:9 picture fills the 16:9 box: no crop and no bars");
        card.ImageHeight.Should().BeApproximately(box * 9 / 16, 1);
        card.ColumnWidth.Should().BeApproximately(box, 1, "the name and the readout wrap at the box width");
        card.NoteVisible.Should().Be(note);
        if (note)
        {
            card.NoteText.Should().Contain("Use current frame", "the note names the gesture that re-takes the picture");
        }
    }

    /// <summary>T-180 — at 200% display scaling a 640px picture is 320 DIPs, and nothing drops below the minimum.</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(640, 360, 320)]
    [InlineData(320, 180, 320)]
    public void At200Percent_APictureIsHalfItsPixelsInDips_NeverBelowTheMinimum(int width, int height, double box)
    {
        MeasureCard(width, height, scale: 2.0).BoxWidth.Should().BeApproximately(box, 1);
    }

    /// <summary>
    /// Pixels, not DPI-scaled size. WPF sizes a bitmap by its DPI metadata, so a 480px picture tagged 192 DPI is
    /// "240 wide" to WPF and one tagged 72 DPI is "640 wide". The box — and sharpness — are questions about pixels.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(480, 270, 192, 480, false)]
    [InlineData(480, 270, 72, 480, false)]
    [InlineData(160, 90, 48, 320, true)]
    public void APictureIsJudgedByItsPixels_NotItsDpiSize(int width, int height, double dpi, double box, bool lowResolution)
    {
        var card = MeasureCard(width, height, dpi);

        card.BoxWidth.Should().BeApproximately(box, 1, $"{width}px at {dpi} DPI has {width} real pixels");
        card.NoteVisible.Should().Be(lowResolution, $"{width}px at {dpi} DPI has {width} real pixels for a 320px minimum box");
    }

    /// <summary>T-180 — a 4:3 picture shows whole, centred in the 16:9 box, never cropped.</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void A4By3Picture_ShowsWhole_CentredInThe16By9Box()
    {
        var card = MeasureCard(640, 480);

        card.BoxWidth.Should().BeApproximately(640, 1);
        card.BoxHeight.Should().BeApproximately(360, 1);
        card.ImageWidth.Should().BeApproximately(480, 1, "Uniform scales 640x480 into 640x360 as 480x360");
        card.ImageHeight.Should().BeApproximately(360, 1);
        card.ImageOffsetX.Should().BeApproximately((640 - 480) / 2d, 1, "centred, with equal bars either side");
    }

    /// <summary>
    /// T-180 — a portrait picture shows whole and the card stays a card: a 360px-wide box, 202.5 high, with the
    /// picture about 114 DIPs wide inside it. Accepted, not a defect.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void APortraitPicture_ShowsWhole_AndTheCardStaysACard()
    {
        var card = MeasureCard(360, 640);

        card.BoxWidth.Should().BeApproximately(360, 1);
        card.BoxHeight.Should().BeApproximately(202.5, 1);
        card.ImageWidth.Should().BeApproximately(360 * 202.5 / 640, 1);
        card.ImageHeight.Should().BeApproximately(202.5, 1);
    }

    /// <summary>T-180 boundaries, rendered: one pixel either side of the minimum and of the cap.</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(319, 320, true)]
    [InlineData(320, 320, false)]
    [InlineData(321, 321, false)]
    [InlineData(639, 639, false)]
    [InlineData(640, 640, false)]
    [InlineData(641, 640, false)]
    public void TheBoundaries_OneEitherSideOfTheMinimumAndTheCap(int width, double box, bool note)
    {
        var card = MeasureCard(width, (int)Math.Round(width * 9.0 / 16));

        card.BoxWidth.Should().BeApproximately(box, 1);
        card.NoteVisible.Should().Be(note);
    }

    /// <summary>The note threshold is the card's MINIMUM box width, one pixel either side — not a third hand-typed number.</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheNoteThreshold_IsTheCardsMinimumBoxWidth()
    {
        // Rounded, never truncated: with layout rounding the rendered width is Round(320·s)/s, a hair under 320 at
        // some custom display scales, and truncating would move the threshold to 319.
        var box = (int)Math.Round(MeasureCard(64, 36).BoxWidth);

        MeasureCard(box - 1, 180).NoteVisible.Should().BeTrue($"{box - 1}px is narrower than the {box}px minimum box");
        MeasureCard(box, 180).NoteVisible.Should().BeFalse($"{box}px fills the {box}px minimum box pixel for pixel");
    }

    /// <summary>
    /// T-180 — a 640 box is never clipped by the tooltip chrome: 640 + 2x4 padding + 2x1 border = 650, and the
    /// style's MaxWidth leaves room for it.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheLargestCard_IsNotClippedByTheTooltip()
    {
        var card = MeasureCard(640, 360);

        card.TipMaxWidth.Should().BeGreaterThanOrEqualTo(650);
        card.TipDesiredWidth.Should().BeGreaterThanOrEqualTo(650, "the card asks for its full width");
        card.BoxWidth.Should().BeApproximately(640, 1, "and the box gets it");

        // The tooltip's own DesiredSize is capped at its MaxWidth, so it cannot show clipping. What the card's
        // content asks for, measured with no limit, can: it must fit inside the cap.
        card.ContentUnconstrainedWidth.Should().BeLessThanOrEqualTo(card.TipMaxWidth,
            "the card's content (box + padding + border) must fit inside the tooltip's MaxWidth");
    }

    /// <summary>
    /// T-180 review — the card lays out on whole device pixels. The border and padding put the picture 5 DIPs in,
    /// which is part-way into a screen pixel at 125% and 150%, and without rounding the picture is drawn soft at the
    /// scales most laptops use. UseLayoutRounding is inherited, so it is read on the picture itself.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheCardLaysOutOnWholeDevicePixels()
    {
        var rounded = false;
        StaViewHarness.OnSta(() => AtScale(1.0, () =>
        {
            var (_, chip) = FirstChip(thumbnailPath: ThumbnailFile(640, 360));
            rounded = ((Image)Part(OpenCard(chip), "CardImage")).UseLayoutRounding;
        }));

        rounded.Should().BeTrue("the picture must start on a whole screen pixel at every display scale");
    }

    /// <summary>
    /// T-180 review — the display scale is read from the element being sized. Every other test pins a stand-in that
    /// ignores its argument, so this one records what it is handed: the box and the column, never null or the
    /// profile, and a 640px picture at 2.0 is then 320 DIPs.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheDisplayScale_IsReadFromTheElementBeingSized()
    {
        var seen = new List<object?>();
        double boxWidth = 0;
        string[] names = Array.Empty<string>();

        StaViewHarness.OnSta(() =>
        {
            var saved = ProfilePreviewBoxSizeConverter.DisplayScale;
            ProfilePreviewBoxSizeConverter.DisplayScale = v =>
            {
                seen.Add(v);
                return 2.0;
            };
            try
            {
                var (_, chip) = FirstChip(thumbnailPath: ThumbnailFile(640, 360));
                var tip = (ToolTip)OpenCard(chip);
                var box = (FrameworkElement)Part(tip, "CardPictureBox");
                var column = (FrameworkElement)Part(tip, "CardColumn");
                boxWidth = box.ActualWidth;
                names = seen.Select(v => ReferenceEquals(v, box) ? "box" : ReferenceEquals(v, column) ? "column" : "other")
                    .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            }
            finally
            {
                ProfilePreviewBoxSizeConverter.DisplayScale = saved;
            }
        });

        seen.Should().NotBeEmpty("the converter must ask for the display scale");
        names.Should().Equal(new[] { "box", "column" }, "it asks about the element it sizes, and nothing else");
        boxWidth.Should().BeApproximately(320, 1, "640 pixels at 2.0 are 320 DIPs");
    }

    /// <summary>
    /// The box and column bindings read the NAMED resources, not a copy of their values, so the minimum and the cap
    /// are each written once.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheSizeBindings_ReadTheNamedMinimumAndCap()
    {
        var sources = new List<(bool Min, bool Max)>();

        StaViewHarness.OnSta(() => AtScale(1.0, () =>
        {
            var (view, chip) = FirstChip(thumbnailPath: ThumbnailFile(640, 360));
            var tip = (ToolTip)OpenCard(chip);
            var min = view.Resources["ProfilePreviewCardWidth"];
            var max = view.Resources["ProfilePreviewCardMaxWidth"];

            foreach (var (name, property) in new[]
            {
                ("CardPictureBox", FrameworkElement.WidthProperty),
                ("CardPictureBox", FrameworkElement.HeightProperty),
                ("CardColumn", FrameworkElement.WidthProperty),
            })
            {
                var multi = BindingOperations.GetMultiBinding((DependencyObject)Part(tip, name), property);
                sources.Add((
                    ReferenceEquals(((Binding)multi.Bindings[2]).Source, min),
                    ReferenceEquals(((Binding)multi.Bindings[3]).Source, max)));
            }
        }));

        sources.Should().HaveCount(3).And.OnlyContain(s => s.Min && s.Max);
    }

    /// <summary>
    /// T-180 — the card hugs its box: its width is the box plus the chrome (2x4 padding + 2x1 border), so a small
    /// card is not drawn inside a large empty frame.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(640, 360, true, 650)]
    [InlineData(320, 180, true, 330)]
    [InlineData(0, 0, false, 330)]
    public void TheCardHugsItsBox_NoWiderThanTheBoxPlusChrome(int width, int height, bool withThumbnail, double card)
    {
        MeasureCard(Math.Max(1, width), Math.Max(1, height), withThumbnail: withThumbnail)
            .TipDesiredWidth.Should().BeApproximately(card, 1);
    }

    /// <summary>
    /// The chip is unchanged — still a 28x28 crop. Measured on the clipping box, not the image: under
    /// UniformToFill a 16:9 picture overflows the square and the box clips it, so the image's own width is wider.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheChipIsUnchanged_28Square_Cropped()
    {
        double width = 0, height = 0;
        var stretch = Stretch.None;
        var clips = false;

        StaViewHarness.OnSta(() => AtScale(1.0, () =>
        {
            var (view, chip) = FirstChip(thumbnailPath: ThumbnailFile(640, 360));
            StaViewHarness.LayOut(view, 1280, 800);
            var image = StaViewHarness.Descendants<Image>(chip).First();
            var box = (Border)VisualTreeHelper.GetParent(image);
            width = box.ActualWidth;
            height = box.ActualHeight;
            clips = box.ClipToBounds;
            stretch = image.Stretch;
        }));

        width.Should().Be(ChipThumbnailWidth);
        height.Should().Be(ChipThumbnailWidth);
        clips.Should().BeTrue();
        stretch.Should().Be(Stretch.UniformToFill, "the chip crops to a square; only the card shows the whole frame");
    }

    /// <summary>
    /// T-180 — the named width is the MINIMUM (320), and the cap is the stored picture width, so a stored capture
    /// shows pixel-exact at 100% and the two cannot drift. Replaces T-172's "stored at twice the card" pin, whose
    /// reason — sharp up to 200% in a fixed 320 card — no longer describes a card that grows to the picture.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheNamedWidthIsTheMinimum_AndTheCapIsTheStoredPictureWidth()
    {
        double min = 0, max = 0, noPictureColumn = 0;

        StaViewHarness.OnSta(() => AtScale(1.0, () =>
        {
            var (view, chip) = FirstChip(withThumbnail: false);
            min = (double)view.Resources["ProfilePreviewCardWidth"];
            max = (double)view.Resources["ProfilePreviewCardMaxWidth"];
            noPictureColumn = ((FrameworkElement)Part(OpenCard(chip), "CardColumn")).ActualWidth;
        }));

        min.Should().Be(320, "the minimum box width");
        noPictureColumn.Should().Be(min, "the column reads the named minimum rather than repeating it");
        max.Should().Be(
            VideoSplitJoiner.App.ViewModels.BulkCutViewModel.ProfileThumbnailWidth,
            "the cap IS the stored width: a stored capture shows one picture pixel per screen pixel at 100%");
    }

    private sealed record CardMeasure(
        bool BoxVisible,
        double BoxWidth,
        double BoxHeight,
        double ColumnWidth,
        bool SourceLoaded,
        double ImageWidth,
        double ImageHeight,
        bool NoteVisible,
        string NoteText,
        bool HasText,
        double TipDesiredWidth,
        double TipMaxWidth,
        double ContentUnconstrainedWidth,
        double ImageOffsetX);

    private sealed record Readout(string IntroLabel, string IntroValue, string OutroLabel, string OutroValue);

    /// <summary>Opens the card for a profile whose picture is <paramref name="width"/>x<paramref name="height"/> and measures it.</summary>
    private static CardMeasure MeasureCard(
        int width = 320,
        int height = 180,
        double dpi = 96,
        double scale = 1.0,
        bool withThumbnail = true,
        string? thumbnailPath = null)
    {
        CardMeasure? result = null;

        StaViewHarness.OnSta(() => AtScale(scale, () =>
        {
            var path = withThumbnail ? thumbnailPath ?? ThumbnailFile(width, height, dpi) : null;
            var (view, chip) = FirstChip(withThumbnail: withThumbnail, thumbnailPath: path);
            StaViewHarness.LayOut(view, 1280, 800);

            var tip = (ToolTip)OpenCard(chip);
            var box = (Border)Part(tip, "CardPictureBox");
            var column = (FrameworkElement)Part(tip, "CardColumn");
            var image = (Image)Part(tip, "CardImage");
            var note = StaViewHarness.Descendants<TextBlock>(tip)
                .FirstOrDefault(t => (t.Text ?? string.Empty).Contains("low resolution", StringComparison.OrdinalIgnoreCase));

            var noteVisible = note is not null
                && note.Visibility == Visibility.Visible
                && Ancestors(note).OfType<UIElement>().TakeWhile(e => !ReferenceEquals(e, tip)).All(e => e.Visibility == Visibility.Visible);

            result = new CardMeasure(
                BoxVisible: box.Visibility == Visibility.Visible,
                BoxWidth: box.ActualWidth,
                BoxHeight: box.ActualHeight,
                ColumnWidth: column.ActualWidth,
                SourceLoaded: image.Source is not null,
                ImageWidth: image.ActualWidth,
                ImageHeight: image.ActualHeight,
                NoteVisible: noteVisible,
                NoteText: note?.Text ?? string.Empty,
                HasText: StaViewHarness.Descendants<TextBlock>(tip).Any(t => !string.IsNullOrEmpty(t.Text)),
                TipDesiredWidth: tip.DesiredSize.Width,
                TipMaxWidth: tip.MaxWidth,
                ContentUnconstrainedWidth: UnconstrainedWidth((FrameworkElement)VisualTreeHelper.GetChild(tip, 0)),
                ImageOffsetX: image.TranslatePoint(new Point(0, 0), box).X);
        }));

        return result!;
    }

    // ---- plumbing ---------------------------------------------------------------------------------

    /// <summary>Pins the card's display scale for the body, restoring the seam afterwards. Call inside the STA body.</summary>
    private static void AtScale(double scale, Action body)
    {
        var saved = ProfilePreviewBoxSizeConverter.DisplayScale;
        ProfilePreviewBoxSizeConverter.DisplayScale = _ => scale;
        try
        {
            body();
        }
        finally
        {
            ProfilePreviewBoxSizeConverter.DisplayScale = saved;
        }
    }

    /// <summary>Builds the view with one profile and returns its realised chip.</summary>
    private static (BulkCutView View, ListBoxItem Chip) FirstChip(
        string name = "Season 1 opener",
        bool withThumbnail = true,
        string? thumbnailPath = null,
        double introSeconds = 12,
        double? outroSeconds = 30)
    {
        var settings = new FakeSettings();
        settings.SaveProfile(new VideoSplitJoiner.Core.Profiles.CutProfile(
            name,
            TimeSpan.FromSeconds(introSeconds),
            outroSeconds is { } outro ? TimeSpan.FromSeconds(outro) : null,
            withThumbnail ? thumbnailPath ?? ThumbnailFile() : null));

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
        // tooltip for real. Nothing shows it here, so it is set explicitly. Measured at 1280x800 (was
        // 400x600) so a 640 card can be measured at all (T-180).
        tip!.PlacementTarget = HostOf(chip);
        tip.ApplyTemplate();
        tip.Measure(new Size(1280, 800));
        tip.Arrange(new Rect(0, 0, 1280, 800));
        tip.UpdateLayout();
        return tip;
    }

    /// <summary>A named part of the card's template.</summary>
    private static object Part(FrameworkElement card, string name)
    {
        var tip = (ToolTip)card;
        var part = tip.Template.FindName(name, tip);
        part.Should().NotBeNull($"the card template must have a part named {name}");
        return part!;
    }

    /// <summary>The two readout lines, split into label and value (the label and spacer runs, then the rest).</summary>
    private static Readout ReadReadout(FrameworkElement card)
    {
        static (string Label, string Value) Split(TextBlock line)
        {
            var runs = line.Inlines.OfType<Run>().Select(r => r.Text ?? string.Empty).ToList();
            runs[1].Should().Be("  ", "the label and the value are set apart by two spaces (I101)");
            return (runs[0], string.Concat(runs.Skip(2)));
        }

        var intro = Split((TextBlock)Part(card, "IntroReadout"));
        var outro = Split((TextBlock)Part(card, "OutroReadout"));
        return new Readout(intro.Label, intro.Value, outro.Label, outro.Value);
    }

    /// <summary>What <paramref name="element"/> asks for when nothing limits it — a width no parent's cap can hide.</summary>
    private static double UnconstrainedWidth(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    private static Color BrushColor(FrameworkElement scope, string key) =>
        ((SolidColorBrush)scope.FindResource(key)).Color;

    /// <summary>WCAG 2.x contrast ratio between two opaque colours.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
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

    /// <summary>A real JPEG on disk (320x180 by default), so the card has something to actually measure.</summary>
    private static string ThumbnailFile(int width = 320, int height = 180, double dpi = 96)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vsj-hover-" + Guid.NewGuid().ToString("N") + ".jpg");

        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 251);
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, dpi, dpi, PixelFormats.Bgra32, palette: null, pixels, stride);

        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);

        return path;
    }
}
