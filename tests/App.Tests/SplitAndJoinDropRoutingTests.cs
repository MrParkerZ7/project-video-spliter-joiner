using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-154 (SPEC-010 / SPEC-012) — drop routing on Split and Join, which had no coverage at all.
///
/// <para>All three screens carry a <c>HandleDroppedFiles</c> commented <i>"extracted for testability"</i>,
/// and until this ticket not one of them was tested. That is how an 11-entry extension allowlist missing
/// <c>.m2ts</c>, <c>.mts</c> and <c>.3gp</c> stayed in for so long: dragging one of those did nothing,
/// said nothing, and nothing in CI disagreed.</para>
///
/// <para>These cover the routing contract each screen actually has — Split takes the FIRST video (it
/// loads one file), Join takes ALL of them — plus the shared filter behaviour they both depend on.</para>
/// </summary>
public sealed class SplitAndJoinDropRoutingTests : IDisposable
{
    private readonly string _dir;

    public SplitAndJoinDropRoutingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t154-sj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }


    /// <summary>Minimal <see cref="IJoinEngine"/>: these tests never run a join, only route a drop.</summary>
    private sealed class InertJoinEngine : IJoinEngine
    {
        public Task<CompatReport> CheckCompatibilityAsync(
            IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => throw new InvalidOperationException("no compat check should be needed to route a drop");

        public Task<JoinResult> JoinAsync(
            JoinRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null)
            => Task.FromResult(JoinResult.Ok(req.OutputPath));
    }

    private string Make(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "placeholder — the filter only reads the extension");
        return p;
    }

    // ---- The shared filter, which is what actually broke ---------------------------------------------

    [Fact]
    public void TheContainersBothScreensAcceptIncludeTheOnesThatWereMissing()
    {
        var dropped = new[]
        {
            Make("a.m2ts"), Make("b.mts"), Make("c.3gp"), Make("d.vob"), Make("e.mxf"), Make("f.mp4"),
        };

        VideoFileFilter.AcceptVideoFiles(dropped).Should().HaveCount(
            6, "every one is a container ffmpeg handles — refusing them silently is the reported bug");
    }

    [Fact]
    public void OrderIsPreservedAndDuplicatesCollapse()
    {
        var a = Make("a.mp4");
        var b = Make("b.mkv");

        VideoFileFilter.AcceptVideoFiles(new[] { b, a, b }).Should().Equal(
            new[] { b, a }, "first-seen order, deduped — a drop must not silently reorder someone's clips");
    }

    [Fact]
    public void NullAndBlankPathsAreSurvivable()
    {
        VideoFileFilter.AcceptVideoFiles(new string?[] { null, "", "   " }!).Should().BeEmpty();
        VideoFileFilter.AcceptVideoFiles(null).Should().BeEmpty();
        VideoFileFilter.HasAnyVideo(null).Should().BeFalse();
    }

    [Fact]
    public void AFileWithNoExtensionIsNotGuessedAt()
    {
        VideoFileFilter.AcceptVideoFiles(new[] { Make("no-extension") }).Should().BeEmpty(
            "guessing would hand ffmpeg an unknown blob; the fix for this class is to SAY SO, not to guess");
    }

    // ---- Split: loads the FIRST video --------------------------------------------------------------

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void SplitRoutesTheFirstDroppedVideo_SkippingNonVideosBeforeIt()
    {
        var vm = new SplitViewModel(new BulkFakeProbe(), new ThrowingFakeSplitEngine(), player: null, new FakeSettings());
        var notes = Make("notes.txt");
        var first = Make("a.mp4");
        var second = Make("b.mkv");

        var considered = SplitView.HandleDroppedFiles(new[] { notes, first, second }, vm);

        considered.Should().Equal(
            new[] { first, second }, "the non-video is filtered before the choice is made");
        considered[0].Should().Be(first, "Split loads ONE file — the first video dropped");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void SplitIgnoresADropWithNoVideoInIt()
    {
        var vm = new SplitViewModel(new BulkFakeProbe(), new ThrowingFakeSplitEngine(), player: null, new FakeSettings());

        SplitView.HandleDroppedFiles(new[] { Make("notes.txt"), Make("photo.jpg") }, vm)
            .Should().BeEmpty("nothing loadable — and nothing should be loaded");
    }

    // ---- Join: takes ALL of them -------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public void JoinRoutesEveryDroppedVideo_InOrder()
    {
        var vm = new JoinViewModel(new InertJoinEngine(), new BulkFakeProbe());
        var a = Make("a.mp4");
        var b = Make("b.m2ts");
        var c = Make("c.mkv");

        var considered = JoinView.HandleDroppedFiles(new[] { a, Make("notes.txt"), b, c }, vm);

        considered.Should().Equal(
            new[] { a, b, c }, "Join concatenates — it takes them all, in the order dropped");
    }

    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public void JoinIgnoresADropWithNoVideoInIt()
    {
        var vm = new JoinViewModel(new InertJoinEngine(), new BulkFakeProbe());

        JoinView.HandleDroppedFiles(new[] { Make("archive.zip") }, vm).Should().BeEmpty();
    }
}
