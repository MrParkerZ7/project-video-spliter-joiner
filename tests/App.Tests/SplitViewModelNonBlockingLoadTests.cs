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
/// T-030 — non-blocking load. The preview + info must appear as soon as the probe succeeds; the
/// keyframe scan runs in the background. These tests drive a keyframe-gated fake probe (a
/// TaskCompletionSource per file) so the ordering is deterministic: we can assert the load
/// completes and opens the player BEFORE the keyframe task is released, then release it and assert
/// the keyframes commit. A per-load CTS guarantees a stale scan can't overwrite a newer file's
/// keyframes, and a cut placed mid-index awaits the in-flight scan so it still snaps.
///
/// The fakes run on a single-threaded <see cref="SynchronizationContext"/> pump (see
/// <see cref="Pump"/>) because the VM commits background-scan results via
/// <c>FromCurrentSynchronizationContext</c> — under the xUnit default (no context) the continuation
/// would run on the thread pool and races. The pump makes it deterministic, mirroring the WPF
/// dispatcher.
/// </summary>
public sealed class SplitViewModelNonBlockingLoadTests
{
    private const string PathA = @"C:\videos\a.mp4";
    private const string PathB = @"C:\videos\b.mp4";

    // ---- Fakes ------------------------------------------------------------------------------

    /// <summary>
    /// Probe whose <see cref="GetKeyframesAsync"/> is gated by a per-path TaskCompletionSource so a
    /// test can hold the keyframe scan open, assert non-blocking behavior, then release it. Probe
    /// itself completes synchronously. Snap uses the real nearest-keyframe math.
    /// </summary>
    private sealed class GatedProbe : IMediaProbe
    {
        private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<TimeSpan>>> _gates = new();

        public ProbeResult ProbeResultToReturn { get; set; } = ProbeResult.Success(
            new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>()));

        public List<string> KeyframeRequests { get; } = new();

