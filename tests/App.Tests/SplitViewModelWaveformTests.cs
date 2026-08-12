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
using VideoSplitJoiner.Core.Waveform;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-084 / D-002 — the background audio-waveform wiring in <see cref="SplitViewModel"/>, exercised
/// through the WPF-free <see cref="WaveformViewModel"/> over a GATED fake <see cref="IWaveformService"/>
/// (a per-path <see cref="TaskCompletionSource{T}"/>) so the ordering is deterministic: a load kicks off
/// extraction that stays in flight until the test releases it, then we assert the band flips to
/// shown/hidden. Mirrors the <see cref="SplitViewModelNonBlockingLoadTests"/> harness (single-threaded
/// <see cref="Pump"/>) because the VM commits the extraction result via
/// <c>FromCurrentSynchronizationContext</c> — under the xUnit default (no context) that continuation
/// would race on the thread pool; the pump makes it deterministic, mirroring the WPF dispatcher.
///
/// <para>Covers: a non-null result sets Peaks + HasAudio (band shown); a null result leaves HasAudio
/// false (band hidden); IsLoading holds while extracting and clears on the result; a new load cancels
/// the prior extraction and a stale late result is dropped; and Clear hides/resets the band and sweeps
/// the file's cache.</para>
/// </summary>
public sealed class SplitViewModelWaveformTests
{
    private const string PathA = @"C:\videos\a.mp4";
    private const string PathB = @"C:\videos\b.mp4";

    // ---- Fakes ------------------------------------------------------------------------------

    /// <summary>
    /// Waveform service whose <see cref="GetPeaksAsync"/> is gated by a per-path
    /// <see cref="TaskCompletionSource{T}"/> so a test can hold extraction open, assert the loading
    /// state, then release it with a peak array (band shown) or <c>null</c> (band hidden). Records the
    /// bucket counts requested and every <see cref="Clear"/> call so the stale-guard + cache-sweep
    /// behaviour is observable. Honours cancellation so a superseded request surfaces as cancelled.
    /// </summary>
    private sealed class GatedWaveformService : IWaveformService
    {
        private readonly Dictionary<string, TaskCompletionSource<float[]?>> _gates = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = new();

        public List<int> RequestedBuckets { get; } = new();

        public List<string> Cleared { get; } = new();

        public int ClearAllCount { get; private set; }

        public Task<float[]?> GetPeaksAsync(string inputPath, int buckets, CancellationToken ct)
        {
            Requests.Add(inputPath);
            RequestedBuckets.Add(buckets);
            var tcs = Gate(inputPath);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        public void Clear(string inputPath) => Cleared.Add(inputPath);

        public void ClearAll() => ClearAllCount++;

        /// <summary>Release the gated extraction for <paramref name="path"/> with a result (peaks or null).</summary>
        public void Release(string path, float[]? peaks) => Gate(path).TrySetResult(peaks);

        private TaskCompletionSource<float[]?> Gate(string path)
        {
            if (!_gates.TryGetValue(path, out var tcs))
            {
                tcs = new TaskCompletionSource<float[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates[path] = tcs;
            }

            return tcs;
        }
    }

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
            => new(requested, TimeSpan.Zero);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.FromSeconds(1);
    }

    private sealed class NoOpSplitEngine : ISplitEngine
    {
        public Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null, IProgress<VideoSplitJoiner.Core.Split.PartProgress>? partProgress = null)
            => Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
    }

    private sealed class RecordingPlayer : IMediaPlayer
    {
        public TimeSpan Position { get; set; }

        public TimeSpan? Duration => null;

        public bool IsPlaying => false;

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        public void Open(string path) { }

        public void Play() { }

        public void Pause() { }

        public void Stop() { }

        public void Seek(TimeSpan t) { }

        public void Unload() { }

        public void StepFrame(int direction) { }

#pragma warning disable CS0067
        public event EventHandler? PositionChanged;

        public event EventHandler? Seeked;

        public event EventHandler? DurationAvailable;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    /// <summary>
    /// A minimal single-threaded synchronization-context pump — same shape as
    /// <see cref="SplitViewModelNonBlockingLoadTests"/>. The VM posts its background-extraction
    /// completion back onto the captured context (WPF dispatcher in the app); this stands in for it so
    /// the continuation runs deterministically on the test thread. <see cref="RunUntil"/> drains posted
    /// callbacks until the predicate holds (or a timeout).
    /// </summary>
    private sealed class Pump : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback cb, object? state)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void RunUntil(Func<bool> done, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
            while (!done())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Pump.RunUntil timed out waiting for the condition.");
                }

