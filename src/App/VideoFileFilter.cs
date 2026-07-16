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
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".m4v", ".webm", ".ts", ".mpg", ".mpeg", ".wmv", ".flv",
    };

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
