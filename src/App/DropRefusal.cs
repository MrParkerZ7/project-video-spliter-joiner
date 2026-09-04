using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VideoSplitJoiner.App;

/// <summary>
/// Pure, WPF-free accounting for what a drop left behind — the one refusal vocabulary all three
/// screens speak (T-154).
///
/// <para>Bulk Cut learned to explain a refused drop first; Split and Join were left silent, and the
/// remaining criterion on T-154 is literally <i>"same silence"</i>. Mirroring the Bulk implementation
/// by hand into two more view-models would have produced three copies of the pluralisation, three
/// slightly different sentences, and — on the evidence of how the counts in
/// <c>docs/specs/_index.md</c> drifted — three things to keep in step forever. The wording lives here
/// once instead, so a user moving between tabs reads the same sentence for the same event.</para>
///
/// <para>What each screen refuses is genuinely different, which is why this exposes clauses rather
/// than one fixed sentence: Bulk refuses a file already in its list, Join allows duplicates by design
/// but the filter still collapses two copies inside a single drop, and Split — which opens one file
/// at a time — silently ignores every video after the first.</para>
///
/// <para><b>Boundary worth stating.</b> A drag containing <i>no</i> recognised video never reaches any
/// of this: <c>OnDragOver</c> answers <see cref="VideoFileFilter.HasAnyVideo"/> with
/// <c>DragDropEffects.None</c>, Windows shows a no-entry cursor, and the drop event is never
/// delivered. The cursor is that case's feedback. Everything here describes a drop that WAS accepted
/// and still could not take everything in it.</para>
/// </summary>
public static class DropRefusal
{
    /// <summary>One reason some of a drop did not land, with its singular and plural phrasings.</summary>
    /// <param name="Count">How many files this reason accounts for. Zero clauses are dropped.</param>
    /// <param name="One">Phrasing when <paramref name="Count"/> is 1, e.g. "1 is not a video file".</param>
    /// <param name="Many">Phrasing otherwise, e.g. "3 are not video files".</param>
    public readonly record struct Clause(int Count, string One, string Many);

    /// <summary>What a raw drop payload actually contained, before any screen's own rules apply.</summary>
    /// <param name="Videos">Accepted video paths — deduped, first-seen order (<see cref="VideoFileFilter"/>).</param>
    /// <param name="Folders">Dropped folders. Explorer delivers these as ordinary FileDrop paths.</param>
    /// <param name="NotVideo">Files that are neither a folder nor a recognised video container.</param>
    /// <param name="DuplicatesInDrop">Video paths that appeared more than once in the same payload.</param>
    public sealed record Tally(
        IReadOnlyList<string> Videos,
        int Folders,
        int NotVideo,
        int DuplicatesInDrop);

