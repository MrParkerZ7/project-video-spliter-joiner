using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoSplitJoiner.Core.Profiles;

namespace VideoSplitJoiner.App.Settings;

/// <summary>
/// Export and import cut profiles as ONE self-contained file (T-147).
///
/// <para><b>Why one file, images included.</b> A profile is stored across two roots: the profile itself in
/// Roaming <c>%APPDATA%</c> (<c>settings.json</c>) and its picture in Local <c>%LOCALAPPDATA%</c>
/// (<c>profile-thumbs/</c>). Anyone who backs up "their settings", or moves machine via a roaming profile,
/// keeps the profiles and loses every image — the profile survives with a <c>ThumbnailPath</c> pointing at
/// a folder that does not exist there. Rather than migrate existing users' files between roots (a risky
/// change for an old install), the backup file carries the images inline, so one file is genuinely the
/// whole story. See ADR-0021.</para>
///
/// <para>Images are ~96px, so base64 inside the JSON costs little and removes any question of relative
/// paths, missing siblings, or zip handling.</para>
///
/// <para><b>Import is an upsert, never a wipe.</b> Importing must not be able to cost someone the profiles
/// they already had — that would make "restore" the most dangerous button in the app. Collisions are the
/// caller's decision, surfaced through <see cref="ImportPlan"/>, not resolved silently here.</para>
/// </summary>
public static class ProfileBackup
{
    /// <summary>Bumped only if the shape changes incompatibly; an unknown version is refused, not guessed at.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- The file shape ---------------------------------------------------------------------------

