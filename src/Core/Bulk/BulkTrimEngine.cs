using System.Text.RegularExpressions;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Io;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// The D-004 Bulk Cut Core orchestrator: runs many single-kept-segment trims SEQUENTIALLY and
/// FAILURE-ISOLATED over the existing <see cref="ISplitEngine"/>. It owns only the batch loop,
/// failure isolation, cancel semantics, collision policy, batch disk pre-flight, progress rollup,
/// and the result ledger — every row still goes through <see cref="ISplitEngine.SplitAsync"/>, so
/// the <c>-c copy</c> invariant, temp-then-move cancel-safety, and per-run disk pre-flight are all
/// inherited (no second ffmpeg path). UI-free: only BCL + Core types.
/// </summary>
public sealed class BulkTrimEngine : IBulkTrimEngine
{
    /// <summary>Container/overhead slack added to each output drive's required-space estimate (mirrors <c>SplitEngine</c>).</summary>
    private const long PreflightMarginBytes = 16L * 1024 * 1024;

    private static readonly IReadOnlyList<string> NoWarnings = Array.Empty<string>();

    private readonly ISplitEngine _splitEngine;
    private readonly IBulkTrimRequestBuilder _requestBuilder;
    private readonly IDiskSpaceProbe _diskProbe;

    /// <summary>Create the batch runner over the shared split engine and request builder (default disk probe).</summary>
    public BulkTrimEngine(ISplitEngine splitEngine, IBulkTrimRequestBuilder requestBuilder)
        : this(splitEngine, requestBuilder, new DriveInfoDiskSpaceProbe())
    {
    }

    /// <summary>Create the batch runner with an explicit disk-space probe (used by tests to force a shortfall / an unmeasurable drive).</summary>
    public BulkTrimEngine(ISplitEngine splitEngine, IBulkTrimRequestBuilder requestBuilder, IDiskSpaceProbe diskProbe)
    {
        _splitEngine = splitEngine ?? throw new ArgumentNullException(nameof(splitEngine));
        _requestBuilder = requestBuilder ?? throw new ArgumentNullException(nameof(requestBuilder));
        _diskProbe = diskProbe ?? throw new ArgumentNullException(nameof(diskProbe));
    }

