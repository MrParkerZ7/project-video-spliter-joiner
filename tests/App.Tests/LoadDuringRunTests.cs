using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-157 — a load started while a split is running must not tear the split down.
///
/// <para>Split's <c>LoadAsync</c> and <c>RunSplitAsync</c> share ONE <see cref="OperationViewModel"/>,
/// and both enter it through <c>RunWithResultAsync</c> → <c>BeginRun</c>, whose first two statements were
/// <c>_cts?.Dispose(); _cts = new CancellationTokenSource();</c> with no in-flight check. So dropping a
/// second video while the first was still exporting disposed the export's own token, pointed Cancel at
/// the load instead, and reset the export's progress line mid-run.</para>
///
/// <para>Every screen guarded <b>Clear</b> with <c>!Operation.IsRunning</c>; none guarded Load or Add
/// with anything. Found by an adversarial review of the T-154 drop path — the drop is the easiest way to
/// reach it, but the Load button and the picker reach the same method.</para>
///
/// <para><b>Scope, checked rather than assumed.</b> This is Split-only. Join's and Bulk Cut's add paths
/// never touch their <c>Operation</c> at all (its single use on each is the run itself), so no equivalent
/// teardown exists there and none is invented here.</para>
/// </summary>
public sealed class LoadDuringRunTests : IDisposable
{
    private readonly string _dir;

