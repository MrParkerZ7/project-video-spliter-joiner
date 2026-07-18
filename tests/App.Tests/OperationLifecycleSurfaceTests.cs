using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-073: unit tests for the clear operation-state surface. Verifies the VM helpers that drive the
/// mutually-exclusive lifecycle surfaces (Running / Completed / Cancelled / Failed) —
/// <see cref="OperationViewModel.IsCompleted"/> / <see cref="OperationViewModel.IsCancelled"/> across
/// every transition, and <see cref="OperationViewModel.ResultSummary"/> being set on success and
/// cleared on a new run / <see cref="OperationViewModel.Reset"/> — plus that the Split/Join VMs supply
/// an accurate summary line. No ffmpeg, no GUI.
/// </summary>
public sealed class OperationLifecycleSurfaceTests
{
    private sealed record FakeResult(bool Ok, UserFacingError? Failure);

    // ---- OperationViewModel: IsCompleted / IsCancelled across transitions --------------------

    [Fact]
    public void FreshVm_Idle_NoTerminalFlags()
    {
        var vm = new OperationViewModel();

        vm.State.Should().Be(OperationState.Idle);
        vm.IsRunning.Should().BeFalse();
        vm.IsCompleted.Should().BeFalse();
        vm.IsCancelled.Should().BeFalse();
        vm.Error.Should().BeNull();
    }

    [Fact]
    public async Task WhileRunning_OnlyIsRunning_NoTerminalFlags()
    {
        var vm = new OperationViewModel();
        var gate = new TaskCompletionSource();

        bool runningOnly = false;
        var run = vm.RunAsync(async (_, _) =>
        {
            runningOnly = vm.IsRunning && !vm.IsCompleted && !vm.IsCancelled;
            await gate.Task;
        }, "Working…");

        await Task.Yield();
        runningOnly.Should().BeTrue("only IsRunning is true while running");

        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task Idle_To_Running_To_Completed_SetsIsCompletedOnly()
    {
        var vm = new OperationViewModel();

        await vm.RunAsync((_, _) => Task.CompletedTask, "Working…");

        vm.State.Should().Be(OperationState.Completed);
        vm.IsCompleted.Should().BeTrue();
        vm.IsCancelled.Should().BeFalse();
        vm.IsRunning.Should().BeFalse();
        vm.Error.Should().BeNull("a completed run is not a failure");
    }

    [Fact]
    public async Task Idle_To_Running_To_Cancelled_SetsIsCancelledOnly_NotErrorRed()
    {
        var vm = new OperationViewModel();
        var started = new TaskCompletionSource();

        var run = vm.RunAsync(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, "Working…");

        await started.Task;
        vm.CancelCommand.Execute(null);
        await run;

        vm.State.Should().Be(OperationState.Cancelled);
        vm.IsCancelled.Should().BeTrue();
        vm.IsCompleted.Should().BeFalse();
        vm.IsRunning.Should().BeFalse();
        vm.Error.Should().BeNull("Cancelled must be neutral, never the red error surface");
    }

    [Fact]
    public async Task Idle_To_Running_To_Failed_NoTerminalBoolFlags_ErrorSet()
    {
        var vm = new OperationViewModel();

        await vm.RunAsync((_, _) => throw new InvalidOperationException("boom"), "Working…");

        vm.State.Should().Be(OperationState.Failed);
        vm.IsCompleted.Should().BeFalse("Failed is surfaced by Error, not IsCompleted");
        vm.IsCancelled.Should().BeFalse();
        vm.IsRunning.Should().BeFalse();
        vm.Error.Should().NotBeNull("the red error block is driven by Error");
    }

    [Fact]
    public async Task StateChange_RaisesIsCompletedAndIsCancelled()
    {
        var vm = new OperationViewModel();
        var raised = new HashSet<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        await vm.RunAsync((_, _) => Task.CompletedTask, "Working…");

        // The State setter must notify both new lifecycle flags so the bound surfaces switch.
        raised.Should().Contain(nameof(OperationViewModel.IsCompleted));
        raised.Should().Contain(nameof(OperationViewModel.IsCancelled));
    }

    [Fact]
    public async Task Completed_Then_Reset_ClearsIsCompleted()
    {
        var vm = new OperationViewModel();
        await vm.RunAsync((_, _) => Task.CompletedTask, "Working…");
        vm.IsCompleted.Should().BeTrue();

        vm.Reset();

        vm.State.Should().Be(OperationState.Idle);
        vm.IsCompleted.Should().BeFalse("Reset returns to Idle so no stale 'done' surface lingers");
        vm.IsCancelled.Should().BeFalse();
    }

    // ---- ResultSummary: set-on-success + clear-on-new-run + clear-on-reset -------------------

    [Fact]
    public async Task ResultSummary_SetByProducer_SurvivesUntilNextRun()
    {
        var vm = new OperationViewModel();
        await vm.RunAsync((_, _) => Task.CompletedTask, "Working…");
        vm.ResultSummary = "Split into 3 parts"; // producer supplies it after success

        vm.ResultSummary.Should().Be("Split into 3 parts");
    }

    [Fact]
    public async Task ResultSummary_ClearedAtStartOfNewRun()
    {
        var vm = new OperationViewModel();
        await vm.RunAsync((_, _) => Task.CompletedTask, "Working…");
        vm.ResultSummary = "Split into 3 parts";

        // A new run must clear the previous "done" line before it begins (observed inside the work).
        string? summaryAtStart = "unset";
        var gate = new TaskCompletionSource();
        var run = vm.RunAsync(async (_, _) =>
        {
            summaryAtStart = vm.ResultSummary;
            await gate.Task;
        }, "Working…");

        await Task.Yield();
        summaryAtStart.Should().BeNull("a new run clears any prior success summary");

        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task ResultSummary_ClearedOnReset()
    {
        var vm = new OperationViewModel();
        await vm.RunAsync((_, _) => Task.CompletedTask, "Working…");
        vm.ResultSummary = "Joined 4 clips → joined.mkv";

        vm.Reset();

        vm.ResultSummary.Should().BeNull("Reset clears the success summary");
    }

    [Fact]
    public void ResultSummary_Setter_RaisesPropertyChanged()
    {
        var vm = new OperationViewModel();
        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.ResultSummary)) raised = true; };

