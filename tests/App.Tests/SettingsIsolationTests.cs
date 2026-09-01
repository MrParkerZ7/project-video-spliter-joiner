using System;
using System.IO;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-140 — the suite must never write the user's real settings file.
///
/// <para>It did. `%APPDATA%/VideoSplitJoiner/settings.json` on a real machine contained
/// <c>"lastInputDir": "C:\\videos"</c> and <c>"lastOutputDir": "C:\\out"</c> — fixture paths from this
/// very assembly. <c>SplitViewModel</c> and <c>JoinViewModel</c> default to <c>new AppSettings()</c>, and
/// about fifteen tests construct them without injecting a store, then load a fixture path whose setter
/// calls <c>Save()</c>. The user experiences it as the app forgetting their folders and layout.</para>
///
/// <para>These tests pin the GUARD rather than the call sites: a module initializer redirects the default
/// path for the whole assembly, so the sixteenth test that forgets to inject is inert instead of
/// destructive.</para>
/// </summary>
public sealed class SettingsIsolationTests
{
    /// <summary>The real per-user path, computed independently of the override under test.</summary>
    private static string RealUserPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, AppSettings.AppFolderName, "settings.json");
    }

    [Trait("serves-spec", "SPEC-009")]
    [Fact]
    public void TheDefaultPathIsRedirected_ForTheWholeAssembly()
    {
        AppSettings.DefaultFilePathOverride.Should().NotBeNullOrWhiteSpace(
            "the module initializer must have run before any test in this assembly");

        AppSettings.DefaultFilePath().Should().NotBe(
            RealUserPath(),
            "a suite run must never resolve to the user's real profile");

        AppSettings.DefaultFilePath().Should().StartWith(
            Path.GetTempPath(), "stray writes land in temp, where they belong");
    }

    /// <summary>
    /// The load-bearing one. This is the exact shape that caused the bug: a view model built without an
    /// injected settings store, then handed a path. It must not reach the user's file.
    /// </summary>
    [Trait("serves-spec", "SPEC-009")]
    [Fact]
    public void AViewModelBuiltWithoutInjectedSettings_DoesNotTouchTheUsersFile()
    {
        var real = RealUserPath();
        var before = File.Exists(real) ? File.ReadAllBytes(real) : null;

        // The exact fallback ~15 tests hit: a view model with no injected store does `new AppSettings()`,
        // which resolves DefaultFilePath(). Build one that way, then write through the same construction.
        _ = new SplitViewModel(new BulkFakeProbe(), new ThrowingFakeSplitEngine());

        var fallbackStore = new AppSettings();      // identical to the view models' fallback
        fallbackStore.LastInputDir = @"C:\videos";  // the setter that calls Save() — the actual culprit

        if (before is null)
        {
            File.Exists(real).Should().BeFalse("the suite must not CREATE the user's settings file either");
        }
        else
        {
            File.ReadAllBytes(real).Should().Equal(
                before, "the user's settings must be byte-identical after a test that persists");
        }
    }

    [Trait("serves-spec", "SPEC-009")]
    [Fact]
    public void TheRedirectedStoreStillWorksNormally()
    {
        // The guard must not break persistence — it relocates it, it does not disable it.
        var path = Path.Combine(TestSettingsIsolation.Root, "roundtrip-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new AppSettings(path) { LastInputDir = @"D:\somewhere" };

        new AppSettings(path).LastInputDir.Should().Be(
            @"D:\somewhere", "isolation must not turn saving into a no-op — that would hide real bugs");
    }
}