                if (_queue.TryTake(out var item, TimeSpan.FromMilliseconds(50)))
                {
                    item.cb(item.state);
                }
            }
        }
    }

    private static (SplitViewModel Vm, FakeProbe Probe, GatedWaveformService Waves) Build()
    {
        var probe = new FakeProbe();
        var waves = new GatedWaveformService();
        var vm = new SplitViewModel(probe, new NoOpSplitEngine(), new RecordingPlayer(), settings: null, thumbnails: null, waveforms: waves);
        return (vm, probe, waves);
    }

    private static void WithPump(Action<Pump> body)
    {
        var prior = SynchronizationContext.Current;
        var pump = new Pump();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            body(pump);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
    }

    // ---- Background load populates Peaks / HasAudio ------------------------------------------

    [Fact]
    public void Load_KicksOffExtraction_AndShowsLoadingState_BeforeResult()
    {
        WithPump(pump =>
        {
            var (vm, _, waves) = Build();

            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);

            // Extraction was kicked off for the loaded file (never blocks the load).
            waves.Requests.Should().ContainSingle().Which.Should().Be(PathA);
            waves.RequestedBuckets.Should().ContainSingle().Which.Should().BeGreaterThan(0);

            // In-flight extraction → loading state, band not yet shown, no stale peaks.
            vm.Waveform.IsLoading.Should().BeTrue();
            vm.Waveform.HasAudio.Should().BeFalse();
            vm.Waveform.Peaks.Should().BeEmpty();
        });
    }

    [Fact]
    public void Load_WhenExtractionCompletes_WithPeaks_SetsPeaksAndHasAudio()
    {
        WithPump(pump =>
        {
            var (vm, _, waves) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            vm.Waveform.IsLoading.Should().BeTrue();

            var peaks = new[] { 0.1f, 0.5f, 1f, 0.25f };
            waves.Release(PathA, peaks);
            pump.RunUntil(() => !vm.Waveform.IsLoading);

            vm.Waveform.HasAudio.Should().BeTrue("a non-null peak array → the band is drawn");
            vm.Waveform.Peaks.Should().Equal(peaks);
            vm.Waveform.IsLoading.Should().BeFalse("the result cleared the loading flag");
        });
    }

    [Fact]
    public void Load_WhenExtractionReturnsNull_HasAudioStaysFalse_BandHidden()
    {
        WithPump(pump =>
        {
            var (vm, _, waves) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);

            // No audio track / best-effort failure → null.
            waves.Release(PathA, null);
            pump.RunUntil(() => !vm.Waveform.IsLoading);

            vm.Waveform.HasAudio.Should().BeFalse("null result → the band stays hidden");
            vm.Waveform.Peaks.Should().BeEmpty();
            vm.Waveform.IsLoading.Should().BeFalse();
        });
    }

    // ---- New load cancels the prior extraction; stale result dropped ------------------------

    [Fact]
    public void LoadB_WhileAExtracting_A_LateResult_DoesNotOverwriteB()
    {
        WithPump(pump =>
        {
            var (vm, _, waves) = Build();

            // Load A — its extraction is gated (in flight).
            var loadA = vm.LoadAsync(PathA);
            pump.RunUntil(() => loadA.IsCompleted);
            vm.Waveform.IsLoading.Should().BeTrue();

            // Load B before A's extraction finishes → A's CTS is cancelled, B's starts fresh.
            var loadB = vm.LoadAsync(PathB);
            pump.RunUntil(() => loadB.IsCompleted);
            vm.InputPath.Should().Be(PathB);
            waves.Requests.Should().Equal(PathA, PathB);

            // Release B first with its own peaks → the band shows B's wave.
            var bPeaks = new[] { 0.2f, 0.4f, 0.6f };
            waves.Release(PathB, bPeaks);
            pump.RunUntil(() => !vm.Waveform.IsLoading);
            vm.Waveform.Peaks.Should().Equal(bPeaks);
            vm.Waveform.HasAudio.Should().BeTrue();

            // NOW A completes LATE with different peaks — its stale result must NOT overwrite B's.
            waves.Release(PathA, new[] { 0.99f, 0.99f, 0.99f, 0.99f });
            pump.RunUntil(() => true, TimeSpan.FromMilliseconds(200));

            vm.Waveform.Peaks.Should().Equal(bPeaks, "A's extraction was superseded by B's load");
            vm.Waveform.HasAudio.Should().BeTrue();
        });
    }

    [Fact]
    public void LoadOverLoad_SweepsPreviousFilesWaveformCache()
    {
        WithPump(pump =>
        {
            var (vm, _, waves) = Build();

            var loadA = vm.LoadAsync(PathA);
            pump.RunUntil(() => loadA.IsCompleted);
            waves.Release(PathA, new[] { 0.3f, 0.6f });
            pump.RunUntil(() => !vm.Waveform.IsLoading);

            // A different file loaded WITHOUT an explicit Clear must sweep the outgoing file's temp PCM.
            var loadB = vm.LoadAsync(PathB);
            pump.RunUntil(() => loadB.IsCompleted);

            waves.Cleared.Should().Contain(PathA, "the outgoing file's waveform cache is swept on load-over-load");
        });
    }

    // ---- Clear hides + resets the band, cancels in-flight extraction ------------------------

    [Fact]
    public void Clear_WhileExtracting_HidesBand_CancelsExtraction_LateResultDropped()
    {
        WithPump(pump =>
        {
            var (vm, _, waves) = Build();

            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            vm.Waveform.IsLoading.Should().BeTrue();

            // Clear while extraction is in flight → band reset + hidden, cache swept.
            vm.ClearCommand.Execute(null);

            vm.Waveform.IsLoading.Should().BeFalse("Clear resets the band");
            vm.Waveform.HasAudio.Should().BeFalse();
            vm.Waveform.Peaks.Should().BeEmpty();
            waves.Cleared.Should().Contain(PathA, "Clear sweeps the current file's waveform cache");

            // The gated extraction completes LATE — its stale result must NOT re-show the band.
            waves.Release(PathA, new[] { 0.5f, 0.5f });
            pump.RunUntil(() => true, TimeSpan.FromMilliseconds(200));

            vm.Waveform.HasAudio.Should().BeFalse("an extraction superseded by Clear can never re-show the band");
            vm.Waveform.Peaks.Should().BeEmpty();
        });
    }

    [Fact]
    public void Clear_AfterPeaksShown_HidesBand()
    {
        WithPump(pump =>
        {
            var (vm, _, waves) = Build();

            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            waves.Release(PathA, new[] { 0.4f, 0.8f, 0.4f });
            pump.RunUntil(() => !vm.Waveform.IsLoading);
            vm.Waveform.HasAudio.Should().BeTrue();

            vm.ClearCommand.Execute(null);

            vm.Waveform.HasAudio.Should().BeFalse("Clear hides the band");
            vm.Waveform.Peaks.Should().BeEmpty();
        });
    }
}
