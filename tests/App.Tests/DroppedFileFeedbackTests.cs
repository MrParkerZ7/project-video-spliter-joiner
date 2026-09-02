using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-154 (SPEC-011) — dropped files must never disappear in silence.
///
/// <para>Reported as <i>"I can't drag and drop file any more … why it's work sometime doesn't work some
/// time?"</i>. It is not intermittent: three paths discard a drop deterministically, and none of them says
/// anything, so from the outside they are indistinguishable from a dead drop target.</para>
///
/// <para>Drag-and-drop had <b>no test at all</b> before this file — on any of the three screens — despite
/// <see cref="BulkCutView.HandleDroppedFiles"/> being commented "extracted for testability". The seam was
/// built for a test nobody wrote, which is exactly how three silent-refusal paths shipped.</para>
/// </summary>
public sealed class DroppedFileFeedbackTests : IDisposable
{
    private readonly string _dir;

    public DroppedFileFeedbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t154-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Make(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "not really video, but the filter only reads the extension");
        return p;
    }

    private static BulkCutViewModel Vm() =>
        new(new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(),
            new FakeSettings(), new FakeBulkTrimEngine());

    // ---- The formats that were silently refused ------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData("clip.m2ts")]   // AVCHD — camcorders, Blu-ray rips
    [InlineData("clip.mts")]    // AVCHD
    [InlineData("clip.3gp")]    // phone video
    [InlineData("clip.3g2")]
    [InlineData("clip.ogv")]
    [InlineData("clip.vob")]    // DVD
    [InlineData("clip.asf")]
    [InlineData("clip.divx")]
    [InlineData("clip.f4v")]
    [InlineData("clip.mxf")]    // broadcast
    [InlineData("clip.m2v")]
    public void CommonContainersAreAccepted(string name)
    {
        var path = Make(name);

        VideoFileFilter.AcceptVideoFiles(new[] { path }).Should().ContainSingle(
            $"{Path.GetExtension(name)} is an ordinary video container ffmpeg handles — refusing it " +
            "without a word is what reads as 'drag and drop is broken today'");
        VideoFileFilter.HasAnyVideo(new[] { path }).Should().BeTrue(
            "otherwise the drag shows a no-entry cursor and the drop never happens");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData("clip.MP4")]
    [InlineData("clip.M2TS")]
    [InlineData("clip.Mkv")]
    public void ExtensionMatchingStaysCaseInsensitive(string name)
    {
        VideoFileFilter.AcceptVideoFiles(new[] { Make(name) }).Should().ContainSingle();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void GenuinelyNonVideoFilesAreStillRefused()
    {
        // Broadening the list must not turn it into "accept anything" — that would hand ffmpeg a
        // document and surface a failure later, further from the cause.
        VideoFileFilter.AcceptVideoFiles(new[] { Make("notes.txt"), Make("photo.jpg"), Make("archive.zip") })
            .Should().BeEmpty();
    }

    // ---- The silence itself — the actual defect ------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void DroppingOnlyNonVideoFiles_SaysSo_InsteadOfDoingNothing()
    {
        var vm = Vm();

        var accepted = BulkCutView.HandleDroppedFiles(
            new[] { Make("notes.txt"), Make("photo.jpg") }, vm);

        accepted.Should().BeEmpty();
        vm.Items.Should().BeEmpty();
        vm.DropSummary.Should().NotBeNullOrWhiteSpace(
            "a drop that adds nothing must explain itself — silence is indistinguishable from a dead " +
            "drop target, which is exactly how this was reported");
        vm.DropSummary.Should().Contain("2");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ReDroppingAFileAlreadyInTheList_SaysSo()
    {
        var vm = Vm();
        var path = Make("a.mp4");

        await vm.AddFilesAsync(new[] { path });
        vm.Items.Should().ContainSingle("precondition");

        BulkCutView.HandleDroppedFiles(new[] { path }, vm);
        await Task.Yield();

        vm.Items.Should().ContainSingle("it is the same file — one row is correct");
        vm.DropSummary.Should().NotBeNullOrWhiteSpace(
            "re-dropping a file you already added looks EXACTLY like a broken drop target, and is the " +
            "sharpest of the three silent paths");
        vm.DropSummary.Should().Contain("already");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void AMixedDrop_AddsTheVideosAndAccountsForTheRest()
    {
        var vm = Vm();

        var accepted = BulkCutView.HandleDroppedFiles(
            new[] { Make("a.mp4"), Make("notes.txt"), Make("b.mkv"), Make("photo.jpg") }, vm);

        accepted.Should().HaveCount(2, "the videos still arrive — a bad file must not poison the drop");
        vm.DropSummary.Should().NotBeNullOrWhiteSpace("and the two that vanished are accounted for");
        vm.DropSummary.Should().Contain("2");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void ADropThatAddsEverythingSaysNothing()
    {
        var vm = Vm();

        BulkCutView.HandleDroppedFiles(new[] { Make("a.mp4"), Make("b.mkv") }, vm);

        vm.DropSummary.Should().BeNullOrEmpty(
            "nothing was refused, so there is nothing to explain — a message on every drop is noise, " +
            "and noise is what teaches people to ignore the one that matters");
    }

    // ---- The diagnostic that makes the NEXT report answerable ---------------------------------------

    /// <summary>
    /// T-154 — a probe that does not write is worse than no probe, because it reads as evidence of
    /// absence. The point of this trace is to tell two cases apart on the next report: nothing logged
    /// means Windows never delivered the drag to us (not our bug); a line means we saw it and the line
    /// says what we decided.
    /// </summary>
    [Fact]
    public void TheDragDropTraceActuallyWrites_AndRecordsTheDecision()
    {
        var before = File.Exists(DropDiagnostics.LogPath)
            ? File.ReadAllText(DropDiagnostics.LogPath).Length
            : 0;

        var secret = Path.Combine(_dir, "private-folder-name", "holiday.m2ts");
        DropDiagnostics.Record(
            "drop", "TestOnly", new[] { secret, Path.Combine(_dir, "notes.txt") },
            accepted: false, note: "unit-test");

        File.Exists(DropDiagnostics.LogPath).Should().BeTrue("the trace must reach disk to be worth anything");
        var text = File.ReadAllText(DropDiagnostics.LogPath);
        text.Length.Should().BeGreaterThan(before);

        var line = text.TrimEnd().Split('\n').Last();
        line.Should().Contain("TestOnly").And.Contain("files=2").And.Contain("accepted=False");
        line.Should().Contain(".m2ts", "the extension is what diagnoses an allowlist refusal");
        line.Should().NotContain(
            "private-folder-name",
            "extensions are enough to diagnose a refusal - this file gets pasted into bug reports, so it "
            + "must not carry someone's folder structure with it");
    }

    [Fact]
    public void TheTraceSurvivesGarbageInput()
    {
        var act = () =>
        {
            DropDiagnostics.Record("over", "TestOnly", null, accepted: false);
            DropDiagnostics.Record("over", "TestOnly", new string?[] { null, "", "   " }!, accepted: false);
            DropDiagnostics.Record("over", "TestOnly", new[] { "|<>:invalid" }, accepted: false);
        };

        act.Should().NotThrow("a diagnostic must never be the reason a drop fails");
    }

    // ---- Routing, which had no coverage at all -------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheDropRoutingAddsEveryVideo_InTheOrderDropped()
    {
        var vm = Vm();
        var a = Make("a.mp4");
        var b = Make("b.mkv");
        var c = Make("c.mov");

        BulkCutView.HandleDroppedFiles(new[] { a, b, c }, vm).Should().Equal(a, b, c);
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void DuplicatesWithinOneDropCollapse()
    {
        var vm = Vm();
        var a = Make("a.mp4");

        BulkCutView.HandleDroppedFiles(new[] { a, a }, vm).Should().ContainSingle();
    }
}
