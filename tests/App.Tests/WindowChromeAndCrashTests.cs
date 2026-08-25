using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-105 coverage for the pure logic extracted from the WPF chrome/crash code so it is testable
/// without a real window or a raised dispatcher exception (SPEC-015 I23/I24): the maximized-bounds
/// work-area clamp (<see cref="WindowChromeMath"/>) and the crash-dialog message composition
/// (<see cref="CrashReport"/>). The views/handlers delegate to these — behaviour is unchanged.
/// </summary>
public sealed class WindowChromeAndCrashTests
{
    // ---- SPEC-015 I23 — WindowChromeMath.MaximizedWorkAreaBounds ------------------------------

    [Trait("serves-spec", "SPEC-015")]
    [Fact]
    public void MaximizedBounds_TaskbarBottom_FillsWorkArea()
    {
        // 1920×1080 monitor, 40px taskbar at the bottom → work 0,0..1920,1040.
        var b = WindowChromeMath.MaximizedWorkAreaBounds(
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040, fullLeft: 0, fullTop: 0);
        b.Should().Be((0, 0, 1920, 1040)); // pos at monitor origin, size = work extent
    }

    [Trait("serves-spec", "SPEC-015")]
    [Fact]
    public void MaximizedBounds_TaskbarLeft_OffsetsPositionAndShrinksWidth()
    {
        // 80px taskbar on the left → work starts at x=80, width 1840.
        var b = WindowChromeMath.MaximizedWorkAreaBounds(
            workLeft: 80, workTop: 0, workRight: 1920, workBottom: 1080, fullLeft: 0, fullTop: 0);
        b.Should().Be((80, 0, 1840, 1080));
    }

    [Trait("serves-spec", "SPEC-015")]
    [Fact]
    public void MaximizedBounds_TaskbarTop_OffsetsY()
    {
        var b = WindowChromeMath.MaximizedWorkAreaBounds(
            workLeft: 0, workTop: 40, workRight: 1920, workBottom: 1080, fullLeft: 0, fullTop: 0);
        b.Should().Be((0, 40, 1920, 1040));
    }

    [Trait("serves-spec", "SPEC-015")]
    [Fact]
    public void MaximizedBounds_SecondaryMonitorNegativeOrigin_IsMonitorRelative()
    {
        // A monitor to the left of primary: origin (-1920,0), 40px bottom taskbar.
        var b = WindowChromeMath.MaximizedWorkAreaBounds(
            workLeft: -1920, workTop: 0, workRight: 0, workBottom: 1040, fullLeft: -1920, fullTop: 0);
        b.Should().Be((0, 0, 1920, 1040)); // position is relative to the monitor origin → 0,0
    }

    // ---- SPEC-015 I24 — CrashReport.ComposeMessage -------------------------------------------

    [Trait("serves-spec", "SPEC-015")]
    [Fact]
    public void ComposeMessage_WithLogPath_IncludesSavedLine()
    {
        var body = CrashReport.ComposeMessage("The app hit an error.", "NullReference", @"C:\logs\crash.txt");
        body.Should().Be(
            "The app hit an error.\n\nNullReference\n\nA crash log was saved to:\nC:\\logs\\crash.txt\n\n(The full details have been copied to your clipboard.)");
    }

    [Trait("serves-spec", "SPEC-015")]
    [Fact]
    public void ComposeMessage_NoLogPath_OmitsSavedLine()
    {
        var body = CrashReport.ComposeMessage("The app hit an error.", "NullReference", null);
        body.Should().Be(
            "The app hit an error.\n\nNullReference\n\n(The full details have been copied to your clipboard.)");
        body.Should().NotContain("A crash log was saved");
    }
}
