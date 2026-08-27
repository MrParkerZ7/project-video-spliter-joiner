using System;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-118 (epic G-041) — the Bulk row's editable IN/OUT time field must never write a VM-rendered value
/// back into <c>Requested</c>. The field displays the keyframe-SNAPPED time truncated to 0.1s; before
/// T-118 a plain focus pass re-committed that text, silently replacing the user's real request (e.g. 5s)
/// with the snapped value (4s), zeroing the snap delta and sending the truncated time to the engine.
/// Also covers the clock parser, which moved here from the view code-behind and had no tests.
/// </summary>
public sealed class CutTimeCommitTests
{
    // ---- The corruption guard ----------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void UntouchedField_IsUnchanged_SoNothingIsCommitted()
    {
        // The field renders the SNAPPED 4s while the user's real request was 5s.
        var rendered = TimeSpan.FromSeconds(4);

        CutTimeCommit.IsUnchanged(CutMarkerViewModel.FormatClock(rendered), rendered).Should().BeTrue();
        CutTimeCommit.TryResolveEdit(CutMarkerViewModel.FormatClock(rendered), rendered, out _)
            .Should().BeFalse("a focus pass over an unedited field must never overwrite Requested with the snapped value");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void UntouchedField_ToleratesSurroundingWhitespace()
    {
        var rendered = TimeSpan.FromSeconds(4);
        var text = "  " + CutMarkerViewModel.FormatClock(rendered) + " ";

        CutTimeCommit.TryResolveEdit(text, rendered, out _).Should().BeFalse();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void RealEdit_IsCommitted_WithTheTypedValue()
    {
        var rendered = TimeSpan.FromSeconds(4);

        CutTimeCommit.TryResolveEdit("00:06.0", rendered, out var t).Should().BeTrue();
        t.Should().Be(TimeSpan.FromSeconds(6), "a genuine typed edit still commits");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void UnparseableEdit_IsRejected_WithoutAValue()
    {
        var rendered = TimeSpan.FromSeconds(4);

        CutTimeCommit.TryResolveEdit("not a time", rendered, out var t).Should().BeFalse();
        t.Should().Be(TimeSpan.Zero, "a rejected parse yields no value for the caller to write");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void SubTenthPrecision_IsNotDestroyedByAFocusPass()
    {
        // The display truncates to 0.1s: 4.06s renders as "00:04.0". A focus pass must NOT commit
        // "00:04.0" back over the real 4.06s request.
        var real = TimeSpan.FromSeconds(4.06);
        var displayed = CutMarkerViewModel.FormatClock(real);

        displayed.Should().Be("00:04.0", "the renderer truncates tenths");
        CutTimeCommit.TryResolveEdit(displayed, real, out _)
            .Should().BeFalse("committing the truncated render back would silently lose the sub-tenth precision");
    }

    // ---- The clock parser (moved out of the view; previously untested) ------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData("12", 12)]           // plain seconds
    [InlineData("01:30", 90)]        // mm:ss
    [InlineData("00:04.5", 4.5)]     // mm:ss.f
    [InlineData("1:00:00", 3600)]    // h:mm:ss
    [InlineData(" 2:05 ", 125)]      // surrounding whitespace
    public void TryParseClock_ParsesTheSupportedShapes(string text, double expectedSeconds)
    {
        CutTimeCommit.TryParseClock(text, out var t).Should().BeTrue();
        t.Should().BeCloseTo(TimeSpan.FromSeconds(expectedSeconds), TimeSpan.FromMilliseconds(1));
    }

    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]          // too many segments
    [InlineData("-5")]               // negative
    public void TryParseClock_RejectsInvalidInput(string? text)
    {
        CutTimeCommit.TryParseClock(text, out var t).Should().BeFalse();
        t.Should().Be(TimeSpan.Zero);
    }
}
