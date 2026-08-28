using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-125 (epic G-042) — the precision choice. Lossless stays the default and the app's identity; Exact
/// honours the time the user set by re-encoding ~1 GOP. The trade-off must be legible BEFORE the run,
/// and a row must stop advertising a keyframe offset that Exact mode will not produce.
/// </summary>
public sealed class ExactCutModeTests
{
    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeBulkTrimEngine Engine) Build()
    {
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), engine);
        return (vm, probe, engine);
    }

    private static async Task AddRowAsync(BulkCutViewModel vm, BulkFakeProbe probe, string path, double intro)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(60), 4); // the 4s grid from the bug report
        await vm.AddFilesAsync(new[] { path });
        vm.Items.Single(i => i.Path == path).IntroEnd.Requested = TimeSpan.FromSeconds(intro);
    }

    // ---- The default stays lossless -----------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void LosslessIsTheDefault_AndSaysSo()
    {
        var (vm, _, _) = Build();

        vm.ExactCut.Should().BeFalse("the lossless path is the app's identity and must stay the default");
        vm.PrecisionNote.Should().Contain("Lossless").And.Contain("keyframe");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheDefaultSendsLosslessToTheEngine()
    {
        var (vm, probe, engine) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 5);

        await vm.RunBatchAsync();

        engine.ReceivedOptions!.Precision.Should().Be(CutPrecision.Lossless);
    }

    // ---- Turning Exact on ---------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TurningOnExact_StatesTheCostUpFront()
    {
        var (vm, _, _) = Build();

        vm.ExactCut = true;

        vm.PrecisionNote.Should().Contain("Exact").And.Contain("re-encodes",
            "the user must see the cost before running, not discover it afterwards");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ExactMode_ReachesTheEngine()
    {
        var (vm, probe, engine) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 5);
        vm.ExactCut = true;

        await vm.RunBatchAsync();

        engine.ReceivedOptions!.Precision.Should().Be(CutPrecision.Exact);
    }

    // ---- The row stops advertising an offset that will not happen -----------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task UnderExact_TheRowStopsShowingASnapOffset()
    {
        var (vm, probe, _) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 5);
        var row = vm.Items.Single();

        // Lossless: 5s on a 4s grid snaps to 4s, and the row says so.
        row.IntroEnd.HasSnapNote.Should().BeTrue();
        row.IntroEnd.SnapNote.Should().Contain("00:04.0");

        vm.ExactCut = true;

        row.IntroEnd.HasSnapNote.Should().BeFalse(
            "under exact cutting the cut lands on 5s, so advertising a 4s keyframe would mislead");
        row.IntroEnd.SnapNote.Should().BeEmpty();
        row.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(5), "the request itself is untouched");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task SwitchingBackToLossless_RestoresTheSnapReadout()
    {
        var (vm, probe, _) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 5);
        var row = vm.Items.Single();

        vm.ExactCut = true;
        vm.ExactCut = false;

        row.IntroEnd.HasSnapNote.Should().BeTrue("the offset is real again under the lossless path");
        row.IntroEnd.SnapNote.Should().Contain("00:04.0");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task UnderExact_TheSnapMagnitudeWarningIsSuppressed()
    {
        var (vm, probe, _) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 5);
        var row = vm.Items.Single();

        (row.Warning ?? string.Empty).Should().Contain("cut moved", "lossless really does move the cut");

        vm.ExactCut = true;

        (row.Warning ?? string.Empty).Should().NotContain(
            "cut moved", "under exact cutting nothing moves, so the advisory would be false");
    }

    // ---- Performance: switching mode is free --------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task SwitchingPrecision_PerformsNoProbeWork()
    {
        var (vm, probe, _) = Build();
        await AddRowAsync(vm, probe, @"C:\v\a.mp4", 5);
        var before = probe.GetKeyframesCallCount;

        vm.ExactCut = true;
        vm.ExactCut = false;
        vm.ExactCut = true;

        probe.GetKeyframesCallCount.Should().Be(
            before, "flipping the precision toggle is pure VM state — it must never re-scan keyframes");
    }
}
