using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// D-004 / T-097 — the shared tab-strip Load/Clear buttons + taskbar/title follow the ACTIVE screen
/// across THREE tabs now (Split 0 / Join 1 / Bulk Cut 2). Extends
/// <see cref="MainViewModelTabStripButtonsTests"/> to the Bulk aggregate op + labels/tooltips, proves
/// the switch re-raises the button bindings, and proves a legacy 3-arg test ctor (no BulkCut) falls
/// back to Split on tab 2 without throwing. VM-side only (no ffmpeg / render), via the extended test
/// ctor with fake-backed screen VMs.
/// </summary>
public sealed class MainViewModelBulkTabRoutingTests
{
    // ---- Minimal fakes (Bulk fakes reused from BulkTestFakes.cs) -----------------------------

    private sealed class NoOpJoinEngine : IJoinEngine
    {
        public Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => Task.FromResult(CompatReport.Ok());

        public Task<JoinResult> JoinAsync(JoinRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<OperationStatus>? status = null)
            => Task.FromResult(JoinResult.Ok(req.OutputPath));
    }

    private static MainViewModel Build(out SplitViewModel split, out JoinViewModel join, out BulkCutViewModel bulk)
    {
        var settings = new FakeSettings();
        var probe = new BulkFakeProbe();
        split = new SplitViewModel(probe, new ThrowingFakeSplitEngine(), player: null, settings);
        join = new JoinViewModel(new NoOpJoinEngine(), probe, settings);
        bulk = new BulkCutViewModel(probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, new FakeBulkTrimEngine());
        return new MainViewModel(split, join, settings, bulk);
    }

    // ---- Tests ------------------------------------------------------------------------------

    [Fact]
    public void OnBulkTab_RoutesToBulkScreen_WithBulkLabelsAndOperation()
    {
        var vm = Build(out _, out _, out var bulk);
        vm.SelectedTabIndex = 2; // Bulk Cut

        vm.CurrentOperation.Should().BeSameAs(bulk.Operation,
            "the taskbar/title bind CurrentOperation, which follows the Bulk aggregate op on tab 2");
        vm.CurrentClearCommand.Should().BeSameAs(bulk.ClearCommand,
            "the shared Clear button drives Bulk's ClearCommand on tab 2");
        vm.CurrentLoadLabel.Should().Be("Add videos…");
        vm.CurrentClearLabel.Should().Be("Clear all");
        vm.CurrentLoadTooltip.Should().Be("Add videos to bulk-trim their intro/outro");
        vm.CurrentClearTooltip.Should().Be("Remove all videos and reset the Bulk Cut screen");
    }

    [Fact]
    public void SwitchingToBulk_RaisesPropertyChanged_ForTheTabStripBindings()
    {
        var vm = Build(out _, out _, out _);
        vm.SelectedTabIndex = 0; // start on Split

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedTabIndex = 2; // flip to Bulk Cut

        changed.Should().Contain(nameof(MainViewModel.CurrentOperation));
        changed.Should().Contain(nameof(MainViewModel.CurrentClearCommand));
        changed.Should().Contain(nameof(MainViewModel.CurrentLoadLabel));
        changed.Should().Contain(nameof(MainViewModel.CurrentClearLabel));
        changed.Should().Contain(nameof(MainViewModel.CurrentLoadTooltip));
        changed.Should().Contain(nameof(MainViewModel.CurrentClearTooltip));
        changed.Should().Contain(nameof(MainViewModel.WindowTitle));
    }

    [Fact]
    public void SwitchingAcrossAllThreeTabs_ReRoutesTheClearCommand()
    {
        var vm = Build(out var split, out var join, out var bulk);

        vm.SelectedTabIndex = 2;
        vm.CurrentClearCommand.Should().BeSameAs(bulk.ClearCommand);
        vm.CurrentOperation.Should().BeSameAs(bulk.Operation);

        vm.SelectedTabIndex = 1;
        vm.CurrentClearCommand.Should().BeSameAs(join.ClearCommand);
        vm.CurrentOperation.Should().BeSameAs(join.Operation);

        vm.SelectedTabIndex = 0;
        vm.CurrentClearCommand.Should().BeSameAs(split.ClearCommand);
        vm.CurrentOperation.Should().BeSameAs(split.Operation);
    }

    [Fact]
    public void NullBulk_LegacyThreeArgCtor_FallsBackToSplitOnTab2_WithoutThrowing()
    {
        var settings = new FakeSettings();
        var probe = new BulkFakeProbe();
        var split = new SplitViewModel(probe, new ThrowingFakeSplitEngine(), player: null, settings);
        var join = new JoinViewModel(new NoOpJoinEngine(), probe, settings);

        // The legacy 3-arg test ctor leaves BulkCut null.
        var vm = new MainViewModel(split, join, settings);

        Action act = () => vm.SelectedTabIndex = 2;
        act.Should().NotThrow();

        vm.CurrentOperation.Should().BeSameAs(split.Operation, "tab 2 with no BulkCut falls back to Split");
        vm.CurrentClearCommand.Should().BeSameAs(split.ClearCommand);
        vm.CurrentLoadLabel.Should().Be("Load…", "the Bulk labels only apply when a BulkCut exists");
    }

    [Fact]
    public async Task Tab2_Running_WindowTitle_OverlaysTheBulkAggregateVerbAndPercent()
    {
        var vm = Build(out _, out _, out var bulk);
        vm.SelectedTabIndex = 2;

        var gate = new TaskCompletionSource();
        var titleMidRun = string.Empty;

        var run = bulk.Operation.RunAsync(async (progress, _) =>
        {
            bulk.Operation.StatusText = "Trimming… (2)";
            progress.Report(0.5);
            await Task.Delay(20);
            titleMidRun = vm.WindowTitle;
            await gate.Task;
        }, "Trimming…");

        gate.SetResult();
        await run;

        titleMidRun.Should().StartWith("Trimming 50%",
            "on tab 2 the window title is composed from the Bulk aggregate op (CurrentOperation)");
        titleMidRun.Should().EndWith("— " + MainViewModel.BaseTitle);
    }
}
