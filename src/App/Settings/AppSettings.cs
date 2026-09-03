using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoSplitJoiner.Core.Profiles;

namespace VideoSplitJoiner.App.Settings;

/// <summary>
/// File-backed <see cref="IAppSettings"/> (T-038). Persists the two "remember where I was" folders to
/// <c>%APPDATA%/VideoSplitJoiner/settings.json</c> via <see cref="System.Text.Json"/>, mirroring the
/// per-user app-data convention T-037 established for the error logs (under LocalApplicationData).
/// <para>
/// Robust by design — a settings problem must never crash the app:
/// <list type="bullet">
///   <item>missing file → defaults (nulls);</item>
///   <item>corrupt / unreadable JSON → defaults, swallowed, no throw;</item>
///   <item>unwritable dir / locked file → the value stays in memory only, no throw.</item>
/// </list>
/// Writes are best-effort temp-then-rename so a crash mid-write can never leave a half-written file
/// in place of a good one. The settings file PATH is injectable so unit tests target a temp dir
/// instead of the real APPDATA.
/// </para>
/// </summary>
public sealed class AppSettings : IAppSettings
{
    /// <summary>The folder name created under the app-data root (matches <c>ErrorLogWriter.AppFolderName</c>).</summary>
    public const string AppFolderName = "VideoSplitJoiner";

    /// <summary>The settings file name under the app-data folder.</summary>
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly ProfileThumbnailStore? _thumbnailStore;

    private string? _lastInputDir;
    private string? _lastOutputDir;
    private LayoutMode _layoutMode = LayoutMode.Horizontal;
    private double? _horizontalSplitRatio;
    private double? _verticalSplitRatio;
    private bool? _bulkApplyCutToAllRows;
    private bool? _bulkAutoDeleteOriginals;
    private bool? _bulkAutoEmptyRecycleBin;
    private AppTab? _lastTab;
    private double? _bulkHorizontalSplitRatio;
    private double? _bulkVerticalSplitRatio;
    private List<CutProfile> _cutProfiles = new();

    /// <summary>
    /// Create a settings store over the default per-user file, loading any existing state. Wires the
    /// default <see cref="ProfileThumbnailStore"/> (T-106) so <see cref="DeleteProfile"/> cascades to the
    /// deleted profile's thumbnail file. (Constructing the store is side-effect-free — it only resolves a
    /// root string; no directory is created until a thumbnail is actually saved.)
    /// </summary>
    public AppSettings()
        : this(DefaultFilePath(), new ProfileThumbnailStore())
    {
    }

    /// <summary>
    /// Create a settings store over an explicit file path (used by tests). Loads the file immediately
    /// if it exists; a missing or corrupt file leaves the store at defaults (nulls). The optional
    /// <paramref name="thumbnailStore"/> (T-106) receives the delete-cascade — when <c>null</c>, deleting
    /// a profile simply skips the thumbnail-file cleanup (the profile persistence is unaffected).
    /// </summary>
    public AppSettings(string filePath, ProfileThumbnailStore? thumbnailStore = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _thumbnailStore = thumbnailStore;
        Load();
    }

    /// <summary>The resolved settings file path this store reads/writes.</summary>
    public string FilePath => _filePath;

