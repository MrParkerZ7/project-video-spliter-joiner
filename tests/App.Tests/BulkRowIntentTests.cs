using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-127 (SPEC-011) — the Bulk Cut row's <b>intent / eligibility split</b>.
///
/// <para><b>The defect.</b> One property did two jobs: <c>IsEnabled</c> exposed the user's checkbox intent
/// through a setter while its GETTER answered computed eligibility
/// (<c>_isEnabledByUser &amp;&amp; !IsAutoDisabled</c>), and the row checkbox bound to it <b>two-way</b>. A
/// freshly imported row is a no-op trim, so it is auto-disabled, so the getter returned <c>false</c> for
/// EVERY row while the backing intent field was already <c>true</c>. The user's click therefore wrote
/// <c>true</c> over <c>true</c>, the setter's <c>!=</c> guard short-circuited, no <c>PropertyChanged</c> was
/// raised, and <b>the gesture was dead</b>. Clicking a second time wrote <c>false</c> and silently poisoned
/// the row: it stayed excluded even after a real cut was set, and apply-to-all skipped it (it targets
/// <c>IsCheckedByUser</c>).</para>
///
/// <para><b>The fix under test.</b> <see cref="BulkItemViewModel.IsCheckedByUser"/> is the public, settable,
/// always-notifying INTENT the checkbox binds to; <see cref="BulkItemViewModel.IsEnabled"/> is READ-ONLY
/// computed ELIGIBILITY that the engine / <c>CanRunBatch</c> / <c>RunLabel</c> filter on; and
/// <see cref="BulkItemViewModel.ExclusionReason"/> +
/// <see cref="BulkItemViewModel.IsExcludedDespiteBeingChecked"/> make "ticked but not counted" legible
/// instead of silent.</para>
///
/// <para>Every case below asserts BOTH correctness and performance; the performance assertions are
/// STRUCTURAL — bounded heavy-op counts (keyframe scans, frame grabs) and an O(1)-per-toggle notification
/// shape — never wall-clock timing. No ffmpeg, no WPF: the real-snap <see cref="BulkFakeProbe"/> drives the
/// actual nearest-keyframe math and the <see cref="FakeThumbnailService"/> stands in for the frame grabs.</para>
/// </summary>
public sealed class BulkRowIntentTests
{
    private const string PathA = @"C:\videos\ep01.mp4";
    private const string PathB = @"C:\videos\ep02.mp4";
    private const string PathC = @"C:\videos\ep03.mp4";

    /// <summary>The sentence a ticked-but-nothing-to-trim row explains itself with (em dash escaped so this source stays ASCII).</summary>
    private const string NothingToTrim = "nothing to trim yet \u2014 set an intro or outro";

    // ---- Harness ------------------------------------------------------------------------------

    // A debounce seam that never settles: every cut-point frame grab parks in its debounce window and can
    // never reach the thumbnail service, so a test that is not ABOUT grabs never races a background one.
    private static readonly TaskCompletionSource ParkedForever = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task NeverSettles(TimeSpan _, CancellationToken ct) => ParkedForever.Task.WaitAsync(ct);

    /// <summary>An immediate (non-parking) debounce seam — grabs proceed straight to the fake service.</summary>
    private static Task Immediate(TimeSpan _, CancellationToken ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeThumbnailService Thumbs) Build(
        Func<TimeSpan, CancellationToken, Task>? thumbnailDelay = null)
    {
        var probe = new BulkFakeProbe();
        var thumbs = new FakeThumbnailService();
        var vm = new BulkCutViewModel(
            probe,
            new ThrowingFakeSplitEngine(),
            thumbs,
            new FakeSettings(),
            new FakeBulkTrimEngine(),
            thumbnailDebounce: TimeSpan.FromMilliseconds(1),
            thumbnailDelay: thumbnailDelay ?? NeverSettles);
        return (vm, probe, thumbs);
    }

