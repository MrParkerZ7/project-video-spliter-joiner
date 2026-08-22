using System;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// SPEC-015 app-shell-theming gaps (todo-automate) for <see cref="MainViewModel"/>: the per-tab
/// stop-inactive-players routing (I6), the decoupled caption title (I9), and the WindowTitle
/// PropertyChanged re-raise wiring (I10). Reuses the internal Bulk fakes for the injected screens.
/// </summary>
public sealed class MainViewModelSpecGapTests
{
    /// <summary>An <see cref="IMediaPlayer"/> that records how many times <see cref="Stop"/> was called.</summary>
    private sealed class RecordingPlayer : IMediaPlayer
    {
        public int StopCount { get; private set; }

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration => null;

        public bool IsPlaying => false;

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        public void Open(string path) { }

        public void Play() { }

        public void Pause() { }

        public void Stop() => StopCount++;

        public void Seek(TimeSpan t) { }

        public void Unload() { }

        public void StepFrame(int direction) { }

#pragma warning disable CS0067
        public event EventHandler? PositionChanged;

        public event EventHandler? Seeked;

        public event EventHandler? DurationAvailable;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    private static SplitViewModel BuildSplit(IMediaPlayer player) =>
        new(new BulkFakeProbe(), new ThrowingFakeSplitEngine(), player, new FakeSettings());

    private static BulkCutViewModel BuildBulk(IMediaPlayer player) =>
        new(new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(),
            new FakeBulkTrimEngine(), player);

    // SPEC-015#I6 — StopInactiveScreenPlayers stops the preview player of each non-active screen on every
    // tab switch (Split.Player.Stop() when tab != 0; BulkCut.Player.Stop() when not bulk-active).
    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public void SwitchingTabs_StopsTheDeactivatedScreensPlayer_NotTheActiveOne()
    {
        var splitPlayer = new RecordingPlayer();
        var bulkPlayer = new RecordingPlayer();
        var vm = new MainViewModel(BuildSplit(splitPlayer), joinViewModel: null, settings: null, bulkCut: BuildBulk(bulkPlayer));

        // Switch to Bulk (tab 2): Split is deactivated → its player is stopped; Bulk is active → not stopped.
        var splitBefore = splitPlayer.StopCount;
        var bulkBefore = bulkPlayer.StopCount;
        vm.SelectedTabIndex = 2;
        splitPlayer.StopCount.Should().BeGreaterThan(splitBefore, "switching away from Split stops its preview player");
        bulkPlayer.StopCount.Should().Be(bulkBefore, "the just-activated Bulk player is NOT stopped");

        // Switch to Split (tab 0): Bulk is deactivated → its player is stopped; Split is active → not stopped.
        var splitBefore2 = splitPlayer.StopCount;
        var bulkBefore2 = bulkPlayer.StopCount;
        vm.SelectedTabIndex = 0;
        bulkPlayer.StopCount.Should().BeGreaterThan(bulkBefore2, "switching away from Bulk stops its preview player");
        splitPlayer.StopCount.Should().Be(splitBefore2, "the just-activated Split player is NOT stopped");
    }

    // SPEC-015#I6 (null-safe) — a legacy no-Bulk ctor over a NullMediaPlayer must not throw on tab switches.
    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public void SwitchingTabs_NullSafe_LegacyNoBulk_DoesNotThrow()
    {
        // Split over the inert NullMediaPlayer default; no Bulk screen injected.
        var vm = new MainViewModel(new SplitViewModel(new BulkFakeProbe(), new ThrowingFakeSplitEngine()));

        var act = () =>
        {
            vm.SelectedTabIndex = 1;
            vm.SelectedTabIndex = 2;
            vm.SelectedTabIndex = 0;
        };

        act.Should().NotThrow("a null/absent Bulk screen and an inert player both no-op on tab switches");
    }

    // SPEC-015#I9 — CaptionTitle always equals BaseTitle and is decoupled from WindowTitle (the caption
    // never shows the running-progress overlay that WindowTitle carries).
    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public async Task CaptionTitle_StaysBaseTitle_WhileWindowTitleOverlaysProgress()
    {
        var vm = new MainViewModel(BuildSplit(new RecordingPlayer()));

        vm.CaptionTitle.Should().Be(MainViewModel.BaseTitle, "idle: caption is the plain app name");

        var gate = new TaskCompletionSource();
        var captionMidRun = string.Empty;
        var windowMidRun = string.Empty;

        var run = vm.Split.Operation.RunAsync(async (progress, _) =>
        {
            vm.Split.Operation.StatusText = "Splitting… (4 parts)";
            progress.Report(0.5);
            await Task.Delay(20);
            captionMidRun = vm.CaptionTitle;
            windowMidRun = vm.WindowTitle;
            await gate.Task;
        }, "Splitting…");

        gate.SetResult();
        await run;

        captionMidRun.Should().Be(MainViewModel.BaseTitle, "the caption never carries the progress overlay");
        windowMidRun.Should().StartWith("Splitting 50%",
            "the OS window title DOES overlay progress — proving CaptionTitle and WindowTitle are decoupled");
        vm.CaptionTitle.Should().Be(MainViewModel.BaseTitle, "after the run the caption is still the plain app name");
    }

    // SPEC-015#I10 — HookOperations re-raises WindowTitle on the active op's State/Progress/StatusText/etc.
    // changes; an unrelated op property (ResultSummary) does not raise it.
    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public async Task HookOperations_ReRaisesWindowTitle_OnActiveOpChanges_NotOnUnrelatedProperty()
    {
        var vm = new MainViewModel(BuildSplit(new RecordingPlayer()));

        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.WindowTitle)) raised++;
        };

        await vm.Split.Operation.RunAsync(async (progress, _) =>
        {
            progress.Report(0.3);
            await Task.Delay(20);
        }, "Splitting…");

        raised.Should().BeGreaterThan(0,
            "the active screen's op State/Progress/StatusText/EtaText changes re-raise WindowTitle");

        var before = raised;
        vm.Split.Operation.ResultSummary = "done"; // not one of the title-composing properties
        raised.Should().Be(before, "a non-title op property (ResultSummary) does not re-raise WindowTitle");
    }
}
