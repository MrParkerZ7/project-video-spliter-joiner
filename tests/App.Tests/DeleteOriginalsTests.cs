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

    private readonly HandleHoldingPlayer _player = new();

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

    /// <summary>
    /// Models the real hazard T-145 is about: the preview holds an OPEN HANDLE on whatever it opened, and
    /// only <see cref="Unload"/> releases it. <see cref="Stop"/> deliberately does not - that is the whole
    /// distinction the fix turns on, and a fake where Stop also released would let the bug back in.
    /// </summary>
    private sealed class HandleHoldingPlayer : VideoSplitJoiner.App.Media.IMediaPlayer
    {
        private FileStream? _handle;

        public int UnloadCalls { get; private set; }

        public int StopCalls { get; private set; }

        public string? HeldPath { get; private set; }

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        public void Open(string path)
        {
            Release();
            try
            {
                // FileShare.None: nothing else may delete it while this is held - exactly the refusal.
                _handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                HeldPath = path;
            }
            catch
            {
                _handle = null;
            }

            Duration = TimeSpan.FromSeconds(60);
            DurationAvailable?.Invoke(this, EventArgs.Empty);
        }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;

        public void Stop()
        {
            StopCalls++;
            IsPlaying = false;   // NOT a release - Stop halts playback only
        }

        public void Seek(TimeSpan t) => Position = t;

        public void Unload()
        {
            UnloadCalls++;
            Release();
            Duration = null;
        }

        public void StepFrame(int direction) { }

        private void Release()
        {
            try { _handle?.Dispose(); } catch { }
            _handle = null;
            HeldPath = null;
        }

        public event EventHandler? PositionChanged;

        public event EventHandler? DurationAvailable;

#pragma warning disable CS0067
        public event EventHandler? Seeked;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    private (BulkCutViewModel Vm, BulkFakeProbe Probe, RecordingDisposer Disposer, FakeBulkTrimEngine Engine) Build(
        bool withDisposer = true)
    {
        var probe = new BulkFakeProbe();
        var disposer = new RecordingDisposer();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), engine,
            player: _player,
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

    /// <summary>
    /// T-150 (SPEC-011 I118) — the window between confirming and sweeping.
    ///
    /// <para><see cref="IfTheOutputHasVanishedSinceTheRun_TheOriginalIsNotOffered"/> covers the gate
    /// BEFORE the dialog. This covers the gap AFTER it: the user says yes, and the trimmed file is gone
    /// by the time the sweep reaches that row — an antivirus quarantine, a sync client, a second app, a
    /// full disk finishing a flush. Without the in-loop re-check the app bins the original anyway, which
    /// is the only remaining copy, and reports it as a success.</para>
    ///
    /// <para>The confirmation hook IS the window: whatever it does happens after the decision and before
    /// the sweep, which is exactly the interleaving being tested.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task IfTheOutputVanishesBetweenConfirmingAndSweeping_TheOriginalSurvives()
    {
        var (vm, probe, disposer, _) = Build();
        var doomed = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("doomed.mp4")));
        var fine = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("fine.mp4")));

        vm.CanDeleteOriginals.Should().BeTrue("precondition: both rows are offered");

        // Yes — and in the same breath, one trimmed result disappears.
        vm.ConfirmDeleteOriginals = (_, _) =>
        {
            File.Delete(doomed.OutputPath);
            return true;
        };

        vm.DeleteOriginals();

        File.Exists(doomed.Path).Should().BeTrue(
            "its trimmed output was gone by sweep time, so the original was the ONLY copy left");
        disposer.Disposed.Should().NotContain(
            doomed.Path, "and it must never have reached the disposer at all");

        File.Exists(fine.Path).Should().BeFalse(
            "one row losing its output does not spare the rest — the sweep is per-row");
        disposer.Disposed.Should().Contain(fine.Path);
    }

    /// <summary>
    /// T-150 — the other half of the same re-check: the ORIGINAL vanishing in that window.
    ///
    /// <para>No data is at risk here (the file is already gone), but without this half the row is handed
    /// to the disposer and counted as binned — so the user is told N originals were removed when one of
    /// them was never there. A delete summary that overstates itself is exactly the report you cannot
    /// audit later.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task IfTheORIGINALVanishesBetweenConfirmingAndSweeping_ItIsNotCountedAsBinned()
    {
        var (vm, probe, disposer, _) = Build();
        var gone = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("gone.mp4")));
        var real = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("real.mp4")));

        vm.ConfirmDeleteOriginals = (_, _) =>
        {
            File.Delete(gone.Path);   // something else got there first
            return true;
        };

        vm.DeleteOriginals();

        disposer.Disposed.Should().NotContain(
            gone.Path, "there was nothing left to send to the Recycle Bin");
        disposer.Disposed.Should().Contain(real.Path);
        vm.Operation.ResultSummary.Should().Contain(
            "Sent 1", "the count must describe what was actually binned, not what was offered");
    }

    /// <summary>
    /// T-150 — the same window, but the output is truncated rather than deleted. A zero-byte file still
    /// EXISTS, so an existence-only re-check would sail past it and bin the original.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task IfTheOutputIsTruncatedBetweenConfirmingAndSweeping_TheOriginalSurvives()
    {
        var (vm, probe, disposer, _) = Build();
        var row = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));

        vm.ConfirmDeleteOriginals = (_, _) =>
        {
            File.WriteAllBytes(row.OutputPath, Array.Empty<byte>());
            return true;
        };

        vm.DeleteOriginals();

        File.Exists(row.Path).Should().BeTrue(
            "a zero-byte trim is not a trim — the original is still the only real copy");
        disposer.Disposed.Should().NotContain(row.Path);
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
        vm.Operation.ResultSummary.Should().Contain("Sent 1")
            .And.Contain("Still in use",
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

    // ---- T-145: the app must let go of the file before asking for it to be deleted ----------------

    /// <summary>
    /// The reported failure: the previewed row's original could not be binned because the PREVIEW was
    /// still holding it. The fake holds a real <c>FileShare.None</c> handle, so if the fix is removed
    /// this test fails the way the user's machine did rather than passing on a technicality.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ThePreviewedRowsOriginal_IsBinnedToo_NotRefusedBecauseWeHeldIt()
    {
        var (vm, probe, disposer, _) = Build();
        var a = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));
        var b = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("b.mp4")));

        vm.SelectedItem = a;
        _player.Open(a.Path);                       // the preview now holds a.mp4 open
        _player.HeldPath.Should().Be(a.Path, "precondition: the app really is holding the file");

        vm.DeleteOriginals();

        File.Exists(a.Path).Should().BeFalse(
            "the previewed original must go too - the app holding its own file is not the user's problem");
        File.Exists(b.Path).Should().BeFalse();
        disposer.Disposed.Should().HaveCount(2);
    }

    /// <summary>
    /// Stop() looks like it should be enough and is not: it halts playback but keeps the handle. This
    /// pins the distinction so a future "tidy-up" cannot swap Unload for Stop.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ItUnloadsThePlayer_NotMerelyStopsIt()
    {
        var (vm, probe, _, _) = Build();
        var a = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));
        vm.SelectedItem = a;
        _player.Open(a.Path);

        vm.DeleteOriginals();

        _player.UnloadCalls.Should().BeGreaterThan(
            0, "only Unload closes the media element and releases the handle");
        _player.HeldPath.Should().BeNull("and the handle really is gone by the time the sweep runs");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AfterDeleting_ThePreviewDoesNotStillPointAtABinnedFile()
    {
        var (vm, probe, _, _) = Build();
        var a = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));
        vm.SelectedItem = a;
        _player.Open(a.Path);

        vm.DeleteOriginals();

        vm.SelectedItem.Should().BeNull(
            "leaving a row selected invites the preview to re-open a file that no longer exists");
    }

    /// <summary>A lock we do NOT own still refuses, and the summary has to name which file.</summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AFileLockedBySomethingElse_IsNamedInTheSummary()
    {
        var (vm, probe, disposer, _) = Build();
        var a = MarkTrimmed(await AddAsync(vm, probe, MakeVideo("a.mp4")));
        MarkTrimmed(await AddAsync(vm, probe, MakeVideo("b.mp4")));
        disposer.Refuse.Add(a.Path);

        vm.DeleteOriginals();

        vm.Operation.ResultSummary.Should().Contain(
            Path.GetFileName(a.Path),
            "'1 could not be removed' leaves the user hunting for which of twelve files it was");
    }
}
