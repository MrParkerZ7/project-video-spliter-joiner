using System.Diagnostics;
using System.Text;

namespace VideoSplitJoiner.Core.Ffmpeg;

/// <summary>Runs ffprobe queries and returns their stdout (typically JSON).</summary>
public interface IFfprobeRunner
{
    /// <summary>
    /// Run ffprobe with the given args and return its stdout. Throws
    /// <see cref="FfprobeException"/> on a non-zero exit (a probe failure is exceptional),
    /// <see cref="FfmpegNotFoundException"/> if the binary cannot be located, and
    /// <see cref="OperationCanceledException"/> if cancelled.
    /// </summary>
    Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default);
}

/// <summary>
/// Default ffprobe runner. Same process discipline as <see cref="FfmpegRunner"/> but
/// captures stdout (the JSON payload) and treats a non-zero exit as an exception.
/// </summary>
public sealed class FfprobeRunner : IFfprobeRunner
{
    private const int TailSize = 40;

    private readonly IFfmpegBinaryLocator _locator;

    public FfprobeRunner(IFfmpegBinaryLocator locator)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <inheritdoc />
    public async Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var exe = _locator.ResolveFfprobe();
        var tail = new RollingTail(TailSize);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            // ffprobe emits UTF-8 (its JSON payload on stdout, diagnostics on stderr) regardless
            // of the Windows console codepage. Decode both as UTF-8 so unicode paths in the JSON
            // and in error output survive intact instead of becoming mojibake (T-036).
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args.ToList())
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

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
            }
        });

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
        var stdout = await stdOutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new FfprobeException(process.ExitCode, tail.Snapshot());
        }

        return stdout;
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
