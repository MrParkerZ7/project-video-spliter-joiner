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
    /// </summary>
    Task<JoinResult> JoinAsync(JoinRequest req, IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <inheritdoc cref="IJoinEngine" />
public sealed class JoinEngine : IJoinEngine
{
    private readonly IFfmpegRunner _runner;
    private readonly IMediaProbe _probe;

    /// <summary>Create the engine over the T-002 runner and T-003 probe.</summary>
    public JoinEngine(IFfmpegRunner runner, IMediaProbe probe)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
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
        CancellationToken ct = default)
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

            var result = await _runner.RunAsync(args, totalDuration, progress, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                TryDeleteFile(tempOut);
                return JoinResult.Refused(CompatReport.Incompatible(new[]
                {
                    new Mismatch(
                        "ffmpeg",
                        $"ffmpeg concat failed (exit {result.ExitCode}). Last output:{Environment.NewLine}{result.StdErrText}"),
                }));
            }

            // Move the temp output into place (overwrite already permission-checked upstream).
            if (File.Exists(outFull))
            {
                File.Delete(outFull);
            }

            File.Move(tempOut, outFull);

            progress?.Report(1.0);
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
