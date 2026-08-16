using System.Globalization;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// Splits a media file into contiguous segments at keyframe-snapped cut points, using
/// lossless stream-copy (<c>-c copy</c>) — near-instant, no quality loss.
/// </summary>
public interface ISplitEngine
{
    /// <summary>
    /// Split <paramref name="req"/>'s input at its cut points. Probes the file, validates and
    /// snaps the cuts to keyframes, and extracts N segments via the segment muxer (single copy
    /// pass), preserving ALL streams. Reports progress 0..1. Cancellation removes any partially
    /// written final output. Throws <see cref="SplitException"/> for a genuinely invalid request
    /// (missing input, unwritable dir, no valid cuts, refused overwrite).
    /// <paramref name="status"/> (optional, T-044) receives a stage transition as the engine enters
    /// each real phase: Preparing → Splitting → Finalizing → Done — synced to the actual work, never
    /// a timer. The numeric <paramref name="progress"/> channel is unchanged.
    /// <paramref name="partProgress"/> (optional, T-069) receives per-part samples as each part is
    /// written — the current 1-based part index, the total part count, and that part's local 0..1
    /// fraction. Both split paths report it: the per-segment subset path from its natural loop, the
    /// fast single-pass muxer path DERIVED from the ffmpeg time it already parses (no extra passes).
    /// Additive — the overall <paramref name="progress"/> and staged <paramref name="status"/>
    /// channels are unchanged.
    /// </summary>
    Task<SplitResult> SplitAsync(
        SplitRequest req,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IProgress<OperationStatus>? status = null,
        IProgress<PartProgress>? partProgress = null);
}

/// <inheritdoc cref="ISplitEngine" />
public sealed class SplitEngine : ISplitEngine
{
    private readonly IFfmpegRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly ErrorLogWriter _logWriter;

    /// <summary>Create the engine over the T-002 runner and T-003 probe.</summary>
    public SplitEngine(IFfmpegRunner runner, IMediaProbe probe)
        : this(runner, probe, new ErrorLogWriter())
    {
    }

