using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-189 (G-059) — a Bulk Cut row in Exact mode whose source Exact cannot cut is honest BEFORE Run.
///
/// <para>Since T-189 an HEVC source falls back from Exact to the lossless cut (its joint decoded against the head's
/// parameter sets). Before this ticket an Exact row hid its snap readout, cut at <c>Requested</c> and grabbed its chip
/// at <c>Requested</c> — so every HEVC Exact row would have shown a cut the run does not make, and an intro inside the
/// first GOP would have been skipped at Run with no word. The row now asks the same gate the engine falls back on
/// (<see cref="SmartCutArgsBuilder.TryResolveEncoders"/>, on the row's probed <see cref="MediaInfo"/>): a row Exact
/// cannot cut behaves exactly as in Lossless — snap note, snapped cut, snapped chip, eligibility from <c>Snapped</c> —
/// and states the reason in the run's own words, once. A row Exact can cut is unchanged.</para>
/// </summary>
public sealed class ExactFallbackRowTests : IDisposable
{
    private const string HevcReason = "frame-exact cutting is not supported for HEVC/H.265 video yet";

    private static readonly string HevcNote =
        $"{BulkTrimEngine.ExactFallbackPrefix} ({HevcReason}) - cut snapped to the nearest keyframe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-t189-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string MakeVideo(string name)
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "video bytes");
        return p;
    }

    private static Task Immediate(TimeSpan _, CancellationToken ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeThumbnailService Thumbs, FakeBulkTrimEngine Engine) Build()
    {
        var probe = new BulkFakeProbe();
        var thumbs = new FakeThumbnailService { ThumbnailFactory = (_, time, _) => $"frame-{time.TotalSeconds}.jpg" };
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), thumbs, new FakeSettings(), engine,
            thumbnailDebounce: TimeSpan.FromMilliseconds(1), thumbnailDelay: Immediate);
        vm.LookupFileHolders = _ => Array.Empty<string>();
        return (vm, probe, thumbs, engine);
    }

    /// <summary>Add a row whose source probes as <paramref name="videoCodec"/> / <paramref name="audioCodec"/>, keyframes every <paramref name="step"/> s.</summary>
    private static async Task<BulkItemViewModel> AddAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, string? videoCodec, string? audioCodec = "aac", double step = 2)
    {
        probe.SetUniform(path, S(60), step);
        probe.SetCodecs(path, videoCodec, audioCodec);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask;
        return row;
    }

    private static void Settle(BulkItemViewModel row) =>
        row.InFlightGrabs.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the grab should finish promptly");

    private static IReadOnlyList<TimeSpan> RequestedTimes(FakeThumbnailService thumbs, string path) =>
        thumbs.Requests.Where(r => r.InputPath == path).Select(r => r.Time).ToList();

    // ---- The HEVC row before Run ------------------------------------------------------------------------

    // serves-spec: SPEC-011#I164 / SPEC-011#I91 / SPEC-011#I61 — an HEVC row in Exact shows the snap note, cuts and grabs
    // its chip at Snapped, is judged on Snapped, and states the reason before Run.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnHevcRowInExact_ShowsTheSnappedCut_ItsChip_AndTheReason_BeforeRun()
    {
        var (vm, probe, thumbs, _) = Build();
        vm.ExactCut = true;
        var path = MakeVideo("phone.mp4");
        var row = await AddAsync(vm, probe, path, "hevc");

        row.IntroEnd.Requested = S(11); // a 10/12 tie on the 2 s grid → snaps to the earlier 10 s
        Settle(row);

        row.ExactUnavailableReason.Should().Be(HevcReason);
        row.IntroEnd.Snapped.Should().Be(S(10));
        row.IntroEnd.HasSnapNote.Should().BeTrue("the run cuts at the keyframe, so the row says so (SuppressSnapNote stays false)");
        row.IntroEnd.SnapNote.Should().Contain("00:10.0");
        row.KeptDuration.Should().Be(S(50), "eligibility and the kept length come from Snapped: 60 − 10");
        row.IsValidCut.Should().BeTrue();
        row.RowState.Should().Be(RowState.Ready);
        RequestedTimes(thumbs, path).Should().NotBeEmpty();
        RequestedTimes(thumbs, path).Last().Should().Be(S(10), "the chip shows the frame the run cuts at");
        RequestedTimes(thumbs, path).Should().NotContain(S(11), "the row never shows a Requested cut it will not make");
        row.Warning.Should().Contain(HevcNote, "the reason is stated before Run, in the run's own words");
    }

    // serves-spec: SPEC-011#I164 — the snapped intro is 0 with no outro: excluded before Run with "nothing to trim yet",
    // and the row still carries the reason (where the engine used to skip it with no word).
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnHevcRowInExact_WhoseSnappedIntroIs0_IsExcludedBeforeRun_WithTheReason()
    {
        var (vm, probe, _, _) = Build();
        vm.ExactCut = true;
        var row = await AddAsync(vm, probe, MakeVideo("phone.mp4"), "hevc");

        row.IntroEnd.Requested = S(0.8); // inside the first GOP → snaps to 0

        row.IntroEnd.Snapped.Should().Be(TimeSpan.Zero);
        row.IsNoOpTrim.Should().BeTrue("the lossless cut it falls back to removes nothing");
        row.RowState.Should().Be(RowState.NoOpTrim);
        row.IsEnabled.Should().BeFalse();
        row.ExclusionReason.Should().Be("nothing to trim yet — set an intro or outro");
        row.Warning.Should().Contain(HevcNote);
    }

    // serves-spec: SPEC-011#I91 — the same request on an H.264 row is a real Exact trim (0.8 s is cut exactly).
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnH264RowInExact_IsUnchanged_SnapNoteHidden_RequestedCutAndChip()
    {
        var (vm, probe, thumbs, _) = Build();
        vm.ExactCut = true;
        var path = MakeVideo("camera.mp4");
        var row = await AddAsync(vm, probe, path, "h264");

        row.IntroEnd.Requested = S(11);
        Settle(row);

        row.ExactUnavailableReason.Should().BeNull();
        row.IntroEnd.HasSnapNote.Should().BeFalse("Exact really cuts this row at 11 s");
        row.KeptDuration.Should().Be(S(49), "an Exact row Exact can cut is judged on Requested: 60 − 11");
        RequestedTimes(thumbs, path).Last().Should().Be(S(11));
        RequestedTimes(thumbs, path).Should().NotContain(S(10));
        (row.Warning ?? string.Empty).Should().NotContain(BulkTrimEngine.ExactFallbackPrefix);
        row.BuildBulkTrimItem().IntroEnd.Should().Be(S(11), "the engine still gets Requested and decides");
    }

    // serves-spec: SPEC-011#I91 — flipping precision re-evaluates both kinds of row, both ways.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task FlippingPrecision_ReEvaluatesBothRows()
    {
        var (vm, probe, _, _) = Build();
        var hevc = await AddAsync(vm, probe, MakeVideo("hevc.mp4"), "hevc");
        var h264 = await AddAsync(vm, probe, MakeVideo("h264.mp4"), "h264");
        hevc.IntroEnd.Requested = S(11);
        h264.IntroEnd.Requested = S(11);

        hevc.IntroEnd.HasSnapNote.Should().BeTrue("Lossless: both rows show their snap");
        h264.IntroEnd.HasSnapNote.Should().BeTrue();
        (hevc.Warning ?? string.Empty).Should().NotContain(BulkTrimEngine.ExactFallbackPrefix, "Lossless has nothing to fall back from");

        vm.ExactCut = true;

        hevc.IntroEnd.HasSnapNote.Should().BeTrue();
        hevc.KeptDuration.Should().Be(S(50));
        hevc.Warning.Should().Contain(HevcNote);
        h264.IntroEnd.HasSnapNote.Should().BeFalse();
        h264.KeptDuration.Should().Be(S(49));

        vm.ExactCut = false;

        hevc.IntroEnd.HasSnapNote.Should().BeTrue();
        (hevc.Warning ?? string.Empty).Should().NotContain(BulkTrimEngine.ExactFallbackPrefix);
        h264.IntroEnd.HasSnapNote.Should().BeTrue();
        h264.KeptDuration.Should().Be(S(50));
    }

    // serves-spec: SPEC-011#I91 / SPEC-011#I164 — a row added after the flip takes the rule at probe time, even while its
    // scan is still running.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ARowAddedAfterTheFlip_TakesTheRuleAtProbeTime()
    {
        var (vm, probe, _, _) = Build();
        vm.ExactCut = true;
        var hevcPath = MakeVideo("hevc.mp4");
        var h264Path = MakeVideo("h264.mp4");
        probe.SetUniform(hevcPath, S(60), 2);
        probe.SetCodecs(hevcPath, "hevc");
        probe.SetUniform(h264Path, S(60), 2);
        probe.GatedPaths.Add(hevcPath);
        probe.GatedPaths.Add(h264Path);

        await vm.AddFilesAsync(new[] { hevcPath, h264Path });
        var hevc = vm.Items.Single(i => i.Path == hevcPath);
        var h264 = vm.Items.Single(i => i.Path == h264Path);

        hevc.KeyframesReady.Should().BeFalse("precondition: the scans are held open");
        hevc.ExactUnavailableReason.Should().Be(HevcReason, "the rule runs on the probe, before the scan");
        hevc.IntroEnd.HasSnapNote.Should().BeTrue("a fall-back row shows '→ snapping…' like a Lossless one");
        hevc.Warning.Should().Contain(HevcNote);
        h264.IntroEnd.HasSnapNote.Should().BeFalse("an Exact row Exact can cut hides the readout from its first moment");

        probe.ReleaseScans();
        await hevc.CurrentScanTask;
        await h264.CurrentScanTask;
        hevc.IntroEnd.Requested = S(5);
        hevc.IntroEnd.Snapped.Should().Be(S(4));
        hevc.IntroEnd.HasSnapNote.Should().BeTrue();
    }

    // serves-spec: SPEC-011#I164 / SPEC-011#I161 — after the run the reason appears ONCE on the row, and RunWarnings
    // still holds the engine's ledger warning (the auto-clear summary reads it).
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AfterARun_TheReasonAppearsOnce_AndRunWarningsKeepsTheLedgerWarning()
    {
        var (vm, probe, _, engine) = Build();
        vm.ExactCut = true;
        var row = await AddAsync(vm, probe, MakeVideo("phone.mp4"), "hevc");
        row.IntroEnd.Requested = S(11);
        var ledger = await TheEnginesOwnWarningAsync("hevc", "aac");
        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.Completed,
            items.Select(i => new BulkTrimItemResult(i, ItemOutcome.Done, i.DesiredOutputPath, null, new[] { ledger })).ToList());

        await vm.RunBatchAsync();

        ledger.Should().Be(HevcNote, "the row's before-Run sentence is the engine's own sentence, word for word");
        row.RunWarnings.Should().Equal(new[] { ledger }, "the ledger warning is kept for the auto-clear summary");
        CountOf(row.Warning!, HevcNote).Should().Be(1, "shown once after the run, not twice");
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ---- Every Exact-gated read follows the predicate ------------------------------------------------------

    // serves-spec: SPEC-011#I92 — the T-120 "cut moved Ns" note shows on a fall-back row, as in Lossless.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnHevcRowInExact_ShowsTheCutMovedNote_WhenItsSnapMovesTheCut()
    {
        var (vm, probe, _, _) = Build();
        vm.ExactCut = true;
        var row = await AddAsync(vm, probe, MakeVideo("phone.mp4"), "hevc", step: 4);

        row.IntroEnd.Requested = S(5); // 4 s grid → 4 s

        row.Warning.Should().Contain("cut moved 1.0s to the nearest keyframe");
    }

    // serves-spec: SPEC-011#I62 / SPEC-011#I158 — dragging a fall-back row's intro across a keyframe re-grabs its chip at
    // the new Snapped (the re-grab trigger listens to Snapped, as in Lossless).
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task DraggingAnHevcRowsIntroAcrossAKeyframe_ReGrabsTheChipAtTheNewSnapped()
    {
        var (vm, probe, thumbs, _) = Build();
        vm.ExactCut = true;
        var path = MakeVideo("phone.mp4");
        var row = await AddAsync(vm, probe, path, "hevc");
        row.IntroEnd.Requested = S(11);
        Settle(row);
        RequestedTimes(thumbs, path).Last().Should().Be(S(10), "precondition");

        row.IntroEnd.Requested = S(13.2); // nearer 14 than 12
        Settle(row);

        row.IntroEnd.Snapped.Should().Be(S(14));
        RequestedTimes(thumbs, path).Last().Should().Be(S(14), "the chip follows the new snapped cut");
        RequestedTimes(thumbs, path).Should().NotContain(S(13.2));
    }

    // ---- The outro handle follows the predicate too ----------------------------------------------------------

    // serves-spec: SPEC-011#I164 / SPEC-011#I8 / SPEC-011#I64 — the outro of a row Exact cannot cut (HEVC, and a source
    // the generic branch refuses): AddOutro keeps its snap note (BulkItemViewModel.AddOutro's SuppressSnapNote), the kept
    // length and eligibility come from the Snapped outro (EffectiveOutroStart), and its chip is grabbed at Snapped.
    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData("hevc")]
    [InlineData("prores")]
    public async Task AnOutroAddedToARowExactCannotCut_ShowsItsSnap_IsJudgedOnSnapped_AndGrabsItsChipAtSnapped(string video)
    {
        var (vm, probe, thumbs, _) = Build();
        vm.ExactCut = true;
        var path = MakeVideo("phone.mp4");
        var row = await AddAsync(vm, probe, path, video);
        row.IntroEnd.Requested = S(11); // → 10

        row.AddOutro(S(47.2)); // off the 2 s grid: nearer 48 than 46
        Settle(row);

        row.OutroStart!.Snapped.Should().Be(S(48));
        row.OutroStart.HasSnapNote.Should().BeTrue("the run cuts the outro at the keyframe, so the row says so");
        row.OutroStart.SnapNote.Should().Contain("00:48.0");
        row.KeptDuration.Should().Be(S(38), "both cuts are judged on Snapped: 48 − 10 (not 47.2 − 10)");
        row.IsValidCut.Should().BeTrue();
        RequestedTimes(thumbs, path).Should().Contain(S(48), "the outro chip shows the frame the run cuts at");
        RequestedTimes(thumbs, path).Should().NotContain(S(47.2), "the row never shows a Requested outro it will not make");
    }

    // serves-spec: SPEC-011#I164 / SPEC-011#I91 — an outro that already exists when Exact is switched on keeps its snap
    // note and its Snapped cut and chip across the flip.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnHevcRowsOutro_AddedInLossless_KeepsItsSnap_WhenExactIsSwitchedOn()
    {
        var (vm, probe, thumbs, _) = Build();
        var path = MakeVideo("phone.mp4");
        var row = await AddAsync(vm, probe, path, "hevc");
        row.IntroEnd.Requested = S(11);
        row.AddOutro(S(47.2));
        Settle(row);
        row.OutroStart!.HasSnapNote.Should().BeTrue("precondition: Lossless shows the outro's snap");

        vm.ExactCut = true;
        Settle(row);

        row.OutroStart.HasSnapNote.Should().BeTrue("the flip leaves a row Exact cannot cut as it was");
        row.KeptDuration.Should().Be(S(38));
        RequestedTimes(thumbs, path).Should().NotContain(S(47.2));
        RequestedTimes(thumbs, path).Last().Should().Be(S(48), "the outro chip stays at the snapped cut");
    }

    // serves-spec: SPEC-011#I164 / SPEC-011#I91 — an outro set on an Exact row BEFORE its probe says the source is HEVC:
    // it starts as an Exact handle (snap note hidden, the source is not known yet), and the probe turns it into a
    // fall-back handle — snap note back, Snapped cut and chip.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnOutroSetBeforeTheProbe_FollowsTheRule_OnceTheProbeSaysHevc()
    {
        var path = MakeVideo("phone.mp4");
        var probe = new BulkFakeProbe();
        probe.SetUniform(path, S(60), 2);
        var thumbs = new FakeThumbnailService { ThumbnailFactory = (_, time, _) => $"frame-{time.TotalSeconds}.jpg" };
        var row = new BulkItemViewModel(
            path, probe, new SemaphoreSlim(3, 3), thumbnails: thumbs,
            thumbnailDebounce: TimeSpan.FromMilliseconds(1), thumbnailDelay: Immediate);
        row.SetExactCut(true);
        row.IntroEnd.Requested = S(11);
        row.AddOutro(S(47.2));
        row.OutroStart!.SuppressSnapNote.Should().BeTrue("precondition: before the probe, Exact is assumed to cut the row");

        row.SetSourceMedia(new MediaInfo(
            S(60), "mp4",
            new[] { new StreamInfo(0, "hevc", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
            new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") }));
        row.Duration = S(60);
        await row.StartKeyframeScanAsync();
        Settle(row);

        row.OutroStart.SuppressSnapNote.Should().BeFalse();
        row.OutroStart.Snapped.Should().Be(S(48));
        row.OutroStart.HasSnapNote.Should().BeTrue();
        row.KeptDuration.Should().Be(S(38));
        RequestedTimes(thumbs, path).Should().Contain(S(48));
        RequestedTimes(thumbs, path).Should().NotContain(S(47.2));
    }

    // serves-spec: SPEC-011#I64 / SPEC-011#I158 — on a SCANNING row Exact cannot cut, an added outro's chip is not grabbed
    // at the add (its time is provisional until the snap lands, as in Lossless); the land kick grabs it at Snapped.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnOutroAddedToAScanningHevcRowInExact_WaitsForTheScan_ThenGrabsAtSnapped()
    {
        var (vm, probe, thumbs, _) = Build();
        vm.ExactCut = true;
        var path = MakeVideo("phone.mp4");
        probe.SetUniform(path, S(60), 2);
        probe.SetCodecs(path, "hevc");
        probe.GatedPaths.Add(path);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        row.KeyframesReady.Should().BeFalse("precondition: the scan is held open");

        row.AddOutro(S(47.2));
        Settle(row);

        RequestedTimes(thumbs, path).Should().NotContain(S(47.2), "a fall-back row's outro time is provisional while scanning");
        RequestedTimes(thumbs, path).Should().NotContain(S(48));

        probe.ReleaseScans();
        await row.CurrentScanTask;
        Settle(row);

        row.OutroStart!.Snapped.Should().Be(S(48));
        RequestedTimes(thumbs, path).Should().Contain(S(48), "the land kick grabs the outro at its snapped cut");
        RequestedTimes(thumbs, path).Should().NotContain(S(47.2));
    }

    // ---- The generic branch -------------------------------------------------------------------------------

    // serves-spec: SPEC-011#I164 — a row the generic "no known encoder" branch refuses behaves the same way.
    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData("prores", "aac", "no known encoder for video codec 'prores'")]
    [InlineData("h264", "pcm_s24le", "no known encoder for audio codec 'pcm_s24le'")]
    public async Task ARowTheGenericBranchRefuses_ShowsTheSnappedCut_AndTheReason(string video, string audio, string reason)
    {
        var (vm, probe, thumbs, _) = Build();
        vm.ExactCut = true;
        var path = MakeVideo("studio.mov");
        var row = await AddAsync(vm, probe, path, video, audio);

        row.IntroEnd.Requested = S(11);
        Settle(row);

        row.ExactUnavailableReason.Should().Be(reason);
        row.IntroEnd.HasSnapNote.Should().BeTrue();
        row.KeptDuration.Should().Be(S(50));
        RequestedTimes(thumbs, path).Last().Should().Be(S(10));
        row.Warning.Should().Contain($"{BulkTrimEngine.ExactFallbackPrefix} ({reason}) - cut snapped to the nearest keyframe");
    }

    // ---- Auto-clear keeps the after-run word --------------------------------------------------------------

    // serves-spec: SPEC-011#I161 — the summary names an HEVC fall-back row the clear removed, although its reason was
    // already on the row before Run.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task WithAutoClear_AnHevcFallBackRowClears_AndTheSummaryNamesIt()
    {
        var (vm, probe, _, engine) = Build();
        vm.ExactCut = true;
        var path = MakeVideo("phone.mp4");
        var row = await AddAsync(vm, probe, path, "hevc");
        row.IntroEnd.Requested = S(11);
        row.Warning.Should().Contain(HevcNote, "precondition: the reason was shown before Run");
        var ledger = await TheEnginesOwnWarningAsync("hevc", "aac");
        var output = MakeVideo("phone_trimmed.mp4");
        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.Completed,
            items.Select(i => new BulkTrimItemResult(i, ItemOutcome.Done, output, null, new[] { ledger })).ToList());
        vm.AutoClearAfterRun = true;

        await vm.RunBatchAsync();

        vm.Items.Should().BeEmpty("a finished fall-back row clears like the rest");
        vm.Operation.ResultSummary.Should().Contain("Not cut exactly (snapped to a keyframe): phone.mp4");
    }

    // ---- Boundaries ---------------------------------------------------------------------------------------

    // serves-spec: SPEC-011#I164 — a start already on a keyframe: the engine is PureCopy and adds no warning, but the row's
    // before-Run note still shows, because the rule looks at the source; Snapped == Requested, so the cut shown is made.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnHevcRowWhoseIntroIsOnAKeyframe_StillShowsTheReason_WithSnappedEqualToRequested()
    {
        var (vm, probe, _, _) = Build();
        vm.ExactCut = true;
        var row = await AddAsync(vm, probe, MakeVideo("phone.mp4"), "hevc");

        row.IntroEnd.Requested = S(4);

        row.IntroEnd.Snapped.Should().Be(row.IntroEnd.Requested);
        row.IntroEnd.HasSnapNote.Should().BeFalse("there is no offset to show");
        row.Warning.Should().Contain(HevcNote);
    }

    // serves-spec: SPEC-011#I164 — an audio-only source has no video stream to refuse: an Exact row Exact can cut.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnAudioOnlyRow_IsUnchanged()
    {
        var (vm, probe, _, _) = Build();
        vm.ExactCut = true;
        var row = await AddAsync(vm, probe, MakeVideo("voice.m4a"), null, "aac");

        row.ExactUnavailableReason.Should().BeNull();
        row.IntroEnd.Requested = S(11);
        row.IntroEnd.HasSnapNote.Should().BeFalse();
    }

    // serves-spec: SPEC-011#I164 — a row whose probe failed is already "can't read this file"; the rule never runs.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ALoadFailedRow_NeverRunsTheRule()
    {
        var (vm, probe, _, _) = Build();
        vm.ExactCut = true;
        var path = MakeVideo("broken.mp4");
        probe.FailProbePaths.Add(path);

        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single();

        row.ExactUnavailableReason.Should().BeNull();
        row.ExclusionReason.Should().Be("can't read this file");
        (row.Warning ?? string.Empty).Should().NotContain(BulkTrimEngine.ExactFallbackPrefix);
    }

    // serves-spec: SPEC-011#I90 — the mode's own promise names the exception.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void PrecisionNote_InExact_NamesTheHevcException()
    {
        var (vm, _, _, _) = Build();

        vm.ExactCut = true;

        vm.PrecisionNote.Should().Be(
            "Exact — cuts land where you set them (re-encodes ~1s per cut; HEVC video still snaps to keyframes)");
    }

    // ---- The engine's own sentence --------------------------------------------------------------------------

    /// <summary>
    /// Run the REAL Core <see cref="BulkTrimEngine"/> + <see cref="SmartCutEngine"/> over a source probed as the given
    /// codecs (fakes for the probe, ffmpeg and the lossless pass) and return the warning it writes — so the row's
    /// before-Run sentence is checked against the engine's actual words, not a copy of them.
    /// </summary>
    private async Task<string> TheEnginesOwnWarningAsync(string video, string? audio)
    {
        var input = MakeVideo("engine-src.mp4");
        var probe = new BulkFakeProbe();
        probe.SetUniform(input, S(60), 2);
        probe.SetCodecs(input, video, audio);
        var engine = new BulkTrimEngine(
            new CopyingSplitEngine(), new EchoRequestBuilder(), new SmartCutEngine(new NeverRunner(), probe));

        var result = await engine.RunAsync(
            new[] { new BulkTrimItem(input, S(11), null, Path.Combine(_dir, "engine-out.mp4")) },
            new BulkTrimOptions(Precision: CutPrecision.Exact));

        return result.Items.Single().Warnings.Single();
    }

    private sealed class NeverRunner : IFfmpegRunner
    {
        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
            => throw new InvalidOperationException("a source Exact cannot cut must reach no ffmpeg run");
    }

    private sealed class EchoRequestBuilder : IBulkTrimRequestBuilder
    {
        public Task<SplitRequest> BuildAsync(BulkTrimItem item, string effectiveOutputPath, bool overwrite, CancellationToken ct)
        {
            var full = Path.GetFullPath(effectiveOutputPath);
            return Task.FromResult(new SplitRequest(
                item.InputPath, new[] { item.IntroEnd }, Path.GetDirectoryName(full)!, Path.GetFileName(full), overwrite,
                new[] { 2 }));
        }
    }

    private sealed class CopyingSplitEngine : ISplitEngine
    {
        public Task<SplitResult> SplitAsync(
            SplitRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<OperationStatus>? status = null,
            IProgress<PartProgress>? partProgress = null)
            => Task.FromResult(SplitResult.Empty(Array.Empty<string>()));
    }
}
