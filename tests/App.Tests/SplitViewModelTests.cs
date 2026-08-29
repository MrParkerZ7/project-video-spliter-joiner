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

        public Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null, IProgress<VideoSplitJoiner.Core.Split.PartProgress>? partProgress = null)
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

    // ---- Info card / badge (T-059) ----------------------------------------------------------

    [Fact]
    public async Task Load_PopulatesFileNameMetaLineAndBadge()
    {
        var (vm, probe, _) = Build();
        var info = new MediaInfo(
            TimeSpan.FromMinutes(10),
            "matroska",
            new[] { new StreamInfo(0, "hevc", "video", 3840, 2160, "yuv420p", null, null, "1/30") },
            Array.Empty<StreamInfo>());
        probe.ProbeResultToReturn = ProbeResult.Success(info);

        await vm.LoadAsync(FakePath);

        vm.FileName.Should().Be("clip.mp4");
        // The fake path isn't a real file → size unknown → meta line is "container · duration" only.
        vm.MetaLine.Should().Be("matroska · 10:00");
        vm.Badge.Should().Be("HEVC · MATROSKA");
    }

    [Fact]
    public void NoFile_InfoCardValuesAreNull()
    {
        var (vm, _, _) = Build();
        vm.FileName.Should().BeNull();
        vm.MetaLine.Should().BeNull();
        vm.Badge.Should().BeNull();
    }

    [Fact]
    public async Task Clear_ResetsInfoCardValues()
    {
        var (vm, probe, _) = Build();
        probe.ProbeResultToReturn = ProbeResult.Success(
            new MediaInfo(TimeSpan.FromMinutes(5), "matroska",
                new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
                Array.Empty<StreamInfo>()));
        await vm.LoadAsync(FakePath);
        vm.Badge.Should().NotBeNull();

        vm.Clear();

        vm.FileName.Should().BeNull();
        vm.MetaLine.Should().BeNull();
        vm.Badge.Should().BeNull();
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

    // ---- Marker list is time-ordered (T-071) ------------------------------------------------

    [Fact]
    public async Task AddMarkers_OutOfOrder_MarkerListEnumeratesAscendingByTime()
    {
        var (vm, probe, _) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);

        // Add out of order: 5:00 then 2:00 then 8:00 then 3:30 (all on 1s keyframes → snap to self).
        vm.AddMarker(TimeSpan.FromSeconds(50));
        vm.AddMarker(TimeSpan.FromSeconds(20));
        vm.AddMarker(TimeSpan.FromSeconds(8));
        vm.AddMarker(TimeSpan.FromSeconds(35));

        vm.Markers.Select(m => m.Snapped).Should().ContainInOrder(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(35),
            TimeSpan.FromSeconds(50));
        vm.Markers.Select(m => m.Snapped).Should().BeInAscendingOrder("the list is time-ordered, not add-ordered");
    }

    [Fact]
    public async Task AddMarkers_FiveThenTwo_TwoSortsAboveFive()
    {
        // The headline acceptance case: add a cut at 5:00 then 2:00 → list shows 2:00 above 5:00.
        var (vm, probe, _) = Build();
        // 1s keyframe grid out to 10 minutes so 2:00 / 5:00 snap to themselves.
        probe.KeyframesToReturn = Enumerable.Range(0, 601).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);

        vm.AddMarker(TimeSpan.FromMinutes(5));
        vm.AddMarker(TimeSpan.FromMinutes(2));

        vm.Markers[0].Snapped.Should().Be(TimeSpan.FromMinutes(2), "2:00 sorts to the top");
        vm.Markers[1].Snapped.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task RemoveMiddleMarker_RemainingListStaysOrdered()
    {
        var (vm, probe, _) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);

        vm.AddMarker(TimeSpan.FromSeconds(40));
        vm.AddMarker(TimeSpan.FromSeconds(10));
        vm.AddMarker(TimeSpan.FromSeconds(25));

        // Remove the middle-by-time marker (25s).
        var middle = vm.Markers.Single(m => m.Snapped == TimeSpan.FromSeconds(25));
        vm.RemoveMarkerCommand.Execute(middle);

        vm.Markers.Select(m => m.Snapped).Should().ContainInOrder(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40));
        vm.Markers.Select(m => m.Snapped).Should().BeInAscendingOrder();
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

    [Fact]
    public async Task RunSplit_FfmpegFailure_ErrorExposesFullCopyText_AndLogPath()
    {
        var (vm, probe, engine) = Build();
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromSeconds(5));

        var fullStdErr = "line A\nline B\nConversion failed! the real cause";
        var logPath = @"C:\logs\split-20260717-120000.log";
        // The engine leads its message with the mapped friendly headline, then the full stderr.
        engine.Handler = _ => throw new SplitException(
            "Not enough space to write the output (ffmpeg exit -22).\n" + fullStdErr,
            logPath,
            fullStdErr);

        await vm.RunSplitAsync();

        vm.Operation.State.Should().Be(OperationState.Failed);
        var err = vm.Operation.Error;
        err.Should().NotBeNull();

        // Headline is the FIRST line only — not the whole multi-line stderr blob.
        err!.Message.Should().Be("Not enough space to write the output (ffmpeg exit -22).");
        err.Message.Should().NotContain("Conversion failed!");

        // The full text + log path are threaded through and copyable.
        err.FullText.Should().Be(fullStdErr);
        err.LogFilePath.Should().Be(logPath);
        err.HasLogFile.Should().BeTrue();
        err.DetailText.Should().Contain("Conversion failed! the real cause");
        err.CopyText.Should().Contain("Not enough space to write the output")
            .And.Contain("Conversion failed! the real cause")
            .And.Contain(logPath);
    }

    // ---- Selectable segments (T-049) --------------------------------------------------------

    /// <summary>Build a VM loaded with a 15m clip and a 1s-GOP keyframe grid (0,1,…,900s).</summary>
    private static async Task<(SplitViewModel Vm, FakeProbe Probe)> BuildLoaded15mAsync()
    {
        var probe = new FakeProbe
        {
            ProbeResultToReturn = ProbeResult.Success(new MediaInfo(
                TimeSpan.FromMinutes(15), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())),
            // Keyframes every second so 5m / 10m snap exactly to themselves.
            KeyframesToReturn = Enumerable.Range(0, 901).Select(i => TimeSpan.FromSeconds(i)).ToArray(),
        };
        var vm = new SplitViewModel(probe, new FakeSplitEngine());
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        return (vm, probe);
    }

    [Fact]
    public async Task Segments_MarkersAt5And10On15mClip_ProjectsThreeParts_AllSelected()
    {
        var (vm, _) = await BuildLoaded15mAsync();

        vm.AddMarker(TimeSpan.FromMinutes(5));
        vm.AddMarker(TimeSpan.FromMinutes(10));

        vm.Segments.Should().HaveCount(3);

        vm.Segments[0].Index.Should().Be(1);
        vm.Segments[0].Start.Should().Be(TimeSpan.Zero);
        vm.Segments[0].End.Should().Be(TimeSpan.FromMinutes(5));
        vm.Segments[0].Duration.Should().Be(TimeSpan.FromMinutes(5));

        vm.Segments[1].Index.Should().Be(2);
        vm.Segments[1].Start.Should().Be(TimeSpan.FromMinutes(5));
        vm.Segments[1].End.Should().Be(TimeSpan.FromMinutes(10));

        vm.Segments[2].Index.Should().Be(3);
        vm.Segments[2].Start.Should().Be(TimeSpan.FromMinutes(10));
        vm.Segments[2].End.Should().Be(TimeSpan.FromMinutes(15));

        vm.Segments.Should().OnlyContain(s => s.IsSelected, "parts default to selected");
        vm.SelectedSegmentCount.Should().Be(3);
        vm.SegmentCount.Should().Be(3);

        // Display formatting: "Part 2 · 05:00–10:00 · 5:00".
        vm.Segments[1].Display.Should().Be("Part 2 · 05:00–10:00 · 5:00");
    }

    [Fact]
    public async Task Segments_Deselect_UpdatesSelectedCountAndRunLabel()
    {
        var (vm, _) = await BuildLoaded15mAsync();
        vm.AddMarker(TimeSpan.FromMinutes(5));
        vm.AddMarker(TimeSpan.FromMinutes(10));

        vm.RunLabel.Should().Be("Split 3 parts");

        vm.Segments[1].IsSelected = false;

        vm.SelectedSegmentCount.Should().Be(2);
        vm.RunLabel.Should().Be("Split 2 of 3 parts");
        vm.CanRunSplit.Should().BeTrue("two parts still selected");
    }

    [Fact]
    public async Task Segments_ZeroSelected_DisablesRun()
    {
        var (vm, _) = await BuildLoaded15mAsync();
        vm.AddMarker(TimeSpan.FromMinutes(5));
        vm.AddMarker(TimeSpan.FromMinutes(10));

        vm.SelectNoSegmentsCommand.Execute(null);

        vm.SelectedSegmentCount.Should().Be(0);
        vm.CanRunSplit.Should().BeFalse("zero parts selected → Run disabled");
        vm.RunSplitCommand.CanExecute(null).Should().BeFalse();

        // Re-select all restores Run.
        vm.SelectAllSegmentsCommand.Execute(null);
        vm.SelectedSegmentCount.Should().Be(3);
        vm.CanRunSplit.Should().BeTrue();
    }

    [Fact]
    public async Task Segments_DeselectPreservedAcrossRebuild_WhenAnotherMarkerAdded()
    {
        var (vm, _) = await BuildLoaded15mAsync();
        vm.AddMarker(TimeSpan.FromMinutes(5));
        vm.AddMarker(TimeSpan.FromMinutes(10));

        // Uncheck part 1, then add another marker (rebuild) — part 1 must stay unchecked.
        vm.Segments[0].IsSelected = false;
        vm.AddMarker(TimeSpan.FromMinutes(12));

        vm.Segments.Should().HaveCount(4);
        vm.Segments[0].IsSelected.Should().BeFalse("prior selection is preserved by index across rebuild");
    }

    [Fact]
    public async Task RunSplit_AllSelected_PassesNullSelection_MuxerPath()
    {
        var probe = new FakeProbe
        {
            ProbeResultToReturn = ProbeResult.Success(new MediaInfo(
                TimeSpan.FromMinutes(15), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())),
            KeyframesToReturn = Enumerable.Range(0, 901).Select(i => TimeSpan.FromSeconds(i)).ToArray(),
        };
        var engine = new FakeSplitEngine();
        var vm = new SplitViewModel(probe, engine);
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromMinutes(5));
        vm.AddMarker(TimeSpan.FromMinutes(10));

        await vm.RunSplitAsync();

        engine.LastRequest.Should().NotBeNull();
        engine.LastRequest!.SelectedSegmentIndices.Should().BeNull(
            "all parts selected → null selection keeps the fast muxer path");
    }

    [Fact]
    public async Task RunSplit_Subset_PassesSelectedIndices_KeepingOriginalIndex()
    {
        var probe = new FakeProbe
        {
            ProbeResultToReturn = ProbeResult.Success(new MediaInfo(
                TimeSpan.FromMinutes(15), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())),
            KeyframesToReturn = Enumerable.Range(0, 901).Select(i => TimeSpan.FromSeconds(i)).ToArray(),
        };
        var engine = new FakeSplitEngine();
        var vm = new SplitViewModel(probe, engine);
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromMinutes(5));
        vm.AddMarker(TimeSpan.FromMinutes(10));

        // Keep only the MIDDLE part (index 2).
        vm.Segments[0].IsSelected = false;
        vm.Segments[2].IsSelected = false;

        await vm.RunSplitAsync();

        engine.LastRequest.Should().NotBeNull();
        engine.LastRequest!.SelectedSegmentIndices.Should().NotBeNull();
        engine.LastRequest.SelectedSegmentIndices.Should().ContainSingle().Which.Should().Be(
            2, "the ORIGINAL 1-based index of the selected middle part is preserved");
    }

    // ---- Player wiring (T-012) --------------------------------------------------------------

    /// <summary>Minimal recording fake for the preview-player seam.</summary>
    private sealed class RecordingMediaPlayer : VideoSplitJoiner.App.Media.IMediaPlayer
    {
        public List<string> Opened { get; } = new();

        /// <summary>Count of <see cref="Unload"/> calls — asserted by the Split Clear test (T-047).</summary>
        public int UnloadCount { get; private set; }

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration => null;

        public bool IsPlaying => false;

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        public void Open(string path) => Opened.Add(path);

        public void Play() { }

        public void Pause() { }

        public void Stop() { }

        public void Seek(TimeSpan t) { }

        public void Unload() => UnloadCount++;

        public void StepFrame(int direction) { }

#pragma warning disable CS0067
        public event EventHandler? PositionChanged;

        public event EventHandler? Seeked;

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

    // ---- Clear / reset (T-047) --------------------------------------------------------------

    [Fact]
    public async Task Clear_ResetsToEmpty_UnloadsPlayer_AndDisablesRun()
    {
        var probe = new FakeProbe();
        var player = new RecordingMediaPlayer();
        var vm = new SplitViewModel(probe, new FakeSplitEngine(), player);
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromSeconds(2));
        vm.AddMarker(TimeSpan.FromSeconds(4));

        // Pre-conditions: loaded + markers + can run.
        vm.HasFile.Should().BeTrue();
        vm.Markers.Should().HaveCountGreaterThan(0);
        vm.CanRunSplit.Should().BeTrue();

        vm.ClearCommand.Execute(null);

        vm.HasFile.Should().BeFalse();
        vm.InputPath.Should().BeNull();
        vm.Info.Should().BeNull();
        vm.Markers.Should().BeEmpty();
        vm.Keyframes.Should().BeEmpty();
        vm.KeyframeWarning.Should().BeNull();
        vm.LastResult.Should().BeNull();
        vm.CanRunSplit.Should().BeFalse();
        vm.IsIndexingKeyframes.Should().BeFalse();
        player.UnloadCount.Should().BeGreaterThan(0, "the preview player is unloaded on clear");
    }

    [Fact]
    public void Clear_CanExecute_False_WhenNoFile()
    {
        var (vm, _, _) = Build();

        vm.CanClear.Should().BeFalse();
        vm.ClearCommand.CanExecute(null).Should().BeFalse();
    }

    // ---- Add-at-playhead is the primary add gesture (T-064 regression) ----------------------

    /// <summary>
    /// A player fake whose playhead can be MOVED between adds (raising PositionChanged so the VM's
    /// PlayerViewModel tracks it) and that reports a duration so <see cref="SplitViewModel.CanSetCutAtPlayhead"/>
    /// is enabled. Models the real "scrub, then Add" loop the primary gesture depends on.
    /// </summary>
    private sealed class MovablePlayer : VideoSplitJoiner.App.Media.IMediaPlayer
    {
        private TimeSpan _position;

        public TimeSpan Position
        {
            get => _position;
            set
            {
                _position = value;
                PositionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying => false;

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        /// <summary>Report a known duration → drives the VM's IsReady true (enables set-cut-at-playhead).</summary>
        public void SignalReady(TimeSpan duration)
        {
            Duration = duration;
            DurationAvailable?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Move the playhead to <paramref name="t"/> and raise PositionChanged, as playback/seek would.</summary>
        public void MoveTo(TimeSpan t) => Position = t;

        public void Open(string path) { }

        public void Play() { }

        public void Pause() { }

        public void Stop() { }

        public void Seek(TimeSpan t) => Position = t;

        public void Unload() { }

        public void StepFrame(int direction) { }

        public event EventHandler? PositionChanged;

        public event EventHandler? DurationAvailable;

#pragma warning disable CS0067
        public event EventHandler? Seeked;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    private static async Task<(SplitViewModel Vm, MovablePlayer Player)> BuildWithMovablePlayerAsync()
    {
        var probe = new FakeProbe
        {
            ProbeResultToReturn = ProbeResult.Success(new MediaInfo(
                TimeSpan.FromMinutes(15), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())),
            // 1s-GOP grid so distinct playhead positions snap to distinct keyframes.
            KeyframesToReturn = Enumerable.Range(0, 901).Select(i => TimeSpan.FromSeconds(i)).ToArray(),
        };
        var player = new MovablePlayer();
        var vm = new SplitViewModel(probe, new FakeSplitEngine(), player);
        await vm.LoadAsync(FakePath);
        player.SignalReady(TimeSpan.FromMinutes(15));
        return (vm, player);
    }

    [Fact]
    public async Task SetCutAtPlayhead_AtThreeDistinctPositions_AddsThreeMarkers()
    {
        // The reported "add cut only once" bug (post-T-059): the primary Add must add at the LIVE,
        // moving playhead so repeated adds at distinct spots each land — not re-submit one static time.
        var (vm, player) = await BuildWithMovablePlayerAsync();

        player.MoveTo(TimeSpan.FromMinutes(2));
        vm.SetCutAtPlayheadCommand.Execute(null);

        player.MoveTo(TimeSpan.FromMinutes(5));
        vm.SetCutAtPlayheadCommand.Execute(null);

        player.MoveTo(TimeSpan.FromMinutes(9));
        vm.SetCutAtPlayheadCommand.Execute(null);

        vm.Markers.Should().HaveCount(3, "three adds at three distinct playhead positions → three markers");
        vm.Markers.Select(m => m.Snapped).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task SetCutAtPlayhead_SamePositionTwice_DedupsToOne()
    {
        // The dedup on the same snapped keyframe MUST still hold (two cuts can't share one keyframe).
        var (vm, player) = await BuildWithMovablePlayerAsync();

        player.MoveTo(TimeSpan.FromMinutes(4));
        vm.SetCutAtPlayheadCommand.Execute(null);
        vm.SetCutAtPlayheadCommand.Execute(null); // same playhead → deduped

        vm.Markers.Should().HaveCount(1, "two adds at the SAME playhead keyframe collapse to one cut");
    }

    [Fact]
    public async Task NewMarkerPosition_FollowsPlayhead_UntilUserTypesAnExactTime()
    {
        // T-064: the typed-position field advances with the playhead by default (so the field-based add
        // isn't stuck on one value), but a user's exact-time entry is not stomped by the next tick.
        var (vm, player) = await BuildWithMovablePlayerAsync();

        player.MoveTo(TimeSpan.FromSeconds(30));
        vm.NewMarkerPosition.Should().Be(TimeSpan.FromSeconds(30), "the field follows the live playhead");

        player.MoveTo(TimeSpan.FromSeconds(45));
        vm.NewMarkerPosition.Should().Be(TimeSpan.FromSeconds(45), "still following");

        // User types an exact time → follow turns off.
        vm.NewMarkerPosition = TimeSpan.FromSeconds(120);
        player.MoveTo(TimeSpan.FromSeconds(60));
        vm.NewMarkerPosition.Should().Be(TimeSpan.FromSeconds(120),
            "a manual exact-time entry is not overwritten by a later playhead tick");
    }

    [Fact]
    public async Task NewMarkerPosition_ReFollowsPlayhead_AfterReload()
    {
        // Re-arming on load: after the user pins the field then reloads, the fresh file follows again.
        var (vm, player) = await BuildWithMovablePlayerAsync();
        vm.NewMarkerPosition = TimeSpan.FromSeconds(200); // pin it (turns follow off)

        await vm.LoadAsync(FakePath); // reload re-arms follow
        player.SignalReady(TimeSpan.FromMinutes(15));
        player.MoveTo(TimeSpan.FromSeconds(15));

        vm.NewMarkerPosition.Should().Be(TimeSpan.FromSeconds(15), "a reload re-arms playhead-follow");
    }

    [Fact]
    public async Task Clear_CanExecute_True_WhenFileLoaded()
    {
        var (vm, probe, _) = Build();
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2) };

        await vm.LoadAsync(FakePath);

        vm.CanClear.Should().BeTrue();
        vm.ClearCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Clear_CanExecute_False_WhileOperationRunning()
    {
        var probe = new FakeProbe();
        var engine = new FakeSplitEngine();
        var vm = new SplitViewModel(probe, engine);
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromSeconds(2));

        // Gate the split so it stays "running" while we assert CanClear.
        var gate = new TaskCompletionSource<SplitResult>();
        engine.Handler = _ => gate.Task;

        var run = vm.RunSplitAsync();
        vm.Operation.IsRunning.Should().BeTrue();
        vm.CanClear.Should().BeFalse("a running split must not be cleared mid-op");
        vm.ClearCommand.CanExecute(null).Should().BeFalse();

        // Let the run finish → clear becomes available again.
        gate.SetResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
        await run;
        vm.CanClear.Should().BeTrue();
    }

    // ==== SPEC-010 split-screen gaps (todo-automate) =========================================

    // SPEC-010#I1 — LoadAsync(null-or-whitespace) is a no-op: returns immediately, mutates nothing.
    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public async Task LoadAsync_NullOrBlankPath_IsNoOp_MutatesNothing()
    {
        var (vm, _, engine) = Build();
        var stateBefore = vm.Operation.State;

        var act = async () =>
        {
            await vm.LoadAsync(null);
            await vm.LoadAsync("   ");
        };

        await act.Should().NotThrowAsync();
        vm.InputPath.Should().BeNull("a null/blank path never loads a file");
        vm.HasFile.Should().BeFalse();
        vm.Info.Should().BeNull();
        vm.Operation.State.Should().Be(stateBefore, "the shared operation is untouched by a no-op load");
        engine.LastRequest.Should().BeNull();
    }

    // SPEC-010#I22 — no file loaded, or duration <= 0, yields an empty Segments projection.
    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public async Task Segments_EmptyOnFreshVm_AfterClear_AndForZeroDurationFile()
    {
        var (vm, probe, _) = Build();

        // Fresh (unloaded) VM → no parts projected.
        vm.Segments.Should().BeEmpty("a fresh VM has no segment projection");

        // A file whose probed duration is Zero → still no parts, even with a marker placed.
        probe.ProbeResultToReturn = ProbeResult.Success(
            new MediaInfo(TimeSpan.Zero, "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>()));
        probe.KeyframesToReturn = new[] { TimeSpan.Zero };
        await vm.LoadAsync(FakePath);
        vm.HasFile.Should().BeTrue();
        vm.AddMarker(TimeSpan.FromSeconds(1));
        vm.Segments.Should().BeEmpty("a zero-duration file projects no parts");

        // A normally-loaded file with markers projects parts; Clear then wipes them back to empty.
        var (vm2, probe2, _) = Build();
        probe2.KeyframesToReturn = Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm2.LoadAsync(FakePath);
        vm2.AddMarker(TimeSpan.FromSeconds(20));
        vm2.AddMarker(TimeSpan.FromSeconds(40));
        vm2.Segments.Should().NotBeEmpty("precondition: markers on a real-duration file project parts");

        vm2.Clear();
        vm2.Segments.Should().BeEmpty("Clear unloads the file → empty segment projection");
    }

    // SPEC-010#I27 — RunSplitAsync is a no-op unless CanRunSplit (guarded early-return).
    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public async Task RunSplitAsync_NoOp_WhenCanRunSplitFalse()
    {
        var (vm, probe, engine) = Build();
        probe.KeyframesToReturn = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        // No markers → CanRunSplit false.
        vm.CanRunSplit.Should().BeFalse("no markers → the run is gated off");

        var stateBefore = vm.Operation.State;

        await vm.RunSplitAsync();

        engine.LastRequest.Should().BeNull("the guarded early-return never builds a request / calls the engine");
        vm.Operation.State.Should().Be(stateBefore, "Operation state is untouched by the no-op run");
    }

    // SPEC-010#I30 — a blank NamingPattern is replaced with SplitRequest.DefaultNamingPattern.
    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public async Task RunSplit_BlankNamingPattern_SubstitutesDefault()
    {
        var (vm, probe, engine) = Build();
        probe.KeyframesToReturn = Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray();
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromSeconds(20));
        vm.NamingPattern = "   "; // blank → must fall back to the default at request-build time

        await vm.RunSplitAsync();

        engine.LastRequest.Should().NotBeNull();
        engine.LastRequest!.NamingPattern.Should().Be(SplitRequest.DefaultNamingPattern,
            "a blank naming pattern is substituted with SplitRequest.DefaultNamingPattern");
    }

    // SPEC-010#I26 — CanRunSplit is true iff InputPath is set AND there is >=1 marker AND OutputDir is
    // set AND >=1 segment is selected. The file / marker / selection clauses are pinned by the tests
    // above; this pins the OutputDir clause — with everything else satisfied, a blank destination
    // alone disables the run, and restoring it re-enables it.
    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public async Task CanRunSplit_False_WhenOutputDirBlank_EvenWithFileMarkerAndSelection()
    {
        var (vm, _) = await BuildLoaded15mAsync();
        vm.AddMarker(TimeSpan.FromMinutes(5));

        vm.CanRunSplit.Should().BeTrue("precondition: file + marker + selection + an anchored OutputDir");

        vm.OutputDir = "   ";
        vm.CanRunSplit.Should().BeFalse("a whitespace-only destination fails the OutputDir clause alone");
        vm.RunSplitCommand.CanExecute(null).Should().BeFalse("the command guard tracks CanRunSplit");

        vm.OutputDir = string.Empty;
        vm.CanRunSplit.Should().BeFalse("an empty destination fails the same clause");

        // The other three clauses were never disturbed — restoring the destination re-enables the run.
        vm.InputPath.Should().NotBeNull();
        vm.Markers.Should().ContainSingle();
        vm.SelectedSegmentCount.Should().BeGreaterThan(0);

        vm.OutputDir = @"C:\out";
        vm.CanRunSplit.Should().BeTrue("restoring the destination satisfies the last clause again");
        vm.RunSplitCommand.CanExecute(null).Should().BeTrue();
    }

    // SPEC-010#I25 — SelectAllSegmentsCommand / SelectNoSegmentsCommand set every part's IsSelected
    // true/false AND are enabled ONLY when Segments.Count > 0. The set-behaviour half is pinned by
    // Segments_ZeroSelected_DisablesRun; this pins the ENABLEMENT half on both commands — disabled
    // with no projection, enabled once parts exist, disabled again once the projection is dropped.
    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public async Task SelectAllAndNoneCommands_Disabled_WhenNoPartsProjected_EnabledOnceProjected()
    {
        var (vm, _, _) = Build();

        vm.Segments.Should().BeEmpty("a fresh VM has no segment projection");
        vm.SelectAllSegmentsCommand.CanExecute(null).Should().BeFalse("no parts to select");
        vm.SelectNoSegmentsCommand.CanExecute(null).Should().BeFalse("no parts to deselect");

        var (loaded, _) = await BuildLoaded15mAsync();
        loaded.AddMarker(TimeSpan.FromMinutes(5));
        loaded.Segments.Should().HaveCount(2, "precondition: one cut projects two parts");

        loaded.SelectAllSegmentsCommand.CanExecute(null).Should().BeTrue("parts exist → select-all is enabled");
        loaded.SelectNoSegmentsCommand.CanExecute(null).Should().BeTrue("parts exist → select-none is enabled");

        // Both commands act on EVERY row while enabled.
        loaded.SelectNoSegmentsCommand.Execute(null);
        loaded.Segments.Should().OnlyContain(s => !s.IsSelected, "select-none clears every part");
        loaded.SelectAllSegmentsCommand.Execute(null);
        loaded.Segments.Should().OnlyContain(s => s.IsSelected, "select-all sets every part");

        // Losing the projection disables both again.
        loaded.Clear();
        loaded.Segments.Should().BeEmpty("Clear drops the projection");
        loaded.SelectAllSegmentsCommand.CanExecute(null).Should().BeFalse("no parts left to select");
        loaded.SelectNoSegmentsCommand.CanExecute(null).Should().BeFalse("no parts left to deselect");
    }

    // SPEC-010#I24 — RunLabel is "Split N parts" when all are selected, "Split M of N parts" for a
    // subset, and a bare "Split" when there are no parts at all. The all-selected plural and the
    // subset branches are pinned above; this pins the ZERO-part branch and the SINGULAR ("1 part")
    // branch — an uncut loaded file projects exactly one whole-file part.
    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public async Task RunLabel_IsBareSplit_WithNoParts_AndSingular_ForOnePart()
    {
        var (vm, _, _) = Build();

        vm.SegmentCount.Should().Be(0);
        vm.RunLabel.Should().Be("Split", "no projection yet → a bare label");

        var (loaded, _) = await BuildLoaded15mAsync();
        loaded.Markers.Should().BeEmpty("precondition: no cuts placed yet");
        loaded.SegmentCount.Should().Be(1, "an uncut file projects exactly one part — the whole clip");
        loaded.SelectedSegmentCount.Should().Be(1, "parts default to selected");
        loaded.RunLabel.Should().Be("Split 1 part", "the singular branch reads 'part', not 'parts'");

        // Deselecting the only part crosses into the subset branch (still "N parts" plural there).
        loaded.Segments[0].IsSelected = false;
        loaded.RunLabel.Should().Be("Split 0 of 1 parts", "a partial selection uses the 'M of N' branch");

        // Dropping the projection returns to the bare label.
        loaded.Clear();
        loaded.SegmentCount.Should().Be(0);
        loaded.RunLabel.Should().Be("Split", "clearing the file removes the projection → the bare label again");
    }

    // SPEC-010#I21 — Segments projects from the distinct snapped marker times STRICTLY INSIDE
    // (0, duration), plus the file duration. The general projection is pinned by
    // Segments_MarkersAt5And10On15mClip_ProjectsThreeParts_AllSelected (interior markers only); this
    // pins the BOUNDARY FILTER — a marker snapped to exactly 0, or to exactly the duration, is not an
    // interior cut and must never manufacture a zero-length part.
    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public async Task Segments_MarkersAtExactlyZeroAndDuration_AreNotBoundaries_OneWholePart()
    {
        var (vm, _) = await BuildLoaded15mAsync();

        // The 1s keyframe grid runs 0..900s, so both requests snap to themselves — exactly the two
        // excluded boundary values.
        vm.AddMarker(TimeSpan.Zero);
        vm.AddMarker(TimeSpan.FromMinutes(15));

        vm.Markers.Should().HaveCount(2, "both markers exist in the cut list");
        vm.Markers.Select(m => m.Snapped).Should().ContainInOrder(TimeSpan.Zero, TimeSpan.FromMinutes(15));

        vm.Segments.Should().ContainSingle(
            "boundaries at exactly 0 and exactly the duration are not interior cuts — no zero-length part");
        vm.SegmentCount.Should().Be(1);
        vm.Segments[0].Index.Should().Be(1);
        vm.Segments[0].Start.Should().Be(TimeSpan.Zero);
        vm.Segments[0].End.Should().Be(TimeSpan.FromMinutes(15));
        vm.Segments[0].Duration.Should().Be(TimeSpan.FromMinutes(15), "the single part spans the whole clip");

        // Only the boundary times are filtered — a genuine interior cut alongside them still projects.
        vm.AddMarker(TimeSpan.FromMinutes(5));

        vm.Markers.Should().HaveCount(3);
        vm.Segments.Should().HaveCount(2, "the interior cut is the only real boundary of the three markers");
        vm.Segments[0].Start.Should().Be(TimeSpan.Zero);
        vm.Segments[0].End.Should().Be(TimeSpan.FromMinutes(5));
        vm.Segments[1].Start.Should().Be(TimeSpan.FromMinutes(5));
        vm.Segments[1].End.Should().Be(TimeSpan.FromMinutes(15));
    }

    // SPEC-010#I36 — NewMarkerPosition follows the live playhead until the user types a differing
    // value (which pins it), and RE-ARMS following after a load OR a Clear.
    // NewMarkerPosition_FollowsPlayhead_UntilUserTypesAnExactTime pins the follow + the pin, and
    // NewMarkerPosition_ReFollowsPlayhead_AfterReload pins the re-arm-after-LOAD half; this pins the
    // re-arm-after-CLEAR half — Clear resets the field to 00:00 AND re-arms follow, exactly as a load does.
    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public async Task NewMarkerPosition_ReFollowsPlayhead_AfterClear()
    {
        var (vm, player) = await BuildWithMovablePlayerAsync();

        vm.NewMarkerPosition = TimeSpan.FromSeconds(200); // user types an exact time → follow turns off
        player.MoveTo(TimeSpan.FromSeconds(60));
        vm.NewMarkerPosition.Should().Be(TimeSpan.FromSeconds(200),
            "precondition: the field is pinned by the user's typed value");

        vm.ClearCommand.Execute(null);

        vm.NewMarkerPosition.Should().Be(TimeSpan.Zero, "Clear resets the field back to the start");

        player.MoveTo(TimeSpan.FromSeconds(15));

        vm.NewMarkerPosition.Should().Be(TimeSpan.FromSeconds(15),
            "Clear re-armed playhead-follow, exactly as a load does");
    }
}
