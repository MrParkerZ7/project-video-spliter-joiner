using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;
using static VideoSplitJoiner.Core.Tests.SmartCutJointTiming;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-191, review fix (G-059, SPEC-001 I44 / I46) - a Start one or two frames before a key frame, on a source whose key frame has
/// LEADING PICTURES (B-frames displayed before it that need the GOP before it): an open-GOP H.264 file and an MPEG-4 part 2 file
/// with B-frames.
///
/// <para><b>The defect.</b> The input seek lands on the key frame K by its decoding time, and the leading B-frames that are displayed
/// before K cannot be decoded without the GOP before it. The output seek decoded from the start of the file and had them. So for a
/// Start at K-2 frames the head should hold the source's 2 frames of <c>[Start, K)</c> and held 1, starting a frame late (video
/// start 0.066 s), and at K-1 it held none, or no video stream at all - with ffmpeg's exit code 0. The first version of this
/// ticket checked only that the head was not empty, so a head with one frame instead of two was reported Done and the final's
/// video started late against its audio. Measured on the bundled ffmpeg n7.1.5 (30 fps, B-frames 2): MPEG-4 in MP4 with keyframes
/// every 2 s, Start 5.9333 s (K-2) and 5.9667 s (K-1); x264 <c>open-gop=1:keyint=50</c>, Starts 1.6, 1.6333, 3.2667, 3.3, 6.6 and
/// 6.6333 s (not every key frame of that file has leading pictures).</para>
///
/// <para><b>The fix under test.</b> The engine compares the head with the source: as many video frames as the source has in
/// <c>[Start, HeadEnd)</c>, the first at the head's time 0; a head that is not that is a fall back to the lossless cut with a stated
/// reason, nothing written. The tests below use only what the shipped code already has (the engine, the real probe, the builder), so
/// this very file runs against the code before the fix, where it fails.</para>
/// </summary>
public sealed class SmartCutLeadingPictureIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SmartCutLeadingPictureIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t191l-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private void RequireBinaries()
    {
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg");
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
    }

    private static FfmpegBinaryLocator Locator() => new(
        ffmpegOverride: FfmpegTestBinaries.Ffmpeg, ffprobeOverride: FfmpegTestBinaries.Ffprobe);

    private static FfmpegRunner Runner() => new(Locator());

    private static MediaProbe Probe() => new(new FfprobeRunner(Locator()));

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    public enum Shape
    {
        /// <summary>libx264, closed GOP, 2 s keyframe interval (the control).</summary>
        ClosedGop,

        /// <summary>libx264 <c>open-gop=1</c>, a key frame every 50 frames (1.6667 s).</summary>
        OpenGop,

        /// <summary>MPEG-4 part 2 in MP4 with B-frames, a key frame every 2 s.</summary>
        Mpeg4,
    }

    private async Task<string> MakeAsync(Shape shape)
    {
        var path = Path.Combine(_dir, shape + ".mp4");
        var video = shape switch
        {
            Shape.ClosedGop => new[] { "-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0" },
            Shape.OpenGop => new[] { "-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-x264-params", "open-gop=1:keyint=50:min-keyint=50:scenecut=0" },
            _ => new[] { "-c:v", "mpeg4", "-bf", "2", "-g", "60", "-qscale:v", "3" },
        };
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg()
            .Raw("-y", "-loglevel", "error")
            .Raw("-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=14")
            .Raw("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=14")
            .Raw("-map", "0:v:0", "-map", "1:a:0")
            .Raw(video)
            .Raw("-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128000", "-ar", "48000", "-ac", "2", "-t", "14")
            .Output(path));
        run.ExitCode.Should().Be(0, "the source must be produced first: " + run.StdErrText);
        return path;
    }

    /// <summary>The first video frame of the file written by an engine run or a reference head: md5 per frame, in output order.</summary>
    private async Task<IReadOnlyList<string>> FrameHashesAsync(string path)
    {
        var listing = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".framemd5");
        var run = await Runner().RunAsync(FfmpegArgs.ForFfmpeg().Raw("-v", "error").Input(path)
            .Raw("-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-y").Output(listing));
        if (run.ExitCode != 0 || !File.Exists(listing))
        {
            return Array.Empty<string>(); // a file with no video stream
        }

        return (await File.ReadAllLinesAsync(listing))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(',')[5].Trim())
            .ToList();
    }

    /// <summary>The head the shipped engine built: <c>-ss</c> AFTER <c>-i</c>, decoding from the start of the file.</summary>
    private async Task<IReadOnlyList<string>> ShippedHeadHashesAsync(string source, TimeSpan start, TimeSpan headEnd)
    {
        var info = ((ProbeResult.ProbeSucceeded)await Probe().ProbeAsync(source)).Info;
        SmartCutArgsBuilder.TryResolveEncoders(info, out var venc, out var aenc, out var why).Should().BeTrue(why);
        var head = Path.Combine(_dir, "reference-head.mp4");
        var tokens = SmartCutArgsBuilder.HeadReencode(source, start, headEnd, info, venc!, aenc, head).ToList().Skip(2).SkipLast(1).ToList();

        // Move -ss <start> from before -i <input> to after it: the order the head had before T-191 (and the order that is not a seek).
        var ss = tokens.IndexOf("-ss");
        if (ss < tokens.IndexOf("-i"))
        {
            var seconds = tokens[ss + 1];
            tokens.RemoveRange(ss, 2);
            var input = tokens.IndexOf("-i");
            tokens.Insert(input + 2, "-ss");
            tokens.Insert(input + 3, seconds);
        }

        var args = FfmpegArgs.ForFfmpeg();
        foreach (var token in tokens)
        {
            args.Raw(token);
        }

        var run = await Runner().RunAsync(args.Output(head));
        run.ExitCode.Should().Be(0, run.StdErrText);
        return await FrameHashesAsync(head);
    }

    /// <summary>
    /// One Exact cut through the real engine at <paramref name="start"/>. Whatever it does, it must never report Done with a head
    /// that is not the frames the shipped head had: either it falls back (nothing written, no temp dir, a stated reason), or the
    /// head file the engine wrote (the recording runner copies it out before the sweep) IS the shipped head, md5 for md5. (The
    /// FINAL is not compared: the joint is SmartCutOpenGopIntegrationTests' and SmartCutJointIntegrityTests' to check, and an
    /// open-GOP source no longer reaches this head check at all - T-223 makes it fall back first.) Returns whether it fell back.
    /// </summary>
    private async Task<bool> CutAndCheckAsync(string source, double start, string label)
    {
        var effective = await FirstFrameAtOrAfterAsync(source, start - 0.0005);
        var keyframes = await Probe().GetKeyframesAsync(source);
        var headEnd = SmartCutPlanner.NextKeyframeAfter(S(effective), keyframes)
            ?? throw new InvalidOperationException("the source has no keyframe after the start");
        var shipped = await ShippedHeadHashesAsync(source, S(effective), headEnd);

        var dest = Path.Combine(_dir, "out", "exact.mp4");
        var runner = new RecordingRealRunner(Runner(), Path.Combine(_dir, "kept-" + Guid.NewGuid().ToString("N")));
        var result = await new SmartCutEngine(runner, Probe()).CutAsync(source, S(start), null, dest);

        IReadOnlyList<string> headHashes = Array.Empty<string>();
        if (!result.FellBack)
        {
            runner.HeadCopy.Should().NotBeNull("the engine wrote a head and the recording runner copied it out");
            headHashes = await FrameHashesAsync(runner.HeadCopy!);
        }

        _output.WriteLine($"{label}: Start {start} (frame {effective:0.######}) HeadEnd {headEnd.TotalSeconds:0.######}: the shipped head holds {shipped.Count} frame(s); "
            + (result.FellBack
                ? $"the engine FELL BACK: {result.FallbackReason}"
                : $"the engine reported Done; its head holds {headHashes.Count} frame(s) and {(headHashes.SequenceEqual(shipped) ? "IS" : "is NOT")} the shipped head"));

        _output.WriteLine("  shipped head: " + string.Join(' ', shipped.Select(h => h[..6])));
        _output.WriteLine("  engine head:  " + string.Join(' ', headHashes.Select(h => h[..6])));
        using var scope = new AssertionScope(label);
        if (result.FellBack)
        {
            result.FallbackReason.Should().NotBeNullOrWhiteSpace("a fall back says why");
            result.OutputPath.Should().BeNull();
            File.Exists(dest).Should().BeFalse("a cut that fell back writes nothing");
            var outDir = Path.GetDirectoryName(dest)!;
            (Directory.Exists(outDir) ? Directory.GetDirectories(outDir, ".vsj-smartcut-*") : Array.Empty<string>())
                .Should().BeEmpty("the temp dir is swept");
        }
        else
        {
            shipped.Should().NotBeEmpty();
            headHashes.Should().Equal(shipped,
                "a result the engine reports Done has exactly the head the shipped engine made, not fewer, later or different frames");
        }

        return result.FellBack;
    }

    // serves-spec: SPEC-001#I44 / SPEC-001#I46 - the control: a CLOSED-GOP source at K-3, K-2 and K-1 frames is cut exactly. The
    // head is 3, 2 and 1 frames, the engine does not fall back, and its head is the shipped head.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(7.9)]         // K-3, K = 8 s
    [InlineData(7.933333)]    // K-2
    [InlineData(7.966667)]    // K-1
    public async Task AClosedGopSource_JustBeforeAKeyframe_IsCutExactly(double start)
    {
        RequireBinaries();
        var src = await MakeAsync(Shape.ClosedGop);

        var fellBack = await CutAndCheckAsync(src, start, $"closed GOP, Start {start}");

        fellBack.Should().BeFalse("a closed-GOP source has no leading pictures: the input-seek head is the shipped head");
    }

    // serves-spec: SPEC-001#I44 / SPEC-001#I46 - the defect: MPEG-4 part 2 with B-frames at K-2 and K-1 (K = 6 s). The input-seek
    // head held 1 frame where the shipped head held 2, and none at K-1; shipped at 983d668 the engine reported Done anyway. Now it
    // falls back (or, if a later change makes the head right, the final starts with the shipped head).
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(5.933333)]    // K-2
    [InlineData(5.966667)]    // K-1
    public async Task AnMpeg4SourceWithBFrames_JustBeforeAKeyframe_NeverGivesAWrongHead(double start)
    {
        RequireBinaries();
        var src = await MakeAsync(Shape.Mpeg4);

        await CutAndCheckAsync(src, start, $"MPEG-4 part 2, Start {start}");
    }

    // serves-spec: SPEC-001#I44 / SPEC-001#I46 - the same on an x264 open-GOP source at K-2 and K-1 of the key frames that have
    // leading pictures (K = 1.6667, 3.3333 and 6.6667 s): 2 frames shipped, 1 or none from the input seek. Not every key frame of
    // the file has leading pictures (K = 5 s does not), which is why the starts are listed.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(1.6)]
    [InlineData(1.633333)]
    [InlineData(3.266667)]
    [InlineData(3.3)]
    [InlineData(6.6)]
    [InlineData(6.633333)]
    public async Task AnOpenGopSource_JustBeforeAKeyframe_NeverGivesAWrongHead(double start)
    {
        RequireBinaries();
        var src = await MakeAsync(Shape.OpenGop);

        await CutAndCheckAsync(src, start, $"open GOP, Start {start}");
    }

    // serves-spec: SPEC-001#I46 / SPEC-001#I70 - the same open-GOP file with a Start well inside a GOP (4 frames or more before the
    // key frame). T-191 cut these exactly - the head is right there - and the control said so; T-223 found that the JOINT behind a
    // non-IDR key frame is not (the copied part's frames refer to the GOP before it), so every one of these now falls back with the
    // keyframe-type reason, before a head is written. The head check above stays the guard for the sources that do get that far.
    [Trait("serves-spec", "SPEC-001")]
    [SkippableTheory]
    [InlineData(1.5)]
    [InlineData(3.0)]
    [InlineData(6.4)]
    public async Task AnOpenGopSource_WellInsideAGop_FallsBack_BecauseItsKeyframeIsNotAnIdr(double start)
    {
        RequireBinaries();
        var src = await MakeAsync(Shape.OpenGop);

        var fellBack = await CutAndCheckAsync(src, start, $"open GOP, Start {start}");

        fellBack.Should().BeTrue("the key frame the head ends on is a non-IDR I-frame, so the copied part is not self-contained (T-223)");
    }
}
