using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FluentAssertions;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-138 (SPEC-011) — the reported bug was a LAYOUT failure, so it is asserted by laying the real view out.
///
/// <para>T-168 renamed this from ...AGrowingProfileBar...: it builds the view with NO DataContext and
/// grows the header synthetically, so it never lays out a populated profile card. It tests a growing
/// HEADER. The growing profile bar is covered by
/// <c>TheChipListIsCappedAtTwoRows_AndScrollsPastIt_SoTheHeaderCannotGrowWithTheProfileCount</c>.</para>
///
/// <para>User: <i>"when I click save new profile the profiles bar move to middle of page and all content
/// gone"</i>. Saving the first profile flips <c>HasProfiles</c> false→true, revealing the picker, the
/// Delete button and the apply controls at once. The header grows; if the window's middle row does not
/// defend its space, the header takes it and the screen goes blank until something forces a re-layout.</para>
///
/// <para><b>Why this test exists at all.</b> Two fixes for this were shipped on hypotheses — dangling mouse
/// capture, then a layered popup — and both were wrong, because "I could not reproduce it locally" was
/// accepted as a reason to guess. It is reproducible without a popup, a file dialog, a video or a click:
/// build the real <see cref="BulkCutView"/>, lay it out at real window sizes, grow the header, and look at
/// what happens to the content. That is what should have been written first.</para>
///
/// <para>What holds the line today is <c>RootGrid</c>'s middle row: <c>Height="*" MinHeight="220"</c>. This
/// pins that, so a future edit that drops the <c>MinHeight</c>, or makes the middle row <c>Auto</c>, fails
/// here instead of on the user's screen.</para>
///
/// <para>Everything runs on ONE STA thread: WPF needs STA, and the app's theme resources contain unfrozen
/// Freezables (a <c>DropShadowEffect</c>) that cannot be touched from a second thread — so a per-test
/// thread would fail for reasons that have nothing to do with layout.</para>
/// </summary>
public sealed class BulkCutViewLayoutTests
{
    /// <summary>The window sizes checked — shared with every other layout suite (T-162).</summary>
    private static (double W, double H)[] Sizes => StaViewHarness.Sizes;

    /// <summary>The floor the middle row promises (<c>RootGrid</c> row 1 <c>MinHeight</c>).</summary>
    private const double ContentFloor = 220;

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheContentAreaSurvivesAGrowingHeader_AtEveryRealisticWindowSize()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var view = new BulkCutView();

                // Laying out at all is itself a real check: StaticResource resolves at RUNTIME, so a
                // missing brush builds green and only dies at first render. That shipped once already.
                LayOut(view, w, h);

                var content = Find<FrameworkElement>(view, "BulkContentArea");
                if (content is null)
                {
                    failures.Add($"{w}x{h}: BulkContentArea not found — the view's shape changed");
                    continue;
                }

                var before = content.ActualHeight;

                // The reported transition, forced directly: make the header taller, the way revealing the
                // populated profile bar does. Done as pure layout so the assertion is about layout, not
                // about the save pipeline.
                var header = VisualTreeHelper.GetChild(Find<Grid>(view, "RootGrid")!, 0) as FrameworkElement;
                if (header is null)
                {
                    failures.Add($"{w}x{h}: no header row to grow");
                    continue;
                }

                header.MinHeight = header.ActualHeight + 140;
                LayOut(view, w, h);

                var after = content.ActualHeight;

                if (after < ContentFloor)
                {
                    failures.Add(
                        $"{w}x{h}: content collapsed to {after:0} (floor {ContentFloor}) when the header " +
                        $"grew — this is the reported bug: 'the profiles bar move to middle of page and " +
                        $"all content gone'. Was {before:0} before.");
                }

