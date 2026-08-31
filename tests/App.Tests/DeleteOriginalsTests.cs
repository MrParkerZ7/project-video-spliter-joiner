using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Io;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-144 (SPEC-011) — "Delete originals": reclaim the space a finished batch left behind.
///
/// <para>This is the most destructive thing in the app, so almost every test here is about what it must
/// REFUSE to do. The gate is evaluated fresh on each read rather than remembered from the run, because
/// the run may be minutes old by the time the button is pressed.</para>
///
/// <para>The disposer is a recording fake. In production it is the Recycle Bin; a null disposer means the
/// feature is unavailable rather than "delete permanently", so a test that forgets to inject one cannot
/// bin anything real — the same defensive default that keeps Exact+ReplaceOriginal safe (T-130).</para>
/// </summary>
public sealed class DeleteOriginalsTests : IDisposable
{
    private readonly string _dir;

    public DeleteOriginalsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-del-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Records what it was asked to dispose of, and actually removes it so state is checkable.</summary>
    private sealed class RecordingDisposer : IOriginalDisposer
    {
        public List<string> Disposed { get; } = new();

        /// <summary>Paths this refuses to remove — stands in for a locked file.</summary>
        public HashSet<string> Refuse { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void DisposeOriginalBackup(string backupPath)
        {
            Disposed.Add(backupPath);
            if (Refuse.Contains(backupPath))
            {
                return; // best-effort by contract: it declines without throwing
            }

            try { File.Delete(backupPath); } catch { /* mirrors the real disposer */ }
        }
    }

