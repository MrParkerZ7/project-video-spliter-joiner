using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// SPEC-002 I47–I53 — how <see cref="BulkTrimEngine"/> routes a row between the lossless split path and
/// the frame-exact <see cref="ISmartCutEngine"/> (T-125, epic G-042). The load-bearing properties are the
/// negative ones: Lossless must never reach the smart cutter, a null smart-cut engine must degrade to the
/// lossless path rather than fail, and a per-row <c>FellBack</c> must fall back FOR THAT ROW ONLY while
/// telling the user why.
/// </summary>
public sealed class CutPrecisionRoutingTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-precision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Records what it was asked to cut and replays a scripted result.</summary>
    private sealed class FakeSmartCutEngine : ISmartCutEngine
    {
        public int CallCount { get; private set; }

        public List<string> Inputs { get; } = new();

        public List<string> Outputs { get; } = new();

        public List<TimeSpan> Starts { get; } = new();

        /// <summary>Per-call result factory; default = a successful exact cut.</summary>
        public Func<string, SmartCutResult>? ResultFactory { get; set; }

        public Task<SmartCutResult> CutAsync(
            string inputPath, TimeSpan start, TimeSpan? end, string outputPath,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            CallCount++;
            Inputs.Add(inputPath);
            Outputs.Add(outputPath);
            Starts.Add(start);

            var result = ResultFactory?.Invoke(inputPath)
                ?? new SmartCutResult(outputPath, SmartCutStrategy.HeadReencode, false, null, TimeSpan.FromSeconds(3));
            return Task.FromResult(result);
        }
    }

    private static async Task<BatchResult> RunAsync(
        string dir, BulkTrimOptions opts, FakeSplitEngine split, FakeSmartCutEngine? smart, params string[] names)
    {
        var items = new List<BulkTrimItem>();
        foreach (var n in names)
        {
            var input = Path.Combine(dir, n);
            File.WriteAllText(input, "src");
            items.Add(new BulkTrimItem(input, TimeSpan.FromSeconds(5), null, Path.Combine(dir, "out_" + n), Tag: n));
        }

        var engine = new BulkTrimEngine(
            split, new FakeRequestBuilder(), new FakeDiskSpaceProbe(long.MaxValue), smart);
        return await engine.RunAsync(items, opts);
    }

    // ---- Lossless (the default) never reaches the smart cutter --------------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task Lossless_NeverInvokesTheSmartCutter()
    {
        var dir = NewDir();
        try
        {
            var split = new FakeSplitEngine();
            var smart = new FakeSmartCutEngine();

            var result = await RunAsync(dir, new BulkTrimOptions(), split, smart, "a.mp4", "b.mp4");

            smart.CallCount.Should().Be(0, "the default path must never pay for a re-encode");
            split.CallCount.Should().Be(2, "both rows took the ordinary lossless path");
            result.Outcome.Should().Be(BatchOutcome.Completed);
        }
        finally { Cleanup(dir); }
    }

    // ---- Exact routes to the smart cutter, bypassing the split engine -------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task Exact_RoutesEachRowToTheSmartCutter_AndSkipsTheSplitEngine()
    {
        var dir = NewDir();
        try
        {
            var split = new FakeSplitEngine();
            var smart = new FakeSmartCutEngine();

            var result = await RunAsync(
                dir, new BulkTrimOptions(Precision: CutPrecision.Exact), split, smart, "a.mp4", "b.mp4");

            smart.CallCount.Should().Be(2, "every row took the exact route");
            split.CallCount.Should().Be(0, "a successfully exact-cut row must not also run the lossless pass");
            smart.Starts.Should().AllBeEquivalentTo(TimeSpan.FromSeconds(5), "the requested time is passed through exactly");
            result.Outcome.Should().Be(BatchOutcome.Completed);
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task Exact_WritesToTheSameCollisionResolvedPath_AsTheLosslessPathWouldHave()
    {
        var dir = NewDir();
        try
        {
            var smart = new FakeSmartCutEngine();
            await RunAsync(dir, new BulkTrimOptions(Precision: CutPrecision.Exact), new FakeSplitEngine(), smart, "a.mp4");

            smart.Outputs.Should().ContainSingle()
                .Which.Should().Be(Path.GetFullPath(Path.Combine(dir, "out_a.mp4")),
                    "exact rows obey the same collision-resolved destination as everything else");
        }
        finally { Cleanup(dir); }
    }

    // ---- The fallbacks (the load-bearing negatives) --------------------------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ANullSmartCutEngine_DegradesToLossless_RatherThanFailing()
    {
        var dir = NewDir();
        try
        {
            var split = new FakeSplitEngine();

            var result = await RunAsync(
                dir, new BulkTrimOptions(Precision: CutPrecision.Exact), split, smart: null, "a.mp4");

            split.CallCount.Should().Be(1, "with no smart cutter available the row still gets cut, losslessly");
            result.Outcome.Should().Be(BatchOutcome.Completed);
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task AFellBackRow_RunsTheLosslessPath_AndSaysWhy()
    {
        var dir = NewDir();
        try
        {
            var split = new FakeSplitEngine();
            var smart = new FakeSmartCutEngine
            {
                ResultFactory = _ => new SmartCutResult(
                    null, SmartCutStrategy.HeadReencode, FellBack: true,
                    "no known encoder for video codec 'prores_raw_hq'", TimeSpan.Zero),
            };

            var result = await RunAsync(
                dir, new BulkTrimOptions(Precision: CutPrecision.Exact), split, smart, "a.mp4");

            smart.CallCount.Should().Be(1, "exact was attempted");
            split.CallCount.Should().Be(1, "and the row still got cut, via the lossless path");

            var row = result.Items.Single();
            row.Outcome.Should().Be(ItemOutcome.Done);
            row.Warnings.Should().ContainSingle()
                .Which.Should().Contain("exact cut unavailable").And.Contain("prores_raw_hq",
                    "the user is told why this row is not frame-exact, rather than silently getting a different result");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task FallbackIsPerRow_NotPerBatch()
    {
        var dir = NewDir();
        try
        {
            var split = new FakeSplitEngine();
            var smart = new FakeSmartCutEngine
            {
                // Only the middle file cannot be exact-cut.
                ResultFactory = input => Path.GetFileName(input) == "b.mp4"
                    ? new SmartCutResult(null, SmartCutStrategy.HeadReencode, true, "unsupported codec", TimeSpan.Zero)
                    : new SmartCutResult(input, SmartCutStrategy.HeadReencode, false, null, TimeSpan.FromSeconds(2)),
            };

            var result = await RunAsync(
                dir, new BulkTrimOptions(Precision: CutPrecision.Exact), split, smart, "a.mp4", "b.mp4", "c.mp4");

            smart.CallCount.Should().Be(3, "every row is attempted exactly");
            split.CallCount.Should().Be(1, "ONLY the one that fell back also ran the lossless path");
            result.Items.Count(r => r.Warnings.Count > 0).Should().Be(1, "one row explains itself; the others stay quiet");
            result.Outcome.Should().Be(BatchOutcome.Completed);
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task AnAlreadyOnKeyframeRow_FallsBackQuietly_WithNoWarningNoise()
    {
        var dir = NewDir();
        try
        {
            var split = new FakeSplitEngine();
            var smart = new FakeSmartCutEngine
            {
                // PureCopy = the lossless cut is already exact here; nothing to explain.
                ResultFactory = _ => new SmartCutResult(
                    null, SmartCutStrategy.PureCopy, FellBack: true,
                    "the requested time is already on a keyframe", TimeSpan.Zero),
            };

            var result = await RunAsync(
                dir, new BulkTrimOptions(Precision: CutPrecision.Exact), split, smart, "a.mp4");

            split.CallCount.Should().Be(1, "the row is cut losslessly, which is exact in this case");
            result.Items.Single().Warnings.Should().BeEmpty(
                "a cut that was already exact needs no explanation — warning only when exact was genuinely unavailable");
        }
        finally { Cleanup(dir); }
    }
}
