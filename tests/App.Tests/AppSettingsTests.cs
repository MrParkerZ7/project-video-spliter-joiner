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
}
