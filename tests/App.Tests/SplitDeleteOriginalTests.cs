using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Io;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-162 (G-052, SPEC-010) — the Split screen can reclaim its source file, and refuses whenever it
/// cannot prove every produced part survived.
///
/// <para>A split <b>multiplies</b> disk usage: cutting a 4 GB recording into six parts leaves 8 GB where
/// 4 GB was, and the source is exactly the file you no longer want. Bulk Cut could already reclaim that
/// space (G-050); Split could not.</para>
///
/// <para><b>The all-parts rule is what this file is really about.</b> Bulk Cut deletes one original per
/// row against one output. Split deletes one source that produced N parts, so the eligibility question
/// is different in kind: binning the source when part 4 of 6 is missing loses footage that exists
/// nowhere else. Every test below that asserts a REFUSAL is guarding that.</para>
/// </summary>
public sealed class SplitDeleteOriginalTests : IDisposable
{
    private readonly string _dir;

    public SplitDeleteOriginalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t162-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Make(string name, int bytes = 2048)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    /// <summary>Records what it was asked to bin, and actually removes it — so "did it go" is real.</summary>
    private sealed class RecordingDisposer : IOriginalDisposer
    {
        public List<string> Asked { get; } = new();

        /// <summary>When true the file is left in place, standing in for a locked file.</summary>
        public bool RefuseToDelete { get; set; }

        public void DisposeOriginalBackup(string backupPath)
        {
            Asked.Add(backupPath);
            if (!RefuseToDelete)
            {
                try { File.Delete(backupPath); } catch { /* mirrors the real best-effort contract */ }
            }
        }
    }

    /// <summary>A split engine that reports the segments the test wants, without touching ffmpeg.</summary>
    private sealed class ScriptedSplitEngine : ISplitEngine
    {
        private readonly IReadOnlyList<string> _outputs;

        public ScriptedSplitEngine(IReadOnlyList<string> outputs) => _outputs = outputs;

        public Task<SplitResult> SplitAsync(
            SplitRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<OperationStatus>? status = null,
            IProgress<PartProgress>? partProgress = null)
            => Task.FromResult(new SplitResult(
                _outputs.Select(p => new SplitSegment(p, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero)).ToList(),
                Array.Empty<string>()));
    }

    /// <summary>
    /// Load a source and drive a real split through the VM so <c>LastResult</c> is set the way the
    /// screen sets it — never by poking private state, which would test the test.
    /// </summary>
    private async Task<(SplitViewModel Vm, RecordingDisposer Disposer, string Source, string[] Parts)>
        SplitInto(int partCount, bool wireDisposer = true)
    {
        var source = Make("source.mp4", 8 * 1024 * 1024);
        var parts = Enumerable.Range(1, partCount).Select(i => Make($"part{i}.mp4")).ToArray();

        var disposer = new RecordingDisposer();
        var vm = new SplitViewModel(
            new BulkFakeProbe(), new ScriptedSplitEngine(parts), player: null, new FakeSettings(),
            originalDisposer: wireDisposer ? disposer : null);

        await vm.LoadAsync(source);
        vm.AddCutAtCommand.Execute(TimeSpan.FromSeconds(10));
        vm.OutputDir = _dir;
        await vm.RunSplitAsync();

        vm.LastResult.Should().NotBeNull("precondition — the split must have produced a result");
        return (vm, disposer, source, parts);
    }

