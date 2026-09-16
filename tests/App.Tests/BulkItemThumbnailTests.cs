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

        // Wait for the piled-up grabs to actually reach the gate, then assert the bound never exceeded 3.
        //
        // T-159: the original was `SpinUntil(() => thumbs.CurrentConcurrent >= 3, 500ms)` — a race, not a
        // wait. On a loaded machine the grabs had not arrived inside the window and the test failed for
        // being early rather than for the bound being wrong.
        //
        // T-159 follow-up: the first fix awaited the signal, and that was WRONG HERE. This test installs
        // a PumpContext, whose Post() only enqueues — continuations run when someone calls Drain(). An
        // `await` therefore parks a continuation in a queue nobody drains, and the test hangs forever,
        // taking the other six in this class with it and leaving the test host unable to exit.
        //
        // So: keep the deterministic SIGNAL (set inside the same lock that counts the grabs, so there is
        // no sampling window) but observe it by SPINNING, which needs no pump. The timeout is a deadlock
        // guard, not a timing assumption.
        var reached = thumbs.WhenConcurrentReaches(3);
        var spun = SpinUntil(() => reached.IsCompleted, TimeSpan.FromSeconds(30));
        spun.Should().BeTrue("at least 3 grabs should be in flight against the gate");
        thumbs.PeakConcurrent.Should().BeLessThanOrEqualTo(3, "the shared gate caps concurrent ffmpeg frame grabs");

        // Release the parked grabs so they all drain — the bound still held throughout.
        // T-159: this was the wait that actually flaked. Releasing the gate and giving eight queued
        // grabs TWO wall-clock seconds to all arrive is a race against machine load, not an assertion
        // about the code — it lost whenever the run followed a full build. Now signalled from the same
        // lock that counts them; the timeout is a deadlock guard only.
        thumbs.Gate!.TrySetResult();
        var draining = thumbs.WhenCallCountReaches(8);
        var drained = SpinUntil(() => draining.IsCompleted, TimeSpan.FromSeconds(30));
        drained.Should().BeTrue("every row's grab eventually runs, three at a time");
        thumbs.PeakConcurrent.Should().BeLessThanOrEqualTo(3);
    }

    // ---- T-174 (G-057 decision 1): the chip shows the frame the run will cut at ---------------------
    //
    // A frame is requested only at the handle's EFFECTIVE cut time — Snapped in Lossless, Requested under Exact —
    // and never while that time is provisional: no known duration, or Lossless with the snap still pending.
    //
    // Two harness rules (T-137 / T-159). Without the pump, assert on Requests only: a committed path arrives
    // through Progress<T>, after InFlightGrabs completes. And a held scan cannot be awaited under the pump — so a
    // test that needs both installs the pump before constructing the row (the grabbers capture it) and restores
    // the previous context before starting the scan (the scan's continuation does not park in the pump).

    private static FakeThumbnailService FrameService() =>
        new() { ThumbnailFactory = (_, time, _) => $"frame-{(int)time.TotalSeconds}.jpg" };

    /// <summary>A row on the 2 s grid whose keyframe scan is held open. No pump: assert on Requests only.</summary>
    private static (BulkItemViewModel Row, FakeThumbnailService Thumbs, BulkFakeProbe Probe, Task Scan) HeldScanRow(bool exact = false)
    {
        var probe = new BulkFakeProbe();
        probe.SetUniform(PathA, TimeSpan.FromSeconds(60), 2);
        probe.GatedPaths.Add(PathA);
        var thumbs = FrameService();
        var row = new BulkItemViewModel(PathA, probe, ScanGate(), thumbnails: thumbs, thumbnailDelay: Immediate)
        {
            Duration = TimeSpan.FromSeconds(60),
        };
        if (exact)
        {
            row.SetExactCut(true);
        }

        var scan = row.StartKeyframeScanAsync();
        row.KeyframesReady.Should().BeFalse("precondition: the scan is held open");
        return (row, thumbs, probe, scan);
    }

    /// <summary>Wait for the row's grabs to settle as REQUESTS (no pump, so no path is asserted).</summary>
    private static void SettleRequests(BulkItemViewModel row) =>
        row.InFlightGrabs.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the grab should finish promptly");

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task Lossless_ADragDuringTheScan_GrabsNothing_ThenOnceAtTheSnappedTime_WhenTheScanLands()
    {
        var (row, thumbs, probe, scan) = HeldScanRow();

        row.IntroEnd.Requested = S(11); // off-keyframe: provisional 11 s, snapped 10 s
        SettleRequests(row);
        probe.ReleaseScans();
        await scan;
        SettleRequests(row);

        thumbs.Requests.Should().ContainSingle("a Lossless chip waits for the snap and grabs once")
            .Which.Time.Should().Be(S(10), "at the snapped time the run will cut at");
        thumbs.Requests.Should().NotContain(r => r.Time == S(11), "11 s was only ever provisional");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task Lossless_AnOutroAddedDuringTheScan_GrabsNothingUntilTheLand_ThenOnceAtTheSnappedOutro()
    {
        var (row, thumbs, probe, scan) = HeldScanRow();

        row.AddOutro(S(51));
        SettleRequests(row);
        thumbs.Requests.Should().BeEmpty("the new outro's cut time is provisional until the scan lands");

        probe.ReleaseScans();
        await scan;
        SettleRequests(row);

        thumbs.Requests.Where(r => r.Time != TimeSpan.Zero).Should().ContainSingle("one outro grab, at the land")
            .Which.Time.Should().Be(S(50));
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task Lossless_ARowRemovedDuringItsScan_RequestsNothing()
    {
        var (row, thumbs, probe, scan) = HeldScanRow();

        row.IntroEnd.Requested = S(11);
        row.CancelScan();
        probe.ReleaseScans();
        await scan;
        SettleRequests(row);

        thumbs.Requests.Should().BeEmpty("nothing reaches the frame service for a row that left during its scan");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task Lossless_AnOutroClearedDuringTheScan_IsNeverGrabbed()
    {
        var (row, thumbs, probe, scan) = HeldScanRow();

        row.AddOutro(S(51));
        row.ClearOutro();
        probe.ReleaseScans();
        await scan;
        SettleRequests(row);

        thumbs.Requests.Should().NotContain(r => r.Time == S(51) || r.Time == S(50), "the outro is gone by the land");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task Lossless_WhenTheScanFails_OneIdentityGrab()
    {
        var (row, thumbs, probe, scan) = HeldScanRow();

        row.IntroEnd.Requested = S(11);
        SettleRequests(row);
        probe.ScanGate.TrySetException(new System.IO.IOException("scan failed"));
        await scan; // the failure branch swallows it
        SettleRequests(row);

        thumbs.Requests.Should().ContainSingle("the failed scan resolves the snap to identity, and grabs once")
            .Which.Time.Should().Be(S(11));
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task Exact_ADragDuringTheScan_GrabsAtTheRequestedTime_AndNeverAtTheSnapped()
    {
        var (row, thumbs, probe, scan) = HeldScanRow(exact: true);

        row.IntroEnd.Requested = S(11);
        SettleRequests(row);
        thumbs.Requests.Should().Contain(r => r.Time == S(11), "under Exact the cut is the request, known at once");

        probe.ReleaseScans();
        await scan;
        SettleRequests(row);

        thumbs.Requests.Should().NotContain(r => r.Time == S(10), "Exact never cuts at the keyframe, so never shows it");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task Exact_OnAReadyRow_CommitsTheRequestedFrame_AndADragWithinOneGopRegrabs()
    {
        var (row, thumbs, pump) = await BuildReadyRowAsync(Immediate, stepSeconds: 4, introSeconds: 9);
        using var pumpScope = pump;
        Settle(row, pump);
        row.IntroEnd.Snapped.Should().Be(S(8), "precondition: 9 s snaps to 8 s on the 4 s grid");
        var before = thumbs.Requests.Count;

        row.SetExactCut(true);
        Settle(row, pump);

        thumbs.Requests.Skip(before).Should().ContainSingle().Which.Time.Should().Be(S(9));
        row.IntroThumbnailPath.Should().Be("frame-9.jpg", "the chip shows where Exact cuts, not the keyframe");

        before = thumbs.Requests.Count;
        row.IntroEnd.Requested = S(9.8);
        Settle(row, pump);

        row.IntroEnd.Snapped.Should().Be(S(8), "precondition: still the same keyframe, so Snapped did not change");
        thumbs.Requests.Skip(before).Should().ContainSingle("under Exact every move of the request moves the cut")
            .Which.Time.Should().Be(S(9.8));
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task AFlip_RegrabsTheIntro_WhenItsCutTimeMoves_BothWays()
    {
        var (row, thumbs, pump) = await BuildReadyRowAsync(Immediate, introSeconds: 11);
        using var pumpScope = pump;
        Settle(row, pump);
        row.IntroEnd.Snapped.Should().Be(S(10), "precondition: a snap offset of one second");

        var before = thumbs.Requests.Count;
        row.SetExactCut(true);
        Settle(row, pump);
        thumbs.Requests.Skip(before).Should().ContainSingle().Which.Time.Should().Be(S(11));

        before = thumbs.Requests.Count;
        row.SetExactCut(false);
        Settle(row, pump);
        thumbs.Requests.Skip(before).Should().ContainSingle().Which.Time.Should().Be(S(10));
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task AFlip_RegrabsTheOutro_WhenItsCutTimeMoves_BothWays()
    {
        var (row, thumbs, pump) = await BuildReadyRowAsync(Immediate, introSeconds: 10);
        using var pumpScope = pump;
        row.AddOutro(S(51));
        Settle(row, pump);
        row.OutroStart!.Snapped.Should().Be(S(50), "precondition: the outro has a snap offset; the intro has none");

        var before = thumbs.Requests.Count;
        row.SetExactCut(true);
        Settle(row, pump);
        thumbs.Requests.Skip(before).Should().ContainSingle().Which.Time.Should().Be(S(51));

        before = thumbs.Requests.Count;
        row.SetExactCut(false);
        Settle(row, pump);
        thumbs.Requests.Skip(before).Should().ContainSingle().Which.Time.Should().Be(S(50));
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task AFlip_OnAHandleWithNoSnapOffset_RequestsNothing()
    {
        var (row, thumbs, pump) = await BuildReadyRowAsync(Immediate, introSeconds: 10);
        using var pumpScope = pump;
        Settle(row, pump);
        row.IntroEnd.Snapped.Should().Be(row.IntroEnd.Requested, "precondition: the intro sits on a keyframe");

        var before = thumbs.Requests.Count;
        row.SetExactCut(true);
        row.SetExactCut(false);
        Settle(row, pump);

        thumbs.Requests.Count.Should().Be(before, "the cut time did not move, so the frame is already right");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task AFlipToExact_OnAScanningRow_RequestsTheRequestedFrame_BeforeTheLand()
    {
        var (row, thumbs, probe, scan) = HeldScanRow();
        row.IntroEnd.Requested = S(11);
        SettleRequests(row);
        thumbs.Requests.Should().BeEmpty("precondition: Lossless and still pending");

        row.SetExactCut(true);
        SettleRequests(row);

        thumbs.Requests.Should().ContainSingle("a pending handle's grab time becomes known when Exact makes the request the cut")
            .Which.Time.Should().Be(S(11));

        probe.ReleaseScans();
        await scan;
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public void AFlip_OnARowWithNoDuration_RequestsNothing()
    {
        var probe = new BulkFakeProbe();
        var thumbs = FrameService();
        var row = new BulkItemViewModel(PathA, probe, ScanGate(), thumbnails: thumbs, thumbnailDelay: Immediate);
        row.IntroEnd.Requested = S(11);

        row.SetExactCut(true);
        row.SetExactCut(false);
        SettleRequests(row);

        thumbs.Requests.Should().BeEmpty("with no known duration there is no cut time to show in either precision");
    }

    /// <summary>Exact on, held scan, a committed frame at 11 s; flipping to Lossless makes that time provisional.</summary>
    private static (BulkItemViewModel Row, FakeThumbnailService Thumbs, BulkFakeProbe Probe, PumpContext Pump) ExactScanningRowUnderThePump()
    {
        var previous = SynchronizationContext.Current;
        var pump = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(pump); // the grabbers capture the pump...

        var probe = new BulkFakeProbe();
        probe.SetUniform(PathA, TimeSpan.FromSeconds(60), 2);
        probe.GatedPaths.Add(PathA);
        var thumbs = FrameService();
        var row = new BulkItemViewModel(PathA, probe, ScanGate(), thumbnails: thumbs, thumbnailDelay: Immediate)
        {
            Duration = TimeSpan.FromSeconds(60),
        };

        SynchronizationContext.SetSynchronizationContext(previous); // ...and the held scan does not
        row.SetExactCut(true);
        _ = row.StartKeyframeScanAsync();
        row.IntroEnd.Requested = S(11);
        return (row, thumbs, probe, pump);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public void AFlipThatMakesTheCutTimeProvisional_ClearsTheChip_AndRequestsNothing()
    {
        var (row, thumbs, probe, pump) = ExactScanningRowUnderThePump();
        using var pumpScope = pump;
        row.InFlightGrabs.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        pump.PumpSettled();
        row.IntroThumbnailPath.Should().Be("frame-11.jpg", "precondition: under Exact the chip shows the requested cut");
        var before = thumbs.Requests.Count;

        row.SetExactCut(false); // Lossless, snap still pending: the cut time is provisional again

        row.IntroThumbnailPath.Should().BeNull("the chip goes back to the placeholder rather than show a frame Lossless will not cut at");
        pump.PumpSettled();
        row.IntroThumbnailPath.Should().BeNull();
        thumbs.Requests.Count.Should().Be(before, "a provisional time is never requested");

        probe.ReleaseScans();
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public void AGrabThatFinishedJustBeforeTheFlip_NeverCommitsItsFrame()
    {
        var (row, _, probe, pump) = ExactScanningRowUnderThePump();
        using var pumpScope = pump;
        row.InFlightGrabs.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        // Deliberately NOT pumped: the finished grab's result is still queued when the flip cancels it.

        row.SetExactCut(false);
        pump.PumpSettled();

        row.IntroThumbnailPath.Should().BeNull("a cancelled grab never commits, even one that had already finished");

        probe.ReleaseScans();
    }

    [Theory]
    [Trait("serves-spec", "SPEC-011")]
    [InlineData(false, 50)]
    [InlineData(true, 51)]
    public async Task AnOutroAddedBeforeTheProbe_GetsItsFrameAtTheLand(bool exact, double expectedSeconds)
    {
        var probe = new BulkFakeProbe();
        probe.SetUniform(PathA, TimeSpan.FromSeconds(60), 2);
        var thumbs = FrameService();
        var row = new BulkItemViewModel(PathA, probe, ScanGate(), thumbnails: thumbs, thumbnailDelay: Immediate);
        if (exact)
        {
            row.SetExactCut(true);
        }

        row.AddOutro(S(51));
        SettleRequests(row);
        thumbs.Requests.Should().BeEmpty("with no duration yet there is no cut time to grab");

        row.Duration = TimeSpan.FromSeconds(60);
        probe.GatedPaths.Add(PathA);
        var scan = row.StartKeyframeScanAsync();
        probe.ReleaseScans();
        await scan;
        SettleRequests(row);

        thumbs.Requests.Where(r => r.Time != TimeSpan.Zero).Should().ContainSingle("one outro grab, at the land")
            .Which.Time.Should().Be(S(expectedSeconds));
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task MovingOutroHandle_RegrabsAtNewSnappedTime()
    {
        var (row, thumbs, pump) = await BuildReadyRowAsync(Immediate, introSeconds: 10);
        using var pumpScope = pump;
        row.AddOutro(S(50));
        Settle(row, pump);
        row.OutroThumbnailPath.Should().Be("frame-50.jpg");

        row.OutroStart!.Requested = S(41); // snaps to 40
        Settle(row, pump);

        row.OutroStart.Snapped.Should().Be(S(40));
        thumbs.Requests.Should().Contain(r => r.Time == S(40));
        row.OutroThumbnailPath.Should().Be("frame-40.jpg", "moving the outro handle re-grabs at the new snapped cut");
    }

    [Theory]
    [Trait("serves-spec", "SPEC-011")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ARowWithNoDuration_RequestsNoFrame(bool exact, bool loadFailed)
    {
        var probe = new BulkFakeProbe();
        var thumbs = FrameService();
        var row = new BulkItemViewModel(PathA, probe, ScanGate(), thumbnails: thumbs, thumbnailDelay: Immediate);
        if (loadFailed)
        {
            row.MarkLoadFailed(); // Duration stays null: setting it would make the row keyframes-ready
        }

        if (exact)
        {
            row.SetExactCut(true);
        }

        row.IntroEnd.Requested = S(11);
        row.AddOutro(S(51));
        SettleRequests(row);

        thumbs.Requests.Should().BeEmpty("a row whose file has not been read, or failed to load, has no cut time to show");
    }

    /// <summary>
    /// Spin until <paramref name="condition"/> holds, or the timeout expires.
    ///
    /// <para><b>Deliberately a spin, not an await</b> (T-159). This class installs a
    /// <see cref="PumpContext"/> whose <c>Post</c> only enqueues — a continuation runs when the test
    /// calls <c>Drain()</c>. Awaiting here would park the continuation in a queue nobody drains and hang
    /// the test forever; that is exactly what the first version of the T-159 fix did, and it took the
    /// whole class down with it.</para>
    ///
    /// <para>What T-159 actually changed is the CONDITION, not the mechanism: callers now spin on a
    /// signal the fake sets inside the same lock that updates its counters, instead of sampling a
    /// mutable counter against a short wall-clock deadline. The signal removes the race; the spin keeps
    /// it usable under the pump; the timeout is a deadlock guard rather than a timing assumption.</para>
    /// </summary>
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
