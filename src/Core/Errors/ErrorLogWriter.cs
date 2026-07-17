using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace VideoSplitJoiner.Core.Errors;

/// <summary>
/// Writes the FULL diagnostic text of a failed ffmpeg operation to a per-run log file so the user
/// always has the complete output — not just the tail — and can attach it to a bug report. The file
/// lands under <c>%LOCALAPPDATA%/VideoSplitJoiner/logs/&lt;op&gt;-&lt;yyyyMMdd-HHmmss&gt;.log</c> by
/// default; the base directory is injectable so the writer is unit-testable against a temp dir.
/// <para>
/// Writing is <b>best-effort</b>: any failure (unwritable dir, locked file, security) is swallowed
/// and <see cref="TryWrite"/> returns <c>null</c> — a logging problem must never crash the operation
/// the user actually asked for.
/// </para>
/// </summary>
public sealed class ErrorLogWriter
{
    private readonly string _logDirectory;

    /// <summary>The folder name created under the app-data root.</summary>
    public const string AppFolderName = "VideoSplitJoiner";

    /// <summary>Create a writer targeting the default per-user log directory.</summary>
    public ErrorLogWriter()
        : this(DefaultLogDirectory())
    {
    }

    /// <summary>Create a writer targeting an explicit log directory (used by tests).</summary>
    public ErrorLogWriter(string logDirectory)
    {
        _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
    }

    /// <summary>The resolved log directory this writer targets.</summary>
    public string LogDirectory => _logDirectory;

    /// <summary>
    /// The default per-user log directory:
    /// <c>%LOCALAPPDATA%/VideoSplitJoiner/logs</c>. Falls back to the OS temp folder when the
    /// local-app-data path cannot be resolved (rare — headless / restricted environments).
    /// </summary>
    public static string DefaultLogDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, AppFolderName, "logs");
    }

    /// <summary>
    /// Render the full log <b>body</b> (deterministic, no file I/O) for a failed op. Exposed so the
    /// same text can be surfaced in the UI as the copyable "full text" AND written to disk, and so the
    /// format is unit-testable without touching the filesystem. Includes a UTC timestamp, the exact
    /// command, the exit code, and the complete stderr.
    /// </summary>
    public static string BuildLogBody(string operation, string command, int exitCode, string fullStdErr)
    {
        var sb = new StringBuilder();
        sb.Append("VideoSplitJoiner — ").Append(operation).Append(" failed").AppendLine();
        sb.Append("Timestamp : ")
            .Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .AppendLine();
        sb.Append("Exit code : ").Append(exitCode.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("Command   : ").Append(command ?? string.Empty).AppendLine();
        sb.AppendLine();
        sb.AppendLine("---- ffmpeg stderr (full) ----");
        sb.Append(fullStdErr ?? string.Empty);
        return sb.ToString();
    }

    /// <summary>
    /// Write the full log for a failed op and return the written file path, or <c>null</c> if writing
    /// failed for any reason (best-effort — never throws). Creates the log directory on demand.
    /// </summary>
    /// <param name="operation">Short op label used in the file name (e.g. <c>split</c>, <c>join</c>).</param>
    /// <param name="command">The exact ffmpeg command line that was run.</param>
    /// <param name="exitCode">ffmpeg's exit code.</param>
    /// <param name="fullStdErr">The complete stderr captured for the run.</param>
    public string? TryWrite(string operation, string command, int exitCode, string fullStdErr)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);

            var safeOp = SanitizeOp(operation);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var fileName = $"{safeOp}-{stamp}.log";
            var path = Path.Combine(_logDirectory, fileName);

            // Avoid clobbering a same-second sibling (two failures within one second).
            if (File.Exists(path))
            {
                fileName = $"{safeOp}-{stamp}-{Guid.NewGuid():N}.log";
                path = Path.Combine(_logDirectory, fileName);
            }

            var body = BuildLogBody(operation, command, exitCode, fullStdErr);
            File.WriteAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch
        {
            // Best-effort: a logging failure must never crash the operation.
            return null;
        }
    }

    private static string SanitizeOp(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            return "op";
        }

        var chars = operation.Trim().ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
            {
                chars[i] = '-';
            }
        }

        return new string(chars);
    }
}
