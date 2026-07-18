using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-044: the Split/Join VMs must forward the engine's staged <see cref="OperationStatus"/> reports
/// into <see cref="OperationViewModel.StatusText"/>, formatted "Stage… (detail)". A fake engine that
/// reports the real stage sequence drives the VM; a PropertyChanged listener records every
/// StatusText value so we can assert the label transitions through the stages (Preparing… →
/// Splitting… (M parts) → Finalizing… → Done for split; the join analog for join).
/// </summary>
public sealed class StagedStatusWiringTests
{
    private const string FakePath = @"C:\videos\clip.mp4";

    // ---- Deterministic dispatch harness -----------------------------------------------------

    /// <summary>
    /// A minimal single-threaded <see cref="SynchronizationContext"/> that queues every
    /// <see cref="Post"/> callback and runs them, in FIFO order, only when <see cref="Drain"/> is
    /// called. This reproduces the WPF Dispatcher semantics the product relies on:
    /// <see cref="OperationViewModel"/> marshals its staged <c>IProgress&lt;OperationStatus&gt;</c>
    /// reports through <see cref="Progress{T}"/>, which captures the ambient
    /// <see cref="SynchronizationContext"/>. In a real app that context is the ordered, single-threaded
    /// UI message pump. Under xUnit's default there is NO context, so <see cref="Progress{T}"/> falls
    /// back to the ThreadPool — each report runs on an arbitrary pool thread with no ordering and may
    /// not have executed before the test asserts, scrambling/dropping the recorded stage sequence.
    /// Installing this context on the test thread restores the production ordering guarantee, so the
    /// test verifies the REAL marshalling path (ordered FIFO) rather than the ThreadPool fallback —
    /// without weakening what is asserted.
    /// </summary>
    private sealed class OrderedSyncContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        // Send runs inline — a real dispatcher would marshal, but for tests the ordering that matters
        // is Post (what Progress<T> uses); running Send inline keeps any synchronous callers correct.
        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>Run every queued callback, in the order it was posted, until the queue is empty.</summary>
        public void Drain()
        {
            while (_queue.TryDequeue(out var work))
            {
                work.Callback(work.State);
            }
        }
    }

    /// <summary>
    /// Run <paramref name="body"/> with an <see cref="OrderedSyncContext"/> installed as the ambient
    /// synchronization context, then drain all posted callbacks so every staged status report has been
    /// delivered — in order — before the caller asserts. Restores the previous context on exit.
    /// </summary>
    private static async Task WithOrderedDispatchAsync(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        var ctx = new OrderedSyncContext();
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            await body().ConfigureAwait(true);
            // Flush every Progress<T> callback the run posted, in FIFO order, before returning.
            ctx.Drain();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // ---- Fakes ------------------------------------------------------------------------------

    /// <summary>Minimal probe with a fixed duration + evenly-spaced keyframes for snapping.</summary>
    private sealed class FakeProbe : IMediaProbe
    {
        public IReadOnlyList<TimeSpan> Keyframes { get; set; } =
            Enumerable.Range(0, 61).Select(i => TimeSpan.FromSeconds(i)).ToArray();

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResult.Success(
                new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult(Keyframes);

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

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.FromSeconds(1);

        private static TimeSpan Abs(TimeSpan t) => t < TimeSpan.Zero ? t.Negate() : t;
    }

    /// <summary>Split engine that reports a scripted staged sequence through the T-044 status channel.</summary>
    private sealed class StagedSplitEngine : ISplitEngine
    {
        public Task<SplitResult> SplitAsync(
            SplitRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<OperationStatus>? status = null,
            IProgress<PartProgress>? partProgress = null)
        {
            status?.Report(new OperationStatus("Preparing"));
            status?.Report(new OperationStatus("Splitting", "3 parts"));
            status?.Report(new OperationStatus("Finalizing"));
            status?.Report(new OperationStatus("Done", null, 1.0));
            return Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
        }
    }

    /// <summary>Join engine that reports a scripted staged sequence through the T-044 status channel.</summary>
    private sealed class StagedJoinEngine : IJoinEngine
    {
        public Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => Task.FromResult(CompatReport.Ok());

        public Task<JoinResult> JoinAsync(
            JoinRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<OperationStatus>? status = null)
        {
            status?.Report(new OperationStatus("Checking compatibility"));
            status?.Report(new OperationStatus("Joining", "2 clips"));
            status?.Report(new OperationStatus("Finalizing"));
            status?.Report(new OperationStatus("Done", null, 1.0));
            return Task.FromResult(JoinResult.Ok(req.OutputPath));
        }
    }

    /// <summary>Join probe fake — a video stream so the info chip / compat path is well-formed.</summary>
    private sealed class JoinProbe : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResult.Success(new MediaInfo(
                TimeSpan.FromSeconds(5), "mp4",
                new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
                Array.Empty<StreamInfo>())));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimeSpan>>(Array.Empty<TimeSpan>());

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
            => new(requested, TimeSpan.Zero);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    private static List<string> RecordStatusText(OperationViewModel op)
    {
        var seen = new List<string>();
        op.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationViewModel.StatusText))
            {
                seen.Add(op.StatusText);
            }
        };
        return seen;
    }

    // ---- Split ------------------------------------------------------------------------------

    [Fact]
    public async Task RunSplit_StatusTextTransitionsThroughStages()
    {
        var probe = new FakeProbe();
        var vm = new SplitViewModel(probe, new StagedSplitEngine());
        await vm.LoadAsync(FakePath);
        vm.OutputDir = @"C:\out";
        vm.AddMarker(TimeSpan.FromSeconds(20));
        vm.AddMarker(TimeSpan.FromSeconds(40));

        var seen = RecordStatusText(vm.Operation);

        // Drive the run under an ordered single-threaded dispatch context (mirrors the WPF Dispatcher)
        // and drain every posted status callback before asserting — so the recorded sequence is the
        // deterministic, in-order one the product produces in a real app, not the ThreadPool-scrambled
        // fallback xUnit's context-free thread would yield.
        await WithOrderedDispatchAsync(() => vm.RunSplitAsync());

        // The staged labels must have appeared, in order, on the UI-bound StatusText.
        seen.Should().ContainInOrder(
            "Preparing…", "Splitting… (3 parts)", "Finalizing…", "Done");
        vm.Operation.State.Should().Be(OperationState.Completed);
    }

    // ---- Join -------------------------------------------------------------------------------

    [Fact]
    public async Task RunJoin_StatusTextTransitionsThroughStages()
    {
        var vm = new JoinViewModel(new StagedJoinEngine(), new JoinProbe());
        await vm.AddFilesAsync(new[] { @"C:\videos\a.mp4", @"C:\videos\b.mp4" });
        vm.OutputPath = @"C:\videos\joined.mp4";

        var seen = RecordStatusText(vm.Operation);

        // Same deterministic ordered dispatch as the split test (see WithOrderedDispatchAsync).
        await WithOrderedDispatchAsync(() => vm.RunJoinAsync());

        seen.Should().ContainInOrder(
            "Checking compatibility…", "Joining… (2 clips)", "Finalizing…", "Done");
        vm.Operation.State.Should().Be(OperationState.Completed);
    }
}
