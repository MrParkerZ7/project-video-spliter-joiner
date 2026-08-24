using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Thumbnails;
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

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

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

        public void Unload()
        {
            Calls.Add("Unload");
            Duration = null;
            IsPlaying = false;
            Position = TimeSpan.Zero;
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

        /// <summary>Simulate FFME's async seek settling: set the position and raise Seeked.</summary>
        public void RaiseSeeked(TimeSpan position)
        {
            Position = position;
            Seeked?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);

        public void RaiseFailed(string reason) => Failed?.Invoke(this, reason);

        public event EventHandler? PositionChanged;

        public event EventHandler? Seeked;

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

    /// <summary>A controllable monotonic millisecond clock for the live-scrub throttle tests (T-051).</summary>
    private sealed class FakeClock
    {
        public long NowMs { get; set; }

        public void Advance(long ms) => NowMs += ms;
    }

    /// <summary>Build a ready VM whose throttle clock the test drives via <paramref name="clock"/> (T-051).</summary>
    private static (PlayerViewModel Vm, FakeMediaPlayer Player) BuildReadyWithClock(FakeClock clock, double durationSeconds = 60)
    {
        var player = new FakeMediaPlayer();
        var vm = new PlayerViewModel(player, () => clock.NowMs);
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

    // ---- Scrub pop-back guard (T-033) -------------------------------------------------------

    [Fact]
    public void UserSeek_StaleEcho_DoesNotPopPositionOffTarget()
    {
        // The KEY regression: user seeks to T; a stale echo P ≠ T (outside tolerance) arrives while
        // the async seek is still in flight → the display must STAY at T, not pop back to P.
        var (vm, player) = BuildReady(60);

        vm.Position = TimeSpan.FromSeconds(40);          // user scrub → seek to 40
        vm.Position.Should().Be(TimeSpan.FromSeconds(40), "display pins at the seek target immediately");

        player.RaisePositionChanged(TimeSpan.FromSeconds(12)); // STALE echo far from 40

        vm.Position.Should().Be(TimeSpan.FromSeconds(40), "a stale echo must not pop the playhead off the target");
    }

    [Fact]
    public void UserSeek_SeekedEvent_ClearsHold_ThenNormalEchoUpdates()
    {
        var (vm, player) = BuildReady(60);

        vm.Position = TimeSpan.FromSeconds(40);
        player.RaisePositionChanged(TimeSpan.FromSeconds(12)); // stale, held
        vm.Position.Should().Be(TimeSpan.FromSeconds(40));

        // Deterministic release: the player signals the seek completed at the target.
        player.RaiseSeeked(TimeSpan.FromSeconds(40));
        vm.Position.Should().Be(TimeSpan.FromSeconds(40));

        // Hold released → subsequent playback echo now moves the display normally.
        player.RaisePositionChanged(TimeSpan.FromSeconds(41));
        vm.Position.Should().Be(TimeSpan.FromSeconds(41), "after the seek settles, normal playback echo resumes");
    }

    [Fact]
    public void UserSeek_EchoWithinTolerance_SettlesHold_AndResumesEchoes()
    {
        var (vm, player) = BuildReady(60);

        vm.Position = TimeSpan.FromSeconds(40);

        // An echo within ~250ms of the target counts as "landed" → hold clears, update applies.
        player.RaisePositionChanged(TimeSpan.FromSeconds(40.1));
        vm.Position.Should().BeCloseTo(TimeSpan.FromSeconds(40.1), TimeSpan.FromMilliseconds(1));

        // Not frozen: a later distinct echo moves the display.
        player.RaisePositionChanged(TimeSpan.FromSeconds(41));
        vm.Position.Should().Be(TimeSpan.FromSeconds(41));
    }

    [Fact]
    public void Scrub_HoldsTargetUnderStaleEcho()
    {
        var (vm, player) = BuildReady(60);

        vm.Scrub(TimeSpan.FromSeconds(50));
        vm.Position.Should().Be(TimeSpan.FromSeconds(50));

        player.RaisePositionChanged(TimeSpan.FromSeconds(20)); // stale
        vm.Position.Should().Be(TimeSpan.FromSeconds(50), "skip/jump seeks hold their target too");
    }

    [Fact]
    public void SkipBy_HoldsTargetUnderStaleEcho()
    {
        var (vm, player) = BuildReady(60);
        player.RaisePositionChanged(TimeSpan.FromSeconds(10)); // establish current position (not seeking)

        vm.SkipBy(TimeSpan.FromSeconds(20));                   // → seek to 30
        vm.Position.Should().Be(TimeSpan.FromSeconds(30));

        player.RaisePositionChanged(TimeSpan.FromSeconds(11)); // stale echo near old pos
        vm.Position.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void UserSeek_NeverMatchingEcho_ReleasesHold_AfterBoundedTicks_NotFrozenForever()
    {
        // Anti-freeze backstop: if echoes never hit the target and no Seeked ever fires, the hold
        // must release after a bounded number of ticks so the slider is not stuck permanently.
        var (vm, player) = BuildReady(600);

        vm.Position = TimeSpan.FromSeconds(300);

        // Pump many off-target echoes (advancing playback that never equals the target).
        for (var i = 0; i < 30; i++)
        {
            player.RaisePositionChanged(TimeSpan.FromSeconds(100 + i));
        }

        // The hold has since released; the display now tracks live echoes again (not pinned at 300).
        vm.Position.Should().NotBe(TimeSpan.FromSeconds(300), "the hold must not freeze the slider forever");
    }

    [Fact]
    public void UserSeek_StaleEcho_DoesNotTriggerReSeek()
    {
        // Existing seek-feedback protection stays intact: a player-driven echo never re-seeks, even
        // while the seek-target hold is active.
        var (vm, player) = BuildReady(60);

        vm.Position = TimeSpan.FromSeconds(40); // one user seek
        var seekCountAfterUserScrub = player.Seeks.Count;

        player.RaisePositionChanged(TimeSpan.FromSeconds(12)); // stale echo
        player.RaiseSeeked(TimeSpan.FromSeconds(40));          // settle
        player.RaisePositionChanged(TimeSpan.FromSeconds(41)); // normal echo

        player.Seeks.Count.Should().Be(seekCountAfterUserScrub, "echoes must not loop back into a Seek");
    }

    [Fact]
    public void DragScrub_SuppressesEchoes_ThenSeeksOnRelease()
    {
        var (vm, player) = BuildReady(60);

        vm.BeginUserScrub();
        player.RaisePositionChanged(TimeSpan.FromSeconds(5)); // echo while dragging → ignored
        vm.Position.Should().NotBe(TimeSpan.FromSeconds(5), "echoes are suppressed while dragging");

        vm.EndUserScrub(35);                                  // release at 35 → seek + hold
        vm.Position.Should().Be(TimeSpan.FromSeconds(35));
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(35));

        player.RaisePositionChanged(TimeSpan.FromSeconds(9)); // stale echo after release → held
        vm.Position.Should().Be(TimeSpan.FromSeconds(35));
    }

    // ---- Live coalesced + throttled scrub (T-051) -------------------------------------------

    [Fact]
    public void ScrubPreview_NotInFlight_SeeksImmediately()
    {
        var clock = new FakeClock();
        var (vm, player) = BuildReadyWithClock(clock);

        vm.ScrubPreview(TimeSpan.FromSeconds(10));

        player.Seeks.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ScrubPreview_Coalesces_OneNowThenLatestOnSeeked()
    {
        // The KEY anti-lag behavior: three rapid previews while a seek is in flight → exactly ONE seek
        // now, and after the in-flight seek settles → exactly ONE more, to the LAST target (t3). The
        // intermediate target (t2) is dropped — no backlog of three seeks.
        var clock = new FakeClock();
        var (vm, player) = BuildReadyWithClock(clock);

        vm.ScrubPreview(TimeSpan.FromSeconds(10)); // t1 → issued now (in flight)
        vm.ScrubPreview(TimeSpan.FromSeconds(20)); // t2 → coalesced (pending), dropped by t3
        vm.ScrubPreview(TimeSpan.FromSeconds(30)); // t3 → coalesced (pending, overwrites t2)

        player.Seeks.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(10),
            "with a seek in flight, only the first target is issued; the rest are coalesced");

        player.RaiseSeeked(TimeSpan.FromSeconds(10)); // in-flight seek settles

        player.Seeks.Should().HaveCount(2, "the coalesced follow-up issues exactly one more seek");
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(30), "it converges on the NEWEST pin (t3), not t2");
    }

    [Fact]
    public void ScrubPreview_PendingClears_NoFurtherSeekWithoutNewPreview()
    {
        var clock = new FakeClock();
        var (vm, player) = BuildReadyWithClock(clock);

        vm.ScrubPreview(TimeSpan.FromSeconds(10)); // issued
        vm.ScrubPreview(TimeSpan.FromSeconds(30)); // pending
        player.RaiseSeeked(TimeSpan.FromSeconds(10)); // → issues follow-up to 30
        player.Seeks.Should().HaveCount(2);

        // The follow-up seek settles; pending is already cleared → no third seek fires on its own.
        player.RaiseSeeked(TimeSpan.FromSeconds(30));

        player.Seeks.Should().HaveCount(2, "with pending cleared, no seek issues without a new preview");
    }

    [Fact]
    public void ScrubPreview_DeadBand_SkipsRedundantSeek()
    {
        var clock = new FakeClock();
        var (vm, player) = BuildReadyWithClock(clock);

        vm.ScrubPreview(TimeSpan.FromSeconds(10)); // issued (in flight)
        player.RaiseSeeked(TimeSpan.FromSeconds(10)); // settle → last-issued target = 10
        player.Seeks.Should().ContainSingle();

        clock.Advance(1000); // past the throttle window so only the dead-band can gate it

        // A target essentially equal to the last-issued one → skipped as redundant.
        vm.ScrubPreview(TimeSpan.FromSeconds(10).Add(TimeSpan.FromMilliseconds(2)));

        player.Seeks.Should().ContainSingle("a target within the dead-band of the last issue is redundant");
    }

    [Fact]
    public void ScrubPreview_Throttles_TooSoonBecomesPendingNotIssued()
    {
        var clock = new FakeClock();
        var (vm, player) = BuildReadyWithClock(clock);

        vm.ScrubPreview(TimeSpan.FromSeconds(10)); // issued at t=0
        player.RaiseSeeked(TimeSpan.FromSeconds(10)); // settle → not in flight
        player.Seeks.Should().ContainSingle();

        clock.Advance(20); // 20ms < 70ms throttle window
        vm.ScrubPreview(TimeSpan.FromSeconds(30)); // too soon → stashed as pending, NOT issued

        player.Seeks.Should().ContainSingle("a preview inside the throttle window is not issued immediately");

        // A later completion window drains the pending target (here via a fresh preview past the window).
        clock.Advance(100); // now past the throttle window
        vm.ScrubPreview(TimeSpan.FromSeconds(30));
        player.Seeks.Should().HaveCount(2);
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ScrubPreview_RoutesThroughHold_NoPopBack()
    {
        // T-051 must not regress T-033: a live-scrub seek arms the seek-target hold, so a stale echo
        // arriving before the seek lands must not pop the playhead off the target.
        var clock = new FakeClock();
        var (vm, player) = BuildReadyWithClock(clock);

        vm.ScrubPreview(TimeSpan.FromSeconds(40));
        vm.Position.Should().Be(TimeSpan.FromSeconds(40), "the live-scrub seek pins the display at the target");

        player.RaisePositionChanged(TimeSpan.FromSeconds(12)); // stale echo far from 40
        vm.Position.Should().Be(TimeSpan.FromSeconds(40), "the hold blocks the stale echo (T-033 preserved)");
    }

    [Fact]
    public void EndUserScrub_AfterLivePreviews_IssuesFinalExactSeek()
    {
        var clock = new FakeClock();
        var (vm, player) = BuildReadyWithClock(clock);

        vm.BeginUserScrub();
        vm.ScrubPreview(TimeSpan.FromSeconds(10)); // live seek during drag
        clock.Advance(1000);
        vm.EndUserScrub(35);                        // release at 35 → final exact seek

        vm.Position.Should().Be(TimeSpan.FromSeconds(35));
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(35), "release issues the final exact seek");
    }

    // ---- Track click to point (T-075) -------------------------------------------------------

    // With IsMoveToPointEnabled=True, a click on the slider TRACK jumps the thumb straight to the
    // click point: WPF sets Value to the clicked time (→ Position setter → BeginSeek) AND begins a
    // zero-distance thumb drag whose release (EndUserScrub) issues a second seek to the SAME point.
    // These tests model that exact event sequence in VM terms and assert a click converges on a
    // SINGLE seek to the click point — not a page-step sequence and not a double-seek/warp.

    [Fact]
    public void TrackClickToPoint_ValueChangeThenZeroDistanceDrag_SeeksOnceToClickPoint()
    {
        var (vm, player) = BuildReady(60);

        // Click at 45s: (1) move-to-point sets Value → Position setter seeks to 45, then
        // (2) DragStarted → BeginUserScrub, (3) DragCompleted at the same value → EndUserScrub(45).
        vm.PositionSeconds = 45;   // move-to-point Value change
        vm.BeginUserScrub();       // Thumb.DragStarted
        vm.EndUserScrub(45);       // Thumb.DragCompleted at the SAME point

        // Exactly ONE seek, landing at the click point — the drag-completed duplicate is deduped.
        player.Seeks.Should().ContainSingle("a click converges on a single seek, not a double-seek/warp");
        player.Seeks[0].Should().Be(TimeSpan.FromSeconds(45));
        vm.Position.Should().Be(TimeSpan.FromSeconds(45), "the click lands the playhead at the clicked time");
    }

    [Fact]
    public void TrackClickToPoint_DragStartedBeforeValueChange_StillSeeksOnceToClickPoint()
    {
        // WPF may raise Thumb.DragStarted before the Value change settles. Either ordering must still
        // yield exactly one seek to the click point (the setter's BeginSeek is not gated on scrubbing).
        var (vm, player) = BuildReady(60);

        vm.BeginUserScrub();       // Thumb.DragStarted first
        vm.PositionSeconds = 45;   // then the move-to-point Value change
        vm.EndUserScrub(45);       // Thumb.DragCompleted at the same point

        player.Seeks.Should().ContainSingle();
        player.Seeks[0].Should().Be(TimeSpan.FromSeconds(45));
        vm.Position.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void TrackClickToPoint_NoDragEvents_StillSeeksToClickPoint()
    {
        // A pure click that produces NO drag events at all (only the Value change) must still fire the
        // exact seek to the click point via the Position-setter path.
        var (vm, player) = BuildReady(60);

        vm.PositionSeconds = 45;   // move-to-point Value change only

        player.Seeks.Should().ContainSingle();
        player.Seeks[0].Should().Be(TimeSpan.FromSeconds(45));
        vm.Position.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void TrackClickToPoint_ClickWithoutValueChange_StillSeeksViaRelease()
    {
        // Defensive: if the Value change did NOT fire (e.g. click at the current position, or WPF
        // routed only the drag), the drag-completed EndUserScrub must still issue the exact seek so
        // the click is never a no-op. Here no prior seek is in flight, so it is not deduped away.
        var (vm, player) = BuildReady(60);

        vm.BeginUserScrub();
        vm.EndUserScrub(45);       // click-without-value-change → release must still seek

        player.Seeks.Should().ContainSingle();
        player.Seeks[0].Should().Be(TimeSpan.FromSeconds(45));
        vm.Position.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void TrackClickToPoint_WhileHeld_StaleEchoDoesNotWarpOffClickPoint()
    {
        // A click while playing seeks to the point; a stale playback echo arriving before the async
        // seek lands must not pop/warp the playhead off the clicked time (T-033 hold preserved).
        var (vm, player) = BuildReady(60);

        vm.PositionSeconds = 45;   // click to 45
        vm.BeginUserScrub();
        vm.EndUserScrub(45);

        player.RaisePositionChanged(TimeSpan.FromSeconds(12)); // stale echo far from 45
        vm.Position.Should().Be(TimeSpan.FromSeconds(45), "the click's seek hold blocks the stale echo — no warp");
    }

    [Fact]
    public void TrackClickToPoint_DoesNotWedgeLiveScrubState_NextClickStillSeeks()
    {
        // The deduped drag-completed seek must not leave the T-051 coalesce state wedged: after the
        // in-flight click seek settles (FFME Seeked), a subsequent click must still issue its seek.
        var (vm, player) = BuildReady(60);

        vm.PositionSeconds = 45;
        vm.BeginUserScrub();
        vm.EndUserScrub(45);
        player.Seeks.Should().ContainSingle();

        player.RaiseSeeked(TimeSpan.FromSeconds(45)); // the click's seek settles, clears in-flight

        // A second click elsewhere must produce its own seek.
        vm.PositionSeconds = 20;
        vm.BeginUserScrub();
        vm.EndUserScrub(20);

        player.Seeks.Should().HaveCount(2, "a later click still seeks — live-scrub state is not wedged");
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(20));
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

    // Large-jog magnitudes wired by the ±10m/±20m buttons (T-040). Same SkipBy clamp path,
    // asserted at the button deltas: +600s from 5s lands mid-clip; -1200s from 30s clamps to 0.
    [Fact]
    public void SkipBy_TenMinutesForward_SeeksToPositionPlusDelta_WhenDurationAllows()
    {
        var (vm, player) = BuildReady(1000);
        player.RaisePositionChanged(TimeSpan.FromSeconds(5));

        vm.SkipBy(TimeSpan.FromSeconds(600));

        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(605));
    }

    [Fact]
    public void SkipBy_TwentyMinutesBackward_ClampsToZero()
    {
        var (vm, player) = BuildReady(1000);
        player.RaisePositionChanged(TimeSpan.FromSeconds(30));

        vm.SkipBy(TimeSpan.FromSeconds(-1200));

        player.Seeks[^1].Should().Be(TimeSpan.Zero);
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

    // ---- Jog freezes the preview (T-110) ----------------------------------------------------

    // A jog (SkipBy / any skip button) is a FREEZE gesture: whatever the transport state, it must land
    // the preview PAUSED on the exact target frame — pause-first THEN seek — never a ~1s play-burst.
    // The fake records the call order, so we assert Pause precedes Seek and the player is left stopped
    // on the exact ±Ns target (the frame "Add cut at playhead" would cut at). Reuses the existing
    // FakeMediaPlayer (its Pause/Seek/IsPlaying model the freeze — no new IMediaPlayer method needed).

    [Fact]
    [Trait("serves-spec", "SPEC-013")]
    public void SkipBy_WhilePlaying_Freezes_PausesThenSeeks_LeavesPlayerStopped()
    {
        var (vm, player) = BuildReady(60);
        player.RaisePositionChanged(TimeSpan.FromSeconds(10)); // current position = 10 (not seeking)

        vm.PlayPause();                                        // start playback
        player.IsPlaying.Should().BeTrue();

        vm.SkipBy(TimeSpan.FromSeconds(5));                    // jog +5 while playing → freeze-seek to 15

        player.Calls.Should().ContainInOrder(new[] { "Pause", "Seek" }, "the jog pauses the element BEFORE seeking (freeze order)");
        player.IsPlaying.Should().BeFalse("a jog freezes the preview — the element is paused, not left playing");
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(15), "the jog lands on the exact ±Ns target, not keyframe-snapped");
        vm.Position.Should().Be(TimeSpan.FromSeconds(15), "the frozen frame equals Player.Position (what 'Add cut at playhead' cuts at)");
        vm.IsPlaying.Should().BeFalse("the VM transport flag reflects the freeze so the Play/Pause label is correct");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-013")]
    public void SkipBy_WhilePaused_Freezes_AndPlayStillPlaysAfterward()
    {
        var (vm, player) = BuildReady(60);
        player.RaisePositionChanged(TimeSpan.FromSeconds(10)); // paused at 10

        vm.SkipBy(TimeSpan.FromSeconds(5));                    // jog +5 while paused → seek to 15, stays paused

        player.IsPlaying.Should().BeFalse("already paused — the jog leaves it paused on the target, no burst");
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(15));
        vm.Position.Should().Be(TimeSpan.FromSeconds(15));

        // Required-Fail guard: a jog-freeze must NOT disable playback — Play still plays afterward.
        vm.PlayPause();
        player.Calls.Should().Contain("Play");
        player.IsPlaying.Should().BeTrue("Play still starts playback after a jog-freeze");
        vm.IsPlaying.Should().BeTrue();
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

    // ---- Volume (T-029) ---------------------------------------------------------------------

    [Fact]
    public void SettingVolume_WritesPlayerVolume()
    {
        var (vm, player) = BuildReady();

        vm.Volume = 0.3;

        vm.Volume.Should().Be(0.3);
        player.Volume.Should().Be(0.3);
    }

    [Fact]
    public void Volume_DefaultsToOne()
    {
        var (vm, _) = Build();

        vm.Volume.Should().Be(1.0);
    }

    // ---- Mute (T-029) -----------------------------------------------------------------------

    [Fact]
    public void MuteCommand_Toggles_IsMuted_And_WritesPlayer()
    {
        var (vm, player) = BuildReady();

        vm.MuteCommand.Execute(null);
        vm.IsMuted.Should().BeTrue();
        player.IsMuted.Should().BeTrue();

        vm.MuteCommand.Execute(null);
        vm.IsMuted.Should().BeFalse();
        player.IsMuted.Should().BeFalse();
    }

    [Fact]
    public void MuteUnmute_PreservesSliderVolumeLevel()
    {
        var (vm, player) = BuildReady();
        vm.Volume = 0.4;

        vm.MuteCommand.Execute(null);   // mute
        vm.Volume.Should().Be(0.4, "muting must not lose the slider value");
        player.IsMuted.Should().BeTrue();

        vm.MuteCommand.Execute(null);   // unmute
        vm.Volume.Should().Be(0.4, "unmute restores the prior slider level");
        vm.IsMuted.Should().BeFalse();
        player.IsMuted.Should().BeFalse();
    }

    // ---- Speed (T-029) ----------------------------------------------------------------------

    [Fact]
    public void SettingSpeedRatio_WritesPlayer_AndUpdatesText()
    {
        var (vm, player) = BuildReady();

        vm.SpeedRatio = 1.5;

        vm.SpeedRatio.Should().Be(1.5);
        player.SpeedRatio.Should().Be(1.5);
        vm.SpeedText.Should().Be("1.5x");
    }

    [Fact]
    public void SpeedPresets_ContainsExpectedValues()
    {
        var (vm, _) = Build();

        vm.SpeedPresets.Should().Equal(0.25, 0.5, 1.0, 1.5, 2.0);
    }

    // ---- Open resets volume / mute / speed (T-029) ------------------------------------------

    [Fact]
    public void Open_ResetsVolumeMuteSpeed_ToDefaults()
    {
        var (vm, player) = BuildReady();
        vm.Volume = 0.2;
        vm.IsMuted = true;
        vm.SpeedRatio = 2.0;

        vm.Open(@"C:\videos\next.mp4");

        vm.Volume.Should().Be(1.0);
        vm.IsMuted.Should().BeFalse();
        vm.SpeedRatio.Should().Be(1.0);
        player.Volume.Should().Be(1.0);
        player.IsMuted.Should().BeFalse();
        player.SpeedRatio.Should().Be(1.0);
    }

    // ==== SPEC-013 preview-player gaps (todo-automate) =======================================

    /// <summary>Records Clear(path) / ClearAll / GetThumbnail calls so hover-wiring can be observed (I39).</summary>
    private sealed class RecordingThumbnailService : IThumbnailService
    {
        public List<string> Cleared { get; } = new();

        public int ClearAllCount { get; private set; }

        public List<(string Path, TimeSpan Time)> Requests { get; } = new();

        public Task<string?> GetThumbnailAsync(string inputPath, TimeSpan time, int width, CancellationToken ct)
        {
            Requests.Add((inputPath, time));
            return Task.FromResult<string?>(null);
        }

        public void Clear(string inputPath) => Cleared.Add(inputPath);

        public void ClearAll() => ClearAllCount++;
    }

    // SPEC-013#I35 — Unload() resets the same preview state as Open: player.Unload called; Duration→null
    // (IsReady false); IsPlaying false; Position Zero; Volume 1.0 / IsMuted false / SpeedRatio 1.0;
    // seek hold cleared; PreviewFailed cleared. Mirrors Open_CallsPlayerOpen_ResetsState for the Unload path.
    [Fact]
    [Trait("serves-spec", "SPEC-013")]
    public void Unload_FullyResetsPreviewState()
    {
        var (vm, player) = BuildReady(60);

        // Dirty the VM: preview-failed, playing, non-default audio/speed, a mid-seek hold at a non-zero pos.
        player.RaiseFailed("boom");                       // PreviewFailed = true
        vm.PlayPause();                                    // IsPlaying = true (Failed doesn't clear Duration → still ready)
        vm.Volume = 0.3;
        vm.IsMuted = true;
        vm.SpeedRatio = 2.0;
        vm.Position = TimeSpan.FromSeconds(40);            // user seek → seek-target hold armed, position 40

        // Preconditions: genuinely dirtied.
        vm.PreviewFailed.Should().BeTrue();
        vm.IsPlaying.Should().BeTrue();
        vm.Position.Should().Be(TimeSpan.FromSeconds(40));

        vm.Unload();

        player.Calls.Should().Contain("Unload", "Unload blanks the underlying player surface");
        vm.IsReady.Should().BeFalse();
        vm.Duration.Should().BeNull();
        vm.IsPlaying.Should().BeFalse();
        vm.Position.Should().Be(TimeSpan.Zero);
        vm.Volume.Should().Be(1.0);
        vm.IsMuted.Should().BeFalse();
        vm.SpeedRatio.Should().Be(1.0);
        vm.PreviewFailed.Should().BeFalse();
        vm.PreviewFailedReason.Should().BeNull();
    }

    // SPEC-013#I39 — hover-thumbnail wiring through the full ctor: Open → Thumbnail.SetInput(path);
    // OnDurationAvailable → Thumbnail.SetDuration(duration); Unload → Thumbnail.Clear().
    [Fact]
    [Trait("serves-spec", "SPEC-013")]
    public void HoverThumbnail_Wiring_OpenSetsInput_DurationForwarded_UnloadClears()
    {
        var player = new FakeMediaPlayer();
        var thumbs = new RecordingThumbnailService();
        var vm = new PlayerViewModel(player, thumbs); // full ctor wires the hover Thumbnail over the service
        const string path = @"C:\videos\clip.mp4";

        // (a) Open forwards the path to Thumbnail.SetInput (duration not yet known).
        vm.Open(path);
        vm.Thumbnail.MouseEnter();                          // cursor over the bar
        vm.Thumbnail.IsThumbnailVisible.Should().BeFalse("SetInput set the path but no duration is known yet");

        // (b) OnDurationAvailable forwards the known duration to Thumbnail.SetDuration → popup can show,
        //     proving BOTH the input path (SetInput) and the duration (SetDuration) reached the preview.
        player.RaiseDurationAvailable(TimeSpan.FromSeconds(30));
        vm.Thumbnail.IsThumbnailVisible.Should().BeTrue("SetInput(path) + SetDuration(30s) → the hover popup is showable");

        // (c) Unload calls Thumbnail.Clear → sweeps the input's cache via the service.
        vm.Unload();
        thumbs.Cleared.Should().Contain(path, "Unload invokes Thumbnail.Clear, which sweeps the current input path");
        vm.Thumbnail.IsThumbnailVisible.Should().BeFalse("Unload hides the hover popup");
    }

    // SPEC-013#I34/I35 — Open resets the T-051 live-scrub coalesce/throttle state (ResetScrubState
    // clears _seekInFlight/_pendingScrubTarget) so the FIRST ScrubPreview on a freshly-loaded file
    // issues IMMEDIATELY, not stashed behind a phantom in-flight seek left over from the prior file.
    // Distinct from TrackClickToPoint_DoesNotWedgeLiveScrubState, which clears in-flight via Seeked
    // (the settle path); this exercises the Open/Unload RESET path instead.
    [Fact]
    [Trait("serves-spec", "SPEC-013")]
    public void Open_ResetsLiveScrubState_NextScrubPreviewIssuesImmediately_NotWedged()
    {
        var clock = new FakeClock();
        var (vm, player) = BuildReadyWithClock(clock);

        // File A: leave a live-scrub seek IN FLIGHT — issue one preview and never raise Seeked, so the
        // coalesce state stays latched (_seekInFlight = true, nothing settles it).
        vm.ScrubPreview(TimeSpan.FromSeconds(10));
        player.Seeks.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(10),
            "the live-scrub seek is issued and now latched in flight (no Seeked to clear it)");

        // Open file B and make it ready. Open MUST ResetScrubState — dropping the phantom in-flight seek.
        vm.Open(@"C:\videos\B.mp4");
        player.RaiseDurationAvailable(TimeSpan.FromSeconds(90));

        var before = player.Seeks.Count;

        // Move past the throttle window so the ONLY thing that could gate this preview is a wedged
        // in-flight seek — isolates the invariant under test from the T-051 throttle window.
        clock.Advance(1000);

        // First live preview on the freshly-loaded file.
        vm.ScrubPreview(TimeSpan.FromSeconds(20));

        // PERF (structural — call-count, no wall-clock): the first post-Open preview issues IMMEDIATELY,
        // not coalesced behind a phantom in-flight seek. Exactly one new seek, converging on the target.
        player.Seeks.Count.Should().Be(before + 1,
            "Open reset the live-scrub state, so the first preview on the new file issues immediately — not stashed behind a phantom in-flight seek");
        player.Seeks[^1].Should().Be(TimeSpan.FromSeconds(20),
            "the issued seek converges on the new target, not coalesced away or dropped");

        // CORRECTNESS: ready once the new duration arrives; the playhead is pinned at the new target.
        vm.IsReady.Should().BeTrue("the freshly-opened file is ready after its duration arrives");
        vm.Position.Should().Be(TimeSpan.FromSeconds(20), "the live-scrub seek pins the display at the new target");
    }
}