    /// <inheritdoc />
    public async Task<BatchResult> RunAsync(
        IReadOnlyList<BulkTrimItem> items,
        BulkTrimOptions options,
        IProgress<BulkTrimProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Guard — nothing to do.
        if (items is null || items.Count == 0)
        {
            return new BatchResult(BatchOutcome.Completed, Array.Empty<BulkTrimItemResult>());
        }

        var opts = options ?? new BulkTrimOptions();
        var n = items.Count;
        var results = new BulkTrimItemResult?[n];
        var effective = new string[n];
        var overwrite = new bool[n];

        progress?.Report(new BulkTrimProgress(0, n, string.Empty, 0.0, 0.0, BulkTrimPhase.Preflight));

        // 2. Collision pre-resolve (per item, before any run). A resolution failure is isolated to
        //    its own row (Failed) — it never aborts the batch.
        for (var i = 0; i < n; i++)
        {
            var item = items[i];
            try
            {
                var (eff, ow, skip) = ResolveCollision(item, opts);
                effective[i] = eff;
                overwrite[i] = ow;
                if (skip)
                {
                    results[i] = new BulkTrimItemResult(item, ItemOutcome.Skipped, null, null, NoWarnings);
                }
            }
            catch (Exception ex)
            {
                effective[i] = item.DesiredOutputPath;
                results[i] = new BulkTrimItemResult(item, ItemOutcome.Failed, null, WrapError(ex), NoWarnings);
            }
        }

        // 3. Batch disk pre-flight — block the WHOLE batch before any ffmpeg runs on a knowable
        //    shortfall; unmeasurable drives skip the check (never a false-positive block).
        if (IsBlockedByDiskPreflight(items, effective, results))
        {
            var diskErr = new UserFacingError(
                ErrorCategory.DiskFull,
                "Not enough space to write the outputs — free up space or choose another output folder.",
                string.Empty,
                "Free up space on the output drive, or choose a different output folder.");

            for (var i = 0; i < n; i++)
            {
                results[i] = new BulkTrimItemResult(items[i], ItemOutcome.NotStarted, null, diskErr, NoWarnings);
            }

            return new BatchResult(BatchOutcome.Blocked, Materialize(results));
        }

        // 4. Sequential run — head-to-tail, failure-isolated, cancel-aware.
        var runnableTotal = 0;
        for (var i = 0; i < n; i++)
        {
            if (results[i] is null)
            {
                runnableTotal++;
            }
        }

        var runnableDone = 0;
        var cancelled = false;

        for (var i = 0; i < n; i++)
        {
            if (results[i] is not null)
            {
                continue; // pre-skipped or resolution-failed — already decided.
            }

            // Cancelled BEFORE this row starts → this row (and the rest) never started.
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var item = items[i];
            var fileName = Path.GetFileName(item.InputPath);
            var baseDone = runnableDone;

            IProgress<double>? rowProgress = progress is null
                ? null
                : new DelegateProgress<double>(local =>
                {
                    var clamped = Clamp01(local);
                    var overall = runnableTotal == 0 ? 0.0 : (baseDone + clamped) / runnableTotal;
                    progress.Report(new BulkTrimProgress(i, n, fileName, clamped, overall, BulkTrimPhase.Item));
                });

            try
            {
                var req = await _requestBuilder.BuildAsync(item, effective[i], overwrite[i], ct).ConfigureAwait(false);
                var splitResult = await _splitEngine.SplitAsync(req, rowProgress, ct).ConfigureAwait(false);

                results[i] = new BulkTrimItemResult(
                    item, ItemOutcome.Done, effective[i], null, splitResult?.Warnings ?? NoWarnings);
                runnableDone++;
                ReportRowComplete(progress, i, n, fileName, runnableDone, runnableTotal);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // In-flight row cancelled: the engine already swept its temp (a partial is NEVER
                // moved into place); classify this row Cancelled and stop before the next.
                results[i] = new BulkTrimItemResult(item, ItemOutcome.Cancelled, null, null, NoWarnings);
                cancelled = true;
                break;
            }
            catch (NoOpTrimException)
            {
                // Both boundaries collapsed → nothing would be removed → a deliberate skip, not a failure.
                results[i] = new BulkTrimItemResult(item, ItemOutcome.Skipped, null, null, NoWarnings);
                runnableDone++;
                ReportRowComplete(progress, i, n, fileName, runnableDone, runnableTotal);
            }
            catch (SplitException ex)
            {
                // Isolated per row — a mid-run ENOSPC on one large file does not abort a smaller later one.
                results[i] = new BulkTrimItemResult(item, ItemOutcome.Failed, null, MapSplitException(ex), NoWarnings);
                runnableDone++;
                ReportRowComplete(progress, i, n, fileName, runnableDone, runnableTotal);
            }
            catch (Exception ex)
            {
                // Defensive: an IOException / UnauthorizedAccessException from a builder or a collision
                // race must not abort the batch. (OperationCanceledException with a genuinely-cancelled
                // token is handled above; anything else lands here as a Failed row.)
                results[i] = new BulkTrimItemResult(item, ItemOutcome.Failed, null, WrapError(ex), NoWarnings);
                runnableDone++;
                ReportRowComplete(progress, i, n, fileName, runnableDone, runnableTotal);
            }
        }

        // 5. Finalize — fill any undecided rows (only happens on cancel) as NotStarted, pick the
        //    batch outcome, and report a terminal progress sample.
        for (var i = 0; i < n; i++)
        {
            results[i] ??= new BulkTrimItemResult(items[i], ItemOutcome.NotStarted, null, null, NoWarnings);
        }

        var ledger = Materialize(results);

        if (!cancelled)
        {
            progress?.Report(new BulkTrimProgress(n, n, string.Empty, 1.0, 1.0, BulkTrimPhase.Running));
        }

        var outcome = ResolveBatchOutcome(ledger, cancelled);
        return new BatchResult(outcome, ledger);
    }

