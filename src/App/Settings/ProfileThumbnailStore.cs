using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VideoSplitJoiner.App.Settings;

/// <summary>
/// File store for cut-profile thumbnails (G-038 / T-106): copies a chosen image (an uploaded picture or a
/// captured frame jpg from <c>IThumbnailService</c>) into a per-user thumbnails folder and hands back the
/// stored PATH — the path is what a <c>CutProfile.ThumbnailPath</c> carries; the JSON never holds image
/// bytes. Deleting is the inverse: best-effort file removal when a profile is deleted or its thumbnail
/// cleared.
///
/// <para><b>Root:</b> <c>%LOCALAPPDATA%/VideoSplitJoiner/profile-thumbs</c>, mirroring the thumb-cache root
/// composition (<see cref="VideoSplitJoiner.Core.Thumbnails.FfmpegThumbnailService.DefaultCacheRoot"/>,
/// SPEC-005 I25) — LocalAppData with an OS-temp fallback for headless/restricted environments. The root is
/// injectable so tests never touch the real per-user folder.</para>
///
/// <para><b>File naming:</b> <c>&lt;safe(profileName)&gt;.&lt;ext&gt;</c> where <c>safe(...)</c> sanitizes
/// invalid filename characters and appends a short deterministic hash of the (case-folded) profile name so
/// two distinct names that sanitize alike never collide. Because the safe name is derived deterministically
/// from the profile name (case-insensitively, matching the upsert key), <see cref="Save"/>,
/// <see cref="Delete"/>, and the delete-cascade all resolve the SAME file across sessions. Saving overwrites
/// the profile's prior thumbnail — including a previous one with a different extension.</para>
///
/// <para><b>Best-effort deletes:</b> <see cref="Delete"/> / <see cref="DeleteByPath"/> never throw on a
/// missing or locked file — a thumbnail problem must never break profile save/load/apply. WPF-free (BCL
/// only): it copies/deletes files, it does not decode images.</para>
/// </summary>
public sealed class ProfileThumbnailStore
{
    /// <summary>The folder name created under the app-data root (matches the rest of the app's convention).</summary>
    public const string AppFolderName = "VideoSplitJoiner";

    /// <summary>Sub-folder under the app-data root that holds all profile thumbnails.</summary>
    public const string ThumbsFolderName = "profile-thumbs";

    /// <summary>Fallback extension when the source has no recognized image extension.</summary>
    private const string DefaultExtension = ".png";

