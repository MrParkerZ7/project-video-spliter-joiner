using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-111 (SPEC-011) — the Bulk Cut "Apply → all" gestures must re-fire EVERY time, not just once. Two
/// buttons are covered: the profile <b>Apply → all</b> (<see cref="BulkCutViewModel.ApplyProfileToAllCommand"/>)
/// and the per-row <b>⧉ apply-to-all</b> (<see cref="BulkCutViewModel.ApplyToAllCommand"/>).
///
/// <para><b>Confirmed root cause (candidate 3 — "CanExecute stuck").</b> <see cref="RelayCommand.CanExecuteChanged"/>
/// used to forward SOLELY to <c>CommandManager.RequerySuggested</c>, so the VM's explicit
/// <c>RaiseCanExecuteChanged()</c> never notified a bound button deterministically — a subscribed handler saw
/// ZERO callbacks (the button re-evaluated only when WPF's heuristic, weak-referenced global requery happened
/// to fire on unrelated UI input, so its enabled-state went stale after the first use). And
/// <c>RaiseProfileCommandStates</c> re-raised only <c>SaveProfileCommand</c>, leaning on that global side
/// effect instead of re-raising the actual Apply→all command. The fix makes <see cref="RelayCommand"/> raise
/// its OWN event directly (while STILL chaining CommandManager) and re-raises each profile command explicitly.
///
/// <para>Candidates 1/2/4 were empirically <b>ruled out</b> and are pinned here as regression guards: selection
/// survives an apply (1), a re-apply reads the CURRENT source and propagates NEW values (2), and the shared
/// report refreshes on every apply (4). Apply semantics (outro-from-END, re-snap + re-validate per target,
/// invalidated rows reported — SPEC-011) are asserted intact.</para>
/// </summary>
public sealed class BulkCutApplyToAllReactivationTests
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
        await row.CurrentScanTask; // deterministic: keyframes ready before we set the cut
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        if (outroSeconds is double o)
        {
            row.AddOutro(TimeSpan.FromSeconds(o));
        }

        return row;
    }

    // ---- Root cause: the command's OWN CanExecuteChanged must fire (candidate 3) --------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public void RaiseCanExecuteChanged_NotifiesTheCommandsOwnSubscribers_Deterministically()
    {
        // Before the fix this was 0: CanExecuteChanged forwarded only to CommandManager.RequerySuggested,
        // which never fires a subscribed handler off a live dispatcher. That inertness is the root cause of
        // the button not reliably re-reflecting its gate after the first use.
        var command = new RelayCommand(_ => { }, _ => true);
        var raised = 0;
        command.CanExecuteChanged += (_, __) => raised++;

        command.RaiseCanExecuteChanged();

        raised.Should().Be(1, "RaiseCanExecuteChanged must deterministically notify the command's own subscribers");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ProfileApplyToAll_CanExecuteChanged_FiresWhenGateInputsChange()
    {
        var (vm, probe, _) = Build();
        var a = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 12);
        vm.SelectedItem = a;
        vm.SaveProfile("P"); // a profile is now selected

        var raised = 0;
        vm.ApplyProfileToAllCommand.CanExecuteChanged += (_, __) => raised++;

        // (1) profile-selection change is a gate input …
        vm.SelectedProfile = null;
        vm.SelectedProfile = vm.Profiles.First();
        // (2) … and so is the checked-row set.
        a.IsEnabled = false;
        a.IsEnabled = true;

        raised.Should().BeGreaterThan(0,
            "the Apply→all command must re-raise its OWN CanExecuteChanged when the profile selection or the checked-row set changes");
    }

    // ---- Per-row ⧉ apply-to-all re-fires every time (candidates 1 + 2 as guards) -------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task PerRowApplyToAll_SecondInvocation_StaysEnabled_AndReApplies()
    {
        var (vm, probe, _) = Build();
        var source = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 12, outroSeconds: 88); // tail 12
        var target = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 100, 2, introSeconds: 4);

        vm.ApplyToAllCommand.CanExecute(source).Should().BeTrue("2 rows → per-row ⧉ enabled");
        vm.ApplyToAll(source);
        target.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(12), "1st apply propagates intro");
        target.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(88), "1st apply propagates outro FROM END (100 − 12)");

        // The user tweaks the target between applies …
        target.IntroEnd.Requested = TimeSpan.FromSeconds(4);
        target.ClearOutro();

        // … the second click must STILL be enabled and MUST re-sync the target back to the source.
        vm.ApplyToAllCommand.CanExecute(source).Should().BeTrue("per-row ⧉ stays enabled for a second use");
        vm.ApplyToAll(source);
        target.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(12), "2nd apply re-propagates the source intro");
        target.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(88), "2nd apply re-propagates the source outro");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task PerRowApplyToAll_SourceChangedBetweenApplies_PropagatesTheNewValues()
    {
        var (vm, probe, _) = Build();
        var source = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 12);
        var target = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 100, 2, introSeconds: 4);

        vm.ApplyToAll(source);
        target.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(12));

        // Move the SOURCE's cut, then re-apply — the NEW value must reach the target (not swallowed as a no-op).
        source.IntroEnd.Requested = TimeSpan.FromSeconds(30);
        vm.ApplyToAll(source);

        target.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(30), "the CURRENT source value propagates on re-apply");
    }

    // ---- Profile Apply → all re-fires every time ---------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ProfileApplyToAll_TwiceInARow_StaysEnabled_SelectionSurvives_AndReApplies()
    {
        var (vm, probe, _) = Build();
        var a = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 12, outroSeconds: 88); // tail 12
        vm.SelectedItem = a;
        vm.SaveProfile("Series");
        var b = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 100, 2, introSeconds: 4);

        vm.ApplyProfileToAllCommand.CanExecute(null).Should().BeTrue("profile selected + checked rows → enabled");
        vm.ApplyProfileToAll();
        b.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(12));
        b.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(88));

        // Candidate 1 guard: applying must NOT lose the profile selection.
        vm.SelectedProfile.Should().NotBeNull("the selected profile survives an apply");

        // User drifts a target; the second Apply→all must still be enabled and re-apply.
        b.IntroEnd.Requested = TimeSpan.FromSeconds(4);
        b.ClearOutro();

        vm.ApplyProfileToAllCommand.CanExecute(null).Should().BeTrue("the profile Apply→all stays enabled for a second use");
        vm.ApplyProfileToAll();
        b.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(12), "the 2nd Apply→all re-applies the profile");
        b.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(88));
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ProfileApplyToAll_SourceReSavedBetweenApplies_PropagatesTheNewValues()
    {
        var (vm, probe, _) = Build();
        var a = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 12);
        vm.SelectedItem = a;
        vm.SaveProfile("P");
        var b = await AddRowAsync(vm, probe, @"C:\v\b.mp4", 100, 2, introSeconds: 4);

        vm.ApplyProfileToAll();
        b.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(12));

        // Change the source + re-save the SAME profile name (upsert), then Apply→all again.
        vm.SelectedItem = a;
        a.IntroEnd.Requested = TimeSpan.FromSeconds(30);
        vm.SaveProfile("P");

        vm.SelectedProfile.Should().NotBeNull("selection survives the re-save");
        vm.ApplyProfileToAllCommand.CanExecute(null).Should().BeTrue();
        vm.ApplyProfileToAll();

        b.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(30), "the re-saved profile's NEW intro propagates");
    }

    // ---- Boundary cases from the Case-Coverage Matrix ----------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task PerRowApplyToAll_SingleRow_IsDisabled()
    {
        var (vm, probe, _) = Build();
        var only = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 12);

        vm.ApplyToAllCommand.CanExecute(only).Should().BeFalse("a 1-row list has nothing to apply-to");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ProfileApplyToAll_Disabled_WithoutProfile_ThenReEnabled_OnSelect()
    {
        var (vm, probe, _) = Build();
        var a = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 12);
        vm.SelectedItem = a;
        vm.SaveProfile("P");

        vm.SelectedProfile = null;
        vm.CanApplyProfileToAll.Should().BeFalse("no profile → disabled");
        vm.ApplyProfileToAllCommand.CanExecute(null).Should().BeFalse();

        vm.SelectedProfile = vm.Profiles.First();
        vm.CanApplyProfileToAll.Should().BeTrue("profile picked → re-enabled");
        vm.ApplyProfileToAllCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ProfileApplyToAll_InvalidatingProfile_IsReported_AndStillReAppliable()
    {
        // A profile whose intro overshoots a short file: applied-to, flagged invalid, NOT dropped, and the
        // gate stays true so the user can adjust + re-apply.
        var probe = new BulkFakeProbe();
        var settings = new FakeSettings();
        settings.SaveProfile(new CutProfile("Long", TimeSpan.FromSeconds(80), null));
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), settings, new FakeBulkTrimEngine());
        vm.SelectedProfile = vm.Profiles.Single();

        var target = await AddRowAsync(vm, probe, @"C:\v\short.mp4", 60, 2, introSeconds: 6);

        var r1 = vm.ApplyProfileToAll();
        r1!.AppliedCount.Should().Be(1);
        r1.InvalidatedRows.Should().Contain(target, "an invalidated row is REPORTED, never silently dropped");

        vm.ApplyProfileToAllCommand.CanExecute(null).Should().BeTrue("the checked-row set is unchanged → still re-appliable");
        vm.ApplyProfileToAll().Should().NotBeNull("the second apply still runs");
    }

    // ---- Candidate 4 guard: the shared report refreshes on EVERY apply -----------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ApplyToAllReport_Refreshes_OnEachApply_IncludingAnIdenticalReapply()
    {
        var (vm, probe, _) = Build();
        var source = await AddRowAsync(vm, probe, @"C:\v\a.mp4", 100, 2, introSeconds: 12, outroSeconds: 88);
        await AddRowAsync(vm, probe, @"C:\v\b.mp4", 100, 2, introSeconds: 4);

        var reportChanges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BulkCutViewModel.ApplyToAllReport))
            {
                reportChanges++;
            }
        };

        vm.ApplyToAll(source);
        vm.ApplyToAll(source); // identical re-apply

        reportChanges.Should().Be(2, "the apply report refreshes on each apply so a re-apply never LOOKS like a no-op");
    }

    // ---- Shared CutMarkerViewModel guard: the Requested re-snap path is untouched -------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public void CutMarkerRequested_StillReSnaps_OnAChange_SharedWithSplit()
    {
        // The fix does not touch CutMarkerViewModel.Requested; this pins that the shared setter still
        // re-snaps a genuine change (the same setter Split's markers use) so Split is not regressed.
        var probe = new BulkFakeProbe();
        var keyframes = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) };
        var marker = new CutMarkerViewModel(probe, () => keyframes, TimeSpan.FromSeconds(9));

        marker.Snapped.Should().Be(TimeSpan.FromSeconds(10), "9 snaps to the nearest keyframe 10");

        marker.Requested = TimeSpan.FromSeconds(19);
        marker.Snapped.Should().Be(TimeSpan.FromSeconds(20), "a genuine change re-snaps to 20");
    }
}
