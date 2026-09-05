using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-138 (SPEC-011) — the reported bug was a LAYOUT failure, so it is asserted by laying the real view out.
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
    /// <summary>The window sizes checked, from a wide monitor down to a cramped one.</summary>
    private static readonly (double W, double H)[] Sizes =
    {
        (1600, 900), (1280, 800), (1024, 768), (900, 700), (760, 620),
    };

    /// <summary>The floor the middle row promises (<c>RootGrid</c> row 1 <c>MinHeight</c>).</summary>
    private const double ContentFloor = 220;

    /// <summary>The cap the profile bar promises (<c>ProfileBar</c> <c>MaxWidth</c>, T-161).</summary>
    private const double ProfileBarMaxWidth = 420;

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheContentAreaSurvivesAGrowingProfileBar_AtEveryRealisticWindowSize()
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
    /// T-161 (SPEC-007) — the profile bar SCROLLS; it never pushes the apply actions off the row.
    ///
    /// <para>The ComboBox became a horizontal strip of clickable chips so the pictures profiles already
    /// carry (T-129, T-135) are visible at rest instead of hidden behind a closed dropdown. The obvious
    /// way to get that wrong is to let the strip grow with the profile count until the apply buttons
    /// beside it walk off the screen — which is <b>exactly</b> what T-136 fixed on this very bar.</para>
    ///
    /// <para>So it is tested with the bar POPULATED. An empty bar is collapsed and proves nothing; the
    /// interesting layout only exists once there are more profiles than fit.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheProfileBarScrolls_AndNeverPushesTheApplyActionsOffScreen()
    {
        var failures = new List<string>();

        OnSta(() =>
        {
            foreach (var (w, h) in Sizes)
            {
                var settings = new FakeSettings();
                for (var i = 0; i < 14; i++)
                {
                    settings.SaveProfile(new VideoSplitJoiner.Core.Profiles.CutProfile(
                        $"Season {i + 1} opener", TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(30)));
                }

                var view = new BulkCutView
                {
                    DataContext = new VideoSplitJoiner.App.ViewModels.BulkCutViewModel(
                        new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(),
                        settings, new FakeBulkTrimEngine()),
                };

                LayOut(view, w, h);

                var bar = Find<FrameworkElement>(view, "ProfileBar");
                if (bar is null)
                {
                    failures.Add($"{w}x{h}: ProfileBar not found — the profile card's shape changed");
                    continue;
                }

                // Pin the CAP, not merely "inside the window". The surrounding WrapPanel already stops
                // the bar overflowing the window on its own — it would simply take the whole line and
                // wrap the apply buttons underneath — so an on-screen check passes with or without the
                // cap and proves nothing. The first version of this test did exactly that and its
                // mutation survived. What the cap actually buys is the bar staying beside its actions
                // instead of displacing them, so that is what is asserted.
                var barWidth = bar.ActualWidth;
                if (barWidth > ProfileBarMaxWidth + 2)
                {
                    failures.Add(
                        $"{w}x{h}: the profile bar is {barWidth:0}px wide — 14 profiles widened it past its " +
                        $"{ProfileBarMaxWidth:0}px cap instead of scrolling inside it");
                }

                var barRight = bar.TranslatePoint(new Point(0, 0), view).X + barWidth;
                if (barRight > w + 1)
                {
                    failures.Add(
                        $"{w}x{h}: the profile bar itself ends at x={barRight:0}, past the {w:0}px window");
                }

                // The actions that act on the selection must remain on screen beside it. This is the
                // T-136 failure restated: the bar grew, and everything after it left the building.
                foreach (var b in Descendants<Button>(view))
                {
                    var content = b.Content as string;
                    if (content is null || !content.Contains("Apply to", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var right = b.TranslatePoint(new Point(0, 0), view).X + b.ActualWidth;
                    if (right > w + 1)
                    {
                        failures.Add(
                            $"{w}x{h}: \"{content}\" ends at x={right:0}, past the {w:0}px window — the " +
                            "profile bar pushed the apply actions off the edge");
                    }
                }
            }
        });

        failures.Should().BeEmpty(
            "a long profile list must scroll inside the bar, not widen it:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    // ---- plumbing ---------------------------------------------------------------------------------

    /// <summary>
    /// ONE STA thread for the whole class, shared by every test.
    ///
    /// <para><see cref="Application"/> is a process-wide singleton pinned to the thread that created it,
    /// and the app's themes hold unfrozen Freezables (a <c>DropShadowEffect</c>). A thread per test
    /// therefore fails on the second test with "cannot access Freezable across threads" - a plumbing
    /// error that says nothing about layout. One worker, reused, removes the whole class of noise.</para>
    /// </summary>
    private static readonly Lazy<StaWorker> Worker = new(() => new StaWorker(), isThreadSafe: true);

    private static void OnSta(Action body) => Worker.Value.Run(body);

    private sealed class StaWorker
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(Action Body, System.Threading.Tasks.TaskCompletionSource Done)> _queue = new();

        public StaWorker()
        {
            var thread = new Thread(() =>
            {
                EnsureApplicationResources();
                foreach (var (body, done) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        body();
                        done.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        done.TrySetException(ex);
                    }
                }
            })
            {
                IsBackground = true,
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Run(Action body)
        {
            var done = new System.Threading.Tasks.TaskCompletionSource(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((body, done));

            done.Task.Wait(TimeSpan.FromSeconds(60)).Should().BeTrue(
                "laying out a few views must not hang");
            done.Task.GetAwaiter().GetResult();   // rethrow whatever the body threw, with its own message
        }
    }

    /// <summary>
    /// Load the app's REAL theme dictionaries. A stubbed theme would prove nothing — the styles are part
    /// of what is under test, and a `StaticResource` that does not resolve is exactly the class of bug
    /// this catches.
    /// </summary>
    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
        {
            return;
        }

        var app = new Application();
        foreach (var relative in new[] { "Themes/Tokens.xaml", "Themes/Controls.xaml" })
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/VideoSplitJoiner.App;component/" + relative, UriKind.Absolute),
            });
        }
    }

    private static void LayOut(FrameworkElement view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
    }

    private static T? Find<T>(DependencyObject root, string name)
        where T : FrameworkElement
        => Descendants<T>(root).FirstOrDefault(e => e.Name == name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
            {
                yield return hit;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }
}
