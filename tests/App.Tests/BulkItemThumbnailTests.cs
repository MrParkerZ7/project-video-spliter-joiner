using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for the T-108 per-row cut-point frame thumbnails on <see cref="BulkItemViewModel"/> (SPEC-011):
/// each row grabs a small frame at the keyframe-SNAPPED intro-end (and, when set, outro-start), debounced +
/// latest-wins + cancel-prior (modelled on <see cref="ThumbnailPreviewViewModel"/>), bounded in concurrency
/// across a batch, best-effort (a null grab → null path → the placeholder chip), and cancelled per row on
/// Remove/Clear. No ffmpeg, no WPF: the real-snap <see cref="BulkFakeProbe"/> drives snapping, the
/// <see cref="FakeThumbnailService"/> scripts the grab, a pumpable <see cref="SynchronizationContext"/> makes
/// the grabber's <c>Progress&lt;T&gt;</c> result-marshal deterministic, and a gated debounce seam makes the
/// coalesce race controllable — the same harness shape as <see cref="ThumbnailPreviewViewModelTests"/>.
/// </summary>
public sealed class BulkItemThumbnailTests
{
    private const string PathA = @"C:\videos\ep01.mp4";

    // ---- Pumpable single-threaded sync context (drains the grabber's Progress<T> posts) -----

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback D, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public int Drain()
        {
            var ran = 0;
            while (_queue.TryDequeue(out var item))
            {
                item.D(item.State);
                ran++;
            }

            return ran;
        }

        /// <summary>
        /// Drain the posts the completed grab produced.
        ///
        /// <para>T-137: this used to spin-drain against a 500ms wall-clock deadline, because the grab
        /// continuation resumes on a thread-pool thread and only THEN posts its result here. Under a
        /// solution-level run the pool is saturated by the other assembly, the continuation was not
        /// reliably scheduled inside the deadline, and the test failed with an empty request list.
        /// Callers now await the grab itself (<c>Settle</c>), so there is nothing left to wait for.</para>
        /// </summary>
        public void PumpSettled()
        {
            // Two passes: the first runs the result post, which may itself queue a trailing post.
            Drain();
            Drain();
        }

