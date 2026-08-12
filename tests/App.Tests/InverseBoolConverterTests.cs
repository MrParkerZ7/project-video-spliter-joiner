using System.Globalization;
using FluentAssertions;
using VideoSplitJoiner.App.Views;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-091 — the bool inverter that drives the nested Cut-markers ‖ Parts <c>OrientedSplitPanel</c>'s
/// <c>IsVertical</c> from the INVERSE of the main axis, so the markers/parts pair flips OPPOSITE to the
/// outer video/tools split (horizontal MODE → stacked; vertical MODE → side by side).
/// </summary>
public sealed class InverseBoolConverterTests
{
    private static readonly InverseBoolConverter Converter = new();

    [Fact]
    public void Convert_True_ReturnsFalse()
    {
        Converter.Convert(true, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(false, "the inner panel stacks (IsVertical=true) only in horizontal MODE (Main.IsVertical=false)");
    }

    [Fact]
    public void Convert_False_ReturnsTrue()
    {
        Converter.Convert(false, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(true, "horizontal MODE (Main.IsVertical=false) → inner IsVertical=true → markers stacked above parts");
    }

    [Fact]
    public void ConvertBack_IsSymmetric()
    {
        Converter.ConvertBack(true, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(false);
        Converter.ConvertBack(false, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(true);
    }

    [Fact]
    public void Convert_NonBool_TreatedAsFalse_ReturnsTrue()
    {
        // A stray/null binding never throws — a non-true value inverts to true.
        Converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture).Should().Be(true);
    }
}
