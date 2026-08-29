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
/// SPEC-002 — the interaction of <see cref="CutPrecision.Exact"/> with
/// <see cref="OutputMode.ReplaceOriginal"/>, found 2026-08-30 while auditing docs against code.
///
/// <para><b>The hazard.</b> Under ReplaceOriginal the resolved destination IS the input
/// (<c>ResolveCollision</c> returns the source path). <see cref="SmartCutEngine"/> finishes by
/// <c>File.Delete</c>-ing the destination and moving its result in. Combined, that hard-deletes the
/// user's original with no backup, no Recycle Bin and no restore-on-failure — bypassing the
/// verify-then-replace guarantee the lossless path gets from
/// <c>SplitEngine.ReplaceOriginalInPlace</c>, and losing the file outright if the move then fails.
/// Nothing in the ViewModel or Core coupled the two options, so both checkboxes were freely
/// combinable in the shipped v1.1.0.</para>
///
/// <para><b>The rule under test.</b> Replace-originals wins: the row takes the lossless path and the
/// smart cutter is never handed a destination that is its own source. The user is TOLD, reusing the
/// established fallback wording rather than silently changing what they asked for.</para>
/// </summary>
public sealed class ExactCutReplaceOriginalSafetyTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-exact-replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Records every destination it is asked to write to, so "never the source" is provable.</summary>
    private sealed class RecordingSmartCutEngine : ISmartCutEngine
    {
        public List<string> Outputs { get; } = new();

        public int CallCount => Outputs.Count;

        public Task<SmartCutResult> CutAsync(
            string inputPath, TimeSpan start, TimeSpan? end, string outputPath,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Outputs.Add(outputPath);
            return Task.FromResult(
                new SmartCutResult(outputPath, SmartCutStrategy.HeadReencode, false, null, TimeSpan.FromSeconds(3)));
        }
    }

    private static List<BulkTrimItem> Items(string dir, params string[] names)
    {
        var items = new List<BulkTrimItem>();
        foreach (var n in names)
        {
            var input = Path.Combine(dir, n);
            File.WriteAllText(input, "original-bytes");
            items.Add(new BulkTrimItem(input, TimeSpan.FromSeconds(5), null, Path.Combine(dir, "out_" + n), Tag: n));
        }

        return items;
    }

    private static BulkTrimEngine Engine(FakeSplitEngine split, RecordingSmartCutEngine smart) =>
        new(split, new FakeRequestBuilder(), new FakeDiskSpaceProbe(long.MaxValue), smart);

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ExactPlusReplaceOriginal_NeverHandsTheSmartCutterTheUsersOriginal()
    {
        var dir = NewDir();
        try
        {
            var split = new FakeSplitEngine();
            var smart = new RecordingSmartCutEngine();

            var result = await Engine(split, smart).RunAsync(
                Items(dir, "a.mp4", "b.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            smart.CallCount.Should().Be(0,
                "the smart cutter deletes its destination before moving the result in — handing it the " +
                "user's original would hard-delete the file with no backup and no Recycle Bin");
            smart.Outputs.Should().NotContain(Path.Combine(dir, "a.mp4"));
            split.CallCount.Should().Be(2, "both rows still get cut, via the safe lossless replace path");
            result.Outcome.Should().Be(BatchOutcome.Completed);
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task TheFallbackIsAnnounced_NotSilent()
    {
        var dir = NewDir();
        try
        {
            var result = await Engine(new FakeSplitEngine(), new RecordingSmartCutEngine()).RunAsync(
                Items(dir, "a.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            var warnings = result.Items[0].Warnings ?? Array.Empty<string>();
            warnings.Should().ContainSingle("the user asked for exact cutting and did not get it")
                .Which.Should().Contain("exact cut unavailable").And.Contain("replacing originals");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ExactStillRunsWhenTheDestinationIsNotTheSource()
    {
        var dir = NewDir();
        try
        {
            var split = new FakeSplitEngine();
            var smart = new RecordingSmartCutEngine();

            await Engine(split, smart).RunAsync(
                Items(dir, "a.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact)); // default = write beside the source

            smart.CallCount.Should().Be(1, "the guard is scoped to ReplaceOriginal — ordinary exact cuts are untouched");
            smart.Outputs.Single().Should().NotBe(Path.Combine(dir, "a.mp4"));
            split.CallCount.Should().Be(0, "a successful exact cut does not also run the lossless pass");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>Performance: the refused combination costs nothing extra — no exact attempt is even made.</summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task TheGuardCostsNoExtraWork()
    {
        var dir = NewDir();
        try
        {
            var split = new FakeSplitEngine();
            var smart = new RecordingSmartCutEngine();

            await Engine(split, smart).RunAsync(
                Items(dir, "a.mp4", "b.mp4", "c.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            smart.CallCount.Should().Be(0, "no wasted re-encode is started only to be discarded");
            split.CallCount.Should().Be(3, "exactly one lossless pass per row — O(N), no retry loop");
        }
        finally { Cleanup(dir); }
    }
}
