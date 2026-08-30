using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Io;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// SPEC-002 — the interaction of <see cref="CutPrecision.Exact"/> with
/// <see cref="OutputMode.ReplaceOriginal"/>, found 2026-08-30 while auditing docs against code.
///
/// <para><b>The hazard.</b> Under ReplaceOriginal the resolved destination IS the input
/// (<c>ResolveCollision</c> returns the source path). <see cref="SmartCutEngine"/> finishes by
/// <c>File.Delete</c>-ing the destination and moving its result in. Handed the original as its
/// destination, that hard-deletes the user's file with no backup, no Recycle Bin and no
/// restore-on-failure — bypassing the verify-then-replace guarantee the lossless path gets from
/// <c>SplitEngine.ReplaceOriginalInPlace</c>, and losing the file outright if the move then fails.
/// Nothing in the ViewModel or Core coupled the two options, so both checkboxes were freely
/// combinable in the shipped v1.1.0.</para>
///
/// <para><b>The rule under test — one invariant, two halves.</b> The invariant never moves: the smart
/// cutter is NEVER handed a destination that is its own source. What follows from it depends on whether
/// a SAFE SWAP is available — i.e. on whether an <see cref="IOriginalDisposer"/> was injected.</para>
///
/// <para><b>Half one — no disposer: the combination is refused (SPEC-002 I54/I55).</b> Without a
/// disposer there is no safe way to put a produced file where the original lives, so the row falls back
/// to the lossless cut and the user is TOLD, reusing the established fallback wording rather than
/// silently changing what they asked for. Covered by
/// <see cref="ExactPlusReplaceOriginal_NeverHandsTheSmartCutterTheUsersOriginal"/> (no exact attempt is
/// even started), <see cref="TheFallbackIsAnnounced_NotSilent"/> (the substitution is announced) and
/// <see cref="TheGuardCostsNoExtraWork"/> (the refusal is O(N) and discards no re-encode);
/// <see cref="ExactStillRunsWhenTheDestinationIsNotTheSource"/> pins its SCOPE — an ordinary exact cut
/// is untouched by the refusal.</para>
///
/// <para><b>Half two — disposer present: the combination is performed safely (T-130).</b> Exact cutting
/// runs, writing to a sibling <c>.vsj-exact</c> temp, and the produced file is then swapped onto the
/// original through <see cref="OriginalReplacer"/> — the same backup + restore-on-failure + disposer
/// machinery the lossless path uses. The remaining tests cover that half: the cut runs and the lossless
/// pass does not (<see cref="WithADisposer_ExactPlusReplaceOriginal_IsProducedExactly_WithoutTheLosslessPass"/>);
/// the destination handed over is still the temp, never the input
/// (<see cref="WithADisposer_TheSmartCutterIsStillNeverHandedTheUsersOriginal"/>); a successful swap
/// leaves the produced bytes in place and hands the backup over exactly once
/// (<see cref="AfterASuccessfulSwap_TheOriginalHoldsTheProducedBytes_AndTheBackupIsHandedOverOnce"/>)
/// with no temp left behind (<see cref="ASuccessfulRow_LeavesNoSiblingExactTempBehind"/>); a fell-back
/// attempt is swept and yields the destination to the lossless pass
/// (<see cref="WhenTheExactCutFallsBack_TheTempIsSwept_AndTheLosslessPassOwnsTheDestination"/>); a swap
/// that cannot complete leaves the original byte-identical and reports the row failed
/// (<see cref="AFailedSwap_LeavesTheOriginalByteIdentical_AndReportsTheRowFailed"/>); and the whole
/// thing costs one smart-cut call per row with no second ffmpeg pass — the swap is a file operation,
/// not a re-encode (<see cref="TheSwapCostsOneSmartCutPerRow_AndNoSecondFfmpegPass"/>).</para>
/// </summary>
public sealed class ExactCutReplaceOriginalSafetyTests
{
    /// <summary>Suffix of the sibling temp an exact cut writes to under ReplaceOriginal (mirrors <c>BulkTrimEngine</c>).</summary>
    private const string ExactTempSuffix = ".vsj-exact";

    /// <summary>Bytes <see cref="Items"/> puts in every source file — what "the original, untouched" means here.</summary>
    private const string OriginalBytes = "original-bytes";

    /// <summary>Bytes a successful exact cut produces, so "the produced file took the original's place" is checkable.</summary>
    private const string ExactBytes = "exact-cut-bytes";

    /// <summary>Bytes an exact attempt leaves behind before reporting FellBack — must never reach the original.</summary>
    private const string AbandonedExactBytes = "abandoned-exact-attempt";

