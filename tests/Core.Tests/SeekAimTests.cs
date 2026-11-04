using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-237, binary-free: the aim of the input seek to a keyframe of an MP4 or QuickTime source is asked of the demuxer
/// (<see cref="ISeekLandingProbe"/>) and is the keyframe's own time unless a seek aimed there lands on the keyframe before it
/// (SPEC-001 I75). A first version aimed at the middle of the GOP by a rule on times; a review found a file with idle stretches
/// where that landed one GOP late and the keyframe's own aim was right, so no rule on times is used. The real-ffmpeg proof is
/// <see cref="LosslessBoundaryIntegrationTests"/> (the <c>vfr*</c> shapes).
/// </summary>
public sealed class SeekAimTests : IDisposable
{
    private readonly string _dir;

    public SeekAimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t237-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private const string MovName = "mov,mp4,m4a,3gp,3g2,mj2";

    /// <summary>A landing probe from a function of the aim; null from the function is "cannot say". Records every aim asked.</summary>
    private sealed class FakeLanding : ISeekLandingProbe
    {
        private readonly Func<TimeSpan, TimeSpan?> _landing;

        public FakeLanding(Func<TimeSpan, TimeSpan?> landing) => _landing = landing;

        public List<TimeSpan> Asked { get; } = new();

        public Task<TimeSpan?> GetSeekLandingAsync(string path, TimeSpan aim, CancellationToken ct = default)
        {
            Asked.Add(aim);
            return Task.FromResult(_landing(aim));
        }
    }

    /// <summary>The landing of a file whose seek lands on the previous keyframe for every aim before <paramref name="landsFrom"/> past the keyframe.</summary>
    private static Func<TimeSpan, TimeSpan?> EarlyUntil(TimeSpan keyframe, TimeSpan previous, double landsFrom) =>
        aim => aim >= keyframe + S(landsFrom) ? keyframe : previous;

