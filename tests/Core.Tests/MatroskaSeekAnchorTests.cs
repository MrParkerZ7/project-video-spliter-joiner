using FluentAssertions;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-234, binary-free: a per-segment (selected parts) copy on a Matroska or WebM source aims its input <c>-ss</c> at a
/// point just before the next keyframe instead of at the part's keyframe (SPEC-001 I74), and every other container keeps
/// seeking to the keyframe. The real-ffmpeg proof is <see cref="LosslessBoundaryIntegrationTests"/> (the <c>mkv</c> shape).
/// </summary>
public sealed class MatroskaSeekAnchorTests
{
    private const string Mkv = @"C:\in.mkv";
    private const string Out = @"C:\out\seg.mkv";

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static string ValueAfter(IReadOnlyList<string> tokens, string flag)
    {
        var i = tokens.ToList().IndexOf(flag);
        i.Should().BeGreaterThanOrEqualTo(0, $"the command must carry {flag}");
        return tokens[i + 1];
    }

    // The bench's mkv shape: video keyframes every second from 0.023 s (the video starts after the audio).
    private static readonly IReadOnlyList<TimeSpan> Keyframes = Enumerable.Range(0, 10).Select(i => S(0.023 + i)).ToList();

    // ---- IsMatroskaContainer ----------------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I74 - ffprobe names both .mkv and .webm "matroska,webm"; nothing else is Matroska.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("matroska,webm", true)]
    [InlineData("Matroska,WebM", true)]
    [InlineData("matroska", true)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", false)]
    [InlineData("mpegts", false)]
    [InlineData("avi", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void IsMatroskaContainer_RecognisesFfprobesMatroskaFormatName(string? container, bool expected) =>
        SplitArgsBuilder.IsMatroskaContainer(container).Should().Be(expected);

    // ---- MatroskaSeekAnchor -----------------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I74 - an interior keyframe: 1 ms before the next one.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_InteriorKeyframe_IsOneMillisecondBeforeTheNextKeyframe()
    {
        var anchor = SplitArgsBuilder.MatroskaSeekAnchor(S(3.023), Keyframes, S(10.023));

        anchor!.Value.TotalSeconds.Should().BeApproximately(4.022, 1e-6);
    }

    // serves-spec: SPEC-001#I74 - the last keyframe has no next one: the anchor is 1 ms before the file's end.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_LastKeyframe_IsOneMillisecondBeforeTheFilesEnd()
    {
        var anchor = SplitArgsBuilder.MatroskaSeekAnchor(S(9.023), Keyframes, S(10.023));

        anchor!.Value.TotalSeconds.Should().BeApproximately(10.022, 1e-6);
    }

    // serves-spec: SPEC-001#I74 (boundary) - a part that starts at or before the first keyframe needs none: the seek
    // clamps to the first cue, which is the part's start.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.023)]
    [InlineData(0.01)]
    public void Anchor_AtOrBeforeTheFirstKeyframe_IsNull(double start) =>
        SplitArgsBuilder.MatroskaSeekAnchor(S(start), Keyframes, S(10.023)).Should().BeNull();

    // serves-spec: SPEC-001#I74 (boundary) - nothing to anchor on, or a next keyframe inside the margin: null (today's seek).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_NoKeyframes_OrANextKeyframeInsideTheMargin_OrAnEndAtTheStart_IsNull()
    {
        SplitArgsBuilder.MatroskaSeekAnchor(S(3), Array.Empty<TimeSpan>(), S(10)).Should().BeNull();
        SplitArgsBuilder.MatroskaSeekAnchor(S(3), new[] { S(1), S(3), S(3.001) }, S(10)).Should().BeNull("3.001 - 1 ms is not after the start");
        SplitArgsBuilder.MatroskaSeekAnchor(S(9), new[] { S(1), S(9) }, S(9.001)).Should().BeNull("the end is within the margin of the last keyframe");
        SplitArgsBuilder.MatroskaSeekAnchor(S(9), new[] { S(1), S(9) }, S(9)).Should().BeNull("an end at the start leaves no GOP to anchor in");
    }

    // serves-spec: SPEC-001#I74 - a start between two keyframes (not itself one) uses the first keyframe after it.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_StartBetweenKeyframes_UsesTheFirstKeyframeAfterIt()
    {
        SplitArgsBuilder.MatroskaSeekAnchor(S(3.5), Keyframes, S(10.023))!.Value.TotalSeconds.Should().BeApproximately(4.022, 1e-6);
    }

    // serves-spec: SPEC-001#I74 (boundary) - just past the margin: 2 ms between keyframes leaves a 1 ms GOP to anchor in.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_NextKeyframeTwoMillisecondsAway_IsOneMillisecondAfterTheStart()
    {
        SplitArgsBuilder.MatroskaSeekAnchor(S(3), new[] { S(1), S(3), S(3.002) }, S(10))!.Value.TotalSeconds.Should().BeApproximately(3.001, 1e-6);
    }

    // serves-spec: SPEC-001#I74 - a null list is a programming error.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void Anchor_NullKeyframes_Throws()
    {
        var act = () => SplitArgsBuilder.MatroskaSeekAnchor(S(3), null!, S(10));
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- PerSegment with an anchor -------------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I74 - the seek is the anchor and -to is measured from it, so the part still ends at its end.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_WithAnAnchor_SeeksToTheAnchor_AndMeasuresToFromIt()
    {
        var tokens = SplitArgsBuilder.PerSegment(Mkv, S(3.023), S(6.023), TimeSpan.Zero, Out, S(4.022)).ToList();

        ValueAfter(tokens, "-ss").Should().Be("4.022");
        ValueAfter(tokens, "-to").Should().Be("2.001", "6.023 - 4.022: ffmpeg counts -to from the seek target");
        SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
    }

    // serves-spec: SPEC-001#I74 - the final part keeps omitting -to.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_FinalPartWithAnAnchor_OmitsTo()
    {
        var tokens = SplitArgsBuilder.PerSegment(Mkv, S(6.023), null, TimeSpan.Zero, Out, S(7.022)).ToList();

        ValueAfter(tokens, "-ss").Should().Be("7.022");
        tokens.Should().NotContain("-to");
    }

    // serves-spec: SPEC-001#I74 - the anchor is converted by the format start like any seek: with an audio that starts at
    // -0.023 s (the common ffmpeg-made Matroska), ffmpeg adds that back and aims at the anchor.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void PerSegment_WithAnAnchor_ConvertsItByTheFormatStart()
    {
        var tokens = SplitArgsBuilder.PerSegment(Mkv, S(3), S(6), S(-0.023), Out, S(3.999)).ToList();

        ValueAfter(tokens, "-ss").Should().Be("4.022");
        ValueAfter(tokens, "-to").Should().Be("2.001", "6 - (-0.023 + 4.022)");
    }

    // serves-spec: SPEC-001#I74 (boundary) - an anchor that is not after the start changes nothing: today's command.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(null)]
    [InlineData(3.0)]
    [InlineData(2.5)]
    public void PerSegment_NoAnchor_OrOneNotAfterTheStart_EmitsTodaysTokens(double? anchor)
    {
        var tokens = SplitArgsBuilder.PerSegment(
            @"C:\in.mp4", S(3), S(6), TimeSpan.Zero, @"C:\out\seg.mp4", anchor is { } a ? S(a) : null).ToList();

        tokens.Should().Equal(
            "-hide_banner", "-nostdin", "-y", "-ss", "3", "-i", @"C:\in.mp4", "-to", "3",
            "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", @"C:\out\seg.mp4");
    }

    // ---- The engine: which containers get the anchor (fake runner and probe) --------------------------------------------

    private sealed class Workspace : IDisposable
    {
        public Workspace(string extension)
        {
            Dir = Path.Combine(Path.GetTempPath(), "vsj-t234-" + Guid.NewGuid().ToString("N"));
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

    private static MediaInfo Info(string container, TimeSpan startTime, TimeSpan duration) =>
        new(
            duration,
            container,
            new[] { new StreamInfo(0, "h264", "video", 320, 240, "yuv420p", null, null, "1/1000", 30, null, S(0.023)) },
            new[] { new StreamInfo(1, "aac", "audio", null, null, null, 44100, 2, "1/1000", null, null, startTime) },
            startTime);

    private static async Task<RecordingTimingRunner> SubsetAsync(string container, string extension, TimeSpan startTime, int[] parts, params double[] cuts)
    {
        using var ws = new Workspace(extension);
        var runner = new RecordingTimingRunner();
        var info = Info(container, startTime, S(10.023));

        await new SplitEngine(runner, new FixedInfoProbe(info, Keyframes)).SplitAsync(
            new SplitRequest(ws.Input, cuts.Select(S).ToList(), ws.OutDir, SelectedSegmentIndices: parts));
        return runner;
    }

    // serves-spec: SPEC-001#I74 - on a Matroska source an interior part and the final part aim at 1 ms before the next
    // keyframe; the progress length stays the part's own (end - keyframe, not end - anchor).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMatroska_AimsEachPartInsideItsFirstGop_AndReportsTheRunLengthFromTheKeyframe()
    {
        var runner = await SubsetAsync("matroska,webm", ".mkv", TimeSpan.Zero, new[] { 2, 3 }, 3, 6);

        runner.Runs.Should().HaveCount(2);
        ValueAfter(runner.Runs[0].Tokens, "-ss").Should().Be("4.022", "part 2 starts at the keyframe 3.023; the next is 4.023");
        ValueAfter(runner.Runs[0].Tokens, "-to").Should().Be("2.001", "the part ends at the keyframe 6.023");
        runner.Runs[0].TotalDuration!.Value.TotalSeconds.Should().BeApproximately(3, 1e-6, "the run's progress length is the part's: 6.023 - 3.023");
        ValueAfter(runner.Runs[1].Tokens, "-ss").Should().Be("7.022", "part 3 starts at the keyframe 6.023; the next is 7.023");
        runner.Runs[1].Tokens.Should().NotContain("-to");
        runner.Runs[1].TotalDuration!.Value.TotalSeconds.Should().BeApproximately(4.0, 1e-6, "the final part runs from the keyframe 6.023 to the file's end 10.023");
        runner.Runs.Should().AllSatisfy(r => SplitArgsBuilder.SatisfiesCopyInvariant(r.Tokens).Should().BeTrue());
    }

    // serves-spec: SPEC-001#I74 (boundary) - the part planned from 0 seeks to the file's start, as before.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMatroska_TheFirstPart_KeepsItsSeekToTheFilesStart()
    {
        var runner = await SubsetAsync("matroska,webm", ".mkv", TimeSpan.Zero, new[] { 1 }, 3, 6);

        ValueAfter(runner.Runs.Single().Tokens, "-ss").Should().Be("0");
    }

    // serves-spec: SPEC-001#I74 - the last keyframe: the final part aims 1 ms before the end of the file.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMatroska_AFinalPartThatStartsOnTheLastKeyframe_AimsJustBeforeTheEnd()
    {
        var runner = await SubsetAsync("matroska,webm", ".mkv", TimeSpan.Zero, new[] { 2 }, 8.8);

        var tokens = runner.Runs.Single().Tokens;
        ValueAfter(tokens, "-ss").Should().Be("10.022", "the keyframe 9.023 is the last; the file ends at 10.023");
        tokens.Should().NotContain("-to");
    }

    // serves-spec: SPEC-001#I74 - the format start (an audio at -0.023 s) is subtracted from the anchor like any seek.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMatroska_WithANegativeFormatStart_ConvertsTheAnchorByIt()
    {
        var runner = await SubsetAsync("matroska,webm", ".mkv", S(-0.023), new[] { 3 }, 3, 6);

        // end of file = -0.023 + 10.023 = 10; the anchor for the keyframe 6.023 is 7.022; -ss = 7.022 - (-0.023)
        ValueAfter(runner.Runs.Single().Tokens, "-ss").Should().Be("7.045");
    }

    // serves-spec: SPEC-001#I74 (boundary) - the last keyframe on a file whose format starts at -0.023 s: the file's last
    // timestamp is start_time + duration = 10.0, so the anchor is 9.999 and -ss, counted from the format start, 10.022.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task Subset_OnMatroska_ALastKeyframePart_WithANegativeFormatStart_AimsBeforeTheLastTimestamp()
    {
        var runner = await SubsetAsync("matroska,webm", ".mkv", S(-0.023), new[] { 2 }, 8.8);

        ValueAfter(runner.Runs.Single().Tokens, "-ss").Should().Be("10.022");
    }

    // serves-spec: SPEC-001#I74 - the controls: MP4/MOV, MPEG-TS and an unnamed container keep seeking to the keyframe.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", ".mp4")]
    [InlineData("mpegts", ".ts")]
    [InlineData("avi", ".avi")]
    [InlineData("", ".bin")]
    public async Task Subset_OnOtherContainers_SeeksToTheKeyframe(string container, string extension)
    {
        var runner = await SubsetAsync(container, extension, TimeSpan.Zero, new[] { 2, 3 }, 3, 6);

        ValueAfter(runner.Runs[0].Tokens, "-ss").Should().Be("3.023");
        ValueAfter(runner.Runs[0].Tokens, "-to").Should().Be("3");
        ValueAfter(runner.Runs[1].Tokens, "-ss").Should().Be("6.023");
    }
}
