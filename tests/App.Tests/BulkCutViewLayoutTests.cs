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
