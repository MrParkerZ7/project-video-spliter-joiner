using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// D-004 / T-097 — the Bulk Cut drop routing. <see cref="BulkCutView.HandleDroppedFiles"/> is extracted
/// static (like Join/Split) so the video-filter + add-routing is unit-testable without a window: it
/// keeps only known video paths (order preserved), routes them to
/// <see cref="BulkCutViewModel.AddFilesCommand"/>, and is a no-op when nothing survives the filter.
/// (The rows are appended synchronously before the first probe await, so Items reflects them at once.)
/// </summary>
public sealed class BulkCutViewDropRoutingTests
{
    private static BulkCutViewModel BuildVm()
        => new(new BulkFakeProbe(), new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), new FakeBulkTrimEngine());

    [Fact]
    public void HandleDroppedFiles_FiltersNonVideos_AddsOnlyVideosInOrder()
    {
        var vm = BuildVm();
        var dropped = new[] { @"C:\v\a.mp4", @"C:\v\notes.txt", @"C:\v\b.mkv", @"C:\v\cover.jpg" };

        var added = BulkCutView.HandleDroppedFiles(dropped, vm);

        added.Should().Equal(@"C:\v\a.mp4", @"C:\v\b.mkv");
        vm.Items.Select(i => i.FileName).Should().Equal("a.mp4", "b.mkv");
    }

    [Fact]
    public void HandleDroppedFiles_NoVideos_IsANoOp()
    {
        var vm = BuildVm();
        var dropped = new[] { @"C:\v\notes.txt", @"C:\v\cover.jpg" };

        var added = BulkCutView.HandleDroppedFiles(dropped, vm);

        added.Should().BeEmpty();
        vm.Items.Should().BeEmpty();
    }

    [Fact]
    public void HandleDroppedFiles_PreservesFirstSeenOrder_AndDedupes()
    {
        var vm = BuildVm();
        var dropped = new[] { @"C:\v\b.mkv", @"C:\v\a.mp4", @"C:\v\b.mkv" };

        var added = BulkCutView.HandleDroppedFiles(dropped, vm);

        added.Should().Equal(@"C:\v\b.mkv", @"C:\v\a.mp4");
        vm.Items.Select(i => i.FileName).Should().Equal("b.mkv", "a.mp4");
    }
}