    // ---- IsMovContainer ---------------------------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I75 - ffprobe names MP4, QuickTime, M4A, 3GP and 3G2 files "mov,mp4,m4a,3gp,3g2,mj2"; nothing else is.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", true)]
    [InlineData("MOV,MP4,M4A,3GP,3G2,MJ2", true)]
    [InlineData("mp4", true)]
    [InlineData("matroska,webm", false)]
    [InlineData("mpegts", false)]
    [InlineData("avi", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void IsMovContainer_RecognisesFfprobesMp4FormatName(string? container, bool expected) =>
        SplitArgsBuilder.IsMovContainer(container).Should().Be(expected);

    // ---- SeekAim.ChooseAsync ------------------------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I75 - without a probe the aim is the keyframe's own time, as before T-237.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Choose_WithoutAProbe_IsTheKeyframe_Unverified()
    {
        var choice = await SeekAim.ChooseAsync(null, "x.mp4", S(5), S(7.5), default);

        choice.Aim.Should().Be(S(5));
        choice.Outcome.Should().Be(SeekAim.Outcome.Unverified);
    }

    // serves-spec: SPEC-001#I75 - a seek aimed at the keyframe that lands on it keeps the keyframe's own aim, and asks once: every
    // file the aim at the keyframe was right for (constant-frame-rate files, and a file with idle stretches) is untouched.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Choose_WhenTheKeyframesOwnAimLandsOnIt_KeepsItAndAsksOnce()
    {
        var probe = new FakeLanding(_ => S(5));

        var choice = await SeekAim.ChooseAsync(probe, "x.mp4", S(5), S(7.5), default);

        choice.Aim.Should().Be(S(5));
        choice.Outcome.Should().Be(SeekAim.Outcome.OnKeyframe);
        probe.Asked.Should().Equal(S(5));
    }

    // serves-spec: SPEC-001#I75 - a landing within 2 ms of the keyframe is on it (the list's times are rounded), 3 ms is not.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Choose_TheLandingToleranceIsTwoMilliseconds()
    {
        (await SeekAim.ChooseAsync(new FakeLanding(_ => S(5.002)), "x.mp4", S(5), S(7.5), default)).Outcome.Should().Be(SeekAim.Outcome.OnKeyframe);
        (await SeekAim.ChooseAsync(new FakeLanding(_ => S(4.998)), "x.mp4", S(5), S(7.5), default)).Outcome.Should().Be(SeekAim.Outcome.OnKeyframe);
        (await SeekAim.ChooseAsync(new FakeLanding(_ => S(5.003)), "x.mp4", S(5), S(7.5), default)).Outcome.Should().NotBe(SeekAim.Outcome.OnKeyframe);
    }

    // serves-spec: SPEC-001#I75 - T-237 repro: the keyframe's own aim lands on the keyframe before it (the phone-style file: it needs 0.035
    // to 0.1 s more), so the aim moves later in small steps and the first that lands is the answer.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Choose_WhenTheSeekLandsEarly_TheFirstLaterAimThatLandsIsUsed()
    {
        var probe = new FakeLanding(EarlyUntil(S(5), S(2.5), 0.1));

        var choice = await SeekAim.ChooseAsync(probe, "x.mp4", S(5), S(7.5), default);

        choice.Aim.Should().Be(S(5.15), "0.04 and 0.08 past the keyframe still land early; 0.15 lands");
        choice.Outcome.Should().Be(SeekAim.Outcome.Anchored);
        probe.Asked.Should().Equal(S(5), S(5.04), S(5.08), S(5.15));
    }

    // serves-spec: SPEC-001#I75 - the first step already lands for the usual phone-style gap.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Choose_AGapOfAFewHundredthsOfASecond_IsFoundAtTheFirstStep()
    {
        var probe = new FakeLanding(EarlyUntil(S(5), S(2.5), 0.035));

        var choice = await SeekAim.ChooseAsync(probe, "x.mp4", S(5), S(7.5), default);

        choice.Aim.Should().Be(S(5.04));
        probe.Asked.Should().Equal(S(5), S(5.04));
    }

    // serves-spec: SPEC-001#I75 - no aim in reach lands: Unresolved, the aim is the keyframe's own, and the ladder was walked to its end.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Choose_WhenNoAimLands_IsUnresolved_AndTheAimIsTheKeyframe()
    {
        var probe = new FakeLanding(_ => S(2.5));

        var choice = await SeekAim.ChooseAsync(probe, "x.mp4", S(5), S(9), default);

        choice.Aim.Should().Be(S(5));
        choice.Outcome.Should().Be(SeekAim.Outcome.Unresolved);
        probe.Asked.Should().Equal(S(5), S(5.04), S(5.08), S(5.15), S(5.3), S(5.6), S(6.2));
    }

    // serves-spec: SPEC-001#I75 - an aim never reaches what bounds it (the next keyframe, the file's end, the cut's end), within 5 ms.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Choose_NeverAimsAtOrPastTheUpperBound()
    {
        var probe = new FakeLanding(_ => S(2.5));

        var choice = await SeekAim.ChooseAsync(probe, "x.mp4", S(5), S(5.1), default);

        choice.Outcome.Should().Be(SeekAim.Outcome.Unresolved);
        probe.Asked.Should().Equal(new[] { S(5), S(5.04), S(5.08) }, "5.15 is past 5.095, the bound minus its margin");
        probe.Asked.Skip(1).Should().OnlyContain(a => a < S(5.1) - SeekAim.Margin);
    }

    // serves-spec: SPEC-001#I75 - a probe that cannot say (null), at the keyframe or on the way, is no answer: today's aim, unverified.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Choose_WhenTheProbeCannotSay_IsTheKeyframe_Unverified()
    {
        var first = new FakeLanding(_ => null);
        var choice = await SeekAim.ChooseAsync(first, "x.mp4", S(5), S(7.5), default);
        choice.Aim.Should().Be(S(5));
        choice.Outcome.Should().Be(SeekAim.Outcome.Unverified);
        first.Asked.Should().HaveCount(1);

        var later = new FakeLanding(aim => aim == S(5) ? S(2.5) : null);
        choice = await SeekAim.ChooseAsync(later, "x.mp4", S(5), S(7.5), default);
        choice.Aim.Should().Be(S(5));
        choice.Outcome.Should().Be(SeekAim.Outcome.Unverified, "a half-answered ladder is not an answer");
    }

    // serves-spec: SPEC-001#I75 - a probe that throws is the same as one that cannot say; a cancellation is not swallowed.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Choose_AProbeThatThrows_IsUnverified_ButACancellationPropagates()
    {
        var choice = await SeekAim.ChooseAsync(new ThrowingLanding(new InvalidOperationException("boom")), "x.mp4", S(5), S(7.5), default);
        choice.Outcome.Should().Be(SeekAim.Outcome.Unverified);

        var act = () => SeekAim.ChooseAsync(new ThrowingLanding(new OperationCanceledException()), "x.mp4", S(5), S(7.5), default);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ThrowingLanding : ISeekLandingProbe
    {
        private readonly Exception _ex;

        public ThrowingLanding(Exception ex) => _ex = ex;

        public Task<TimeSpan?> GetSeekLandingAsync(string path, TimeSpan aim, CancellationToken ct = default) => throw _ex;
    }

    // ---- The per-segment path (SplitEngine) ----------------------------------------------------------------------------------

    private static MediaInfo Info(string container) =>
        new(
            S(10),
            container,
            new[] { new StreamInfo(0, "h264", "video", 320, 240, "yuv420p", null, null, "1/1000", 30, null, TimeSpan.Zero) },
            new[] { new StreamInfo(1, "aac", "audio", null, null, null, 44100, 2, "1/1000", null, null, TimeSpan.Zero) },
            TimeSpan.Zero);

    private static readonly IReadOnlyList<TimeSpan> Keyframes = Enumerable.Range(0, 10).Select(i => S(i)).ToList();

    /// <summary>A fixed-info probe that also answers where a seek lands.</summary>
    private sealed class LandingInfoProbe : IMediaProbe, ISeekLandingProbe
    {
        private readonly FixedInfoProbe _inner;
        private readonly Func<TimeSpan, TimeSpan?> _landing;

        public LandingInfoProbe(MediaInfo info, IReadOnlyList<TimeSpan> keyframes, Func<TimeSpan, TimeSpan?> landing)
        {
            _inner = new FixedInfoProbe(info, keyframes);
            _landing = landing;
        }

        public List<TimeSpan> Asked { get; } = new();

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) => _inner.ProbeAsync(path, ct);

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) => _inner.GetKeyframesAsync(path, ct);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) => _inner.SnapToNearestKeyframe(keyframes, requested);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => _inner.AverageGop(keyframes);

        public Task<TimeSpan?> GetSeekLandingAsync(string path, TimeSpan aim, CancellationToken ct = default)
        {
            Asked.Add(aim);
            return Task.FromResult(_landing(aim));
        }
    }