        /// <summary>
        /// Paths whose in-flight scan token actually FIRED (recorded from the ct.Register callback
        /// below). Lets a test prove a superseded load ACTIVELY cancels the prior scan (I5) rather
        /// than merely ignoring its late result. Test-only probe extension — no production surface.
        /// </summary>
        public HashSet<string> CancelledScans { get; } = new();

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResultToReturn);

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
        {
            KeyframeRequests.Add(path);
            var tcs = Gate(path);
            // Honour cancellation so a superseded scan surfaces as cancelled (stale-guard test).
            ct.Register(() =>
            {
                CancelledScans.Add(path);
                tcs.TrySetCanceled(ct);
            });
            return tcs.Task;
        }

        /// <summary>Release the gated keyframe scan for <paramref name="path"/> with a result.</summary>
        public void Release(string path, IReadOnlyList<TimeSpan> keyframes)
            => Gate(path).TrySetResult(keyframes);

        /// <summary>
        /// FAULT the gated keyframe scan for <paramref name="path"/> (ffprobe blew up). Test-only
        /// failure injection beside <see cref="Release"/> — the third source of the identity-snap
        /// fallback (fail / cancel / empty), alongside the empty-list release.
        /// </summary>
        public void Fail(string path, Exception ex) => Gate(path).TrySetException(ex);

        /// <summary>
        /// CANCEL the gated keyframe scan for <paramref name="path"/> without touching the VM's own
        /// index CTS, so the cancelled-scan fallback can be exercised while the marker's resolve is
        /// still the current file's. Test-only cancel injection beside <see cref="Release"/>.
        /// </summary>
        public void CancelScan(string path) => Gate(path).TrySetCanceled();

        private TaskCompletionSource<IReadOnlyList<TimeSpan>> Gate(string path)
        {
            if (!_gates.TryGetValue(path, out var tcs))
            {
                tcs = new TaskCompletionSource<IReadOnlyList<TimeSpan>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates[path] = tcs;
            }

            return tcs;
        }

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

        /// <summary>Count of <see cref="Unload"/> calls (T-047).</summary>
        public int UnloadCount { get; private set; }

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

    /// <summary>
    /// A minimal single-threaded synchronization-context pump. The VM posts its background-scan
    /// completion back onto the captured context (WPF dispatcher in the app); this stands in for
    /// it so the continuation runs deterministically on the test thread. <see cref="RunUntil"/>
    /// drains posted callbacks until the predicate holds (or a timeout), advancing any Task the
    /// VM awaited on a background thread.
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

    private static (SplitViewModel Vm, GatedProbe Probe, RecordingPlayer Player) Build()
    {
        var probe = new GatedProbe();
        var player = new RecordingPlayer();
        var vm = new SplitViewModel(probe, new NoOpSplitEngine(), player);
        return (vm, probe, player);
    }

    /// <summary>Run <paramref name="body"/> under a single-threaded pump so posted continuations are deterministic.</summary>
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

    // ---- Non-blocking load ------------------------------------------------------------------

    [Fact]
    public void Load_ShowsPreviewAndInfo_BeforeKeyframeScanCompletes()
    {
        WithPump(pump =>
        {
            var (vm, probe, player) = Build();
            var info = new MediaInfo(TimeSpan.FromSeconds(90), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>());
            probe.ProbeResultToReturn = ProbeResult.Success(info);

            // Probe completes synchronously; keyframe scan stays GATED (not released).
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);

            // Preview + info committed although the keyframe scan has NOT completed.
            vm.Info.Should().BeSameAs(info);
            vm.InputPath.Should().Be(PathA);
            vm.HasFile.Should().BeTrue();
            player.Opened.Should().ContainSingle().Which.Should().Be(PathA);

            // The scan is still in flight → indexing flag is up, keyframes not yet populated.
            vm.IsIndexingKeyframes.Should().BeTrue();
            vm.KeyframesReady.Should().BeFalse();
            vm.Keyframes.Should().BeEmpty();
        });
    }

    [Fact]
    public void Load_WhenKeyframeScanCompletes_PopulatesKeyframesAndClearsIndexing()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            vm.IsIndexingKeyframes.Should().BeTrue();

            // 6s-apart keyframes → coarse GOP → warning expected once they arrive.
            var kf = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12) };
            probe.Release(PathA, kf);
            pump.RunUntil(() => !vm.IsIndexingKeyframes);

            vm.Keyframes.Should().HaveCount(3);
            vm.IsIndexingKeyframes.Should().BeFalse();
            vm.KeyframesReady.Should().BeTrue();
            vm.KeyframeWarning.Should().NotBeNull("6s GOP is coarse");
            vm.KeyframeWarning.Should().Contain("nearest keyframe");
        });
    }

    [Fact]
    public void Load_FineGop_NoWarning_AfterScanCompletes()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);

            probe.Release(PathA, new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) });
            pump.RunUntil(() => !vm.IsIndexingKeyframes);

            vm.KeyframeWarning.Should().BeNull("2s GOP is not coarse");
        });
    }

    // ---- Stale-scan guard -------------------------------------------------------------------

    [Fact]
    public void LoadB_WhileAIndexing_A_LateCompletion_DoesNotOverwriteB()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();

            // Load A — its keyframe scan is gated (in flight).
            var loadA = vm.LoadAsync(PathA);
            pump.RunUntil(() => loadA.IsCompleted);
            vm.IsIndexingKeyframes.Should().BeTrue();

            // Load B before A's scan finishes → A's index is cancelled, B's starts.
            var loadB = vm.LoadAsync(PathB);
            pump.RunUntil(() => loadB.IsCompleted);
            vm.InputPath.Should().Be(PathB);

            // Release B first with its own keyframes.
            var bKeyframes = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3) };
            probe.Release(PathB, bKeyframes);
            pump.RunUntil(() => !vm.IsIndexingKeyframes);
            vm.Keyframes.Should().BeEquivalentTo(bKeyframes);

            // NOW A completes late — its stale result must NOT overwrite B's keyframes.
            probe.Release(PathA, new[] { TimeSpan.FromSeconds(99) });
            // Drain any posted callbacks; B's keyframes must survive.
            pump.RunUntil(() => true, TimeSpan.FromMilliseconds(200));

            vm.Keyframes.Should().BeEquivalentTo(bKeyframes, "A's late scan was cancelled/superseded");
            vm.IsIndexingKeyframes.Should().BeFalse();
        });
    }

    // ---- Optimistic marker + async snap resolve (T-041) -------------------------------------

    [Fact]
    public void CutPlacedWhileIndexing_AddsInstantly_AsPending_ThenResolvesSnap()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            vm.IsIndexingKeyframes.Should().BeTrue();

            // Place a cut BEFORE keyframes exist → the marker appears INSTANTLY (T-041), pending snap.
            vm.AddCutAt(TimeSpan.FromSeconds(3.4));

            vm.Markers.Should().ContainSingle("the marker is added immediately, not deferred");
            vm.Markers[0].Requested.Should().Be(TimeSpan.FromSeconds(3.4));
            vm.Markers[0].IsSnapPending.Should().BeTrue();
            vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(3.4), "provisional identity snap while pending");
            vm.Markers[0].Display.Should().Contain("snapping…");

            // Release keyframes at whole seconds → 3.4 resolves in place to 3.0.
            probe.Release(PathA, Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray());
            pump.RunUntil(() => !vm.Markers[0].IsSnapPending);

            vm.Markers.Should().ContainSingle();
            vm.Markers[0].IsSnapPending.Should().BeFalse();
            vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(3), "resolved against the awaited in-flight scan");
            vm.Markers[0].Delta.Should().Be(TimeSpan.FromSeconds(3) - TimeSpan.FromSeconds(3.4));
        });
    }

    [Fact]
    public void AddCutAtCommand_WhileIndexing_AddsInstantly_AsPending_ThenResolves()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            vm.IsIndexingKeyframes.Should().BeTrue();

            // The timeline-click / playhead-capture entry points all route through AddCutAt; exercise
            // it via the command (SetCutAtPlayhead is additionally gated on Player.IsReady, which the
            // duration-less fake never reports — the instant-add behaviour is identical either way).
            vm.AddCutAtCommand.Execute(TimeSpan.FromSeconds(5.7));

            vm.Markers.Should().ContainSingle("AddCutAt adds the marker instantly while indexing");
            vm.Markers[0].IsSnapPending.Should().BeTrue();

            probe.Release(PathA, Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray());
            pump.RunUntil(() => !vm.Markers[0].IsSnapPending);

            vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(6), "5.7 snaps to 6");
        });
    }

    [Fact]
    public void CutPlacedWhenKeyframesReady_AddsSynchronously_NotPending()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);

            // Keyframes arrive FIRST → the ready path is taken.
            probe.Release(PathA, Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray());
            pump.RunUntil(() => !vm.IsIndexingKeyframes);

            vm.AddCutAt(TimeSpan.FromSeconds(3.4));

            vm.Markers.Should().ContainSingle();
            vm.Markers[0].IsSnapPending.Should().BeFalse("keyframes were ready → synchronous snap");
            vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(3), "snapped synchronously on add");
        });
    }

    [Fact]
    public void TwoCutsWhileIndexing_ResolvingToSameKeyframe_DedupeToOne()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);

            // Two DIFFERENT requested times that will BOTH snap to the 3.0 keyframe (3.2 and 3.4).
            vm.AddCutAt(TimeSpan.FromSeconds(3.2));
            vm.AddCutAt(TimeSpan.FromSeconds(3.4));

            // Different requested times → both add optimistically (requested-time dedupe lets them by).
            vm.Markers.Should().HaveCount(2, "distinct requested times both add while pending");

            probe.Release(PathA, Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray());
            pump.RunUntil(() => vm.Markers.Count == 1);

            // Both resolved to keyframe 3.0 → the resolve-time dedupe collapses them to one.
            vm.Markers.Should().ContainSingle("both snapped to the same keyframe → deduped on resolve");
            vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(3));
            vm.Markers[0].IsSnapPending.Should().BeFalse();
        });
    }

    [Fact]
    public void SameRequestedTimeWhileIndexing_DoesNotDoubleAdd()
    {
        WithPump(pump =>
        {
            var (vm, _, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);

            // Identical requested times must not double-add while pending (requested-time dedupe).
            vm.AddCutAt(TimeSpan.FromSeconds(3.4));
            vm.AddCutAt(TimeSpan.FromSeconds(3.4));

            vm.Markers.Should().ContainSingle("identical requested times dedupe before resolve");
        });
    }

    [Fact]
    public void PendingCut_WhenFileChanges_DoesNotCorruptNewFilesMarkers()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();

            // Load A, place a cut while A is still indexing → pending marker on A.
            var loadA = vm.LoadAsync(PathA);
            pump.RunUntil(() => loadA.IsCompleted);
            vm.AddCutAt(TimeSpan.FromSeconds(3.4));
            vm.Markers.Should().ContainSingle();

            // Load B before A's scan finishes → markers cleared, A's index cancelled, B's starts.
            var loadB = vm.LoadAsync(PathB);
            pump.RunUntil(() => loadB.IsCompleted);
            vm.InputPath.Should().Be(PathB);
            vm.Markers.Should().BeEmpty("a new load clears markers");

            // Place a cut on B (pending).
            vm.AddCutAt(TimeSpan.FromSeconds(7.0));
            vm.Markers.Should().ContainSingle();

            // A's scan completes LATE — its stale pending resolve must NOT touch B's markers.
            probe.Release(PathA, Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray());
            pump.RunUntil(() => true, TimeSpan.FromMilliseconds(200));

            // Now B resolves normally.
            probe.Release(PathB, Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray());
            pump.RunUntil(() => !vm.Markers[0].IsSnapPending);

            vm.Markers.Should().ContainSingle("only B's marker survives; A's stale resolve was dropped");
            vm.Markers[0].Requested.Should().Be(TimeSpan.FromSeconds(7.0));
            vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(7), "resolved against B's keyframes");
        });
    }

    [Fact]
    public void PendingCut_IndexFails_DoesNotCrash_IdentitySnap()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);

            vm.AddCutAt(TimeSpan.FromSeconds(3.4));
            vm.Markers.Should().ContainSingle();
            vm.Markers[0].IsSnapPending.Should().BeTrue();

            // Release an EMPTY list → snap falls back to identity — no crash, delta 0, pending cleared.
            probe.Release(PathA, Array.Empty<TimeSpan>());
            pump.RunUntil(() => !vm.Markers[0].IsSnapPending);

            vm.Markers.Should().ContainSingle();
            vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(3.4), "no keyframes → identity snap");
            vm.Markers[0].Delta.Should().Be(TimeSpan.Zero);
        });
    }

    // ---- Marker list stays time-ordered through pending→resolved (T-071) --------------------

    [Fact]
    public void PendingCutsAddedOutOfOrder_SettleIntoTimeOrder_OnResolve()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            vm.IsIndexingKeyframes.Should().BeTrue();

            // Add cuts out of chronological order WHILE indexing (all pending, provisional identity
            // snap → sorted by requested time for now: 2.6, 7.4, 5.1).
            vm.AddCutAt(TimeSpan.FromSeconds(7.4));
            vm.AddCutAt(TimeSpan.FromSeconds(2.6));
            vm.AddCutAt(TimeSpan.FromSeconds(5.1));

            // Even while pending, the provisional (requested-time) sort holds.
            vm.Markers.Select(m => m.Snapped).Should().BeInAscendingOrder("provisional order while pending");

            // Release whole-second keyframes → 7.4→7, 2.6→3, 5.1→5. The resolve re-positions each
            // marker so the final list is ascending by SNAPPED time.
            probe.Release(PathA, Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray());
            pump.RunUntil(() => vm.Markers.All(m => !m.IsSnapPending));

            vm.Markers.Should().HaveCount(3);
            vm.Markers.Select(m => m.Snapped).Should().ContainInOrder(
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7));
            vm.Markers.Select(m => m.Snapped).Should().BeInAscendingOrder(
                "each pending marker re-settled into its snapped-time slot");
        });
    }

    [Fact]
    public void PendingCut_ResolvingPastLaterMarkers_RepositionsToCorrectSlot()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);

            // Added while indexing: a marker requested at 4.6 lands mid-list by provisional time, but
            // its snapped time (5) must sit AFTER the 2 and 4 markers once every marker resolves —
            // exercises the remove+sorted-insert re-position, not just a stable no-op.
            vm.AddCutAt(TimeSpan.FromSeconds(4.6));
            vm.AddCutAt(TimeSpan.FromSeconds(2.1));
            vm.AddCutAt(TimeSpan.FromSeconds(3.9));

            probe.Release(PathA, Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray());
            pump.RunUntil(() => vm.Markers.All(m => !m.IsSnapPending));

            // 2.1→2, 3.9→4, 4.6→5 (distinct keyframes → no dedupe).
            vm.Markers.Should().HaveCount(3);
            vm.Markers.Select(m => m.Snapped).Should().ContainInOrder(
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
            vm.Markers.Select(m => m.Snapped).Should().BeInAscendingOrder("list stays sorted after resolve");
        });
    }

    // ---- Failure path unchanged -------------------------------------------------------------

    [Fact]
    public void Load_ProbeFailed_SurfacesFriendlyError_NoBackgroundIndex()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            probe.ProbeResultToReturn = ProbeResult.Failure("not a media file");

            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);

            vm.InputPath.Should().BeNull("a failed probe must not load the file");
            vm.Operation.State.Should().Be(OperationState.Failed);
            vm.Operation.Error.Should().NotBeNull();
            vm.Operation.Error!.RawTail.Should().Contain("not a media file");
            vm.StatusText.Should().NotBeNullOrEmpty();
            vm.IsIndexingKeyframes.Should().BeFalse("no background scan is started on a failed probe");
            probe.KeyframeRequests.Should().BeEmpty();
        });
    }

    // ---- Clear cancels the in-flight background index (T-047) -------------------------------

    [Fact]
    public void Clear_WhileIndexing_CancelsScan_LateCompletion_DoesNotRepopulateKeyframes()
    {
        WithPump(pump =>
        {
            var (vm, probe, player) = Build();

            // Load A — its keyframe scan is gated (still in flight).
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            vm.IsIndexingKeyframes.Should().BeTrue();
            vm.HasFile.Should().BeTrue();

            // Clear while the scan is running → file unloads, indexing flag drops, player unloaded.
            vm.ClearCommand.Execute(null);

            vm.HasFile.Should().BeFalse();
            vm.IsIndexingKeyframes.Should().BeFalse("clear cancels the in-flight scan");
            vm.Keyframes.Should().BeEmpty();
            player.UnloadCount.Should().BeGreaterThan(0);

            // The gated scan completes LATE — its stale result must NOT repopulate Keyframes.
            probe.Release(PathA, new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) });
            pump.RunUntil(() => true, TimeSpan.FromMilliseconds(200));

            vm.Keyframes.Should().BeEmpty("a scan superseded by Clear can never repopulate keyframes");
            vm.IsIndexingKeyframes.Should().BeFalse();
        });
    }

    // ---- One shared keyframe scan across many mid-index cuts (I13, SPEC-010) -----------------

    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public void MultipleCutsWhileIndexing_ShareSingleKeyframeScan_NotOnePerMarker()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();

            // Load A with the keyframe scan GATED (in flight) so every cut below is placed while
            // IsIndexingKeyframes is true — the optimistic/pending path.
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            vm.IsIndexingKeyframes.Should().BeTrue();

            // Three DISTINCT cuts placed mid-index → three pending markers, all riding the SAME
            // in-flight scan (AddCutAt awaits the shared _keyframeIndexTask, never a second
            // GetKeyframesAsync). Added out of chronological order to also exercise the sort.
            vm.AddCutAt(TimeSpan.FromSeconds(2.4));
            vm.AddCutAt(TimeSpan.FromSeconds(8.1));
            vm.AddCutAt(TimeSpan.FromSeconds(5.6));
            vm.Markers.Should().HaveCount(3);
            vm.Markers.Should().OnlyContain(m => m.IsSnapPending);

            // Release the ONE gated scan → all three pending markers resolve against it.
            probe.Release(PathA, Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToArray());
            pump.RunUntil(() => !vm.IsIndexingKeyframes && vm.Markers.All(m => !m.IsSnapPending));

            // PERF (dedup-coalesce, I13): exactly ONE scan served all three cuts — never one ffprobe
            // pass per marker. Structural, not a timer.
            probe.KeyframeRequests.Count(p => p == PathA).Should().Be(1,
                "a single background scan is shared by every mid-index cut, not re-run per marker");

            // CORRECTNESS: three markers survive (2.4→2, 5.6→6, 8.1→8 are distinct keyframes → no
            // collapse), all resolved (none pending), and the list is time-ordered by snapped time.
            vm.Markers.Should().HaveCount(3);
            vm.Markers.Should().OnlyContain(m => !m.IsSnapPending);
            vm.Markers.Select(m => m.Snapped).Should().ContainInOrder(
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(8));
            vm.Markers.Select(m => m.Snapped).Should().BeInAscendingOrder("snapped times ascending");
        });
    }

    // ---- A new load ACTIVELY cancels the prior scan's token (I5, SPEC-010) -------------------

    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public void LoadB_WhileAIndexing_ActivelyCancelsAScan_NotJustIgnoresLateResult()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();

            // Load A — its keyframe scan is gated (in flight, token live).
            var loadA = vm.LoadAsync(PathA);
            pump.RunUntil(() => loadA.IsCompleted);
            vm.IsIndexingKeyframes.Should().BeTrue();

            // Load B before A's scan finishes → StartKeyframeIndex cancels A's CTS, which synchronously
            // fires the token registration inside A's GetKeyframesAsync (records the path + cancels the
            // gate). B's scan is now the in-flight one.
            var loadB = vm.LoadAsync(PathB);
            pump.RunUntil(() => loadB.IsCompleted);
            vm.InputPath.Should().Be(PathB);

            // PERF (cancellation-honored, I5): A's scan token actually FIRED — this is ACTIVE
            // cancellation, not merely dropping a late result. Structural (cancelled-set membership).
            probe.CancelledScans.Should().Contain(PathA,
                "a newer load must cancel the prior file's scan token, not just ignore its result");

            // CORRECTNESS (latest-wins): release B first with its own keyframes → they commit.
            var bKeyframes = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3) };
            probe.Release(PathB, bKeyframes);
            pump.RunUntil(() => !vm.IsIndexingKeyframes);
            vm.Keyframes.Should().BeEquivalentTo(bKeyframes);

            // A's late release is a NO-OP — its gate was already cancelled and its continuation is
            // stale-guarded — so B's keyframes stand and indexing stays clear.
            probe.Release(PathA, new[] { TimeSpan.FromSeconds(99) });
            pump.RunUntil(() => true, TimeSpan.FromMilliseconds(200));

            vm.Keyframes.Should().BeEquivalentTo(bKeyframes, "A's cancelled scan can never overwrite B");
            vm.IsIndexingKeyframes.Should().BeFalse();
        });
    }

    // ---- Identity-snap fallback when the index fails / is cancelled (I16, SPEC-010) -----------

    // SPEC-010#I16 — when the keyframe index FAILS, is CANCELLED, or returns an EMPTY list, snapping
    // falls back to an identity snap (Snapped == Requested, Delta zero), the pending flag resolves,
    // the indexing flag clears, and nothing throws. PendingCut_IndexFails_DoesNotCrash_IdentitySnap
    // covers the EMPTY-list source (despite its name); these two cover the FAULTED and CANCELLED
    // sources of the same fallback.

    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public void PendingCut_WhenIndexFaults_FallsBackToIdentitySnap_ClearsIndexing_NoCrash()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            vm.IsIndexingKeyframes.Should().BeTrue();

            // A cut placed mid-index rides the in-flight scan (pending, provisional identity snap).
            vm.AddCutAt(TimeSpan.FromSeconds(3.4));
            vm.Markers.Should().ContainSingle();
            vm.Markers[0].IsSnapPending.Should().BeTrue();

            // The scan FAULTS (ffprobe blew up) instead of returning a keyframe list.
            var act = () =>
            {
                probe.Fail(PathA, new InvalidOperationException("ffprobe failed to read packets"));
                pump.RunUntil(() => !vm.IsIndexingKeyframes && !vm.Markers[0].IsSnapPending);
            };

            act.Should().NotThrow("a faulted keyframe index is absorbed, never surfaced as a crash");

            vm.Markers.Should().ContainSingle();
            vm.Markers[0].IsSnapPending.Should().BeFalse("the pending snap resolves even when the index failed");
            vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(3.4), "no keyframes → identity snap");
            vm.Markers[0].Delta.Should().Be(TimeSpan.Zero, "an identity snap carries a zero delta");
            vm.Keyframes.Should().BeEmpty("a faulted scan commits no keyframes");
            vm.IsIndexingKeyframes.Should().BeFalse("the indexing flag clears on failure, not only on success");
            vm.KeyframeWarning.Should().BeNull("no keyframes → no coarse-GOP warning");
        });
    }

    [Fact]
    [Trait("serves-spec", "SPEC-010")]
    public void PendingCut_WhenIndexIsCancelled_FallsBackToIdentitySnap_ClearsIndexing_NoCrash()
    {
        WithPump(pump =>
        {
            var (vm, probe, _) = Build();
            var load = vm.LoadAsync(PathA);
            pump.RunUntil(() => load.IsCompleted);
            vm.IsIndexingKeyframes.Should().BeTrue();

            vm.AddCutAt(TimeSpan.FromSeconds(5.7));
            vm.Markers.Should().ContainSingle();
            vm.Markers[0].IsSnapPending.Should().BeTrue();

            // The scan is CANCELLED (the VM's own index CTS is untouched, so this marker's resolve is
            // still the current file's — the cancelled-scan branch, not the stale-load branch).
            var act = () =>
            {
                probe.CancelScan(PathA);
                pump.RunUntil(() => !vm.IsIndexingKeyframes && !vm.Markers[0].IsSnapPending);
            };

            act.Should().NotThrow("a cancelled keyframe index is absorbed, never surfaced as a crash");

            vm.Markers.Should().ContainSingle();
            vm.Markers[0].IsSnapPending.Should().BeFalse("the pending snap resolves even when the index was cancelled");
            vm.Markers[0].Snapped.Should().Be(TimeSpan.FromSeconds(5.7), "no keyframes → identity snap");
            vm.Markers[0].Delta.Should().Be(TimeSpan.Zero, "an identity snap carries a zero delta");
            vm.Keyframes.Should().BeEmpty("a cancelled scan commits no keyframes");
            vm.IsIndexingKeyframes.Should().BeFalse("the indexing flag clears on cancellation too");
            vm.KeyframeWarning.Should().BeNull("no keyframes → no coarse-GOP warning");
        });
    }
}
