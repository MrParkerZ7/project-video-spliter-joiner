using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-080 regression at the VM seam: the reported crash is split → Clear → load a NEW video → the app
/// self-closes. This models that whole loop in <see cref="SplitViewModel"/> terms over a recording
/// <see cref="IMediaPlayer"/> fake (records Open/Unload order) so the reload path is exercised without
/// FFME/GUI. Complements <see cref="MediaReopenGuardTests"/> (which covers the FFME Close→Open
/// sequencing directly) and the T-030/T-047 stale-scan tests (already green) — here we assert the
/// full-cycle ordering and that repeated split→clear→load cycles stay stable.
/// </summary>
public sealed class SplitViewModelReloadAfterClearTests
{
    private const string PathA = @"C:\videos\a.mp4";
    private const string PathB = @"C:\videos\b.mp4";

    // ---- Fakes ------------------------------------------------------------------------------

    /// <summary>Probe that completes synchronously with scriptable info + keyframes (real snap math).</summary>
    private sealed class FakeProbe : IMediaProbe
    {
        public ProbeResult ProbeResultToReturn { get; set; } = ProbeResult.Success(
            new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>()));

        public IReadOnlyList<TimeSpan> KeyframesToReturn { get; set; } =
            Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray();

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResultToReturn);

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
        public Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<OperationStatus>? status = null, IProgress<PartProgress>? partProgress = null)
            => Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
    }

    /// <summary>Records the ORDER of Open/Unload calls so the clear-then-reopen sequence is assertable.</summary>
    private sealed class RecordingMediaPlayer : IMediaPlayer
    {
        public List<string> Calls { get; } = new();

        public List<string> Opened { get; } = new();

        public int UnloadCount { get; private set; }

        public int OpenCount { get; private set; }

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration => null;

        public bool IsPlaying => false;

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        public void Open(string path)
        {
            Calls.Add("Open");
            Opened.Add(path);
            OpenCount++;
        }

        public void Play() { }

        public void Pause() { }

        public void Stop() { }

        public void Seek(TimeSpan t) { }

        public void Unload()
        {
            Calls.Add("Unload");
            UnloadCount++;
        }

        public void StepFrame(int direction) { }

#pragma warning disable CS0067
        public event EventHandler? PositionChanged;

        public event EventHandler? Seeked;

        public event EventHandler? DurationAvailable;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    private static (SplitViewModel Vm, FakeProbe Probe, RecordingMediaPlayer Player) Build()
    {
        var probe = new FakeProbe();
        var player = new RecordingMediaPlayer();
        var vm = new SplitViewModel(probe, new NoOpSplitEngine(), player);
        return (vm, probe, player);
    }

    // ---- Clear-then-load re-opens (the crash path) ------------------------------------------

    [Fact]
    public async Task Load_Clear_LoadNew_ReopensSafely_UnloadPrecedesReopen()
    {
        // The exact reported sequence in VM terms: load → Clear → load a NEW file. The player must be
        // Unloaded on Clear and then re-Opened on the second load — and the Unload must come BEFORE the
        // second Open (Close→Open ordering), never crash.
        var (vm, _, player) = Build();

        await vm.LoadAsync(PathA);
        vm.HasFile.Should().BeTrue();

        vm.ClearCommand.Execute(null);
        vm.HasFile.Should().BeFalse();

        var act = async () => await vm.LoadAsync(PathB);
        await act.Should().NotThrowAsync("re-opening a new video after Clear must not crash");

        vm.HasFile.Should().BeTrue();
        vm.InputPath.Should().Be(PathB);
        player.Opened.Should().Equal(new[] { PathA, PathB }, "both files opened, second is the new one");
        player.UnloadCount.Should().BeGreaterThan(0, "Clear unloaded the player");

        // Ordering: the Unload from Clear precedes the second Open.
        var firstUnload = player.Calls.IndexOf("Unload");
        var secondOpen = player.Calls.LastIndexOf("Open");
        firstUnload.Should().BeLessThan(secondOpen, "Close→Open: the clear's Unload precedes the reopen");
    }

    // ---- The FULL reported loop: split → clear → load, repeated ×3 ---------------------------

    [Fact]
    public async Task SplitThenClearThenLoad_ThreeCycles_StayStable()
    {
        var (vm, _, player) = Build();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var path = cycle % 2 == 0 ? PathA : PathB;

            await vm.LoadAsync(path);
            vm.HasFile.Should().BeTrue($"cycle {cycle}: loaded");

            // Run a split to completion (no-op engine → Completed) so we exercise the post-op state.
            vm.OutputDir = @"C:\out";
            vm.AddMarker(TimeSpan.FromSeconds(10));
            vm.CanRunSplit.Should().BeTrue($"cycle {cycle}: ready to split");
            await vm.RunSplitAsync();
            vm.Operation.State.Should().Be(OperationState.Completed, $"cycle {cycle}: split completed");

            // Clear.
            vm.ClearCommand.Execute(null);
            vm.HasFile.Should().BeFalse($"cycle {cycle}: cleared");
            vm.Markers.Should().BeEmpty($"cycle {cycle}: markers dropped on clear");
        }

        // Three loads + three unloads, all opens recorded, no exception → stable across cycles.
        player.OpenCount.Should().Be(3, "one open per cycle");
        player.UnloadCount.Should().Be(3, "one unload per clear");
    }

    // ---- Load-new WITHOUT clear (drag over an existing file) stays stable --------------------

    [Fact]
    public async Task LoadNew_WithoutClear_OverExistingFile_ReopensSafely()
    {
        // The boundary case from the acceptance matrix: dragging a new file over a loaded one (no
        // Clear) also re-opens; the second Open must land and not crash.
        var (vm, _, player) = Build();

        await vm.LoadAsync(PathA);
        var act = async () => await vm.LoadAsync(PathB);
        await act.Should().NotThrowAsync();

        vm.InputPath.Should().Be(PathB);
        player.Opened.Should().Equal(new[] { PathA, PathB }, "the drag-over reload opens the new file");
    }
}