    private (BulkCutViewModel Vm, BulkFakeProbe Probe, RecordingDisposer Disposer, FakeBulkTrimEngine Engine) Build(
        bool withDisposer = true)
    {
        var probe = new BulkFakeProbe();
        var disposer = new RecordingDisposer();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), engine,
            originalDisposer: withDisposer ? disposer : null);
        vm.ConfirmDeleteOriginals = (_, _) => true;
        return (vm, probe, disposer, engine);
    }

    private string MakeVideo(string name, int bytes = 4096)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    private async Task<BulkItemViewModel> AddAsync(BulkCutViewModel vm, BulkFakeProbe probe, string path)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(60), 1);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask;
        row.IntroEnd.Requested = TimeSpan.FromSeconds(5);
        return row;
    }

    /// <summary>Put a row in the state a finished batch leaves it in: Done, with a real output on disk.</summary>
    private BulkItemViewModel MarkTrimmed(BulkItemViewModel row, string? outputPath = null, bool writeOutput = true)
    {
        var output = outputPath ?? Path.Combine(_dir, Path.GetFileNameWithoutExtension(row.Path) + "_trimmed.mp4");
        if (writeOutput)
        {
            File.WriteAllBytes(output, new byte[2048]);
        }

        row.ApplyResult(new BulkTrimItemResult(
            row.BuildBulkTrimItem(), ItemOutcome.Done, output, null, Array.Empty<string>()));
        return row;
    }

    // ---- The gate ---------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task BeforeAnyRun_ItIsUnavailable()
    {
        var (vm, probe, _, _) = Build();
        await AddAsync(vm, probe, MakeVideo("a.mp4"));

        vm.CanDeleteOriginals.Should().BeFalse("nothing has been trimmed, so there is nothing safe to delete");
        vm.DeleteOriginalsCommand.CanExecute(null).Should().BeFalse();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AfterASuccessfulRow_ItOffersThatOriginal()
    {
        var (vm, probe, _, _) = Build();
        MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));

        vm.CanDeleteOriginals.Should().BeTrue();
        vm.DeletableOriginalCount.Should().Be(1);
        vm.DeleteOriginalsLabel.Should().Contain("1");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData(ItemOutcome.Failed)]
    [InlineData(ItemOutcome.Skipped)]
    [InlineData(ItemOutcome.Cancelled)]
    [InlineData(ItemOutcome.NotStarted)]
    public async Task ARowThatDidNotSucceed_IsNeverOffered(ItemOutcome outcome)
    {
        var (vm, probe, _, _) = Build();
        var row = await AddAsync(vm, probe, MakeVideo("a.mp4"));
        row.ApplyResult(new BulkTrimItemResult(row.BuildBulkTrimItem(), outcome, null, null, Array.Empty<string>()));

        vm.CanDeleteOriginals.Should().BeFalse(
            $"a {outcome} row produced no trusted output — deleting its source would lose the only copy");
    }

    /// <summary>
    /// The run may be minutes old. If the user moved or deleted the trimmed file in the meantime, the
    /// original is the only copy left and must not be touched.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task IfTheOutputHasVanishedSinceTheRun_TheOriginalIsNotOffered()
    {
        var (vm, probe, _, _) = Build();
        var row = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));
        vm.CanDeleteOriginals.Should().BeTrue("precondition");

        File.Delete(row.OutputPath);   // the user moved it away after the run

        vm.CanDeleteOriginals.Should().BeFalse(
            "the gate is re-evaluated at press time, not remembered from the run");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnEmptyOutputFile_IsNotTreatedAsASuccessfulTrim()
    {
        var (vm, probe, _, _) = Build();
        var row = await AddAsync(vm, probe, MakeVideo("a.mp4"));
        var output = Path.Combine(_dir, "empty_trimmed.mp4");
        File.WriteAllBytes(output, Array.Empty<byte>());
        MarkTrimmed(row, output, writeOutput: false);

        vm.CanDeleteOriginals.Should().BeFalse("a zero-byte output is not a trim, whatever the ledger says");
    }

    /// <summary>Under replace-originals the original IS the output — binning it destroys the only copy.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task WhenTheOutputIsTheOriginal_NothingIsOffered()
    {
        var (vm, probe, _, _) = Build();
        var path = MakeVideo("a.mp4");
        var row = await AddAsync(vm, probe, path);
        MarkTrimmed(row, outputPath: path, writeOutput: false);   // replace-originals shape

        vm.CanDeleteOriginals.Should().BeFalse(
            "replace-originals already consumed the original — there is no second copy to discard");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task WithNoDisposer_TheFeatureIsUnavailable_RatherThanDeletingPermanently()
    {
        var (vm, probe, _, _) = Build(withDisposer: false);
        MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));

        vm.CanDeleteOriginals.Should().BeFalse(
            "null must mean 'cannot delete', never 'delete with File.Delete' — a forgotten injection " +
            "should be inert, not destructive");
    }

    // ---- Doing it --------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ItBinsTheOriginals_AndLeavesTheTrimmedFilesAlone()
    {
        var (vm, probe, disposer, _) = Build();
        var a = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));
        var b = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("b.mp4")));

        vm.DeleteOriginals();

        disposer.Disposed.Should().BeEquivalentTo(new[] { a.Path, b.Path },
            "the ORIGINALS go, via the disposer — never File.Delete, so they stay recoverable");
        File.Exists(a.Path).Should().BeFalse();
        File.Exists(b.Path).Should().BeFalse();
        File.Exists(a.OutputPath).Should().BeTrue("the trimmed results are the whole point — they stay");
        File.Exists(b.OutputPath).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task DecliningTheConfirmation_DeletesNothing()
    {
        var (vm, probe, disposer, _) = Build();
        var a = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));
        vm.ConfirmDeleteOriginals = (_, _) => false;

        vm.DeleteOriginals();

        disposer.Disposed.Should().BeEmpty("No means no — nothing may be touched");
        File.Exists(a.Path).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheConfirmationIsToldTheCountAndTheBytes()
    {
        var (vm, probe, _, _) = Build();
        MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4", bytes: 5000)));
        MarkTrimmed(await AddAsync(vm, probe, MakeVideo("b.mp4", bytes: 5000)));

        var seenCount = 0;
        long seenBytes = 0;
        vm.ConfirmDeleteOriginals = (c, b) => { seenCount = c; seenBytes = b; return false; };

        vm.DeleteOriginals();

        seenCount.Should().Be(2);
        seenBytes.Should().Be(10000, "the prize is the space reclaimed, so the dialog has to state it");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task OneFileThatCannotBeBinned_DoesNotStopTheRest()
    {
        var (vm, probe, disposer, _) = Build();
        var a = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));
        var b = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("b.mp4")));
        disposer.Refuse.Add(a.Path);   // locked

        vm.DeleteOriginals();

        File.Exists(a.Path).Should().BeTrue("it refused, so it survives");
        File.Exists(b.Path).Should().BeFalse("and the others still went");
        vm.Operation.ResultSummary.Should().Contain("1").And.Contain("could not be removed",
            "a silent partial success would leave the user believing space was freed that was not");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ARowWhoseOriginalIsGone_CannotBeCutAgain()
    {
        var (vm, probe, _, _) = Build();
        var a = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));

        vm.DeleteOriginals();

        a.OriginalDeleted.Should().BeTrue();
        a.IsEnabled.Should().BeFalse("its source no longer exists, so it can never take part in a batch");
        vm.CanDeleteOriginals.Should().BeFalse("and it is not offered a second time");
    }

    // ---- Performance ------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ItCostsOneDisposerCallPerRow_AndNoReProbe()
    {
        var (vm, probe, disposer, _) = Build();
        for (var i = 0; i < 5; i++)
        {
            MarkTrimmed(await AddAsync(vm, probe, MakeVideo($"v{i}.mp4")));
        }

        var scansBefore = probe.GetKeyframesCallCount;

        vm.DeleteOriginals();

        disposer.Disposed.Should().HaveCount(5, "exactly one disposal per eligible row — no retries");
        probe.GetKeyframesCallCount.Should().Be(scansBefore, "deleting a file is not a reason to re-scan");
    }
}
