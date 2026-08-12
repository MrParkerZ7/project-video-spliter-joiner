using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    private string? _lastInputDir;
    private string? _lastOutputDir;
    private LayoutMode _layoutMode = LayoutMode.Horizontal;
    private double? _horizontalSplitRatio;
    private double? _verticalSplitRatio;

    /// <summary>Create a settings store over the default per-user file, loading any existing state.</summary>
    public AppSettings()
        : this(DefaultFilePath())
    {
    }

    /// <summary>
    /// Create a settings store over an explicit file path (used by tests). Loads the file immediately
    /// if it exists; a missing or corrupt file leaves the store at defaults (nulls).
    /// </summary>
    public AppSettings(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
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

    /// <summary>
    /// The default per-user settings file: <c>%APPDATA%/VideoSplitJoiner/settings.json</c>. Falls back
    /// to the OS temp folder when the app-data path cannot be resolved (rare — headless / restricted).
    /// </summary>
    public static string DefaultFilePath()
    {
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
    }
}
