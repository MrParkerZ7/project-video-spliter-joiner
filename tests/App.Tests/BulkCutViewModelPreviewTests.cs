using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-100 tests for the Bulk Cut tab's SelectedItem + single shared preview player (epic G-037).
/// Selecting a row opens THAT file in the ONE shared <see cref="PlayerViewModel"/> (never a player per
/// row); a rapid switch re-opens through the same player; a null/cleared selection unloads it; a batch
/// run stops the preview decode. Driven over a <see cref="RecordingMediaPlayer"/> at the
/// <see cref="IMediaPlayer"/> seam — no WPF, no FFME, no real playback. The native Close→Open
/// sequencing itself lives inside <see cref="FfmeMediaPlayer"/>'s <see cref="MediaReopenGuard"/> (T-080)
/// and is proven by MediaReopenGuardTests; here we prove the VM routes every switch through the one
/// shared player's Open/Unload so that guard is engaged.
/// </summary>
public sealed class BulkCutViewModelPreviewTests
{
    /// <summary>Records Open(path) / Unload / Stop / Play / Pause / Seek in call order for the VM tests.</summary>
    private sealed class RecordingMediaPlayer : IMediaPlayer
    {
        /// <summary>Ordered op log, e.g. "Open", "Unload", "Stop".</summary>
        public List<string> Calls { get; } = new();

        /// <summary>Every path handed to <see cref="Open"/>, in order.</summary>
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

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, RecordingMediaPlayer Player) Build()
    {
        var probe = new BulkFakeProbe();
        var player = new RecordingMediaPlayer();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(),
            new FakeBulkTrimEngine(), player);
        return (vm, probe, player);
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

    // ---- Surface exists ---------------------------------------------------------------------

    [Fact]
    public void Player_IsNonNull_AndDefaultCtorStillWorks_WithNoPlayer()
    {
        // The new ctor param is optional — the legacy 5-arg construction still compiles + runs.
        var probe = new BulkFakeProbe();
        var vm = new BulkCutViewModel(probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings());

        vm.Player.Should().NotBeNull("the shared preview player is always constructed (Null player by default)");
        vm.SelectedItem.Should().BeNull("nothing is selected before any row is added");
    }

    // ---- select → one Open (prev closed first, via the guard) --------------------------------

    [Fact]
    public async Task SelectingRow_OpensThatFile_InTheOneSharedPlayer_ExactlyOnce()
    {
        var (vm, probe, player) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\a.mp4"); // auto-selects the first row

        vm.SelectedItem.Should().BeSameAs(row);
        player.OpenCount.Should().Be(1, "selecting a row opens its file exactly once in the shared player");
        player.Opened.Should().ContainSingle().Which.Should().Be(@"C:\v\a.mp4");
    }

    // ---- switch → Close-then-Open (guarded) --------------------------------------------------

    [Fact]
    public async Task SwitchingSelection_ReOpensTheNewFile_InTheSameSharedPlayer()
    {
        var (vm, probe, player) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4"); // auto-select a → Open(a)
        var rowB = await AddRowAsync(vm, probe, @"C:\v\b.mp4"); // a stays selected (already non-null)

        player.Opened.Should().ContainSingle("adding a second file does not change the selection")
            .Which.Should().Be(@"C:\v\a.mp4");

        vm.SelectedItem = rowB; // the switch

        // Two Opens through the ONE shared player, in order — the Close→Open between them is the
        // FfmeMediaPlayer MediaReopenGuard's job (proven by MediaReopenGuardTests), engaged because the
        // VM routes the switch through PlayerViewModel.Open on the single shared player.
        player.Opened.Should().Equal(@"C:\v\a.mp4", @"C:\v\b.mp4");
        vm.SelectedItem.Should().BeSameAs(rowB);
    }

    [Fact]
    public async Task RapidSwitch_NeverThrows_AndConvergesOnTheLastSelection()
    {
        var (vm, probe, player) = Build();
        var a = await AddRowAsync(vm, probe, @"C:\v\a.mp4");
        var b = await AddRowAsync(vm, probe, @"C:\v\b.mp4");
        var c = await AddRowAsync(vm, probe, @"C:\v\c.mp4");

        Action rapid = () =>
        {
            vm.SelectedItem = b;
            vm.SelectedItem = c;
            vm.SelectedItem = a;
            vm.SelectedItem = c;
        };

        rapid.Should().NotThrow("every switch is sequenced through the shared player's reopen guard");
        vm.SelectedItem.Should().BeSameAs(c);
        player.Opened[^1].Should().Be(@"C:\v\c.mp4", "the player converges on the last selection");
    }

    // ---- null / clear → Unload ---------------------------------------------------------------

    [Fact]
    public async Task NullSelection_UnloadsTheSharedPlayer()
    {
        var (vm, probe, player) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4"); // auto-select → Open

        vm.SelectedItem = null;

        player.UnloadCount.Should().Be(1, "a null selection unloads the shared player");
    }