    /// <summary>
    /// Sort one raw drop payload into the categories every screen needs.
    ///
    /// <para>Folders are counted separately rather than lumped in with <paramref name="isFolder"/>-less
    /// junk because calling a folder "not a video file" is simply untrue, and a message that says
    /// something false about what you just did is worse than no message. A folder is the most natural
    /// gesture for a batch video tool and deserves its own sentence.</para>
    /// </summary>
    /// <param name="dropped">The raw paths, exactly as the drop delivered them. Null/blank entries are ignored.</param>
    /// <param name="isFolder">
    /// Directory test — seam so this is testable without touching the disk. Defaults to
    /// <see cref="Directory.Exists"/>. A throwing probe is treated as "not a folder": classification
    /// must never be the reason a drop fails.
    /// </param>
    public static Tally Classify(IReadOnlyList<string>? dropped, Func<string, bool>? isFolder = null)
    {
        if (dropped is null || dropped.Count == 0)
        {
            return new Tally(Array.Empty<string>(), 0, 0, 0);
        }

        isFolder ??= SafeIsFolder;

        // Folders are removed BEFORE the accept filter runs, not counted alongside its output.
        // `VideoFileFilter` tests the extension and never touches the disk, so a directory named
        // `Season.1.1080p.mkv` — scene releases are routinely named with the container suffix — is a
        // "video" to it. Filtering the raw list and counting folders separately put that directory in
        // BOTH buckets: the note said one file was not added while a row for it appeared underneath,
        // and on Split it became the file Split tried to load. The four counts are a partition of the
        // payload, and callers rely on that.
        var files = new List<string>(dropped.Count);
        var folders = 0;

        foreach (var path in dropped)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Safely(isFolder, path))
            {
                folders++;
            }
            else
            {
                files.Add(path);
            }
        }

        var videos = VideoFileFilter.AcceptVideoFiles(files);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int notVideo = 0, duplicates = 0;

        foreach (var path in files)
        {
            if (!VideoFileFilter.HasAnyVideo(new[] { path }))
            {
                notVideo++;
                continue;
            }

            // Second and later sightings of the same path inside ONE payload: the filter collapses
            // them, so without counting here they vanish with no trace.
            if (!seen.Add(path))
            {
                duplicates++;
            }
        }

        return new Tally(videos, folders, notVideo, duplicates);
    }

    /// <summary>
    /// One line accounting for a drop, or <c>null</c> when it left nothing behind.
    ///
    /// <para>Null on a clean drop is deliberate and load-bearing: a message on every drop is noise, and
    /// noise is what teaches people to ignore the one that matters.</para>
    /// </summary>
    /// <param name="pastVerb">What did not happen to them — "added" on Bulk/Join, "loaded" on Split.</param>
    /// <param name="tail">
    /// Optional sentence explaining a screen-specific rule (Split's one-file-at-a-time). Appended after
    /// the clause list; ignored when nothing was refused.
    /// </param>
    /// <param name="clauses">The reasons. Zero-count clauses are skipped; order is preserved.</param>
    public static string? Describe(string pastVerb, string? tail, params Clause[] clauses)
    {
        if (clauses is null || clauses.Length == 0)
        {
            return null;
        }

        var parts = new List<string>(clauses.Length);
        var total = 0;

        foreach (var clause in clauses)
        {
            if (clause.Count <= 0)
            {
                continue;
            }

            total += clause.Count;
            parts.Add(clause.Count == 1 ? clause.One : clause.Many);
        }

        if (total == 0)
        {
            return null;
        }

        var sentence = new StringBuilder()
            .Append(total)
            .Append(total == 1 ? " file was not " : " files were not ")
            .Append(pastVerb)
            .Append(": ")
            .Append(string.Join(", ", parts));

        if (!string.IsNullOrWhiteSpace(tail))
        {
            sentence.Append(" — ").Append(tail);
        }

        return sentence.ToString();
    }

    /// <summary>"2 are not video files" / "1 is not a video file".</summary>
    public static Clause NotVideo(int count) =>
        new(count, "1 is not a video file", $"{count} are not video files");

    /// <summary>Folders get their own sentence — see <see cref="Classify"/> for why.</summary>
    public static Clause Folders(int count) =>
        new(count,
            "1 is a folder (drop the files inside it)",
            $"{count} are folders (drop the files inside them)");

    /// <summary>Bulk Cut only — it keeps one row per source, so a re-drop is a genuine refusal.</summary>
    public static Clause AlreadyInList(int count) =>
        new(count, "1 is already in the list", $"{count} are already in the list");

    /// <summary>
    /// The same path twice inside one payload. Join otherwise permits the same clip twice, so this
    /// collapse is invisible AND inconsistent with the screen's own rule unless it is named.
    /// </summary>
    public static Clause DroppedTwice(int count) =>
        new(count, "1 was dropped twice", $"{count} were dropped twice");

    /// <summary>Split only — it opens one file at a time, so every video after the first is skipped.</summary>
    public static Clause OtherVideosSkipped(int count) =>
        new(count, "1 other video was skipped", $"{count} other videos were skipped");

    private static bool SafeIsFolder(string path) => Directory.Exists(path);

    private static bool Safely(Func<string, bool> isFolder, string path)
    {
        try
        {
            return isFolder(path);
        }
        catch
        {
            // A too-long / reserved / malformed path throws here. Treat it as "not a folder" and let it
            // fall through to the extension test — a diagnostic must never be why a drop fails.
            return false;
        }
    }
}
