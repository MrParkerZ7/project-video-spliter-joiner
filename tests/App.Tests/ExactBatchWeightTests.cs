using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-191 (G-059, SPEC-011 I165) - the batch bar, the taskbar and the "~Ns left" under an Exact batch. Before this ticket every row was
/// weighed by its kept duration, so an Exact row with a short kept range barely moved the batch bar while its head encode ran (the
/// head costs far more per second than the copy): the row bar moved while the batch bar and the time left sat still, the frozen look
/// the ticket removes. A row Exact really cuts now weighs the <see cref="SmartCutCost"/> (<c>h·k + c</c>) of
/// <see cref="SmartCutPlanner.Plan"/> on its own keyframes - the same function the engine splits the row's own fraction by - and every
/// other row keeps its kept duration. <c>WeightedOverall</c> (SPEC-011 I41) is unchanged.
/// </summary>
public sealed class ExactBatchWeightTests : IDisposable
{
    private static readonly double K = SmartCutEngine.EncodeCostFactor;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-t191b-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private string MakeVideo(string name)
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "video bytes");
        return p;
    }

    private static Task Immediate(TimeSpan _, CancellationToken ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeBulkTrimEngine Engine) Build(bool exact)
    {
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), engine,
            thumbnailDebounce: TimeSpan.FromMilliseconds(1), thumbnailDelay: Immediate);
        vm.LookupFileHolders = _ => Array.Empty<string>();
        vm.ExactCut = exact;
        return (vm, probe, engine);
    }

    /// <summary>Add a row of <paramref name="durationSeconds"/> with a keyframe every <paramref name="step"/> s, cut from <paramref name="intro"/> (to <paramref name="outro"/>).</summary>
    private async Task<BulkItemViewModel> AddAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string name, double durationSeconds, double intro, double? outro = null,
        string? videoCodec = null, double step = 2)
    {
        var path = MakeVideo(name);
        probe.SetUniform(path, S(durationSeconds), step);
        if (videoCodec is not null)
        {
            probe.SetCodecs(path, videoCodec);
        }

        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask;
        row.IntroEnd.Requested = S(intro);
        if (outro is { } o)
        {
            row.AddOutro(S(o));
        }

        return row;
    }

    private static double Expected(BulkItemViewModel row, double? outro) =>
        SmartCutCost.Of(
            SmartCutPlanner.Plan(row.IntroEnd.Requested, outro is { } o ? S(o) : null, row.Keyframes),
            row.Duration!.Value).Total;

    // ---- The weight of a row Exact really cuts --------------------------------------------------------------------------------

    // serves-spec: SPEC-011#I165 - a row Exact cuts weighs h·k + c of SmartCutPlanner.Plan on its own keyframes: h·k + c for a mid-GOP
    // intro, c for an intro on a keyframe AND for one 5 ms from a keyframe (the planner's 10 ms tolerance - there is no second search
    // that could disagree), and h·k for a range with no keyframe inside it. Every figure also equals the Core cost function's.
    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData(5.0, null, 1.0, 54.0)]      // mid-GOP: head [5, 6) re-encoded, [6, 60) copied
    [InlineData(5.0, 30.0, 1.0, 24.0)]      // the same with an outro: copy [6, 30)
    [InlineData(6.0, null, 0.0, 54.0)]      // on a keyframe: the lossless cut is exact, all copy
    [InlineData(6.005, null, 0.0, 53.995)]  // 5 ms past a keyframe: within the planner's tolerance, still a pure copy
    [InlineData(5.0, 5.5, 0.5, 0.0)]        // no keyframe inside [5, 5.5): re-encode the whole range
    public async Task AnExactRow_WeighsTheCostOfThePlannersPlan(double intro, double? outro, double reencoded, double copied)
    {
        var (vm, probe, _) = Build(exact: true);
        var row = await AddAsync(vm, probe, "a.mp4", 60, intro, outro);

        var weight = BulkCutViewModel.RowWeight(row);

        row.ExactCutApplies.Should().BeTrue();
        weight.Should().BeApproximately((reencoded * K) + copied, 1e-3, "h·k + c (a TimeSpan is whole ticks, times the factor)");
        weight.Should().BeApproximately(Expected(row, outro), 1e-9, "the Core cost function of SmartCutPlanner.Plan on the row's keyframes");
        row.ExactCutCost.Should().BeApproximately(weight, 1e-12);
    }

    // serves-spec: SPEC-011#I165 - a Lossless row weighs its kept duration, as before: the snapped cut's length in seconds.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ALosslessRow_StillWeighsItsKeptDuration()
    {
        var (vm, probe, _) = Build(exact: false);
        var row = await AddAsync(vm, probe, "a.mp4", 60, intro: 5);

        BulkCutViewModel.RowWeight(row).Should().Be(row.KeptDuration!.Value.TotalSeconds);
        row.KeptDuration!.Value.Should().Be(S(56), "5 s snaps to the earlier of the 4 s and 6 s keyframes, so 56 s are kept");
        row.ExactCutCost.Should().BeNull();
    }

    // serves-spec: SPEC-011#I165 / SPEC-011#I164 - a row Exact cannot cut (HEVC; a Matroska name) is cut by the lossless pass, so it keeps
    // its kept-duration weight even while Exact is on: the weight is the cost of the cut the run makes.
    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData("hevc", "phone.mp4")]
    [InlineData(null, "camera.mkv")]
    public async Task ARowExactCannotCut_KeepsItsKeptDurationWeight_EvenInExactMode(string? videoCodec, string name)
    {
        var (vm, probe, _) = Build(exact: true);
        var row = await AddAsync(vm, probe, name, 60, intro: 5, videoCodec: videoCodec);

        row.ExactUnavailableReason.Should().NotBeNull("precondition: Exact cannot cut this source");
        row.ExactCutApplies.Should().BeFalse();
        row.ExactCutCost.Should().BeNull();
        BulkCutViewModel.RowWeight(row).Should().Be(row.KeptDuration!.Value.TotalSeconds);
        row.KeptDuration!.Value.Should().Be(S(56), "judged on the snapped cut: 5 s snaps to the 4 s keyframe");
    }

    // ---- The batch bar while a head encodes -------------------------------------------------------------------------------------

    /// <summary>Runs every posted callback inline, in order, so the fan-out is observable mid-run (as in BulkCutViewModelTests).</summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    // serves-spec: SPEC-011#I165 / SPEC-011#I41 / SPEC-011#I42 - an Exact row with a short kept range [5 s, 20 s) followed by a long row
    // that is cut losslessly (HEVC, 2990 s kept): while the engine reports the Exact row's HEAD slice the batch bar rises strictly, and
    // by the end of the head it stands at the head's share of the batch's cost, h·k / (h·k + c + 2990) - far above the share its kept
    // duration alone (15 s of 3005) would give. The taskbar value is the same fraction; the time left is fed by it.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheBatchBar_RisesStrictlyThroughAnExactRowsHead_InProportionToItsCost()
    {
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try
        {
            var (vm, probe, engine) = Build(exact: true);
            var exactRow = await AddAsync(vm, probe, "a.mp4", 60, intro: 5, outro: 20);
            var losslessRow = await AddAsync(vm, probe, "b.mp4", 3000, intro: 10, videoCodec: "hevc");
            exactRow.ExactCutApplies.Should().BeTrue();
            losslessRow.ExactCutApplies.Should().BeFalse("precondition: HEVC is cut by the lossless pass");

            var cost = SmartCutCost.Of(SmartCutPlanner.Plan(S(5), S(20), exactRow.Keyframes), S(60)); // head 1 s, copy [6, 20) = 14 s
            var weights = new[] { BulkCutViewModel.RowWeight(exactRow), BulkCutViewModel.RowWeight(losslessRow) };
            weights[0].Should().BeApproximately((1 * K) + 14, 1e-9);
            weights[1].Should().Be(2990, "the kept duration: 3000 s from the keyframe at 10 s");
            var w = cost.ReencodeShare;

            // The engine reports the Exact row's head slice: five samples across [0, w].
            var headFractions = new[] { 0.2, 0.4, 0.6, 0.8, 1.0 }.Select(f => f * w).ToArray();
            engine.ProgressScript = headFractions
                .Select(f => new BulkTrimProgress(0, 2, "a.mp4", f, 0, BulkTrimPhase.Item))
                .ToArray();

            var seen = new List<double>();
            var etaDuringRun = new List<string?>();
            vm.Operation.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OperationViewModel.Progress))
                {
                    seen.Add(vm.Operation.Progress);
                }
                else if (e.PropertyName == nameof(OperationViewModel.EtaText) && vm.Operation.IsRunning)
                {
                    etaDuringRun.Add(vm.Operation.EtaText);
                }
            };

            await vm.RunBatchAsync();

            var head = seen.Take(headFractions.Length).ToList();
            head.Should().HaveCount(headFractions.Length, "one batch value per head sample");
            head.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems("the batch bar rises strictly while the head slice is reported");
            var total = weights.Sum();
            for (var i = 0; i < head.Count; i++)
            {
                head[i].Should().BeApproximately(weights[0] * headFractions[i] / total, 1e-9, $"sample {i}: Σ(wᵢ·fᵢ)/Σwᵢ");
            }

            var byKeptDuration = 15d * w / (15d + 2990d);
            head[^1].Should().BeGreaterThan(byKeptDuration * 3,
                "the head's slice moves the batch bar in proportion to its cost, not its 15 kept seconds: " + head[^1] + " against " + byKeptDuration);
            head[^1].Should().BeApproximately(((1 * K) + 0d) / total, 1e-9, "at the end of the head the batch stands at h·k / Σw");
            etaDuringRun.Should().Contain(t => t != null && t.StartsWith("~", StringComparison.Ordinal),
                "the time left is fed by the batch fraction once the head reports (not 'estimating…' for the whole head)");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
    }
}
