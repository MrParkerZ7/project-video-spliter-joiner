using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.App.Views;
using VideoSplitJoiner.Core.Join;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-154 (SPEC-010 / SPEC-012) — the half of "dropped files disappear in silence" that was left out of
/// scope the first time.
///
/// <para>Bulk Cut learned to explain a refused drop; Split and Join did not, and the ticket's remaining
/// buildable criterion is literally <i>"On Split and Join — same silence, not yet fixed"</i>. Each screen
/// refuses something the others do not, so "same silence" is not the same sentence: Split opens
/// <b>one file at a time</b> and quietly ignores every video after the first, while Join
/// <b>permits duplicates by design</b> and so must never borrow Bulk Cut's "already in the list".</para>
///
/// <para><b>What these tests deliberately do NOT claim.</b> A drag containing no recognised video never
/// produces a drop at all — <c>OnDragOver</c> answers <see cref="VideoFileFilter.HasAnyVideo"/> with
/// <c>DragDropEffects.None</c> and Windows shows a no-entry cursor, so no handler runs and no note can be
/// shown. These exercise the view-model entry point, which is reachable exactly when a drop IS delivered.
/// The cursor is the feedback for the other case.</para>
/// </summary>
public sealed class SplitAndJoinDropFeedbackTests : IDisposable
{
    private readonly string _dir;

    public SplitAndJoinDropFeedbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t154-fb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Make(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "placeholder — the filter only reads the extension");
        return p;
    }

    private string MakeFolder(string name)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static SplitViewModel SplitVm() =>
        new(new BulkFakeProbe(), new ThrowingFakeSplitEngine(), player: null, new FakeSettings());

    private static JoinViewModel JoinVm() => new(new InertJoinEngine(), new BulkFakeProbe());

    private static BulkCutViewModel BulkVm() =>
        new(new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(),
            new FakeSettings(), new FakeBulkTrimEngine());

    /// <summary>
    /// A probe whose <see cref="ProbeAsync"/> does not complete until released, so a test can observe
    /// the view-model at the moment it is suspended mid-load. The shared <c>BulkFakeProbe</c> cannot do
    /// this — it returns <c>Task.FromResult</c>, so every "did this happen before the await?" assertion
    /// against it passes either way.
    /// </summary>
    private sealed class BlockingProbe : VideoSplitJoiner.Core.Media.IMediaProbe
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True once the load has actually entered the probe — so a test can tell "suspended
        /// mid-load" apart from "the load never started", which look identical from the outside.</summary>
        public bool Entered { get; private set; }

        public void Release() => _gate.TrySetResult();

        public async Task<VideoSplitJoiner.Core.Media.ProbeResult> ProbeAsync(
            string path, CancellationToken ct = default)
        {
            Entered = true;
            await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return VideoSplitJoiner.Core.Media.ProbeResult.Success(
                new VideoSplitJoiner.Core.Media.MediaInfo(
                    TimeSpan.FromSeconds(60), "mp4",
                    Array.Empty<VideoSplitJoiner.Core.Media.StreamInfo>(),
                    Array.Empty<VideoSplitJoiner.Core.Media.StreamInfo>()));
        }

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimeSpan>>(Array.Empty<TimeSpan>());

        // Not exercised — this fake exists only to hold a load open at the probe.
        public VideoSplitJoiner.Core.Media.KeyframeSnap SnapToNearestKeyframe(
            IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
            => throw new NotSupportedException("BlockingProbe only suspends a load");

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    /// <summary>Minimal <see cref="IJoinEngine"/> — these tests route a drop, they never run a join.</summary>
    private sealed class InertJoinEngine : IJoinEngine
    {
        public Task<CompatReport> CheckCompatibilityAsync(
            IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => Task.FromResult(CompatReport.Ok());

        public Task<JoinResult> JoinAsync(
            JoinRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null)
            => Task.FromResult(JoinResult.Ok(req.OutputPath));
    }

    // ---- The shared vocabulary ------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void ADropThatRefusedNothingProducesNoMessageAtAll()
    {
        DropRefusal.Describe("added", tail: null,
            DropRefusal.NotVideo(0), DropRefusal.Folders(0), DropRefusal.AlreadyInList(0))
            .Should().BeNull(
                "a message on every drop is noise, and noise is what teaches people to ignore the one " +
                "that matters");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void OneRefusalReadsAsOneAndSeveralReadAsSeveral()
    {
        DropRefusal.Describe("added", tail: null, DropRefusal.NotVideo(1))
            .Should().Be("1 file was not added: 1 is not a video file");

        DropRefusal.Describe("added", tail: null, DropRefusal.NotVideo(2), DropRefusal.AlreadyInList(1))
            .Should().Be("3 files were not added: 2 are not video files, 1 is already in the list");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void ADroppedFolderIsCalledAFolder_NotANonVideoFile()
    {
        // Explorer delivers a folder as an ordinary FileDrop path with no extension. Reporting it as
        // "not a video file" is simply untrue, and a message that says something false about what you
        // just did is worse than no message.
        var tally = DropRefusal.Classify(new[] { MakeFolder("season-1"), Make("a.mp4") });

        tally.Folders.Should().Be(1);
        tally.NotVideo.Should().Be(0, "a folder is a folder — it is not a file of the wrong type");

        var message = DropRefusal.Describe("added", tail: null,
            DropRefusal.NotVideo(tally.NotVideo), DropRefusal.Folders(tally.Folders));

        message.Should().Contain("folder");
        message.Should().NotContain("not a video file",
            "mis-describing the most natural gesture for a batch video tool is a new wrong statement, " +
            "not a fix");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void ClassifyingNeverDecidesWhetherADropSucceeds()
    {
        // A too-long / reserved / malformed path makes Directory.Exists throw. A diagnostic must never
        // be the reason a drop fails, so the probe is swallowed and the path falls through to the
        // extension test.
        var video = Make("a.mp4");

        DropRefusal.Tally? tally = null;
        var act = () => tally = DropRefusal.Classify(
            new[] { video, "\\\\?\\nonsense" },
            isFolder: _ => throw new IOException("the shell said no"));

        act.Should().NotThrow();
        tally!.Videos.Should().ContainSingle().Which.Should().Be(video);
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task AFolderNamedLikeAVideoIsAFolder_AndIsNotAlsoCountedAsAVideo()
    {
        // Scene releases are routinely named with the container suffix, so "Season.1.1080p.mkv" as a
        // DIRECTORY is an ordinary thing to drag. VideoFileFilter only reads the extension and never
        // touches the disk, so filtering the raw list and counting folders separately put this in both
        // buckets at once: the note said one file was not added while a row for it appeared underneath,
        // and Split tried to load the directory instead of the real video beside it.
        var folder = MakeFolder("Season.1.1080p.mkv");
        var real = Make("episode.mp4");

        var tally = DropRefusal.Classify(new[] { folder, real });

        tally.Folders.Should().Be(1);
        tally.Videos.Should().ContainSingle().Which.Should().Be(
            real, "the directory is not a video however its name reads");
        tally.NotVideo.Should().Be(0);
        (tally.Videos.Count + tally.Folders + tally.NotVideo + tally.DuplicatesInDrop)
            .Should().Be(2, "the four counts must partition the drop — nothing counted twice, nothing lost");

        // And end to end: Split loads the real video, not the directory.
        var vm = SplitVm();
        await vm.AddDroppedFilesAsync(new[] { folder, real });

        vm.InputPath.Should().Be(real);
        vm.DropSummary.Should().Be("1 file was not loaded: 1 is a folder (drop the files inside it)");
    }

    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public void TheSameFileTwiceInOneDropIsCounted()
    {
        var a = Make("a.mp4");

        var tally = DropRefusal.Classify(new[] { a, a, Make("b.mkv") });

        tally.DuplicatesInDrop.Should().Be(1);
        tally.Videos.Should().HaveCount(2, "the filter collapses the repeat — that is what must be named");
    }

    // ---- Split: it opens ONE file at a time -----------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task SplitExplainsADropItCouldLoadNothingFrom()
    {
        var vm = SplitVm();

        await vm.AddDroppedFilesAsync(new[] { Make("notes.txt"), Make("photo.jpg") });

        vm.InputPath.Should().BeNull("there was nothing loadable in the drop");
        vm.DropSummary.Should().Be(
            "2 files were not loaded: 2 are not video files",
            "a drop that loads nothing must say why, and say it correctly — the exact sentence is pinned " +
            "because a note that is confidently wrong is worse than the silence this replaced");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task SplitSaysHowManyOtherVideosItSkipped()
    {
        // The refusal Bulk Cut has no phrase for. Drop five videos on Split and four vanish with no
        // error and no note — which reads exactly like "it only half worked".
        var vm = SplitVm();

        await vm.AddDroppedFilesAsync(new[] { Make("a.mp4"), Make("b.mkv"), Make("c.m2ts") });

        vm.DropSummary.Should().Be(
            "2 files were not loaded: 2 other videos were skipped — Split opens one file at a time.",
            "the count alone reads as a malfunction; the reason makes it a rule, and both are pinned so " +
            "a mis-wired clause cannot pass by containing the right digit");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task SplitSaysNothingWhenItLoadedTheOnlyVideoDropped()
    {
        var vm = SplitVm();

        await vm.AddDroppedFilesAsync(new[] { Make("only.mp4") });

        vm.DropSummary.Should().BeNull("nothing was refused, so there is nothing to report");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public async Task ClearingSplitRemovesTheNote()
    {
        var vm = SplitVm();
        await vm.AddDroppedFilesAsync(new[] { Make("a.mp4"), Make("notes.txt") });
        vm.DropSummary.Should().NotBeNullOrWhiteSpace("precondition — the drop was refused something");

        vm.Clear();

        vm.DropSummary.Should().BeNull(
            "the note describes a drop whose screen no longer exists; leaving it up is the stale-note " +
            "bug that shipped on Bulk Cut");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void SplitsNoteIsOnScreenBeforeTheLoadEvenStarts()
    {
        // The view calls the VM fire-and-forget and then reads DropSummary immediately, to put the same
        // sentence into dragdrop.log for that drop. Both depend on the note being assigned BEFORE the
        // first await, not after the probe returns.
        //
        // Proving that needs a probe that genuinely suspends: the shared BulkFakeProbe returns
        // Task.FromResult, so the whole async method runs to completion synchronously and an assertion
        // afterwards passes whether the assignment is before or after the await. The first version of
        // this test used that fake and was vacuous — a mutation moving the assignment past the await
        // survived it.
        var probe = new BlockingProbe();
        var vm = new SplitViewModel(probe, new ThrowingFakeSplitEngine(), player: null, new FakeSettings());

        SplitView.HandleDroppedFiles(new[] { Make("a.mp4"), Make("b.mkv") }, vm);

        probe.Entered.Should().BeTrue(
            "precondition — the load must have genuinely REACHED the probe. Asserting only that " +
            "InputPath is null would also pass if the load never started, which is a different bug " +
            "wearing this test's clothes");
        vm.InputPath.Should().BeNull("precondition — and it is still suspended there");
        vm.DropSummary.Should().NotBeNullOrWhiteSpace(
            "the note is assigned synchronously; one that appears only after the probe cannot be " +
            "logged alongside the drop it describes");

        probe.Release();
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void SplitStillLoadsTheFirstVideoThroughTheViewRoute()
    {
        // The routing change moved the load out of the view (LoadCommand.Execute) and into the VM. The
        // existing SplitAndJoinDropRoutingTests still assert the RETURN value, so this covers the half
        // they do not: that a drop through the view actually loads the first video. Asserting the
        // return list again would only look like coverage.
        var vm = SplitVm();
        var first = Make("a.mp4");
        var second = Make("b.mkv");

        var considered = SplitView.HandleDroppedFiles(new[] { Make("notes.txt"), first, second }, vm);

        considered.Should().Equal(new[] { first, second });
        vm.InputPath.Should().Be(first, "Split loads the FIRST video, through the VM rather than the view");
    }

    // ---- Join: it takes them all, and allows duplicates -----------------------------------------

    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public async Task JoinAddsTheVideosAndExplainsTheRestOfAMixedDrop()
    {
        var vm = JoinVm();

        await vm.AddDroppedFilesAsync(new[] { Make("a.mp4"), Make("notes.txt"), Make("b.mkv") });

        vm.Items.Should().HaveCount(2, "one unsupported file must not poison the whole drop");
        vm.DropSummary.Should().Be(
            "1 file was not added: 1 is not a video file",
            "pinned in full — a clause wired to the wrong count or the wrong noun would still contain a '1'");
    }

    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public async Task JoinNeverSaysAlreadyInTheList()
    {
        // Join permits the same clip twice on purpose — you may genuinely want it twice in the output.
        // Borrowing Bulk Cut's wording here would contradict the screen's own rule.
        var vm = JoinVm();
        var a = Make("a.mp4");

        await vm.AddDroppedFilesAsync(new[] { a });
        await vm.AddDroppedFilesAsync(new[] { a });

        vm.Items.Should().HaveCount(2, "duplicates are allowed on Join by design");
        vm.DropSummary.Should().BeNull("nothing was refused — the second copy was added");
    }

    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public async Task JoinNamesTheSameFileDroppedTwiceInOneDrop()
    {
        // Inconsistent today and invisible: the filter collapses the repeat inside a single payload
        // even though adding it twice in two gestures is permitted.
        var vm = JoinVm();
        var a = Make("a.mp4");

        await vm.AddDroppedFilesAsync(new[] { a, a });

        vm.Items.Should().HaveCount(1, "the shared filter dedupes within one drop");
        vm.DropSummary.Should().Be(
            "1 file was not added: 1 was dropped twice",
            "the collapse contradicts Join's own duplicates-allowed rule, so it must be named — in full, " +
            "because Join must never reach for Bulk Cut's 'already in the list' wording here");
    }

    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public async Task JoinSaysNothingWhenEveryDroppedFileWasAdded()
    {
        var vm = JoinVm();

        await vm.AddDroppedFilesAsync(new[] { Make("a.mp4"), Make("b.mkv") });

        vm.Items.Should().HaveCount(2);
        vm.DropSummary.Should().BeNull();
    }

    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public async Task ClearingJoinRemovesTheNote()
    {
        var vm = JoinVm();
        await vm.AddDroppedFilesAsync(new[] { Make("a.mp4"), Make("notes.txt") });
        vm.DropSummary.Should().NotBeNullOrWhiteSpace("precondition — the drop was refused something");

        vm.Clear();

        vm.DropSummary.Should().BeNull();
    }

    // ---- Bulk Cut: the stale note that shipped --------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ClearingBulkCutRemovesTheStaleDropNote()
    {
        // Found while mirroring this screen onto the other two: DropSummary was written on drop and
        // cleared by nothing but a later drop, so "1 file was not added" survived Clear all and sat
        // over an empty list contradicting it. Copying the screen as-shipped would have reproduced the
        // bug three times over.
        var vm = BulkVm();
        await vm.AddDroppedFilesAsync(new[] { Make("a.mp4"), Make("notes.txt") });
        vm.DropSummary.Should().NotBeNullOrWhiteSpace("precondition — the drop was refused something");
        vm.Items.Should().NotBeEmpty("precondition — Clear is a no-op on an empty list");

        vm.Clear();

        vm.Items.Should().BeEmpty();
        vm.DropSummary.Should().BeNull("an empty list cannot still owe you an explanation for a drop");
    }
}
