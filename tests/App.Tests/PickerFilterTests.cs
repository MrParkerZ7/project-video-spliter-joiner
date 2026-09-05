using System;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.App;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-158 (SPEC-010 / SPEC-011 / SPEC-012) — the picker offers exactly what the app accepts.
///
/// <para>Every screen has TWO doors: drag-and-drop, and the file picker. T-154 fixed the drop side and
/// left the picker carrying a hand-typed seven-extension list while <see cref="VideoFileFilter"/> had
/// grown to 26. So an <c>.m2ts</c> — the very format whose absence produced the original report — was
/// invisible under "Video files" even though dropping one worked, and the door a frustrated user falls
/// back to was the stale one.</para>
///
/// <para>The cure is derivation, not a longer hand-typed string: adding an extension to the accept-list
/// now changes what the picker offers, with nothing else to remember. This pins that.</para>
/// </summary>
public sealed class PickerFilterTests
{
    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void ThePickerOffersEveryExtensionTheAppAccepts()
    {
        var filter = VideoFileFilter.DialogFilter;

        // Probe the accept-list through its public API rather than reflecting at the private set: if a
        // format is accepted on a drop, the picker must offer it.
        var accepted = new[]
        {
            ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv",
            ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".mpe", ".m2v", ".m1v", ".vob",
            ".3gp", ".3g2", ".ogv", ".asf", ".divx", ".f4v", ".mxf", ".rm", ".rmvb",
        };

        foreach (var ext in accepted)
        {
            VideoFileFilter.HasAnyVideo(new[] { "x" + ext }).Should().BeTrue(
                $"{ext} is on the accept-list, so this test's own list is current");

            filter.Should().Contain(
                "*" + ext,
                $"a dropped {ext} is accepted, so the picker must offer it too — the two doors into a " +
                "screen cannot disagree about what this app can open");
        }
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void TheFilterKeepsTheAllFilesEscapeHatch()
    {
        // An allowlist is a guess about a container; "All files" is how someone opens the one we got
        // wrong. Anything chosen through it that the app cannot use is now refused in words, not silence.
        VideoFileFilter.DialogFilter.Should().EndWith("|All files|*.*");
    }

    [Trait("serves-spec", "SPEC-010")]
    [Fact]
    public void TheFilterIsDerived_SoItCannotDriftFromTheAcceptList()
    {
        // The stale seven-extension string is exactly what this must never look like again.
        var filter = VideoFileFilter.DialogFilter;
        var offered = filter.Split('|')[1].Split(';');

        offered.Should().HaveCountGreaterThan(
            20, "a hand-typed subset is how the picker fell 19 extensions behind the drop path");
        offered.Should().OnlyContain(p => p.StartsWith("*.", StringComparison.Ordinal));
    }
}
