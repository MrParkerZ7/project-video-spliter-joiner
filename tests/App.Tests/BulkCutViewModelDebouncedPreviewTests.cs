using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-115 tests for the DEBOUNCED Bulk Cut preview-open (epic G-040, SPEC-011 + SPEC-013). Selecting a row
/// must light it up INSTANTLY (selection + <see cref="BulkCutViewModel.HasSelection"/> + command CanExecute
/// synchronous) while the heavy FFME preview open is deferred behind a short debounce with cancel-prior /
/// latest-wins: arrowing through N rows opens ONLY the settled row (not one decoder init per row swept past
/// — the selection-lag root cause), a null/clear selection cancels the pending open before it can land, and
/// a batch run preempts a still-pending open (stop-on-run wins). Driven over a <see cref="RecordingMediaPlayer"/>
/// at the <see cref="IMediaPlayer"/> seam + a gated debounce delay-func + a pumpable
/// <see cref="SynchronizationContext"/> — no WPF, no FFME, no real clock — exactly the harness shape as
/// <see cref="BulkItemThumbnailTests"/> (the T-108 grabber the debounce is modelled on).
/// </summary>
public sealed class BulkCutViewModelDebouncedPreviewTests
{
    // ---- Recording media player (records Open/Unload/Stop in call order) ---------------------

    private sealed class RecordingMediaPlayer : IMediaPlayer
    {
        public List<string> Calls { get; } = new();

        public List<string> Opened { get; } = new();

        public int OpenCount => Opened.Count;

        public int UnloadCount => Calls.Count(c => c == "Unload");

        public int StopCount => Calls.Count(c => c == "Stop");

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        public void Open(string path)
        {
            Calls.Add("Open");
            Opened.Add(path);
            IsPlaying = false;
            Duration = null;
        }

        public void Play()
        {
            Calls.Add("Play");
            IsPlaying = true;
        }

        public void Pause()
        {
            Calls.Add("Pause");
            IsPlaying = false;
        }

