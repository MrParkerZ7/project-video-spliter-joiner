using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-101 tests for the Bulk Cut tab's two "set at playhead" gestures (epic G-037). The commands live
/// WPF-free on <see cref="BulkCutViewModel"/>: they read the selected row + the shared preview player's
/// position and write the row's intro-end / outro-start (which re-snap to the row's keyframes), so a
/// cut can be placed by watching. Driven over a small <see cref="ReadyMediaPlayer"/> at the
/// <see cref="IMediaPlayer"/> seam (the seam a real FFME element sits behind) — no WPF, no FFME.
/// </summary>
public sealed class BulkCutViewModelSetAtPlayheadTests
{
    /// <summary>
    /// A fake player that can be made ready (duration known → <see cref="PlayerViewModel.IsReady"/>) and
    /// have its playhead moved, each raising the same event the real player raises so the bound
    /// <see cref="PlayerViewModel"/> reflects it.
    /// </summary>
    private sealed class ReadyMediaPlayer : IMediaPlayer
    {
        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        /// <summary>Simulate the duration arriving from the decoder — flips the VM to ready.</summary>
        public void MakeReady(TimeSpan duration)
        {
            Duration = duration;
            DurationAvailable?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Simulate a playback tick / settled seek at <paramref name="t"/> (no seek armed).</summary>
        public void MovePlayheadTo(TimeSpan t)
        {
            Position = t;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Open(string path)
        {
            IsPlaying = false;
            Duration = null;
            Position = TimeSpan.Zero;
        }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;

        public void Stop()
        {
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void Seek(TimeSpan t) => Position = t;

        public void Unload()
        {
            Duration = null;
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void StepFrame(int direction)
        {
        }

        public event EventHandler? PositionChanged;

        public event EventHandler? DurationAvailable;

#pragma warning disable CS0067 // These are raised by the real player; this fake never fires them.
        public event EventHandler? Seeked;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, ReadyMediaPlayer Player) Build()
    {
        var probe = new BulkFakeProbe();
        var player = new ReadyMediaPlayer();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(),
            new FakeBulkTrimEngine(), player);
        return (vm, probe, player);
    }

    /// <summary>Add one row (auto-selected as the first), keyframes every 2s, and await the scan.</summary>
    private static async Task<BulkItemViewModel> AddReadyRowAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double durationSeconds = 120)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(durationSeconds), stepSeconds: 2);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask; // keyframes landed + handle snaps resolved
        return row;
    }

    // ---- intro-end at playhead ---------------------------------------------------------------

