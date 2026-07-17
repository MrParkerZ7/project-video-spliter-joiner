using FluentAssertions;
using VideoSplitJoiner.Core.Errors;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Unit tests for the copyable-error surface on <see cref="UserFacingError"/> (T-037): the
/// <see cref="UserFacingError.CopyText"/> aggregates headline + detail + log path, and
/// <see cref="UserFacingError.DetailText"/> prefers the full text over the tail.
/// </summary>
public sealed class UserFacingErrorTests
{
    [Fact]
    public void CopyText_IncludesHeadline_Hint_FullText_AndLogPath()
    {
        var err = new UserFacingError(
            ErrorCategory.Unknown,
            "The operation failed (exit code -22).",
            RawTail: "short tail",
            Hint: "See the details.",
            LogFilePath: @"C:\logs\split-20260717-120000.log",
            FullText: "the full stderr\nwith many lines\nConversion failed!");

        var copy = err.CopyText;

        copy.Should().Contain("The operation failed (exit code -22).");
        copy.Should().Contain("See the details.");
        copy.Should().Contain("Conversion failed!", "the FULL text is copied, not the tail");
        copy.Should().Contain(@"C:\logs\split-20260717-120000.log", "the saved log path is copyable");
    }

    [Fact]
    public void DetailText_PrefersFullText_WhenPresent()
    {
        var err = new UserFacingError(
            ErrorCategory.Unknown,
            "headline",
            RawTail: "just the tail",
            FullText: "the full multi-line stderr");

        err.DetailText.Should().Be("the full multi-line stderr");
    }

    [Fact]
    public void DetailText_FallsBackToRawTail_WhenNoFullText()
    {
        var err = new UserFacingError(ErrorCategory.Unknown, "headline", RawTail: "just the tail");

        err.DetailText.Should().Be("just the tail");
        err.HasLogFile.Should().BeFalse();
    }

    [Fact]
    public void HasLogFile_TrueOnlyWhenPathSet()
    {
        new UserFacingError(ErrorCategory.Unknown, "h", "t").HasLogFile.Should().BeFalse();
        new UserFacingError(ErrorCategory.Unknown, "h", "t", LogFilePath: @"C:\x.log").HasLogFile.Should().BeTrue();
    }
}
