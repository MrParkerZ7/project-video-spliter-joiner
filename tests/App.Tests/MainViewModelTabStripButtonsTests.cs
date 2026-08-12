using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-088 — the shared Split/Join tab-strip Load / Clear buttons follow the ACTIVE screen. The
/// MainViewModel's <c>Current*</c> routing resolves the moved buttons' command + labels/tooltips from
/// <see cref="MainViewModel.SelectedTabIndex"/> (Split on 0, Join on 1) exactly like
/// <see cref="MainViewModel.CurrentOperation"/> (T-068), and re-raises them when the tab flips. Load's
/// picker lives in each view's code-behind (GUI concern, not testable here); this covers the VM-side
/// routing that the buttons bind to. Exercised through the test ctor with fake screen VMs — no ffmpeg
/// / render needed (mirrors <see cref="MainViewModelLayoutToggleTests"/>).
/// </summary>
public sealed class MainViewModelTabStripButtonsTests
{
    // ---- Minimal fakes (mirroring MainViewModelLayoutToggleTests) ---------------------------

    private sealed class FakeSettings : IAppSettings
    {
        public string? LastInputDir { get; set; }
        public string? LastOutputDir { get; set; }
        public LayoutMode LayoutMode { get; set; } = LayoutMode.Horizontal;
        public double? HorizontalSplitRatio { get; set; }
        public double? VerticalSplitRatio { get; set; }
    }

    private sealed class FakeProbe : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResult.Success(
                new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimeSpan>>(new[] { TimeSpan.Zero });

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
            => new(requested, TimeSpan.Zero);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.FromSeconds(2);
    }

    private sealed class NoOpSplitEngine : ISplitEngine
    {
        public Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<OperationStatus>? status = null, IProgress<PartProgress>? partProgress = null)
            => Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
    }

    private sealed class NoOpJoinEngine : IJoinEngine
    {
        public Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => Task.FromResult(CompatReport.Ok());

        public Task<JoinResult> JoinAsync(JoinRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<OperationStatus>? status = null)
            => Task.FromResult(JoinResult.Ok(req.OutputPath));
    }

    private static MainViewModel BuildViewModel(out SplitViewModel split, out JoinViewModel join)
    {
        var settings = new FakeSettings();
        split = new SplitViewModel(new FakeProbe(), new NoOpSplitEngine(), player: null, settings);
        join = new JoinViewModel(new NoOpJoinEngine(), new FakeProbe(), settings);
        return new MainViewModel(split, join, settings);
    }

    // ---- Tests ------------------------------------------------------------------------------

    [Fact]
    public void OnSplitTab_RoutesToSplitScreen_WithSplitLabels()
    {
        var vm = BuildViewModel(out var split, out _);
        vm.SelectedTabIndex = 0;   // Split

        vm.CurrentClearCommand.Should().BeSameAs(split.ClearCommand,
            "the shared Clear button drives the ACTIVE screen — Split's ClearCommand on tab 0");
        vm.CurrentLoadLabel.Should().Be("Load…");
        vm.CurrentClearLabel.Should().Be("Clear");
        vm.CurrentLoadTooltip.Should().Be("Open a video file to split");
        vm.CurrentClearTooltip.Should().Be("Unload the current file and reset the Split screen");
    }

    [Fact]
    public void OnJoinTab_RoutesToJoinScreen_WithJoinLabels()
    {
        var vm = BuildViewModel(out _, out var join);
        vm.SelectedTabIndex = 1;   // Join

        vm.CurrentClearCommand.Should().BeSameAs(join.ClearCommand,
            "the shared Clear button drives the ACTIVE screen — Join's ClearCommand on tab 1");
        vm.CurrentLoadLabel.Should().Be("Add files…");
        vm.CurrentClearLabel.Should().Be("Clear all");
        vm.CurrentLoadTooltip.Should().Be("Add one or more video clips to the join queue");
        vm.CurrentClearTooltip.Should().Be("Remove all queued clips and reset the Join screen");
    }

    [Fact]
    public void SwitchingTab_RaisesPropertyChanged_ForTheTabStripButtonBindings()
    {
        var vm = BuildViewModel(out _, out _);
        vm.SelectedTabIndex = 0;   // start on Split

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedTabIndex = 1;   // flip to Join

        changed.Should().Contain(nameof(MainViewModel.CurrentClearCommand),
            "the shared Clear button re-binds to the newly-active screen's command");
        changed.Should().Contain(nameof(MainViewModel.CurrentLoadLabel));
        changed.Should().Contain(nameof(MainViewModel.CurrentClearLabel));
        changed.Should().Contain(nameof(MainViewModel.CurrentLoadTooltip));
        changed.Should().Contain(nameof(MainViewModel.CurrentClearTooltip));
    }

    [Fact]
    public void SwitchingTab_ReRoutesTheClearCommand_BetweenScreens()
    {
        var vm = BuildViewModel(out var split, out var join);

        vm.SelectedTabIndex = 0;
        vm.CurrentClearCommand.Should().BeSameAs(split.ClearCommand);

        vm.SelectedTabIndex = 1;
        vm.CurrentClearCommand.Should().BeSameAs(join.ClearCommand,
            "flipping the tab must re-point the shared Clear button at the active screen");

        vm.SelectedTabIndex = 0;
        vm.CurrentClearCommand.Should().BeSameAs(split.ClearCommand,
            "and back again");
    }
}
