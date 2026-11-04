using FluentAssertions;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-237, binary-free: a per-segment (selected parts) copy on an MP4 or QuickTime source aims its input <c>-ss</c> at the middle of
/// the part's first GOP instead of at the keyframe (SPEC-001 I75), the way a Matroska source does just before the next keyframe
/// (T-234, <see cref="MatroskaSeekAnchorTests"/>). The real-ffmpeg proof, on variable-frame-rate files, is
/// <see cref="LosslessBoundaryIntegrationTests"/> (the <c>vfr*</c> shapes).
/// </summary>
public sealed class MovSeekAnchorTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static string ValueAfter(IReadOnlyList<string> tokens, string flag)
    {
        var i = tokens.ToList().IndexOf(flag);
        i.Should().BeGreaterThanOrEqualTo(0, $"the command must carry {flag}");
        return tokens[i + 1];
    }

    // The B-frame MP4 recipe: video keyframes every second from 0 (the file's ten seconds).
    private static readonly IReadOnlyList<TimeSpan> Keyframes = Enumerable.Range(0, 10).Select(i => S(i)).ToList();

    // ---- IsMovContainer -------------------------------------------------------------------------------------------------

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

    // ---- MovSeekAnchor --------------------------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I75 - an interior keyframe: the middle of its GOP.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_InteriorKeyframe_IsTheMiddleOfItsGop()
    {
        SplitArgsBuilder.MovSeekAnchor(S(3), Keyframes, S(10))!.Value.TotalSeconds.Should().BeApproximately(3.5, 1e-9);
        SplitArgsBuilder.MovSeekAnchor(S(3), new[] { S(0), S(3), S(8) }, S(10))!.Value.TotalSeconds
            .Should().BeApproximately(5.5, 1e-9, "a 5 s GOP: its middle");
    }

    // serves-spec: SPEC-001#I75 - the last keyframe has no next one: the middle of what is left of the file.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_LastKeyframe_IsTheMiddleOfTheRestOfTheFile()
    {
        SplitArgsBuilder.MovSeekAnchor(S(9), Keyframes, S(10))!.Value.TotalSeconds.Should().BeApproximately(9.5, 1e-9);
        SplitArgsBuilder.MovSeekAnchor(S(9), Keyframes, S(12))!.Value.TotalSeconds.Should().BeApproximately(10.5, 1e-9);
    }

    // serves-spec: SPEC-001#I75 (boundary) - a part that starts at or before the first keyframe needs none: the seek clamps to the
    // file's start.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Anchor_AtOrBeforeTheFirstKeyframe_IsNull(double start) =>
        SplitArgsBuilder.MovSeekAnchor(S(start), Keyframes, S(10)).Should().BeNull();

    // serves-spec: SPEC-001#I75 (boundary) - a GOP with a later keyframe after it that is under half a second, and a list with no
    // keyframes, get none: the aim stays at the keyframe, as before. Exactly half a second is anchored.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_AGopUnderHalfASecond_OrNoKeyframes_IsNull_AndExactlyHalfASecondIsAnchored()
    {
        SplitArgsBuilder.MovSeekAnchor(S(3), Array.Empty<TimeSpan>(), S(10)).Should().BeNull();
        SplitArgsBuilder.MovSeekAnchor(S(3), new[] { S(1), S(3), S(3.4) }, S(10)).Should().BeNull("a 0.4 s GOP");
        SplitArgsBuilder.MovSeekAnchor(S(3), new[] { S(1), S(3), S(3.5) }, S(10))!.Value.TotalSeconds.Should().BeApproximately(3.25, 1e-9);
    }

    // serves-spec: SPEC-001#I75 - T-237 review: the last GOP has no later keyframe to overshoot onto, so a quarter of a second is
    // enough (it is the stretch that is past the index gap that matters); shorter keeps the aim at the keyframe. Shipped first
    // version wrote nothing for a tail under half a second and the last part of a file with a 0.17-0.37 s tail began one GOP early.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_TheLastGop_NeedsAQuarterOfASecond()
    {
        SplitArgsBuilder.MovSeekAnchor(S(9.7), new[] { S(1), S(9.7) }, S(10))!.Value.TotalSeconds.Should().BeApproximately(9.85, 1e-9, "a 0.3 s tail");
        SplitArgsBuilder.MovSeekAnchor(S(9.75), new[] { S(1), S(9.75) }, S(10))!.Value.TotalSeconds.Should().BeApproximately(9.875, 1e-9, "exactly 0.25 s");
        SplitArgsBuilder.MovSeekAnchor(S(9.8), new[] { S(1), S(9.8) }, S(10)).Should().BeNull("a 0.2 s tail");
    }

    // serves-spec: SPEC-001#I75 - T-237 review: a GOP with a later keyframe after it must also be 16 frame periods long. At 10 fps
    // with three B-frames the next keyframe's index time can be 0.8 s before its presentation time, and the middle of a 1.2 s GOP
    // then lands one GOP LATE (measured on a 10 fps file, 30 % of its frames dropped). At 30 fps 16 frames are 0.53 s.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_AGopWithALaterKeyframe_MustBeSixteenFramePeriodsLong()
    {
        var keyframes = new[] { S(1), S(3), S(4.2) };

        SplitArgsBuilder.MovSeekAnchor(S(3), keyframes, S(10), frameRate: 10).Should().BeNull("1.2 s is 12 frames at 10 fps");
        SplitArgsBuilder.MovSeekAnchor(S(3), keyframes, S(10), frameRate: 7).Should().BeNull("1.2 s is 8 frames at 7 fps");
        SplitArgsBuilder.MovSeekAnchor(S(3), keyframes, S(10), frameRate: 30)!.Value.TotalSeconds.Should().BeApproximately(3.6, 1e-9);
        SplitArgsBuilder.MovSeekAnchor(S(3), keyframes, S(10), frameRate: 0)!.Value.TotalSeconds.Should().BeApproximately(3.6, 1e-9, "an unknown rate counts times only");
        SplitArgsBuilder.MovSeekAnchor(S(3), keyframes, S(10), frameRate: null)!.Value.TotalSeconds.Should().BeApproximately(3.6, 1e-9);
        SplitArgsBuilder.MovSeekAnchor(S(3), new[] { S(1), S(3), S(5) }, S(10), frameRate: 10)!.Value.TotalSeconds
            .Should().BeApproximately(4, 1e-9, "a 2 s GOP is 20 frames at 10 fps");
    }

    // serves-spec: SPEC-001#I75 - the last GOP and a span the cut's end stops need only 6 frame periods: 0.6 s at 10 fps.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_AStretchWithNoLaterKeyframe_MustBeSixFramePeriodsLong()
    {
        SplitArgsBuilder.MovSeekAnchor(S(9.5), new[] { S(1), S(9.5) }, S(10), frameRate: 10).Should().BeNull("0.5 s is 5 frames at 10 fps");
        SplitArgsBuilder.MovSeekAnchor(S(9.4), new[] { S(1), S(9.4) }, S(10), frameRate: 10)!.Value.TotalSeconds.Should().BeApproximately(9.7, 1e-9);
    }

    // serves-spec: SPEC-001#I75 - T-237 review (BLOCKER on the first version): the end trim's anchor never passes the cut's end. A
    // concat list whose inpoint is after its outpoint is an error, and the first version wrote inpoint 6.25 for an end at 5.3 on
    // a plain constant-frame-rate MP4 and failed the row. The span the end stops is the stretch measured, with no keyframe to
    // overshoot onto, and a span under a quarter of a second keeps the keyframe.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_WithACutEnd_NeverPassesIt()
    {
        var keyframes = new[] { S(0), S(2.5), S(5), S(7.5) };

        SplitArgsBuilder.MovSeekAnchor(S(5), keyframes, S(10), upTo: S(5.3))!.Value.TotalSeconds.Should().BeApproximately(5.15, 1e-9);
        SplitArgsBuilder.MovSeekAnchor(S(5), keyframes, S(10), upTo: S(6.2))!.Value.TotalSeconds.Should().BeApproximately(5.6, 1e-9);
        SplitArgsBuilder.MovSeekAnchor(S(5), keyframes, S(10), upTo: S(6.3))!.Value.TotalSeconds.Should().BeApproximately(5.65, 1e-9);
        SplitArgsBuilder.MovSeekAnchor(S(5), keyframes, S(10), upTo: S(5.2)).Should().BeNull("a span under a quarter of a second");
        SplitArgsBuilder.MovSeekAnchor(S(5), keyframes, S(10), upTo: S(9))!.Value.TotalSeconds.Should().BeApproximately(6.25, 1e-9, "an end past the next keyframe changes nothing");
        foreach (var cut in new[] { 5.26, 5.3, 5.6, 6.1, 6.2, 6.3, 7.4 })
        {
            var anchor = SplitArgsBuilder.MovSeekAnchor(S(5), keyframes, S(10), upTo: S(cut));
            (anchor is null || anchor.Value < S(cut)).Should().BeTrue($"the anchor for an end at {cut} s is {anchor}");
        }
    }

    // serves-spec: SPEC-001#I75 - a start between two keyframes (not itself one) uses the first keyframe after it, and a start that
    // is a keyframe of the list a millisecond off its own time still finds the next one.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_StartBetweenKeyframes_UsesTheFirstKeyframeAfterIt()
    {
        SplitArgsBuilder.MovSeekAnchor(S(3.2), Keyframes, S(10))!.Value.TotalSeconds.Should().BeApproximately(3.2 + (0.8 / 2), 1e-9);
        SplitArgsBuilder.MovSeekAnchor(S(3.0005), Keyframes, S(10))!.Value.TotalSeconds.Should().BeApproximately(3.0005 + (0.9995 / 2), 1e-6,
            "half a millisecond past the keyframe 3 is 3, not the keyframe after it");
    }

    // serves-spec: SPEC-001#I75 - a null list is a programming error.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_NullKeyframes_Throws()
    {
        var act = () => SplitArgsBuilder.MovSeekAnchor(S(3), null!, S(10));
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- SeekAnchor: which container gets which anchor ---------------------------------------------------------------------

    // serves-spec: SPEC-001#I75 / I74 - MP4 and QuickTime get the middle of the GOP, Matroska its anchor before the next keyframe,
    // every other container (and an unnamed one) none.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void SeekAnchor_RoutesByContainer()
    {
        SplitArgsBuilder.SeekAnchor("mov,mp4,m4a,3gp,3g2,mj2", S(3), Keyframes, S(10))!.Value.TotalSeconds.Should().BeApproximately(3.5, 1e-9);
        SplitArgsBuilder.SeekAnchor("matroska,webm", S(3), Keyframes, S(10))!.Value.TotalSeconds.Should().BeApproximately(3.999, 1e-9);
        SplitArgsBuilder.SeekAnchor("mpegts", S(3), Keyframes, S(10)).Should().BeNull();
        SplitArgsBuilder.SeekAnchor("avi", S(3), Keyframes, S(10)).Should().BeNull();
        SplitArgsBuilder.SeekAnchor(null, S(3), Keyframes, S(10)).Should().BeNull();
        SplitArgsBuilder.SeekAnchor("", S(3), Keyframes, S(10)).Should().BeNull();
    }

    // ---- The engine: which parts of an MP4 get the anchor (fake runner and probe) ---------------------------------------------

    private sealed class Workspace : IDisposable
    {
        public Workspace(string extension)
        {
            Dir = Path.Combine(Path.GetTempPath(), "vsj-t237-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Input = Path.Combine(Dir, "src" + extension);
            File.WriteAllText(Input, "placeholder");
            OutDir = Path.Combine(Dir, "out");
            Directory.CreateDirectory(OutDir);
        }

        public string Input { get; }

        public string OutDir { get; }

        private string Dir { get; }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static MediaInfo Info(string container, TimeSpan duration) =>
        new(
            duration,
            container,
            new[] { new StreamInfo(0, "h264", "video", 320, 240, "yuv420p", null, null, "1/1000", 30, null, TimeSpan.Zero) },
            new[] { new StreamInfo(1, "aac", "audio", null, null, null, 44100, 2, "1/1000", null, null, TimeSpan.Zero) },
            TimeSpan.Zero);

    private static async Task<RecordingTimingRunner> SubsetAsync(
        string container, string extension, IReadOnlyList<TimeSpan> keyframes, int[] parts, params double[] cuts)
    {
        using var ws = new Workspace(extension);
        var runner = new RecordingTimingRunner();

        await new SplitEngine(runner, new FixedInfoProbe(Info(container, S(10)), keyframes)).SplitAsync(
            new SplitRequest(ws.Input, cuts.Select(S).ToList(), ws.OutDir, SelectedSegmentIndices: parts));
        return runner;
    }

    // serves-spec: SPEC-001#I75 - on an MP4 source an interior part and the final part aim at the middle of their first GOP; -to is
    // measured from the aim, so the part still ends at its end; the progress length stays the part's own (end - keyframe).
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", ".mp4")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", ".mov")]
    public async Task Subset_OnMp4_AimsEachPartInTheMiddleOfItsFirstGop_AndReportsTheRunLengthFromTheKeyframe(string container, string extension)
    {
        var runner = await SubsetAsync(container, extension, Keyframes, new[] { 2, 3 }, 3, 6);

        runner.Runs.Should().HaveCount(2);
        ValueAfter(runner.Runs[0].Tokens, "-ss").Should().Be("3.5", "part 2 starts at the keyframe 3; its GOP is 3-4 s");
        ValueAfter(runner.Runs[0].Tokens, "-to").Should().Be("2.5", "the part ends at the keyframe 6: 2.5 s from the aim");
        runner.Runs[0].TotalDuration!.Value.TotalSeconds.Should().BeApproximately(3, 1e-6, "the run's progress length is the part's: 6 - 3");
        ValueAfter(runner.Runs[1].Tokens, "-ss").Should().Be("6.5", "part 3 starts at the keyframe 6; its GOP is 6-7 s");
        runner.Runs[1].Tokens.Should().NotContain("-to");
        runner.Runs[1].TotalDuration!.Value.TotalSeconds.Should().BeApproximately(4.0, 1e-6, "the final part runs from the keyframe 6 to the file's end");
        runner.Runs.Should().AllSatisfy(r => SplitArgsBuilder.SatisfiesCopyInvariant(r.Tokens).Should().BeTrue());
    }

    // serves-spec: SPEC-001#I75 (boundary) - the part planned from 0 seeks to the file's start, as before.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMp4_TheFirstPart_KeepsItsSeekToTheFilesStart()
    {
        var runner = await SubsetAsync("mov,mp4,m4a,3gp,3g2,mj2", ".mp4", Keyframes, new[] { 1 }, 3, 6);

        ValueAfter(runner.Runs.Single().Tokens, "-ss").Should().Be("0");
    }

    // serves-spec: SPEC-001#I75 - the last keyframe: the final part aims in the middle of what is left of the file.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMp4_AFinalPartThatStartsOnTheLastKeyframe_AimsInTheMiddleOfTheRest()
    {
        var runner = await SubsetAsync("mov,mp4,m4a,3gp,3g2,mj2", ".mp4", Keyframes, new[] { 2 }, 8.8);

        var tokens = runner.Runs.Single().Tokens;
        ValueAfter(tokens, "-ss").Should().Be("9.5", "the keyframe 9 is the last; the file ends at 10");
        tokens.Should().NotContain("-to");
    }

    // serves-spec: SPEC-001#I75 (boundary) - a GOP under half a second keeps today's aim, at the keyframe.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMp4_WithShortGops_KeepsTheSeekAtTheKeyframe()
    {
        var shortGops = Enumerable.Range(0, 26).Select(i => S(i * 0.4)).ToList();

        var runner = await SubsetAsync("mov,mp4,m4a,3gp,3g2,mj2", ".mp4", shortGops, new[] { 2, 3 }, 3.2, 6.4);

        ValueAfter(runner.Runs[0].Tokens, "-ss").Should().Be("3.2");
        ValueAfter(runner.Runs[0].Tokens, "-to").Should().Be("3.2");
        ValueAfter(runner.Runs[1].Tokens, "-ss").Should().Be("6.4");
    }
}
