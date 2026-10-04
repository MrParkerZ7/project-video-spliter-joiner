namespace VideoSplitJoiner.Bench;

/// <summary>
/// T-188: the one folder the bench may read from. Fixtures, the manifest, working files and results all live
/// under it (default <c>%TEMP%\vsj-bench-fixtures</c>, moved with <c>--root</c>), and every scenario resolves
/// each input through <see cref="Resolve"/>, which accepts a path only when its full, link-resolved form is
/// inside the root. The check is made on the path alone: a lexical test first (so an outside path or a
/// <c>..</c> escape is refused without touching the disk at all), then a walk over the components below the
/// root that reads only their attributes and, for a junction or symbolic link, the link itself. A link's
/// target is never opened; a target outside the root refuses the path. Nothing outside the root is opened,
/// read or handed to ffmpeg/ffprobe. A link whose target is a volume or device path (<c>\??\Volume{…}\</c>,
/// <c>GLOBALROOT</c>, a rooted path with no drive) cannot be placed on the path alone, so it refuses the path too.
/// A folder becomes a root only through <see cref="Claim"/>: it must be new or empty (or an older bench root), and
/// it then carries the <see cref="MarkerName"/> marker, because the bench deletes its own scratch folders under it.
/// </summary>
internal sealed class FixtureRoot
{
    /// <summary>The default root's folder name under the user's temp folder.</summary>
    public const string DefaultFolderName = "vsj-bench-fixtures";

    /// <summary>The file that marks a folder as a bench fixture root (written by <see cref="Claim"/>).</summary>
    public const string MarkerName = ".vsj-bench-root";

    private const int MaxLinkHops = 32;

    /// <summary>Open (not create) the root at <paramref name="path"/>.</summary>
    public FixtureRoot(string path)
    {
        Lexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Real = RealPath(Lexical);
    }

    /// <summary><c>%TEMP%\vsj-bench-fixtures</c>.</summary>
    public static string DefaultPath => Path.Combine(Path.GetTempPath(), DefaultFolderName);

    /// <summary>The root as given, made absolute.</summary>
    public string Lexical { get; }

    /// <summary>The root with any junction or link in its own path resolved — every resolved input starts with it.</summary>
    public string Real { get; }

    /// <summary>Where the results ledger lives.</summary>
    public string ResultsDir => Path.Combine(Real, "results");

    /// <summary>The append-only results ledger, one JSON line per measurement.</summary>
    public string TimingsFile => Path.Combine(ResultsDir, "timings.jsonl");

    /// <summary>Scratch space for scenario outputs (split parts, cold copies, service caches).</summary>
    public string WorkDir => Path.Combine(Real, "work");

    /// <summary>The fixture manifest: which fixtures passed their self-check, with size and time stamp.</summary>
    public string ManifestFile => Path.Combine(Real, "fixtures.json");

    /// <summary>Where the engines' full-error logs go (never %LOCALAPPDATA%).</summary>
    public string ErrorLogDir => Path.Combine(Real, "errlogs");

    /// <summary>The marker that makes this folder a bench root.</summary>
    public string MarkerFile => Path.Combine(Real, MarkerName);

