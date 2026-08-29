using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for <see cref="JoinViewModel"/> using fake <see cref="IJoinEngine"/> /
/// <see cref="IMediaProbe"/> — no ffmpeg, no GUI, no rendering. Deterministic: the fakes return
/// scripted results synchronously (TaskCompletionSource-free where ordering does not matter).
/// </summary>
public sealed class JoinViewModelTests
{
    private const string Clip1 = @"C:\videos\a.mp4";
    private const string Clip2 = @"C:\videos\b.mp4";
    private const string Clip3 = @"C:\videos\c.mp4";
    private const string Output = @"C:\videos\joined.mp4";

    // ---- Fakes ------------------------------------------------------------------------------

    /// <summary>Scripted join engine: records the last request + hands back canned compat/join results.</summary>
    private sealed class FakeJoinEngine : IJoinEngine
    {
        public CompatReport CompatToReturn { get; set; } = CompatReport.Ok();

        public Func<JoinRequest, JoinResult>? JoinHandler { get; set; }

        /// <summary>Async hook returning an (optionally still-pending) Task — lets a test hold a join "running".</summary>
        public Func<JoinRequest, Task<JoinResult>>? JoinTaskHandler { get; set; }

        public JoinRequest? LastRequest { get; private set; }

        public int CompatCheckCount { get; private set; }

        public IReadOnlyList<string>? LastCheckedPaths { get; private set; }

        public Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default)
        {
            CompatCheckCount++;
            LastCheckedPaths = inputPaths.ToList();
            return Task.FromResult(CompatToReturn);
        }