    /// <inheritdoc />
    public string? LastInputDir
    {
        get => _lastInputDir;
        set
        {
            if (!string.Equals(_lastInputDir, value, StringComparison.Ordinal))
            {
                _lastInputDir = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public string? LastOutputDir
    {
        get => _lastOutputDir;
        set
        {
            if (!string.Equals(_lastOutputDir, value, StringComparison.Ordinal))
            {
                _lastOutputDir = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public LayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (_layoutMode != value)
            {
                _layoutMode = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public double? HorizontalSplitRatio
    {
        get => _horizontalSplitRatio;
        set
        {
            if (!Nullable.Equals(_horizontalSplitRatio, value))
            {
                _horizontalSplitRatio = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public double? VerticalSplitRatio
    {
        get => _verticalSplitRatio;
        set
        {
            if (!Nullable.Equals(_verticalSplitRatio, value))
            {
                _verticalSplitRatio = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public AppTab? LastTab
    {
        get => _lastTab;
        set
        {
            if (!Nullable.Equals(_lastTab, value))
            {
                _lastTab = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public bool? BulkApplyCutToAllRows
    {
        get => _bulkApplyCutToAllRows;
        set
        {
            if (!Nullable.Equals(_bulkApplyCutToAllRows, value))
            {
                _bulkApplyCutToAllRows = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public bool? BulkAutoDeleteOriginals
    {
        get => _bulkAutoDeleteOriginals;
        set
        {
            if (!Nullable.Equals(_bulkAutoDeleteOriginals, value))
            {
                _bulkAutoDeleteOriginals = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public bool? BulkAutoEmptyRecycleBin
    {
        get => _bulkAutoEmptyRecycleBin;
        set
        {
            if (!Nullable.Equals(_bulkAutoEmptyRecycleBin, value))
            {
                _bulkAutoEmptyRecycleBin = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public double? BulkHorizontalSplitRatio
    {
        get => _bulkHorizontalSplitRatio;
        set
        {
            if (!Nullable.Equals(_bulkHorizontalSplitRatio, value))
            {
                _bulkHorizontalSplitRatio = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public double? BulkVerticalSplitRatio
    {
        get => _bulkVerticalSplitRatio;
        set
        {
            if (!Nullable.Equals(_bulkVerticalSplitRatio, value))
            {
                _bulkVerticalSplitRatio = value;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CutProfile> CutProfiles => _cutProfiles;

    /// <inheritdoc />
    public void SaveProfile(CutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var index = _cutProfiles.FindIndex(
            p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _cutProfiles[index] = profile; // upsert-in-place (position preserved)
        }
        else
        {
            _cutProfiles.Add(profile);
        }

        Save();
    }

    /// <inheritdoc />
    public void DeleteProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        // Capture the matching profile BEFORE removing, so the cascade can also clean up a thumbnail
        // whose stored path diverges from the recomputed safe-name path (e.g. a directly-set path).
        var removedProfile = _cutProfiles.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        var removed = _cutProfiles.RemoveAll(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            // T-106 cascade: best-effort delete the profile's thumbnail file(s). Both calls never throw
            // (missing/locked file is swallowed) so a thumbnail problem can never break the profile delete.
            _thumbnailStore?.Delete(name);
            if (removedProfile?.ThumbnailPath is { } thumbnailPath)
            {
                _thumbnailStore?.DeleteByPath(thumbnailPath);
            }

            Save();
        }
    }

    /// <summary>
    /// The default per-user settings file: <c>%APPDATA%/VideoSplitJoiner/settings.json</c>. Falls back
    /// to the OS temp folder when the app-data path cannot be resolved (rare — headless / restricted).
    /// </summary>
    /// <summary>
    /// Redirects <see cref="DefaultFilePath"/> away from the user's real profile (T-140).
    ///
    /// <para><b>Why this exists.</b> <c>SplitViewModel</c> and <c>JoinViewModel</c> default their settings
    /// dependency to <c>new AppSettings()</c>, which resolves here. Around fifteen tests construct those
    /// view models without injecting a settings store and then load a fixture path, whose setter calls
    /// <c>Save()</c> - so running the suite OVERWROTE the real
    /// <c>%APPDATA%/VideoSplitJoiner/settings.json</c>, wiping the user's last-used folders and layout.
    /// It was found by reading a real machine's settings file and seeing test fixture paths in it.</para>
    ///
    /// <para>Fixing only the call sites would leave the trap armed for the next test that forgets. The
    /// test assembly sets this once, so a missed injection is inert rather than destructive.</para>
    /// </summary>
    public static string? DefaultFilePathOverride { get; set; }

    public static string DefaultFilePath()
    {
        if (DefaultFilePathOverride is { Length: > 0 } redirected)
        {
            return redirected;
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, AppFolderName, FileName);
    }

    /// <summary>
    /// Read the settings file into memory. Missing file → defaults; corrupt / unreadable JSON →
    /// defaults, swallowed. Never throws.
    /// </summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var dto = JsonSerializer.Deserialize<SettingsDto>(json, SerializerOptions);
            if (dto is not null)
            {
                _lastInputDir = NullIfBlank(dto.LastInputDir);
                _lastOutputDir = NullIfBlank(dto.LastOutputDir);
                _layoutMode = ParseLayoutMode(dto.LayoutMode);
                _horizontalSplitRatio = ClampRatio(dto.HorizontalSplitRatio);
                _verticalSplitRatio = ClampRatio(dto.VerticalSplitRatio);
                // T-143: an unrecognised stored value (a hand-edited file, a build with fewer tabs)
                // falls back to Split rather than throwing or opening on nothing.
                _lastTab = Enum.IsDefined(typeof(AppTab), dto.LastTab ?? -1) ? (AppTab)dto.LastTab!.Value : null;
                _bulkApplyCutToAllRows = dto.BulkApplyCutToAllRows;
                _bulkAutoDeleteOriginals = dto.BulkAutoDeleteOriginals;   // absent -> null -> OFF
                _bulkAutoEmptyRecycleBin = dto.BulkAutoEmptyRecycleBin;   // absent -> null -> OFF // absent (older file) → null → default (ON)
                _bulkHorizontalSplitRatio = ClampRatio(dto.BulkHorizontalSplitRatio); // absent (older file) → null → default
                _bulkVerticalSplitRatio = ClampRatio(dto.BulkVerticalSplitRatio);
                _cutProfiles = MapProfiles(dto.CutProfiles); // missing/empty field → empty list (older files safe)
            }
        }
        catch
        {
            // Corrupt / unreadable settings must never crash the app — fall back to defaults.
            _lastInputDir = null;
            _lastOutputDir = null;
            _layoutMode = LayoutMode.Horizontal;
            _horizontalSplitRatio = null;
            _verticalSplitRatio = null;
            _bulkHorizontalSplitRatio = null;
            _bulkVerticalSplitRatio = null;
            _cutProfiles = new List<CutProfile>();
        }
    }

    /// <summary>
    /// Map the persisted cut-profile DTOs to validated <see cref="CutProfile"/> records. Robust by
    /// design — a null field (older file that predates the feature) yields an empty list, and any single
    /// malformed entry (blank name, NaN/negative offset, a value the record's own validation rejects) is
    /// SKIPPED rather than crashing the load, so one bad row never loses the good ones. Deduped by name
    /// (case-insensitive, first occurrence wins) to match the upsert contract.
    /// </summary>
    private static List<CutProfile> MapProfiles(List<CutProfileDto>? dtos)
    {
        var result = new List<CutProfile>();
        if (dtos is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in dtos)
        {
            var name = NullIfBlank(dto?.Name);
            if (name is null || !TryToSeconds(dto!.IntroSeconds, out var intro))
            {
                continue;
            }

            TimeSpan? outro = null;
            if (dto.OutroSeconds is double outroSeconds)
            {
                // A finite, non-negative value can STILL be outside TimeSpan's range (e.g. 1e300), and
                // TimeSpan.FromSeconds would throw. That throw used to escape this loop and abort the whole
                // load, so ONE corrupt row silently wiped every saved profile. Range is validated up front.
                if (!TryToSeconds(outroSeconds, out var parsedOutro))
                {
                    continue;
                }

                outro = parsedOutro;
            }

            CutProfile profile;
            try
            {
                // T-106: an absent thumbnailPath (older file) → null; the record normalizes blank → null.
                profile = new CutProfile(name, intro, outro, NullIfBlank(dto.ThumbnailPath));
            }
            catch
            {
                continue; // the record's own validation rejected it — skip, don't crash the load
            }

            if (seen.Add(profile.Name))
            {
                result.Add(profile);
            }
        }

        return result;
    }

    private static bool IsFiniteNonNegative(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

    /// <summary>
    /// Convert a persisted seconds value to a <see cref="TimeSpan"/>, rejecting anything
    /// <see cref="TimeSpan.FromSeconds(double)"/> could not represent — NaN, infinity, negative, OR a
    /// finite-but-out-of-range magnitude such as <c>1e300</c>. The last case is the one that mattered:
    /// it passes a naive finite/non-negative check yet throws on conversion, and that throw used to
    /// escape the profile-mapping loop and take every VALID sibling profile down with it.
    /// </summary>
    private static bool TryToSeconds(double value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (!IsFiniteNonNegative(value) || value > TimeSpan.MaxValue.TotalSeconds)
        {
            return false;
        }

        try
        {
            result = TimeSpan.FromSeconds(value);
            return true;
        }
        catch (OverflowException)
        {
            return false; // belt-and-braces: never let a corrupt row abort the load
        }
    }

    /// <summary>
    /// Parse the persisted layout string case-insensitively; anything missing/unknown → the
    /// <see cref="LayoutMode.Horizontal"/> default (first launch / legacy file / typo never crashes).
    /// </summary>
    private static LayoutMode ParseLayoutMode(string? value) =>
        string.Equals(value, nameof(LayoutMode.Vertical), StringComparison.OrdinalIgnoreCase)
            ? LayoutMode.Vertical
            : LayoutMode.Horizontal;

    /// <summary>
    /// Keep a persisted split-ratio inside a sane band (0.05..0.95) so a corrupt/out-of-range value can
    /// never wedge a pane to zero. Null / NaN / infinity → null (use the default).
    /// </summary>
    private static double? ClampRatio(double? value)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v))
        {
            return null;
        }

        return Math.Clamp(v, 0.05, 0.95);
    }

    /// <summary>
    /// Persist the current state best-effort via temp-then-rename. An unwritable dir / locked file is
    /// swallowed — the value stays in memory for the session. Never throws.
    /// </summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var dto = new SettingsDto
            {
                LastInputDir = _lastInputDir,
                LastOutputDir = _lastOutputDir,
                LayoutMode = _layoutMode.ToString(),
                HorizontalSplitRatio = _horizontalSplitRatio,
                VerticalSplitRatio = _verticalSplitRatio,
                LastTab = (int?)_lastTab,
                BulkApplyCutToAllRows = _bulkApplyCutToAllRows,
                BulkAutoDeleteOriginals = _bulkAutoDeleteOriginals,
                BulkAutoEmptyRecycleBin = _bulkAutoEmptyRecycleBin,
                BulkHorizontalSplitRatio = _bulkHorizontalSplitRatio,
                BulkVerticalSplitRatio = _bulkVerticalSplitRatio,
                // Null (not an empty array) when there are none, so the key is omitted entirely
                // (JsonIgnoreCondition.WhenWritingNull) — an older/empty file stays byte-clean.
                CutProfiles = _cutProfiles.Count == 0
                    ? null
                    : _cutProfiles.Select(p => new CutProfileDto
                    {
                        Name = p.Name,
                        IntroSeconds = p.IntroFromStart.TotalSeconds,
                        OutroSeconds = p.OutroFromEnd?.TotalSeconds,
                        // T-106: persist the PATH only (never image bytes); null → key omitted (byte-clean).
                        ThumbnailPath = p.ThumbnailPath,
                    }).ToList(),
            };

            var json = JsonSerializer.Serialize(dto, SerializerOptions);

            // Temp-then-rename so a crash mid-write can't replace a good file with a half-written one.
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        catch
        {
            // Best-effort: an unwritable dir / locked file leaves the value in memory only.
            TryDeleteTemp();
        }
    }

    private void TryDeleteTemp()
    {
        try
        {
            var tempPath = _filePath + ".tmp";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Nothing more we can do; ignore.
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// On-disk shape: <c>{ "lastInputDir": "...", "lastOutputDir": "...", "layoutMode": "Horizontal",
    /// "horizontalSplitRatio": 0.7, "verticalSplitRatio": 0.62 }</c>. Every field is nullable so an
    /// older file (missing the newer keys) round-trips to the documented defaults.
    /// </summary>
    private sealed class SettingsDto
    {
        [JsonPropertyName("lastInputDir")]
        public string? LastInputDir { get; set; }

        [JsonPropertyName("lastOutputDir")]
        public string? LastOutputDir { get; set; }

        [JsonPropertyName("layoutMode")]
        public string? LayoutMode { get; set; }

        [JsonPropertyName("horizontalSplitRatio")]
        public double? HorizontalSplitRatio { get; set; }

        [JsonPropertyName("verticalSplitRatio")]
        public double? VerticalSplitRatio { get; set; }

        /// <summary>
        /// The Bulk Cut tab's per-axis split ratios (G-039 / T-112) — separate keys from the Split-tab
        /// ratios above so the two tabs never share a persisted split. Absent in older files → <c>null</c>
        /// → the Bulk default (backward-compatible additive fields; null is omitted on write).
        /// </summary>
        [JsonPropertyName("bulkHorizontalSplitRatio")]
        public double? BulkHorizontalSplitRatio { get; set; }

        /// <summary>
        /// T-133 — whether set-at-playhead fans out to every ticked row. Absent in older files →
        /// <c>null</c> → the default, ON.
        /// </summary>
        /// <summary>T-143 - the last-used screen. Absent in older files => null => Split.</summary>
        [JsonPropertyName("lastTab")]
        public int? LastTab { get; set; }

        [JsonPropertyName("bulkApplyCutToAllRows")]
        public bool? BulkApplyCutToAllRows { get; set; }

        public bool? BulkAutoDeleteOriginals { get; set; }

        public bool? BulkAutoEmptyRecycleBin { get; set; }

        [JsonPropertyName("bulkVerticalSplitRatio")]
        public double? BulkVerticalSplitRatio { get; set; }

        /// <summary>
        /// The saved cut profiles (T-102). Absent in older files → <c>null</c> → an empty list on load
        /// (never a crash, no loss of the sibling folder/layout/ratio fields).
        /// </summary>
        [JsonPropertyName("cutProfiles")]
        public List<CutProfileDto>? CutProfiles { get; set; }
    }

    /// <summary>
    /// On-disk shape of one cut profile (T-102): the name plus the two offsets as SECONDS (double) — a
    /// stable, human-readable JSON form (never TimeSpan ticks). <c>outroSeconds</c> is nullable
    /// (<c>null</c> ⇒ keep runs to EOF, no tail trim). <c>thumbnailPath</c> (T-106) is an optional PATH
    /// string to the profile's thumbnail image — never image bytes; absent in older files → <c>null</c>
    /// (backward-compatible additive field), and null is omitted on write (WhenWritingNull) so a
    /// no-thumbnail profile stays byte-clean.
    /// </summary>
    private sealed class CutProfileDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("introSeconds")]
        public double IntroSeconds { get; set; }

        [JsonPropertyName("outroSeconds")]
        public double? OutroSeconds { get; set; }

        [JsonPropertyName("thumbnailPath")]
        public string? ThumbnailPath { get; set; }
    }
}
