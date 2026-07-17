using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for <see cref="SplitViewModel"/> using fake <see cref="IMediaProbe"/> /
/// <see cref="ISplitEngine"/> — no ffmpeg, no GUI, no rendering. The fakes are
/// TaskCompletionSource-free where ordering does not matter and deterministic where it does.
/// </summary>
public sealed class SplitViewModelTests
{
    private const string FakePath = @"C:\videos\clip.mp4";

    // ---- Fakes ------------------------------------------------------------------------------

    /// <summary>
    /// Fake probe. Snapping uses the real nearest-keyframe algorithm (copied minimal) so the
    /// snap/delta assertions exercise real math, not a stub value. Everything else is scripted.
    /// </summary>
    private sealed class FakeProbe : IMediaProbe
    {
        public ProbeResult ProbeResultToReturn { get; set; } = ProbeResult.Success(
            new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>()));

        public IReadOnlyList<TimeSpan> KeyframesToReturn { get; set; } = Array.Empty<TimeSpan>();

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

    private sealed class FakeSplitEngine : ISplitEngine
    {
        public SplitRequest? LastRequest { get; private set; }

        public Func<SplitRequest, Task<SplitResult>>? Handler { get; set; }

        public Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            LastRequest = req;
            progress?.Report(0.5);
            return Handler is not null
                ? Handler(req)
                : Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
        }
    }

    private static (SplitViewModel Vm, FakeProbe Probe, FakeSplitEngine Engine) Build()
    {
        var probe = new FakeProbe();
        var engine = new FakeSplitEngine();
        return (new SplitViewModel(probe, engine), probe, engine);
    }

    // ---- Load -------------------------------------------------------------------------------

    [Fact]
    public async Task Load_Success_SetsInfoAndKeyframes()
    {
        var (vm, probe, _) = Build();
        var info = new MediaInfo(TimeSpan.FromSeconds(90), "mov,mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>());
        probe.ProbeResultToReturn = ProbeResult.Success(info);
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

        await vm.LoadAsync(FakePath);

        vm.Info.Should().BeSameAs(info);
        vm.Keyframes.Should().HaveCount(3);
        vm.InputPath.Should().Be(FakePath);
        vm.HasFile.Should().BeTrue();
        vm.KeyframeWarning.Should().BeNull("2s GOP is not coarse");
        vm.Operation.Error.Should().BeNull();
    }

    [Fact]
    public async Task Load_CoarseGop_SetsKeyframeWarning()
    {
        var (vm, probe, _) = Build();
        // Keyframes 6s apart → mean GOP 6s > 4s threshold → warning.
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12) };

        await vm.LoadAsync(FakePath);

        vm.KeyframeWarning.Should().NotBeNull();
        vm.KeyframeWarning.Should().Contain("nearest keyframe");
    }

    [Fact]
    public async Task Load_ProbeFailed_SurfacesFriendlyError_NoThrow()
    {
        var (vm, probe, _) = Build();
        probe.ProbeResultToReturn = ProbeResult.Failure("not a media file");

        var act = async () => await vm.LoadAsync(FakePath);

        await act.Should().NotThrowAsync();
        vm.InputPath.Should().BeNull("a failed probe must not load the file");
        vm.Operation.State.Should().Be(OperationState.Failed);
        vm.Operation.Error.Should().NotBeNull();
        vm.Operation.Error!.RawTail.Should().Contain("not a media file");
        vm.StatusText.Should().NotBeNullOrEmpty();
    }

    // ---- Markers + snap ---------------------------------------------------------------------

    [Fact]
    public async Task AddMarker_ComputesSnapAndDeltaFromKeyframes()
    {
        var (vm, probe, _) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);

        vm.AddMarker(TimeSpan.FromSeconds(3.4));

        vm.Markers.Should().HaveCount(1);
        var marker = vm.Markers[0];
        marker.Snapped.Should().Be(TimeSpan.FromSeconds(3), "3.4 snaps to the 3.0 keyframe");
        marker.Delta.Should().Be(TimeSpan.FromSeconds(-0.4));
        marker.Display.Should().Contain("−0.4s");
    }

    [Fact]
    public async Task AddMarker_ChangingRequested_ResnapsMarker()
    {
        var (vm, probe, _) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);
        vm.AddMarker(TimeSpan.FromSeconds(3.4));

        vm.Markers[0].Requested = TimeSpan.FromSeconds(7.1);

        vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void AddMarker_WithNoFile_IsIgnored()
    {
        var (vm, _, _) = Build();

        vm.AddMarker(TimeSpan.FromSeconds(5));

        vm.Markers.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveMarker_RemovesFromCollection()
    {
        var (vm, probe, _) = Build();
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5) };
        await vm.LoadAsync(FakePath);
        vm.AddMarker(TimeSpan.FromSeconds(3));
        var marker = vm.Markers[0];

        vm.RemoveMarkerCommand.Execute(marker);

        vm.Markers.Should().BeEmpty();
    }

    // ---- CanRunSplit ------------------------------------------------------------------------

    [Fact]
    public void CanRunSplit_False_WithNoFile()
    {
        var (vm, _, _) = Build();

        vm.CanRunSplit.Should().BeFalse();
        vm.RunSplitCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CanRunSplit_False_WithFileButNoMarkers()
    {
        var (vm, probe, _) = Build();
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5) };
        await vm.LoadAsync(FakePath);

        vm.OutputDir = @"C:\out";

        vm.CanRunSplit.Should().BeFalse("no markers yet");
    }

    [Fact]
    public async Task CanRunSplit_True_WithFileAndMarkerAndOutputDir()
    {
        var (vm, probe, _) = Build();
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";

        vm.AddMarker(TimeSpan.FromSeconds(5));

        vm.CanRunSplit.Should().BeTrue();
        vm.RunSplitCommand.CanExecute(null).Should().BeTrue();
    }

    // ---- Run split --------------------------------------------------------------------------

    [Fact]
    public async Task RunSplit_BuildsExpectedRequest_SortedCutPoints_SetsLastResult()
    {
        var (vm, probe, engine) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        vm.NamingPattern = "{name}_p{index:00}{ext}";
        vm.Overwrite = true;

        // Added out of order — the request must sort them.
        vm.AddMarker(TimeSpan.FromSeconds(30));
        vm.AddMarker(TimeSpan.FromSeconds(10));
        vm.AddMarker(TimeSpan.FromSeconds(20));

        var segments = new[]
        {
            new SplitSegment(@"C:\out\clip_p01.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.Zero, TimeSpan.Zero),
        };
        engine.Handler = _ => Task.FromResult(new SplitResult(segments, Array.Empty<string>()));

        await vm.RunSplitAsync();

        engine.LastRequest.Should().NotBeNull();
        engine.LastRequest!.InputPath.Should().Be(FakePath);
        engine.LastRequest.OutputDir.Should().Be(@"C:\out");
        engine.LastRequest.NamingPattern.Should().Be("{name}_p{index:00}{ext}");
        engine.LastRequest.Overwrite.Should().BeTrue();
        engine.LastRequest.CutPoints.Should().ContainInOrder(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.LastResult.Should().NotBeNull();
        vm.LastResult!.Segments.Should().HaveCount(1);
    }

    [Fact]
    public async Task RunSplit_EngineThrows_OperationFailed_ErrorSet()
    {
        var (vm, probe, engine) = Build();
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromSeconds(5));

        engine.Handler = _ => throw new SplitException("cannot split — output dir unwritable");

        await vm.RunSplitAsync();

        vm.Operation.State.Should().Be(OperationState.Failed);
        vm.Operation.Error.Should().NotBeNull();
        vm.LastResult.Should().BeNull();
    }

    // ---- Player wiring (T-012) --------------------------------------------------------------

    /// <summary>Minimal recording fake for the preview-player seam.</summary>
    private sealed class RecordingMediaPlayer : VideoSplitJoiner.App.Media.IMediaPlayer
    {
        public List<string> Opened { get; } = new();

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration => null;

        public bool IsPlaying => false;

        public void Open(string path) => Opened.Add(path);

        public void Play() { }

        public void Pause() { }

        public void Stop() { }

        public void Seek(TimeSpan t) { }

        public void StepFrame(int direction) { }

#pragma warning disable CS0067
        public event EventHandler? PositionChanged;

        public event EventHandler? DurationAvailable;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    [Fact]
    public void Ctor_WithoutPlayer_StillConstructs_AndExposesPlayer()
    {
        // The pre-existing 3-arg construction (player omitted → NullMediaPlayer default) must work.
        var (vm, _, _) = Build();

        vm.Player.Should().NotBeNull();
    }

    [Fact]
    public async Task Load_Success_OpensThePreviewPlayer()
    {
        var probe = new FakeProbe();
        var player = new RecordingMediaPlayer();
        var vm = new SplitViewModel(probe, new FakeSplitEngine(), player);
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5) };

        await vm.LoadAsync(FakePath);

        player.Opened.Should().ContainSingle().Which.Should().Be(FakePath);
    }
}
