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
/// Unit tests for the T-013 playhead-capture / seek-to-marker wiring on
/// <see cref="SplitViewModel"/>. All fakes — no ffmpeg, no WPF, no real playback. The
/// <see cref="RecordingPlayer"/> lets us set a playhead position, raise DurationAvailable to flip
/// the player to <c>IsReady</c>, and record every <c>Open</c> / <c>Seek</c> so the seek
/// destinations can be asserted deterministically.
/// </summary>
public sealed class SplitViewModelPlayheadTests
{
    private const string FakePath = @"C:\videos\clip.mp4";

    // ---- Fakes ------------------------------------------------------------------------------

    /// <summary>Probe with the real nearest-keyframe snap so snap assertions exercise real math.</summary>
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
        public Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null)
            => Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
    }

    /// <summary>Records Open/Seek, exposes a settable playhead, and raises DurationAvailable.</summary>
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

        /// <summary>Count of <see cref="Unload"/> calls (T-047).</summary>
        public int UnloadCount { get; private set; }

        public void Unload()
        {
            UnloadCount++;
            Duration = null;
            Position = TimeSpan.Zero;
        }

        public void StepFrame(int direction) { }

        /// <summary>Flip the player to a known duration (→ PlayerViewModel.IsReady == true).</summary>
        public void RaiseDurationAvailable(TimeSpan duration)
        {
            Duration = duration;
            DurationAvailable?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Move the playhead and notify — the PlayerViewModel mirrors this into its Position.</summary>
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

    // ---- Load opens the player --------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_OpensTheFileInThePlayer()
    {
        var (vm, probe, player) = Build();
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5) };

        await vm.LoadAsync(FakePath);

        player.Opened.Should().ContainSingle().Which.Should().Be(FakePath);
    }

    // ---- Set cut at playhead ----------------------------------------------------------------

    [Fact]
    public async Task SetCutAtPlayhead_AddsSnappedMarkerAtCurrentPosition()
    {
        var (vm, _, player) = await BuildLoadedReadyAsync();
        player.RaisePositionChanged(TimeSpan.FromSeconds(3.4));

        vm.SetCutAtPlayhead();

        vm.Markers.Should().ContainSingle();
        vm.Markers[0].Requested.Should().Be(TimeSpan.FromSeconds(3.4));
        vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(3), "3.4 snaps to the 3.0 keyframe via the existing snap path");
    }

    [Fact]
    public void SetCutAtPlayhead_CanExecute_False_WhenNoFile()
    {
        var (vm, _, _) = Build();

        vm.CanSetCutAtPlayhead.Should().BeFalse();
        vm.SetCutAtPlayheadCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task SetCutAtPlayhead_CanExecute_False_WhenLoadedButPlayerNotReady()
    {
        var (vm, probe, _) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);
        // No DurationAvailable raised → player not ready.

        vm.CanSetCutAtPlayhead.Should().BeFalse();
        vm.SetCutAtPlayheadCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task SetCutAtPlayhead_CanExecute_True_AfterLoadAndDurationAvailable()
    {
        var (vm, _, _) = await BuildLoadedReadyAsync();

        vm.CanSetCutAtPlayhead.Should().BeTrue();
        vm.SetCutAtPlayheadCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task SetCutAtPlayhead_SamePositionTwice_IsDeduped()
    {
        var (vm, _, player) = await BuildLoadedReadyAsync();
        player.RaisePositionChanged(TimeSpan.FromSeconds(3.4));

        vm.SetCutAtPlayhead();
        vm.SetCutAtPlayhead(); // same playhead → same snapped keyframe → no duplicate.

        vm.Markers.Should().ContainSingle("capturing the same playhead twice dedupes via the existing marker logic");
    }

    // ---- Seek to marker ---------------------------------------------------------------------

    [Fact]
    public async Task SeekToMarker_SeeksThePlayerToTheSnappedTime()
    {
        var (vm, _, player) = await BuildLoadedReadyAsync();
        player.RaisePositionChanged(TimeSpan.FromSeconds(3.4));
        vm.SetCutAtPlayhead();
        var marker = vm.Markers[0];
        player.Seeks.Clear();

        vm.SeekToMarker(marker);

        player.Seeks.Should().ContainSingle().Which.Should().Be(marker.Snapped);
        marker.Snapped.Should().Be(TimeSpan.FromSeconds(3));
    }
}
