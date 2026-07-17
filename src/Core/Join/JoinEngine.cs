using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Join;

/// <summary>
/// Glues several videos into one via lossless stream-copy concat (<c>-c copy</c>, concat
/// demuxer) — but ONLY when the inputs are truly concat-compatible. A pre-flight
/// compatibility check runs first; on any mismatch the join REFUSES and writes nothing,
/// returning a report that names the offending clip and the conflicting field.
/// </summary>
public interface IJoinEngine
{
    /// <summary>
    /// Probe every input (via the T-003 <see cref="IMediaProbe"/>) and compare them for concat
    /// safety, taking the first as the reference. Compares video (codec, width, height, pix_fmt,
    /// time_base) and audio (codec, sample_rate, channels). A missing file or failed probe on any
    /// input is itself reported as a mismatch — this never throws for a bad input.
    /// </summary>
    Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default);

    /// <summary>
    /// Run the compatibility check, then — only if compatible — stream-copy concat the inputs
    /// into <see cref="JoinRequest.OutputPath"/>. On incompatibility returns a refusal and writes
    /// NOTHING. Reports progress 0..1 against the summed input duration. A single input is
    /// passed through with the same <c>-c copy</c> command. Cancellation removes any partially
    /// written output.
    /// <paramref name="status"/> (optional, T-044) receives a stage transition as the engine enters
    /// each real phase: Checking compatibility → Joining → Finalizing → Done — synced to the actual
    /// work, never a timer. The numeric <paramref name="progress"/> channel is unchanged.
    /// </summary>
    Task<JoinResult> JoinAsync(
        JoinRequest req,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IProgress<OperationStatus>? status = null);
}

/// <inheritdoc cref="IJoinEngine" />
public sealed class JoinEngine : IJoinEngine
{
    private readonly IFfmpegRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly ErrorLogWriter _logWriter;

    /// <summary>Create the engine over the T-002 runner and T-003 probe.</summary>
    public JoinEngine(IFfmpegRunner runner, IMediaProbe probe)
        : this(runner, probe, new ErrorLogWriter())
    {
    }