        /// <summary>
        /// Uninstall this pump from the current thread (T-148).
        ///
        /// <para>The clear lives in the scope that INSTALLED the context - a <c>using</c> in the test body -
        /// rather than in a teardown on the test class. xUnit wraps each test method in its own
        /// <c>AsyncTestSyncContext</c> and restores the pre-test ambient context in a <c>finally</c> that runs
        /// BEFORE the class is disposed, so a class-level teardown would only ever observe the
        /// already-restored context and could never clear this pump.</para>
        ///
        /// <para>Clears to <c>null</c> - a pooled thread's natural state - rather than restoring the prior
        /// value, because every suite that needs a context installs its own.</para>
        /// </summary>
        /// <summary>
        /// Uninstall THIS pump, and only this pump.
        ///
        /// <para>The guard matters. At the sites where the pump is installed inside an <c>async</c>
        /// helper, <c>AsyncTaskMethodBuilder.Start</c> has already restored the caller's context by the
        /// time this runs — so the current context is xUnit's own per-test one, not the pump. An
        /// unguarded <c>SetSynchronizationContext(null)</c> there would tear out xUnit's context rather
        /// than ours: harmless in every observed run, but it is the exact action this suite declines to
        /// take against the other suites' contexts, and code that contradicts its own rationale is a
        /// trap for whoever reads it next.</para>
        /// </summary>
        public void Dispose()
        {
            if (ReferenceEquals(SynchronizationContext.Current, this))
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }
    }

    /// <summary>
    /// A controllable debounce seam: each grab's debounce await parks on a fresh
    /// <see cref="TaskCompletionSource"/> until <see cref="Release"/>d, so a request can be left "in the
    /// debounce window" while a newer request supersedes it — exactly the coalesce race. A cancel faults the
    /// wait (latest-wins), so a superseded/removed request never proceeds to a grab.
    /// </summary>
    private sealed class GatedDelay
    {
        private readonly List<TaskCompletionSource> _gates = new();

        public Func<TimeSpan, CancellationToken, Task> Func => Wait;

        public int Count => _gates.Count;

        private Task Wait(TimeSpan _, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource();
            _gates.Add(tcs);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        public void ReleaseAll()
        {
            foreach (var g in _gates)
            {
                g.TrySetResult();
            }
        }
    }

    /// <summary>An immediate (non-parking) debounce seam — grabs proceed straight to the service.</summary>
    private static Task Immediate(TimeSpan _, CancellationToken ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

    private static SemaphoreSlim ScanGate() => new(3, 3);

    /// <summary>
    /// Build a keyframes-READY row wired to a fake thumbnail service, with an injected debounce seam +
    /// (optional) shared thumbnail gate. Installs a pumpable sync context FIRST so the grabber captures it.
    /// The scan is awaited, so the row's initial cut-point grab has already been kicked on return.
    /// </summary>
    private static async Task<(BulkItemViewModel Row, FakeThumbnailService Thumbs, PumpContext Pump)> BuildReadyRowAsync(
        Func<TimeSpan, CancellationToken, Task> delay,
        double durationSeconds = 60,
        double stepSeconds = 2,
        double introSeconds = 10,
        SemaphoreSlim? thumbnailGate = null,
        FakeThumbnailService? thumbs = null,
        BulkFakeProbe? probe = null)
    {
        var pump = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(pump);

        probe ??= new BulkFakeProbe();
        var duration = TimeSpan.FromSeconds(durationSeconds);
        probe.SetUniform(PathA, duration, stepSeconds);
        thumbs ??= new FakeThumbnailService { ThumbnailFactory = (_, time, _) => $"frame-{(int)time.TotalSeconds}.jpg" };

        var row = new BulkItemViewModel(
            PathA, probe, ScanGate(),
            thumbnails: thumbs,
            thumbnailGate: thumbnailGate,
            thumbnailDebounce: TimeSpan.FromMilliseconds(50),
            thumbnailDelay: delay)
        {
            Duration = duration,
        };
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        await row.StartKeyframeScanAsync();
        return (row, thumbs, pump);
    }

    /// <summary>
    /// Wait for the row's in-flight cut-point grab to finish, then drain the posts it produced.
    ///
    /// <para>T-137: the grab is fire-and-forget in production (a handle move must never block the UI),
    /// so a test has nothing to wait on unless the row exposes it. Waiting on the work rather than on a
    /// timeout is what makes these assertions deterministic under load; the timeout below is a deadlock
    /// guard a healthy run never approaches, not the synchronisation mechanism.</para>
    /// </summary>
    private static void Settle(BulkItemViewModel row, PumpContext pump)
    {
        // The grab swallows its own failures, so this only ever completes normally.
        row.InFlightGrabs.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue(
            "the grab should finish promptly - 30s means it is genuinely stuck, not merely busy");
        pump.PumpSettled();
    }

    // ---- Grab-on-snapped-change (initial + move) --------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task KeyframesResolve_GrabsIntroFrame_AtSnappedTime()
    {
        // intro 11s → snaps to keyframe 10s (step 2). The initial grab fires when the scan resolves.
        var (row, thumbs, pump) = await BuildReadyRowAsync(Immediate, introSeconds: 11);
        using var pumpScope = pump;
        Settle(row, pump);

        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10));
        thumbs.Requests.Should().Contain(r => r.Time == TimeSpan.FromSeconds(10),
            "the intro-end frame is grabbed at the keyframe-snapped time");
        row.IntroThumbnailPath.Should().Be("frame-10.jpg");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task MovingIntroHandle_RegrabsAtNewSnappedTime()
    {
        var (row, thumbs, pump) = await BuildReadyRowAsync(Immediate, introSeconds: 10);
        using var pumpScope = pump;
        Settle(row, pump);
        row.IntroThumbnailPath.Should().Be("frame-10.jpg");

        // Move the intro handle: 21s → snaps to 20s → a fresh grab at the new snapped time.
        row.IntroEnd.Requested = TimeSpan.FromSeconds(21);
        Settle(row, pump);

        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(20));
        row.IntroThumbnailPath.Should().Be("frame-20.jpg", "moving the handle re-grabs at the new snapped cut");
    }

    // ---- Debounce coalesces rapid moves (one grab) ------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task RapidIntroMoves_AreDebounced_ToASingleGrab()
    {
        var delay = new GatedDelay();
        // Build with the initial grab parked in the debounce window (not released yet).
        var (row, thumbs, pump) = await BuildReadyRowAsync(delay.Func, introSeconds: 10);
        using var pumpScope = pump;

        // Two more rapid moves while everything is still parked — each supersedes the prior (latest-wins).
        row.IntroEnd.Requested = TimeSpan.FromSeconds(30); // snaps to 30
        row.IntroEnd.Requested = TimeSpan.FromSeconds(50); // snaps to 50

        delay.ReleaseAll();
        Settle(row, pump);

        // Only the LATEST request survived the debounce and reached ffmpeg — the earlier ones were cancelled.
        thumbs.Requests.Should().ContainSingle().Which.Time.Should().Be(TimeSpan.FromSeconds(50),
            "rapid handle moves coalesce into a single grab at the final snapped cut");
        row.IntroThumbnailPath.Should().Be("frame-50.jpg");
    }

    // ---- Outro thumb appears / clears with the handle ---------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task AddOutro_GrabsOutroFrame_ThenClearOutro_DropsIt()
    {
        var (row, thumbs, pump) = await BuildReadyRowAsync(Immediate, introSeconds: 10);
        using var pumpScope = pump;
        Settle(row, pump);

        row.AddOutro(TimeSpan.FromSeconds(50));
        Settle(row, pump);

        row.HasOutro.Should().BeTrue();
        row.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(50));
        row.OutroThumbnailPath.Should().Be("frame-50.jpg", "the outro-start frame is grabbed when the handle is added");

        row.ClearOutro();

        row.HasOutro.Should().BeFalse();
        row.OutroThumbnailPath.Should().BeNull("clearing the outro drops its frame (the chip hides)");
    }

    // ---- Null grab → null path (placeholder) ------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task NullGrab_LeavesNullPath_ForThePlaceholder()
    {
        var thumbs = new FakeThumbnailService { ThumbnailFactory = null }; // every grab returns null
        var (row, _, pump) = await BuildReadyRowAsync(Immediate, introSeconds: 10, thumbs: thumbs);
        using var pumpScope = pump;
        Settle(row, pump);

        thumbs.GetThumbnailCallCount.Should().BeGreaterThan(0, "the grab was attempted");
        row.IntroThumbnailPath.Should().BeNull("a null grab shows the placeholder chip, not an image");
    }

    // ---- Per-row cancel on Remove/Clear -----------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task CancelScan_CancelsInFlightGrab_NoPathSet_NeverReachesService()
    {
        var delay = new GatedDelay();
        // The initial grab is parked in the debounce window.
        var (row, thumbs, pump) = await BuildReadyRowAsync(delay.Func, introSeconds: 10);
        using var pumpScope = pump;

        // Remove/Clear routes through CancelScan → cancels the grabber CTS → the parked debounce faults.
        row.CancelScan();
        delay.ReleaseAll();
        Settle(row, pump);

        thumbs.Requests.Should().BeEmpty("a cancelled (removed) row's grab never reaches ffmpeg");
        row.IntroThumbnailPath.Should().BeNull("no frame is committed after cancel");
    }

    // ---- Clear cancels a PARKED outro grab (I64 — the outro analog of CancelScan) ------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ClearOutro_CancelsParkedOutroGrab_NeverReachesService()
    {
        var delay = new GatedDelay();
        // Keyframes-ready row; the initial intro grab is parked in the debounce window.
        var (row, thumbs, pump) = await BuildReadyRowAsync(delay.Func, introSeconds: 10);
        using var pumpScope = pump;

        // Add an outro at a keyframe (50s snaps to 50) → its grab PARKS in the debounce window too.
        row.AddOutro(TimeSpan.FromSeconds(50));

        // Clear the outro WHILE its grab is still parked → ClearOutro cancels the outro grabber's CTS,
        // so the parked debounce faults (latest-wins) and the superseded grab is dropped before ffmpeg —
        // the untested cancel-of-a-parked-grab path (contrast the intro grabber's CancelScan test above).
        row.ClearOutro();

        // Release every parked gate + pump to completion: the intro grab resumes, the cancelled outro grab does not.
        delay.ReleaseAll();
        Settle(row, pump);

        // PERF (cancellation-honored + no-I/O-on-hot-path): the superseded outro grab NEVER reaches the
        // service — no request was ever recorded for the outro's snapped time (50s).
        thumbs.Requests.Should().NotContain(r => r.Time == TimeSpan.FromSeconds(50),
            "clearing the outro cancels its parked grab before it reaches ffmpeg (the outro analog of CancelScan)");

        // CORRECTNESS: the outro handle is gone and its frame is dropped.
        row.HasOutro.Should().BeFalse("clearing the outro drops the handle");
        row.OutroThumbnailPath.Should().BeNull("the cancelled outro grab commits no frame");
    }

    // ---- Bounded concurrency across a batch --------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ManyRows_ShareOneGate_BoundsConcurrentGrabsToThree()
    {
        // One shared thumbnail gate + one service whose grabs PARK (so in-flight grabs pile up) — exactly
        // how BulkCutViewModel wires a batch. Eight rows each fire an initial intro grab; the gate must cap
        // concurrent ffmpeg grabs at 3 no matter how many rows resolve at once.
        using var pump = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(pump);

        var gate = new SemaphoreSlim(3, 3);
        var thumbs = new FakeThumbnailService
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ThumbnailFactory = (_, time, _) => $"frame-{(int)time.TotalSeconds}.jpg",
        };

        var probe = new BulkFakeProbe();
        var rows = new List<BulkItemViewModel>();
        for (var i = 0; i < 8; i++)
        {
            var path = $@"C:\videos\clip{i}.mp4";
            probe.SetUniform(path, TimeSpan.FromSeconds(60), 2);
            var row = new BulkItemViewModel(
                path, probe, ScanGate(),
                thumbnails: thumbs,
                thumbnailGate: gate,
                thumbnailDebounce: TimeSpan.FromMilliseconds(1),
                thumbnailDelay: Immediate)
            {
                Duration = TimeSpan.FromSeconds(60),
            };
            row.IntroEnd.Requested = TimeSpan.FromSeconds(10 + i); // distinct snapped times → distinct grabs
            rows.Add(row);
        }

        // Resolve every row's keyframes → each fires its initial intro grab; the parked grabs pile up.
        await Task.WhenAll(rows.Select(r => r.StartKeyframeScanAsync()));

        // Give the piled-up grabs a moment to reach the gate, then assert the bound never exceeded 3.
        var spun = SpinUntil(() => thumbs.CurrentConcurrent >= 3, TimeSpan.FromMilliseconds(500));
        spun.Should().BeTrue("at least 3 grabs should be in flight against the gate");
        thumbs.PeakConcurrent.Should().BeLessThanOrEqualTo(3, "the shared gate caps concurrent ffmpeg frame grabs");

        // Release the parked grabs so they all drain — the bound still held throughout.
        thumbs.Gate!.TrySetResult();
        var drained = SpinUntil(() => thumbs.GetThumbnailCallCount >= 8, TimeSpan.FromSeconds(2));
        drained.Should().BeTrue("every row's grab eventually runs, three at a time");
        thumbs.PeakConcurrent.Should().BeLessThanOrEqualTo(3);
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(2);
        }

        return condition();
    }
}
