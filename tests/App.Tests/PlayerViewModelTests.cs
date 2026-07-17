using System;
using System.Collections.Generic;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for <see cref="PlayerViewModel"/> over a <see cref="FakeMediaPlayer"/> — no WPF,
/// no MediaElement, no real playback. The fake records transport calls and lets tests raise the
/// player's events (DurationAvailable / PositionChanged / Ended / Failed) so the VM's observable
/// state, command guards, and the seek-feedback guard can be verified deterministically.
/// </summary>
public sealed class PlayerViewModelTests
{
    // ---- Fake -------------------------------------------------------------------------------

    /// <summary>Records Open/Play/Pause/Stop/Seek and exposes event raisers to the tests.</summary>
    private sealed class FakeMediaPlayer : IMediaPlayer
    {
        public List<string> Calls { get; } = new();

        public List<string> Opened { get; } = new();

        public List<TimeSpan> Seeks { get; } = new();

        /// <summary>Every StepFrame direction, in call order (+1 forward, −1 back).</summary>
        public List<int> Steps { get; } = new();

        /// <summary>The last StepFrame direction, or 0 if never stepped.</summary>
        public int LastStepDirection { get; private set; }

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public void Open(string path)
        {
            Calls.Add("Open");
            Opened.Add(path);
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
            Seeks.Add(t);
            Position = t;
        }

        public void StepFrame(int direction)
        {
            Calls.Add("StepFrame");
            Steps.Add(direction);
            LastStepDirection = direction;
        }

        // ---- Event raisers (test-driven) ----
        public void RaiseDurationAvailable(TimeSpan duration)
        {
            Duration = duration;
            DurationAvailable?.Invoke(this, EventArgs.Empty);
        }

        public void RaisePositionChanged(TimeSpan position)
        {
            Position = position;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);

        public void RaiseFailed(string reason) => Failed?.Invoke(this, reason);

        public event EventHandler? PositionChanged;

        public event EventHandler? DurationAvailable;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
    }

    private static (PlayerViewModel Vm, FakeMediaPlayer Player) Build()
    {
        var player = new FakeMediaPlayer();
        return (new PlayerViewModel(player), player);
    }

    private static (PlayerViewModel Vm, FakeMediaPlayer Player) BuildReady(double durationSeconds = 60)
    {
        var (vm, player) = Build();
        player.RaiseDurationAvailable(TimeSpan.FromSeconds(durationSeconds));
        return (vm, player);
    }

    // ---- Readiness --------------------------------------------------------------------------

