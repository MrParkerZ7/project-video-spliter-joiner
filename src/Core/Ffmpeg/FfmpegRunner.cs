using System.Diagnostics;

namespace VideoSplitJoiner.Core.Ffmpeg;

/// <summary>Runs ffmpeg conversions through a single choke-point.</summary>
public interface IFfmpegRunner
{
    /// <summary>
    /// Run ffmpeg with the given args. Reports progress (0..1) if a total duration is
    /// supplied and ffmpeg emits <c>time=</c> markers. Returns a <see cref="FfmpegResult"/> for
    /// ANY exit code (non-zero is not an exception). Throws <see cref="FfmpegNotFoundException"/>
    /// if the binary cannot be located, and <see cref="OperationCanceledException"/> if cancelled.
    /// </summary>
    Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Default ffmpeg runner. Launches ffmpeg with <c>UseShellExecute=false</c>, redirected
/// std streams, and no window; closes stdin; streams stderr line-by-line into a rolling
/// tail buffer and a progress parser. On cancellation, kills the entire process tree and
/// throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class FfmpegRunner : IFfmpegRunner
{
    // Large enough to retain the FULL stderr of a detection pass (blackdetect/scdet/
    // metadata=print emit one-or-more lines per event; a busy real video can produce
    // thousands). Still bounded so a pathological stream cannot grow memory without limit.
    // Split/convert runs only ever need the last few lines, so the extra capacity is unused there.
    private const int TailSize = 100_000;

    private readonly IFfmpegBinaryLocator _locator;

    public FfmpegRunner(IFfmpegBinaryLocator locator)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <inheritdoc />
    public async Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var exe = _locator.ResolveFfmpeg();
        var parser = new FfmpegProgress(totalDuration);
        var tail = new RollingTail(TailSize);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args.ToList())
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Close stdin so ffmpeg never blocks reading it.
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // stdin may already be closed; ignore.
        }

        var stdErrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                tail.Add(line);
                var fraction = parser.Feed(line);
                if (fraction is { } f)
                {
                    progress?.Report(f);
                }
            }
        });

        // Drain stdout so a full pipe never deadlocks the child.
        var stdOutTask = process.StandardOutput.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        await stdErrTask.ConfigureAwait(false);
        await stdOutTask.ConfigureAwait(false);

        return new FfmpegResult(process.ExitCode, tail.Snapshot());
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process already gone / access race; nothing to do.
        }
    }
}
