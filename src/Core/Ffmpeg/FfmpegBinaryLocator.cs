namespace VideoSplitJoiner.Core.Ffmpeg;

/// <summary>Resolves the absolute (or PATH-resolvable) ffmpeg/ffprobe executable path.</summary>
public interface IFfmpegBinaryLocator
{
    /// <summary>Resolve the ffmpeg executable, or throw <see cref="FfmpegNotFoundException"/>.</summary>
    string ResolveFfmpeg();

    /// <summary>Resolve the ffprobe executable, or throw <see cref="FfmpegNotFoundException"/>.</summary>
    string ResolveFfprobe();
}

/// <summary>
/// Bundle-agnostic locator. Resolution order per tool:
/// (a) explicit override path (constructor), (b) an app-local <c>ffmpeg/</c> folder next to
/// the running assembly (<see cref="AppContext.BaseDirectory"/>), (c) bare name on <c>PATH</c>
/// (let the OS resolve). If nothing resolves to an existing binary, throws
/// <see cref="FfmpegNotFoundException"/>.
/// </summary>
public sealed class FfmpegBinaryLocator : IFfmpegBinaryLocator
{
    private readonly string? _ffmpegOverride;
    private readonly string? _ffprobeOverride;

    /// <summary>
    /// Create a locator with optional explicit override paths.
    /// </summary>
    /// <param name="ffmpegOverride">Explicit ffmpeg path, or null to auto-discover.</param>
    /// <param name="ffprobeOverride">Explicit ffprobe path, or null to auto-discover.</param>
    public FfmpegBinaryLocator(string? ffmpegOverride = null, string? ffprobeOverride = null)
    {
        _ffmpegOverride = string.IsNullOrWhiteSpace(ffmpegOverride) ? null : ffmpegOverride;
        _ffprobeOverride = string.IsNullOrWhiteSpace(ffprobeOverride) ? null : ffprobeOverride;
    }

    /// <inheritdoc />
    public string ResolveFfmpeg() => Resolve("ffmpeg", _ffmpegOverride);

    /// <inheritdoc />
    public string ResolveFfprobe() => Resolve("ffprobe", _ffprobeOverride);

    private static string Resolve(string toolName, string? overridePath)
    {
        var exeName = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;

        // (a) explicit override
        if (overridePath is not null)
        {
            if (File.Exists(overridePath))
            {
                return overridePath;
            }

            throw new FfmpegNotFoundException(
                $"Override path for '{toolName}' does not exist: '{overridePath}'.");
        }

        // (b) app-local ffmpeg/ folder next to the running assembly
        var appLocal = Path.Combine(AppContext.BaseDirectory, "ffmpeg", exeName);
        if (File.Exists(appLocal))
        {
            return appLocal;
        }

        // (c) PATH — return the bare name and let the OS resolve, but only if it is
        // actually discoverable on PATH so we can throw a helpful message otherwise.
        var onPath = FindOnPath(exeName);
        if (onPath is not null)
        {
            return toolName; // bare name; OS resolves at process start
        }

        throw new FfmpegNotFoundException(
            $"Could not locate '{toolName}'. Tried: explicit override (none), " +
            $"app-local folder ('{appLocal}'), and PATH. " +
            $"Provide an override path, place '{exeName}' in an 'ffmpeg' folder next to the app, " +
            $"or install {toolName} and add it to PATH.");
    }

    private static string? FindOnPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return null;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