    // --- Collision resolution -----------------------------------------------------------------

    /// <summary>
    /// Resolve one item's effective output path + overwrite flag + skip decision per the options.
    /// Under <see cref="OutputMode.NewFile"/> (the default) the SOURCE is never a write target: any
    /// policy that would resolve to the input path is forced onto an AutoSuffix name instead. That
    /// guard is bypassed - deliberately, and only - for the explicitly opt-in
    /// <see cref="OutputMode.ReplaceOriginal"/>, where the source IS the destination by definition.
    /// </summary>
    private static (string Effective, bool Overwrite, bool Skip) ResolveCollision(BulkTrimItem item, BulkTrimOptions opts)
    {
        var inputFull = Path.GetFullPath(item.InputPath);

        // T-121: replace-the-original is a destination choice, not a collision outcome - it wins, and
        // the collision policy is moot (the destination is always taken, by the source itself). The
        // engine still writes to a temp file and only replaces the original after a verified run.
        if (opts.Output == OutputMode.ReplaceOriginal)
        {
            return (inputFull, true, false);
        }

        var desiredFull = Path.GetFullPath(item.DesiredOutputPath);

        switch (opts.Collision)
        {
            case CollisionPolicy.Overwrite:
                if (PathsEqual(desiredFull, inputFull))
                {
                    return (ResolveAutoSuffix(item.DesiredOutputPath, inputFull), false, false);
                }

                return (desiredFull, true, false);

            case CollisionPolicy.Skip:
                if (PathsEqual(desiredFull, inputFull))
                {
                    return (ResolveAutoSuffix(item.DesiredOutputPath, inputFull), false, false);
                }

                return File.Exists(desiredFull)
                    ? (desiredFull, false, true)
                    : (desiredFull, false, false);

            case CollisionPolicy.AutoSuffix:
            default:
                return (ResolveAutoSuffix(item.DesiredOutputPath, inputFull), false, false);
        }
    }

    /// <summary>
    /// Return the first free path at/after <paramref name="desiredPath"/>: the base itself when free,
    /// else <c>&lt;stem&gt;_2&lt;ext&gt;</c>, <c>_3</c>, … Never returns the source path.
    /// </summary>
    private static string ResolveAutoSuffix(string desiredPath, string inputFull)
    {
        var full = Path.GetFullPath(desiredPath);
        if (!File.Exists(full) && !PathsEqual(full, inputFull))
        {
            return full;
        }

        var dir = Path.GetDirectoryName(full) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);

