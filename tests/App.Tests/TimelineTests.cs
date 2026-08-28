using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for the T-014 timeline overlay: the pure <see cref="TimelineMath"/> mapping and the
/// <see cref="TimelineViewModel"/> projection + click routing. All fakes — no WPF, no ffmpeg, no
/// real playback. Reuses the same fake shapes as <see cref="SplitViewModelPlayheadTests"/> so the
/// projection exercises the real snap + AddCutAt paths.
/// </summary>
public sealed class TimelineTests
{
    private const string FakePath = @"C:\videos\clip.mp4";

    // =========================================================================================
    // TimelineMath — pure mapping
    // =========================================================================================

    [Fact]
    public void ToNormalized_MapsFractionOfDuration()
    {
        var dur = TimeSpan.FromSeconds(10);
        TimelineMath.ToNormalized(TimeSpan.Zero, dur).Should().Be(0d);
        TimelineMath.ToNormalized(TimeSpan.FromSeconds(5), dur).Should().BeApproximately(0.5, 1e-9);
        TimelineMath.ToNormalized(TimeSpan.FromSeconds(10), dur).Should().Be(1d);
    }

    [Fact]
    public void ToNormalized_ClampsBeyondRange()
    {
        var dur = TimeSpan.FromSeconds(10);
        TimelineMath.ToNormalized(TimeSpan.FromSeconds(-5), dur).Should().Be(0d);
        TimelineMath.ToNormalized(TimeSpan.FromSeconds(25), dur).Should().Be(1d);
    }

    [Fact]
    public void ToNormalized_ZeroDuration_IsZero_NoThrow()
    {
        FluentActions.Invoking(() => TimelineMath.ToNormalized(TimeSpan.FromSeconds(3), TimeSpan.Zero))
            .Should().NotThrow();
        TimelineMath.ToNormalized(TimeSpan.FromSeconds(3), TimeSpan.Zero).Should().Be(0d);
        TimelineMath.ToNormalized(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(-1)).Should().Be(0d);
    }

    [Fact]
    public void FromNormalized_MapsBackToTime_AndClamps()
    {
        var dur = TimeSpan.FromSeconds(10);
        TimelineMath.FromNormalized(0d, dur).Should().Be(TimeSpan.Zero);
        TimelineMath.FromNormalized(0.5, dur).Should().Be(TimeSpan.FromSeconds(5));
        TimelineMath.FromNormalized(1d, dur).Should().Be(TimeSpan.FromSeconds(10));
        TimelineMath.FromNormalized(-0.3, dur).Should().Be(TimeSpan.Zero);
        TimelineMath.FromNormalized(1.7, dur).Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void FromNormalized_ZeroDuration_IsZero()
    {
        TimelineMath.FromNormalized(0.5, TimeSpan.Zero).Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.831)]
    [InlineData(1.0)]
    public void ToNormalized_And_FromNormalized_AreInverseWithinRounding(double x)
    {
        var dur = TimeSpan.FromSeconds(97.3);
        var roundTrip = TimelineMath.ToNormalized(TimelineMath.FromNormalized(x, dur), dur);
        roundTrip.Should().BeApproximately(x, 1e-6);
    }

    // =========================================================================================
    // TimelineViewModel — projection
    // =========================================================================================

    [Fact]
    public async Task Projection_MarkerTicks_HaveCorrectNormalized()
    {
        var (vm, _, player) = await BuildLoadedReadyAsync(); // duration = 60s, keyframes at every second
        var timeline = vm.Timeline;

        // Marker: snapping 30.4 lands on the 30s keyframe → normalized 0.5.
        player.RaisePositionChanged(TimeSpan.FromSeconds(30.4));
        vm.SetCutAtPlayhead();

        timeline.MarkerTicks.Should().ContainSingle();
        timeline.MarkerTicks[0].Normalized.Should().BeApproximately(0.5, 1e-9);
        timeline.MarkerTicks[0].Time.Should().Be(TimeSpan.FromSeconds(30));
        timeline.MarkerTicks[0].Ref.Should().BeSameAs(vm.Markers[0]);
    }

    [Fact]
    public async Task Projection_Reprojects_WhenMarkerAdded()
    {
        var (vm, _, player) = await BuildLoadedReadyAsync();
        vm.Timeline.MarkerTicks.Should().BeEmpty();

        player.RaisePositionChanged(TimeSpan.FromSeconds(12));
        vm.SetCutAtPlayhead();

        vm.Timeline.MarkerTicks.Should().ContainSingle("adding a marker re-projects via CollectionChanged");
    }

    [Fact]
    public async Task PlayheadNormalized_TracksPlayerPosition()
    {
        var (vm, _, player) = await BuildLoadedReadyAsync(); // 60s

        player.RaisePositionChanged(TimeSpan.FromSeconds(45));

        vm.Timeline.PlayheadNormalized.Should().BeApproximately(0.75, 1e-9);
    }