    /// <summary>
    /// Add ONE row and drive it to exactly the state the bug report starts from: probed, keyframes ready,
    /// both handles at their defaults — i.e. a freshly imported <b>no-op trim</b> row.
    /// </summary>
    private static async Task<BulkItemViewModel> AddRowAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path,
        double durationSeconds = 60, double stepSeconds = 2)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(durationSeconds), stepSeconds);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask; // deterministic: keyframes ready before anything is asserted
        return row;
    }

    /// <summary>Give a row a real, valid, non-degenerate trim (default 10s..50s of a 60s source).</summary>
    private static void SetRealCut(BulkItemViewModel row, double introSeconds = 10, double outroSeconds = 50)
    {
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        row.AddOutro(TimeSpan.FromSeconds(outroSeconds));
    }

    /// <summary>Records every <c>PropertyChanged</c> name raised by one source, in order.</summary>
    private sealed class Recorder
    {
        private readonly List<string> _names = new();

        public Recorder(INotifyPropertyChanged source) =>
            source.PropertyChanged += (_, e) => _names.Add(e.PropertyName ?? string.Empty);

        public IReadOnlyList<string> Names => _names;

        public int Count(string name) => _names.Count(n => n == name);

        public void Reset() => _names.Clear();
    }

    /// <summary>
    /// The row renders a flag and a sentence for the same condition; they must never disagree. (The only
    /// path that could diverge is <c>ExclusionReason</c>'s defensive "still scanning" branch, which is
    /// unreachable: <c>IsAutoDisabled</c>'s non-load-failure term already requires KeyframesReady.)
    /// </summary>
    private static void ReasonAndFlagAgree(BulkItemViewModel row) =>
        row.IsExcludedDespiteBeingChecked.Should().Be(
            row.ExclusionReason is not null,
            "the flag the row renders on and the sentence it renders must describe the same state");

    // ---- The split itself is structural, not a convention -------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public void IntentIsWritable_AndEligibilityIsReadOnly()
    {
        // This is the assertion that fails outright against the PRE-FIX type: IsEnabled carried a public
        // setter back then, which is precisely what let the checkbox bind TwoWay to computed eligibility.
        var eligibility = typeof(BulkItemViewModel).GetProperty(
            nameof(BulkItemViewModel.IsEnabled), BindingFlags.Public | BindingFlags.Instance);
        var intent = typeof(BulkItemViewModel).GetProperty(
            nameof(BulkItemViewModel.IsCheckedByUser), BindingFlags.Public | BindingFlags.Instance);

        eligibility.Should().NotBeNull();
        eligibility!.CanWrite.Should().BeFalse(
            "IsEnabled is COMPUTED eligibility — a settable IsEnabled is what let the checkbox bind two-way to it");
        eligibility.SetMethod.Should().BeNull("there must be no way back to the conflated property");

        intent.Should().NotBeNull("the checkbox needs an intent property to bind to");
        intent!.CanRead.Should().BeTrue();
        intent.CanWrite.Should().BeTrue();
        intent.GetMethod!.IsPublic.Should().BeTrue();
        intent.SetMethod!.IsPublic.Should().BeTrue();
    }

    // ---- 1. The dead gesture ------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ClickingTheCheckboxOnAFreshlyImportedRow_Notifies_AndTheIntentSticks()
    {
        var (vm, probe, _) = Build();
        var row = await AddRowAsync(vm, probe, PathA);

        row.IsNoOpTrim.Should().BeTrue("a freshly imported row keeps the whole file — the bug's starting state");
        row.IsEnabled.Should().BeFalse("so it is not eligible, which is what the old binding rendered");

        // Arrange the box exactly as the user SAW it before the fix: the conflated getter answered false,
        // so the box rendered unticked and the click wrote TRUE.
        row.IsCheckedByUser = false;
        var recorder = new Recorder(row);

        row.IsCheckedByUser = true; // the click that used to be dead

        recorder.Count(nameof(BulkItemViewModel.IsCheckedByUser)).Should().Be(
            1, "the gesture must raise a change for the property the checkbox binds to — pre-fix it raised nothing at all");
        recorder.Count(nameof(BulkItemViewModel.IsEnabled)).Should().Be(
            1, "eligibility is derived from intent, so it must be re-announced whenever intent moves");
        recorder.Count(nameof(BulkItemViewModel.ExclusionReason)).Should().Be(1);
        recorder.Count(nameof(BulkItemViewModel.IsExcludedDespiteBeingChecked)).Should().Be(1);

        row.IsCheckedByUser.Should().BeTrue(
            "the bound property must READ BACK what was written — the pre-fix getter answered eligibility, "
            + "so the binding never converged on the value the click wrote and the gesture died");
        row.IsEnabled.Should().BeFalse(
            "ticking a no-op row expresses intent; it does not invent something to trim");

        // The gesture is repeatable in both directions and never latches.
        recorder.Reset();
        row.IsCheckedByUser = false;
        row.IsCheckedByUser.Should().BeFalse();
        recorder.Count(nameof(BulkItemViewModel.IsCheckedByUser)).Should().Be(1);

        // PERFORMANCE: a redundant write is coalesced, so a re-render that echoes the current value back
        // through the two-way binding costs nothing and cannot loop.
        recorder.Reset();
        row.IsCheckedByUser = false;
        recorder.Names.Should().BeEmpty("writing the value the property already holds must raise nothing");
    }

    // ---- 2. Intent survives ineligibility ------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task FreshRow_IsCheckedButNotEligible_AndBecomesEligibleWithNoFurtherGesture()
    {
        var (vm, probe, _) = Build();
        var row = await AddRowAsync(vm, probe, PathA);

        row.IsCheckedByUser.Should().BeTrue(
            "a freshly imported row starts ticked — the user's intent is to include everything they dropped in");
        row.IsEnabled.Should().BeFalse("there is nothing to trim yet, so the row is not eligible");
        row.IsExcludedDespiteBeingChecked.Should().BeTrue(
            "ticked AND excluded is exactly the state that used to be invisible");
        ReasonAndFlagAgree(row);

        var recorder = new Recorder(row);

        // No user gesture at all — only the cut changes.
        SetRealCut(row);

        row.IsCheckedByUser.Should().BeTrue("intent was never touched, so it must be exactly as the user left it");
        row.IsValidCut.Should().BeTrue();
        row.IsEnabled.Should().BeTrue(
            "the row joins the batch the moment it becomes eligible — the user does not have to click again");
        row.IsExcludedDespiteBeingChecked.Should().BeFalse();
        row.ExclusionReason.Should().BeNull();
        row.RowState.Should().Be(RowState.Ready);

        recorder.Count(nameof(BulkItemViewModel.IsEnabled)).Should().BeGreaterThan(
            0, "the eligibility flip must be announced, or the checkbox and the run count go stale");
        recorder.Count(nameof(BulkItemViewModel.IsCheckedByUser)).Should().Be(
            0, "setting a cut is not a checkbox gesture — it must never rewrite the user's intent");
    }

    // ---- 3. The poisoning trap ------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UntickedRow_StaysExcludedAfterARealCut_AndApplyToAllSkipsIt_UntilReTicked()
    {
        var (vm, probe, _) = Build();
        var source = await AddRowAsync(vm, probe, PathA);
        var victim = await AddRowAsync(vm, probe, PathB);
        SetRealCut(source, 10, 50); // tail = 60 - 50 = 10s

        // The user unticks the row ...
        victim.IsCheckedByUser = false;
        victim.IsEnabled.Should().BeFalse();

        // ... and then gives it a perfectly good cut of its own.
        SetRealCut(victim, 12, 48);

        victim.IsValidCut.Should().BeTrue("the cut itself is fine");
        victim.RowState.Should().Be(RowState.Ready);
        victim.IsEnabled.Should().BeFalse(
            "eligibility still respects intent — a row the user unticked stays out no matter how good its cut is");
        victim.ExclusionReason.Should().BeNull(
            "an unticked row is the user's own choice, not an exclusion the app has to explain");
        ReasonAndFlagAgree(victim);

        // Apply-to-all targets INTENT, so it skips the unticked row and leaves its cut alone.
        var first = vm.ApplyToAll(source);

        first.Should().NotBeNull();
        first!.AppliedCount.Should().Be(0, "the only other row is unticked, so there is nothing to apply to");
        first!.InvalidatedRows.Should().BeEmpty();
        victim.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(12), "the skipped row's own cut is untouched");

        // Re-ticking is the whole point: the row rejoins, with no other gesture and no re-import.
        victim.IsCheckedByUser = true;

        victim.IsEnabled.Should().BeTrue("re-ticking restores eligibility for a row whose cut is already valid");

        var second = vm.ApplyToAll(source);

        second!.AppliedCount.Should().Be(1, "the re-ticked row is a target again — this arc used to be unreachable");
        victim.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(10), "intro copied absolute");
        victim.HasOutro.Should().BeTrue();
        victim.OutroStart!.Requested.Should().Be(TimeSpan.FromSeconds(50), "outro copied FROM END (60 - 10)");
    }

    // ---- 4. ExclusionReason ---------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ExclusionReason_NamesTheNoOpCase_AndClearsOnceTheCutIsReal()
    {
        var (vm, probe, _) = Build();
        var row = await AddRowAsync(vm, probe, PathA);

        row.ExclusionReason.Should().Be(
            NothingToTrim,
            "a ticked row the app is excluding must say WHY, phrased as a state rather than an error");
        row.IsExcludedDespiteBeingChecked.Should().BeTrue();
        ReasonAndFlagAgree(row);

        SetRealCut(row);

        row.ExclusionReason.Should().BeNull("an eligible row has nothing to explain");
        row.IsExcludedDespiteBeingChecked.Should().BeFalse();
        ReasonAndFlagAgree(row);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ExclusionReason_IsNullForARowTheUserUnticked()
    {
        var (vm, probe, _) = Build();
        var row = await AddRowAsync(vm, probe, PathA);

        row.ExclusionReason.Should().Be(NothingToTrim, "ticked + no-op means the app owes an explanation");

        row.IsCheckedByUser = false;

        row.ExclusionReason.Should().BeNull(
            "unticking is a decision, not an exclusion — explaining why their own choice was honoured is noise");
        row.IsExcludedDespiteBeingChecked.Should().BeFalse();
        ReasonAndFlagAgree(row);

        row.IsCheckedByUser = true;

        row.ExclusionReason.Should().Be(NothingToTrim, "re-ticking brings the explanation back");
        ReasonAndFlagAgree(row);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ExclusionReason_IsNullWhileTheRowIsStillScanning_ThenNamesTheNoOp()
    {
        var (vm, probe, _) = Build();
        probe.SetUniform(PathA, TimeSpan.FromSeconds(60), 2);
        probe.GateEverything = true; // hold the keyframe scan open

        await vm.AddFilesAsync(new[] { PathA });
        var row = vm.Items.Single();

        row.KeyframesReady.Should().BeFalse("the scan is held open");
        row.IsCheckedByUser.Should().BeTrue();
        row.IsEnabled.Should().BeTrue(
            "a still-scanning row is not yet judged ineligible — CanRunBatch waits on it instead");
        row.ExclusionReason.Should().BeNull("still scanning is not an exclusion, it is just not an answer yet");
        row.IsExcludedDespiteBeingChecked.Should().BeFalse();
        ReasonAndFlagAgree(row);

        probe.ReleaseScans();
        await row.CurrentScanTask;

        row.KeyframesReady.Should().BeTrue();
        row.ExclusionReason.Should().Be(NothingToTrim, "once the scan lands the verdict is real and gets a sentence");
        ReasonAndFlagAgree(row);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task ExclusionReason_DistinguishesATooShortKeepFromAnUnreadableFile()
    {
        var (vm, probe, _) = Build();

        // A degenerate cut that is NOT out of range: intro 58s snaps to keyframe 58 of a 60s/2s-GOP file,
        // so both handles sit plainly inside the video and the kept span (2s) merely fails to exceed
        // MinKeptSpan (2s). Calling this "out of range" — as this test originally asserted — told the user
        // to fix something that was not wrong (T-127 review finding #2); the sentence names the real
        // problem and the number they have to beat.
        var degenerate = await AddRowAsync(vm, probe, PathA);
        degenerate.IntroEnd.Requested = TimeSpan.FromSeconds(58);

        degenerate.IsValidCut.Should().BeFalse();
        degenerate.IsNoOpTrim.Should().BeFalse();
        degenerate.RowState.Should().Be(RowState.Invalid);
        degenerate.ExclusionReason.Should().Contain("too close",
            "both handles are inside the file — what is wrong is the gap between them, not their range");
        degenerate.ExclusionReason.Should().Contain("2.0s", "it names the minimum the user has to clear");
        degenerate.ExclusionReason.Should().NotContain("out of range");
        ReasonAndFlagAgree(degenerate);

        // Unreadable source: the probe fails, so the row is auto-disabled with its own sentence.
        probe.FailProbePaths.Add(PathC);
        await vm.AddFilesAsync(new[] { PathC });
        var broken = vm.Items.Single(i => i.Path == PathC);

        broken.RowState.Should().Be(RowState.LoadFailed);
        broken.IsCheckedByUser.Should().BeTrue("intent is untouched by a probe failure");
        broken.IsEnabled.Should().BeFalse();
        broken.ExclusionReason.Should().Be("can't read this file");
        ReasonAndFlagAgree(broken);

        // Even here, unticking silences the explanation — the user has taken the row off the table.
        broken.IsCheckedByUser = false;
        broken.ExclusionReason.Should().BeNull();
        ReasonAndFlagAgree(broken);
    }

    // ---- 5. The batch projections count eligibility, not raw intent ----------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task CanRunBatch_AndRunLabel_CountEligibility_NotRawIntent()
    {
        var (vm, probe, _) = Build();
        var a = await AddRowAsync(vm, probe, PathA);
        await AddRowAsync(vm, probe, PathB);

        vm.Items.Should().OnlyContain(i => i.IsCheckedByUser, "every freshly imported row starts ticked");
        vm.RunLabel.Should().Be("Run bulk cut (0)", "but none of them has anything to trim yet");
        vm.CanRunBatch.Should().BeFalse("raw intent must never be mistaken for a runnable row");

        var recorder = new Recorder(vm);

        SetRealCut(a);

        vm.RunLabel.Should().Be("Run bulk cut (1)");
        vm.CanRunBatch.Should().BeTrue();

        recorder.Reset();
        a.IsCheckedByUser = false;

        vm.RunLabel.Should().Be("Run bulk cut (0)", "unticking removes an eligible row from the count");
        vm.CanRunBatch.Should().BeFalse();
        recorder.Count(nameof(BulkCutViewModel.RunLabel)).Should().BeGreaterThan(
            0, "the tab must re-project the run state when a row's intent changes, or the button label goes stale");
        recorder.Count(nameof(BulkCutViewModel.CanRunBatch)).Should().BeGreaterThan(0);

        a.IsCheckedByUser = true;

        vm.RunLabel.Should().Be("Run bulk cut (1)", "re-ticking a valid row puts it straight back in the batch");
        vm.CanRunBatch.Should().BeTrue();
    }

    // ---- 6. PERFORMANCE: toggling intent is pure VM state ---------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task TogglingIntent_TriggersNoKeyframeScan_AndNoFrameGrab()
    {
        // Immediate debounce seam so cut-point grabs really do reach the fake service — the ceiling below
        // therefore bounds WORK ACTUALLY DONE, not an artefact of a parked seam.
        var (vm, probe, thumbs) = Build(Immediate);
        var a = await AddRowAsync(vm, probe, PathA);
        var b = await AddRowAsync(vm, probe, PathB);
        var c = await AddRowAsync(vm, probe, PathC);
        SetRealCut(b);

        probe.GetKeyframesCallCount.Should().Be(3, "one keyframe scan per imported row, and no more");

        // The whole setup can only ever have REQUESTED this many cut-point frames: one intro grab per row
        // when its keyframes resolve (3), plus b's intro move and b's outro add (2). Latest-wins coalescing
        // can only push the real number lower, never higher.
        const int SetupGrabCeiling = 5;
        var scansBefore = probe.GetKeyframesCallCount;

        for (var i = 0; i < 60; i++)
        {
            var ticked = i % 2 == 0;
            a.IsCheckedByUser = ticked;
            b.IsCheckedByUser = !ticked;
            c.IsCheckedByUser = ticked;
        }

        a.IsCheckedByUser.Should().BeFalse("180 alternating writes land on the last one — the state is real, not ignored");
        b.IsCheckedByUser.Should().BeTrue();

        probe.GetKeyframesCallCount.Should().Be(
            scansBefore,
            "toggling a checkbox is pure VM state — it must never re-scan keyframes (180 toggles would mean 180 ffprobe runs)");
        probe.PeakScans.Should().BeLessThanOrEqualTo(3, "the bounded scan gate is never re-entered by a toggle");
        thumbs.GetThumbnailCallCount.Should().BeLessThanOrEqualTo(
            SetupGrabCeiling,
            "intent carries no cut-point change, so no toggle may request a frame — the grab count stays at its import-time ceiling");
        thumbs.Requests.Should().OnlyContain(
            r => r.Width == BulkItemViewModel.ThumbnailWidth,
            "every grab that did happen came from the import path, at the row thumbnail width");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task TogglingIntent_NotifiesOnlyTheIntentProjection_NeverTheCutRecomputePipeline()
    {
        var (vm, probe, _) = Build();
        var row = await AddRowAsync(vm, probe, PathA);
        SetRealCut(row); // eligibility now genuinely flips with intent

        var introBefore = row.IntroEnd.Requested;
        var snappedBefore = row.IntroEnd.Snapped;
        var recorder = new Recorder(row);

        const int Toggles = 20;
        for (var i = 0; i < Toggles; i++)
        {
            row.IsCheckedByUser = i % 2 != 0; // starts ticked, so every write is a real change
        }

        // O(1) notifications per toggle means O(N) overall; a recompute fan-out would be O(N x properties).
        recorder.Count(nameof(BulkItemViewModel.IsCheckedByUser)).Should().Be(Toggles);
        recorder.Count(nameof(BulkItemViewModel.IsEnabled)).Should().Be(Toggles);
        recorder.Count(nameof(BulkItemViewModel.ExclusionReason)).Should().Be(Toggles);
        recorder.Count(nameof(BulkItemViewModel.IsExcludedDespiteBeingChecked)).Should().Be(Toggles);
        recorder.Names.Should().HaveCount(
            4 * Toggles,
            "exactly four notifications per toggle — the intent projection and nothing else");

        // The heavy, recomputed side of the row must not be touched at all.
        recorder.Count(nameof(BulkItemViewModel.RowState)).Should().Be(0);
        recorder.Count(nameof(BulkItemViewModel.IsValidCut)).Should().Be(0);
        recorder.Count(nameof(BulkItemViewModel.IsNoOpTrim)).Should().Be(0);
        recorder.Count(nameof(BulkItemViewModel.KeptDuration)).Should().Be(0);
        recorder.Count(nameof(BulkItemViewModel.KeyframesReady)).Should().Be(0);
        recorder.Count(nameof(BulkItemViewModel.Warning)).Should().Be(0);
        recorder.Count(nameof(BulkItemViewModel.OutputPath)).Should().Be(0);

        row.IntroEnd.Requested.Should().Be(introBefore, "no toggle may move a cut handle");
        row.IntroEnd.Snapped.Should().Be(snappedBefore);
        row.IsValidCut.Should().BeTrue("the cut is exactly as it was before the toggling");
    }

    // ---- Review fixes: eligibility must follow the cut point the ENGINE will use ----------------

    // Review finding #1. Eligibility used to be computed from IntroEnd.Snapped, but BuildBulkTrimItem
    // hands the engine IntroEnd.Requested, and under Exact cut the engine honours it. On a coarse GOP a
    // 3s intro snaps back to 0, so the row was judged a no-op and auto-excluded — and after T-127 it was
    // told "nothing to trim yet" for a trim Exact mode performs perfectly well. The exclusion was silent
    // before the fix; the fix turned it into a false instruction the user cannot obey.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task UnderExactCut_ARowWhoseRequestSnapsBackToZero_IsStillEligible()
    {
        var (vm, probe, _) = Build();
        // An 8s GOP: 3s is nearest to keyframe 0, so Snapped == 0 while Requested stays 3s.
        var row = await AddRowAsync(vm, probe, PathA, durationSeconds: 120, stepSeconds: 8);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(3);

        row.IntroEnd.Snapped.Should().Be(TimeSpan.Zero, "precondition: 3s snaps back to the 0s keyframe");

        // Lossless: the copy really would start at 0, so nothing is trimmed — excluding it is CORRECT.
        row.IsNoOpTrim.Should().BeTrue("on the lossless path this cut genuinely removes nothing");
        row.IsEnabled.Should().BeFalse();
        row.ExclusionReason.Should().Be(NothingToTrim);

        vm.ExactCut = true;

        // Exact: the engine cuts at 3s, so the row DOES trim and must be eligible — and must not be
        // told to set an intro it has already set.
        row.IsNoOpTrim.Should().BeFalse("under Exact the cut lands at the requested 3s, which is a real trim");
        row.IsValidCut.Should().BeTrue();
        row.IsEnabled.Should().BeTrue("a row Exact mode can cut must not be excluded from the batch");
        row.ExclusionReason.Should().BeNull("telling the user to set an intro they already set is a falsehood");
        row.IsExcludedDespiteBeingChecked.Should().BeFalse();
        vm.CanRunBatch.Should().BeTrue();
        vm.RunLabel.Should().Contain("(1)");

        vm.ExactCut = false;

        row.IsEnabled.Should().BeFalse("flipping back re-applies the lossless verdict");
        row.ExclusionReason.Should().Be(NothingToTrim);
    }

    // The precision flip has to RE-EVALUATE eligibility, not just repaint the warning: SetExactCut used to
    // raise only Warning, so IsEnabled / ExclusionReason went stale on the bound row.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task FlippingExactCut_RaisesTheEligibilityProjection_NotJustTheWarning()
    {
        var (vm, probe, thumbs) = Build();
        var row = await AddRowAsync(vm, probe, PathA, durationSeconds: 120, stepSeconds: 8);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(3);

        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        var scansBefore = probe.GetKeyframesCallCount;
        var grabsBefore = thumbs.GetThumbnailCallCount;

        vm.ExactCut = true;

        raised.Should().Contain(nameof(BulkItemViewModel.IsEnabled));
        raised.Should().Contain(nameof(BulkItemViewModel.ExclusionReason));
        raised.Should().Contain(nameof(BulkItemViewModel.IsNoOpTrim));

        // PERFORMANCE: the flip is pure VM arithmetic over values already in hand.
        probe.GetKeyframesCallCount.Should().Be(scansBefore, "changing precision must never re-scan keyframes");
        thumbs.GetThumbnailCallCount.Should().Be(grabsBefore, "changing precision must never re-grab frames");
    }

    // Review finding #4. Every other mutator of the eligibility inputs funnels through RecomputeAll;
    // the scan-START path did not. Restarting a scan flips KeyframesReady, which flips IsAutoDisabled,
    // so IsEnabled and ExclusionReason changed with nothing pushed to the view — a stale "nothing to
    // trim yet" line sitting on a row whose verdict had been withdrawn.
    [Fact]
    [Trait("serves-spec", "SPEC-011")]
    public async Task RestartingTheKeyframeScan_RepublishesTheEligibilityProjection()
    {
        var (vm, probe, _) = Build();
        var row = await AddRowAsync(vm, probe, PathA);

        row.ExclusionReason.Should().Be(NothingToTrim, "precondition: a settled, ticked, no-op row");

        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        probe.GateEverything = true; // hold the restarted scan open
        var restart = row.StartKeyframeScanAsync();

        row.KeyframesReady.Should().BeFalse("the restarted scan withdrew the verdict");
        row.ExclusionReason.Should().BeNull("a row being re-scanned is not excluded — it has no answer yet");
        raised.Should().Contain(nameof(BulkItemViewModel.IsEnabled),
            "the withdrawn verdict must reach the view, or the old reason line stays on screen stale");
        raised.Should().Contain(nameof(BulkItemViewModel.ExclusionReason));

        probe.ReleaseScans();
        await restart;
        row.ExclusionReason.Should().Be(NothingToTrim, "and the verdict comes back when the scan lands");
    }
}
