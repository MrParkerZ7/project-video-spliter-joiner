using System;
using System.Globalization;
using System.IO;
using System.Linq;
using VideoSplitJoiner.Core.Errors;

namespace VideoSplitJoiner.App;

/// <summary>
/// A one-line-per-event trace of every drag-over and drop the app sees (T-154).
///
/// <para><b>Why this exists.</b> "Drag and drop does not work" was reported for `.mp4`/`.mkv` files on
/// every screen, and diagnosing it consumed a round-trip of guesses — because there was no way to tell
/// the two possibilities apart from the outside:</para>
///
/// <list type="bullet">
/// <item><b>The app never saw the drag.</b> Nothing is logged. Windows did not deliver the event to us —
/// the cause is outside this application (a stuck shell drag state, an integrity-level mismatch between
/// the drag source and this process, a shell extension). Nothing in our code can fix that, and knowing it
/// immediately is worth more than another hypothesis.</item>
/// <item><b>The app saw it and refused.</b> A line appears with the paths and the effect. Then it IS ours,
/// and the line says which check rejected it.</item>
/// </list>
///
/// <para>Writes to <c>%LOCALAPPDATA%/VideoSplitJoiner/logs/dragdrop.log</c>, capped, append-only, and
/// entirely best-effort — a diagnostic that can break the thing it diagnoses is worse than none, so every
/// path swallows its own errors.</para>
/// </summary>
public static class DropDiagnostics
{
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    private static string? _logDirectory;
    private static string? _lastOverKey;

    /// <summary>
    /// Where the trace is written. Defaults to the user's real log folder; a test points it at a temp
    /// directory instead (T-154 follow-up).
    ///
    /// <para><b>This seam is not tidiness.</b> Without it the suite wrote into the very file the
    /// reporter is asked to attach: 332 of the 1526 lines in a real user's log came from
    /// <c>dotnet test</c>. Worse, the 256KB cap evicts the OLDER half, so a local test run could delete
    /// the reporter's actual drop line before they ever opened the file — a diagnostic destroying the
    /// evidence it exists to preserve. <c>ErrorLogWriter</c> has carried the same seam for exactly this
    /// reason since it was written; this one simply never got it.</para>
    /// </summary>
    public static string LogDirectory
    {
        get => _logDirectory ?? ErrorLogWriter.DefaultLogDirectory();
        set => _logDirectory = value;
    }

    /// <summary>Restore the real user log folder (a test calls this on teardown).</summary>
    public static void ResetLogDirectory() => _logDirectory = null;

    /// <summary>Full path of the trace file. Stable so it can be quoted in a bug report.</summary>
    public static string LogPath => Path.Combine(LogDirectory, "dragdrop.log");

    /// <summary>
    /// Record one drag-drop event. <paramref name="stage"/> is <c>"over"</c> or <c>"drop"</c>;
    /// <paramref name="screen"/> names the tab; <paramref name="accepted"/> is what the app decided.
    /// </summary>
    public static void Record(string stage, string screen, string[]? paths, bool accepted, string? note = null)
    {
        try
        {
            var when = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var count = paths?.Length ?? 0;

            // Extensions, not full paths: enough to diagnose an allowlist refusal without writing
            // someone's folder structure into a file they may paste into a public issue.
            var kinds = paths is null || paths.Length == 0
                ? "-"
                : string.Join(
                    " ",
                    paths.Where(p => !string.IsNullOrWhiteSpace(p))
                         .Select(p => { try { return Path.GetExtension(p); } catch { return "?"; } })
                         .Select(x => string.IsNullOrEmpty(x) ? "(none)" : x)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Take(8));

            var line =
                $"{when} {stage,-4} {screen,-8} files={count} kinds=[{kinds}] accepted={accepted}"
                + (string.IsNullOrWhiteSpace(note) ? string.Empty : $" note={note}");

            lock (Gate)
            {
                // COLLAPSE a repeated drag-over. OLE raises DragOver per mouse message, so a two-second
                // hover wrote hundreds of identical lines: a real user's log held 1408 "over" against
                // ~118 "drop" — the signal was 8% of the file, and every one of those lines was
                // File.Exists + FileInfo.Length + AppendAllText on the UI thread DURING the very gesture
                // this trace exists to diagnose.
                //
                // Only consecutive IDENTICAL over-lines are dropped, so a decision CHANGE (the cursor
                // flipping from refuse to accept as the payload is re-evaluated) still records — which is
                // the thing the log is actually asked to show. A "drop" always writes, and clears the
                // key so the next drag starts fresh.
                if (string.Equals(stage, "over", StringComparison.Ordinal))
                {
                    var key = $"{screen}|{count}|{kinds}|{accepted}|{note}";
                    if (string.Equals(_lastOverKey, key, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _lastOverKey = key;
                }
                else
                {
                    _lastOverKey = null;
                }

                var dir = LogDirectory;
                Directory.CreateDirectory(dir);

                var path = LogPath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    // Keep the RECENT half: the interesting event is the one that just happened.
                    var kept = File.ReadAllLines(path);
                    File.WriteAllLines(path, kept.Skip(kept.Length / 2));
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // A diagnostic must never be the reason something fails.
        }
    }
}
