using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-147 (SPEC-007) — the VM layer over <see cref="ProfileBackup"/>: the two commands the profile bar
/// binds to, and the two decisions the VM must never make on the user's behalf.
///
/// <para><see cref="ProfileBackupTests"/> covers the file format itself. What is asserted HERE is the
/// wiring, because that is where a backup feature turns dangerous: the collision hook defaults to
/// KEEPING existing profiles, so an unwired or half-wired host cannot silently overwrite them, and a
/// cancelled dialog must be a no-op rather than an error.</para>
/// </summary>
public sealed class BulkCutProfileBackupCommandsTests : IDisposable
{
    private readonly string _dir;

    public BulkCutProfileBackupCommandsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t147-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static readonly TaskCompletionSource ParkedForever = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task NeverSettles(TimeSpan _, CancellationToken ct) => ParkedForever.Task.WaitAsync(ct);

    /// <summary>Profiles are projected into the bar ON CONSTRUCT, so seeding happens before the VM exists.</summary>
    private (BulkCutViewModel Vm, FakeSettings Settings) Build(params CutProfile[] seed)
    {
        var settings = new FakeSettings();
        foreach (var p in seed)
        {
            settings.SaveProfile(p);
        }

        var vm = new BulkCutViewModel(
            new BulkFakeProbe(),
            new ThrowingFakeSplitEngine(),
            new FakeThumbnailService(),
            settings,
            new FakeBulkTrimEngine(),
            thumbnailStore: new ProfileThumbnailStore(Path.Combine(_dir, "thumbs")),
            thumbnailDelay: NeverSettles);
        return (vm, settings);
    }

    private static CutProfile Profile(string name, double intro = 5) =>
        new(name, TimeSpan.FromSeconds(intro), null);

    private string Path_(string name) => Path.Combine(_dir, name);