    [Fact]
    public async Task SetIntroAtPlayhead_WritesSelectedRowIntroEnd_FromPlayhead_AndSnaps()
    {
        var (vm, probe, player) = Build();
        var row = await AddReadyRowAsync(vm, probe, @"C:\v\a.mp4"); // auto-selected + opened
        player.MakeReady(TimeSpan.FromSeconds(120));                 // duration arrives → IsReady
        player.MovePlayheadTo(TimeSpan.FromSeconds(31));             // playhead between keyframes 30/32

        vm.CanSetCutAtPlayhead.Should().BeTrue();
        vm.SetIntroAtPlayheadCommand.CanExecute(null).Should().BeTrue();

        vm.SetIntroAtPlayheadCommand.Execute(null);

        row.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(31), "the intro-end is captured from the playhead");
        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(30), "the write re-snaps to the nearest keyframe (30s)");
    }

    // ---- outro-start at playhead: add when none, move when present ---------------------------

    [Fact]
    public async Task SetOutroAtPlayhead_AddsOutro_WhenRowHasNone_FromPlayhead_AndSnaps()
    {
        var (vm, probe, player) = Build();
        var row = await AddReadyRowAsync(vm, probe, @"C:\v\a.mp4");
        row.HasOutro.Should().BeFalse("a fresh row has no outro handle");
        player.MakeReady(TimeSpan.FromSeconds(120));
        player.MovePlayheadTo(TimeSpan.FromSeconds(101)); // between keyframes 100/102

        vm.SetOutroAtPlayheadCommand.Execute(null);

        row.HasOutro.Should().BeTrue("setting outro-start with no outro adds the handle");
        row.OutroStart!.Requested.Should().Be(TimeSpan.FromSeconds(101), "the outro-start is captured from the playhead");
        row.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(100), "the added outro re-snaps to the nearest keyframe (100s)");
    }

    [Fact]
    public async Task SetOutroAtPlayhead_MovesExistingOutro_WhenRowAlreadyHasOne()
    {
        var (vm, probe, player) = Build();
        var row = await AddReadyRowAsync(vm, probe, @"C:\v\a.mp4");
        row.AddOutro(TimeSpan.FromSeconds(90)); // pre-existing outro at 90s
        var existing = row.OutroStart;
        player.MakeReady(TimeSpan.FromSeconds(120));
        player.MovePlayheadTo(TimeSpan.FromSeconds(111)); // between keyframes 110/112

        vm.SetOutroAtPlayheadCommand.Execute(null);

        row.OutroStart.Should().BeSameAs(existing, "moving an existing outro does not replace the handle");
        row.OutroStart!.Requested.Should().Be(TimeSpan.FromSeconds(111), "the existing outro-start moves to the playhead");
        row.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(110), "the moved outro re-snaps to the nearest keyframe (110s)");
    }

    // ---- disabled when no selection / player not ready ---------------------------------------

    [Fact]
    public void SetAtPlayhead_Disabled_AndNoOp_WhenNoSelection()
    {
        var (vm, _, player) = Build();
        player.MakeReady(TimeSpan.FromSeconds(120)); // ready, but nothing selected

        vm.SelectedItem.Should().BeNull();
        vm.CanSetCutAtPlayhead.Should().BeFalse("no row is selected");
        vm.SetIntroAtPlayheadCommand.CanExecute(null).Should().BeFalse();
        vm.SetOutroAtPlayheadCommand.CanExecute(null).Should().BeFalse();

        // A forced Execute is a guarded no-op (never throws with no selection).
        Action forced = () =>
        {
            vm.SetIntroAtPlayheadCommand.Execute(null);
            vm.SetOutroAtPlayheadCommand.Execute(null);
        };
        forced.Should().NotThrow();
    }

    [Fact]
    public async Task SetAtPlayhead_Disabled_AndNoOp_WhenPlayerNotReady()
    {
        var (vm, probe, _) = Build();
        var row = await AddReadyRowAsync(vm, probe, @"C:\v\a.mp4"); // selected, but player never made ready
        var introBefore = row.IntroEnd.Requested;

        vm.Player.IsReady.Should().BeFalse("the preview player has no duration yet");
        vm.CanSetCutAtPlayhead.Should().BeFalse("the player is not ready");
        vm.SetIntroAtPlayheadCommand.CanExecute(null).Should().BeFalse();
        vm.SetOutroAtPlayheadCommand.CanExecute(null).Should().BeFalse();

        vm.SetIntroAtPlayheadCommand.Execute(null);
        vm.SetOutroAtPlayheadCommand.Execute(null);

        row.IntroEnd.Requested.Should().Be(introBefore, "an unready player leaves the row untouched");
        row.HasOutro.Should().BeFalse("no outro is added while the player is not ready");
    }

    // ---- readiness flips re-raise the guard --------------------------------------------------

    [Fact]
    public async Task PlayerBecomingReady_EnablesTheGesture_ForTheSelectedRow()
    {
        var (vm, probe, player) = Build();
        await AddReadyRowAsync(vm, probe, @"C:\v\a.mp4"); // selected, not yet ready
        vm.CanSetCutAtPlayhead.Should().BeFalse();

        player.MakeReady(TimeSpan.FromSeconds(120)); // duration arrives

        vm.CanSetCutAtPlayhead.Should().BeTrue("a selected row + a now-ready player enables the gesture");
        vm.SetIntroAtPlayheadCommand.CanExecute(null).Should().BeTrue();
    }
}