    /// <summary>
    /// Make sure this folder may be used as a root, before anything is written or deleted under it. A folder with
    /// the marker is a root. A new or empty folder becomes one (the marker is written). A non-empty folder without
    /// the marker becomes one only when it already holds the bench's own <c>fixtures.json</c> or results ledger (a
    /// root made before the marker existed); anything else is refused (<see cref="ExitCodes.PathRefused"/>), so a
    /// mistyped <c>--root</c> can never have the bench delete <c>work/</c> or <c>selftest/</c> in someone's folder.
    /// Once the folder is a root, the check reads no file (the marker's attributes only).
    /// </summary>
    public void Claim(TextWriter? output = null)
    {
        if (File.Exists(Real))
        {
            throw new BenchException(ExitCodes.Usage, $"'{Real}' is a file; --root takes a folder.");
        }

        if (File.Exists(MarkerFile))
        {
            return;
        }

        if (!Directory.Exists(Real))
        {
            try
            {
                Directory.CreateDirectory(Real);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new BenchException(ExitCodes.Usage, $"cannot create the fixture root '{Real}': {e.Message}");
            }

            WriteMarker();
            return;
        }

        if (!Directory.EnumerateFileSystemEntries(Real).Any())
        {
            WriteMarker();
            return;
        }

        if (HoldsBenchFiles())
        {
            WriteMarker();
            output?.WriteLine($"NOTE '{Real}' holds the bench's own files from before the {MarkerName} marker; it is now marked as a fixture root.");
            return;
        }

        throw new BenchException(ExitCodes.PathRefused,
            $"'{Real}' is not a bench fixture root: the folder is not empty and has no {MarkerName} marker. --root takes a new or empty " +
            "folder (or an existing bench root), because the bench writes and deletes its own scratch folders under its root.");
    }

    private void WriteMarker() =>
        File.WriteAllText(MarkerFile, "VideoSplitJoiner bench fixture root (T-188): synthetic fixtures, work files and results only. The bench deletes its own scratch folders here.\n");

    /// <summary>A root made before the marker: a <c>fixtures.json</c> that reads as the bench's manifest, or a results ledger.</summary>
    private bool HoldsBenchFiles()
    {
        if (File.Exists(TimingsFile))
        {
            return true;
        }

        try
        {
            return File.Exists(ManifestFile)
                && System.Text.Json.JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestFile)) is { Fixtures.Count: > 0 };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A path under the root (no checks — for files the bench itself is about to write).</summary>
    public string Child(params string[] parts) => Path.Combine(Real, Path.Combine(parts));

    /// <summary>
    /// The guard. Returns the full, link-resolved path when it is inside the root; otherwise throws a
    /// <see cref="BenchException"/> (<see cref="ExitCodes.PathRefused"/>) before anything is opened.
    /// </summary>
    public string Resolve(string path)
    {
        if (TryResolve(path, out var resolved, out var reason))
        {
            return resolved;
        }

        throw new BenchException(ExitCodes.PathRefused, $"refused '{path}': {reason}. The bench only opens files under its fixture root '{Real}'.");
    }

    /// <summary>The guard, without throwing.</summary>
    public bool TryResolve(string path, out string resolved, out string reason)
    {
        resolved = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "the path is empty";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "it is not a valid path (" + e.Message + ")";
            return false;
        }

        // 1. Lexical: Windows resolves '..' on the path string before the file system sees it, exactly as
        //    GetFullPath does, so a path that is not inside the root as written is refused here, unopened.
        var rel = RelativeInside(full);
        if (rel is null)
        {
            reason = "it is outside the fixture root";
            return false;
        }

        // 2. Links: a junction or symbolic link below the root may only lead back inside it.
        var current = Real;
        var pending = new Queue<string>(Components(rel));
        var hops = 0;
        while (pending.Count > 0)
        {
            var next = Path.Combine(current, pending.Dequeue());
            var link = Inspect(next);
            if (link.Unknown is not null)
            {
                reason = $"'{next}' could not be inspected ({link.Unknown})";
                return false;
            }

            if (link.Refused is not null)
            {
                reason = $"'{next}' is {link.Refused}";
                return false;
            }

            if (link.Target is null)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                reason = "it goes through too many links";
                return false;
            }

            // A relative target is relative to the folder holding the link, which is `current`.
            var targetFull = Path.GetFullPath(link.Target, current);
            var rest = string.Join(Path.DirectorySeparatorChar, pending);
            full = Path.GetFullPath(rest.Length == 0 ? targetFull : Path.Combine(targetFull, rest));
            rel = RelativeInside(full);
            if (rel is null)
            {
                reason = $"'{next}' is a link to '{targetFull}', which is outside the fixture root";
                return false;
            }