    [Fact]
    public void DurationAvailable_SetsDurationAndIsReady()
    {
        var (vm, player) = Build();
        vm.IsReady.Should().BeFalse();

        player.RaiseDurationAvailable(TimeSpan.FromSeconds(42));

        vm.IsReady.Should().BeTrue();
        vm.Duration.Should().Be(TimeSpan.FromSeconds(42));
        vm.DurationSeconds.Should().Be(42);
        vm.PlayPauseCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void PlayPause_Guarded_WhenNotReady()
    {
        var (vm, player) = Build();

        vm.PlayPauseCommand.CanExecute(null).Should().BeFalse();
        vm.PlayPause(); // direct call must also no-op when not ready

        vm.IsPlaying.Should().BeFalse();
        player.Calls.Should().NotContain("Play");
    }

    // ---- Play / pause -----------------------------------------------------------------------

    [Fact]
    public void PlayPause_Toggles_IsPlaying_And_Calls_Play_Then_Pause()
    {
        var (vm, player) = BuildReady();

        vm.PlayPause();
        vm.IsPlaying.Should().BeTrue();
        vm.PlayPauseLabel.Should().Be("Pause");

        vm.PlayPause();
        vm.IsPlaying.Should().BeFalse();
        vm.PlayPauseLabel.Should().Be("Play");

        player.Calls.Should().ContainInOrder("Play", "Pause");
    }

    // ---- PositionChanged (no feedback re-seek) ----------------------------------------------

    [Fact]
    public void PositionChanged_Updates_Position_And_Text_WithNoReSeek()
    {
        var (vm, player) = BuildReady();

        player.RaisePositionChanged(TimeSpan.FromSeconds(12.3));

        vm.Position.Should().Be(TimeSpan.FromSeconds(12.3));
        vm.PositionText.Should().Be("00:12.3");
        vm.PositionSeconds.Should().BeApproximately(12.3, 0.0001);
        // The playback-driven update must NOT loop back into a Seek.
        player.Calls.Should().NotContain("Seek");
        player.Seeks.Should().BeEmpty();
    }

    // ---- User scrub (Position setter → Seek) ------------------------------------------------

    [Fact]
    public void SettingPosition_UserScrub_CallsSeek()
    {
        var (vm, player) = BuildReady();

        vm.Position = TimeSpan.FromSeconds(20);

        player.Calls.Should().Contain("Seek");
        player.Seeks.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void SettingPositionSeconds_UserScrub_CallsSeek()
    {
        var (vm, player) = BuildReady();

        vm.PositionSeconds = 15;

        player.Seeks.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Scrub_CallsSeek()
    {
        var (vm, player) = BuildReady();

        vm.Scrub(TimeSpan.FromSeconds(33));

        player.Seeks.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(33));
    }

    // ---- Stop -------------------------------------------------------------------------------

    [Fact]
    public void Stop_CallsStop_ClearsIsPlaying_RewindsPosition()
    {
        var (vm, player) = BuildReady();
        vm.PlayPause(); // playing
        player.RaisePositionChanged(TimeSpan.FromSeconds(10));

        vm.Stop();

        player.Calls.Should().Contain("Stop");
        vm.IsPlaying.Should().BeFalse();
        vm.Position.Should().Be(TimeSpan.Zero);
    }

    // ---- Ended ------------------------------------------------------------------------------

    [Fact]
    public void Ended_ClearsIsPlaying()
    {
        var (vm, player) = BuildReady();
        vm.PlayPause();
        vm.IsPlaying.Should().BeTrue();

        player.RaiseEnded();

        vm.IsPlaying.Should().BeFalse();
    }

    // ---- Failed -----------------------------------------------------------------------------

    [Fact]
    public void Failed_SetsPreviewFailed_SurfacesReason_NoCrash()
    {
        var (vm, player) = Build();

        var act = () => player.RaiseFailed("codec not supported");

        act.Should().NotThrow();
        vm.PreviewFailed.Should().BeTrue();
        vm.PreviewFailedReason.Should().Be("codec not supported");
        vm.IsPlaying.Should().BeFalse();
    }

    // ---- Open (T-013 seam) ------------------------------------------------------------------

    [Fact]
    public void Open_CallsPlayerOpen_ResetsState()
    {
        var (vm, player) = BuildReady();
        player.RaiseFailed("boom");

        vm.Open(@"C:\videos\next.mp4");

        player.Opened.Should().ContainSingle().Which.Should().Be(@"C:\videos\next.mp4");
        vm.PreviewFailed.Should().BeFalse("opening a new file resets the failure banner");
        vm.Duration.Should().BeNull();
        vm.IsReady.Should().BeFalse();
        vm.Position.Should().Be(TimeSpan.Zero);
    }

    // ---- Clamping ---------------------------------------------------------------------------

    [Fact]
    public void Position_ClampedToZeroAndDuration()
    {
        var (vm, player) = BuildReady(30);

        // Seed a non-zero position so the subsequent clamp-to-zero is a real change (and seeks).
        vm.Position = TimeSpan.FromSeconds(10);

        vm.Position = TimeSpan.FromSeconds(-5);
        vm.Position.Should().Be(TimeSpan.Zero);
        player.Seeks[^1].Should().Be(TimeSpan.Zero);

        vm.Position = TimeSpan.FromSeconds(999);
        vm.Position.Should().Be(TimeSpan.FromSeconds(30));
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(30));
    }

    // ---- Skip / jog (T-028) -----------------------------------------------------------------

    [Fact]
    public void SkipBy_Forward_SeeksToPositionPlusDelta()
    {
        var (vm, player) = BuildReady(60);
        player.RaisePositionChanged(TimeSpan.FromSeconds(5));

        vm.SkipBy(TimeSpan.FromSeconds(10));

        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void SkipBy_Backward_ClampsToZero()
    {
        var (vm, player) = BuildReady(60);
        player.RaisePositionChanged(TimeSpan.FromSeconds(5));

        vm.SkipBy(TimeSpan.FromSeconds(-10));

        player.Seeks[^1].Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void SkipBy_PastDuration_ClampsToDuration()
    {
        var (vm, player) = BuildReady(60);
        player.RaisePositionChanged(TimeSpan.FromSeconds(5));

        vm.SkipBy(TimeSpan.FromSeconds(300));

        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void SkipBy_NoOp_WhenNotReady()
    {
        var (vm, player) = Build();

        vm.SkipBy(TimeSpan.FromSeconds(10));

        player.Calls.Should().NotContain("Seek");
    }

    [Fact]
    public void SkipCommand_ParsesSecondsParameter_AndSeeks()
    {
        var (vm, player) = BuildReady(60);
        player.RaisePositionChanged(TimeSpan.FromSeconds(5));

        vm.SkipCommand.Execute("10");   // string parameter, as XAML supplies it
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(15));

        // The player echoes its new position back through PositionChanged (as FFME does), which the
        // VM mirrors into its Position — so the next relative jog is measured from 15s.
        player.RaisePositionChanged(TimeSpan.FromSeconds(15));

        vm.SkipCommand.Execute("-5");
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(10));
    }

    // ---- Jump to ends (T-028) ---------------------------------------------------------------

    [Fact]
    public void JumpToStart_SeeksToZero()
    {
        var (vm, player) = BuildReady(60);
        player.RaisePositionChanged(TimeSpan.FromSeconds(30));

        vm.JumpToStartCommand.Execute(null);

        player.Seeks[^1].Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void JumpToEnd_SeeksToDuration()
    {
        var (vm, player) = BuildReady(45);

        vm.JumpToEndCommand.Execute(null);

        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(45));
    }

    // ---- Frame step (T-028) -----------------------------------------------------------------

    [Fact]
    public void StepForwardCommand_StepsPlayerPlusOne()
    {
        var (vm, player) = BuildReady();

        vm.StepForwardCommand.Execute(null);

        player.LastStepDirection.Should().Be(+1);
        player.Steps.Should().ContainSingle().Which.Should().Be(+1);
    }

    [Fact]
    public void StepBackCommand_StepsPlayerMinusOne()
    {
        var (vm, player) = BuildReady();

        vm.StepBackCommand.Execute(null);

        player.LastStepDirection.Should().Be(-1);
        player.Steps.Should().ContainSingle().Which.Should().Be(-1);
    }

    [Fact]
    public void StepFrame_NoOp_WhenNotReady()
    {
        var (vm, player) = Build();

        vm.StepFrame(+1);

        player.Calls.Should().NotContain("StepFrame");
        player.Steps.Should().BeEmpty();
    }

    // ---- Command guards: all jog/step/jump gated on IsReady ---------------------------------

    [Fact]
    public void JogStepJumpCommands_CanExecuteFalse_WhenNotReady_TrueAfter()
    {
        var (vm, player) = Build();

        vm.SkipCommand.CanExecute("10").Should().BeFalse();
        vm.JumpToStartCommand.CanExecute(null).Should().BeFalse();
        vm.JumpToEndCommand.CanExecute(null).Should().BeFalse();
        vm.StepForwardCommand.CanExecute(null).Should().BeFalse();
        vm.StepBackCommand.CanExecute(null).Should().BeFalse();

        player.RaiseDurationAvailable(TimeSpan.FromSeconds(60));

        vm.SkipCommand.CanExecute("10").Should().BeTrue();
        vm.JumpToStartCommand.CanExecute(null).Should().BeTrue();
        vm.JumpToEndCommand.CanExecute(null).Should().BeTrue();
        vm.StepForwardCommand.CanExecute(null).Should().BeTrue();
        vm.StepBackCommand.CanExecute(null).Should().BeTrue();
    }
}
