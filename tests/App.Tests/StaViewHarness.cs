using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using FluentAssertions;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Lays real WPF views out, with the app's real themes, on ONE shared STA thread.
///
/// <para>Extracted from <c>BulkCutViewLayoutTests</c> (T-162) when the Split screen needed the same
/// harness. It could not simply be copied: <see cref="Application"/> is a process-wide singleton pinned
/// to the thread that created it, and the app's themes hold unfrozen Freezables (a
/// <c>DropShadowEffect</c>), so a SECOND STA worker fails with "cannot access Freezable across threads" —
/// a plumbing error that says nothing about layout. One worker, shared by every layout suite, is the
/// only shape that works.</para>
///
/// <para><b>Laying a view out is itself a real assertion.</b> `StaticResource` resolves at RUNTIME, so a
/// missing or misnamed brush builds green and only dies at first render. That has shipped here before,
/// which is why these tests load the genuine theme dictionaries rather than a stub.</para>
/// </summary>
internal static class StaViewHarness
{
    /// <summary>Window sizes worth checking, from a wide monitor down to a cramped one.</summary>
    internal static readonly (double W, double H)[] Sizes =
    {
        (1600, 900), (1280, 800), (1024, 768), (900, 700), (760, 620),
    };

    private static readonly Lazy<StaWorker> Worker = new(() => new StaWorker(), isThreadSafe: true);

    internal static void OnSta(Action body) => Worker.Value.Run(body);

    internal static void LayOut(FrameworkElement view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
    }

    internal static T? Find<T>(DependencyObject root, string name)
        where T : FrameworkElement
        => Descendants<T>(root).FirstOrDefault(e => e.Name == name);

    internal static IEnumerable<T> Descendants<T>(DependencyObject root)
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
                "laying a few views out must not hang");
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
}
