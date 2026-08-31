using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-134 (SPEC-011) — say what Run will do BEFORE it is pressed.
///
/// <para><b>Why this exists.</b> The information was already there — the button read
/// <c>Run bulk cut (N)</c>, and every excluded row has carried an
/// <see cref="BulkItemViewModel.ExclusionReason"/> since T-127 — and it still was not enough. A user
/// imported a batch, set one cut, pressed Run, got one file, and had to ask <i>"bulk is mean single or
/// what?"</i>. A count that silently equals 1 beside a list of 12 is the state that produced that.</para>
///
/// <para>T-133 removed the common cause; this makes the remaining cases legible. The reasons are read
/// VERBATIM off the rows, so the wording lives in exactly one place and these tests match on the row's own
/// text rather than restating it.</para>
/// </summary>
public sealed class RunScopeSummaryTests
{
    private static (BulkCutViewModel Vm, BulkFakeProbe Probe) Build()
    {
        var probe = new BulkFakeProbe();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(),
            new FakeBulkTrimEngine());
        return (vm, probe);
    }

    private static async Task<BulkItemViewModel> AddAsync(BulkCutViewModel vm, BulkFakeProbe probe, string path)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(600), 1);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask;
        return row;
    }

    // ---- Silent when there is nothing to explain ------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void AnEmptyList_SaysNothing()
    {
        var (vm, _) = Build();

        vm.RunScopeSummary.Should().BeNull();
        vm.RunScopeIsWarning.Should().BeFalse();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task WhenEveryRowWillRun_ItSaysNothing()
    {
        var (vm, probe) = Build();
        var a = await AddAsync(vm, probe, @"C:\v\a.mp4");
        var b = await AddAsync(vm, probe, @"C:\v\b.mp4");
        a.IntroEnd.Requested = TimeSpan.FromSeconds(30);
        b.IntroEnd.Requested = TimeSpan.FromSeconds(30);

        vm.RunLabel.Should().Contain("(2)");
        vm.RunScopeSummary.Should().BeNull(
            "a line that always shows becomes furniture — it must appear only when it has something to say");
        vm.RunScopeIsWarning.Should().BeFalse();
    }

    // ---- The reported state ----------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task OneCutInABatchOfThree_SaysSoBeforeRunIsPressed()
    {
        var (vm, probe) = Build();
        var a = await AddAsync(vm, probe, @"C:\v\a.mp4");
        await AddAsync(vm, probe, @"C:\v\b.mp4");
        await AddAsync(vm, probe, @"C:\v\c.mp4");
        a.IntroEnd.Requested = TimeSpan.FromSeconds(30);   // only this one has a cut

        var summary = vm.RunScopeSummary;

        summary.Should().NotBeNull("this is exactly the state that produced 'why did it cut only one file?'");
        summary.Should().Contain("1 of 3", "the shortfall is the point — the count alone was not enough");
        summary.Should().Contain(
            vm.Items[1].ExclusionReason!,
            "the reason is read verbatim off the row, so the wording lives in one place");
        vm.RunScopeIsWarning.Should().BeTrue("rows the user TICKED are being excluded — that is the surprising case");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ReasonsAreGrouped_WithCounts()
    {
        var (vm, probe) = Build();
        var a = await AddAsync(vm, probe, @"C:\v\a.mp4");
        await AddAsync(vm, probe, @"C:\v\b.mp4");
        await AddAsync(vm, probe, @"C:\v\c.mp4");
        await AddAsync(vm, probe, @"C:\v\d.mp4");
        a.IntroEnd.Requested = TimeSpan.FromSeconds(30);

        vm.RunScopeSummary.Should().Contain("3 ×", "three rows share one reason — list it once with a count");
    }

    // ---- A deliberate choice is not an alarm ------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task UntickedRows_AreCountedCalmly_NotWarned()
    {
        var (vm, probe) = Build();
        var a = await AddAsync(vm, probe, @"C:\v\a.mp4");
        var b = await AddAsync(vm, probe, @"C:\v\b.mp4");
        a.IntroEnd.Requested = TimeSpan.FromSeconds(30);
        b.IntroEnd.Requested = TimeSpan.FromSeconds(30);
        b.IsCheckedByUser = false;                         // the user's own decision

        vm.RunScopeSummary.Should().Contain("1 of 2").And.Contain("1 not ticked");
        vm.RunScopeIsWarning.Should().BeFalse(
            "alarming someone about a choice they just made teaches them to ignore the line");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnUntickedRow_ContributesNoReasonText_OnlyACount()
    {
        var (vm, probe) = Build();
        var a = await AddAsync(vm, probe, @"C:\v\a.mp4");
        var b = await AddAsync(vm, probe, @"C:\v\b.mp4");
        a.IntroEnd.Requested = TimeSpan.FromSeconds(30);
        b.IsCheckedByUser = false;

        b.ExclusionReason.Should().BeNull("T-127: unticking silences the explanation");
        vm.RunScopeSummary.Should().Contain("not ticked").And.NotContain("nothing to trim");
    }

    // ---- Still-scanning rows ----------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AStillScanningRow_IsNamedAsScanning_NotAsAProblem()
    {
        var (vm, probe) = Build();
        var a = await AddAsync(vm, probe, @"C:\v\a.mp4");
        a.IntroEnd.Requested = TimeSpan.FromSeconds(30);

        probe.SetUniform(@"C:\v\slow.mp4", TimeSpan.FromSeconds(600), 1);
        probe.GateEverything = true;                        // hold this row's scan open
        await vm.AddFilesAsync(new[] { @"C:\v\slow.mp4" });

        vm.RunScopeSummary.Should().Contain("still scanning", "a row mid-scan is not excluded — it is not ready yet");

        probe.ReleaseScans();
        await vm.Items.Single(i => i.Path == @"C:\v\slow.mp4").CurrentScanTask;

        vm.RunScopeSummary.Should().NotContain("still scanning", "and the wording follows the real state");
    }

    // ---- It stays fresh ----------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ItIsRePublished_OnTheSameSignalsAsRunLabel()
    {
        var (vm, probe) = Build();
        var a = await AddAsync(vm, probe, @"C:\v\a.mp4");
        await AddAsync(vm, probe, @"C:\v\b.mp4");

        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BulkCutViewModel.RunScopeSummary))
            {
                raised++;
            }
        };

        a.IntroEnd.Requested = TimeSpan.FromSeconds(30);

        raised.Should().BeGreaterThan(0, "a stale summary is worse than none — it would state a count that has moved");
    }

    // ---- Performance --------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ReadingTheSummary_TouchesNoDiskAndNoProbe()
    {
        var (vm, probe) = Build();
        var a = await AddAsync(vm, probe, @"C:\v\a.mp4");
        await AddAsync(vm, probe, @"C:\v\b.mp4");
        a.IntroEnd.Requested = TimeSpan.FromSeconds(30);

        var scansBefore = probe.GetKeyframesCallCount;
        for (var i = 0; i < 20; i++)
        {
            _ = vm.RunScopeSummary;
            _ = vm.RunScopeIsWarning;
        }

        probe.GetKeyframesCallCount.Should().Be(
            scansBefore, "it is a projection over rows already in memory — a bound TextBlock re-reads it freely");
    }
}
