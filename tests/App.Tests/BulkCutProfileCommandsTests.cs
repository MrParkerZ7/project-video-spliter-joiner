using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for the T-103 cut-profile commands on <see cref="BulkCutViewModel"/> — the thin VM glue over
/// the T-102 <see cref="CutProfileApplier"/> + <see cref="VideoSplitJoiner.App.Settings.IAppSettings"/>
/// persistence: Save-current-as persists via settings and refreshes the bar; Apply→selected / Apply→all
/// delegate to <see cref="CutProfileApplier.ApplyProfile"/> and surface the returned
/// <see cref="ApplyToAllReport"/>; Delete removes + refreshes; Save is disabled with no selection.
/// Rows use the real-snap <see cref="BulkFakeProbe"/> so apply/validity assertions exercise real logic.
/// </summary>
public sealed class BulkCutProfileCommandsTests
{
    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeSettings Settings) Build()
    {
        var probe = new BulkFakeProbe();
        var settings = new FakeSettings();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, new FakeBulkTrimEngine());
        return (vm, probe, settings);
    }

    private static async Task<BulkItemViewModel> AddRowAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double durationSeconds, double stepSeconds,
        double introSeconds, double? outroSeconds = null)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(durationSeconds), stepSeconds);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        if (outroSeconds is double o)
        {
            row.AddOutro(TimeSpan.FromSeconds(o));
        }

        return row;
    }

    // ---- Save current as… -------------------------------------------------------------------

    [Fact]
    public async Task SaveProfile_BuildsFromSelectedRow_PersistsViaSettings_AndRefreshesBar()
    {
        var (vm, probe, settings) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10, outroSeconds: 90);
        vm.SelectedItem = row;

        vm.SaveProfile("Anime OP");

        settings.CutProfiles.Should().ContainSingle(p => p.Name == "Anime OP", "the profile is persisted via IAppSettings (T-102)");
        var saved = settings.CutProfiles.Single();
        saved.IntroFromStart.Should().Be(TimeSpan.FromSeconds(10), "captured from the row's requested intro-end");
        saved.OutroFromEnd.Should().Be(TimeSpan.FromSeconds(10), "outro-from-end = Duration(100) − outro-start(90)");

        vm.Profiles.Should().ContainSingle(p => p.Name == "Anime OP", "the bar refreshes from settings after save");
        vm.SelectedProfile!.Name.Should().Be("Anime OP", "the just-saved profile becomes the selection");
        vm.HasProfiles.Should().BeTrue();
    }

    [Fact]
    public async Task SaveProfile_BlankName_IsIgnored_NothingPersisted()
    {
        var (vm, probe, settings) = Build();
        vm.SelectedItem = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);

        vm.SaveProfile("   ");

        settings.CutProfiles.Should().BeEmpty("a blank/whitespace name is rejected (naming UX: non-empty)");
        vm.Profiles.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveProfile_DuplicateName_UpsertsInPlace_NoSecondEntry()
    {
        var (vm, probe, settings) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10, outroSeconds: 90);
        vm.SelectedItem = row;
        vm.SaveProfile("Series");

        // Change the cut, re-save under the SAME name → upsert (T-102 upserts by case-insensitive name).
        row.IntroEnd.Requested = TimeSpan.FromSeconds(20);
        vm.SaveProfile("series"); // different casing on purpose

        settings.CutProfiles.Should().ContainSingle("the same-name save upserts rather than adding a second entry");
        settings.CutProfiles.Single().IntroFromStart.Should().Be(TimeSpan.FromSeconds(20), "the profile was replaced in place");
        vm.Profiles.Should().ContainSingle();
    }

    [Fact]
    public void SaveProfile_Command_Disabled_WithNoSelection()
    {
        var (vm, _, _) = Build();

        vm.SelectedItem.Should().BeNull("fresh VM, nothing added");
        vm.CanSaveProfile.Should().BeFalse();
        vm.SaveProfileCommand.CanExecute("X").Should().BeFalse("Save-current-as needs a selected source row");
    }

    // ---- Apply → selected / all -------------------------------------------------------------

    [Fact]
    public async Task ApplyProfileToSelected_AppliesTheCut_AndSurfacesTheReport()
    {
        var (vm, probe, _) = Build();
        // Capture a profile from a source row, then apply it to a DIFFERENT selected target.
        var source = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 10, outroSeconds: 90); // tail = 10
        vm.SelectedItem = source;
        vm.SaveProfile("Series");

        var target = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 100, 2, introSeconds: 4);
        vm.SelectedItem = target;

        var report = vm.ApplyProfileToSelected();

        target.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10), "intro is applied ABSOLUTE from start");
        target.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(90), "outro is applied FROM END (100 − 10)");
        report!.AppliedCount.Should().Be(1);
        report.InvalidatedRows.Should().BeEmpty();
        vm.ApplyToAllReport.Should().BeSameAs(report, "the profile apply surfaces through the SAME report property as apply-to-all");
        vm.ApplyReportSummary.Should().Contain("Applied to 1");
    }

    [Fact]
    public async Task ApplyProfileToAll_AppliesToEveryCheckedRow_AndSurfacesAppliedCount()
    {
        var (vm, probe, _) = Build();
        var source = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 12, outroSeconds: 88); // tail = 12
        vm.SelectedItem = source;
        vm.SaveProfile("Series");

        var b = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 100, 2, introSeconds: 4);
        var c = await AddRowAsync(vm, probe, @"C:\v\c.mp4", 200, 2, introSeconds: 4); // longer episode

        var report = vm.ApplyProfileToAll();

        report!.AppliedCount.Should().Be(3, "applied to all three checked rows (including the source)");
        report.InvalidatedRows.Should().BeEmpty();
        b.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(12));
        b.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(88), "100 − tail 12");
        c.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(188), "200 − tail 12 — uneven lengths align (from END)");
        vm.ApplyToAllReport!.AppliedCount.Should().Be(3);
    }

    [Fact]
    public async Task ApplyProfileToAll_ShorterTarget_Invalidated_IsReported_NotDropped()
    {
        // Seed a profile valid on a long episode BEFORE constructing the VM so the ctor's refresh picks it up.
        var probe = new BulkFakeProbe();
        var settings = new FakeSettings();
        settings.SaveProfile(new CutProfile("Long", TimeSpan.FromSeconds(80), null)); // intro 80 overshoots a 60s file
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, new FakeBulkTrimEngine());

        vm.Profiles.Should().ContainSingle(p => p.Name == "Long", "the seeded profile is projected on construct");
        vm.SelectedProfile = vm.Profiles.Single();

        var target = await AddRowAsync(vm, probe, @"C:\v\short.mp4", 60, 2, introSeconds: 6);

        var report = vm.ApplyProfileToAll();

        report!.AppliedCount.Should().Be(1, "the row was applied-to");
        report.InvalidatedRows.Should().Contain(target, "an invalidated row is REPORTED, never silently dropped");
        target.IsValidCut.Should().BeFalse();
        vm.ApplyReportSummary.Should().Contain("now invalid");
    }

    // SPEC-011#I56 — the load-bearing half of "applies to every IsCheckedByUser row": targeting reads the
    // RAW checkbox intent, NOT the computed IsEnabled. ApplyProfileToAll_AppliesToEveryCheckedRow uses
    // three valid rows, where the two agree, so it cannot tell them apart. Here an invalidating profile
    // auto-DISABLES the row (IsEnabled false) while the checkbox intent stays true — and a fixed profile
    // must still be able to rescue it. Targeting on IsEnabled would strand the row permanently: it could
    // only be re-validated by an apply it is no longer a target of.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ApplyProfileToAll_UsesRawCheckboxIntent_SoItCanReValidateAnAutoDisabledRow()
    {
        // Seed the invalidating profile BEFORE constructing the VM so the ctor's refresh projects it.
        var probe = new BulkFakeProbe();
        var settings = new FakeSettings();
        settings.SaveProfile(new CutProfile("Long", TimeSpan.FromSeconds(80), null)); // intro 80 overshoots a 60s file
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, new FakeBulkTrimEngine());
        vm.SelectedProfile = vm.Profiles.Single();

        var row = await AddRowAsync(vm, probe, @"C:\v\short.mp4", 60, 2, introSeconds: 6);

        vm.ApplyProfileToAll();

        row.IsValidCut.Should().BeFalse("intro 80 clamps to the 60s duration — nothing is left to keep");
        row.IsEnabled.Should().BeFalse("an invalid row auto-disables, so the rendered checkbox reads false");
        row.IsCheckedByUser.Should().BeTrue("but the user's RAW checkbox intent is untouched by the auto-disable");

        // The user fixes the profile and re-applies — the auto-disabled row must still be a target.
        settings.SaveProfile(new CutProfile("Long", TimeSpan.FromSeconds(10), null)); // upsert by name
        vm.SelectedProfile = settings.CutProfiles.Single(p => p.Name == "Long");

        var report = vm.ApplyProfileToAll();

        report!.AppliedCount.Should().Be(1,
            "targeting is the raw checkbox intent, so a currently-invalid (auto-disabled) checked row can still be rescued");
        report.InvalidatedRows.Should().BeEmpty("the fixed profile re-validates it");
        row.IsValidCut.Should().BeTrue();
        row.IsEnabled.Should().BeTrue("the rescued row re-enables itself for the batch");
    }

    [Fact]
    public async Task ApplyProfile_Commands_Disabled_WithNoSelectedProfile()
    {
        var (vm, probe, _) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 10);

        vm.SelectedProfile.Should().BeNull();
        vm.CanApplyProfileToSelected.Should().BeFalse();
        vm.CanApplyProfileToAll.Should().BeFalse();
        vm.ApplyProfileToSelected().Should().BeNull("no profile → no-op");
        vm.ApplyProfileToAll().Should().BeNull("no profile → no-op");
    }

    // ---- Delete -----------------------------------------------------------------------------

    [Fact]
    public async Task DeleteProfile_RemovesFromSettings_AndRefreshesBar()
    {
        var (vm, probe, settings) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 10, outroSeconds: 90);
        vm.SelectedItem = row;
        vm.SaveProfile("A");
        vm.SelectedItem = row; // re-affirm selection
        vm.SaveProfile("B");

        vm.Profiles.Should().HaveCount(2);
        vm.SelectedProfile = vm.Profiles.First(p => p.Name == "A");

        vm.DeleteSelectedProfile();

        settings.CutProfiles.Should().ContainSingle(p => p.Name == "B", "only the deleted profile is gone");
        vm.Profiles.Should().ContainSingle(p => p.Name == "B", "the bar refreshes after delete");
        vm.SelectedProfile!.Name.Should().Be("B", "selection re-points at the first remaining profile");
    }

    [Fact]
    public async Task DeleteProfile_LastOne_LeavesEmptyState()
    {
        var (vm, probe, settings) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;
        vm.SaveProfile("Only");

        vm.DeleteSelectedProfile();

        settings.CutProfiles.Should().BeEmpty();
        vm.Profiles.Should().BeEmpty();
        vm.SelectedProfile.Should().BeNull();
        vm.HasProfiles.Should().BeFalse("the bar reverts to the unobtrusive empty-state affordance");
    }
}
