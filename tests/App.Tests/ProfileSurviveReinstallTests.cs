using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.App.Views;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-147 (SPEC-007) — the survival half of "saved and restorable even after reinstall".
///
/// <para><see cref="InstallerLeavesUserDataTests"/> asserts the installer does not DELETE user data.
/// This asserts the other half: that user data is not stored where an uninstall would reach in the first
/// place, and that a fresh run of the app over the same folders finds everything — which is exactly what
/// a reinstall is, once the program files are put back.</para>
///
/// <para>Deliberately over the app's OWN path resolution rather than a copy of it, so moving either root
/// fails here loudly instead of quietly breaking the guarantee.</para>
/// </summary>
public sealed class ProfileSurviveReinstallTests : IDisposable
{
    private readonly string _dir;

    public ProfileSurviveReinstallTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t147-reinstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ---- Where the data lives ------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void BothRootsAreUnderTheUserProfile_NotBesideTheProgram()
    {
        // The thumbnails root is the app's real resolution - nothing redirects it.
        var thumbsRoot = ProfileThumbnailStore.DefaultRoot();

        // The settings path is redirected away from the user's real profile for the whole test run
        // (TestSettingsIsolation, T-140), so what is asserted about it below is only what holds in BOTH
        // modes. The redirect is the reason this test cannot simply compare it to %APPDATA%.
        var settingsFile = AppSettings.DefaultFilePath();
        var installDir = AppContext.BaseDirectory;

        // The property that makes an uninstall survivable: an installer removes what it installed, and
        // neither of these is under it.
        settingsFile.Should().NotStartWith(installDir,
            "settings stored beside the .exe would be removed with the program");
        thumbsRoot.Should().NotStartWith(installDir,
            "pictures stored beside the .exe would be removed with the program");

        var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profileRoot))
        {
            thumbsRoot.Should().StartWith(profileRoot,
                "the pictures live under the user profile, which an uninstall does not touch");
        }
    }

    // ---- The reinstall itself -------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ProfilesAndPicturesAreStillThereAfterTheProgramIsRemovedAndPutBack()
    {
        var settingsFile = Path.Combine(_dir, "settings.json");
        var thumbsRoot = Path.Combine(_dir, "profile-thumbs");

        var source = Path.Combine(_dir, "chosen.png");
        File.WriteAllText(source, "PICTURE");

        // --- the user, before uninstalling ---
        var before = new AppSettings(settingsFile);
        var store = new ProfileThumbnailStore(thumbsRoot);
        var stored = store.Save("Anime OP", source);
        before.SaveProfile(new CutProfile("Anime OP", TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30), stored));
        before.SaveProfile(new CutProfile("Podcast", TimeSpan.FromSeconds(12), null));

        File.Exists(settingsFile).Should().BeTrue("precondition: the profiles were persisted");
        File.Exists(stored).Should().BeTrue("precondition: the picture was stored");

        // --- uninstall: the installer removes program files ONLY (I79), so nothing here is touched.
        //     Reinstall + first launch = brand-new instances over the same two folders.
        var after = new AppSettings(settingsFile);
        var restoredStore = new ProfileThumbnailStore(thumbsRoot);

        after.CutProfiles.Should().HaveCount(2, "the profiles survived");

        var profile = after.CutProfiles.Single(p => p.Name == "Anime OP");
        profile.IntroFromStart.Should().Be(TimeSpan.FromSeconds(90));
        profile.OutroFromEnd.Should().Be(TimeSpan.FromSeconds(30));
        profile.ThumbnailPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(profile.ThumbnailPath!).Should().BeTrue("and so did its picture");
        File.ReadAllText(profile.ThumbnailPath!).Should().Be("PICTURE", "byte for byte");

        // The reinstalled app can still manage them.
        restoredStore.DeleteByPath(profile.ThumbnailPath!);
        File.Exists(profile.ThumbnailPath!).Should().BeFalse();
    }

    // ---- The picture that did NOT survive the trip ----------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AProfileWhosePictureIsMissing_ShowsThePlaceholder_RatherThanBreaking()
    {
        // The exact cross-machine shape: a profile carrying a ThumbnailPath into a folder that does not
        // exist here (the two-root hazard of ADR-0021, and the reason backups embed images).
        var elsewhere = @"C:\Users\SomeoneElse\AppData\Local\VideoSplitJoiner\profile-thumbs\anime-op.png";
        var converter = new PathToBitmapConverter();

        var image = converter.Convert(elsewhere, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);

        image.Should().BeNull(
            "a null image is what makes the placeholder show — the binding must not throw on a stale path");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task AProfileWhosePictureIsMissing_StillAppliesItsCut()
    {
        var probe = new BulkFakeProbe();
        var gate = new SemaphoreSlim(3, 3);
        const string path = @"C:\v\episode.mp4";
        probe.SetUniform(path, TimeSpan.FromSeconds(100), 2);

        var row = new BulkItemViewModel(path, probe, gate) { Duration = TimeSpan.FromSeconds(100) };
        await row.StartKeyframeScanAsync();

        var orphaned = new CutProfile(
            "Anime OP",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            @"Z:\gone\anime-op.png");

        var report = CutProfileApplier.ApplyProfile(orphaned, new[] { row });

        report.AppliedCount.Should().Be(1);
        row.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10));
        row.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(90),
            "the cut is the profile's job; the picture is decoration and its absence changes nothing");
    }
}