    // ---- The all-parts rule ------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task AfterACleanSplit_TheSourceCanBeBinned()
    {
        var (vm, disposer, source, _) = await SplitInto(3);

        vm.CanDeleteOriginal.Should().BeTrue("every part is on disk and non-empty");
        vm.DeleteOriginalLabel.Should().Contain("source.mp4", "the label names what it will remove");

        vm.ConfirmDeleteOriginal = (_, _) => true;
        vm.DeleteOriginal();

        disposer.Asked.Should().ContainSingle().Which.Should().Be(source);
        File.Exists(source).Should().BeFalse("the source went to the Recycle Bin");
        vm.StatusText.Should().Contain("Recycle Bin");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task AMissingPartRefusesTheWholeDeletion()
    {
        // The invariant this epic hangs on. Part 2 of 3 vanished after the run; the source is the only
        // copy of the material that part was cut from, so nothing may be binned.
        var (vm, disposer, source, parts) = await SplitInto(3);
        File.Delete(parts[1]);

        vm.CanDeleteOriginal.Should().BeFalse("part 2 of 3 is gone — the source is the only copy left");

        vm.ConfirmDeleteOriginal = (_, _) => true;
        vm.DeleteOriginal();

        disposer.Asked.Should().BeEmpty("nothing may be binned when a part is missing");
        File.Exists(source).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task AZeroBytePartRefusesTheWholeDeletion()
    {
        // Present-but-empty is the sharper case: File.Exists says yes and the footage is still gone.
        var (vm, disposer, source, parts) = await SplitInto(3);
        File.WriteAllBytes(parts[2], Array.Empty<byte>());

        vm.CanDeleteOriginal.Should().BeFalse("a 0-byte part contains none of the footage it should");

        vm.ConfirmDeleteOriginal = (_, _) => true;
        vm.DeleteOriginal();

        disposer.Asked.Should().BeEmpty();
        File.Exists(source).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task EligibilityIsReCheckedAtDeletionTime_NotTrustedFromWhenTheButtonEnabled()
    {
        // T-150's window, not re-opened here: the parts were all present when the button enabled and
        // when the confirmation was shown, and one disappeared before the sweep ran.
        var (vm, disposer, source, parts) = await SplitInto(2);
        vm.CanDeleteOriginal.Should().BeTrue("precondition — eligible at the moment of the click");

        vm.ConfirmDeleteOriginal = (_, _) =>
        {
            File.Delete(parts[0]);   // the world moves while the dialog is up
            return true;
        };

        vm.DeleteOriginal();

        disposer.Asked.Should().BeEmpty("the re-check must catch what changed after the confirmation");
        File.Exists(source).Should().BeTrue();
        vm.StatusText.Should().Contain("no longer");
    }

    // ---- The gates ----------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task WithNoDisposerWired_NothingIsEverDeleted()
    {
        var (vm, _, source, _) = await SplitInto(2, wireDisposer: false);

        vm.CanDeleteOriginal.Should().BeFalse("an unwired host must not be able to delete a user's file");

        vm.ConfirmDeleteOriginal = (_, _) => true;
        vm.DeleteOriginal();

        File.Exists(source).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task TheConfirmationDefaultsToRefusing()
    {
        var (vm, disposer, source, _) = await SplitInto(2);

        // ConfirmDeleteOriginal deliberately NOT set — the default must decline.
        vm.DeleteOriginal();

        disposer.Asked.Should().BeEmpty("the default gate refuses, so an unwired prompt cannot delete");
        File.Exists(source).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task DecliningTheConfirmationDeletesNothing()
    {
        var (vm, disposer, source, _) = await SplitInto(2);
        vm.ConfirmDeleteOriginal = (_, _) => false;

        vm.DeleteOriginal();

        disposer.Asked.Should().BeEmpty();
        File.Exists(source).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void BeforeAnySplit_ThereIsNothingToReclaim()
    {
        var vm = new SplitViewModel(
            new BulkFakeProbe(), new ScriptedSplitEngine(Array.Empty<string>()), player: null,
            new FakeSettings(), originalDisposer: new RecordingDisposer());

        vm.CanDeleteOriginal.Should().BeFalse("no split has run");
        vm.DeleteOriginalLabel.Should().NotContain("·", "the label carries no count until it can act");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task ClearingTheScreenWithdrawsTheOffer()
    {
        var (vm, _, _, _) = await SplitInto(2);
        vm.CanDeleteOriginal.Should().BeTrue("precondition");

        vm.Clear();

        vm.CanDeleteOriginal.Should().BeFalse("there is no loaded source to reclaim any more");
    }

    // ---- Refusal is reported, never silent ----------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task ALockedSourceIsReportedWithItsHolder_NotSilentlySwallowed()
    {
        var (vm, disposer, source, _) = await SplitInto(2);
        disposer.RefuseToDelete = true;                       // stands in for a locked file
        vm.LookupFileHolders = _ => new[] { "ffmpeg.exe" };
        vm.ConfirmDeleteOriginal = (_, _) => true;

        vm.DeleteOriginal();

        File.Exists(source).Should().BeTrue("it could not be removed");
        vm.StatusText.Should().Contain("still in use");
        vm.StatusText.Should().Contain("ffmpeg.exe", "naming the holder is what makes this actionable");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task AThrowingHolderLookupNeverBreaksTheDeletion()
    {
        // A diagnostic must never be the reason a deletion path throws (T-155's rule).
        var (vm, disposer, source, _) = await SplitInto(2);
        disposer.RefuseToDelete = true;
        vm.LookupFileHolders = _ => throw new InvalidOperationException("Restart Manager said no");
        vm.ConfirmDeleteOriginal = (_, _) => true;

        var act = () => vm.DeleteOriginal();

        act.Should().NotThrow();
        vm.StatusText.Should().Contain("still in use");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task ADisposerThatThrowsIsReported_NotPropagated()
    {
        var (vm, _, source, _) = await SplitInto(2);
        var throwing = new ThrowingDisposer();
        var vm2 = await RebuildWith(throwing, source);

        vm2.ConfirmDeleteOriginal = (_, _) => true;
        var act = () => vm2.DeleteOriginal();

        act.Should().NotThrow("the disposer is best-effort by contract");
        File.Exists(source).Should().BeTrue();
        vm2.StatusText.Should().Contain("still in use");
    }

    private sealed class ThrowingDisposer : IOriginalDisposer
    {
        public void DisposeOriginalBackup(string backupPath) => throw new IOException("nope");
    }

    private async Task<SplitViewModel> RebuildWith(IOriginalDisposer disposer, string source)
    {
        var parts = new[] { Make("r1.mp4"), Make("r2.mp4") };
        var vm = new SplitViewModel(
            new BulkFakeProbe(), new ScriptedSplitEngine(parts), player: null, new FakeSettings(),
            originalDisposer: disposer);

        await vm.LoadAsync(source);
        vm.AddCutAtCommand.Execute(TimeSpan.FromSeconds(10));
        vm.OutputDir = _dir;
        await vm.RunSplitAsync();
        return vm;
    }

    // ---- The degenerate case Split can actually reach -----------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task APartWrittenOverTheSourceIsNeverBinned()
    {
        // Split's output dir defaults to the SOURCE's own folder (T-061), so a part landing on the
        // source's own path is reachable rather than theoretical — and binning it destroys the only copy.
        var source = Make("clip.mp4", 4096);
        var disposer = new RecordingDisposer();
        var vm = new SplitViewModel(
            new BulkFakeProbe(), new ScriptedSplitEngine(new[] { source }), player: null,
            new FakeSettings(), originalDisposer: disposer);

        await vm.LoadAsync(source);
        vm.AddCutAtCommand.Execute(TimeSpan.FromSeconds(10));
        vm.OutputDir = _dir;
        await vm.RunSplitAsync();

        vm.CanDeleteOriginal.Should().BeFalse("the only 'part' IS the source — deleting it deletes everything");

        vm.ConfirmDeleteOriginal = (_, _) => true;
        vm.DeleteOriginal();

        disposer.Asked.Should().BeEmpty();
        File.Exists(source).Should().BeTrue();
    }
}
