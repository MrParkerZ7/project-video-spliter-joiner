using System;
using System.Collections.Generic;
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

        public JoinRequest? LastRequest { get; private set; }

        public int CompatCheckCount { get; private set; }

        public IReadOnlyList<string>? LastCheckedPaths { get; private set; }

        public Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default)
        {
            CompatCheckCount++;
            LastCheckedPaths = inputPaths.ToList();
            return Task.FromResult(CompatToReturn);
        }

        public Task<JoinResult> JoinAsync(JoinRequest req, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            LastRequest = req;
            progress?.Report(0.5);
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
}
