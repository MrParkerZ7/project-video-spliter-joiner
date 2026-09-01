using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for <see cref="ThumbnailPreviewViewModel"/> (T-078) over a fake
/// <see cref="IThumbnailService"/> — no WPF, no ffmpeg. A pumpable single-threaded
/// <see cref="SynchronizationContext"/> is installed so the VM's <c>Progress&lt;T&gt;</c> result-marshal
/// posts deterministically (the test drains it explicitly). The debounce delay is an immediate no-op
/// seam so hover → grab resolves within the pumped context. Verified: latest-wins cancels the prior
/// request, <c>MouseLeave</c> hides + drops the frame, a null path shows nothing, and load/clear sweep
/// the temp cache.
/// </summary>
public sealed class ThumbnailPreviewViewModelTests
{
    // ---- Fake service -----------------------------------------------------------------------

    /// <summary>
    /// Records each grab (path + time + the token it was given) and returns a scripted result. Each
    /// call's returned path is the requested time's <c>ss</c> value so a test can tell which hover a
    /// resolved path belongs to; a grab observes its token so a cancelled (superseded) request is
    /// detectable. <see cref="Clear"/>/<see cref="ClearAll"/> calls are recorded for the sweep tests.
    /// </summary>
    private sealed class FakeThumbnailService : IThumbnailService
    {
        public List<TimeSpan> Requests { get; } = new();

        public List<CancellationToken> Tokens { get; } = new();

        public List<string> Cleared { get; } = new();

        public int ClearAllCount { get; private set; }

        /// <summary>When set, a grab returns null (simulating a failed/absent frame).</summary>
        public bool ReturnNull { get; set; }

        public Task<string?> GetThumbnailAsync(string inputPath, TimeSpan time, int width, CancellationToken ct)
        {
            Requests.Add(time);
            Tokens.Add(ct);

            if (ReturnNull)
            {
                return Task.FromResult<string?>(null);
            }

            // Encode the requested seconds into the path so the test can match a resolved path to a hover.
            var path = $"frame-{(int)time.TotalSeconds}.jpg";
            return Task.FromResult<string?>(path);
        }

        public void Clear(string inputPath) => Cleared.Add(inputPath);

        public void ClearAll() => ClearAllCount++;
    }

    // ---- Pumpable single-threaded sync context ----------------------------------------------