    private static readonly HashSet<string> KnownImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff",
    };

    private readonly string _root;

    /// <summary>Create the store over the default per-user root (<see cref="DefaultRoot"/>).</summary>
    public ProfileThumbnailStore()
        : this(DefaultRoot())
    {
    }

    /// <summary>
    /// Create the store over an explicit root — used by tests to redirect the thumbnails tree away from the
    /// real per-user folder. No directory is created here; the folder is made lazily on first
    /// <see cref="Save"/>.
    /// </summary>
    public ProfileThumbnailStore(string root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    /// <summary>The resolved thumbnails root this store reads/writes.</summary>
    public string Root => _root;

    /// <summary>
    /// The default per-user thumbnails root: <c>%LOCALAPPDATA%/VideoSplitJoiner/profile-thumbs</c>. Falls
    /// back to the OS temp folder when local-app-data cannot be resolved (headless / restricted), mirroring
    /// <see cref="VideoSplitJoiner.Core.Thumbnails.FfmpegThumbnailService.DefaultCacheRoot"/>.
    /// </summary>
    public static string DefaultRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, AppFolderName, ThumbsFolderName);
    }

    /// <summary>
    /// Copy <paramref name="sourceImageOrFramePath"/> into the thumbnails root as this profile's thumbnail
    /// and return the stored ABSOLUTE path (the value to assign to <c>CutProfile.ThumbnailPath</c>). Any
    /// prior thumbnail for the profile is removed first, so saving overwrites — including across an
    /// extension change. Creates the root folder on demand. Throws <see cref="ArgumentException"/> on a
    /// blank name/source and <see cref="FileNotFoundException"/> when the source does not exist — a genuine
    /// caller error, distinct from the best-effort deletes; a real copy failure (locked/unwritable target)
    /// surfaces as an <see cref="IOException"/> for the caller to report.
    /// </summary>
    /// <param name="profileName">The profile to store the thumbnail for (its name is sanitized to a filename).</param>
    /// <param name="sourceImageOrFramePath">Path to an existing source image or captured frame to copy in.</param>
    /// <returns>The absolute path of the stored thumbnail file.</returns>
    public string Save(string profileName, string sourceImageOrFramePath)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException("A profile name is required to store a thumbnail.", nameof(profileName));
        }

        if (string.IsNullOrWhiteSpace(sourceImageOrFramePath))
        {
            throw new ArgumentException("A source image path is required.", nameof(sourceImageOrFramePath));
        }

        if (!File.Exists(sourceImageOrFramePath))
        {
            throw new FileNotFoundException("The source image to store as a thumbnail was not found.", sourceImageOrFramePath);
        }

        var safeName = SafeFileName(profileName);

        Directory.CreateDirectory(_root);

        var destination = Path.Combine(_root, safeName + NormalizeExtension(sourceImageOrFramePath));

        // Copy to a temp file FIRST, then swap. Deleting the prior thumbnail before the copy (the original
        // order) meant a copy that failed part-way — a source locked by another program, a full or
        // read-only volume — destroyed the picture the profile already had: the caller correctly reported
        // the failure and left CutProfile.ThumbnailPath untouched, but the path then pointed at a file that
        // no longer existed and the profile silently reverted to the placeholder. A failed Save must leave
        // the existing thumbnail exactly as it was.
        var staging = Path.Combine(_root, safeName + ".incoming" + NormalizeExtension(sourceImageOrFramePath));
        try
        {
            File.Copy(sourceImageOrFramePath, staging, overwrite: true);

            // The new bytes are safely on disk; only now is it safe to drop the old thumbnail (which may
            // carry a different extension, so the destination alone is not enough to displace it).
            DeleteExistingFor(safeName);
            File.Move(staging, destination, overwrite: true);
            return destination;
        }
        catch
        {
            TryDeleteFile(staging); // never leave a stray .incoming behind
            throw;
        }
    }

    /// <summary>
    /// Best-effort remove the thumbnail file(s) for <paramref name="profileName"/> — used when a profile is
    /// deleted (cascade) or its thumbnail is cleared. Never throws (missing/locked file is swallowed); a
    /// blank name is a no-op.
    /// </summary>
    public void Delete(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        DeleteExistingFor(SafeFileName(profileName));
    }

    /// <summary>
    /// Best-effort remove a specific stored thumbnail by its <paramref name="path"/> — used when a profile
    /// carries a directly-set thumbnail path. Never throws; a blank path is a no-op.
    /// </summary>
    public void DeleteByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        TryDeleteFile(path);
    }

    /// <summary>
    /// Sanitize a profile name into a stable, collision-resistant filename stem: invalid filename characters
    /// become <c>_</c>, trailing dots/spaces are stripped (Windows would drop them), the readable part is
    /// length-capped, and a short hash of the case-folded name is appended so two distinct names that
    /// sanitize to the same readable stem still map to different files. Deterministic and case-insensitive
    /// (matching the profile upsert key), so save/delete always resolve the same file. Exposed
    /// <c>internal</c> for direct unit tests via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static string SafeFileName(string profileName)
    {
        var trimmed = (profileName ?? string.Empty).Trim();

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var readable = sb.ToString().TrimEnd('.', ' ');
        if (readable.Length > 80)
        {
            readable = readable[..80].TrimEnd('.', ' ');
        }

        if (readable.Length == 0)
        {
            readable = "profile";
        }

        return readable + "_" + ShortHash(trimmed.ToLowerInvariant());
    }

    /// <summary>Enumerate the root and best-effort delete every file whose stem matches <paramref name="safeName"/>.</summary>
    private void DeleteExistingFor(string safeName)
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_root))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), safeName, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(file);
                }
            }
        }
        catch
        {
            // Best-effort — a missing/locked root or file must never surface to the caller.
        }
    }

    /// <summary>Preserve a recognized image extension (lowercased); otherwise default to <c>.png</c>.</summary>
    private static string NormalizeExtension(string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath);
        return !string.IsNullOrEmpty(ext) && KnownImageExtensions.Contains(ext)
            ? ext.ToLowerInvariant()
            : DefaultExtension;
    }

    /// <summary>Short, filesystem-safe hex hash (SHA-256, first 8 bytes) — collision guard for the safe name.</summary>
    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
        {
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort — a missing/locked thumbnail file is not a caller-facing failure.
        }
    }
}