    // =========================================================================================
    // TimelineViewModel — click-to-cut (reuses AddCutAt) + guards
    // =========================================================================================

    [Fact]
    public async Task ClickAt_Half_Duration10s_DropsSnappedMarkerAt5s_ViaAddCutAt()
    {
        // duration 10s, keyframes every second → 0.5 → 5.0 → snaps to 5s keyframe.
        var (vm, probe, player) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);
        player.RaiseDurationAvailable(TimeSpan.FromSeconds(10));

        vm.Timeline.ClickAt(0.5);

        vm.Markers.Should().ContainSingle("ClickAt routes through the existing AddCutAt snap path");
        vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(5));
        vm.Timeline.MarkerTicks.Should().ContainSingle();
        vm.Timeline.MarkerTicks[0].Normalized.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void ClickAt_BeforeLoad_IsNoOp()
    {
        var (vm, _, _) = Build();

        vm.Timeline.ClickAt(0.5);

        vm.Markers.Should().BeEmpty("no file loaded → no meaningful time to cut at");
    }

    [Fact]
    public async Task ClickAt_DurationUnknown_IsNoOp()
    {
        // File loaded but DurationAvailable never raised → Player.Duration is null.
        var (vm, probe, _) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);

        vm.Timeline.ClickAt(0.5);

        vm.Markers.Should().BeEmpty("duration unknown → no-op, no crash");
    }

    [Fact]
    public async Task ClickAt_Zero_And_One_Boundaries()
    {
        var (vm, probe, player) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);
        player.RaiseDurationAvailable(TimeSpan.FromSeconds(10));

        vm.Timeline.ClickAt(0d);
        vm.Timeline.ClickAt(1d);

        vm.Markers.Select(m => m.Snapped).Should().BeEquivalentTo(new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
        });
    }

    [Fact]
    public void DurationUnknown_NoTicks_NoCrash()
    {
        var (vm, _, _) = Build();

        // Before any file/duration — projection must not throw and the playhead clamps to 0.
        FluentActions.Invoking(() => vm.Timeline.PlayheadNormalized.Should().Be(0d))
            .Should().NotThrow();

        vm.Timeline.MarkerTicks.Should().BeEmpty();
        vm.Timeline.PlayheadNormalized.Should().Be(0d);
    }

    // =========================================================================================
    // TimelineViewModel — tick routing to existing seek/preview commands
    // =========================================================================================

    [Fact]
    public async Task SeekMarkerTick_SeeksToSnappedTime_ViaExistingCommand()
    {
        var (vm, _, player) = await BuildLoadedReadyAsync();
        player.RaisePositionChanged(TimeSpan.FromSeconds(30.4));
        vm.SetCutAtPlayhead();
        var tick = vm.Timeline.MarkerTicks[0];
        player.Seeks.Clear();

        vm.Timeline.SeekMarkerTick(tick);

        player.Seeks.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(30),
            "marker tick routes to SeekToMarkerCommand → the snapped time");
    }

    // =========================================================================================
    // SPEC-014 timeline gap (todo-automate) — ctor null-owner guard
    // =========================================================================================

    // SPEC-014#I13 — TimelineViewModel(null owner) throws ArgumentNullException with paramName "owner".
    // No existing test constructs the VM with a null owner (every builder passes a real SplitViewModel).
    [Fact]
    [Trait("serves-spec", "SPEC-014")]
    public void Ctor_NullOwner_ThrowsArgumentNullException()
    {
        ((Action)(() => new TimelineViewModel(null!)))
            .Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("owner");
    }

    // =========================================================================================
    // SPEC-014 timeline gaps (todo-automate) — re-projection triggers + tick-routing guard
    // =========================================================================================

    // SPEC-014#I12 — a player Position, Duration OR IsReady change re-projects the strip (playhead +
    // ticks recomputed; OnPlayerChanged filters on exactly those three names). The Position leg is
    // covered above; this pins the Duration/IsReady leg — a position AND a marker that exist BEFORE
    // the duration is known are both re-scaled the moment it arrives.
    [Fact]
    [Trait("serves-spec", "SPEC-014")]
    public async Task Projection_Reprojects_WhenDurationBecomesKnown()
    {
        var (vm, probe, player) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);

        player.RaisePositionChanged(TimeSpan.FromSeconds(5));
        vm.AddCutAt(TimeSpan.FromSeconds(4));

        vm.Timeline.PlayheadNormalized.Should().Be(0d, "duration is still unknown — there is nothing to scale against");
        vm.Timeline.MarkerTicks.Should().ContainSingle();
        vm.Timeline.MarkerTicks[0].Normalized.Should().Be(0d, "the tick collapses to 0 while the duration is unknown");

        // DurationAvailable moves PlayerViewModel.Duration (and with it IsReady) — either one re-projects.
        player.RaiseDurationAvailable(TimeSpan.FromSeconds(10));

        vm.Timeline.PlayheadNormalized.Should().BeApproximately(0.5, 1e-9,
            "the already-known playhead is re-projected against the newly-known duration");
        vm.Timeline.MarkerTicks[0].Normalized.Should().BeApproximately(0.4, 1e-9,
            "the ticks are recomputed too — the whole strip re-projects, not just the playhead");
        vm.Timeline.MarkerTicks[0].Time.Should().Be(TimeSpan.FromSeconds(4));
    }

    // SPEC-014#I18 — SeekMarkerTick routes to the owner's SeekToMarkerCommand ONLY when the tick's Ref
    // is a CutMarkerViewModel and that command can execute; anything else — a foreign Ref, a null tick
    // — is a silent no-op: never a throw, never a stray seek.
    [Fact]
    [Trait("serves-spec", "SPEC-014")]
    public async Task SeekMarkerTick_NonMarkerRef_OrNull_IsNoOp()
    {
        var (vm, _, player) = await BuildLoadedReadyAsync();
        player.RaisePositionChanged(TimeSpan.FromSeconds(30.4));
        vm.SetCutAtPlayhead();
        player.Seeks.Clear();

        // A hand-built tick whose Ref is NOT a marker (Ref is typed object — this is the defensive branch).
        FluentActions
            .Invoking(() => vm.Timeline.SeekMarkerTick(new TimelineTick(0.5, TimeSpan.FromSeconds(30), Ref: "not-a-marker")))
            .Should().NotThrow();
        player.Seeks.Should().BeEmpty("a non-marker Ref never reaches the seek command");

        FluentActions.Invoking(() => vm.Timeline.SeekMarkerTick(null!))
            .Should().NotThrow("a null tick is a no-op, not a crash");
        player.Seeks.Should().BeEmpty();

        // Sanity: a REAL marker tick does route — so the guard above is a genuine filter, not a dead path.
        vm.Timeline.SeekMarkerTick(vm.Timeline.MarkerTicks[0]);
        player.Seeks.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(30));
    }

    // =========================================================================================
    // Fakes + builders (mirror SplitViewModelPlayheadTests)
    // =========================================================================================

    private sealed class FakeProbe : IMediaProbe
    {
        public IReadOnlyList<TimeSpan> KeyframesToReturn { get; set; } = Array.Empty<TimeSpan>();

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResult.Success(
                new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult(KeyframesToReturn);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
        {
            var best = keyframes[0];
            var bestDist = Abs(best - requested);
            for (var i = 1; i < keyframes.Count; i++)
            {
                var dist = Abs(keyframes[i] - requested);
                if (dist < bestDist || (dist == bestDist && keyframes[i] < best))
                {
                    best = keyframes[i];
                    bestDist = dist;
                }
            }

            return new KeyframeSnap(best, best - requested);
        }

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes)
        {
            if (keyframes.Count < 2)
            {
                return TimeSpan.Zero;
            }

            var ordered = keyframes.OrderBy(k => k).ToList();
            return TimeSpan.FromTicks((ordered[^1].Ticks - ordered[0].Ticks) / (ordered.Count - 1));
        }

        private static TimeSpan Abs(TimeSpan t) => t < TimeSpan.Zero ? t.Negate() : t;
    }

    private sealed class NoOpSplitEngine : ISplitEngine
    {
        public Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null, IProgress<VideoSplitJoiner.Core.Split.PartProgress>? partProgress = null)
            => Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
    }

    private sealed class RecordingPlayer : IMediaPlayer
    {
        public List<string> Opened { get; } = new();

        public List<TimeSpan> Seeks { get; } = new();

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying => false;

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        public void Open(string path) => Opened.Add(path);

        public void Play() { }

        public void Pause() { }

        public void Stop() { }

        public void Seek(TimeSpan t)
        {
            Seeks.Add(t);
            Position = t;
        }

        public void Unload()
        {
            Duration = null;
            Position = TimeSpan.Zero;
        }

        public void StepFrame(int direction) { }

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

        public event EventHandler? PositionChanged;

        public event EventHandler? DurationAvailable;

#pragma warning disable CS0067
        public event EventHandler? Seeked;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    private static (SplitViewModel Vm, FakeProbe Probe, RecordingPlayer Player) Build()
    {
        var probe = new FakeProbe();
        var player = new RecordingPlayer();
        var vm = new SplitViewModel(probe, new NoOpSplitEngine(), player);
        return (vm, probe, player);
    }

    private static async Task<(SplitViewModel Vm, FakeProbe Probe, RecordingPlayer Player)> BuildLoadedReadyAsync()
    {
        var (vm, probe, player) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);
        player.RaiseDurationAvailable(TimeSpan.FromSeconds(60));
        return (vm, probe, player);
    }
}