    /// <summary>
    /// Create the engine with an explicit <see cref="ErrorLogWriter"/> (used by tests to redirect the
    /// full-error log to a temp directory).
    /// </summary>
    public SplitEngine(IFfmpegRunner runner, IMediaProbe probe, ErrorLogWriter logWriter)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _logWriter = logWriter ?? throw new ArgumentNullException(nameof(logWriter));
    }

    /// <inheritdoc />
    public async Task<SplitResult> SplitAsync(
        SplitRequest req,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IProgress<OperationStatus>? status = null,
        IProgress<PartProgress>? partProgress = null)
    {
        ArgumentNullException.ThrowIfNull(req);
        ValidateRequestShape(req);

        // T-044: entering the prepare phase — probe + cut-point planning/snapping.
        // T-093: enrich the detail so the Preparing stage reads "Preparing — scanning keyframes…"
        // rather than a bare "Preparing…". No numeric fraction is reported here, so the operation VM
        // keeps IsIndeterminate = true and the bar shows the busy pulse (never a frozen 0%).
        status?.Report(new OperationStatus("Preparing", "scanning keyframes…"));

        // --- Probe the input for duration + keyframes (via T-003, which uses T-002). ---
        // T-093: ProbeAsync is a metadata-only query (-show_streams/-show_format, no packet or frame
        // decode) so it is cheap; it is NOT the heavy cost this ticket targets and is left as-is.
        var probeResult = await _probe.ProbeAsync(req.InputPath, ct).ConfigureAwait(false);
        if (probeResult is not ProbeResult.ProbeSucceeded ok)
        {
            var reason = probeResult is ProbeResult.ProbeFailed f ? f.Reason : "unknown probe error";
            throw new SplitException($"Cannot split '{req.InputPath}': {reason}");
        }

        var duration = ok.Info.Duration;

        // T-093: the heavy keyframe scan. With MediaProbe's in-flight dedup + shared cache (see
        // MediaProbe.GetKeyframesAsync), the load-time background scan the UI already kicked off is
        // REUSED here — this call awaits that running scan or hits its cached result rather than
        // launching a SECOND full ffprobe pass. Zero redundant scan when the UI already has (or is
        // computing) the keyframes.
        var keyframes = await _probe.GetKeyframesAsync(req.InputPath, ct).ConfigureAwait(false);
        var averageGop = _probe.AverageGop(keyframes);

        // --- Plan (pure): validate cuts, snap, build contiguous ranges. ---
        var plan = SplitPlanner.Plan(
            duration,
            req.CutPoints,
            keyframes,
            _probe.SnapToNearestKeyframe,
            averageGop,
            index => ResolveOutputPath(req, index));

        // T-049: resolve which contiguous parts the caller actually wants written. null selection =
        // ALL parts (today's behaviour, the fast muxer path). A strict SUBSET keeps each part's
        // ORIGINAL 1-based index (so a selected middle part is still …_part02) and is extracted via
        // the per-segment -ss/-to copy path — unselected parts are never written.
        var (selected, isFullSet) = ResolveSelectedSegments(req, plan);

        // The end of the file — used so the FINAL selected part extracts to EOF (omit -to).
        var fileDuration = duration;

        // --- Refuse to clobber existing outputs unless Overwrite (only the SELECTED outputs). ---
        if (!req.Overwrite)
        {
            foreach (var seg in selected)
            {
                if (File.Exists(seg.Planned.OutputPath))
                {
                    throw new SplitException(
                        $"Output '{seg.Planned.OutputPath}' already exists. Pass Overwrite=true to replace existing segments.");
                }
            }
        }

        Directory.CreateDirectory(req.OutputDir);

        // --- Pre-flight: fail early + friendly if the output drive clearly can't hold the result. ---
        // A stream-copy split writes ~the input's bytes back out (segments sum to the source size).
        // If the output drive's free space is knowable and clearly below that, stop now with the
        // DiskFull message rather than letting ffmpeg fail mid-write with exit -28 and a confusing
        // tail. Best-effort: any inability to measure (unknown drive, exception) skips the check.
        EnsureEnoughFreeSpace(req.InputPath, req.OutputDir);

        // --- Extract to a temp dir first, then move each into place (cancel-safe). ---
        var tempDir = Path.Combine(req.OutputDir, ".vsj-split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // T-044: entering the ffmpeg pass. Report the count of parts actually being written.
            var partCount = selected.Count;
            status?.Report(new OperationStatus(
                "Splitting",
                partCount == 1 ? "1 part" : $"{partCount} parts"));

            // T-049 routing: the full contiguous set → the single-pass segment muxer (unchanged fast
            // path). A strict SUBSET → the per-segment -ss/-to copy path, one ffmpeg run per selected
            // part, so ONLY the chosen ranges are written.
            var produced = isFullSet
                ? await ExtractAllViaSegmentMuxer(req, plan, duration, tempDir, progress, partProgress, ct).ConfigureAwait(false)
                : await ExtractSelectedPerSegment(req, selected, plan.Segments.Count, fileDuration, tempDir, progress, partProgress, ct).ConfigureAwait(false);

            // T-044: ffmpeg finished — entering the finalize phase (temp→move + verify each segment).
            status?.Report(new OperationStatus("Finalizing"));

            var moved = MoveTempSegmentsIntoPlace(tempDir, produced, ct);

            progress?.Report(1.0);
            status?.Report(new OperationStatus("Done", null, 1.0));
            return new SplitResult(moved, plan.Warnings);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// One contiguous part chosen for extraction (T-049): the planned segment plus its ORIGINAL
    /// 1-based index in the full plan and whether it is the plan's LAST part (extracts to EOF).
    /// Keeping the original index means a selected middle part is still named <c>…_part02</c>.
    /// </summary>
    private readonly record struct SelectedSegment(PlannedSegment Planned, int OneBasedIndex, bool IsFinalPart);

    /// <summary>
    /// Resolve the caller's <see cref="SplitRequest.SelectedSegmentIndices"/> against the computed
    /// plan (T-049). Returns the ordered list of selected parts (each keeping its ORIGINAL 1-based
    /// index / output path) and a flag telling whether that set is the full contiguous plan (which
    /// stays on the fast segment-muxer path). A null selection = all segments. A non-null selection is
    /// deduped and clamped to the planned range; an empty (non-null) selection — or one that clamps to
    /// nothing — is rejected.
    /// </summary>
    private static (IReadOnlyList<SelectedSegment> Selected, bool IsFullSet) ResolveSelectedSegments(
        SplitRequest req,
        SplitPlan plan)
    {
        var n = plan.Segments.Count;

        IReadOnlyList<int> wantedIndices;
        if (req.SelectedSegmentIndices is null)
        {
            // null = all parts, 1..N.
            wantedIndices = Enumerable.Range(1, n).ToList();
        }
        else if (req.SelectedSegmentIndices.Count == 0)
        {
            throw new SplitException(
                "No segments selected — select at least one part to export, or pass a null selection to export all.");
        }
        else
        {
            wantedIndices = req.SelectedSegmentIndices;
        }

        // Keep the distinct, in-range indices in plan order (1-based), building each SelectedSegment.
        var wanted = new HashSet<int>(wantedIndices);
        var selected = new List<SelectedSegment>(n);
        for (var i = 0; i < n; i++)
        {
            var oneBased = i + 1;
            if (wanted.Contains(oneBased))
            {
                selected.Add(new SelectedSegment(plan.Segments[i], oneBased, IsFinalPart: oneBased == n));
            }
        }

        if (selected.Count == 0)
        {
            throw new SplitException(
                "None of the selected segment indices fall within the planned parts — nothing to write.");
        }

        var isFullSet = selected.Count == n;
        return (selected, isFullSet);
    }

    /// <summary>
    /// The full-set fast path (unchanged behaviour): one single-pass segment-muxer command writes
    /// every contiguous part into <paramref name="tempDir"/> as part000, part001, … which are then
    /// mapped in plan order onto the planned destinations.
    /// </summary>
    private async Task<IReadOnlyList<(PlannedSegment Planned, string TempFile)>> ExtractAllViaSegmentMuxer(
        SplitRequest req,
        SplitPlan plan,
        TimeSpan duration,
        string tempDir,
        IProgress<double>? progress,
        IProgress<PartProgress>? partProgress,
        CancellationToken ct)
    {
        var ext = GetOutputExtension(req);
        var tempPattern = Path.Combine(tempDir, "part%03d" + ext);

        var args = SplitArgsBuilder.SegmentMuxer(req.InputPath, plan.InteriorSnappedCuts, tempPattern);
        AssertCopyInvariant(args);

        // T-069: the muxer stays a SINGLE ffmpeg pass. The runner reports one monotonic overall
        // fraction (elapsed/duration); DERIVE the per-part sample from it — no extra ffmpeg runs.
        // Wrap the overall reporter so each fraction both drives the overall bar (forwarded verbatim)
        // AND is mapped through the pure PartAt(time, boundaries, duration) function to a PartProgress.
        var partCount = plan.Segments.Count;
        var boundaries = plan.InteriorSnappedCuts;
        IProgress<double>? runnerProgress = progress is null && partProgress is null
            ? null
            : new SyncProgress<double>(fraction =>
            {
                progress?.Report(fraction);
                if (partProgress is not null)
                {
                    var time = TimeSpan.FromSeconds(fraction * duration.TotalSeconds);
                    var (partIndex, partFraction) = PartMapping.PartAt(time, boundaries, duration);
                    partProgress.Report(new PartProgress(partIndex, partCount, partFraction));
                }
            });

        var result = await _runner.RunAsync(args, duration, runnerProgress, ct).ConfigureAwait(false);
        ThrowIfFailed(result, args);

        // Ensure every written part lands as Done even if ffmpeg's last time= sample stopped short of
        // the final boundary (the overall bar is set to 1.0 by the caller after the move phase).
        partProgress?.Report(new PartProgress(partCount, partCount, 1.0));

        var produced = new List<(PlannedSegment, string)>(plan.Segments.Count);
        for (var i = 0; i < plan.Segments.Count; i++)
        {
            produced.Add((plan.Segments[i], Path.Combine(tempDir, $"part{i:000}{ext}")));
        }

        return produced;
    }

    /// <summary>
    /// The subset path (T-049): run the per-segment <c>-ss/-to -c copy</c> command once per SELECTED
    /// part, writing ONLY those ranges. Each temp file is named by the part's ORIGINAL 1-based index
    /// (e.g. <c>part002.mp4</c> for a selected middle part) so the identity is preserved through the
    /// move onto the planned destination. The plan's LAST part omits <c>-to</c> and runs to end of
    /// file; interior parts pass an explicit <c>-to == SnappedEnd</c>. Progress is reported coarsely
    /// across the selected parts. The copy invariant is asserted on every command.
    /// </summary>
    private async Task<IReadOnlyList<(PlannedSegment Planned, string TempFile)>> ExtractSelectedPerSegment(
        SplitRequest req,
        IReadOnlyList<SelectedSegment> selected,
        int planPartCount,
        TimeSpan fileDuration,
        string tempDir,
        IProgress<double>? progress,
        IProgress<PartProgress>? partProgress,
        CancellationToken ct)
    {
        var ext = GetOutputExtension(req);
        var produced = new List<(PlannedSegment, string)>(selected.Count);

        for (var i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var sel = selected[i];
            var planned = sel.Planned;
            var tempFile = Path.Combine(tempDir, $"part{sel.OneBasedIndex:000}{ext}");

            // The plan's final part runs to EOF → omit -to (matches the muxer's last segment). Interior
            // parts pass their explicit snapped end. Either way the boundaries are keyframe-snapped so
            // the -c copy is clean.
            TimeSpan? end = sel.IsFinalPart ? null : planned.SnappedEnd;
            var reportedDuration = (end ?? fileDuration) - planned.SnappedStart;

            var args = SplitArgsBuilder.PerSegment(req.InputPath, planned.SnappedStart, end, tempFile);
            AssertCopyInvariant(args);

            // T-069: per-part progress is NATURAL here — one ffmpeg run == one part. Wrap this run's
            // fraction so it drives BOTH the overall bar (mapped across all selected parts: base + local
            // share) AND the per-part channel (this part's ORIGINAL 1-based index + the run's own local
            // fraction). The overall base advances one selected slot per completed part.
            var completedSlots = i;
            var totalSlots = selected.Count;
            var oneBasedIndex = sel.OneBasedIndex;
            IProgress<double>? runProgress = progress is null && partProgress is null
                ? null
                : new SyncProgress<double>(local =>
                {
                    progress?.Report((completedSlots + local) / totalSlots);
                    partProgress?.Report(new PartProgress(oneBasedIndex, planPartCount, local));
                });

            var result = await _runner.RunAsync(args, reportedDuration, runProgress, ct).ConfigureAwait(false);
            ThrowIfFailed(result, args);

            // This part is fully written → mark it Done, and advance the overall bar to the slot mark.
            partProgress?.Report(new PartProgress(oneBasedIndex, planPartCount, 1.0));
            progress?.Report((double)(i + 1) / selected.Count);
            produced.Add((planned, tempFile));
        }

        return produced;
    }

    /// <summary>Assert the stream-copy invariant at runtime (not just in tests) before launching ffmpeg.</summary>
    private static void AssertCopyInvariant(FfmpegArgs args)
    {
        if (!SplitArgsBuilder.SatisfiesCopyInvariant(args.ToList()))
        {
            throw new SplitException(
                "Internal error: built ffmpeg command violates the stream-copy invariant (would re-encode). Refusing to run.");
        }
    }

    /// <summary>
    /// Turn a non-zero ffmpeg result into the mapped, friendly <see cref="SplitException"/> (persisting
    /// the full stderr to a per-run log). No-op on success. Shared by both extraction paths.
    /// </summary>
    private void ThrowIfFailed(FfmpegResult result, FfmpegArgs args)
    {
        if (result.Success)
        {
            return;
        }

        // Classify via the signature+exit-code mapper so the CAUSE is the headline — a disk-full write
        // (exit -28 / ENOSPC) is reported as such even when the stderr tail only carries a benign
        // warning that would otherwise be surfaced as the (misleading) failure text.
        var mapped = FfmpegErrorMapper.Map(result);
        var fullStdErr = result.StdErrText;

        // Persist the FULL stderr (+ command + exit code + timestamp) to a per-run log. Best-effort.
        var command = "ffmpeg " + string.Join(" ", args.ToList());
        var logPath = _logWriter.TryWrite("split", command, result.ExitCode, fullStdErr);

        throw new SplitException(
            $"{mapped.Message} (ffmpeg exit {result.ExitCode}).{Environment.NewLine}{fullStdErr}",
            logPath,
            fullStdErr);
    }

    private static void ValidateRequestShape(SplitRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.InputPath))
        {
            throw new SplitException("Input path is empty.");
        }

        if (!File.Exists(req.InputPath))
        {
            throw new SplitException($"Input file does not exist: '{req.InputPath}'.");
        }

        if (string.IsNullOrWhiteSpace(req.OutputDir))
        {
            throw new SplitException("Output directory is empty.");
        }

        if (req.CutPoints is null || req.CutPoints.Count == 0)
        {
            throw new SplitException("No cut points supplied — nothing to split.");
        }

        // Probe writability early so we fail before probing/running ffmpeg.
        try
        {
            Directory.CreateDirectory(req.OutputDir);
            var probeFile = Path.Combine(req.OutputDir, ".vsj-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probeFile, string.Empty);
            File.Delete(probeFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SplitException($"Output directory '{req.OutputDir}' is not writable: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Move the extracted temp outputs onto their planned, user-named destinations (only the parts
    /// that were actually produced — the SELECTED ones, T-049). Doing the move AFTER ffmpeg succeeds
    /// means a cancel mid-run leaves only temp files (cleaned in finally), never a half-written FINAL
    /// segment. The returned <see cref="SplitResult.Segments"/> lists only the written parts.
    /// </summary>
    private static IReadOnlyList<SplitSegment> MoveTempSegmentsIntoPlace(
        string tempDir,
        IReadOnlyList<(PlannedSegment Planned, string TempFile)> produced,
        CancellationToken ct)
    {
        var moved = new List<SplitSegment>(produced.Count);

        foreach (var (planned, tempFile) in produced)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(tempFile))
            {
                throw new SplitException(
                    $"Expected segment '{tempFile}' was not produced by ffmpeg (got fewer segments than planned).");
            }

            var dest = planned.OutputPath;

            if (File.Exists(dest))
            {
                File.Delete(dest); // Overwrite already permission-checked upstream.
            }

            File.Move(tempFile, dest);

            moved.Add(new SplitSegment(
                Path: dest,
                Start: planned.RequestedStart,
                End: planned.RequestedEnd,
                ActualStart: planned.SnappedStart,
                Delta: planned.StartDelta));
        }

        return moved.AsReadOnly();
    }

    /// <summary>
    /// Best-effort disk-space pre-flight. A stream-copy split reproduces roughly the input's byte
    /// count across its output segments, so if the output drive's available free space is knowably
    /// below the input size (plus a small margin), fail early with the friendly DiskFull message
    /// instead of letting ffmpeg hit ENOSPC (exit -28) mid-write. Any inability to measure —
    /// unknown/unc drive, permission, or a thrown <see cref="DriveInfo"/> query — silently skips
    /// the check (never a false-positive block).
    /// </summary>
    private static void EnsureEnoughFreeSpace(string inputPath, string outputDir)
    {
        try
        {
            var inputSize = new FileInfo(inputPath).Length;
            if (inputSize <= 0)
            {
                return;
            }

            var root = Path.GetPathRoot(Path.GetFullPath(outputDir));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return;
            }

            // Require the input size plus a small fixed margin (segment/container overhead).
            var required = inputSize + (16L * 1024 * 1024);
            if (drive.AvailableFreeSpace < required)
            {
                throw new SplitException(
                    "Not enough space to write the output — free up space or choose another output folder. " +
                    $"(need ~{inputSize / (1024 * 1024)} MB, {drive.AvailableFreeSpace / (1024 * 1024)} MB free on '{root}')");
            }
        }
        catch (SplitException)
        {
            throw; // Our own friendly block — propagate.
        }
        catch
        {
            // Any measurement failure (unknown drive, UNC path, security) → skip the pre-flight.
        }
    }

    private static string ResolveOutputPath(SplitRequest req, int oneBasedIndex)
    {
        var name = Path.GetFileNameWithoutExtension(req.InputPath);
        var ext = GetOutputExtension(req);
        var fileName = ApplyNamingPattern(req.NamingPattern, name, ext, oneBasedIndex);
        return Path.GetFullPath(Path.Combine(req.OutputDir, fileName));
    }

    private static string GetOutputExtension(SplitRequest req)
    {
        var ext = Path.GetExtension(req.InputPath);
        return string.IsNullOrEmpty(ext) ? ".mp4" : ext;
    }

    /// <summary>
    /// Render a segment filename from the pattern. Supports <c>{name}</c>, <c>{ext}</c>,
    /// <c>{index}</c> and a zero-padded form <c>{index:00}</c> / <c>{index:000}</c>.
    /// </summary>
    internal static string ApplyNamingPattern(string pattern, string name, string ext, int index)
    {
        var effective = string.IsNullOrWhiteSpace(pattern) ? SplitRequest.DefaultNamingPattern : pattern;

        var result = effective
            .Replace("{name}", name, StringComparison.Ordinal)
            .Replace("{ext}", ext, StringComparison.Ordinal);

        // Zero-padded index: {index:00} → pad count == number of zeros.
        var open = result.IndexOf("{index:", StringComparison.Ordinal);
        while (open >= 0)
        {
            var close = result.IndexOf('}', open);
            if (close < 0)
            {
                break;
            }

            var spec = result.Substring(open + "{index:".Length, close - (open + "{index:".Length));
            var width = spec.Length; // e.g. "00" → width 2
            var rendered = index.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
            result = result.Remove(open, close - open + 1).Insert(open, rendered);
            open = result.IndexOf("{index:", StringComparison.Ordinal);
        }

        // Plain index.
        result = result.Replace("{index}", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return result;
    }

    /// <summary>
    /// A minimal SYNCHRONOUS <see cref="IProgress{T}"/> — invokes the handler inline on the reporting
    /// thread (the ffmpeg stderr reader), unlike <see cref="Progress{T}"/> which posts to a captured
    /// synchronization context. Core has no UI context to marshal to, and the muxer per-part derivation
    /// (T-069) must run in-order on the same thread that parsed each ffmpeg <c>time=</c> line, so a
    /// straight synchronous relay is what we want here (the outer UI reporter the App passes is itself a
    /// context-marshalling <c>Progress&lt;T&gt;</c>, so UI-thread affinity is still honoured downstream).
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup; a locked/racing temp dir is not a caller-facing failure.
        }
    }
}
