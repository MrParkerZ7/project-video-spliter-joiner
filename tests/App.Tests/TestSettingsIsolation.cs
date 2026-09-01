using System;
using System.IO;
using System.Runtime.CompilerServices;
using VideoSplitJoiner.App.Settings;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-140 — keep the test suite out of the user's real settings file.
///
/// <para><b>The bug this closes.</b> <c>SplitViewModel</c> and <c>JoinViewModel</c> default their settings
/// dependency to <c>new AppSettings()</c>, which resolves to
/// <c>%APPDATA%/VideoSplitJoiner/settings.json</c> — the real one. Around fifteen tests construct those
/// view models without injecting a store, then load a fixture path, whose setter calls <c>Save()</c>. So
/// running the suite overwrote the user's last-used folders and layout. It was found by reading a real
/// machine's settings file and finding <c>C:\videos</c> and <c>C:\out</c> — pure test fixtures — in it.</para>
///
/// <para><b>Why a module initializer.</b> Fixing the fifteen call sites leaves the trap armed for the
/// sixteenth. This runs before any test in the assembly and redirects the default path, so a forgotten
/// injection is inert rather than destructive. The call sites are worth fixing too — a test with a hidden
/// dependency on machine state is a bad test — but this is the guard that cannot be forgotten.</para>
/// </summary>
internal static class TestSettingsIsolation
{
    /// <summary>Where the suite's stray settings writes land instead of the user's profile.</summary>
    internal static string Root { get; } = Path.Combine(
        Path.GetTempPath(), "vsj-test-settings-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Redirect()
    {
        Directory.CreateDirectory(Root);
        AppSettings.DefaultFilePathOverride = Path.Combine(Root, "settings.json");
    }
}
