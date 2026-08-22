using System;
using FluentAssertions;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Unit tests for <see cref="CutProfile"/> (T-102): the plain, Core-resident cut-profile record and its
/// construction-time validation — non-empty (trimmed) name, non-negative offsets, an optional
/// (nullable) outro. Value semantics come from the record; these pin the guardrails.
/// </summary>
public sealed class CutProfileTests
{
    [Fact]
    public void Construct_WithValidValues_ExposesThem()
    {
        var profile = new CutProfile("Intro/Outro", TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20));

        profile.Name.Should().Be("Intro/Outro");
        profile.IntroFromStart.Should().Be(TimeSpan.FromSeconds(12));
        profile.OutroFromEnd.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Construct_WithNullOutro_MeansKeepToEof()
    {
        var profile = new CutProfile("Intro only", TimeSpan.FromSeconds(8), null);

        profile.OutroFromEnd.Should().BeNull("a null outro ⇒ the keep runs to EOF (no tail trim)");
    }

    [Fact]
    public void Construct_WithZeroOffsets_IsAllowed()
    {
        var act = () => new CutProfile("Zero", TimeSpan.Zero, TimeSpan.Zero);

        act.Should().NotThrow("zero is non-negative — a valid (degenerate) offset");
    }

    [Fact]
    public void Name_IsStoredTrimmed()
    {
        var profile = new CutProfile("  Series A  ", TimeSpan.FromSeconds(5), null);

        profile.Name.Should().Be("Series A", "surrounding whitespace is trimmed so it never splits the dedup key");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Construct_WithBlankName_Throws(string? name)
    {
        var act = () => new CutProfile(name!, TimeSpan.FromSeconds(5), null);

        act.Should().Throw<ArgumentException>("a profile name must be non-empty");
    }

    [Fact]
    public void Construct_WithNegativeIntro_Throws()
    {
        var act = () => new CutProfile("Bad intro", TimeSpan.FromSeconds(-1), null);

        act.Should().Throw<ArgumentOutOfRangeException>("offsets must be non-negative");
    }

    [Fact]
    public void Construct_WithNegativeOutro_Throws()
    {
        var act = () => new CutProfile("Bad outro", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>("a present outro must be non-negative");
    }

    [Fact]
    public void ValueEquality_TwoProfilesWithSameValues_AreEqual()
    {
        var a = new CutProfile("Same", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7));
        var b = new CutProfile("Same", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7));

        a.Should().Be(b, "records compare by value");
    }
}
