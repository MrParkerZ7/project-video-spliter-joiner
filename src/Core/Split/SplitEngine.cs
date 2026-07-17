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
    /// </summary>
    Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default);
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
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        ValidateRequestShape(req);

        // --- Probe the input for duration + keyframes (via T-003, which uses T-002). ---
        var probeResult = await _probe.ProbeAsync(req.InputPath, ct).ConfigureAwait(false);
        if (probeResult is not ProbeResult.ProbeSucceeded ok)
        {
            var reason = probeResult is ProbeResult.ProbeFailed f ? f.Reason : "unknown probe error";
            throw new SplitException($"Cannot split '{req.InputPath}': {reason}");
        }

        var duration = ok.Info.Duration;
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

        // --- Refuse to clobber existing outputs unless Overwrite. ---
        if (!req.Overwrite)
        {
            foreach (var seg in plan.Segments)
            {
                if (File.Exists(seg.OutputPath))
                {
                    throw new SplitException(
                        $"Output '{seg.OutputPath}' already exists. Pass Overwrite=true to replace existing segments.");
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
            var tempPattern = Path.Combine(tempDir, "part%03d" + GetOutputExtension(req));

            var args = SplitArgsBuilder.SegmentMuxer(req.InputPath, plan.InteriorSnappedCuts, tempPattern);

            // Enforce the copy invariant at runtime, not just in tests.
            if (!SplitArgsBuilder.SatisfiesCopyInvariant(args.ToList()))
            {
                throw new SplitException(
                    "Internal error: built ffmpeg command violates the stream-copy invariant (would re-encode). Refusing to run.");
            }

            var result = await _runner.RunAsync(args, duration, progress, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                // Classify via the signature+exit-code mapper so the CAUSE is the headline — a
                // disk-full write (exit -28 / ENOSPC) is reported as such even when the stderr tail
                // only carries a benign mpegts "start time for stream N is not set…" warning, which
                // would otherwise be surfaced as the (misleading) failure text.
                var mapped = FfmpegErrorMapper.Map(result);
                var fullStdErr = result.StdErrText;

                // Persist the FULL stderr (+ command + exit code + timestamp) to a per-run log so the
                // user has the complete output, not just the tail. Best-effort — a write failure
                // returns null and never aborts the (already-failing) op.
                var command = "ffmpeg " + string.Join(" ", args.ToList());
                var logPath = _logWriter.TryWrite("split", command, result.ExitCode, fullStdErr);

                throw new SplitException(
                    $"{mapped.Message} (ffmpeg exit {result.ExitCode}).{Environment.NewLine}{fullStdErr}",
                    logPath,
                    fullStdErr);
            }

            // The segment muxer numbers its outputs 0,1,2,… — map them onto our planned paths.
            var produced = MoveTempSegmentsIntoPlace(tempDir, req, plan, ct);

            progress?.Report(1.0);
            return new SplitResult(produced, plan.Warnings);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
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
    /// Move the segment muxer's numbered temp outputs (part000, part001, …) onto the planned,
    /// user-named destinations. Doing the move AFTER ffmpeg succeeds means a cancel mid-run
    /// leaves only temp files (cleaned in finally), never a half-written FINAL segment.
    /// </summary>
    private static IReadOnlyList<SplitSegment> MoveTempSegmentsIntoPlace(
        string tempDir,
        SplitRequest req,
        SplitPlan plan,
        CancellationToken ct)
    {
        var ext = GetOutputExtension(req);
        var produced = new List<SplitSegment>(plan.Segments.Count);

        for (var i = 0; i < plan.Segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var tempFile = Path.Combine(tempDir, $"part{i:000}{ext}");
            if (!File.Exists(tempFile))
            {
                throw new SplitException(
                    $"Expected segment '{tempFile}' was not produced by ffmpeg (got fewer segments than planned).");
            }

            var planned = plan.Segments[i];
            var dest = planned.OutputPath;

            if (File.Exists(dest))
            {
                File.Delete(dest); // Overwrite already permission-checked upstream.
            }

            File.Move(tempFile, dest);

            produced.Add(new SplitSegment(
                Path: dest,
                Start: planned.RequestedStart,
                End: planned.RequestedEnd,
                ActualStart: planned.SnappedStart,
                Delta: planned.StartDelta));
        }

        return produced.AsReadOnly();
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