            current = Real;
            pending = new Queue<string>(Components(rel));
        }

        resolved = current;
        return true;
    }

    /// <summary>The part of <paramref name="full"/> below the root (lexical or real form), or null when it is not inside.</summary>
    private string? RelativeInside(string full)
    {
        foreach (var root in new[] { Real, Lexical })
        {
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return full[prefix.Length..];
            }
        }

        return null;
    }

    private static IEnumerable<string> Components(string rel) =>
        rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The root's own path with every junction or link in it replaced by its target, walking from the volume
    /// root. Reads attributes and link data only.
    /// </summary>
    private static string RealPath(string full)
    {
        var volume = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(volume))
        {
            return full;
        }

        var current = volume;
        var pending = new Queue<string>(Components(full[volume.Length..]));
        var hops = 0;
        while (pending.Count > 0)
        {
            var next = Path.Combine(current, pending.Dequeue());
            var link = Inspect(next);
            if (link.Target is null || ++hops > MaxLinkHops)
            {
                current = next;
                continue;
            }

            var targetFull = Path.GetFullPath(link.Target, current);
            var rest = string.Join(Path.DirectorySeparatorChar, pending);
            var combined = Path.GetFullPath(rest.Length == 0 ? targetFull : Path.Combine(targetFull, rest));
            volume = Path.GetPathRoot(combined) ?? string.Empty;
            current = volume;
            pending = new Queue<string>(Components(combined[volume.Length..]));
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>
    /// A path component: not a link (all null), a link (its target), not inspectable (why), or a link whose target
    /// cannot be placed on the path alone (why it is refused).
    /// </summary>
    private readonly record struct LinkInfo(string? Target, string? Unknown, string? Refused = null);

    /// <summary>
    /// Is <paramref name="path"/> a junction or symbolic link, and to where? Reads the path's attributes and,
    /// for a reparse point, the link itself (opened as a reparse point, never followed). A missing path is
    /// "not a link"; a reparse point that is not a link (a cloud placeholder, say) is "not a link" too.
    /// </summary>
    private static LinkInfo Inspect(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LinkInfo(null, e.Message);
        }

        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return default;
        }

        try
        {
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            var target = info.LinkTarget;
            if (target is null)
            {
                return default;
            }

            var stripped = StripDevicePrefix(target);
            return IsPlaceable(stripped)
                ? new LinkInfo(stripped, null)
                : new LinkInfo(null, null, $"a link to '{target}', a volume or device path the guard cannot place");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LinkInfo(null, e.Message);
        }
    }

    /// <summary>
    /// Can a link target be placed on the path alone? A fully qualified path (<c>C:\x</c>, <c>\\server\share\x</c>)
    /// or a plain relative one (<c>..\x</c>) can. A volume or device path cannot: .NET hands a junction to
    /// <c>\??\Volume{GUID}\x</c> back as <c>Volume{GUID}\x</c>, which would otherwise read as a folder beside the
    /// link. The same goes for <c>GLOBALROOT\…</c>, a leftover <c>\\?\</c>, <c>\??\</c> or <c>\\.\</c> prefix, and a
    /// rooted path with no drive (<c>\Device\…</c>, <c>\x</c>) or a drive-relative one (<c>C:x</c>).
    /// </summary>
    internal static bool IsPlaceable(string target)
    {
        foreach (var device in new[] { "Volume{", "GLOBALROOT", @"\\?\", @"\??\", @"\\.\" })
        {
            if (target.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        try
        {
            return !Path.IsPathRooted(target) || Path.IsPathFullyQualified(target);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary><c>\??\C:\x</c> and <c>\\?\C:\x</c> → <c>C:\x</c>; <c>\\?\UNC\s\x</c> → <c>\\s\x</c>.</summary>
    internal static string StripDevicePrefix(string target)
    {
        foreach (var prefix in new[] { @"\??\UNC\", @"\\?\UNC\" })
        {
            if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + target[prefix.Length..];
            }
        }

        foreach (var prefix in new[] { @"\??\", @"\\?\" })
        {
            if (target.StartsWith(prefix, StringComparison.Ordinal))
            {
                return target[prefix.Length..];
            }
        }

        return target;
    }
}