    /// <summary>
    /// A minimal single-threaded <see cref="SynchronizationContext"/> whose queued callbacks (the VM's
    /// <c>Progress&lt;T&gt;</c> posts) are drained on demand by <see cref="Drain"/>, so result-marshaling
    /// is deterministic in the test.
    /// </summary>
    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback D, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>Drain the currently-queued callbacks; returns how many ran.</summary>
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
        /// Drain the posts that the completed grab produced.
        ///
        /// <para>T-137: this used to spin-drain against a 300ms wall-clock deadline, because the grab
        /// continuation resumes on a thread-pool thread and the result post therefore arrives some
        /// time after the debounce gate is released. Under a solution-level run the pool is saturated
        /// by the other assembly, the continuation was not always scheduled inside 300ms, and the test
        /// failed with an empty request list roughly once every five runs. The caller now awaits the
        /// grab itself before draining, so by the time this runs there is nothing left to wait for.</para>
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
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(null);
    }

    /// <summary>
    /// A controllable debounce seam: each hover's debounce await parks on a fresh
    /// <see cref="TaskCompletionSource"/> until the test <see cref="Release"/>s it, so a request can be
    /// left "in the debounce window" while a newer hover supersedes it — exactly the coalesce race. The
    /// wait completes on release, or faults (cancelled) if the request's token trips first (the
    /// latest-wins cancel), so a superseded wait never proceeds to a grab.
    /// </summary>
    private sealed class GatedDelay
    {
        private readonly List<TaskCompletionSource> _gates = new();

        public Func<TimeSpan, CancellationToken, Task> Func => Wait;

        /// <summary>How many debounce waits have started (one per hover that reached the delay).</summary>
        public int Count => _gates.Count;

        private Task Wait(TimeSpan _, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource();
            _gates.Add(tcs);
            // A cancel (superseding hover) faults the wait so the request never proceeds to a grab.
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        /// <summary>Release the Nth debounce wait so its request proceeds to the grab.</summary>
        public void Release(int index) => _gates[index].TrySetResult();

        /// <summary>Release every not-yet-released debounce wait.</summary>
        public void ReleaseAll()
        {
            foreach (var g in _gates)
            {
                g.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Build a VM with a fake service + a gated debounce delay, install a pumpable sync context, and
    /// return them so a test can drive hovers, release the debounce, and drain the result posts.
    /// </summary>
    private static (ThumbnailPreviewViewModel Vm, FakeThumbnailService Service, PumpContext Pump, GatedDelay Delay) Build()
    {
        var pump = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(pump);

        var service = new FakeThumbnailService();
        var delay = new GatedDelay();
        var vm = new ThumbnailPreviewViewModel(service, TimeSpan.FromMilliseconds(60), delay.Func);
        return (vm, service, pump, delay);
    }

    /// <summary>Load a file into the VM (path + duration) so hovers issue grabs.</summary>
    private static void Load(ThumbnailPreviewViewModel vm, double durationSeconds = 100)
        => vm.SetInput("C:\\clip.mp4", TimeSpan.FromSeconds(durationSeconds));

    /// <summary>
    /// Release every parked debounce wait so each surviving request proceeds to its grab, WAIT for that
    /// grab to actually finish, then drain the pumpable context so the VM's <c>Progress&lt;T&gt;</c>
    /// result posts run. A superseded (cancelled) wait faults instead of proceeding, so only the latest
    /// request commits a path.
    ///
    /// <para>T-137: waiting on <see cref="ThumbnailPreviewViewModel.InFlightGrab"/> is what makes this
    /// deterministic. The grab is fire-and-forget in production (a hover must not block the UI), so the
    /// test previously had nothing to wait on but a timeout - which is a race, not a synchronisation,
    /// and it lost about once in five solution-level runs. The timeout here is a deadlock guard that a
    /// healthy run never approaches, NOT the mechanism.</para>
    /// </summary>
    private static void Settle(GatedDelay delay, PumpContext pump, ThumbnailPreviewViewModel vm)
    {
        delay.ReleaseAll();
        WaitForGrab(vm);
        pump.PumpSettled();
    }

    /// <summary>Block until the VM's most recent grab has run to completion (or been cancelled).</summary>
    private static void WaitForGrab(ThumbnailPreviewViewModel vm)
    {
        // GrabAsync swallows its own failures, so this only ever completes normally.
        vm.InFlightGrab.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue(
            "the grab should finish promptly - 30s means it is genuinely stuck, not merely busy");
    }

    // ---- Visibility -------------------------------------------------------------------------

    [Fact]
    public void MouseEnter_WithLoadedFile_ShowsPopup()
    {
        var (vm, _, pump, _) = Build();
        using var pumpScope = pump;
        Load(vm);

        vm.MouseEnter();

        vm.IsThumbnailVisible.Should().BeTrue();
    }

    [Fact]
    public void MouseEnter_WithNoFile_DoesNotShowPopup()
    {
        var (vm, _, pump, _) = Build();
        using var pumpScope = pump;

        vm.MouseEnter();

        vm.IsThumbnailVisible.Should().BeFalse("no file is loaded, so there is nothing to preview");
    }

    [Fact]
    public void MouseLeave_HidesPopupAndDropsFrame()
    {
        var (vm, _, pump, delay) = Build();
        using var pumpScope = pump;
        Load(vm);
        vm.MouseEnter();

        vm.UpdateHover(TimeSpan.FromSeconds(10), offsetX: 40);
        Settle(delay, pump, vm);
        vm.HoverThumbnailPath.Should().NotBeNull();

        vm.MouseLeave();

        vm.IsThumbnailVisible.Should().BeFalse();
        vm.HoverThumbnailPath.Should().BeNull("leaving the bar drops the current frame");
    }

    [Fact]
    public void UpdateHover_AlwaysUpdatesTimeLabelAndOffset()
    {
        var (vm, _, pump, _) = Build();
        using var pumpScope = pump;
        Load(vm);

        vm.UpdateHover(TimeSpan.FromSeconds(65), offsetX: 123);

        vm.HoverTime.Should().Be(TimeSpan.FromSeconds(65));
        vm.HoverOffsetX.Should().Be(123);
        vm.HoverTimeText.Should().Be("01:05");
    }

    // ---- Grab + latest-wins coalesce --------------------------------------------------------

    [Fact]
    public void UpdateHover_ResolvesFrameForHoveredTime()
    {
        var (vm, service, pump, delay) = Build();
        using var pumpScope = pump;
        Load(vm);
        vm.MouseEnter();

        vm.UpdateHover(TimeSpan.FromSeconds(30), offsetX: 60);
        Settle(delay, pump, vm);

        service.Requests.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(30));
        vm.HoverThumbnailPath.Should().Be("frame-30.jpg");
        vm.HasThumbnail.Should().BeTrue();
    }

    [Fact]
    public void UpdateHover_LatestWins_CancelsPriorRequest()
    {
        var (vm, service, pump, delay) = Build();
        using var pumpScope = pump;
        Load(vm);
        vm.MouseEnter();

        // Two hovers while the first is still parked in the debounce window: the second must cancel the
        // first's request (latest-wins) so the superseded wait faults and never reaches the service.
        vm.UpdateHover(TimeSpan.FromSeconds(10), offsetX: 20);
        vm.UpdateHover(TimeSpan.FromSeconds(50), offsetX: 100);
        Settle(delay, pump, vm);

        // Only the SECOND request ever reached the service — the first was cancelled mid-debounce.
        service.Requests.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(50),
            "the prior request is superseded before it grabs");

        // Only the newest hover's frame is shown.
        vm.HoverThumbnailPath.Should().Be("frame-50.jpg");
    }

    [Fact]
    public void SupersededRequest_NeverClobbersNewer()
    {
        // Three fast hovers — only the last survives the debounce; the earlier two are cancelled.
        var (vm, service, pump, delay) = Build();
        using var pumpScope = pump;
        Load(vm);
        vm.MouseEnter();

        vm.UpdateHover(TimeSpan.FromSeconds(10), offsetX: 20);
        vm.UpdateHover(TimeSpan.FromSeconds(40), offsetX: 80);
        vm.UpdateHover(TimeSpan.FromSeconds(80), offsetX: 160);
        Settle(delay, pump, vm);

        service.Requests.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(80));
        vm.HoverThumbnailPath.Should().Be("frame-80.jpg", "only the latest hover's frame survives");
    }

    // ---- Null / failure ---------------------------------------------------------------------

    [Fact]
    public void NullPath_ShowsNothing()
    {
        var (vm, service, pump, delay) = Build();
        using var pumpScope = pump;
        service.ReturnNull = true;
        Load(vm);
        vm.MouseEnter();

        vm.UpdateHover(TimeSpan.FromSeconds(15), offsetX: 30);
        Settle(delay, pump, vm);

        vm.HoverThumbnailPath.Should().BeNull("a failed grab shows no image");
        vm.HasThumbnail.Should().BeFalse();
    }

    [Fact]
    public void ResultAfterLeave_IsDropped()
    {
        var (vm, _, pump, delay) = Build();
        using var pumpScope = pump;
        Load(vm);
        vm.MouseEnter();

        vm.UpdateHover(TimeSpan.FromSeconds(25), offsetX: 50);
        // Leave BEFORE releasing/draining — the resolved frame must not re-show after leave.
        vm.MouseLeave();
        Settle(delay, pump, vm);

        vm.HoverThumbnailPath.Should().BeNull();
        vm.IsThumbnailVisible.Should().BeFalse();
    }

    [Fact]
    public void UpdateHover_WithNoFile_DoesNotGrab()
    {
        var (vm, service, pump, _) = Build();
        using var pumpScope = pump;
        // No Load — no input path set.

        vm.UpdateHover(TimeSpan.FromSeconds(10), offsetX: 20);

        service.Requests.Should().BeEmpty("with no file there is nothing to grab");
        // The label still tracks the cursor even without a file.
        vm.HoverTime.Should().Be(TimeSpan.FromSeconds(10));
    }

    // ---- Cleanup on load / clear ------------------------------------------------------------

    [Fact]
    public void SetInput_SweepsPreviousFilesCache()
    {
        var (vm, service, pump, _) = Build();
        using var pumpScope = pump;
        vm.SetInput("C:\\first.mp4", TimeSpan.FromSeconds(30));

        // Loading a NEW file sweeps the previous file's temp thumbnails.
        vm.SetInput("C:\\second.mp4", TimeSpan.FromSeconds(40));

        service.Cleared.Should().Contain("C:\\first.mp4");
    }

    [Fact]
    public void Clear_SweepsCacheHidesAndDropsFrame()
    {
        var (vm, service, pump, delay) = Build();
        using var pumpScope = pump;
        Load(vm);
        vm.MouseEnter();
        vm.UpdateHover(TimeSpan.FromSeconds(10), offsetX: 20);
        Settle(delay, pump, vm);

        vm.Clear();

        service.Cleared.Should().Contain("C:\\clip.mp4");
        vm.IsThumbnailVisible.Should().BeFalse();
        vm.HoverThumbnailPath.Should().BeNull();
    }

    [Fact]
    public void IsThumbnailVisible_FalseUntilDurationKnown()
    {
        var (vm, _, pump, _) = Build();
        using var pumpScope = pump;
        // Input set but duration unknown (null) — the popup stays hidden until duration arrives.
        vm.SetInput("C:\\clip.mp4", duration: null);
        vm.MouseEnter();

        vm.IsThumbnailVisible.Should().BeFalse("no duration → cursor-X can't map to a time yet");

        vm.SetDuration(TimeSpan.FromSeconds(50));
        vm.MouseEnter();

        vm.IsThumbnailVisible.Should().BeTrue();
    }
}
