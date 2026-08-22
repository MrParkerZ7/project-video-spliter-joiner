using System;
using System.IO;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for <see cref="AppSettings"/> (T-038): the store round-trips the two remembered folders
/// to a JSON file, and every failure mode (missing file, corrupt JSON, unwritable path) degrades to
/// defaults / no-op WITHOUT throwing. All I/O is redirected to a temp directory so the tests never
/// touch the user's real APPDATA folder (the file path is injectable for exactly this reason).
/// </summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public AppSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-settings-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void RoundTrips_BothDirs_ToDiskAndReloads()
    {
        var settings = new AppSettings(_file);
        settings.LastInputDir = @"D:\videos\in";
        settings.LastOutputDir = @"D:\videos\out";

        // The setter persists immediately → the file exists and contains both keys.
        File.Exists(_file).Should().BeTrue("setting a value persists it immediately");
        var json = File.ReadAllText(_file);
        json.Should().Contain("lastInputDir").And.Contain("lastOutputDir");
        json.Should().Contain(@"D:\\videos\\in").And.Contain(@"D:\\videos\\out");

        // A fresh store over the same path reloads the persisted state.
        var reloaded = new AppSettings(_file);
        reloaded.LastInputDir.Should().Be(@"D:\videos\in");
        reloaded.LastOutputDir.Should().Be(@"D:\videos\out");
    }

    [Fact]
    public void MissingFile_YieldsDefaults_Nulls()
    {
        File.Exists(_file).Should().BeFalse("precondition: the file does not exist yet");

        var settings = new AppSettings(_file);

        settings.LastInputDir.Should().BeNull();
        settings.LastOutputDir.Should().BeNull();
    }

    [Fact]
    public void CorruptJson_YieldsDefaults_NoThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "{ this is not valid json ]]]");

        AppSettings? settings = null;
        Action act = () => settings = new AppSettings(_file);

        act.Should().NotThrow("corrupt settings must never crash the app");
        settings!.LastInputDir.Should().BeNull("corrupt JSON falls back to defaults");
        settings.LastOutputDir.Should().BeNull();
    }

    [Fact]
    public void EmptyFile_YieldsDefaults_NoThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, string.Empty);

        AppSettings? settings = null;
        Action act = () => settings = new AppSettings(_file);

        act.Should().NotThrow();
        settings!.LastInputDir.Should().BeNull();
        settings.LastOutputDir.Should().BeNull();
    }

    [Fact]
    public void UnwritablePath_DoesNotThrow_KeepsValueInMemory()
    {
        // Point the store at a settings file whose PARENT is actually a file, so directory creation
        // (and hence the write) fails internally. The value must still stick in memory.
        var blocker = Path.Combine(Path.GetTempPath(), "vsj-settings-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "I am a file, not a directory");

        try
        {
            var unwritable = Path.Combine(blocker, "sub", "settings.json");
            var settings = new AppSettings(unwritable);

            Action act = () => settings.LastInputDir = @"D:\videos\in";
            act.Should().NotThrow("an unwritable settings path must never crash the caller");

            settings.LastInputDir.Should().Be(@"D:\videos\in", "the value stays in memory for the session");
            File.Exists(unwritable).Should().BeFalse("nothing was written");
        }
        finally
        {
            try { File.Delete(blocker); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void BlankValue_IsNormalizedToNull_OnReload()
    {
        var settings = new AppSettings(_file);
        settings.LastInputDir = "   ";

        var reloaded = new AppSettings(_file);
        reloaded.LastInputDir.Should().BeNull("whitespace-only values normalize to null");
    }

    [Fact]
    public void DefaultFilePath_LandsUnderAppData_VideoSplitJoiner()
    {
        var path = AppSettings.DefaultFilePath();

        Path.GetFileName(path).Should().Be("settings.json");
        path.Should().Contain("VideoSplitJoiner");
    }

    // ---- T-081 / D-001: LayoutMode + per-axis split ratios ----------------------------------

    [Fact]
    public void LayoutMode_DefaultsToHorizontal_WhenMissing()
    {
        // A brand-new store (no file) and a legacy file (only the folder keys) both default to Horizontal.
        var fresh = new AppSettings(_file);
        fresh.LayoutMode.Should().Be(LayoutMode.Horizontal, "first launch / missing setting → Horizontal (today's behavior)");

        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "{ \"lastInputDir\": \"D:\\\\in\" }");
        var legacy = new AppSettings(_file);
        legacy.LayoutMode.Should().Be(LayoutMode.Horizontal, "a legacy file with no layoutMode key falls back to the default");
    }

    [Fact]
    public void LayoutMode_RoundTrips_ToDiskAndReloads()
    {
        var settings = new AppSettings(_file);
        settings.LayoutMode = LayoutMode.Vertical;

        File.Exists(_file).Should().BeTrue("setting the mode persists immediately");
        File.ReadAllText(_file).Should().Contain("\"layoutMode\": \"Vertical\"", "the mode is stored as a stable string");

        var reloaded = new AppSettings(_file);
        reloaded.LayoutMode.Should().Be(LayoutMode.Vertical, "the persisted mode is restored on the next launch");
    }

    [Fact]
    public void LayoutMode_UnknownStringInFile_FallsBackToHorizontal_NoThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "{ \"layoutMode\": \"Diagonal\" }");

        AppSettings? settings = null;
        Action act = () => settings = new AppSettings(_file);

        act.Should().NotThrow();
        settings!.LayoutMode.Should().Be(LayoutMode.Horizontal, "an unknown mode string degrades to the default");
    }

    [Fact]
    public void SplitRatios_RoundTrip_Independently_PerAxis()
    {
        var settings = new AppSettings(_file);
        settings.HorizontalSplitRatio = 0.68;
        settings.VerticalSplitRatio = 0.55;

        var reloaded = new AppSettings(_file);
        reloaded.HorizontalSplitRatio.Should().Be(0.68);
        reloaded.VerticalSplitRatio.Should().Be(0.55, "the two axes persist to separate keys (D6)");
    }

    [Fact]
    public void SplitRatios_DefaultToNull_WhenMissing()
    {
        var settings = new AppSettings(_file);

        settings.HorizontalSplitRatio.Should().BeNull("a never-set ratio means 'use the default'");
        settings.VerticalSplitRatio.Should().BeNull();
    }

    [Fact]
    public void SplitRatio_OutOfRangeInFile_IsClampedOnLoad()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "{ \"horizontalSplitRatio\": 9.0, \"verticalSplitRatio\": -3.0 }");

        var settings = new AppSettings(_file);

        settings.HorizontalSplitRatio.Should().BeLessThanOrEqualTo(0.95, "a corrupt/out-of-range ratio is clamped so no pane wedges to zero");
        settings.VerticalSplitRatio.Should().BeGreaterThanOrEqualTo(0.05);
    }

    // ==== SPEC-009 app-settings gaps (todo-automate) =========================================

    // SPEC-009#I3 — new AppSettings((string)null!) throws ArgumentNullException (ctor null guard).
    // No existing test passes a null path (every test injects a real temp path).
    [Fact]
    [Trait("serves-spec", "SPEC-009")]
    public void Ctor_NullPath_ThrowsArgumentNullException()
    {
        ((Action)(() => new AppSettings((string)null!)))
            .Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("filePath");
    }

    // SPEC-009#I13 — a persisted NON-FINITE ratio maps to null on load (NOT a clamped number), unlike
    // the finite out-of-range case (9.0 / -3.0) which clamps. ±Infinity parses to a double and is
    // routed through ClampRatio → null; a valid sibling field survives (proving it is the ratio branch,
    // not a whole-file reset).
    [Fact]
    [Trait("serves-spec", "SPEC-009")]
    public void Ratio_InfinityInFile_MapsToNull_NotClamped_SiblingSurvives()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file,
            "{ \"lastInputDir\": \"D:\\\\keep\", \"horizontalSplitRatio\": 1e999, \"verticalSplitRatio\": -1e999 }");

        var settings = new AppSettings(_file);

        settings.HorizontalSplitRatio.Should().BeNull("a +Infinity ratio maps to null, never a clamped number");
        settings.VerticalSplitRatio.Should().BeNull("a -Infinity ratio maps to null, never a clamped number");
        settings.LastInputDir.Should().Be(@"D:\keep", "the non-finite ratio nulls only the ratio, not the sibling fields");
    }

    // SPEC-009#I13 — a NaN literal is invalid JSON; the defensive load falls back to defaults, so the
    // ratio loads as null (never a NaN or a clamped number). Complements the ±Infinity case above.
    [Fact]
    [Trait("serves-spec", "SPEC-009")]
    public void Ratio_NaNTokenInFile_LoadsSafelyToNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "{ \"horizontalSplitRatio\": NaN }");

        AppSettings? settings = null;
        ((Action)(() => settings = new AppSettings(_file)))
            .Should().NotThrow("a NaN-bearing settings file must never crash the app");
        settings!.HorizontalSplitRatio.Should().BeNull("a NaN ratio maps to null on load, never a NaN/clamped number");
    }

    // SPEC-009#I15 — the dirty-check setters skip re-persisting when assigned the current value. Delete
    // the file after a write, re-assign the SAME value, and assert the file is NOT rewritten.
    [Fact]
    [Trait("serves-spec", "SPEC-009")]
    public void Setter_ReassigningSameValue_DoesNotRewriteTheFile()
    {
        var settings = new AppSettings(_file);
        settings.LastInputDir = @"D:\videos\in";      // persists → file exists
        File.Exists(_file).Should().BeTrue("precondition: the first set wrote the file");

        File.Delete(_file);                            // remove it so a re-write would be observable

        settings.LastInputDir = @"D:\videos\in";       // SAME value → dirty-check should skip Save
        File.Exists(_file).Should().BeFalse("re-assigning the current value must not re-persist (no rewrite)");

        // A DIFFERENT value still persists (proves the guard is value-based, not a blanket no-write).
        settings.LastInputDir = @"D:\videos\other";
        File.Exists(_file).Should().BeTrue("assigning a changed value re-persists");
    }

    // SPEC-009#I15 — the same no-rewrite guard holds for a ratio setter (Nullable.Equals path).
    [Fact]
    [Trait("serves-spec", "SPEC-009")]
    public void RatioSetter_ReassigningSameValue_DoesNotRewriteTheFile()
    {
        var settings = new AppSettings(_file);
        settings.HorizontalSplitRatio = 0.62;
        File.Exists(_file).Should().BeTrue();

        File.Delete(_file);

        settings.HorizontalSplitRatio = 0.62;          // same value → no rewrite
        File.Exists(_file).Should().BeFalse("re-assigning the current ratio must not re-persist");
    }

    // SPEC-009#I16 — atomic temp-then-rename write: no stray "<path>.tmp" lingers after a save, and a
    // pre-existing good file is replaced (via File.Replace) rather than left half-written.
    [Fact]
    [Trait("serves-spec", "SPEC-009")]
    public void Save_IsAtomicTempThenRename_NoStrayTmp_ReplacesExistingFile()
    {
        var tmp = _file + ".tmp";

        var settings = new AppSettings(_file);
        settings.LastInputDir = @"D:\in";              // first write (File.Move over a fresh path)

        File.Exists(_file).Should().BeTrue();
        File.Exists(tmp).Should().BeFalse("the temp file is renamed into place, never left behind");

        settings.LastOutputDir = @"D:\out";            // second write over an EXISTING file (File.Replace)

        File.Exists(tmp).Should().BeFalse("no stray .tmp after replacing an existing file either");
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty("no temp artifacts linger in the settings folder");

        var reloaded = new AppSettings(_file);
        reloaded.LastInputDir.Should().Be(@"D:\in", "the replace preserved the earlier field");
        reloaded.LastOutputDir.Should().Be(@"D:\out", "the atomic replace committed the new field");
    }
}
