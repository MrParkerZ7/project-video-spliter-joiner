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

    /// <summary>Full path of the trace file. Stable so it can be quoted in a bug report.</summary>
    public static string LogPath =>
        Path.Combine(ErrorLogWriter.DefaultLogDirectory(), "dragdrop.log");

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
                var dir = ErrorLogWriter.DefaultLogDirectory();
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
