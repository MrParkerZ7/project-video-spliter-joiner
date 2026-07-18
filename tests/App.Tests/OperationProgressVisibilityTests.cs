using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-042 end-to-end (VM-level, no ffmpeg / no GUI) coverage that a Split/Join run always shows
/// visible feedback: the shared <see cref="OperationViewModel"/> shows a running status
/// ("Splitting…"/"Joining…") immediately and an indeterminate busy bar until a real progress
/// fraction arrives — the cure for the "-c copy split looks stuck" report. Fakes here are
/// gate-controllable so the mid-run indeterminate state can be observed deterministically.
/// </summary>
public sealed class OperationProgressVisibilityTests
{
    private const string FakePath = @"C:\videos\clip.mp4";

    // ---- Fakes ------------------------------------------------------------------------------

    private sealed class FakeProbe : IMediaProbe
    {
        public IReadOnlyList<TimeSpan> KeyframesToReturn { get; set; } =
            new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

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
                if (dist < bestDist)
                {
                    best = keyframes[i];
                    bestDist = dist;
                }
            }

            return new KeyframeSnap(best, best - requested);
        }

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.FromSeconds(2);

        private static TimeSpan Abs(TimeSpan t) => t < TimeSpan.Zero ? t.Negate() : t;
    }

    /// <summary>Split engine whose progress reporting and completion are both gate-controlled.</summary>
    private sealed class GatedSplitEngine : ISplitEngine
    {
        private readonly bool _reportsProgress;
        private readonly TaskCompletionSource _release;

        public GatedSplitEngine(bool reportsProgress, TaskCompletionSource release)
        {
            _reportsProgress = reportsProgress;
            _release = release;
        }

        public async Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null, IProgress<VideoSplitJoiner.Core.Split.PartProgress>? partProgress = null)
        {
            if (_reportsProgress)
            {
                progress?.Report(0.5);
            }

            await _release.Task.ConfigureAwait(true);
            return new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>());
        }
    }

    private sealed class GatedJoinEngine : IJoinEngine
    {
        private readonly bool _reportsProgress;
        private readonly TaskCompletionSource _release;

        public GatedJoinEngine(bool reportsProgress, TaskCompletionSource release)
        {
            _reportsProgress = reportsProgress;
            _release = release;
        }

        public Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => Task.FromResult(CompatReport.Ok());

        public async Task<JoinResult> JoinAsync(JoinRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null)
        {
            if (_reportsProgress)
            {
                progress?.Report(0.5);
            }

            await _release.Task.ConfigureAwait(true);
            return JoinResult.Ok(req.OutputPath);
        }
    }

    private sealed class JoinProbe : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResult.Success(new MediaInfo(
                TimeSpan.FromSeconds(10), "mp4",
                new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
                Array.Empty<StreamInfo>())));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimeSpan>>(Array.Empty<TimeSpan>());

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
            => new(requested, TimeSpan.Zero);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    // ---- Split ------------------------------------------------------------------------------

    private static async Task<SplitViewModel> BuildLoadedSplitVmAsync(ISplitEngine engine)
    {
        var vm = new SplitViewModel(new FakeProbe(), engine);
        await vm.LoadAsync(FakePath);
        vm.AddMarker(TimeSpan.FromSeconds(2));
        vm.OutputDir = @"C:\out";
        return vm;
    }

    [Fact]
    public async Task Split_WithProgressReported_ProgressMoves_AndStatusIsSplitting()
    {
        var release = new TaskCompletionSource();
        var vm = await BuildLoadedSplitVmAsync(new GatedSplitEngine(reportsProgress: true, release));

        double progressMidRun = -1;
        string statusMidRun = string.Empty;
        bool indeterminateMidRun = true;

        var run = vm.RunSplitAsync();

        // Let the engine report 0.5 and the Progress<T> post drain.
        await Task.Delay(30);
        progressMidRun = vm.Operation.Progress;
        statusMidRun = vm.Operation.StatusText;
        indeterminateMidRun = vm.Operation.IsIndeterminate;

        release.SetResult();
        await run;

        progressMidRun.Should().Be(0.5, "the engine's reported fraction reached Operation.Progress");
        statusMidRun.Should().Be("Splitting…", "the running status is shown from the start of the run");
        indeterminateMidRun.Should().BeFalse("a real fraction flips the bar to determinate");
        vm.Operation.State.Should().Be(OperationState.Completed);
    }

    [Fact]
    public async Task Split_WithNoProgressReported_StaysIndeterminate_WithStatus_UntilDone()
    {
        var release = new TaskCompletionSource();
        var vm = await BuildLoadedSplitVmAsync(new GatedSplitEngine(reportsProgress: false, release));

        var run = vm.RunSplitAsync();

        await Task.Yield();
        vm.Operation.IsRunning.Should().BeTrue();
        vm.Operation.IsIndeterminate.Should().BeTrue("no fraction arrived → busy/indeterminate bar, never a stuck 0");
        vm.Operation.StatusText.Should().Be("Splitting…", "status shows immediately even without granular progress");

        release.SetResult();
        await run;

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.Operation.IsIndeterminate.Should().BeFalse("done → determinate (and full)");
        vm.Operation.Progress.Should().Be(1d);
    }

    // ---- Join -------------------------------------------------------------------------------

    private static async Task<JoinViewModel> BuildReadyJoinVmAsync(IJoinEngine engine)
    {
        var vm = new JoinViewModel(engine, new JoinProbe());
        await vm.AddFilesAsync(new[] { @"C:\videos\a.mp4", @"C:\videos\b.mp4" });
        vm.OutputPath = @"C:\videos\joined.mp4";
        return vm;
    }

    [Fact]
    public async Task Join_WithProgressReported_ProgressMoves_AndStatusIsJoining()
    {
        var release = new TaskCompletionSource();
        var vm = await BuildReadyJoinVmAsync(new GatedJoinEngine(reportsProgress: true, release));
        vm.CanRunJoin.Should().BeTrue();

        var run = vm.RunJoinAsync();

        await Task.Delay(30);
        var progressMidRun = vm.Operation.Progress;
        var statusMidRun = vm.Operation.StatusText;
        var indeterminateMidRun = vm.Operation.IsIndeterminate;

        release.SetResult();
        await run;

        progressMidRun.Should().Be(0.5);
        statusMidRun.Should().Be("Joining…");
        indeterminateMidRun.Should().BeFalse();
        vm.Operation.State.Should().Be(OperationState.Completed);
    }

    [Fact]
    public async Task Join_WithNoProgressReported_StaysIndeterminate_WithStatus_UntilDone()
    {
        var release = new TaskCompletionSource();
        var vm = await BuildReadyJoinVmAsync(new GatedJoinEngine(reportsProgress: false, release));

        var run = vm.RunJoinAsync();

        await Task.Yield();
        vm.Operation.IsRunning.Should().BeTrue();
        vm.Operation.IsIndeterminate.Should().BeTrue();
        vm.Operation.StatusText.Should().Be("Joining…");

        release.SetResult();
        await run;

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.Operation.IsIndeterminate.Should().BeFalse();
        vm.Operation.Progress.Should().Be(1d);
    }
}