        vm.ResultSummary = "Split into 2 parts";

        raised.Should().BeTrue("the bound Completed surface must update when the summary changes");
    }

    // ---- Split VM supplies an accurate summary on success ------------------------------------

    private sealed class FakeSplitProbe : IMediaProbe
    {
        public IReadOnlyList<TimeSpan> Keyframes { get; set; } = Array.Empty<TimeSpan>();

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResult.Success(
                new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult(Keyframes);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
            => new(requested, TimeSpan.Zero);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    private sealed class FakeSplitEngine : ISplitEngine
    {
        public Func<SplitRequest, SplitResult>? Handler { get; set; }

        public Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<OperationStatus>? status = null, IProgress<PartProgress>? partProgress = null)
        {
            progress?.Report(0.5);
            var result = Handler is not null ? Handler(req) : new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>());
            return Task.FromResult(result);
        }
    }

    private static SplitSegment Seg(string path) =>
        new(path, TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.Zero, TimeSpan.Zero);

    [Fact]
    public async Task Split_OnSuccess_SetsResultSummary_WithWrittenPartCount()
    {
        var probe = new FakeSplitProbe
        {
            Keyframes = Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray(),
        };
        var engine = new FakeSplitEngine();
        var vm = new SplitViewModel(probe, engine);

        await vm.LoadAsync(@"C:\videos\clip.mp4");
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromSeconds(20));
        vm.AddMarker(TimeSpan.FromSeconds(40));
        // 2 markers → 3 parts, all selected. Engine echoes 3 written segments.
        engine.Handler = _ => new SplitResult(
            new[] { Seg(@"C:\out\p1.mp4"), Seg(@"C:\out\p2.mp4"), Seg(@"C:\out\p3.mp4") },
            Array.Empty<string>());

        await vm.RunSplitAsync();

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.Operation.IsCompleted.Should().BeTrue();
        vm.Operation.ResultSummary.Should().Be("Split into 3 parts");
    }

    [Fact]
    public async Task Split_Subset_SetsResultSummary_WroteNofM()
    {
        var probe = new FakeSplitProbe
        {
            Keyframes = Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray(),
        };
        var engine = new FakeSplitEngine();
        var vm = new SplitViewModel(probe, engine);

        await vm.LoadAsync(@"C:\videos\clip.mp4");
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromSeconds(20));
        vm.AddMarker(TimeSpan.FromSeconds(40)); // 3 parts total
        vm.Segments[1].IsSelected = false;      // export a strict subset

        // Engine writes only the 2 selected parts.
        engine.Handler = _ => new SplitResult(
            new[] { Seg(@"C:\out\p1.mp4"), Seg(@"C:\out\p3.mp4") },
            Array.Empty<string>());

        await vm.RunSplitAsync();

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.Operation.ResultSummary.Should().Be("Wrote 2 of 3 parts");
    }

    [Fact]
    public async Task Split_Clear_ClearsResultSummary()
    {
        var probe = new FakeSplitProbe
        {
            Keyframes = Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray(),
        };
        var engine = new FakeSplitEngine();
        var vm = new SplitViewModel(probe, engine);

        await vm.LoadAsync(@"C:\videos\clip.mp4");
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromSeconds(20));
        engine.Handler = _ => new SplitResult(new[] { Seg(@"C:\out\p1.mp4"), Seg(@"C:\out\p2.mp4") }, Array.Empty<string>());
        await vm.RunSplitAsync();
        vm.Operation.ResultSummary.Should().NotBeNull();

        vm.Clear();

        vm.Operation.ResultSummary.Should().BeNull("Clear resets the operation, dropping the stale summary");
        vm.Operation.IsCompleted.Should().BeFalse();
    }

    // ---- Join VM supplies an accurate summary on success ------------------------------------

    private sealed class FakeJoinEngine : IJoinEngine
    {
        public Func<JoinRequest, JoinResult>? JoinHandler { get; set; }

        public Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => Task.FromResult(CompatReport.Ok());

        public Task<JoinResult> JoinAsync(JoinRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<OperationStatus>? status = null)
        {
            progress?.Report(0.5);
            var result = JoinHandler is not null ? JoinHandler(req) : JoinResult.Ok(req.OutputPath);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeJoinProbe : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResult.Success(
                new MediaInfo(TimeSpan.FromSeconds(10), "mkv",
                    new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
                    Array.Empty<StreamInfo>())));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimeSpan>>(Array.Empty<TimeSpan>());

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
            => new(requested, TimeSpan.Zero);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    [Fact]
    public async Task Join_OnSuccess_SetsResultSummary_WithClipCountAndOutputName()
    {
        var engine = new FakeJoinEngine();
        var vm = new JoinViewModel(engine, new FakeJoinProbe());

        await vm.AddFilesAsync(new[] { @"C:\videos\a.mkv", @"C:\videos\b.mkv" });
        vm.OutputPath = @"C:\videos\joined.mkv";
        engine.JoinHandler = req => JoinResult.Ok(req.OutputPath);

        await vm.RunJoinAsync();

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.Operation.IsCompleted.Should().BeTrue();
        vm.Operation.ResultSummary.Should().Be("Joined 2 clips → joined.mkv");
    }

    [Fact]
    public async Task Join_Clear_ClearsResultSummary()
    {
        var engine = new FakeJoinEngine();
        var vm = new JoinViewModel(engine, new FakeJoinProbe());

        await vm.AddFilesAsync(new[] { @"C:\videos\a.mkv", @"C:\videos\b.mkv" });
        vm.OutputPath = @"C:\videos\joined.mkv";
        await vm.RunJoinAsync();
        vm.Operation.ResultSummary.Should().NotBeNull();

        vm.Clear();

        vm.Operation.ResultSummary.Should().BeNull("Clear resets the operation, dropping the stale summary");
        vm.Operation.IsCompleted.Should().BeFalse();
    }
}