    /// <summary>What <see cref="FakeSplitEngine"/> materialises — i.e. "the lossless pass owned the destination".</summary>
    private const string LosslessBytes = "trimmed";

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

    /// <summary>
    /// Records destinations like <see cref="RecordingSmartCutEngine"/> AND actually materialises the
    /// produced file, so the swap that follows is a real file operation over real bytes rather than a
    /// no-op over a path that does not exist. Optionally reports <c>FellBack</c> while still leaving its
    /// bytes on disk (the shape the engine's sweep exists for), and optionally HOLDS the produced file
    /// open with an exclusive <see cref="FileShare.None"/> handle — the induction
    /// <c>ReplaceOriginalSafetyTests.LockingFakeRunner</c> uses to fail a replace for real.
    /// </summary>
    private sealed class ProducingSmartCutEngine : ISmartCutEngine, IDisposable
    {
        private readonly bool _fellBack;
        private readonly bool _lockProduced;
        private readonly string _bytes;
        private readonly List<FileStream> _held = new();

        public ProducingSmartCutEngine(bool fellBack = false, bool lockProduced = false)
        {
            _fellBack = fellBack;
            _lockProduced = lockProduced;
            _bytes = fellBack ? AbandonedExactBytes : ExactBytes;
        }

        public List<string> Outputs { get; } = new();

        public int CallCount => Outputs.Count;

        public Task<SmartCutResult> CutAsync(
            string inputPath, TimeSpan start, TimeSpan? end, string outputPath,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Outputs.Add(outputPath);
            File.WriteAllText(outputPath, _bytes);

            if (_lockProduced)
            {
                _held.Add(new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.None));
            }

            return Task.FromResult(_fellBack
                ? new SmartCutResult(null, SmartCutStrategy.HeadReencode, true, "codecs cannot be reproduced", TimeSpan.Zero)
                : new SmartCutResult(outputPath, SmartCutStrategy.HeadReencode, false, null, TimeSpan.FromSeconds(3)));
        }

