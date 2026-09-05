using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoSplitJoiner.App;

/// <summary>
/// Pure, WPF-free helper for filtering dropped file paths down to known video files.
/// Shared by the Split and Join drag-drop handlers (T-016). No WPF dependency so it is
/// directly unit-testable.
/// </summary>
public static class VideoFileFilter
{
    /// <summary>Known video extensions (leading dot, lower-case). Match is case-insensitive.</summary>
    /// <remarks>
    /// T-154 — this list is the most common reason a drop is refused, so it errs toward accepting.
    /// It shipped with 11 entries and no <c>.m2ts</c>/<c>.mts</c> (AVCHD — every consumer camcorder and
    /// most Blu-ray rips) or <c>.3gp</c> (phone video); dragging one of those did nothing and said
    /// nothing, which is indistinguishable from a broken drop target and is how this was reported.
    /// It is still an allowlist rather than "accept anything" so a document handed to ffmpeg fails at the
    /// door rather than three steps later — see T-154 § Design 4 for the probe-based alternative.
    /// </remarks>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Mainstream containers
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv",
        // MPEG transport / program streams — broadcast, camcorders, DVD, Blu-ray
        ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".mpe", ".m2v", ".m1v", ".vob",
        // Mobile
        ".3gp", ".3g2",
        // Others ffmpeg handles routinely
        ".ogv", ".asf", ".divx", ".f4v", ".mxf", ".rm", ".rmvb",
    };

    /// <summary>
    /// The <see cref="Microsoft.Win32.OpenFileDialog"/> filter string, BUILT from the same set the drop
    /// path accepts (T-158).
    ///
    /// <para>All three pickers used to carry a hand-typed seven-extension list while this set had grown
    /// to 26. So an <c>.m2ts</c> — the very format whose absence produced the original report — was
    /// invisible under "Video files" even though DROPPING one worked. The two doors into the same screen
    /// disagreed, and the one a frustrated user falls back to was the stale one.</para>
    ///
    /// <para>Derived rather than duplicated, so the two can never drift again: adding an extension above
    /// changes what the picker offers, with nothing else to remember.</para>
    /// </summary>
    public static string DialogFilter =>
        "Video files|" + string.Join(";", VideoExtensions.OrderBy(e => e, StringComparer.Ordinal).Select(e => "*" + e))
        + "|All files|*.*";

    /// <summary>
    /// Keep only paths whose extension is a known video type (case-insensitive), drop everything
    /// else, dedupe (case-insensitive on the full path), preserve first-seen order.
    /// Null/empty input yields an empty list.
    /// </summary>
    public static IReadOnlyList<string> AcceptVideoFiles(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext) || !VideoExtensions.Contains(ext))
            {
                continue;
            }

            if (seen.Add(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    /// <summary>
    /// True if at least one path is a known video file. Used by the DragOver accept check
    /// to decide whether to show the copy effect.
    /// </summary>
    public static bool HasAnyVideo(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return false;
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext) && VideoExtensions.Contains(ext))
            {
                return true;
            }
        }

        return false;
    }
}
