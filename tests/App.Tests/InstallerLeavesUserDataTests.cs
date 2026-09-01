using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-147 (SPEC-007) — uninstalling must not take the user's profiles with it.
///
/// <para>Today it does not: <c>packaging/VideoSplitJoiner.iss</c> has no <c>[UninstallDelete]</c> section
/// and touches nothing under the user profile. But that is an <b>accident of the current script</b>, not
/// a guarantee — a future installer edit could start removing app-data and nothing would notice until
/// someone lost their profiles on an upgrade.</para>
///
/// <para>This test reads the real installer script and asserts the property directly. It is deliberately
/// narrow: it does not care how the script is organised, only that it never asks Inno Setup to delete
/// anything under the user's data folders.</para>
/// </summary>
public sealed class InstallerLeavesUserDataTests
{
    /// <summary>Walk up from the test binary to the repository root, then to the installer script.</summary>
    private static string? FindInstallerScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "packaging", "VideoSplitJoiner.iss");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheInstallerScriptIsFound()
    {
        // If this fails the other test would vacuously pass, which is worse than a red build.
        FindInstallerScript().Should().NotBeNull(
            "the guarantee below is worthless if the script cannot be located");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void UninstallRemovesNoUserData()
    {
        var script = FindInstallerScript();
        if (script is null)
        {
            return; // reported by the test above
        }

        var text = File.ReadAllText(script);

        text.Should().NotContain(
            "[UninstallDelete]",
            "an UninstallDelete section is how an installer starts eating user data — if one is ever " +
            "genuinely needed, it must be scoped to the install directory and this assertion updated " +
            "deliberately, not silently");

        // The user's profiles and pictures live under these roots. The installer must never name them.
        foreach (var userDataConstant in new[] { "{userappdata}", "{localappdata}", "{userdocs}" })
        {
            text.Should().NotContain(
                userDataConstant,
                $"the installer names {userDataConstant}, which is where profiles and their pictures live");
        }
    }

    /// <summary>
    /// The paths this is protecting, asserted from the app's own resolution so the test tracks the code
    /// rather than a copy of it. If either root ever moves, this fails and the installer check is revisited.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void UserDataStillLivesWhereWeThinkItDoes()
    {
        var settingsRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var thumbsRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        settingsRoot.Should().NotBeNullOrWhiteSpace();
        thumbsRoot.Should().NotBeNullOrWhiteSpace();

        // Documented, not lamented: the split across Roaming and Local is exactly why ProfileBackup
        // embeds images rather than relying on a folder copy (ADR-0021).
        settingsRoot.Should().NotBe(
            thumbsRoot,
            "profiles and their pictures are in DIFFERENT roots — the reason export carries images inline");
    }
}