                if (content.ActualWidth < 200)
                {
                    failures.Add($"{w}x{h}: content squeezed horizontally to {content.ActualWidth:0}");
                }
            }
        });

        failures.Should().BeEmpty(
            "the content area must keep its floor however tall the header gets:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The guard itself, asserted directly rather than only through its effect: deleting the middle row's
    /// <c>MinHeight</c>, or making it size to content, is what would let the header eat the screen.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheWindowsMiddleRowStillDefendsItsSpace()
    {
        // Plain values only: a RowDefinition read on the xUnit thread throws, and the failure would look
        // like a WPF problem rather than the assertion it is.
        var rows = 0;
        var isStar = false;
        var minHeight = 0d;

        OnSta(() =>
        {
            var view = new BulkCutView();
            LayOut(view, 1280, 800);

            var root = Find<Grid>(view, "RootGrid");
            root.Should().NotBeNull("the view should still have a named RootGrid");

            rows = root!.RowDefinitions.Count;
            var middle = root.RowDefinitions.ElementAtOrDefault(1);
            if (middle is not null)
            {
                isStar = middle.Height.IsStar;
                minHeight = middle.MinHeight;
            }
        });

        rows.Should().BeGreaterThanOrEqualTo(3, "header, content, footer");
        isStar.Should().BeTrue(
            "the content row takes the LEFTOVER space; an Auto row would size to its content and let the " +
            "header push it off the screen");
        minHeight.Should().BeGreaterThanOrEqualTo(
            ContentFloor,
            "the floor is what stops a tall header from collapsing the content to nothing");
    }

    /// <summary>
    /// T-146 — the destructive button must not land on Run's pixels.
    ///
    /// <para>"Delete originals" is the only irreversible control on this screen, and it appears exactly
    /// when a batch finishes — under the cursor of someone who has just been pressing Run. The first
    /// attempt appended it AFTER Run inside Run's horizontal <c>StackPanel</c> with
    /// <c>HorizontalAlignment="Left"</c>, which a StackPanel ignores along its stacking axis. The XAML
    /// comment said "pushed to the far LEFT of the footer"; the layout put it immediately right of Run,
    /// and revealing it shoved Run sideways. The ticket's criterion was ticked from the comment rather
    /// than from the markup — which is precisely why this is now a test and not a comment.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheDestructiveButtonSitsAtTheOppositeEndFromRun_AndRevealingItDoesNotMoveRun()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var view = new BulkCutView();
                LayOut(view, w, h);   // the visual tree does not exist until the first layout pass

                var del = Find<Button>(view, "DeleteOriginalsButton");
                var run = Find<Button>(view, "RunBatchButton");

                if (del is null || run is null)
                {
                    failures.Add($"{w}x{h}: footer buttons not found — the view's shape changed");
                    continue;
                }

                // Before the batch finishes the destructive button does not exist on screen.
                del.Visibility = Visibility.Collapsed;
                LayOut(view, w, h);
                var runBefore = run.TranslatePoint(new Point(0, 0), view).X;

                // The batch finishes: the button appears.
                del.Visibility = Visibility.Visible;
                LayOut(view, w, h);
                var runAfter = run.TranslatePoint(new Point(0, 0), view).X;
                var delLeft = del.TranslatePoint(new Point(0, 0), view).X;
                var delRight = delLeft + del.ActualWidth;

                if (Math.Abs(runAfter - runBefore) > 1)
                {
                    failures.Add(
                        $"{w}x{h}: revealing Delete moved Run by {runAfter - runBefore:0.#}px " +
                        $"({runBefore:0} → {runAfter:0}) — the button people press repeatedly must not " +
                        "shift when a destructive one appears");
                }

                if (delRight > runBefore)
                {
                    failures.Add(
                        $"{w}x{h}: Delete (ends at x={delRight:0}) overlaps the pixels Run occupied " +
                        $"before the batch finished (x={runBefore:0}) — that is the misclick this " +
                        "placement exists to prevent");
                }

                // Opposite ends, not merely "not touching".
                if (delLeft > w / 2)
                {
                    failures.Add($"{w}x{h}: Delete starts at x={delLeft:0}, past the midpoint — it is " +
                                 "supposed to be at the FAR left, away from Run");
                }
            }
        });

        failures.Should().BeEmpty(
            "the destructive footer button must sit at the opposite end from Run:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// T-156 — the footer's option row must WRAP, not clip.
    ///
    /// <para>Adding two checkboxes pushed this row past the window width, and a horizontal
    /// <c>StackPanel</c> silently clips: "Replace originals" and both new options simply vanished off
    /// the right edge at 1280px, with nothing on screen to suggest anything was missing.</para>
    ///
    /// <para>This is the <b>third</b> time the same mistake has shipped here — T-136 in the profile bar,
    /// T-141 in the header, now the footer. A pattern that recurs three times is not a slip, so it gets a
    /// test rather than another careful comment.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheFooterOptionsWrapInsteadOfClipping()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var view = new BulkCutView();
                LayOut(view, w, h);

                var options = Find<FrameworkElement>(view, "FooterOptions");
                if (options is null)
                {
                    failures.Add($"{w}x{h}: FooterOptions not found — the view's shape changed");
                    continue;
                }

                // Measure against the WINDOW, not the panel. A horizontal StackPanel that overflows
                // reports its own oversized width as ActualWidth, so its children never look out of
                // bounds relative to IT — the clip happens at the Grid column boundary. Comparing child
                // to panel therefore passes even when everything is off-screen, which is exactly how the
                // first version of this test passed against the clipping layout it was written to catch.
                foreach (var child in Descendants<CheckBox>(options))
                {
                    var right = child.TranslatePoint(new Point(0, 0), view).X + child.ActualWidth;

                    if (right > w + 1)
                    {
                        failures.Add(
                            $"{w}x{h}: an option ends at x={right:0}, past the {w:0}px window — it is " +
                            "off-screen, which is how three destructive checkboxes became invisible");
                    }
                }
            }
        });

        failures.Should().BeEmpty(
            "the footer options must wrap onto another line rather than disappear:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// T-160 (SPEC-011) — the options and the actions occupy SEPARATE rows.
    ///
    /// <para>Eight controls and three conditional notes had accreted onto one footer line, one ticket at
    /// a time. T-156's <c>WrapPanel</c> stopped them clipping, but wrapping inside a <c>*</c> column
    /// squeezed between two <c>Auto</c> columns just reflows them into a narrow ragged block while
    /// <c>RunScopeSummary</c> wraps in its own column beside them.</para>
    ///
    /// <para>The cure is structural — the options get the full width on their own row — so the test is
    /// structural too: the options must end above where Run begins. A future edit that folds them back
    /// onto Run's line fails here rather than looking merely "a bit tight" on someone's screen.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheFooterOptionsSitOnTheirOwnRow_NotOnRunsLine()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var view = new BulkCutView();
                LayOut(view, w, h);

                var options = Find<FrameworkElement>(view, "FooterOptions");
                var run = Find<FrameworkElement>(view, "RunBatchButton");

                if (options is null || run is null)
                {
                    failures.Add($"{w}x{h}: FooterOptions or RunBatchButton not found — the footer's shape changed");
                    continue;
                }

                var optionsBottom = options.TranslatePoint(new Point(0, 0), view).Y + options.ActualHeight;
                var runTop = run.TranslatePoint(new Point(0, 0), view).Y;

                if (optionsBottom > runTop + 1)
                {
                    failures.Add(
                        $"{w}x{h}: the options end at y={optionsBottom:0} but Run starts at y={runTop:0} — " +
                        "they are sharing a line again, which is the crowding this fixed");
                }
            }
        });

        failures.Should().BeEmpty(
            "the output options must own a full-width row above the action row:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// T-168 (SPEC-011) — the profiles card takes the header row's FULL width.
    ///
    /// <para>It carried <c>HorizontalAlignment="Left"</c> and was therefore content-sized, so its right
    /// edge moved with whatever controls happened to be visible. Full width makes that edge stable and is
    /// the precondition for the chips wrapping across the row rather than inside a 420px column.</para>
    ///
    /// <para><b>Asserted with a SHORT profile list on purpose.</b> A full one fills the row by itself, so
    /// a <c>Left</c>-aligned card would pass and the test would prove nothing — the exact shape of vacuity
    /// this suite has already been caught by once (see the deleted T-161 scroll test).</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheProfilesCardTakesTheFullWidthOfItsRow_EvenWithOneProfile()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var view = BuildViewWithProfiles(1);
                LayOut(view, w, h);

                var card = CardBorderAround(Find<FrameworkElement>(view, "ProfileBar")) as Border;
                if (card is null || VisualTreeHelper.GetParent(card) is not FrameworkElement host)
                {
                    failures.Add($"{w}x{h}: the profiles card could not be located — its shape changed");
                    continue;
                }

                // The card's own parent decides the width available to it.
                var available = host.ActualWidth;
                if (available - card.ActualWidth > 2)
                {
                    failures.Add(
                        $"{w}x{h}: the card is {card.ActualWidth:0}px inside a {available:0}px row — " +
                        "it is still content-sized, so the chips cannot wrap across the full width");
                }

                // And the BAR must fill the card. Asserting only the card leaves the 420px cap free to
                // come back: with 14 profiles a capped bar still wraps inside its 420px and still keeps
                // every chip in bounds, so both other tests pass. That mutation survived until this
                // assertion existed — the card being wide is worth nothing if the list inside it is not.
                var bar = Find<FrameworkElement>(view, "ProfileBar");
                if (bar is null)
                {
                    failures.Add($"{w}x{h}: ProfileBar not found");
                    continue;
                }

                var cardInterior = card.ActualWidth - card.Padding.Left - card.Padding.Right;
                if (cardInterior - bar.ActualWidth > 2)
                {
                    failures.Add(
                        $"{w}x{h}: the chip list is {bar.ActualWidth:0}px inside a {cardInterior:0}px card — " +
                        "it is still width-capped, so most profiles stay out of sight");
                }
            }
        });

        failures.Should().BeEmpty(
            "the profiles card must span its row:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// T-168 (SPEC-011) — the chips WRAP onto new lines; they never scroll sideways.
    ///
    /// <para>Wrapping is asserted as <b>≥2 distinct chip Y bands</b> rather than "the bar is wide", because
    /// width alone is satisfied by a stretched bar that still clips. The three attributes that make this
    /// work are all load-bearing, and the most fragile is the least obvious: a <c>WrapPanel</c> inside a
    /// <c>ScrollViewer</c> whose horizontal scrolling is <c>Auto</c> or <c>Hidden</c> is measured at
    /// INFINITE width and never wraps. Only <c>Disabled</c> does. This test is what catches that.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheChipsWrapOntoNewLines_InsteadOfScrollingSideways()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var view = BuildViewWithProfiles(14);
                LayOut(view, w, h);

                var bar = Find<FrameworkElement>(view, "ProfileBar");
                if (bar is null)
                {
                    failures.Add($"{w}x{h}: ProfileBar not found — the card's shape changed");
                    continue;
                }

                var chips = Descendants<ListBoxItem>(bar).ToList();
                if (chips.Count != 14)
                {
                    failures.Add($"{w}x{h}: expected 14 chips realised, found {chips.Count}");
                    continue;
                }

                var bands = chips
                    .Select(c => Math.Round(c.TranslatePoint(new Point(0, 0), bar).Y))
                    .Distinct()
                    .Count();

                if (bands < 2)
                {
                    failures.Add(
                        $"{w}x{h}: all 14 chips sit on ONE line — the bar is not wrapping. The usual cause " +
                        "is ScrollViewer.HorizontalScrollBarVisibility being anything but Disabled, which " +
                        "measures the WrapPanel at infinite width");
                }

                // Nothing may hang off the right edge: wrapping replaced scrolling, so a chip past the
                // edge is now a silent CLIP rather than something the user can scroll to.
                foreach (var chip in chips)
                {
                    var right = chip.TranslatePoint(new Point(0, 0), bar).X + chip.ActualWidth;
                    if (right > bar.ActualWidth + 2)
                    {
                        failures.Add(
                            $"{w}x{h}: a chip ends at x={right:0} inside a {bar.ActualWidth:0}px bar — clipped");
                        break;
                    }
                }
            }
        });

        failures.Should().BeEmpty(
            "the chip list must wrap:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// T-168 (SPEC-011) — the actions occupy their own row BELOW the chips, and wrap rather than clip.
    ///
    /// <para>Structural, like T-160's footer test one region south: the chips must end above where the
    /// actions begin. An edit that folds them back onto one line fails here rather than reading as merely
    /// "a bit tight" on someone's screen.</para>
    ///
    /// <para>Controls are found by walking <c>ProfileActions</c>, with an explicit count guard. The test
    /// this replaces selected them with <c>b.Content as string</c> containing "Apply to" and had no such
    /// guard — the day either segment gains an icon that filter matches ZERO buttons and the loop passes
    /// green having checked nothing.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheActionsSitOnTheirOwnRowBelowTheChips_AndWrapInsteadOfClipping()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var view = BuildViewWithProfiles(14);
                LayOut(view, w, h);

                var bar = Find<FrameworkElement>(view, "ProfileBar");
                var actions = Find<Panel>(view, "ProfileActions");
                if (bar is null || actions is null)
                {
                    failures.Add($"{w}x{h}: ProfileBar or ProfileActions not found — the card's shape changed");
                    continue;
                }

                // Visibility, NOT IsVisible: this harness measures and arranges without a window, so
                // there is no PresentationSource and IsVisible is false for EVERY element. Filtering
                // on it matched zero controls — caught only because the guard below demands 8.
                var controls = Descendants<ButtonBase>(actions)
                    .Where(b => b.Visibility == Visibility.Visible)
                    .ToList();
                if (controls.Count < 8)
                {
                    failures.Add(
                        $"{w}x{h}: only {controls.Count} action controls found under ProfileActions — the " +
                        "sweep below would prove nothing (there should be at least 8)");
                    continue;
                }

                var chipsBottom = bar.TranslatePoint(new Point(0, 0), view).Y + bar.ActualHeight;

                foreach (var c in controls)
                {
                    var topLeft = c.TranslatePoint(new Point(0, 0), view);

                    if (topLeft.Y + 1 < chipsBottom)
                    {
                        failures.Add(
                            $"{w}x{h}: an action starts at y={topLeft.Y:0}, above where the chips end " +
                            $"(y={chipsBottom:0}) — the actions are back on the chip line");
                        break;
                    }

                    if (topLeft.X + c.ActualWidth > w + 1)
                    {
                        failures.Add(
                            $"{w}x{h}: an action ends at x={topLeft.X + c.ActualWidth:0}, past the {w:0}px " +
                            "window — the action row is clipping instead of wrapping");
                        break;
                    }
                }
            }
        });

        failures.Should().BeEmpty(
            "the actions must sit below the chips and wrap:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// T-168 (SPEC-011) — the chip list is capped on the VERTICAL axis and SCROLLS past the cap.
    ///
    /// <para>T-161's <c>MaxWidth</c> rotated onto the axis the new layout is free on. Measured, an uncapped
    /// wrap grows the header 154px at 760x620 and pushes Run 74px off-screen, against 72px of slack — so
    /// this is the invariant that keeps the reversal from re-creating T-141.</para>
    ///
    /// <para>Both halves matter. A cap alone would CLIP the profiles past row two, which is the same silent
    /// loss the wrap was introduced to remove, so <c>ScrollableHeight &gt; 0</c> is asserted too.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheChipListIsCappedAtTwoRows_AndScrollsPastIt_SoTheHeaderCannotGrowWithTheProfileCount()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var view = BuildViewWithProfiles(40);
                LayOut(view, w, h);

                var bar = Find<ListBox>(view, "ProfileBar");
                if (bar is null)
                {
                    failures.Add($"{w}x{h}: ProfileBar not found");
                    continue;
                }

                // Read the cap from the view's own resources — never a hand-copied duplicate of it.
                var cap = (double)view.FindResource("ProfileBarMaxHeight");

                if (bar.ActualHeight > cap + 1)
                {
                    failures.Add(
                        $"{w}x{h}: 40 profiles grew the bar to {bar.ActualHeight:0}px, past its {cap:0}px " +
                        "cap — the header now scales with the profile count (T-141)");
                }

                var scroller = Descendants<ScrollViewer>(bar).FirstOrDefault();
                if (scroller is null)
                {
                    failures.Add($"{w}x{h}: the bar has no ScrollViewer — capped content would be clipped");
                    continue;
                }

                if (scroller.ScrollableHeight <= 0)
                {
                    failures.Add(
                        $"{w}x{h}: 40 profiles are capped but ScrollableHeight is {scroller.ScrollableHeight:0} " +
                        "— the profiles past the cap are CLIPPED, not reachable");
                }
            }
        });

        failures.Should().BeEmpty(
            "the chip list must be capped AND scrollable:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// T-168 (SPEC-011) — Run stays on screen with a full profile list. The regression T-161 feared, and
    /// the reason the vertical cap exists rather than being taste.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void RunStaysOnScreen_EvenWith40Profiles()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var view = BuildViewWithProfiles(40);
                LayOut(view, w, h);

                var run = Find<FrameworkElement>(view, "RunBatchButton");

                if (run is null)
                {
                    failures.Add($"{w}x{h}: the Run button was not found — this test cannot prove anything");
                    continue;
                }

                var bottom = run.TranslatePoint(new Point(0, 0), view).Y + run.ActualHeight;
                if (bottom > h + 1)
                {
                    failures.Add(
                        $"{w}x{h}: Run ends at y={bottom:0}, past the {h:0}px window — a full profile list " +
                        "pushed it off-screen");
                }
            }
        });

        failures.Should().BeEmpty(
            "a full profile list must not push Run off-screen:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// T-168 (SPEC-011) — one very long profile NAME cannot take the whole row.
    ///
    /// <para><b>This assertion was rewritten because its first version was unfalsifiable.</b> It asserted
    /// the chip does not extend past the bar — but a <c>WrapPanel</c> measures its children against the
    /// available width, so that is true whether or not the label is bounded. Deleting the
    /// <c>MaxWidth</c> the test exists to protect left it green.</para>
    ///
    /// <para>What <c>MaxWidth</c> actually buys is that a 300-character name does not swallow an entire
    /// row and push every other profile down — the chip is bounded, the name trims, and its neighbours
    /// still share the line. That is what is asserted now, and deleting the cap fails it.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void OneVeryLongProfileNameCannotSwallowTheWholeRow()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var settings = new FakeSettings();
                settings.SaveProfile(new VideoSplitJoiner.Core.Profiles.CutProfile(
                    new string('W', 300), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(30)));
                for (var i = 0; i < 3; i++)
                {
                    settings.SaveProfile(new VideoSplitJoiner.Core.Profiles.CutProfile(
                        $"Short {i}", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(9)));
                }

                var view = new BulkCutView
                {
                    DataContext = new VideoSplitJoiner.App.ViewModels.BulkCutViewModel(
                        new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(),
                        settings, new FakeBulkTrimEngine()),
                };

                LayOut(view, w, h);

                var bar = Find<FrameworkElement>(view, "ProfileBar");
                var chips = bar is null
                    ? new List<ListBoxItem>()
                    : Descendants<ListBoxItem>(bar).ToList();
                if (bar is null || chips.Count != 4)
                {
                    failures.Add($"{w}x{h}: expected 4 chips, found {chips.Count}");
                    continue;
                }

                var longChip = chips[0];

                // The bound label (220) plus the 28px thumb and the chip's own padding/margins.
                const double ChipCeiling = 300;
                if (longChip.ActualWidth > ChipCeiling)
                {
                    failures.Add(
                        $"{w}x{h}: a 300-character name made its chip {longChip.ActualWidth:0}px wide " +
                        $"(ceiling {ChipCeiling:0}) — the label is unbounded, so one profile swallows the row");
                }

                // …and the neighbours must still share its line.
                var longTop = Math.Round(longChip.TranslatePoint(new Point(0, 0), bar).Y);
                var sharing = chips.Skip(1)
                    .Count(c => Math.Round(c.TranslatePoint(new Point(0, 0), bar).Y) == longTop);
                if (sharing == 0)
                {
                    failures.Add(
                        $"{w}x{h}: the long-named chip is alone on its line — it pushed all 3 other " +
                        "profiles onto later rows");
                }
            }
        });

        failures.Should().BeEmpty(
            "a long name must trim, not widen the chip:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Walks UP from the profile bar to the card <c>Border</c> that encloses it. The card has no
    /// <c>x:Name</c>, and giving it one purely for a test would be the test dictating the view's shape.
    /// </summary>
    private static DependencyObject? CardBorderAround(DependencyObject? from)
    {
        var node = from is null ? null : VisualTreeHelper.GetParent(from);
        while (node is not null and not Border)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return node;
    }

    /// <summary>Builds a real BulkCutView whose view-model already holds <paramref name="count"/> profiles.</summary>
    private static BulkCutView BuildViewWithProfiles(int count)
    {
        var settings = new FakeSettings();
        for (var i = 0; i < count; i++)
        {
            settings.SaveProfile(new VideoSplitJoiner.Core.Profiles.CutProfile(
                $"Season {i + 1} opener", TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(30)));
        }

        return new BulkCutView
        {
            DataContext = new VideoSplitJoiner.App.ViewModels.BulkCutViewModel(
                new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(),
                settings, new FakeBulkTrimEngine()),
        };
    }

    // ---- plumbing ---------------------------------------------------------------------------------
    //
    // T-162: the STA worker, theme loading and visual-tree helpers moved to StaViewHarness so the Split
    // screen's layout suite could share them. They could NOT be copied - Application is a process-wide
    // singleton pinned to its creating thread and the themes hold unfrozen Freezables, so a second STA
    // worker fails with "cannot access Freezable across threads".

    private static void OnSta(Action body) => StaViewHarness.OnSta(body);

    private static void LayOut(FrameworkElement view, double width, double height)
        => StaViewHarness.LayOut(view, width, height);

    private static T? Find<T>(DependencyObject root, string name)
        where T : FrameworkElement
        => StaViewHarness.Find<T>(root, name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
        => StaViewHarness.Descendants<T>(root);
}
