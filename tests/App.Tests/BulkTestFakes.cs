using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Thumbnails;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Real-snap fake probe for the Bulk Cut VM tests (T-096). Snapping/GOP use the REAL nearest-keyframe
/// math (copied minimal) so validity/snap assertions exercise real logic. Duration + keyframes are
/// per-path scriptable; <see cref="GatedPaths"/> + <see cref="ScanGate"/> let a test hold keyframe
/// scans open to observe the bounded-scan throttle and the KeyframesReady run-gate.
/// </summary>
internal sealed class BulkFakeProbe : IMediaProbe
{
    private readonly object _lock = new();

    /// <summary>Per-path (duration, keyframes). Missing path ⇒ <see cref="DefaultDuration"/> / <see cref="DefaultKeyframes"/>.</summary>
    public Dictionary<string, (TimeSpan Duration, IReadOnlyList<TimeSpan> Keyframes)> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths whose <see cref="ProbeAsync"/> returns a failure (⇒ LoadFailed row).</summary>
    public HashSet<string> FailProbePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromSeconds(60);

    public IReadOnlyList<TimeSpan> DefaultKeyframes { get; set; } = Array.Empty<TimeSpan>();

    /// <summary>When a scan enters for a gated path it awaits this gate — set to hold scans open.</summary>
    public TaskCompletionSource ScanGate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Paths whose keyframe scan blocks on <see cref="ScanGate"/> (empty ⇒ every scan is immediate).</summary>
    public HashSet<string> GatedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gate ALL paths (convenience for the concurrency test).</summary>
    public bool GateEverything { get; set; }

    public int CurrentScans { get; private set; }

    public int PeakScans { get; private set; }

    /// <summary>Register keyframes for a path spaced every <paramref name="stepSeconds"/> across <paramref name="duration"/>.</summary>
    public void SetUniform(string path, TimeSpan duration, double stepSeconds)
    {
        var kf = new List<TimeSpan>();
        for (var t = 0.0; t <= duration.TotalSeconds + 1e-6; t += stepSeconds)
        {
            kf.Add(TimeSpan.FromSeconds(Math.Round(t, 3)));
        }

        ByPath[path] = (duration, kf);
    }

    public void ReleaseScans() => ScanGate.TrySetResult();

    public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (FailProbePaths.Contains(path))
        {
            return Task.FromResult(ProbeResult.Failure($"scripted probe failure for '{path}'"));
        }

        var duration = ByPath.TryGetValue(path, out var entry) ? entry.Duration : DefaultDuration;
        var info = new MediaInfo(duration, "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>());
        return Task.FromResult(ProbeResult.Success(info));
    }

    public async Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
    {
        lock (_lock)
        {
            CurrentScans++;
            if (CurrentScans > PeakScans)
            {
                PeakScans = CurrentScans;
            }
        }

        try
        {
            if (GateEverything || GatedPaths.Contains(path))
            {
                await ScanGate.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            return ByPath.TryGetValue(path, out var entry) ? entry.Keyframes : DefaultKeyframes;
        }
        finally
        {
            lock (_lock)
            {
                CurrentScans--;
            }
        }
    }

    public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
    {
        if (keyframes.Count == 0)
        {
            throw new ArgumentException("empty keyframes", nameof(keyframes));
        }

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

/// <summary>
/// Records the <c>items</c>/<c>options</c> it was handed, replays a scripted <see cref="BulkTrimProgress"/>
/// sequence, and returns a scripted <see cref="BatchResult"/> — the seam that proves
/// <see cref="BulkCutViewModel.RunBatchAsync"/> DELEGATES to <see cref="IBulkTrimEngine.RunAsync"/>.
/// </summary>
internal sealed class FakeBulkTrimEngine : IBulkTrimEngine
{
    public int CallCount { get; private set; }

    public IReadOnlyList<BulkTrimItem>? ReceivedItems { get; private set; }

    public BulkTrimOptions? ReceivedOptions { get; private set; }

    public IReadOnlyList<BulkTrimProgress> ProgressScript { get; set; } = Array.Empty<BulkTrimProgress>();

    /// <summary>Invoked at the start of a run (before the result is built) — a test uses it to cancel mid-run.</summary>
    public Action? BeforeReturn { get; set; }

    /// <summary>Builds the returned ledger; default = one <see cref="ItemOutcome.Done"/> per item.</summary>
    public Func<IReadOnlyList<BulkTrimItem>, CancellationToken, BatchResult>? ResultFactory { get; set; }

    public Task<BatchResult> RunAsync(
        IReadOnlyList<BulkTrimItem> items,
        BulkTrimOptions options,
        IProgress<BulkTrimProgress>? progress = null,
        CancellationToken ct = default)
    {
        CallCount++;
        ReceivedItems = items;
        ReceivedOptions = options;

        foreach (var sample in ProgressScript)
        {
            progress?.Report(sample);
        }

        BeforeReturn?.Invoke();

        var result = ResultFactory?.Invoke(items, ct) ?? AllDone(items);
        return Task.FromResult(result);
    }

    /// <summary>Default all-Done ledger: output = each item's desired path, no error, no warnings.</summary>
    public static BatchResult AllDone(IReadOnlyList<BulkTrimItem> items)
    {
        var rows = items
            .Select(i => new BulkTrimItemResult(i, ItemOutcome.Done, i.DesiredOutputPath, null, Array.Empty<string>()))
            .ToList();
        return new BatchResult(BatchOutcome.Completed, rows);
    }
}

/// <summary>
/// An <see cref="ISplitEngine"/> that must NEVER be called: <see cref="RunBatchAsync"/> delegates the whole
/// batch to <see cref="IBulkTrimEngine"/>, so a direct <see cref="SplitAsync"/> here means the VM
/// re-implemented the loop. Records the fact (asserted false) and throws to make the misuse loud.
/// </summary>
internal sealed class ThrowingFakeSplitEngine : ISplitEngine
{
    public bool WasCalled { get; private set; }

    public Task<SplitResult> SplitAsync(
        SplitRequest req,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IProgress<OperationStatus>? status = null,
        IProgress<PartProgress>? partProgress = null)
    {
        WasCalled = true;
        throw new InvalidOperationException(
            "ISplitEngine.SplitAsync must not be called from the App VM — RunBatchAsync must delegate to IBulkTrimEngine.RunAsync.");
    }
}

/// <summary>No-op thumbnail service (the per-row hover preview is a T-097 view concern).</summary>
internal sealed class FakeThumbnailService : IThumbnailService
{
    public Task<string?> GetThumbnailAsync(string inputPath, TimeSpan time, int width, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public void Clear(string inputPath)
    {
    }

    public void ClearAll()
    {
    }
}

/// <summary>In-memory settings (no disk I/O) for the VM tests.</summary>
internal sealed class FakeSettings : IAppSettings
{
    public string? LastInputDir { get; set; }

    public string? LastOutputDir { get; set; }

    public LayoutMode LayoutMode { get; set; }

    public double? HorizontalSplitRatio { get; set; }

    public double? VerticalSplitRatio { get; set; }
}
