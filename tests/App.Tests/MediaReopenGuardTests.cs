using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-080 regression tests for <see cref="MediaReopenGuard"/> — the WPF-free core of the Close→Open
/// lifecycle fix. These model the exact crash sequence in headless terms: after a completed op the
/// user hits Clear (a fire-and-forget FFME Close leaves the element mid-close), then loads a new
/// video (Open). Issuing Open while the element is still closing is the native-crash spot; the guard
/// must DEFER the open until the element settles, DROP a superseded open, and never crash on a stuck
/// element. A scripted <see cref="FakeReopenTarget"/> drives the transitional→settled transition and
/// an injected delay/clock make the settle loop deterministic (no wall-clock waits).
/// </summary>
public sealed class MediaReopenGuardTests
{
    // ---- Fakes ------------------------------------------------------------------------------

    /// <summary>
    /// Scriptable element-state seam. <see cref="IsReopenable"/> starts false (mid-close) and flips
    /// true when the test calls <see cref="Settle"/>, modelling FFME's async Close settling. Each read
    /// of <see cref="IsReopenable"/> is counted so a test can assert the guard actually polled.
    /// </summary>
    private sealed class FakeReopenTarget : IReopenTarget
    {
        private volatile bool _reopenable;
        private int _reads;

        public bool IsReopenable
        {
            get
            {
                Interlocked.Increment(ref _reads);
                return _reopenable;
            }
        }

        public bool IsDetached { get; set; }

        public int Reads => _reads;

        /// <summary>Model the async Close having settled — the element is now safe to reopen.</summary>
        public void Settle() => _reopenable = true;

        /// <summary>Model a fresh Close/Open having put the element back into a transitional state.</summary>
        public void Unsettle() => _reopenable = false;
    }

    /// <summary>A controllable monotonic clock + delay so the settle loop runs with no real waits.</summary>
    private sealed class FakeTiming
    {
        private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Every delay call, in order — lets a test advance state between polls.</summary>
        public List<TimeSpan> Delays { get; } = new();

        /// <summary>Optional hook invoked on each delay, before it returns (drive the target here).</summary>
        public Action? OnDelay { get; set; }