    private async Task<(RecordingTimingRunner Runner, SplitResult Result)> SubsetAsync(
        IMediaProbe probe, string extension, int[] parts, params double[] cuts)
    {
        var input = Path.Combine(_dir, "src" + extension);
        File.WriteAllText(input, "placeholder");
        var outDir = Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var runner = new RecordingTimingRunner();

        var result = await new SplitEngine(runner, probe).SplitAsync(
            new SplitRequest(input, cuts.Select(S).ToList(), outDir, SelectedSegmentIndices: parts));
        return (runner, result);
    }

    private static string ValueAfter(IReadOnlyList<string> tokens, string flag)
    {
        var i = tokens.ToList().IndexOf(flag);
        i.Should().BeGreaterThanOrEqualTo(0, $"the command must carry {flag}");
        return tokens[i + 1];
    }

    // serves-spec: SPEC-001#I75 - an MP4 whose seek lands on the keyframe is cut with today's tokens, -ss the keyframe, whatever the GOP.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMp4_WhereTheKeyframesOwnAimLands_KeepsTheSeekAtTheKeyframe()
    {
        var probe = new LandingInfoProbe(Info(MovName), Keyframes, aim => S(Math.Floor(aim.TotalSeconds)));

        var (runner, result) = await SubsetAsync(probe, ".mp4", new[] { 2, 3 }, 3, 6);

        ValueAfter(runner.Runs[0].Tokens, "-ss").Should().Be("3");
        ValueAfter(runner.Runs[0].Tokens, "-to").Should().Be("3");
        ValueAfter(runner.Runs[1].Tokens, "-ss").Should().Be("6");
        runner.Runs[1].Tokens.Should().NotContain("-to");
        probe.Asked.Should().Equal(new[] { S(3), S(6) }, "one question per part that does not start at the first keyframe");
        result.Warnings.Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I75 - T-237: where the seek lands early, -ss is the aim that lands, -to is measured from it so the part still
    // ends at its end, and the run's progress length is still counted from the keyframe.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMp4_WhereTheSeekLandsEarly_AimsAtTheFirstLaterAimThatLands()
    {
        var probe = new LandingInfoProbe(Info(MovName), Keyframes, aim =>
        {
            var k = Math.Floor(aim.TotalSeconds);
            return aim.TotalSeconds - k >= 0.1 ? S(k) : S(k - 1);
        });

        var (runner, result) = await SubsetAsync(probe, ".mov", new[] { 2, 3 }, 3, 6);

        ValueAfter(runner.Runs[0].Tokens, "-ss").Should().Be("3.15");
        ValueAfter(runner.Runs[0].Tokens, "-to").Should().Be("2.85", "the part ends at the keyframe 6: 2.85 s from the aim");
        runner.Runs[0].TotalDuration!.Value.TotalSeconds.Should().BeApproximately(3, 1e-6);
        ValueAfter(runner.Runs[1].Tokens, "-ss").Should().Be("6.15");
        runner.Runs[1].Tokens.Should().NotContain("-to");
        runner.Runs.Should().AllSatisfy(r => SplitArgsBuilder.SatisfiesCopyInvariant(r.Tokens).Should().BeTrue());
        result.Warnings.Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I75 - when no aim lands, the part is written from the keyframe's own aim and the result says so.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMp4_WhereNoAimLands_KeepsTheKeyframeAndWarns()
    {
        var probe = new LandingInfoProbe(Info(MovName), Keyframes, aim => S(Math.Floor(aim.TotalSeconds) - 1));

        var (runner, result) = await SubsetAsync(probe, ".mp4", new[] { 2 }, 3, 6);

        ValueAfter(runner.Runs.Single().Tokens, "-ss").Should().Be("3");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("Part 2").And.Contain("3 s");
    }

    // serves-spec: SPEC-001#I75 (boundary) - the part planned from 0 seeks to the file's start and asks nothing.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMp4_TheFirstPart_AsksNothing()
    {
        var probe = new LandingInfoProbe(Info(MovName), Keyframes, _ => null);

        var (runner, _) = await SubsetAsync(probe, ".mp4", new[] { 1 }, 3, 6);

        ValueAfter(runner.Runs.Single().Tokens, "-ss").Should().Be("0");
        probe.Asked.Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I75 - a probe that cannot say leaves the seek at the keyframe; so does a probe that cannot be asked at all.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMp4_WithoutAnAnswer_KeepsTheSeekAtTheKeyframe()
    {
        var silent = new LandingInfoProbe(Info(MovName), Keyframes, _ => null);
        var (runner, result) = await SubsetAsync(silent, ".mp4", new[] { 2 }, 3, 6);
        ValueAfter(runner.Runs.Single().Tokens, "-ss").Should().Be("3");
        result.Warnings.Should().BeEmpty("an answer that cannot be had is not a finding");

        var plain = new FixedInfoProbe(Info(MovName), Keyframes);
        var (runner2, _) = await SubsetAsync(plain, ".mp4", new[] { 2 }, 3, 6);
        ValueAfter(runner2.Runs.Single().Tokens, "-ss").Should().Be("3");
    }

    // serves-spec: SPEC-001#I75 / I74 - Matroska keeps T-234's anchor and is never asked; MPEG-TS and AVI keep the keyframe and are never asked.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("matroska,webm", ".mkv", "3.999")]
    [InlineData("mpegts", ".ts", "3")]
    [InlineData("avi", ".avi", "3")]
    public async Task Subset_OnOtherContainers_IsNeverAskedWhereASeekLands(string container, string extension, string expectedSs)
    {
        var probe = new LandingInfoProbe(Info(container), Keyframes, _ => S(0));

        var (runner, _) = await SubsetAsync(probe, extension, new[] { 2 }, 3, 6);

        ValueAfter(runner.Runs.Single().Tokens, "-ss").Should().Be(expectedSs);
        probe.Asked.Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I75 - the production graph can ask where a seek lands: MediaProbe is an ISeekLandingProbe and both engines are
    // built over it with the seam active; a probe without the interface leaves both without it (silently, which is why this is pinned).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheProductionGraph_HasTheSeekLandingQuestionActive_AndAPlainProbeDoesNot()
    {
        var locator = new FfmpegBinaryLocator(ffmpegOverride: "ffmpeg", ffprobeOverride: "ffprobe");
        var probe = new MediaProbe(new FfprobeRunner(locator));
        var plain = new FixedInfoProbe(Info(MovName), Keyframes);

        typeof(ISeekLandingProbe).IsAssignableFrom(typeof(MediaProbe)).Should().BeTrue();
        new SmartCutEngine(new FfmpegRunner(locator), probe).SeekLandingActive.Should().BeTrue();
        new SplitEngine(new FfmpegRunner(locator), probe).SeekLandingActive.Should().BeTrue();
        typeof(ISeekLandingProbe).IsAssignableFrom(typeof(FixedInfoProbe)).Should().BeFalse("precondition");
        new SmartCutEngine(new RecordingFakeRunner(), plain).SeekLandingActive.Should().BeFalse();
        new SplitEngine(new RecordingFakeRunner(), plain).SeekLandingActive.Should().BeFalse();
    }

    // ---- The end trim (SmartCutEngine) ------------------------------------------------------------------------------------------

    private static readonly IReadOnlyList<TimeSpan> Grid2s = Enumerable.Range(0, 31).Select(i => S(i * 2)).ToList();

    private static MediaInfo LongInfo(string container) =>
        new(
            S(60),
            container,
            new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
            new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") },
            TimeSpan.Zero);

    private async Task<(ListCapturingRunner Runner, SmartCutResult Result)> EndTrimAsync(
        ISeekLandingProbe? landing, string container, string extension, double start, double end)
    {
        var input = Path.Combine(_dir, "clip" + extension);
        File.WriteAllText(input, "placeholder");
        var runner = new ListCapturingRunner();
        IMediaProbe probe = landing is null
            ? new FixedInfoProbe(LongInfo(container), Grid2s)
            : new LandingInfoProbe(LongInfo(container), Grid2s, a => landing.GetSeekLandingAsync("", a).Result);

        var result = await new SmartCutEngine(runner, probe).CutAsync(input, S(start), S(end), Path.Combine(_dir, "cut" + extension));
        return (runner, result);
    }

    // serves-spec: SPEC-001#I75 - an end trim whose seek lands on the keyframe keeps the list it always had: inpoint the keyframe.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task EndTrim_WhereTheKeyframesOwnAimLands_KeepsTheInpointAtTheKeyframe()
    {
        var landing = new FakeLanding(aim => S(Math.Floor(aim.TotalSeconds / 2) * 2));

        var (runner, result) = await EndTrimAsync(landing, MovName, ".mp4", 8, 13);

        result.FellBack.Should().BeFalse(result.FallbackReason);
        result.EffectiveStart.Should().Be(S(8));
        runner.Lists.Should().ContainSingle().Which.Should().EndWith("inpoint 8\noutpoint 13\n");
        landing.Asked.Should().Equal(S(8));
    }

    // serves-spec: SPEC-001#I75 - T-237 repro: where the seek lands early, the list's inpoint is the first later aim that lands, the plan and the
    // reported start stay the keyframe's.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task EndTrim_WhereTheSeekLandsEarly_WritesTheAimThatLandsAsTheInpoint()
    {
        var landing = new FakeLanding(aim =>
        {
            var k = Math.Floor(aim.TotalSeconds / 2) * 2;
            return aim.TotalSeconds - k >= 0.1 ? S(k) : S(k - 2);
        });

        var (runner, result) = await EndTrimAsync(landing, MovName, ".mp4", 8, 13);

        result.FellBack.Should().BeFalse(result.FallbackReason);
        result.EffectiveStart.Should().Be(S(8));
        runner.Lists.Should().ContainSingle().Which.Should().EndWith("inpoint 8.15\noutpoint 13\n");
    }

    // serves-spec: SPEC-001#I75 - T-237 review (blocker on the first version): the inpoint never passes the cut's end, which bounds the aims
    // tried. An end 0.2 s past the keyframe leaves 8.04, 8.08 and 8.15 to try; one 0.06 s past leaves only 8.04.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task EndTrim_TheEndBoundsTheAims_SoTheInpointIsNeverPastTheOutpoint()
    {
        var early = new Func<double, FakeLanding>(from => new FakeLanding(aim =>
            aim.TotalSeconds >= 8 + from ? S(8) : S(6)));

        var (runner, result) = await EndTrimAsync(early(0.1), MovName, ".mp4", 8, 8.2);
        result.FellBack.Should().BeFalse(result.FallbackReason);
        runner.Lists.Should().ContainSingle().Which.Should().EndWith("inpoint 8.15\noutpoint 8.2\n");

        var probe = early(0.1);
        var (runner2, result2) = await EndTrimAsync(probe, MovName, ".mp4", 8, 8.06);
        result2.FellBack.Should().BeTrue("no aim before the end lands on the keyframe");
        result2.FallbackReason.Should().Be(SmartCutEngine.SeekNotExactReason);
        result2.EndSnapped.Should().BeTrue("the lossless cut the caller runs ends at a keyframe, not at the end asked for");
        runner2.Commands.Should().BeEmpty("nothing is written for a cut that would begin early");
        probe.Asked.Should().OnlyContain(a => a < S(8.06));
    }

    // serves-spec: SPEC-001#I75 - a seek that lands on no aim in reach is a fall back before anything is written, with its own reason
    // (no parentheses, no internal terms), and no temp folder is left behind.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task EndTrim_WhenNoAimLands_FallsBackBeforeAnythingIsWritten()
    {
        var landing = new FakeLanding(_ => S(6));

        var (runner, result) = await EndTrimAsync(landing, MovName, ".mp4", 8, 13);

        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be("frame-exact cutting is not supported yet for this video's timestamps");
        result.OutputPath.Should().BeNull();
        runner.Commands.Should().BeEmpty();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I75 (boundary) - an end trim from the first keyframe asks nothing (the seek clamps to the file's start), and a
    // container that is not MP4 or QuickTime is never asked either.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task EndTrim_FromTheFirstKeyframe_AsksNothing()
    {
        var landing = new FakeLanding(_ => S(99));

        var (runner, result) = await EndTrimAsync(landing, MovName, ".mp4", 0, 1);

        result.FellBack.Should().BeFalse(result.FallbackReason);
        runner.Lists.Should().ContainSingle().Which.Should().EndWith("inpoint 0\noutpoint 1\n");
        landing.Asked.Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I75 - a probe that cannot say leaves the list as it was.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task EndTrim_WhenTheProbeCannotSay_KeepsTheInpointAtTheKeyframe()
    {
        var (runner, result) = await EndTrimAsync(new FakeLanding(_ => null), MovName, ".mp4", 8, 13);

        result.FellBack.Should().BeFalse(result.FallbackReason);
        runner.Lists.Should().ContainSingle().Which.Should().EndWith("inpoint 8\noutpoint 13\n");
    }
}