        for (var suffix = 2; suffix <= 10_000; suffix++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, $"{stem}_{suffix}{ext}"));
            if (!File.Exists(candidate) && !PathsEqual(candidate, inputFull))
            {
                return candidate;
            }
        }

        throw new SplitException($"Could not find a free output name near '{full}' after 10000 attempts.");
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // --- Disk pre-flight ----------------------------------------------------------------------

    /// <summary>
    /// True when a knowable per-drive shortfall means the whole batch should be blocked before any
    /// ffmpeg runs. Source size is a safe UPPER bound on each output (trimming only removes bytes).
    /// Best-effort: an unmeasurable drive, or an input whose size can't be read, skips that root.
    /// </summary>
    private bool IsBlockedByDiskPreflight(
        IReadOnlyList<BulkTrimItem> items,
        IReadOnlyList<string> effective,
        IReadOnlyList<BulkTrimItemResult?> results)
    {
        var byRoot = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            if (results[i] is not null)
            {
                continue; // skipped / resolution-failed rows do not run, so exclude them.
            }

            var root = Path.GetPathRoot(effective[i]);
            if (string.IsNullOrEmpty(root))
            {
                continue; // unrooted / unmeasurable → skip this row.
            }

            if (!byRoot.TryGetValue(root, out var list))
            {
                list = new List<int>();
                byRoot[root] = list;
            }

            list.Add(i);
        }

        foreach (var (root, idxs) in byRoot)
        {
            var free = _diskProbe.GetAvailableFreeBytes(root);
            if (free is null)
            {
                continue; // unmeasurable drive → skip (never a false-positive block).
            }

            long required = PreflightMarginBytes;
            var measurable = true;
            foreach (var i in idxs)
            {
                try
                {
                    var len = new FileInfo(items[i].InputPath).Length;
                    if (len > 0)
                    {
                        required += len;
                    }
                }
                catch
                {
                    measurable = false;
                    break;
                }
            }

            if (!measurable)
            {
                continue; // could not size the inputs for this root → skip.
            }

            if (free.Value < required)
            {
                return true; // a clear shortfall on any ready drive blocks the whole batch.
            }
        }

        return false;
    }

    // --- Error mapping ------------------------------------------------------------------------

    /// <summary>Map a <see cref="SplitException"/> to a user-facing error, keeping ffmpeg detail when present.</summary>
    private static UserFacingError MapSplitException(SplitException ex)
    {
        if (!string.IsNullOrEmpty(ex.FullStdErr))
        {
            var tail = ex.FullStdErr.Replace("\r\n", "\n").Split('\n');
            var mapped = FfmpegErrorMapper.Map(tail, ParseFfmpegExit(ex.Message));
            return mapped with { LogFilePath = ex.LogFilePath ?? mapped.LogFilePath, FullText = ex.FullStdErr };
        }

        // Validation-only failure (no ffmpeg run) — surface the friendly message, keep any log path.
        return new UserFacingError(
            ErrorCategory.Unknown,
            ex.Message,
            RawTail: string.Empty,
            Hint: null,
            LogFilePath: ex.LogFilePath,
            FullText: null);
    }

    private static UserFacingError WrapError(Exception ex) =>
        new(ErrorCategory.Unknown, ex.Message, RawTail: string.Empty);

    /// <summary>Recover the ffmpeg exit code from a mapped SplitException message (<c>"… (ffmpeg exit -28)."</c>), else 0.</summary>
    private static int ParseFfmpegExit(string? message)
    {
        var m = Regex.Match(message ?? string.Empty, @"ffmpeg exit (-?\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var code) ? code : 0;
    }

    // --- Progress / finalize helpers ----------------------------------------------------------

    private static void ReportRowComplete(
        IProgress<BulkTrimProgress>? progress, int index, int count, string fileName, int done, int total)
    {
        if (progress is null)
        {
            return;
        }

        var overall = total == 0 ? 1.0 : (double)done / total;
        progress.Report(new BulkTrimProgress(index, count, fileName, 1.0, overall, BulkTrimPhase.Item));
    }

    private static BatchOutcome ResolveBatchOutcome(IReadOnlyList<BulkTrimItemResult> ledger, bool cancelled)
    {
        if (cancelled)
        {
            return BatchOutcome.Cancelled;
        }

        var anyImperfect = false;
        foreach (var r in ledger)
        {
            if (r.Outcome is ItemOutcome.Failed or ItemOutcome.Skipped)
            {
                anyImperfect = true;
                break;
            }
        }

        return anyImperfect ? BatchOutcome.CompletedWithFailures : BatchOutcome.Completed;
    }

    private static IReadOnlyList<BulkTrimItemResult> Materialize(BulkTrimItemResult?[] results)
    {
        var ledger = new BulkTrimItemResult[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            ledger[i] = results[i]!;
        }

        return ledger;
    }

    private static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;

    /// <summary>
    /// A minimal synchronous <see cref="IProgress{T}"/> — invokes the handler inline on the reporting
    /// thread (Core has no UI context to marshal to; the App passes a context-marshalling reporter
    /// downstream). Mirrors <c>SplitEngine.SyncProgress</c>.
    /// </summary>
    private sealed class DelegateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public DelegateProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