        public DateTime Now => _now;

        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            Delays.Add(d);
            _now += d; // each poll advances the clock so a stuck target eventually times out
            OnDelay?.Invoke();
            return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
        }
    }

    private static MediaReopenGuard Build(
        IReopenTarget target,
        FakeTiming timing,
        TimeSpan? settleTimeout = null,
        TimeSpan? poll = null) =>
        new(
            target,
            settleTimeout ?? TimeSpan.FromSeconds(5),
            poll ?? TimeSpan.FromMilliseconds(30),
            timing.Delay,
            () => timing.Now);

    // ---- Settled-already → open immediately -------------------------------------------------

    [Fact]
    public async Task WhenAlreadyReopenable_OpensImmediately_NoPoll()
    {
        var target = new FakeReopenTarget();
        target.Settle(); // no close in flight
        var timing = new FakeTiming();
        var guard = Build(target, timing);

        var gen = guard.RequestOpen();
        var decision = await guard.WaitUntilReopenableAsync(gen);

        decision.Should().Be(ReopenDecision.Open);
        timing.Delays.Should().BeEmpty("a settled element needs no settle-poll");
    }

    // ---- The headline crash case: Close in flight, Open must WAIT then open -----------------

    [Fact]
    public async Task CloseInFlight_ThenSettles_OpenWaitsThenOpens()
    {
        // The exact repro in guard terms: element is mid-close (Clear's fire-and-forget Close), an
        // Open is requested → it must NOT open now; once the close settles the open proceeds.
        var target = new FakeReopenTarget(); // starts NOT reopenable (mid-close)
        var timing = new FakeTiming();

        // Settle the element on the 2nd poll, simulating the async close completing shortly after.
        var polls = 0;
        timing.OnDelay = () =>
        {
            if (++polls == 2)
            {
                target.Settle();
            }
        };

        var guard = Build(target, timing);
        var gen = guard.RequestOpen();

        var decision = await guard.WaitUntilReopenableAsync(gen);

        decision.Should().Be(ReopenDecision.Open, "the open proceeds once the close settled");
        timing.Delays.Should().NotBeEmpty("the guard waited for the mid-close element to settle");
        target.Reads.Should().BeGreaterThan(1, "it polled the element's state more than once");
    }

    // ---- Supersede: a newer Open/Unload drops the pending open ------------------------------

    [Fact]
    public async Task NewerOpen_WhileWaiting_SupersedesPendingOpen()
    {
        var target = new FakeReopenTarget(); // mid-close, never settles on its own
        var timing = new FakeTiming();

        var guard = Build(target, timing);
        var firstGen = guard.RequestOpen();

        // A second Open arrives while the first is still waiting for the element to settle.
        timing.OnDelay = () => guard.RequestOpen();

        var decision = await guard.WaitUntilReopenableAsync(firstGen);

        decision.Should().Be(ReopenDecision.Superseded, "a newer open request took ownership");
    }

    [Fact]
    public async Task Unload_WhileWaiting_SupersedesPendingOpen()
    {
        // split → Clear → drag: model an EXTRA Clear (Unload) landing while a load's open is still
        // waiting for the first close to settle — the stale open must drop, not open mid-close.
        var target = new FakeReopenTarget();
        var timing = new FakeTiming();
        var guard = Build(target, timing);
        var gen = guard.RequestOpen();

        timing.OnDelay = () =>
        {
            guard.NotifySuperseded(); // a Clear/Unload bumps the generation
            target.Settle();          // even if the element then settles, the stale open must drop
        };

        var decision = await guard.WaitUntilReopenableAsync(gen);

        decision.Should().Be(ReopenDecision.Superseded, "an Unload superseded the pending open");
    }

    // ---- Stuck element → timeout (open-unsafe), never a crash / infinite wait ---------------

    [Fact]
    public async Task ElementNeverSettles_TimesOut_OpenUnsafe_NotInfinite()
    {
        var target = new FakeReopenTarget(); // stays mid-close forever
        var timing = new FakeTiming();
        // Short timeout + poll so the injected clock trips the deadline after a bounded number of polls.
        var guard = Build(target, timing, settleTimeout: TimeSpan.FromMilliseconds(100), poll: TimeSpan.FromMilliseconds(30));

        var gen = guard.RequestOpen();
        var decision = await guard.WaitUntilReopenableAsync(gen);

        decision.Should().Be(ReopenDecision.Timeout, "a wedged element must not open (native-crash-unsafe)");
        timing.Delays.Should().NotBeEmpty("it polled up to the timeout");
    }

    // ---- Detached target stops the wait -----------------------------------------------------

    [Fact]
    public async Task DetachedTarget_DropsWait()
    {
        var target = new FakeReopenTarget { IsDetached = true };
        var timing = new FakeTiming();
        var guard = Build(target, timing);

        var gen = guard.RequestOpen();
        var decision = await guard.WaitUntilReopenableAsync(gen);

        decision.Should().Be(ReopenDecision.Superseded, "a detached element has nothing to open");
    }

    // ---- A throwing state read is treated as "still transitional", never surfaced -----------

    [Fact]
    public async Task ThrowingStateRead_IsTreatedAsTransitional_ThenSettles()
    {
        // A torn-down element can throw when its state flags are read; the guard must swallow that and
        // keep waiting (treat as not-yet-reopenable), never let the throw escape and crash the load.
        var target = new ThrowingThenSettlingTarget();
        var timing = new FakeTiming();
        timing.OnDelay = () => target.StopThrowingAndSettle();
        var guard = Build(target, timing);

        var gen = guard.RequestOpen();
        var act = async () => await guard.WaitUntilReopenableAsync(gen);

        var decision = await act.Should().NotThrowAsync();
        decision.Subject.Should().Be(ReopenDecision.Open, "it recovered once the element settled");
    }

    private sealed class ThrowingThenSettlingTarget : IReopenTarget
    {
        private bool _settled;

        public bool IsReopenable => _settled
            ? true
            : throw new InvalidOperationException("element torn down");

        public bool IsDetached => false;

        public void StopThrowingAndSettle() => _settled = true;
    }

    // ---- Multiple split→clear→load cycles stay stable (generations converge) -----------------

    [Fact]
    public async Task ThreeClearLoadCycles_EachOpensAfterItsClose_StaysStable()
    {
        var target = new FakeReopenTarget();
        var timing = new FakeTiming();
        var guard = Build(target, timing);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            // Clear: a Close leaves the element transitional and supersedes any pending open.
            guard.NotifySuperseded();
            target.Unsettle();

            // Load: request an open; the element settles on the first poll (close completed).
            var gen = guard.RequestOpen();
            timing.OnDelay = () => target.Settle();

            var decision = await guard.WaitUntilReopenableAsync(gen);

            decision.Should().Be(ReopenDecision.Open, $"cycle {cycle} opens after its close settles");
        }

        guard.Generation.Should().BeGreaterThan(0, "each cycle advanced the lifecycle generation");
    }
}
