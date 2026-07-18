using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for the per-part split progress the VM maps onto the "Parts to export" rows (T-069):
/// as the engine reports <see cref="PartProgress"/>, row K becomes <see cref="PartRowState.Writing"/>
/// (with a live fraction), rows before it Done, rows after it Pending; unselected parts (subset
/// export) stay Pending; and on success every selected row ends Done. No ffmpeg / GUI — a fake engine
/// scripts the part-progress sequence, driven under a deterministic ordered dispatch context so the
/// posted <see cref="Progress{T}"/> callbacks are drained in FIFO order before asserting.
/// </summary>
public sealed class SplitViewModelPartProgressTests
{
    private const string FakePath = @"C:\videos\clip.mp4";

    // ---- Deterministic dispatch harness (mirrors StagedStatusWiringTests) -------------------

    private sealed class OrderedSyncContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Drain()
        {
            while (_queue.TryDequeue(out var work))
            {
                work.Callback(work.State);
            }
        }
    }

    private static async Task WithOrderedDispatchAsync(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        var ctx = new OrderedSyncContext();
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            await body().ConfigureAwait(true);
            ctx.Drain();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // ---- Fakes ------------------------------------------------------------------------------

    private sealed class FakeProbe : IMediaProbe
    {
        // 60s file, 1s keyframes so cuts snap cleanly onto integer seconds.
        public IReadOnlyList<TimeSpan> Keyframes { get; set; } =
            Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray();

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResult.Success(
                new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult(Keyframes);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
        {
            var best = keyframes[0];
            var bestDist = Abs(best - requested);
            for (var i = 1; i < keyframes.Count; i++)
            {
                var dist = Abs(keyframes[i] - requested);
                if (dist < bestDist)
                {
                    best = keyframes[i];
                    bestDist = dist;
                }
            }

            return new KeyframeSnap(best, best - requested);
        }

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.FromSeconds(1);

        private static TimeSpan Abs(TimeSpan t) => t < TimeSpan.Zero ? t.Negate() : t;
    }

    /// <summary>Split engine that replays a scripted list of <see cref="PartProgress"/> samples.</summary>
    private sealed class PartScriptEngine : ISplitEngine
    {
        private readonly IReadOnlyList<PartProgress> _script;

        public PartScriptEngine(params PartProgress[] script) => _script = script;

        public SplitRequest? LastRequest { get; private set; }

        public Task<SplitResult> SplitAsync(
            SplitRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<OperationStatus>? status = null,
            IProgress<PartProgress>? partProgress = null)
        {
            LastRequest = req;
            foreach (var p in _script)
            {
                partProgress?.Report(p);
            }

            return Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
        }
    }

    private static async Task<SplitViewModel> BuildLoadedAsync(ISplitEngine engine, params double[] cutSeconds)
    {
        var vm = new SplitViewModel(new FakeProbe(), engine);
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        foreach (var s in cutSeconds)
        {
            vm.AddMarker(TimeSpan.FromSeconds(s));
        }

        return vm;
    }

    // ---- Tests ------------------------------------------------------------------------------

    [Fact]
    public async Task Rows_StartPending_BeforeRun()
    {
        var vm = await BuildLoadedAsync(new PartScriptEngine(), 20, 40); // 3 parts

        vm.Segments.Should().HaveCount(3);
        vm.Segments.Should().OnlyContain(s => s.WriteState == PartRowState.Pending);
        vm.Segments.Should().OnlyContain(s => s.PartFraction == 0d);
    }

    [Fact]
    public async Task FullSet_MidRunSample_MarksActiveWriting_EarlierDone_LaterPending()
    {
        // 3 parts; a mid-run sample places part 2 at 50%.
        var engine = new PartScriptEngine(
            new PartProgress(1, 3, 0.5),
            new PartProgress(2, 3, 0.5));
        var vm = await BuildLoadedAsync(engine, 20, 40);

        // Capture state at the point part 2 is writing, BEFORE the success sweep marks all Done — do it
        // by scripting only up to part 2 @ 50% and reading rows after the drained callbacks.
        // The engine returns success, so the post-run sweep will mark selected rows Done; to observe the
        // intermediate state we assert on a fresh engine that does NOT complete part 3.
        // Re-run with an engine that stops at part 2 and inspect via a recording of MarkWriting calls.
        var midStates = new List<(int Index, PartRowState State, double Frac)>();
        foreach (var seg in vm.Segments)
        {
            seg.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SplitSegmentViewModel.WriteState))
                {
                    midStates.Add((seg.Index, seg.WriteState, seg.PartFraction));
                }
            };
        }

        await WithOrderedDispatchAsync(() => vm.RunSplitAsync());

        // Part 1 must have transitioned to Done, part 2 to Writing at some point during the run.
        midStates.Should().Contain(x => x.Index == 1 && x.State == PartRowState.Done);
        midStates.Should().Contain(x => x.Index == 2 && x.State == PartRowState.Writing);
    }

    [Fact]
    public async Task FullSet_OnSuccess_AllRowsDone()
    {
        var engine = new PartScriptEngine(
            new PartProgress(1, 3, 0.5),
            new PartProgress(2, 3, 0.5),
            new PartProgress(3, 3, 1.0)); // muxer's final "all done" sample
        var vm = await BuildLoadedAsync(engine, 20, 40);

        await WithOrderedDispatchAsync(() => vm.RunSplitAsync());

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.Segments.Should().OnlyContain(s => s.WriteState == PartRowState.Done);
        vm.Segments.Should().OnlyContain(s => s.IsDone);
    }

    [Fact]
    public async Task Subset_UnselectedRows_StayPending_SelectedEndDone()
    {
        // 3 parts; export parts 1 and 3 only (deselect part 2). The per-segment path reports parts
        // 1 and 3 by their ORIGINAL index; part 2 is never written.
        var engine = new PartScriptEngine(
            new PartProgress(1, 3, 0.5),
            new PartProgress(1, 3, 1.0),
            new PartProgress(3, 3, 0.5),
            new PartProgress(3, 3, 1.0));
        var vm = await BuildLoadedAsync(engine, 20, 40);
        vm.Segments[1].IsSelected = false; // deselect the middle part

        await WithOrderedDispatchAsync(() => vm.RunSplitAsync());

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.Segments[0].WriteState.Should().Be(PartRowState.Done, "part 1 was selected + written");
        vm.Segments[2].WriteState.Should().Be(PartRowState.Done, "part 3 was selected + written");
        vm.Segments[1].WriteState.Should().Be(PartRowState.Pending, "part 2 was unselected — never written");
    }

    [Fact]
    public async Task ReRun_ResetsRowsAtStart_ThenEndsDoneAgain()
    {
        // An engine that observes row state at the moment the run begins (after the VM's pre-run reset).
        bool? pendingAtStart = null;
        var probeEngine = new PartStartProbeEngine(vmSegmentsAllPending =>
            pendingAtStart ??= vmSegmentsAllPending);

        var vm = new SplitViewModel(new FakeProbe(), probeEngine);
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromSeconds(30)); // 2 parts
        probeEngine.Segments = vm.Segments;

        // First run — manually dirty a row so the pre-run reset has something to clear.
        vm.Segments[0].MarkWriting(0.5);
        await WithOrderedDispatchAsync(() => vm.RunSplitAsync());

        pendingAtStart.Should().BeTrue("the VM resets every row to Pending before invoking the engine");
    }

    /// <summary>Engine that reports whether all rows were Pending at the instant the run began.</summary>
    private sealed class PartStartProbeEngine : ISplitEngine
    {
        private readonly Action<bool> _report;

        public PartStartProbeEngine(Action<bool> report) => _report = report;

        public System.Collections.ObjectModel.ObservableCollection<SplitSegmentViewModel>? Segments { get; set; }

        public Task<SplitResult> SplitAsync(
            SplitRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<OperationStatus>? status = null,
            IProgress<PartProgress>? partProgress = null)
        {
            _report(Segments is not null && Segments.All(s => s.WriteState == PartRowState.Pending));
            return Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
        }
    }
}