    internal sealed class BackupDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("exportedUtc")]
        public string? ExportedUtc { get; set; }

        [JsonPropertyName("profiles")]
        public List<ProfileDto>? Profiles { get; set; }
    }

    internal sealed class ProfileDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("introSeconds")]
        public double IntroSeconds { get; set; }

        [JsonPropertyName("outroSeconds")]
        public double? OutroSeconds { get; set; }

        /// <summary>The picture, inline. Null when the profile had none, or its file was unreadable.</summary>
        [JsonPropertyName("imageBase64")]
        public string? ImageBase64 { get; set; }

        /// <summary>Extension of the embedded image, so it can be written back under the right name.</summary>
        [JsonPropertyName("imageExtension")]
        public string? ImageExtension { get; set; }
    }

    // ---- Export -----------------------------------------------------------------------------------

    /// <summary>
    /// Write every profile, with its picture inline, to <paramref name="destination"/>.
    /// A profile whose image cannot be read is still exported — WITHOUT the image, rather than being
    /// dropped: losing a profile because its picture went missing would be a poor trade.
    /// </summary>
    /// <returns>How many profiles were written, and how many of them carried an image.</returns>
    public static (int Profiles, int Images) Export(
        IReadOnlyList<CutProfile> profiles, string destination)
    {
        if (profiles is null)
        {
            throw new ArgumentNullException(nameof(profiles));
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("A destination path is required.", nameof(destination));
        }

        var images = 0;
        var dto = new BackupDto
        {
            Version = CurrentVersion,
            ExportedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Profiles = new List<ProfileDto>(),
        };

        foreach (var p in profiles)
        {
            var entry = new ProfileDto
            {
                Name = p.Name,
                IntroSeconds = p.IntroFromStart.TotalSeconds,
                OutroSeconds = p.OutroFromEnd?.TotalSeconds,
            };

            if (TryReadImage(p.ThumbnailPath, out var bytes, out var ext))
            {
                entry.ImageBase64 = Convert.ToBase64String(bytes!);
                entry.ImageExtension = ext;
                images++;
            }

            dto.Profiles.Add(entry);
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(destination, JsonSerializer.Serialize(dto, Options));
        return (dto.Profiles.Count, images);
    }

    private static bool TryReadImage(string? path, out byte[]? bytes, out string? extension)
    {
        bytes = null;
        extension = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            bytes = File.ReadAllBytes(path);
            extension = Path.GetExtension(path);
            return bytes.Length > 0;
        }
        catch
        {
            return false; // an unreadable picture must not cost the profile
        }
    }

    // ---- Import -----------------------------------------------------------------------------------

    /// <summary>What an import would do, so the caller can decide about collisions before anything changes.</summary>
    public sealed record ImportPlan(
        IReadOnlyList<CutProfile> New,
        IReadOnlyList<CutProfile> Colliding,
        IReadOnlyDictionary<string, (byte[] Bytes, string Extension)> Images,
        string? Error)
    {
        /// <summary>True when the file could not be read as a backup at all — nothing should be changed.</summary>
        public bool Failed => Error is not null;

        public int Total => New.Count + Colliding.Count;
    }

    /// <summary>
    /// Read <paramref name="source"/> and work out what importing it would do, WITHOUT changing anything.
    /// A corrupt, truncated, empty, or unknown-version file yields <see cref="ImportPlan.Failed"/> with a
    /// message — never a partial application, because a half-applied restore is worse than a refused one.
    /// </summary>
    public static ImportPlan Plan(string source, IReadOnlyList<CutProfile> existing)
    {
        var empty = Array.Empty<CutProfile>();
        var noImages = new Dictionary<string, (byte[], string)>(StringComparer.OrdinalIgnoreCase);

        BackupDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<BackupDto>(File.ReadAllText(source));
        }
        catch (Exception ex)
        {
            return new ImportPlan(empty, empty, noImages, "That file could not be read as a profile backup: " + ex.Message);
        }

        if (dto is null || dto.Profiles is null)
        {
            return new ImportPlan(empty, empty, noImages, "That file does not contain any profiles.");
        }

        if (dto.Version > CurrentVersion)
        {
            return new ImportPlan(
                empty, empty, noImages,
                $"That backup was written by a newer version of the app (format {dto.Version}). Update, then import it.");
        }

        var have = new HashSet<string>(existing.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var fresh = new List<CutProfile>();
        var clashing = new List<CutProfile>();
        var images = new Dictionary<string, (byte[], string)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in dto.Profiles)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue; // a nameless profile has no upsert key — skip it rather than invent one
            }

            CutProfile profile;
            try
            {
                profile = new CutProfile(
                    entry.Name.Trim(),
                    TimeSpan.FromSeconds(Math.Max(0, entry.IntroSeconds)),
                    entry.OutroSeconds is { } o ? TimeSpan.FromSeconds(Math.Max(0, o)) : null);
            }
            catch
            {
                continue; // a row that fails the profile's own validation is skipped, not forced in
            }

            if (entry.ImageBase64 is { Length: > 0 })
            {
                try
                {
                    images[profile.Name] = (Convert.FromBase64String(entry.ImageBase64), entry.ImageExtension ?? ".png");
                }
                catch
                {
                    // a corrupt image costs the picture, never the profile
                }
            }

            (have.Contains(profile.Name) ? clashing : fresh).Add(profile);
        }

        return new ImportPlan(fresh, clashing, images, null);
    }

    /// <summary>
    /// Apply a plan. <paramref name="includeColliding"/> decides the collision policy — the CALLER's
    /// decision, made with the plan in hand; this never silently overwrites.
    /// </summary>
    /// <returns>How many profiles were written, and how many pictures were restored.</returns>
    public static (int Profiles, int Images) Apply(
        ImportPlan plan,
        IAppSettings settings,
        ProfileThumbnailStore store,
        bool includeColliding)
    {
        if (plan.Failed)
        {
            return (0, 0);
        }

        var chosen = includeColliding ? plan.New.Concat(plan.Colliding) : plan.New;
        var written = 0;
        var restored = 0;

        foreach (var profile in chosen)
        {
            var toSave = profile;

            if (plan.Images.TryGetValue(profile.Name, out var image))
            {
                // The store copies from a file, so stage the embedded bytes and let it own the naming.
                var staged = Path.Combine(
                    Path.GetTempPath(), "vsj-import-" + Guid.NewGuid().ToString("N") + image.Extension);
                try
                {
                    File.WriteAllBytes(staged, image.Bytes);
                    toSave = profile with { ThumbnailPath = store.Save(profile.Name, staged) };
                    restored++;
                }
                catch
                {
                    // A picture that cannot be stored leaves the profile with none — the placeholder
                    // shows and the cut still applies. Losing the profile over it would be worse.
                }
                finally
                {
                    try { File.Delete(staged); } catch { /* best-effort */ }
                }
            }

            settings.SaveProfile(toSave);
            written++;
        }

        return (written, restored);
    }
}
