using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-192 (G-059, SPEC-001 I45 / I72) - an H.264 Exact head is encoded at x264 <c>veryfast</c> with a quality floor.
///
/// <para>The head is at most one GOP, so its SIZE does not matter; its speed and its quality do. libx264 at its defaults
/// (<c>medium</c>, crf 23) costs 4.94 s for the head at 104.5 s of the 4K fixture, <c>veryfast</c> 2.16 s (T-192's measurement). The
/// quality floor is relative, never a figure: the head must score no more than 0.5 dB PSNR below a default-settings head of the same
/// frames, and its profile must never be lower than the source's (the concat output takes its <c>avcC</c> from the head, so the
/// declared profile must cover the copied part). <c>ultrafast</c> is rejected: it declares Constrained Baseline over a High source.</para>
///
/// <para>This file is the unit half (the tokens, the profile comparator); the real-media half is
/// <see cref="SmartCutHeadQualityIntegrationTests"/>.</para>
/// </summary>
public sealed class SmartCutHeadQualityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-t192u-" + Guid.NewGuid().ToString("N"));

    public SmartCutHeadQualityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static MediaInfo Info(string video = "h264", string audio = "aac") => new(
        S(60), "mov,mp4,m4a,3gp,3g2,mj2",
        new[] { new StreamInfo(0, video, "video", 1920, 1080, "yuv420p", null, null, "1/12800") },
        new[] { new StreamInfo(1, audio, "audio", null, null, null, 48000, 2, "1/48000") });

    private static List<string> HeadTokens(string videoEncoder, string audioEncoder = "aac") =>
        SmartCutArgsBuilder.HeadReencode(
            @"C:\v\clip.mp4", S(5), S(8), Info(), videoEncoder, audioEncoder, @"C:\v\head.mp4").ToList().ToList();

    // serves-spec: SPEC-001#I45 / SPEC-001#I72 - the repro, written first. A libx264 head is `-c:v libx264 -preset veryfast -crf N`:
    // the preset and the crf directly after the codec, both named constants of the builder (one place to change them). The shipped
    // head emitted neither, so libx264 ran at medium / crf 23.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ALibx264Head_CarriesThePresetAndTheCrf_RightAfterItsCodec()
    {
        var tokens = HeadTokens("libx264");

        var codec = tokens.IndexOf("-c:v");
        tokens[codec + 1].Should().Be("libx264");
        tokens.Skip(codec + 2).Take(4).Should().Equal(
            new[] { "-preset", "veryfast", "-crf", SmartCutArgsBuilder.X264HeadCrf.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            "the preset and the crf come immediately after `-c:v libx264`");
        SmartCutArgsBuilder.X264HeadPreset.Should().Be("veryfast");
        tokens.Count(t => t == "-preset").Should().Be(1);
        tokens.Count(t => t == "-crf").Should().Be(1);
        tokens.Should().NotContain("ultrafast", "ultrafast declares Constrained Baseline over a High source: rejected");
    }

    // serves-spec: SPEC-001#I45 - the crf is a sane x264 value (the range x264 accepts is 0-51; a head near lossless would be huge and
    // one above ~28 visibly worse than the source): pinned so a typo cannot ship.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheHeadCrf_IsAnX264Value_BetweenVisuallyLosslessAndTheDefault()
    {
        SmartCutArgsBuilder.X264HeadCrf.Should().BeInRange(12, 23, "veryfast at crf 23 is 1.2 dB below the default head, so the constant is lower than the default 23");
    }

    // serves-spec: SPEC-001#I45 - every other encoder is unchanged: no -preset and no -crf, and the tokens are exactly the ones the
    // head had (codec, pix_fmt, size, the audio codec/rate/channels).
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("libvpx-vp9")]
    [InlineData("libvpx")]
    [InlineData("libsvtav1")]
    [InlineData("mpeg4")]
    [InlineData("mpeg2video")]
    public void EveryOtherEncoder_KeepsItsDefaults(string encoder)
    {
        var tokens = HeadTokens(encoder);

        tokens.Should().NotContain("-preset").And.NotContain("-crf");
        tokens.Skip(tokens.IndexOf("-y")).Should().Equal(new[]
        {
            "-y", "-ss", "5", "-i", @"C:\v\clip.mp4", "-t", "3", "-map", "0",
            "-c:v", encoder, "-pix_fmt", "yuv420p", "-s", "1920x1080",
            "-c:a", "aac", "-ar", "48000", "-ac", "2", @"C:\v\head.mp4",
        });
    }

    // serves-spec: SPEC-001#I45 - libx264 is the only encoder that gets the settings, also with no audio.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ALibx264HeadOfASilentVideo_CarriesThemToo()
    {
        var silent = new MediaInfo(S(60), "mov,mp4", Info().VideoStreams, Array.Empty<StreamInfo>());

        var tokens = SmartCutArgsBuilder.HeadReencode(@"C:\v\clip.mp4", S(5), S(8), silent, "libx264", null, @"C:\v\head.mp4").ToList().ToList();

        tokens.Should().ContainInOrder("-c:v", "libx264", "-preset", "veryfast", "-crf");
    }

    // ---- a FullReencode runs the same head command (its one run is the whole output) ----------------------------------

    private sealed class FixedProbe : IMediaProbe
    {
        private readonly IReadOnlyList<TimeSpan> _keyframes;

        public FixedProbe(IReadOnlyList<TimeSpan> keyframes) => _keyframes = keyframes;

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) => Task.FromResult(ProbeResult.Success(Info()));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) => Task.FromResult(_keyframes);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
            throw new NotSupportedException();

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    // serves-spec: SPEC-001#I45 - the engine's FullReencode (a range inside the last GOP, no copyable tail) runs the SAME head command, so
    // its one run carries the preset and the crf too.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task AFullReencode_RunsTheSameHeadSettings()
    {
        var input = Path.Combine(_dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "bytes");
        var runner = new RecordingFakeRunner();
        var keyframes = Enumerable.Range(0, 30).Select(i => S(i * 2)).ToList(); // the last keyframe is 58 s of 60 s

        var result = await new SmartCutEngine(runner, new FixedProbe(keyframes)).CutAsync(input, S(59), null, Path.Combine(_dir, "cut.mp4"));

        result.Strategy.Should().Be(SmartCutStrategy.FullReencode);
        result.FellBack.Should().BeFalse(result.FallbackReason);
        var tokens = runner.Commands.Should().ContainSingle("a FullReencode is one run").Which;
        tokens.Should().ContainInOrder("-c:v", "libx264", "-preset", "veryfast", "-crf");
    }

    // ---- the profile comparator (the rule's checker; the integration test uses it on ffprobe's profile names) -----------

    // serves-spec: SPEC-001#I72 - the order is Baseline < Main < High < High 10 < High 4:2:2 < High 4:4:4, and a head's profile must
    // never be below the source's: the comparator rejects a Constrained Baseline head over a High source (the ultrafast case) and a
    // Main head over a High one, and accepts High over High and High 10 over High.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("Constrained Baseline", "High", false)]
    [InlineData("Baseline", "High", false)]
    [InlineData("Main", "High", false)]
    [InlineData("High", "High 10", false)]
    [InlineData("High", "High", true)]
    [InlineData("High 10", "High", true)]
    [InlineData("High", "Main", true)]
    [InlineData("High", "Constrained Baseline", true)]
    [InlineData("High 4:2:2", "High 10", true)]
    [InlineData("High 4:4:4 Predictive", "High 4:2:2", true)]
    [InlineData("High 10", "High 4:2:2", false)]
    public void TheProfileComparator_AcceptsOnlyAHeadThatCoversTheSource(string head, string source, bool accepted)
    {
        H264ProfileRank.Covers(head, source).Should().Be(accepted);
    }

    // serves-spec: SPEC-001#I72 - a profile name the comparator does not know is never "covered" (a test must not pass on a typo).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheProfileComparator_RefusesAProfileItDoesNotKnow()
    {
        var act = () => H264ProfileRank.Covers("High", "Some Future Profile");

        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>ffprobe's H.264 profile names in the order the rule uses (T-192): a head covers a source when its rank is not lower.</summary>
internal static class H264ProfileRank
{
    private static readonly string[] Order =
    {
        "Constrained Baseline", "Baseline", "Main", "High", "High 10", "High 4:2:2", "High 4:4:4 Predictive",
    };

    public static int Of(string profile)
    {
        var rank = Array.FindIndex(Order, p => string.Equals(p, profile, StringComparison.OrdinalIgnoreCase));
        return rank >= 0 ? Math.Max(rank - 1, 0) : throw new ArgumentException($"Unknown H.264 profile '{profile}'.", nameof(profile));
    }

    public static bool Covers(string head, string source) => Of(head) >= Of(source);
}
