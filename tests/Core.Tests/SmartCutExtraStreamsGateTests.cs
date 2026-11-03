using FluentAssertions;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-238 (SPEC-001 I71) - the one predicate that decides whether Exact runs for a source,
/// <see cref="SmartCutArgsBuilder.TryResolveEncoders"/>, refuses a source with a subtitle, a data stream or a cover picture
/// (<see cref="MediaInfo.ExtraStreams"/>), and every older reason still wins when it also applies. No ffmpeg: a probe result
/// is built by hand, so this runs everywhere.
/// </summary>
public sealed class SmartCutExtraStreamsGateTests
{
    private const string HevcReason = "frame-exact cutting is not supported for HEVC/H.265 video yet";

    private static MediaInfo Info(
        string video = "h264", string audio = "aac", string container = "mov,mp4,m4a,3gp,3g2,mj2", double startSeconds = 0, int extraStreams = 0) => new(
        TimeSpan.FromSeconds(30),
        container,
        new[] { new StreamInfo(0, video, "video", 1920, 1080, "yuv420p", null, null, "1/30") },
        new[] { new StreamInfo(1, audio, "audio", null, null, null, 48000, 2, "1/48000") },
        TimeSpan.FromSeconds(startSeconds),
        extraStreams);

    // serves-spec: SPEC-001#I71 - Required-Success of the gate: any number of extra streams refuses Exact with the one sentence, and
    // leaves both encoders null (a caller must not use them).
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void TryResolveEncoders_ASourceWithExtraStreams_IsRefused_WithTheOneReason(int extraStreams)
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info(extraStreams: extraStreams), out var video, out var audio, out var why).Should().BeFalse();

        why.Should().Be(SmartCutArgsBuilder.ExtraStreamsReason);
        video.Should().BeNull();
        audio.Should().BeNull();
        why.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
        why.Should().Contain("subtitle").And.Contain("cover picture").And.Contain("data");
    }

    // serves-spec: SPEC-001#I71 - Optional-Success: a source with no extra stream resolves exactly as before this ticket.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TryResolveEncoders_ASourceWithoutExtraStreams_StillResolves()
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info(), out var video, out var audio, out var why).Should().BeTrue(why);

        video.Should().NotBeNull();
        audio.Should().NotBeNull();
        why.Should().BeNull();
    }

    // serves-spec: SPEC-001#I71 - the boundary: the older reasons are reported as themselves when they also apply, so the extra
    // streams sentence is never the first thing a user is told about a file that has another, more basic problem. The Matroska row
    // is the ticket's "a subtitle in MKV (already refused by name)".
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TryResolveEncoders_AnOlderReason_StillWinsOverTheExtraStreams()
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info(video: "hevc", extraStreams: 1), out _, out _, out var hevc).Should().BeFalse();
        hevc.Should().Be(HevcReason);

        SmartCutArgsBuilder.TryResolveEncoders(Info(startSeconds: 1.4, extraStreams: 1), out _, out _, out var startTime).Should().BeFalse();
        startTime.Should().Be(SmartCutArgsBuilder.NonZeroStartTimeReason);

        SmartCutArgsBuilder.TryResolveEncoders(Info(container: "matroska,webm", extraStreams: 1), out _, out _, out var mkv).Should().BeFalse();
        mkv.Should().Be(SmartCutArgsBuilder.MatroskaUnsupportedReason);

        SmartCutArgsBuilder.TryResolveEncoders(Info(extraStreams: 1), out _, out _, out var name, sourcePath: "a.mts").Should().BeFalse();
        name.Should().Be(SmartCutArgsBuilder.NonZeroStartTimeReason, "a transport-stream name is refused by name, as before");

        SmartCutArgsBuilder.TryResolveEncoders(Info(video: "wmv3", extraStreams: 1), out _, out _, out var codec).Should().BeFalse();
        codec.Should().StartWith("no known encoder for video codec", "an unknown codec is reported as itself");
    }
}