    // ---- Export -------------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ExportIsOfferedOnlyWhenThereIsSomethingToExport()
    {
        Build().Vm.ExportProfilesCommand.CanExecute(null).Should().BeFalse("an empty backup helps nobody");

        Build(Profile("Anime OP")).Vm.ExportProfilesCommand.CanExecute(null).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ImportIsAlwaysOffered_BecauseHavingNoProfilesIsExactlyWhenYouNeedIt()
    {
        var (vm, _) = Build();

        vm.ImportProfilesCommand.CanExecute(null).Should().BeTrue(
            "a fresh install has no profiles — gating restore on having some would be backwards");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ExportWritesTheFileTheUserChose_AndSaysWhatItDid()
    {
        var (vm, _) = Build(Profile("Anime OP"), Profile("Podcast"));

        var dest = Path_("backup.json");
        vm.ChooseProfileExportPath = () => dest;

        vm.ExportProfiles();

        File.Exists(dest).Should().BeTrue();
        vm.Operation.ResultSummary.Should().Contain("2", "the count is the confirmation that it worked");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void CancellingTheExportDialogDoesNothingAtAll()
    {
        var (vm, _) = Build(Profile("Anime OP"));
        vm.ChooseProfileExportPath = () => null;

        vm.ExportProfiles();

        vm.Operation.ResultSummary.Should().BeNullOrEmpty("cancelling is not an outcome worth reporting");
        vm.Operation.Error.Should().BeNull("and it is certainly not an error");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AnUnwritableDestinationIsReportedOnTheScreensErrorSurface()
    {
        var (vm, _) = Build(Profile("Anime OP"));

        // A directory occupying the destination path: File.WriteAllText cannot overwrite it.
        var dest = Path_("occupied");
        Directory.CreateDirectory(dest);
        vm.ChooseProfileExportPath = () => dest;

        vm.ExportProfiles();

        vm.Operation.Error.Should().NotBeNull("a silent failure would let someone think they had a backup");
        vm.Operation.Error!.RawTail.Should().NotBeNullOrWhiteSpace("with something copyable to act on");
    }

    // ---- Import -------------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ImportAddsTheProfilesAndTheyAppearInTheBar()
    {
        var (vm, settings) = Build();
        var src = Path_("backup.json");
        ProfileBackup.Export(new[] { Profile("Anime OP", 90), Profile("Podcast", 12) }, src);

        vm.ChooseProfileImportPath = () => src;
        vm.ImportProfiles();

        settings.CutProfiles.Should().HaveCount(2);
        vm.Profiles.Select(p => p.Name).Should().Contain(new[] { "Anime OP", "Podcast" },
            "the bar is refreshed — an import you cannot see is an import that looks broken");
        vm.Operation.ResultSummary.Should().Contain("2");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void CancellingTheImportDialogDoesNothingAtAll()
    {
        var (vm, settings) = Build(Profile("Mine"));
        vm.ChooseProfileImportPath = () => null;

        vm.ImportProfiles();

        settings.CutProfiles.Should().ContainSingle().Which.Name.Should().Be("Mine");
        vm.Operation.Error.Should().BeNull();
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AnUnreadableBackupIsReported_AndChangesNothing()
    {
        var (vm, settings) = Build(Profile("Keep me", 7));

        var src = Path_("bad.json");
        File.WriteAllText(src, "definitely not json");
        vm.ChooseProfileImportPath = () => src;

        vm.ImportProfiles();

        vm.Operation.Error.Should().NotBeNull();
        settings.CutProfiles.Should().ContainSingle().Which.IntroFromStart.Should().Be(
            TimeSpan.FromSeconds(7), "a failed restore leaves what you had exactly as it was");
    }

    // ---- The decision the VM must never make itself --------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AnUnwiredHostKeepsExistingProfiles_RatherThanOverwritingThem()
    {
        var (vm, settings) = Build(Profile("Anime OP", 5));   // theirs

        var src = Path_("backup.json");
        ProfileBackup.Export(new[] { Profile("Anime OP", 99), Profile("New one", 12) }, src);

        vm.ChooseProfileImportPath = () => src;
        // ConfirmProfileOverwrite deliberately NOT wired — the default must be the safe answer.
        vm.ImportProfiles();

        settings.CutProfiles.Single(p => p.Name == "Anime OP").IntroFromStart.Should().Be(
            TimeSpan.FromSeconds(5), "the default answer to 'may I overwrite?' is no");
        settings.CutProfiles.Should().HaveCount(2, "while the genuinely new profile still arrives");
        vm.Operation.ResultSummary.Should().Contain("kept", "and the user is told what was left alone");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheOverwritePromptIsOnlyRaisedWhenSomethingWouldActuallyBeOverwritten()
    {
        var (vm, _) = Build();
        var src = Path_("backup.json");
        ProfileBackup.Export(new[] { Profile("Brand new") }, src);

        var asked = 0;
        vm.ChooseProfileImportPath = () => src;
        vm.ConfirmProfileOverwrite = _ => { asked++; return true; };

        vm.ImportProfiles();

        asked.Should().Be(0, "asking about a collision that does not exist trains people to click Yes");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void SayingYesToTheOverwritePromptOverwrites_WithTheCountItWasAskedAbout()
    {
        var (vm, settings) = Build(Profile("Anime OP", 5), Profile("Podcast", 5));

        var src = Path_("backup.json");
        ProfileBackup.Export(new[] { Profile("Anime OP", 99), Profile("Podcast", 99) }, src);

        var askedAbout = -1;
        vm.ChooseProfileImportPath = () => src;
        vm.ConfirmProfileOverwrite = n => { askedAbout = n; return true; };

        vm.ImportProfiles();

        askedAbout.Should().Be(2, "the prompt names how many profiles are at stake");
        settings.CutProfiles.Should().OnlyContain(p => p.IntroFromStart == TimeSpan.FromSeconds(99));
    }
}
