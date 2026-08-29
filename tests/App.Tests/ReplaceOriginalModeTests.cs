using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-123 (epic G-041) — the opt-in "replace originals" output mode. Because this is the one genuinely
/// destructive gesture in the app, the tests that matter most are the refusals: the default must be the
/// safe mode, and declining the confirmation must run absolutely nothing.
/// </summary>
public sealed class ReplaceOriginalModeTests
{
    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeBulkTrimEngine Engine) Build()
    {
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), engine);
        return (vm, probe, engine);
    }

    /// <summary>
    /// Records Open(path) / Unload / Stop / Play / Pause / Seek in call order — the I88 seam. Mirrors the
    /// private recorder in <c>BulkCutViewModelPreviewTests</c> / <c>BulkCutViewModelDebouncedPreviewTests</c>
    /// (each of those is private to its own class; duplicating it here keeps this file self-contained and
    /// needs no production change).
    /// </summary>
    private sealed class RecordingMediaPlayer : IMediaPlayer
    {
        /// <summary>Ordered op log, e.g. "Open", "Unload", "Stop".</summary>
        public List<string> Calls { get; } = new();

        /// <summary>Every path handed to <see cref="Open"/>, in order.</summary>
        public List<string> Opened { get; } = new();

        public int OpenCount => Opened.Count;

        public int UnloadCount => Calls.Count(c => c == "Unload");

        public int StopCount => Calls.Count(c => c == "Stop");

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        public void Open(string path)
        {
            Calls.Add("Open");
            Opened.Add(path);
            IsPlaying = false;
            Duration = null;
        }

        public void Play()
        {
            Calls.Add("Play");
            IsPlaying = true;
        }

        public void Pause()
        {
            Calls.Add("Pause");
            IsPlaying = false;
        }

        public void Stop()
        {
            Calls.Add("Stop");
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void Seek(TimeSpan t)
        {
            Calls.Add("Seek");
            Position = t;
        }

        public void Unload()
        {
            Calls.Add("Unload");
            Duration = null;
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void StepFrame(int direction) => Calls.Add("StepFrame");

#pragma warning disable CS0067 // Raised by the real player; the recorder never fires them.
        public event EventHandler? PositionChanged;

        public event EventHandler? Seeked;

        public event EventHandler? DurationAvailable;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    /// <summary>
    /// An immediate (non-parking) debounce seam for the T-115 preview-open, so the auto-selected row's
    /// open lands synchronously and the Stop/Unload assertions need no pump (mirrors the helper in
    /// <c>BulkCutViewModelPreviewTests</c>).
    /// </summary>
    private static Task Immediate(TimeSpan _, CancellationToken ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeBulkTrimEngine Engine, RecordingMediaPlayer Player)
        BuildWithPlayer()
    {
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();
        var player = new RecordingMediaPlayer();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), engine,
            player, selectionOpenDelay: Immediate);
        return (vm, probe, engine, player);
    }

    private static async Task AddValidRowAsync(BulkCutViewModel vm, BulkFakeProbe probe, string path)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(60), 2);
        await vm.AddFilesAsync(new[] { path });
        vm.Items.Single(i => i.Path == path).IntroEnd.Requested = TimeSpan.FromSeconds(10);
    }

    // ---- The safe default ---------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheDefaultIsTheNonDestructiveMode()
    {
        var (vm, _, _) = Build();

        vm.ReplaceOriginal.Should().BeFalse("a user who ignores this feature keeps every original");
        vm.CollisionIsInert.Should().BeFalse();
        vm.OutputNote.Should().Contain("_trimmed").And.Contain("originals kept");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TurningItOn_MakesTheCollisionControlInert_AndTheNoteTellsTheTruth()
    {
        var (vm, _, _) = Build();

        vm.ReplaceOriginal = true;

        vm.CollisionIsInert.Should().BeTrue("the collision policy cannot matter when the destination is the source");
        vm.OutputNote.Should().Contain("REPLACES").And.Contain("Recycle Bin",
            "the footer must state plainly what is about to happen — it can never contradict the mode");
    }

    // ---- The refusals (the load-bearing cases) -------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task DecliningTheConfirmation_RunsNothingAtAll()
    {
        var (vm, probe, engine) = Build();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");
        vm.ReplaceOriginal = true;

        var asked = 0;
        vm.ConfirmReplaceOriginals = _ => { asked++; return false; };

        await vm.RunBatchAsync();

        asked.Should().Be(1, "the user is asked exactly once, before anything runs");
        engine.CallCount.Should().Be(0, "declining performs ZERO engine calls — nothing is trimmed or replaced");
        vm.BatchState.Should().NotBe(BulkBatchState.Completed, "a declined batch never reports success");
    }

    /// <summary>
    /// The ORDERING half of the confirmation contract: the prompt is reached BEFORE the preview handle is
    /// released, before the batch enters Preparing, and before a single item is built. Declining therefore
    /// leaves every visible readout exactly where it was — not merely "not Completed".
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task DecliningLeavesTheBatchUntouched_TheConfirmationPrecedesEverything()
    {
        var (vm, probe, engine, player) = BuildWithPlayer();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4"); // auto-selected -> opened in the shared player
        player.OpenCount.Should().Be(1, "precondition: the row's file is open in the shared preview");
        vm.Items.Single().RowState.Should().Be(RowState.Ready, "precondition: the row is runnable");

        vm.ReplaceOriginal = true;
        var asked = 0;
        vm.ConfirmReplaceOriginals = _ => { asked++; return false; };

        await vm.RunBatchAsync();

        asked.Should().Be(1, "precondition: the prompt was reached");
        vm.BatchState.Should().Be(BulkBatchState.Idle, "the prompt precedes the Preparing state");
        vm.Items.Single().RowState.Should().Be(
            RowState.Ready, "no row was marked Queued — the confirmation precedes any item build");
        vm.Operation.State.Should().Be(OperationState.Idle, "the aggregate operation never started");
        player.UnloadCount.Should().Be(
            0, "the preview handle is released only AFTER the user accepts — the prompt comes first");
        player.StopCount.Should().Be(0, "a declined run does not touch the preview transport at all");
        engine.CallCount.Should().Be(0);
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheConfirmationIsCounted_SoTheUserKnowsTheBlastRadius()
    {
        var (vm, probe, engine) = Build();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");
        await AddValidRowAsync(vm, probe, @"C:\v\b.mp4");
        await AddValidRowAsync(vm, probe, @"C:\v\c.mp4");
        vm.ReplaceOriginal = true;

        var reported = -1;
        vm.ConfirmReplaceOriginals = n => { reported = n; return false; };

        await vm.RunBatchAsync();

        reported.Should().Be(3, "the prompt names how many originals are at risk");
        engine.CallCount.Should().Be(0);
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnUnwiredHost_CannotDestroyAnything_TheDefaultRefuses()
    {
        var (vm, probe, engine) = Build();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");
        vm.ReplaceOriginal = true;

        // Deliberately do NOT wire ConfirmReplaceOriginals — the VM default must refuse.
        await vm.RunBatchAsync();

        engine.CallCount.Should().Be(0, "a host that forgot to supply a prompt must never silently replace masters");
    }

    // ---- Accepting: the mode actually reaches the engine ---------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task Accepting_PassesReplaceOriginalToTheEngine()
    {
        var (vm, probe, engine) = Build();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");
        vm.ReplaceOriginal = true;
        vm.ConfirmReplaceOriginals = _ => true;

        await vm.RunBatchAsync();

        engine.CallCount.Should().Be(1);
        engine.ReceivedOptions!.Output.Should().Be(OutputMode.ReplaceOriginal);
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheDefaultModeNeverPrompts_AndSendsNewFile()
    {
        var (vm, probe, engine) = Build();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");

        var asked = 0;
        vm.ConfirmReplaceOriginals = _ => { asked++; return true; };

        await vm.RunBatchAsync();

        asked.Should().Be(0, "the safe mode must not nag");
        engine.ReceivedOptions!.Output.Should().Be(OutputMode.NewFile);
    }

    // ---- The preview file handle: Unload, not Stop ---------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ReplaceOriginalRun_UnloadsThePreview_SoTheFileHandleIsReleased()
    {
        var (vm, probe, engine, player) = BuildWithPlayer();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4"); // auto-selected -> opened in the shared player
        player.OpenCount.Should().Be(1, "precondition: the row's file is open in the shared preview");

        vm.ReplaceOriginal = true;
        vm.ConfirmReplaceOriginals = _ => true;

        await vm.RunBatchAsync();

        engine.CallCount.Should().Be(1, "precondition: the batch actually ran");
        player.UnloadCount.Should().BeGreaterThanOrEqualTo(
            1,
            "Unload closes the media element and RELEASES the file handle — a still-open handle on the "
            + "selected row would make replacing that very file fail");
        player.StopCount.Should().Be(
            0, "Stop only halts playback and leaves the file open, so it is NOT enough in this mode");
        player.Calls.Should().ContainInOrder("Open", "Unload");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheSafeMode_KeepsThePlainStop_AndNeverUnloadsThePreview()
    {
        var (vm, probe, engine, player) = BuildWithPlayer();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");

        await vm.RunBatchAsync();

        engine.CallCount.Should().Be(1, "precondition: the batch actually ran");
        player.StopCount.Should().BeGreaterThanOrEqualTo(
            1, "the safe mode still halts the preview decode before ffmpeg does the real work");
        player.UnloadCount.Should().Be(
            0, "nothing is being overwritten, so the preview keeps the file open — Stop is the whole story");
    }
}