        public void Dispose()
        {
            foreach (var stream in _held)
            {
                try { stream.Dispose(); } catch { /* best-effort */ }
            }

            _held.Clear();
        }
    }

    /// <summary>Records every backup handed to it — and KEEPS the file, so recoverability stays checkable.</summary>
    private sealed class RecordingDisposer : IOriginalDisposer
    {
        public List<string> Disposed { get; } = new();

        public void DisposeOriginalBackup(string backupPath) => Disposed.Add(backupPath);
    }

    private static List<BulkTrimItem> Items(string dir, params string[] names)
    {
        var items = new List<BulkTrimItem>();
        foreach (var n in names)
        {
            var input = Path.Combine(dir, n);
            File.WriteAllText(input, OriginalBytes);
            items.Add(new BulkTrimItem(input, TimeSpan.FromSeconds(5), null, Path.Combine(dir, "out_" + n), Tag: n));
        }

        return items;
    }

    private static BulkTrimEngine Engine(FakeSplitEngine split, RecordingSmartCutEngine smart) =>
        new(split, new FakeRequestBuilder(), new FakeDiskSpaceProbe(long.MaxValue), smart);

    /// <summary>
    /// The engine as the app's composition root builds it (<c>BulkCutViewModel</c>): split engine,
    /// request builder, smart cutter AND the disposer that makes the swap safe. Exercising that exact
    /// 4-arg ctor is deliberate — the disposer arriving is the ONLY difference between the refusal half
    /// and the swap half, so the shipped wiring is what these tests must run through.
    /// </summary>
    private static BulkTrimEngine EngineWith(FakeSplitEngine split, ISmartCutEngine smart, IOriginalDisposer disposer) =>
        new(split, new FakeRequestBuilder(), smart, disposer);

    /// <summary>The sibling temp the engine must cut to when the destination is the user's original.</summary>
    private static string ExactTempFor(string originalPath) =>
        Path.GetFullPath(originalPath) + ExactTempSuffix + Path.GetExtension(originalPath);

    /// <summary>Every <c>.vsj-exact</c> temp still sitting in <paramref name="dir"/> — litter, if any survives.</summary>
    private static IReadOnlyList<string> StrayExactTemps(string dir) =>
        Directory.EnumerateFiles(dir).Where(p => p.Contains(ExactTempSuffix, StringComparison.Ordinal)).ToList();

    // ---- Half one: no disposer, so the combination is refused ----------------------------------

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

    // ---- Half two: a disposer is present, so the cut is performed and swapped in (T-130) -------

    /// <summary>
    /// The disposer turns the refusal into a safe swap: the user gets the exact cut they asked for, and
    /// the lossless pass — the fallback that used to stand in for it — is not run behind it.
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task WithADisposer_ExactPlusReplaceOriginal_IsProducedExactly_WithoutTheLosslessPass()
    {
        var dir = NewDir();
        var smart = new ProducingSmartCutEngine();
        try
        {
            var split = new FakeSplitEngine();
            var input = Path.Combine(dir, "a.mp4");

            var result = await EngineWith(split, smart, new RecordingDisposer()).RunAsync(
                Items(dir, "a.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            smart.CallCount.Should().Be(1,
                "a safe swap now exists (OriginalReplacer), so the combination is performed rather than refused");
            split.CallCount.Should().Be(0, "a successful exact cut does not also run the lossless pass");
            result.Items[0].Outcome.Should().Be(ItemOutcome.Done);
            result.Items[0].OutputPath.Should().Be(Path.GetFullPath(input),
                "the row's output IS the original path — that is what replacing originals means");
            result.Items[0].Warnings.Should().BeEmpty(
                "nothing was substituted for what the user asked for, so there is nothing to announce");
            result.Outcome.Should().Be(BatchOutcome.Completed);
        }
        finally { smart.Dispose(); Cleanup(dir); }
    }

    /// <summary>
    /// THE LOAD-BEARING ONE. Lifting the refusal must not lift the invariant behind it: the smart cutter
    /// still finishes with its own delete-then-move, so being handed the user's original as its
    /// destination would still hard-delete the file before anything replaced it. The cut goes to a
    /// sibling temp; putting those bytes onto the original is the swap's job, not the cutter's.
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task WithADisposer_TheSmartCutterIsStillNeverHandedTheUsersOriginal()
    {
        var dir = NewDir();
        var smart = new ProducingSmartCutEngine();
        try
        {
            var input = Path.Combine(dir, "a.mp4");

            await EngineWith(new FakeSplitEngine(), smart, new RecordingDisposer()).RunAsync(
                Items(dir, "a.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            var handed = smart.Outputs.Should().ContainSingle("the row took the exact path").Which;

            handed.Should().NotBe(Path.GetFullPath(input),
                "SmartCutEngine File.Delete()s its destination before moving its result in — handing it " +
                "the original would destroy the user's file with no backup and nothing yet to restore");
            handed.Should().Be(ExactTempFor(input),
                "the exact cut writes to a SIBLING TEMP; the produced file only reaches the original " +
                "through OriginalReplacer's backup + restore-on-failure machinery");
        }
        finally { smart.Dispose(); Cleanup(dir); }
    }

    /// <summary>
    /// The swap actually happened: the original path holds the PRODUCED bytes, and the pre-replacement
    /// copy was handed to the disposer exactly once — the recoverability seam the Recycle Bin hangs off.
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task AfterASuccessfulSwap_TheOriginalHoldsTheProducedBytes_AndTheBackupIsHandedOverOnce()
    {
        var dir = NewDir();
        var smart = new ProducingSmartCutEngine();
        try
        {
            var disposer = new RecordingDisposer();
            var input = Path.Combine(dir, "a.mp4");
            var backup = Path.GetFullPath(input) + OriginalReplacer.BackupSuffix;

            var result = await EngineWith(new FakeSplitEngine(), smart, disposer).RunAsync(
                Items(dir, "a.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            result.Items[0].Outcome.Should().Be(ItemOutcome.Done);
            File.ReadAllText(input).Should().Be(ExactBytes,
                "the exact cut's output genuinely took the original's place — a swap that reported success " +
                "but left the old bytes there would be the worst possible outcome");
            disposer.Disposed.Should().ContainSingle("one swap produces exactly one backup")
                .Which.Should().Be(backup);
            File.Exists(backup).Should().BeTrue("this disposer KEEPS the backup, so the original stays recoverable");
            File.ReadAllText(backup).Should().Be(OriginalBytes, "the backup is the user's original, byte for byte");
        }
        finally { smart.Dispose(); Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ASuccessfulRow_LeavesNoSiblingExactTempBehind()
    {
        var dir = NewDir();
        var smart = new ProducingSmartCutEngine();
        try
        {
            var input = Path.Combine(dir, "a.mp4");

            var result = await EngineWith(new FakeSplitEngine(), smart, new RecordingDisposer()).RunAsync(
                Items(dir, "a.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            result.Items[0].Outcome.Should().Be(ItemOutcome.Done, "precondition: the row completed through the swap");
            File.ReadAllText(input).Should().Be(ExactBytes, "precondition: the produced file did reach the original");

            File.Exists(ExactTempFor(input)).Should().BeFalse();
            StrayExactTemps(dir).Should().BeEmpty(
                "the produced file is CONSUMED by the swap (File.Replace moves it onto the original) — a " +
                "leftover .vsj-exact sitting beside the user's video is litter from a run that looked clean");
        }
        finally { smart.Dispose(); Cleanup(dir); }
    }

    /// <summary>
    /// The exact attempt bowed out (unreproducible codecs). Its sibling temp must be swept rather than
    /// left beside the user's video, and the destination — which IS the original — belongs to the
    /// lossless pass from then on. The abandoned attempt's bytes must never end up on the original.
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task WhenTheExactCutFallsBack_TheTempIsSwept_AndTheLosslessPassOwnsTheDestination()
    {
        var dir = NewDir();
        var smart = new ProducingSmartCutEngine(fellBack: true);
        try
        {
            var split = new FakeSplitEngine();
            var disposer = new RecordingDisposer();
            var input = Path.Combine(dir, "a.mp4");

            var result = await EngineWith(split, smart, disposer).RunAsync(
                Items(dir, "a.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            smart.Outputs.Should().ContainSingle("precondition: the exact attempt ran and wrote its temp")
                .Which.Should().Be(ExactTempFor(input));
            File.Exists(ExactTempFor(input)).Should().BeFalse("the abandoned attempt's temp is swept");
            StrayExactTemps(dir).Should().BeEmpty();

            split.CallCount.Should().Be(1, "the lossless pass owns the destination once exact cutting bowed out");
            File.ReadAllText(input).Should().Be(LosslessBytes, "the original path holds the LOSSLESS result");
            File.ReadAllText(input).Should().NotBe(AbandonedExactBytes,
                "an attempt that reported FellBack is not a result — its bytes must never reach the user's file");
            disposer.Disposed.Should().BeEmpty("no swap happened, so no backup was ever produced to hand over");
            result.Items[0].Outcome.Should().Be(ItemOutcome.Done, "the row still delivers a cut, just the lossless one");
        }
        finally { smart.Dispose(); Cleanup(dir); }
    }

    /// <summary>
    /// A swap that cannot complete must leave the user's file exactly as it was. The failure is induced
    /// FOR REAL — an exclusive <see cref="FileShare.None"/> handle on the produced file, the same idiom
    /// <c>ReplaceOriginalSafetyTests.LockingFakeRunner</c> uses for the lossless path: File.Replace hits
    /// a sharing violation (the exFAT/SMB shape), the rename-aside fallback moves the original to the
    /// backup, its move-into-place fails on the same lock, and the original is put back.
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task AFailedSwap_LeavesTheOriginalByteIdentical_AndReportsTheRowFailed()
    {
        var dir = NewDir();
        var smart = new ProducingSmartCutEngine(lockProduced: true);
        try
        {
            var split = new FakeSplitEngine();
            var disposer = new RecordingDisposer();
            var input = Path.Combine(dir, "a.mp4");
            var backup = Path.GetFullPath(input) + OriginalReplacer.BackupSuffix;

            var result = await EngineWith(split, smart, disposer).RunAsync(
                Items(dir, "a.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            smart.CallCount.Should().Be(1,
                "precondition: the exact cut DID produce a file, so the failure under test is the swap itself");

            File.ReadAllText(input).Should().Be(OriginalBytes,
                "the rename-aside backup is moved BACK over the original — a swap that cannot finish must " +
                "leave the user's master file byte-identical, never half-written and never missing");
            File.Exists(backup).Should().BeFalse("the restore leaves no stray .vsj-original behind");
            disposer.Disposed.Should().BeEmpty(
                "the disposer is only ever reached after a produced file took the original's place");

            result.Items[0].Outcome.Should().Be(ItemOutcome.Failed, "a swap that cannot complete is a failed row");
            result.Items[0].Error.Should().NotBeNull("the user is told the row failed, not left to discover it");
            result.Items[0].OutputPath.Should().BeNull("nothing took the original's place, so there is no output");
            result.Outcome.Should().Be(BatchOutcome.CompletedWithFailures);
            split.CallCount.Should().Be(0,
                "the failure is reported, not papered over by silently re-cutting down the lossless path");
        }
        finally { smart.Dispose(); Cleanup(dir); }
    }

    /// <summary>
    /// Performance: putting the produced file onto the original is a FILE operation, not a second
    /// encode. Exactly one smart-cut call per row (O(N), no retry loop) and no ffmpeg pass behind it.
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task TheSwapCostsOneSmartCutPerRow_AndNoSecondFfmpegPass()
    {
        var dir = NewDir();
        var smart = new ProducingSmartCutEngine();
        try
        {
            var split = new FakeSplitEngine();
            var disposer = new RecordingDisposer();
            var names = new[] { "a.mp4", "b.mp4", "c.mp4" };

            var result = await EngineWith(split, smart, disposer).RunAsync(
                Items(dir, names),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            smart.CallCount.Should().Be(3, "exactly one exact cut per row — O(N), no retry loop");
            split.CallCount.Should().Be(0,
                "the swap is a File.Replace, not a second ffmpeg pass over footage that was already cut");
            disposer.Disposed.Should().HaveCount(3, "one completed swap per row, so one backup per row");
            smart.Outputs.Should().OnlyHaveUniqueItems(
                "each row gets its OWN sibling temp — a shared scratch path would be a race between rows");
            smart.Outputs.Should().BeEquivalentTo(names.Select(n => ExactTempFor(Path.Combine(dir, n))));
            StrayExactTemps(dir).Should().BeEmpty("every temp was consumed by its swap");
            result.Outcome.Should().Be(BatchOutcome.Completed);
        }
        finally { smart.Dispose(); Cleanup(dir); }
    }

    // ---- The two defects T-130 introduced, and their fixes ---------------------------------------

    /// <summary>
    /// A fell-back exact cut under ReplaceOriginal must SAY SO. The first cut of T-130 put the
    /// temp-sweeping branch ahead of the warning branch in the same if/else chain, so this case returned
    /// before ever reaching the announcement: the user asked for a frame-exact cut, silently received a
    /// keyframe-snapped one, and it was written over their original. Silence is least acceptable in
    /// precisely the mode that destroys the source — both sibling paths (the no-disposer refusal and the
    /// ordinary write-beside-source route) announce the substitution, and so must this one.
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task AFellBackExactCut_ThatReplacedTheOriginal_IsAnnounced_NotSilent()
    {
        var dir = NewDir();
        var smart = new ProducingSmartCutEngine(fellBack: true);
        try
        {
            var result = await EngineWith(new FakeSplitEngine(), smart, new RecordingDisposer()).RunAsync(
                Items(dir, "a.mp4"),
                new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

            var warnings = result.Items[0].Warnings ?? Array.Empty<string>();

            warnings.Should().ContainSingle(
                "the substitution has to reach the user - this row overwrote their original with a cut " +
                "that is NOT the one they asked for")
                .Which.Should().Contain("exact cut unavailable")
                .And.Contain("cut snapped to the nearest keyframe");

            result.Items[0].Outcome.Should().Be(ItemOutcome.Done, "it still delivered a cut, just the lossless one");
        }
        finally { smart.Dispose(); Cleanup(dir); }
    }

    /// <summary>
    /// A swap that throws must not leave its sibling temp beside the user's video. T-130's first cut swept
    /// the temp only on the fell-back branch, so a failed <c>OriginalReplacer.Replace</c> unwound straight
    /// to the row's catch and left a <c>.vsj-exact</c> file in the user's folder permanently.
    ///
    /// <para>The failure is induced by locking the DESTINATION rather than the produced file — deliberately
    /// unlike <see cref="AFailedSwap_LeavesTheOriginalByteIdentical_AndReportsTheRowFailed"/>, which locks
    /// the produced file to drive the restore-on-failure branch. A lock on the produced file would also
    /// block the cleanup being asserted here, so that induction could never prove the sweep happens; this
    /// one fails <c>File.Replace</c> and the fallback's move-aside while leaving the temp deletable.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task AFailedSwap_LeavesNoSiblingExactTempBehind()
    {
        var dir = NewDir();
        var smart = new ProducingSmartCutEngine();
        try
        {
            var items = Items(dir, "a.mp4");
            var input = Path.Combine(dir, "a.mp4");

            BatchResult result;
            using (new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = await EngineWith(new FakeSplitEngine(), smart, new RecordingDisposer())
                    .RunAsync(items, new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));
            }

            result.Items[0].Outcome.Should().Be(ItemOutcome.Failed, "precondition: the swap really did fail");
            smart.CallCount.Should().Be(1, "precondition: the exact cut ran and produced its temp");

            File.Exists(ExactTempFor(input)).Should().BeFalse(
                "a failed swap must clean up after itself - the alternative is litter in the user's video folder");
            StrayExactTemps(dir).Should().BeEmpty();
            File.ReadAllText(input).Should().Be(OriginalBytes, "and the original is untouched, as ever");
        }
        finally { smart.Dispose(); Cleanup(dir); }
    }
}
