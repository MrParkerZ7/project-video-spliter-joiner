using System;
using System.Collections.Generic;
using FluentAssertions;
using VideoSplitJoiner.App;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for the pure, WPF-free <see cref="VideoFileFilter"/> used by the Split/Join
/// drag-drop handlers (T-016). The DragOver/Drop code-behind itself is WPF event wiring and is
/// not unit-tested here — it is compile-verified and live-verified later via the running app.
/// </summary>
public sealed class VideoFileFilterTests
{
    [Fact]
    public void AcceptVideoFiles_KeepsKnownVideoExtensions()
    {
        var input = new[] { @"C:\a.mp4", @"C:\b.mov", @"C:\c.avi", @"C:\d.webm", @"C:\e.ts" };

        VideoFileFilter.AcceptVideoFiles(input)
            .Should().Equal(@"C:\a.mp4", @"C:\b.mov", @"C:\c.avi", @"C:\d.webm", @"C:\e.ts");
    }

    [Fact]
    public void AcceptVideoFiles_ExtensionMatchIsCaseInsensitive()
    {
        var input = new[] { @"C:\a.MP4", @"C:\b.MKV", @"C:\c.Mov" };

        VideoFileFilter.AcceptVideoFiles(input)
            .Should().Equal(@"C:\a.MP4", @"C:\b.MKV", @"C:\c.Mov");
    }

    [Fact]
    public void AcceptVideoFiles_DropsNonVideoAndExtensionless()
    {
        var input = new[] { @"C:\a.txt", @"C:\b.jpg", @"C:\noext", @"C:\keep.mp4" };

        VideoFileFilter.AcceptVideoFiles(input)
            .Should().Equal(@"C:\keep.mp4");
    }

    [Fact]
    public void AcceptVideoFiles_MixedInputReturnsOnlyVideos()
    {
        var input = new[] { @"C:\a.mp4", @"C:\readme.md", @"C:\b.mkv", @"C:\photo.png", @"C:\c.flv" };

        VideoFileFilter.AcceptVideoFiles(input)
            .Should().Equal(@"C:\a.mp4", @"C:\b.mkv", @"C:\c.flv");
    }

    [Fact]
    public void AcceptVideoFiles_DedupesExactDuplicates()
    {
        var input = new[] { @"C:\a.mp4", @"C:\a.mp4", @"C:\b.mkv" };

        VideoFileFilter.AcceptVideoFiles(input)
            .Should().Equal(@"C:\a.mp4", @"C:\b.mkv");
    }

    [Fact]
    public void AcceptVideoFiles_DedupesCaseInsensitivelyOnFullPath()
    {
        var input = new[] { @"C:\Movies\Clip.mp4", @"c:\movies\clip.MP4", @"C:\b.mkv" };

        // Case-different same path collapses to the first-seen form.
        VideoFileFilter.AcceptVideoFiles(input)
            .Should().Equal(@"C:\Movies\Clip.mp4", @"C:\b.mkv");
    }

    [Fact]
    public void AcceptVideoFiles_PreservesFirstSeenOrder()
    {
        var input = new[] { @"C:\c.mov", @"C:\a.mp4", @"C:\b.mkv", @"C:\a.mp4" };

        VideoFileFilter.AcceptVideoFiles(input)
            .Should().Equal(@"C:\c.mov", @"C:\a.mp4", @"C:\b.mkv");
    }

    [Fact]
    public void AcceptVideoFiles_EmptyInputReturnsEmpty()
    {
        VideoFileFilter.AcceptVideoFiles(Array.Empty<string>()).Should().BeEmpty();
    }

    [Fact]
    public void AcceptVideoFiles_NullInputReturnsEmpty()
    {
        VideoFileFilter.AcceptVideoFiles(null).Should().BeEmpty();
    }

    [Fact]
    public void AcceptVideoFiles_SkipsNullAndWhitespaceEntries()
    {
        var input = new[] { null, "", "   ", @"C:\a.mp4" };

        VideoFileFilter.AcceptVideoFiles(input!)
            .Should().Equal(@"C:\a.mp4");
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    [InlineData(".mov")]
    [InlineData(".avi")]
    [InlineData(".m4v")]
    [InlineData(".webm")]
    [InlineData(".ts")]
    [InlineData(".mpg")]
    [InlineData(".mpeg")]
    [InlineData(".wmv")]
    [InlineData(".flv")]
    public void AcceptVideoFiles_AcceptsEveryDocumentedVideoExtension(string ext)
    {
        var input = new[] { @"C:\clip" + ext };

        VideoFileFilter.AcceptVideoFiles(input).Should().ContainSingle();
    }

    [Fact]
    public void HasAnyVideo_TrueWhenAtLeastOneVideoPresent()
    {
        VideoFileFilter.HasAnyVideo(new[] { @"C:\a.txt", @"C:\b.mp4" }).Should().BeTrue();
    }

    [Fact]
    public void HasAnyVideo_FalseWhenNoVideosPresent()
    {
        VideoFileFilter.HasAnyVideo(new[] { @"C:\a.txt", @"C:\b.jpg", @"C:\noext" }).Should().BeFalse();
    }

    [Fact]
    public void HasAnyVideo_FalseForEmptyOrNull()
    {
        VideoFileFilter.HasAnyVideo(Array.Empty<string>()).Should().BeFalse();
        VideoFileFilter.HasAnyVideo(null).Should().BeFalse();
    }

    [Fact]
    public void HasAnyVideo_IsCaseInsensitive()
    {
        VideoFileFilter.HasAnyVideo(new[] { @"C:\a.MP4" }).Should().BeTrue();
    }
}