    [Fact]
    public async Task Clear_UnloadsThePlayer_AndDropsTheSelection()
    {
        var (vm, probe, player) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4");
        vm.CanClear.Should().BeTrue();

        vm.Clear();

        vm.SelectedItem.Should().BeNull("Clear drops the selection");
        vm.Items.Should().BeEmpty();
        player.UnloadCount.Should().BeGreaterThanOrEqualTo(1, "Clear unloads the shared preview player");
        player.Calls.Should().Contain("Unload");
    }

    // ---- run-batch → player stopped ----------------------------------------------------------

    [Fact]
    public async Task RunBatch_StopsThePreviewPlayer_BeforeTrimming()
    {
        var (vm, probe, player) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", introSeconds: 10); // valid cut, auto-selected + opened
        vm.CanRunBatch.Should().BeTrue();

        await vm.RunBatchAsync();

        player.StopCount.Should().BeGreaterThanOrEqualTo(1, "a batch run stops the preview decode before trimming");
        vm.BatchState.Should().Be(BulkBatchState.Completed);
    }

    // ---- auto-select first row ---------------------------------------------------------------

    [Fact]
    public async Task FirstAddFiles_AutoSelectsFirstRow_AndOpensIt()
    {
        var (vm, probe, player) = Build();
        probe.SetUniform(@"C:\v\a.mp4", TimeSpan.FromSeconds(60), 2);
        probe.SetUniform(@"C:\v\b.mp4", TimeSpan.FromSeconds(60), 2);

        await vm.AddFilesAsync(new[] { @"C:\v\a.mp4", @"C:\v\b.mp4" });

        vm.SelectedItem.Should().BeSameAs(vm.Items[0], "the first row is auto-selected on the first add");
        player.Opened.Should().ContainSingle().Which.Should().Be(@"C:\v\a.mp4");
    }

    // ---- select while indexing still opens ---------------------------------------------------

    [Fact]
    public async Task SelectingRow_WhileKeyframesStillIndexing_StillOpens()
    {
        var (vm, probe, player) = Build();
        probe.SetUniform(@"C:\v\a.mp4", TimeSpan.FromSeconds(60), 2);
        probe.GatedPaths.Add(@"C:\v\a.mp4"); // hold the keyframe scan open

        await vm.AddFilesAsync(new[] { @"C:\v\a.mp4" });

        var row = vm.Items.Single();
        row.KeyframesReady.Should().BeFalse("the scan is still gated open");
        vm.SelectedItem.Should().BeSameAs(row, "auto-select does not wait on keyframes");
        player.Opened.Should().ContainSingle().Which.Should().Be(@"C:\v\a.mp4",
            "opening the preview does not depend on the keyframe scan");

        probe.ReleaseScans();
        await row.CurrentScanTask;
    }

    // ---- removing the selected row never opens a removed file --------------------------------

    [Fact]
    public async Task RemovingSelectedRow_RePointsSelectionAtNeighbour_AndOpensIt()
    {
        var (vm, probe, player) = Build();
        var a = await AddRowAsync(vm, probe, @"C:\v\a.mp4"); // auto-selected + Open(a)
        var b = await AddRowAsync(vm, probe, @"C:\v\b.mp4");

        vm.SelectedItem.Should().BeSameAs(a);

        vm.RemoveCommand.Execute(a); // remove the SELECTED row

        vm.Items.Should().ContainSingle().Which.Should().BeSameAs(b);
        vm.SelectedItem.Should().BeSameAs(b, "the selection re-points at the surviving neighbour");
        player.Opened[^1].Should().Be(@"C:\v\b.mp4", "the neighbour is opened; the removed file is never re-opened");
    }

    [Fact]
    public async Task RemovingSelectedLastRow_UnloadsThePlayer_AndClearsSelection()
    {
        var (vm, probe, player) = Build();
        var a = await AddRowAsync(vm, probe, @"C:\v\a.mp4");

        vm.RemoveCommand.Execute(a);

        vm.Items.Should().BeEmpty();
        vm.SelectedItem.Should().BeNull("removing the only (selected) row clears the selection");
        player.UnloadCount.Should().BeGreaterThanOrEqualTo(1, "with no rows left the shared player unloads");
    }

    [Fact]
    public async Task RemovingAnUnselectedRow_DoesNotDisturbTheSelectionOrPlayer()
    {
        var (vm, probe, player) = Build();
        var a = await AddRowAsync(vm, probe, @"C:\v\a.mp4"); // selected
        var b = await AddRowAsync(vm, probe, @"C:\v\b.mp4"); // not selected
        var opensBefore = player.OpenCount;

        vm.RemoveCommand.Execute(b); // remove the NON-selected row

        vm.SelectedItem.Should().BeSameAs(a, "removing an unselected row leaves the selection put");
        player.OpenCount.Should().Be(opensBefore, "no re-open when the removed row was not selected");
    }
}