        public Task<JoinResult> JoinAsync(JoinRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null)
        {
            LastRequest = req;
            progress?.Report(0.5);
            if (JoinTaskHandler is not null)
            {
                return JoinTaskHandler(req);
            }

            var result = JoinHandler is not null ? JoinHandler(req) : JoinResult.Ok(req.OutputPath);
            return Task.FromResult(result);
        }
    }

    /// <summary>Probe fake — returns a scripted MediaInfo so the info chip is populated deterministically.</summary>
    private sealed class FakeProbe : IMediaProbe
    {
        public ProbeResult ProbeResultToReturn { get; set; } = ProbeResult.Success(
            new MediaInfo(
                TimeSpan.FromSeconds(10),
                "mp4",
                new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
                Array.Empty<StreamInfo>()));

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResultToReturn);

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimeSpan>>(Array.Empty<TimeSpan>());

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
            => new(requested, TimeSpan.Zero);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    private static (JoinViewModel Vm, FakeJoinEngine Engine, FakeProbe Probe) Build()
    {
        var engine = new FakeJoinEngine();
        var probe = new FakeProbe();
        return (new JoinViewModel(engine, probe), engine, probe);
    }

    private static CompatReport Incompatible(string field, string detail)
        => CompatReport.Incompatible(new[] { new Mismatch(field, detail) });

    // ---- Add + compat -----------------------------------------------------------------------

    [Fact]
    public async Task AddFiles_PopulatesItems_ProbesForChip_AndRunsCompatCheck()
    {
        var (vm, engine, _) = Build();

        await vm.AddFilesAsync(new[] { Clip1, Clip2 });

        vm.Items.Should().HaveCount(2);
        vm.Items[0].Display.Should().Be("a.mp4");
        vm.Items[0].InfoText.Should().Contain("h264").And.Contain("1920x1080");
        engine.CompatCheckCount.Should().BeGreaterThan(0, "compat is checked on add");
    }

    // ---- Estimated result + run label (T-059) -----------------------------------------------

    [Fact]
    public async Task AddFiles_SetsProbedDurationOnItems_AndSumsEstimatedDuration()
    {
        var (vm, _, _) = Build(); // FakeProbe returns a 10s clip

        await vm.AddFilesAsync(new[] { Clip1, Clip2 });

        vm.Items.Should().OnlyContain(i => i.Duration == TimeSpan.FromSeconds(10));
        // Two 10s clips → 20s total, formatted M:SS.
        vm.EstimatedDuration.Should().Be("0:20");
        vm.HasClips.Should().BeTrue();
    }

    [Fact]
    public void NoClips_EstimatesAreZero_AndPanelHidden()
    {
        var (vm, _, _) = Build();
        vm.HasClips.Should().BeFalse();
        vm.EstimatedDuration.Should().Be("0:00");
        vm.EstimatedSize.Should().Be("0 B");
    }

    [Fact]
    public async Task RunLabel_IsCountAware()
    {
        var (vm, _, _) = Build();
        vm.RunLabel.Should().Be("Join");

        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        vm.RunLabel.Should().Be("Join 3 clips");
    }

    [Fact]
    public async Task Clear_ResetsEstimateAndLabel()
    {
        var (vm, _, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });

        vm.Clear();

        vm.HasClips.Should().BeFalse();
        vm.EstimatedDuration.Should().Be("0:00");
        vm.RunLabel.Should().Be("Join");
    }

    [Fact]
    public async Task AddFiles_Compatible_SetsGreenSummaryAndIsCompatible()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok();

        await vm.AddFilesAsync(new[] { Clip1, Clip2 });

        vm.IsCompatible.Should().BeTrue();
        vm.CompatSummary.Should().Contain("2 clips").And.Contain("ready to join");
    }

    [Fact]
    public async Task AddFiles_Incompatible_SetsRedSummaryNamingMismatch_AndBlocksRun()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = Incompatible("resolution", "clip 2 is 1280x720, reference (clip 1) is 1920x1080");

        await vm.AddFilesAsync(new[] { Clip1, Clip2 });
        vm.OutputPath = Output;

        vm.IsCompatible.Should().BeFalse();
        vm.CompatSummary.Should().Contain("clip 2 is 1280x720");
        vm.CanRunJoin.Should().BeFalse("an incompatible set is not runnable");
        vm.RunJoinCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task FewerThanTwoItems_NotRunnable_AndAddAtLeastTwoSummary()
    {
        var (vm, _, _) = Build();

        await vm.AddFilesAsync(new[] { Clip1 });
        vm.OutputPath = Output;

        vm.Items.Should().HaveCount(1);
        vm.IsCompatible.Should().BeFalse();
        vm.CompatSummary.Should().Contain("at least 2");
        vm.CanRunJoin.Should().BeFalse();
    }

    // ---- Reorder + remove -------------------------------------------------------------------

    [Fact]
    public async Task MoveDown_ReordersItems_AndReChecksCompat()
    {
        var (vm, engine, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        var checksBefore = engine.CompatCheckCount;

        vm.MoveDownCommand.Execute(vm.Items[0]); // a moves to index 1

        vm.Items.Select(i => i.Path).Should().ContainInOrder(Clip2, Clip1, Clip3);
        engine.CompatCheckCount.Should().BeGreaterThan(checksBefore, "reorder re-checks compat");
    }

    [Fact]
    public async Task MoveUp_ReordersItems_AndReChecksCompat()
    {
        var (vm, engine, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        var checksBefore = engine.CompatCheckCount;

        vm.MoveUpCommand.Execute(vm.Items[2]); // c moves to index 1

        vm.Items.Select(i => i.Path).Should().ContainInOrder(Clip1, Clip3, Clip2);
        engine.CompatCheckCount.Should().BeGreaterThan(checksBefore);
    }

    [Fact]
    public async Task Move_ReordersItems_ThirdToFirst_AndReChecksCompat()
    {
        var (vm, engine, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        var checksBefore = engine.CompatCheckCount;

        await vm.MoveAsync(2, 0); // c moves to the front

        vm.Items.Select(i => i.Path).Should().ContainInOrder(Clip3, Clip1, Clip2);
        engine.CompatCheckCount.Should().BeGreaterThan(checksBefore, "reorder re-checks compat");
        engine.LastCheckedPaths.Should().ContainInOrder(Clip3, Clip1, Clip2);
    }

    [Fact]
    public async Task MoveUp_DelegatesToMove_SameAsMove1To0()
    {
        // MoveUp on index 1 must produce the identical order + one recheck as Move(1,0) —
        // proving Up/Down share the single Move path (no duplicate reorder logic).
        var (viaUp, upEngine, _) = Build();
        await viaUp.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        var upChecksBefore = upEngine.CompatCheckCount;
        viaUp.MoveUpCommand.Execute(viaUp.Items[1]); // b (index 1) up

        var (viaMove, moveEngine, _) = Build();
        await viaMove.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        var moveChecksBefore = moveEngine.CompatCheckCount;
        await viaMove.MoveAsync(1, 0);

        viaUp.Items.Select(i => i.Path).Should()
            .ContainInOrder(Clip2, Clip1, Clip3)
            .And.Equal(viaMove.Items.Select(i => i.Path));
        (upEngine.CompatCheckCount - upChecksBefore).Should()
            .Be(moveEngine.CompatCheckCount - moveChecksBefore, "one reorder path → same number of rechecks");
    }

    [Fact]
    public async Task MoveDown_DelegatesToMove_SameAsMoveIndexPlusOne()
    {
        var (vm, engine, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        var checksBefore = engine.CompatCheckCount;

        vm.MoveDownCommand.Execute(vm.Items[0]); // a (index 0) down == Move(0,1)

        vm.Items.Select(i => i.Path).Should().ContainInOrder(Clip2, Clip1, Clip3);
        engine.CompatCheckCount.Should().BeGreaterThan(checksBefore);
    }

    [Fact]
    public async Task Move_ToSameIndex_IsNoOp_NoReorder_NoRecheck()
    {
        var (vm, engine, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        var checksBefore = engine.CompatCheckCount;

        await vm.MoveAsync(1, 1);

        vm.Items.Select(i => i.Path).Should().ContainInOrder(Clip1, Clip2, Clip3);
        engine.CompatCheckCount.Should().Be(checksBefore, "moving to the same slot does no reorder + no recheck");
    }

    [Fact]
    public async Task Move_OutOfRangeIndices_AreClampedOrIgnored_NoThrow_NoCrash()
    {
        var (vm, engine, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        var checksBefore = engine.CompatCheckCount;

        // Out-of-range SOURCE → ignored (nothing to move), order untouched, no recheck.
        await vm.MoveAsync(99, 0);
        vm.Items.Select(i => i.Path).Should().ContainInOrder(Clip1, Clip2, Clip3);
        engine.CompatCheckCount.Should().Be(checksBefore, "an out-of-range source is a no-op");

        // Over-range DESTINATION → clamped to last slot (move item 0 to the end).
        await vm.MoveAsync(0, 99);
        vm.Items.Select(i => i.Path).Should().ContainInOrder(Clip2, Clip3, Clip1);

        // Negative DESTINATION → clamped to the front.
        await vm.MoveAsync(2, -5); // item now at index 2 is Clip1 → to front
        vm.Items.Select(i => i.Path).Should().ContainInOrder(Clip1, Clip2, Clip3);
    }

    [Fact]
    public async Task Move_WithFewerThanTwoItems_IsNoOp()
    {
        var (vm, engine, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1 });
        var checksBefore = engine.CompatCheckCount;

        await vm.MoveAsync(0, 0);

        vm.Items.Should().HaveCount(1);
        engine.CompatCheckCount.Should().Be(checksBefore, "single item → nothing to reorder");
    }

    [Fact]
    public async Task SingleItem_UpDownCommands_Disabled()
    {
        var (vm, _, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1 });

        vm.MoveUpCommand.CanExecute(vm.Items[0]).Should().BeFalse("first (and only) item cannot move up");
        vm.MoveDownCommand.CanExecute(vm.Items[0]).Should().BeFalse("last (and only) item cannot move down");
    }

    [Fact]
    public async Task Remove_DropsItem_AndReChecksCompat()
    {
        var (vm, engine, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        var checksBefore = engine.CompatCheckCount;

        vm.RemoveCommand.Execute(vm.Items[1]); // remove b

        vm.Items.Select(i => i.Path).Should().ContainInOrder(Clip1, Clip3);
        engine.CompatCheckCount.Should().BeGreaterThan(checksBefore, "removal re-checks compat");
    }

    // ---- CanRunJoin gating ------------------------------------------------------------------

    [Fact]
    public async Task CanRunJoin_True_OnlyWhenTwoPlusCompatibleAndOutputSet()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok();
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });

        vm.CanRunJoin.Should().BeFalse("no output path yet");

        vm.OutputPath = Output;

        vm.CanRunJoin.Should().BeTrue();
        vm.RunJoinCommand.CanExecute(null).Should().BeTrue();
    }

    // ---- Run join ---------------------------------------------------------------------------

    [Fact]
    public async Task RunJoin_BuildsExpectedRequest_InListOrder_AndSetsLastResultOnSuccess()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        vm.OutputPath = Output;
        vm.Overwrite = true;
        engine.JoinHandler = req => JoinResult.Ok(req.OutputPath);

        await vm.RunJoinAsync();

        engine.LastRequest.Should().NotBeNull();
        engine.LastRequest!.InputPaths.Should().ContainInOrder(Clip1, Clip2, Clip3);
        engine.LastRequest.OutputPath.Should().Be(Output);
        engine.LastRequest.Overwrite.Should().BeTrue();

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.LastResult.Should().NotBeNull();
        vm.LastResult!.Success.Should().BeTrue();
        vm.LastResult.OutputPath.Should().Be(Output);
    }

    [Fact]
    public async Task RunJoin_Refusal_OperationFailed_ErrorNamesMismatch_NoLastResult()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok(); // list-time check passes; engine refuses at run time
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });
        vm.OutputPath = Output;

        var refusal = Incompatible("codec", "clip 2 is hevc, reference (clip 1) is h264");
        engine.JoinHandler = _ => JoinResult.Refused(refusal);

        await vm.RunJoinAsync();

        vm.Operation.State.Should().Be(OperationState.Failed);
        vm.Operation.Error.Should().NotBeNull();
        vm.Operation.Error!.RawTail.Should().Contain("clip 2 is hevc");
        vm.LastResult.Should().BeNull("a refusal writes nothing and sets no result");
    }

    [Fact]
    public async Task RunJoin_FfmpegFailure_ErrorExposesFullText_AndLogPath()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok();
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });
        vm.OutputPath = Output;

        var fullStdErr = "concat line 1\nconcat line 2\nImpossible to open list.txt";
        var logPath = @"C:\logs\join-20260717-120000.log";
        engine.JoinHandler = _ => JoinResult.RefusedWithLog(
            Incompatible("ffmpeg", "ffmpeg concat failed (exit 1). Last output:\n" + fullStdErr),
            logPath,
            fullStdErr);

        await vm.RunJoinAsync();

        vm.Operation.State.Should().Be(OperationState.Failed);
        var err = vm.Operation.Error;
        err.Should().NotBeNull();
        err!.FullText.Should().Be(fullStdErr);
        err.LogFilePath.Should().Be(logPath);
        err.HasLogFile.Should().BeTrue();
        err.DetailText.Should().Contain("Impossible to open list.txt");
        err.CopyText.Should().Contain("The clips could not be joined")
            .And.Contain("Impossible to open list.txt")
            .And.Contain(logPath);
        vm.LastResult.Should().BeNull();
    }

    [Fact]
    public async Task RunJoin_WhenNotRunnable_IsNoOp()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok();
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });
        // No output path set → not runnable.

        await vm.RunJoinAsync();

        engine.LastRequest.Should().BeNull("run must not build a request when gated off");
        vm.Operation.State.Should().Be(OperationState.Idle);
    }

    [Fact]
    public async Task RunJoin_Success_ClearsPriorError()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok();
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });
        vm.OutputPath = Output;

        // First run refuses → Failed + Error set.
        engine.JoinHandler = _ => JoinResult.Refused(Incompatible("codec", "clip 2 is hevc"));
        await vm.RunJoinAsync();
        vm.Operation.State.Should().Be(OperationState.Failed);

        // Second run succeeds → the prior error is cleared and completion reported.
        engine.JoinHandler = req => JoinResult.Ok(req.OutputPath);
        await vm.RunJoinAsync();

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.Operation.Error.Should().BeNull("a successful run clears the prior failure");
        vm.LastResult.Should().NotBeNull();
    }

    // ---- Clear all (T-047) ------------------------------------------------------------------

    [Fact]
    public async Task ClearAll_EmptiesItems_ResetsCompat_AndLastResult()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });
        vm.OutputPath = Output;
        engine.JoinHandler = req => JoinResult.Ok(req.OutputPath);
        await vm.RunJoinAsync();

        // Pre-conditions: items present, compatible, a result exists.
        vm.Items.Should().HaveCount(3);
        vm.IsCompatible.Should().BeTrue();
        vm.LastResult.Should().NotBeNull();

        vm.ClearCommand.Execute(null);

        vm.Items.Should().BeEmpty();
        vm.Compat.Should().BeNull();
        vm.IsCompatible.Should().BeFalse();
        vm.CompatSummary.Should().Contain("at least 2");
        vm.LastResult.Should().BeNull();
        vm.CanRunJoin.Should().BeFalse();
    }

    [Fact]
    public void ClearAll_CanExecute_False_WhenEmpty()
    {
        var (vm, _, _) = Build();

        vm.CanClear.Should().BeFalse();
        vm.ClearCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ClearAll_CanExecute_True_WhenItemsPresent()
    {
        var (vm, _, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1 });

        vm.CanClear.Should().BeTrue();
        vm.ClearCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task ClearAll_CanExecute_False_WhileOperationRunning()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok();
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });
        vm.OutputPath = Output;

        // Gate the join so it stays "running" while we assert CanClear (returns a still-pending Task).
        var gate = new TaskCompletionSource<JoinResult>();
        engine.JoinTaskHandler = _ => gate.Task;

        var run = vm.RunJoinAsync();
        vm.Operation.IsRunning.Should().BeTrue("the join is in flight");

        vm.CanClear.Should().BeFalse("a running join must not be cleared mid-op");
        vm.ClearCommand.CanExecute(null).Should().BeFalse();

        // Let the run finish → clear becomes available again.
        gate.SetResult(JoinResult.Ok(Output));
        await run;
        vm.CanClear.Should().BeTrue();
    }

    // ==== SPEC-012 join-screen gaps (todo-automate) ==========================================

    /// <summary>A join engine whose compatibility check THROWS — drives the defensive catch (I10).</summary>
    private sealed class ThrowingCompatJoinEngine : IJoinEngine
    {
        public Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => throw new InvalidOperationException("compat boom");

        public Task<JoinResult> JoinAsync(JoinRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null)
            => Task.FromResult(JoinResult.Ok(req.OutputPath));
    }

    // SPEC-012#I1 — AddFilesAsync(null) is a no-op: no items, no probe, no compat check, no throw.
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task AddFilesAsync_Null_IsNoOp()
    {
        var (vm, engine, _) = Build();

        var act = async () => await vm.AddFilesAsync(null);

        await act.Should().NotThrowAsync();
        vm.Items.Should().BeEmpty("a null path set adds nothing");
        engine.CompatCheckCount.Should().Be(0, "no items were added → no compat check runs");
    }

    // SPEC-012#I10 — CheckCompatibilityAsync throwing is caught defensively → Compat null,
    // IsCompatible false, summary prefixed "Could not verify compatibility:", Run gated.
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task RefreshCompat_EngineThrows_CaughtDefensively_RunGated()
    {
        var probe = new FakeProbe();
        var vm = new JoinViewModel(new ThrowingCompatJoinEngine(), probe);

        await vm.AddFilesAsync(new[] { Clip1, Clip2 });
        vm.OutputPath = Output;

        vm.Compat.Should().BeNull("a thrown check is treated as 'no report'");
        vm.IsCompatible.Should().BeFalse();
        vm.CompatSummary.Should().StartWith("Could not verify compatibility:", "the thrown message is surfaced defensively");
        vm.CanRunJoin.Should().BeFalse("an unverifiable set stays gated off");
        vm.RunJoinCommand.CanExecute(null).Should().BeFalse();
    }

    // SPEC-012#I14 — the synchronous Move(int,int) wrapper delegates to MoveAsync (the drag code-behind
    // entry point). Existing reorder tests exercise MoveAsync / MoveUp/DownCommand but never Move().
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task Move_SyncWrapper_ReordersSameAsMoveAsync()
    {
        var (vm, _, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });

        vm.Move(2, 0); // sync wrapper: c to the front (the reorder itself is synchronous)

        vm.Items.Select(i => i.Path).Should().Equal(new[] { Clip3, Clip1, Clip2 },
            "the sync Move() entry point reorders exactly like MoveAsync(2, 0)");
    }

    // SPEC-012#I28 — CancelCommand delegates to Operation.CancelCommand (same instance).
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public void CancelCommand_IsOperationCancelCommand()
    {
        var (vm, _, _) = Build();

        vm.CancelCommand.Should().BeSameAs(vm.Operation.CancelCommand,
            "the Join-level Cancel delegates to the shared operation's cancel");
    }

    // SPEC-012#I5/I8 — a batch AddFilesAsync of N clips re-checks compatibility EXACTLY ONCE
    // (at the tail, after every item is queued), not once per file. Pins the count the existing
    // add test only bounds with BeGreaterThan(0).
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task AddFiles_BatchOfN_RunsExactlyOneCompatCheck()
    {
        var (vm, engine, _) = Build();

        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });

        vm.Items.Select(i => i.Path).Should().ContainInOrder(Clip1, Clip2, Clip3);
        engine.CompatCheckCount.Should().Be(1, "a batch add re-checks compat once at the tail, not per file");
    }

    // SPEC-012#I7 — adding a single clip never touches the engine: RefreshCompatAsync short-circuits
    // with <2 items (no I/O on that path), leaving Run gated behind the "add at least 2" invite.
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task AddFiles_SingleClip_DoesNotCallEngine_AndStaysGated()
    {
        var (vm, engine, _) = Build();

        await vm.AddFilesAsync(new[] { Clip1 });

        engine.CompatCheckCount.Should().Be(0, "with <2 items RefreshCompatAsync short-circuits and never touches the engine");
        vm.CompatSummary.Should().Contain("at least 2");
        vm.CanRunJoin.Should().BeFalse("a single clip is below the ≥2 run gate");
    }

    // SPEC-012#I15 — removing null or a FOREIGN item (never queued in this VM) is a pure no-op:
    // the list is untouched and NO compat recheck fires (no I/O on that path). Mirrors the Move
    // no-op count-assertions.
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task Remove_NullOrForeignItem_IsNoOp_NoRecheck()
    {
        var (vm, engine, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });
        var checksBefore = engine.CompatCheckCount;

        var foreign = new JoinItemViewModel(Clip3); // constructed directly — never added to this VM's list
        vm.RemoveCommand.Execute(null);              // null parameter
        vm.RemoveCommand.Execute(foreign);           // item not in the list

        vm.Items.Should().HaveCount(2);
        engine.CompatCheckCount.Should().Be(checksBefore, "a null/foreign remove changes nothing → no compat recheck");
    }

    // SPEC-012#I2 — AddFilesAsync appends one item per NON-BLANK path, preserving order; blank /
    // whitespace-only entries are skipped and duplicates are deliberately permitted (no dedup).
    // An all-blank set adds nothing and short-circuits before the engine is touched.
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task AddFiles_SkipsBlankPaths_KeepsDuplicates_AndPreservesOrder()
    {
        var (vm, engine, _) = Build();

        await vm.AddFilesAsync(new[] { Clip1, "", "   ", Clip1, Clip2 });

        vm.Items.Select(i => i.Path).Should().Equal(new[] { Clip1, Clip1, Clip2 },
            "blank/whitespace-only entries are skipped, duplicates are kept, and order is preserved");
        engine.LastCheckedPaths.Should().Equal(new[] { Clip1, Clip1, Clip2 },
            "the engine is asked about exactly the queued set, in list order");

        // An all-blank add queues nothing → the added.Count == 0 early return skips the recheck.
        var checksBefore = engine.CompatCheckCount;
        await vm.AddFilesAsync(new[] { "", "   " });

        vm.Items.Should().HaveCount(3, "an all-blank set adds no items");
        engine.CompatCheckCount.Should().Be(checksBefore, "nothing was queued → no compat recheck runs");
    }

    // SPEC-012#I3 — Display is the path's filename, FALLING BACK to the full path when the filename is
    // empty; SizeBytes is the clip's real on-disk byte size, or 0 when the file cannot be read
    // (SafeFileSize never throws). Needs real files on disk, hence the temp dir.
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task AddFiles_ReadsOnDiskSize_ZeroWhenUnreadable_AndDisplayFallsBackToFullPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-join-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sized = Path.Combine(dir, "sized.mp4");
            File.WriteAllBytes(sized, new byte[1234]);
            var missing = Path.Combine(dir, "definitely-missing.mp4");
            var trailingSeparator = dir + Path.DirectorySeparatorChar; // GetFileName → "" → falls back

            var (vm, _, _) = Build();

            await vm.AddFilesAsync(new[] { sized, missing, trailingSeparator });

            vm.Items.Should().HaveCount(3);
            vm.Items[0].Display.Should().Be("sized.mp4", "Display is the filename of the path");
            vm.Items[0].SizeBytes.Should().Be(1234, "SizeBytes is the clip's real on-disk byte size");
            vm.Items[1].SizeBytes.Should().Be(0, "an unreadable/missing file contributes 0 — SafeFileSize never throws");
            vm.Items[2].Display.Should().Be(trailingSeparator, "an empty filename falls back to the full path");
            vm.Items[2].SizeBytes.Should().Be(0, "a non-file path is unreadable → 0 bytes, not an exception");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // SPEC-012#I4 — the info chip's non-video branches: "audio only" when there is no video stream but
    // audio is present, the CONTAINER string when there is neither, and a failed probe leaving both
    // InfoText and Duration null without throwing. The existing add test covers only the video branch.
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task InfoChip_AudioOnly_ContainerFallback_AndProbeFailureLeavesChipNull()
    {
        var (vm, _, probe) = Build();

        // (a) audio present, no video stream → "audio only"; the probed duration is still captured.
        probe.ProbeResultToReturn = ProbeResult.Success(new MediaInfo(
            TimeSpan.FromSeconds(3),
            "mka",
            Array.Empty<StreamInfo>(),
            new[] { new StreamInfo(0, "aac", "audio", null, null, null, 48000, 2, null) }));
        await vm.AddFilesAsync(new[] { Clip1 });

        vm.Items[0].InfoText.Should().Be("audio only", "a probed set with no video stream reads as audio only");
        vm.Items[0].Duration.Should().Be(TimeSpan.FromSeconds(3), "the probed duration is captured on success");

        // (b) neither video nor audio → the container string is the chip's fallback.
        probe.ProbeResultToReturn = ProbeResult.Success(new MediaInfo(
            TimeSpan.FromSeconds(4),
            "matroska,webm",
            Array.Empty<StreamInfo>(),
            Array.Empty<StreamInfo>()));
        await vm.AddFilesAsync(new[] { Clip2 });

        vm.Items[1].InfoText.Should().Be("matroska,webm", "with no streams at all the chip falls back to the container");

        // (c) a failed probe leaves the chip AND the duration null — best-effort, never throws.
        probe.ProbeResultToReturn = ProbeResult.Failure("ffprobe exited 1");
        var act = async () => await vm.AddFilesAsync(new[] { Clip3 });

        await act.Should().NotThrowAsync("the info chip is best-effort — a probe failure never breaks the add");
        vm.Items[2].InfoText.Should().BeNull("a failed probe leaves the info chip blank");
        vm.Items[2].Duration.Should().BeNull("a failed probe contributes no duration to the estimate");
    }

    // SPEC-012#I13 — CanMoveUp is true ONLY for index > 0; CanMoveDown ONLY for 0 ≤ index < Count-1.
    // The existing single-item test pins both ends at once; this pins the interior rows of a 3-clip
    // queue plus the not-in-the-list (IndexOf → -1), null, and non-item parameters.
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task CanMoveUpDown_TrueForInteriorIndices_FalseAtEndsAndForForeignItems()
    {
        var (vm, _, _) = Build();
        await vm.AddFilesAsync(new[] { Clip1, Clip2, Clip3 });

        vm.MoveUpCommand.CanExecute(vm.Items[0]).Should().BeFalse("index 0 has nowhere to move up to");
        vm.MoveUpCommand.CanExecute(vm.Items[1]).Should().BeTrue("an interior row can move up");
        vm.MoveUpCommand.CanExecute(vm.Items[2]).Should().BeTrue("the last row can still move up");

        vm.MoveDownCommand.CanExecute(vm.Items[0]).Should().BeTrue("the first row can move down");
        vm.MoveDownCommand.CanExecute(vm.Items[1]).Should().BeTrue("an interior row can move down");
        vm.MoveDownCommand.CanExecute(vm.Items[2]).Should().BeFalse("the last index has nowhere to move down to");

        // A foreign item (never queued here → IndexOf -1), null, and a non-item parameter are all gated off.
        var foreign = new JoinItemViewModel(@"C:\videos\d.mp4");
        vm.MoveUpCommand.CanExecute(foreign).Should().BeFalse("an item not in the list has no index");
        vm.MoveDownCommand.CanExecute(foreign).Should().BeFalse("an item not in the list has no index");
        vm.MoveUpCommand.CanExecute(null).Should().BeFalse();
        vm.MoveDownCommand.CanExecute(null).Should().BeFalse();
        vm.MoveUpCommand.CanExecute("not an item").Should().BeFalse("a non-item parameter never enables the reorder");
    }

    // SPEC-012#I20 — the derived guards are re-raised by every source that can invalidate them:
    // an item-collection change (CanRunJoin/CanClear/HasClips/RunLabel), the OutputPath setter and an
    // IsCompatible flip (CanRunJoin), and an Operation running/state change (CanClear).
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task DerivedGuards_RaisePropertyChanged_OnItemsOutputPathAndOperationState()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok();

        var seen = new List<string>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName!);

        // (a) collection change → the four item-derived readouts.
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });

        seen.Should().Contain(new[]
        {
            nameof(JoinViewModel.CanRunJoin),
            nameof(JoinViewModel.CanClear),
            nameof(JoinViewModel.HasClips),
            nameof(JoinViewModel.RunLabel),
        }, "an item-collection change re-raises every item-derived guard");

        // (b) the OutputPath setter re-raises the run guard.
        seen.Clear();
        vm.OutputPath = Output;

        seen.Should().Contain(nameof(JoinViewModel.OutputPath))
            .And.Contain(nameof(JoinViewModel.CanRunJoin), "OutputPath feeds CanRunJoin");

        // (c) an IsCompatible flip re-raises the run guard too.
        seen.Clear();
        engine.CompatToReturn = Incompatible("codec", "clip 2 is hevc, reference (clip 1) is h264");
        await vm.RefreshCompatAsync();

        vm.IsCompatible.Should().BeFalse("precondition: the verdict flipped");
        seen.Should().Contain(nameof(JoinViewModel.IsCompatible))
            .And.Contain(nameof(JoinViewModel.CanRunJoin), "IsCompatible feeds CanRunJoin");

        // (d) an Operation running/state change re-raises the Clear guard.
        engine.CompatToReturn = CompatReport.Ok();
        await vm.RefreshCompatAsync();
        var gate = new TaskCompletionSource<JoinResult>();
        engine.JoinTaskHandler = _ => gate.Task;

        seen.Clear();
        var run = vm.RunJoinAsync();

        vm.Operation.IsRunning.Should().BeTrue("precondition: the join is in flight");
        seen.Should().Contain(nameof(JoinViewModel.CanClear), "an operation state change re-raises the Clear guard");

        gate.SetResult(JoinResult.Ok(Output));
        await run;
    }

    // SPEC-012#I27 — Clear() deliberately PRESERVES OutputPath (the destination is usually still where
    // the next join goes) and is a NO-OP whenever CanClear is false (mid-run, or with an empty list).
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task Clear_PreservesOutputPath_AndIsNoOpWhileRunning()
    {
        var (vm, engine, _) = Build();
        engine.CompatToReturn = CompatReport.Ok();
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });
        vm.OutputPath = Output;

        vm.Clear();

        vm.Items.Should().BeEmpty();
        vm.OutputPath.Should().Be(Output, "Clear deliberately keeps the chosen destination for the next join");

        // Mid-run: CanClear is false → Clear() must change nothing (list intact, run untouched).
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });
        var gate = new TaskCompletionSource<JoinResult>();
        engine.JoinTaskHandler = _ => gate.Task;
        var run = vm.RunJoinAsync();
        vm.Operation.IsRunning.Should().BeTrue("precondition: the join is in flight");

        vm.Clear();

        vm.Items.Should().HaveCount(2, "Clear is a no-op while CanClear is false");
        vm.Operation.IsRunning.Should().BeTrue("a no-op Clear must not reset the running operation");

        gate.SetResult(JoinResult.Ok(Output));
        await run;

        // Once the run ends Clear works again — and a second Clear on the empty list is a harmless no-op.
        vm.Clear();
        vm.Items.Should().BeEmpty();

        var again = () => vm.Clear();
        again.Should().NotThrow("Clear with no items is a no-op, not an error");
        vm.OutputPath.Should().Be(Output, "OutputPath survives every Clear");
    }

    // SPEC-012#I17 (the EstimatedSize clause + the unprobed-clip clause) — the duration SUM and
    // HasClips are pinned above; EstimatedSize is only ever asserted with an EMPTY list ("0 B"), so
    // nothing pins that the VM sums every queued clip's on-disk size. Neither does anything pin that
    // a clip which is unsized / not yet probed contributes ZERO rather than corrupting the totals.
    [Fact]
    [Trait("serves-spec", "SPEC-012")]
    public async Task Estimates_SumClipSizes_AndTreatUnprobedClipsAsZero()
    {
        var (vm, _, _) = Build(); // FakeProbe hands both clips a 10s duration
        await vm.AddFilesAsync(new[] { Clip1, Clip2 });

        // Sizes come from disk on add (0 here — the fake paths do not exist); set them like a probe would.
        vm.Items[0].SizeBytes = 1_500_000_000L;
        vm.Items[1].SizeBytes = 1_300_000_000L;

        vm.EstimatedSize.Should().Be("2.6 GB",
            "EstimatedSize is the summed on-disk size of every queued clip (1.5 GB + 1.3 GB), human-formatted");
        vm.EstimatedSize.Should().Be(MediaFormat.FormatSize(2_800_000_000L),
            "the VM formats the SUM through the shared size formatter, not one clip's size");

        // A clip whose size could not be read contributes nothing rather than corrupting the sum.
        vm.Items[1].SizeBytes = 0;
        vm.EstimatedSize.Should().Be(MediaFormat.FormatSize(1_500_000_000L),
            "an unsized clip contributes 0 bytes to the size sum");

        // ...and the same for an unprobed clip on the duration side (20s -> 10s, not "null poisons it").
        vm.EstimatedDuration.Should().Be("0:20", "precondition: both clips are probed at 10s each");
        vm.Items[1].Duration = null;
        vm.EstimatedDuration.Should().Be("0:10", "an unprobed clip contributes 0 to the duration sum");
    }
}
