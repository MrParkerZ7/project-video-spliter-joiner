using System;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-131 (SPEC-013) — which paths the preview player can address, and what it says when it cannot.
///
/// <para><b>The defect.</b> The player opened every file with
/// <c>new Uri(path, UriKind.RelativeOrAbsolute)</c> inside a catch-all that surfaced <c>ex.Message</c>
/// verbatim, so a user whose videos live on a share called <c>\\Seagate NAS\</c> was shown
/// <i>"Invalid URI: The hostname could not be parsed."</i> — .NET's words, no cause, no way forward. And
/// because the set-at-playhead buttons need <c>Player.IsReady</c>, the failure took the normal way of
/// placing cuts with it.</para>
///
/// <para>The backslash-heavy path shapes below are built from a <c>char</c> code point rather than
/// written as literals: every escaping layer between here and a test fixture collapses <c>\\</c>, and a
/// silently-single backslash would make these assertions test nothing at all.</para>
/// </summary>
public sealed class MediaSourceUriTests
{
    private const char B = (char)92;                       // backslash
    private static readonly string S1 = new(B, 1);
    private static readonly string S2 = new(B, 2);

    private static string Unc(string host, string rest) => S2 + host + S1 + rest.Replace("/", S1);

    private static string Local(string drive, string rest) => drive + ":" + S1 + rest.Replace("/", S1);

    // ---- Paths that must keep working, exactly as before -------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Theory]
    [InlineData("C", "videos/a.mp4")]
    [InlineData("C", "videos/my video.mp4")]        // a space in the FILE name was never the problem
    [InlineData("Z", "Videos/ep 1.mp4")]            // a mapped drive — the workaround the message names
    public void OrdinaryLocalPaths_AreAccepted(string drive, string rest)
    {
        var path = Local(drive, rest);

        MediaSourceUri.TryCreate(path, out var uri).Should().BeTrue();
        uri.Should().NotBeNull();
        uri!.IsFile.Should().BeTrue("a local path addresses a file");
    }

    [Trait("serves-spec", "SPEC-013")]
    [Theory]
    [InlineData("NAS")]
    [InlineData("192.168.1.5")]                     // by IP
    [InlineData("my-nas")]                          // a dash is legal in a host
    [InlineData("my.nas.local")]                    // so are dots
    [InlineData("host_under")]                      // and underscores
    public void UncPathsWithALegalHost_AreAccepted(string host)
    {
        var path = Unc(host, "share/a.mp4");

        MediaSourceUri.TryCreate(path, out var uri).Should().BeTrue(
            "this host shape parses — the refusal must be narrow, not 'all network paths'");
        uri.Should().NotBeNull();
        MediaSourceUri.ExplainRefusal(path).Should().NotBeNull("the helper never throws, even when unused");
    }

    // ---- The reported failure ------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Theory]
    [InlineData("my nas")]                          // the realistic trigger: a space
    [InlineData("Seagate NAS")]
    [InlineData("host:port")]
    [InlineData("host[1]")]
    public void UncPathsWhoseHostCannotBeAUri_AreRefused_NotThrown(string host)
    {
        var path = Unc(host, "share/ep 1.mp4");

        // The point of the seam: it DECIDES rather than throwing, so the caller never has to catch a
        // UriFormatException and surface its wording.
        Action act = () => MediaSourceUri.TryCreate(path, out _);
        act.Should().NotThrow("the decision must be total — throwing is what produced the cryptic message");

        MediaSourceUri.TryCreate(path, out var uri).Should().BeFalse();
        uri.Should().BeNull();
    }

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void TheRefusal_NamesTheShare_TheWorkaround_AndThatCuttingStillWorks()
    {
        var message = MediaSourceUri.ExplainRefusal(Unc("Seagate NAS", "Videos/ep1.mp4"));

        message.Should().Contain("Seagate NAS", "the user has to know WHICH share is the problem");
        message.Should().Contain("IN", "cutting is unaffected — the engine never builds a Uri")
            .And.Contain("OUT");
        message.Should().Contain("drive letter", "the mapped-drive workaround genuinely fixes the preview");

        message.Should().NotContain("Invalid URI", "the .NET wording is exactly what this replaces");
        message.Should().NotContain("hostname could not be parsed");
    }

    // ---- The host parser it leans on -----------------------------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void TheUncHost_IsTheServerName_Only()
    {
        MediaSourceUri.TryGetUncHost(Unc("Seagate NAS", "Videos/ep1.mp4")).Should().Be("Seagate NAS");
        MediaSourceUri.TryGetUncHost(S2 + "solo").Should().Be("solo", "a bare server with no share still names one");
        MediaSourceUri.TryGetUncHost(Local("C", "videos/a.mp4")).Should().BeNull("a local path has no host");
        MediaSourceUri.TryGetUncHost(S2).Should().BeNull("a bare separator pair names nothing");
        MediaSourceUri.TryGetUncHost(null).Should().BeNull();
        MediaSourceUri.TryGetUncHost("   ").Should().BeNull();
    }

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void ANonUncRefusal_StillExplainsItself_WithoutInventingAShareName()
    {
        var message = MediaSourceUri.ExplainRefusal("   ");

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("IN", "the cutting-still-works guidance is not host-specific");
        message.Should().NotContain("network share", "there is no share to blame here");
    }

    // ---- Blank input -----------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPaths_AreRefusedQuietly(string? path)
    {
        MediaSourceUri.TryCreate(path, out var uri).Should().BeFalse();
        uri.Should().BeNull();
    }

    // ---- Performance: the decision is pure -------------------------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void TheDecision_TouchesNoDisk_AndIsRepeatable()
    {
        // A path that does not exist on this machine must still be ACCEPTED — the seam answers
        // "can this be addressed", not "is this file here". Probing the disk here would make opening a
        // large network share pay a stat call on every selection change.
        var missing = Local("C", "definitely/not/here/" + Guid.NewGuid().ToString("N") + ".mp4");

        MediaSourceUri.TryCreate(missing, out var first).Should().BeTrue();
        MediaSourceUri.TryCreate(missing, out var second).Should().BeTrue();
        second!.Should().Be(first, "the decision is a pure function of the path");
    }
}