        public void Stop()
        {
            Calls.Add("Stop");
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void Seek(TimeSpan t)
        {
            Calls.Add("Seek");
            Position = t;
        }

        public void Unload()
        {
            Calls.Add("Unload");
            Duration = null;
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void StepFrame(int direction) => Calls.Add("StepFrame");

#pragma warning disable CS0067 // Raised by the real player; the recorder never fires them.
        public event EventHandler? PositionChanged;

        public event EventHandler? Seeked;

        public event EventHandler? DurationAvailable;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    // ---- Pumpable single-threaded sync context (drains the marshalled-back open continuation) -

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

        /// <summary>Drain until the queue is empty (continuations that post more continuations are picked up).</summary>
        public void DrainToEmpty()
        {
            while (Drain() > 0)
            {
            }
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

    // ---- Controllable debounce seam (parks each open in the debounce window until released) ---

    /// <summary>
    /// A gated debounce seam: each open's debounce await parks on a fresh <see cref="TaskCompletionSource"/>
    /// until <see cref="ReleaseAll"/>, so a request can be left "in the debounce window" while a newer
    /// selection supersedes it — exactly the coalesce race. A cancel faults the wait (latest-wins), so a
    /// superseded / cleared / run-preempted open never proceeds to <see cref="IMediaPlayer.Open"/>.
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

    // ---- Build helpers ----------------------------------------------------------------------

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, RecordingMediaPlayer Player, GatedDelay Delay, PumpContext Pump) BuildGated()
    {
        var pump = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(pump);

        var probe = new BulkFakeProbe();
        var player = new RecordingMediaPlayer();
        var delay = new GatedDelay();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(),
            new FakeBulkTrimEngine(), player, selectionOpenDelay: delay.Func);
        return (vm, probe, player, delay, pump);
    }

    private static async Task<BulkItemViewModel> AddRowAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double durationSeconds = 60, double stepSeconds = 2,
        double introSeconds = 10)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(durationSeconds), stepSeconds);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        return row;
    }

    // ---- (a) selection is INSTANT; the open is deferred -------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    [Trait("serves-spec", "SPEC-013")]
    public async Task SelectingRow_SetsHighlightAndCommandStates_IMMEDIATELY_BeforeAnyOpen()
    {
        var (vm, probe, player, delay, pump) = BuildGated();
        using var pumpScope = pump;

        // AddFilesAsync auto-selects the first row → its preview open is scheduled but PARKED in the
        // debounce window (the gate is never released here), so it has NOT opened yet.
        var row = await AddRowAsync(vm, probe, @"C:\v\a.mp4");

        vm.SelectedItem.Should().BeSameAs(row, "the selection is set synchronously");
        vm.HasSelection.Should().BeTrue("HasSelection updates immediately, not on the deferred open");
        vm.CanSaveProfile.Should().BeTrue("Save-current-as depends only on the selected row — set synchronously");
        vm.SaveProfileCommand.CanExecute(null).Should().BeTrue("the command CanExecute reflects the selection instantly");

        player.OpenCount.Should().Be(0, "the heavy FFME preview open is deferred behind the debounce — never fired synchronously");
        delay.Count.Should().BeGreaterThan(0, "a debounced open was scheduled (just parked, not fired)");
    }

    // ---- (b) N rapid selections coalesce to EXACTLY ONE open (of the last) -------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    [Trait("serves-spec", "SPEC-013")]
    public async Task RapidSelections_AreDebounced_ToASingleOpen_OfTheSettledRow()
    {
        var (vm, probe, player, delay, pump) = BuildGated();
        using var pumpScope = pump;
        var a = await AddRowAsync(vm, probe, @"C:\v\a.mp4"); // auto-selected → parks open(a)
        var b = await AddRowAsync(vm, probe, @"C:\v\b.mp4"); // selection stays a (already non-null)
        var c = await AddRowAsync(vm, probe, @"C:\v\c.mp4");

        // Arrow through the rows fast — every switch supersedes (cancels) the prior parked open (latest-wins).
        vm.SelectedItem = b;
        vm.SelectedItem = c;
        vm.SelectedItem = a;
        vm.SelectedItem = c; // settle on c

        player.OpenCount.Should().Be(0, "nothing opens until the debounce settles");

        // Release every parked debounce wait + pump the marshalled continuations.
        delay.ReleaseAll();
        pump.DrainToEmpty();

        player.OpenCount.Should().Be(1, "arrowing through N rows opens ONLY the settled row — the swept-past rows were cancelled");
        player.Opened.Should().ContainSingle().Which.Should().Be(@"C:\v\c.mp4", "the one open is the row we settled on");
    }

    // ---- (c) select → clear cancels the pending open (no stray open, then unload) ------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    [Trait("serves-spec", "SPEC-013")]
    public async Task SelectThenClear_CancelsThePendingOpen_NoStrayOpen_ThenUnload()
    {
        var (vm, probe, player, delay, pump) = BuildGated();
        using var pumpScope = pump;
        await AddRowAsync(vm, probe, @"C:\v\a.mp4"); // auto-selected → parks open(a)

        player.OpenCount.Should().Be(0, "the open is still parked in the debounce window");

        // Clear the selection: this must cancel the pending open FIRST, then unload immediately.
        vm.SelectedItem = null;

        player.UnloadCount.Should().Be(1, "a null selection unloads the shared player immediately");
        player.Calls.Should().NotContain("Open", "the pending open was cancelled before the unload — no stray open lands first");

        // Even after the parked debounce releases, the cancelled open must NEVER fire.
        delay.ReleaseAll();
        pump.DrainToEmpty();

        player.OpenCount.Should().Be(0, "the cancelled open never reaches the player, even once its debounce elapses");
        player.Calls.Should().ContainSingle(c => c == "Unload").And.NotContain("Open");
    }

    // ---- (d) select → run preempts the pending open (stop-on-run wins) -----------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    [Trait("serves-spec", "SPEC-013")]
    public async Task SelectThenRunBatch_CancelsThePendingOpen_StopOnRunWins()
    {
        var (vm, probe, player, delay, pump) = BuildGated();
        using var pumpScope = pump;
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", introSeconds: 10); // valid cut, auto-selected → parks open(a)

        vm.CanRunBatch.Should().BeTrue();
        player.OpenCount.Should().Be(0, "the preview open is still parked when the run starts");

        await vm.RunBatchAsync();

        player.StopCount.Should().BeGreaterThanOrEqualTo(1, "a run stops the preview decode");
        vm.BatchState.Should().Be(BulkBatchState.Completed);

        // The still-pending open must be preempted by the run — it must not fire after the Stop.
        delay.ReleaseAll();
        pump.DrainToEmpty();

        player.OpenCount.Should().Be(0, "stop-on-run wins — a pending preview open never lands after the batch's Stop");
    }
}