    /// <summary>
    /// Create the engine with an explicit <see cref="ErrorLogWriter"/> (used by tests to redirect the
    /// full-error log to a temp directory).
    /// </summary>
    public JoinEngine(IFfmpegRunner runner, IMediaProbe probe, ErrorLogWriter logWriter)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _logWriter = logWriter ?? throw new ArgumentNullException(nameof(logWriter));
    }

    /// <inheritdoc />
    public async Task<CompatReport> CheckCompatibilityAsync(
        IReadOnlyList<string> inputPaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);

        if (inputPaths.Count == 0)
        {
            return CompatReport.Incompatible(new[]
            {
                new Mismatch("input_count", "No inputs supplied — nothing to join."),
            });
        }

        // Probe each input in order. A missing file / probe failure is a reported mismatch, not a throw.
        var infos = new List<MediaInfo>(inputPaths.Count);
        var probeFailures = new List<Mismatch>();

        for (var i = 0; i < inputPaths.Count; i++)
        {
            var clip = i + 1;
            var result = await _probe.ProbeAsync(inputPaths[i], ct).ConfigureAwait(false);
            if (result is ProbeResult.ProbeSucceeded ok)
            {
                infos.Add(ok.Info);
            }
            else
            {
                var reason = result is ProbeResult.ProbeFailed f ? f.Reason : "unknown probe error";
                probeFailures.Add(new Mismatch("probe", $"clip {clip} could not be probed: {reason}"));
            }
        }

        // If any input failed to probe we cannot vouch for compatibility — refuse with those reasons.
        if (probeFailures.Count > 0)
        {
            return CompatReport.Incompatible(probeFailures);
        }

        return CompatChecker.Compare(infos);
    }

    /// <inheritdoc />
    public async Task<JoinResult> JoinAsync(
        JoinRequest req,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IProgress<OperationStatus>? status = null)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (req.InputPaths is null || req.InputPaths.Count == 0)
        {
            return JoinResult.Refused(CompatReport.Incompatible(new[]
            {
                new Mismatch("input_count", "No inputs supplied — nothing to join."),
            }));
        }

        if (string.IsNullOrWhiteSpace(req.OutputPath))
        {
            return JoinResult.Refused(CompatReport.Incompatible(new[]
            {
                new Mismatch("output", "Output path is empty."),
            }));
        }

        // T-044: entering the compatibility pre-flight.
        status?.Report(new OperationStatus("Checking compatibility"));

        // --- Pre-flight: refuse (write nothing) on any incompatibility. ---
        var report = await CheckCompatibilityAsync(req.InputPaths, ct).ConfigureAwait(false);
        if (!report.Compatible)
        {
            return JoinResult.Refused(report);
        }

        // Compatible: gather the summed duration for progress (best-effort; probe again cheaply).
        var totalDuration = await SumDurationsAsync(req.InputPaths, ct).ConfigureAwait(false);

        var outFull = Path.GetFullPath(req.OutputPath);

        // --- Refuse to clobber an existing output unless Overwrite. ---
        if (!req.Overwrite && File.Exists(outFull))
        {
            return JoinResult.Refused(CompatReport.Incompatible(new[]
            {
                new Mismatch(
                    "output_exists",
                    $"Output '{outFull}' already exists. Pass Overwrite=true to replace it."),
            }));
        }

        var outDir = Path.GetDirectoryName(outFull);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        // --- Write the concat list file + a temp output, then move into place (cancel-safe). ---
        var listFile = Path.Combine(Path.GetTempPath(), "vsj-join-" + Guid.NewGuid().ToString("N") + ".txt");
        var tempOut = outFull + ".vsj-join-" + Guid.NewGuid().ToString("N") + Path.GetExtension(outFull);

        try
        {
            var listBody = JoinArgsBuilder.RenderConcatList(req.InputPaths);
            await File.WriteAllTextAsync(listFile, listBody, ct).ConfigureAwait(false);

            var args = JoinArgsBuilder.ConcatCopy(listFile, tempOut);

            // Enforce the copy invariant at runtime, not just in tests.
            if (!JoinArgsBuilder.SatisfiesCopyInvariant(args.ToList()))
            {
                return JoinResult.Refused(CompatReport.Incompatible(new[]
                {
                    new Mismatch(
                        "invariant",
                        "Internal error: built ffmpeg command violates the stream-copy invariant (would re-encode). Refusing to run."),
                }));
            }

            // T-044: inputs are compatible + the concat list is written — entering the ffmpeg join.
            status?.Report(new OperationStatus(
                "Joining",
                req.InputPaths.Count == 1 ? "1 clip" : $"{req.InputPaths.Count} clips"));

            var result = await _runner.RunAsync(args, totalDuration, progress, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                TryDeleteFile(tempOut);

                var fullStdErr = result.StdErrText;

                // Persist the FULL stderr (+ command + exit code + timestamp) to a per-run log so the
                // user has the complete output, not just the tail. Best-effort — a write failure
                // returns null and never aborts the (already-failing) op.
                var command = "ffmpeg " + string.Join(" ", args.ToList());
                var logPath = _logWriter.TryWrite("join", command, result.ExitCode, fullStdErr);

                return JoinResult.RefusedWithLog(
                    CompatReport.Incompatible(new[]
                    {
                        new Mismatch(
                            "ffmpeg",
                            $"ffmpeg concat failed (exit {result.ExitCode}). Last output:{Environment.NewLine}{fullStdErr}"),
                    }),
                    logPath,
                    fullStdErr);
            }

            // T-044: ffmpeg finished — entering the finalize phase (temp→move into place).
            status?.Report(new OperationStatus("Finalizing"));

            // Move the temp output into place (overwrite already permission-checked upstream).
            if (File.Exists(outFull))
            {
                File.Delete(outFull);
            }

            File.Move(tempOut, outFull);

            progress?.Report(1.0);
            status?.Report(new OperationStatus("Done", null, 1.0));
            return JoinResult.Ok(outFull);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(tempOut);
            throw;
        }
        finally
        {
            TryDeleteFile(listFile);
        }
    }

    private async Task<TimeSpan?> SumDurationsAsync(IReadOnlyList<string> inputPaths, CancellationToken ct)
    {
        var total = TimeSpan.Zero;
        foreach (var p in inputPaths)
        {
            var result = await _probe.ProbeAsync(p, ct).ConfigureAwait(false);
            if (result is ProbeResult.ProbeSucceeded ok)
            {
                total += ok.Info.Duration;
            }
            else
            {
                // A probe hiccup here only degrades progress reporting, not correctness.
                return null;
            }
        }

        return total;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort temp cleanup; a locked/racing temp file is not a caller-facing failure.
        }
    }
}