    public LoadDuringRunTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t157-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Make(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "placeholder — the filter only reads the extension");
        return p;
    }

    /// <summary>
    /// A split engine that parks inside <see cref="SplitAsync"/> until released, exposing the token it
    /// was handed. Holding the run open is the only way to observe what a concurrent load does to it.
    /// </summary>
    private sealed class ParkedSplitEngine : ISplitEngine
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The token the running split is actually observing.</summary>
        public CancellationToken RunToken { get; private set; }

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task<SplitResult> SplitAsync(
            SplitRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<OperationStatus>? status = null,
            IProgress<PartProgress>? partProgress = null)
        {
            RunToken = ct;
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return SplitResult.Empty(Array.Empty<string>());
        }
    }

    private async Task<(SplitViewModel Vm, ParkedSplitEngine Engine, Task Run)> StartASplitAsync()
    {
        var engine = new ParkedSplitEngine();
        var probe = new BulkFakeProbe();
        var vm = new SplitViewModel(probe, engine, player: null, new FakeSettings());

        var input = Make("source.mp4");
        await vm.LoadAsync(input);
        vm.InputPath.Should().Be(input, "precondition — the file loaded before the run started");

        vm.AddCutAtCommand.Execute(TimeSpan.FromSeconds(10));
        vm.OutputDir = _dir;
        vm.CanRunSplit.Should().BeTrue("precondition — the run must be startable");

        var run = vm.RunSplitAsync();
        await engine.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        vm.Operation.IsRunning.Should().BeTrue("precondition — the split really is in flight");

        return (vm, engine, run);
    }

    [Trait("serves-spec", "SPEC-008")]
    [Fact]
    public async Task ADropWhileASplitIsRunningLeavesTheRunIntact()
    {
        var (vm, engine, run) = await StartASplitAsync();
        var runningStatus = vm.Operation.StatusText;

        await vm.AddDroppedFilesAsync(new[] { Make("second.mp4") });

        vm.Operation.IsRunning.Should().BeTrue("the split is still going — a drop must not end it");
        vm.Operation.StatusText.Should().Be(
            runningStatus,
            "the running split owns the status line; a refused load must not overwrite it with " +
            "\"Loading…\" and reset the progress the user is watching");

        // The decisive one: Cancel must still reach the SPLIT. Before the guard, the drop replaced the
        // operation's CancellationTokenSource, so Cancel cancelled the load's source and the split's own
        // token was left disposed and unreachable.
        vm.Operation.CancelCommand.Execute(null);
        vm.Operation.CancelCommand.Should().NotBeNull();
        engine.RunToken.IsCancellationRequested.Should().BeTrue(
            "Cancel has to keep cancelling the operation the user started");

        engine.Release();
        await run;
    }

    [Trait("serves-spec", "SPEC-008")]
    [Fact]
    public async Task ADropWhileASplitIsRunningIsRefusedInWords()
    {
        var (vm, engine, run) = await StartASplitAsync();

        await vm.AddDroppedFilesAsync(new[] { Make("second.mp4") });

        vm.InputPath.Should().Be(
            Path.Combine(_dir, "source.mp4"), "the loaded file is unchanged — the drop was refused");
        vm.DropSummary.Should().NotBeNullOrWhiteSpace(
            "refusing in silence would re-create the exact defect T-154 exists to fix — this is a " +
            "refusal, so it has to say so");
        vm.DropSummary.Should().Contain("running");

        engine.Release();
        await run;
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task TheLoadButtonAndPickerAreGuardedToo_NotJustTheDrop()
    {
        // The drop is refused earlier, in AddDroppedFilesAsync, so it never reaches LoadAsync — which
        // means the drop tests say nothing about the OTHER two doors. The tab-strip Load button and the
        // file picker both call LoadAsync directly, and a mutation removing its guard survived every
        // test until this one existed.
        var (vm, engine, run) = await StartASplitAsync();
        var runningStatus = vm.Operation.StatusText;

        await vm.LoadAsync(Make("second.mp4"));

        vm.InputPath.Should().Be(
            Path.Combine(_dir, "source.mp4"), "the picker's file was refused, not loaded");
        vm.Operation.IsRunning.Should().BeTrue("the split is untouched");
        vm.Operation.StatusText.Should().Be(runningStatus);
        engine.RunToken.IsCancellationRequested.Should().BeFalse();
        vm.StatusText.Should().Contain(
            "running", "a refused load has to say why — this is the same silence rule as the drop note");

        engine.Release();
        await run;
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task RunIsNotOfferedWhileARunIsAlreadyGoing()
    {
        // The same re-entrancy through the other door: CanRunSplit gated on file/markers/output/selection
        // but never on whether a split was already running, so a second Run re-entered BeginRun too.
        var (vm, engine, run) = await StartASplitAsync();

        vm.CanRunSplit.Should().BeFalse("a split is already running");

        engine.Release();
        await run;
    }

    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public async Task JoinAlsoRefusesASecondRunWhileOneIsGoing()
    {
        // Join has the same re-entrancy door Split had: CanRunJoin gated on item count, compatibility
        // and output path — everything about the REQUEST, nothing about the operation. Adding the
        // BeginRun throw without closing this would have turned a silent corruption into an unhandled
        // exception on a double-click. (Bulk Cut's CanRunBatch already carried the clause.)
        var engine = new ParkedJoinEngine();
        var vm = new JoinViewModel(engine, new BulkFakeProbe());

        vm.Items.Add(new JoinItemViewModel(Make("a.mp4")));
        vm.Items.Add(new JoinItemViewModel(Make("b.mp4")));
        vm.OutputPath = Path.Combine(_dir, "joined.mp4");

        // Force the compat verdict the gate needs, then prove the ONLY thing still blocking Run is the
        // running operation — otherwise this test would pass for the wrong reason.
        typeof(JoinViewModel).GetProperty(nameof(JoinViewModel.IsCompatible))!
            .SetValue(vm, true);
        vm.CanRunJoin.Should().BeTrue("precondition — Run is offered when nothing is running");

        // Awaited, never .Wait()-ed: blocking the test thread here deadlocks against the ConfigureAwait
        // (true) continuations in the view-model, which resume on the context this test would be sitting
        // on. It hung the whole suite exactly once before this comment existed.
        var run = vm.RunJoinAsync();
        await engine.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        vm.CanRunJoin.Should().BeFalse("a join is already running");

        engine.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A join engine that parks inside <see cref="JoinAsync"/> until released.</summary>
    private sealed class ParkedJoinEngine : IJoinEngine
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public Task<CompatReport> CheckCompatibilityAsync(
            IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => Task.FromResult(CompatReport.Ok());

        public async Task<JoinResult> JoinAsync(
            JoinRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return JoinResult.Ok(req.OutputPath);
        }
    }

    [Trait("serves-spec", "SPEC-008")]
    [Fact]
    public async Task ReEnteringTheOperationIsARefusedProgrammingError()
    {
        // Backstop for every future caller, not just the two that exist today. Re-entry used to corrupt
        // silently; it now fails loudly where a test can see it.
        var (vm, engine, run) = await StartASplitAsync();

        var reenter = async () => await vm.Operation.RunAsync(
            work: (_, _) => Task.CompletedTask, runningStatus: "second run");

        await reenter.Should().ThrowAsync<InvalidOperationException>(
            "a second run on an operation that is already running is a bug, and silently disposing the " +
            "first one's CancellationTokenSource is how it used to present");

        vm.Operation.IsRunning.Should().BeTrue("the original run is untouched");
        engine.RunToken.IsCancellationRequested.Should().BeFalse();

        engine.Release();
        await run;
    }
}
