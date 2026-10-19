using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Split;

/// <summary>The outcome of one frame-exact cut, including whether it had to fall back.</summary>
/// <param name="OutputPath">Where the produced file landed.</param>
/// <param name="Strategy">Which path actually ran.</param>
/// <param name="FellBack">True when exact cutting was unavailable and the caller should use the lossless path.</param>
/// <param name="FallbackReason">Why exact cutting was unavailable (null unless <paramref name="FellBack"/>).</param>
/// <param name="ReencodedDuration">How much video was re-encoded — the cost actually paid.</param>
/// <param name="EffectiveStart">
/// Where the produced file really starts in the source (T-190): the requested start moved up to the first source video
/// frame at or after it, so at most one frame later (33 ms at 30 fps) and equal to the request when it already sat on a
/// frame or the source cannot be asked. Null unless a file was produced. A caller that shows the cut time (T-196) reads
/// this, not the time it asked for.
/// </param>
public sealed record SmartCutResult(
    string? OutputPath,
    SmartCutStrategy Strategy,
    bool FellBack,
    string? FallbackReason,
    TimeSpan ReencodedDuration,
    TimeSpan? EffectiveStart = null);

/// <summary>
/// Frame-exact cutting (T-124, epic G-042): produce <c>[start, end)</c> starting at the requested time, to the frame,
/// rather than at the nearest keyframe.
/// </summary>
public interface ISmartCutEngine
{
    /// <summary>
    /// Cut <paramref name="inputPath"/> from <paramref name="start"/> to <paramref name="end"/> (null =
    /// end of file) into <paramref name="outputPath"/>, starting at the first video frame at or after <paramref name="start"/>
    /// (<see cref="SmartCutResult.EffectiveStart"/>; the request itself when it sits on a frame).
    /// Returns a result whose <c>FellBack</c> flag tells the caller exact cutting was not possible for
    /// this source — the caller then runs the ordinary lossless cut rather than shipping a bad file.
    /// </summary>
    Task<SmartCutResult> CutAsync(
        string inputPath,
        TimeSpan start,
        TimeSpan? end,
        string outputPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="ISmartCutEngine"/>. Re-encodes only the head fragment (requested start → next
/// keyframe) and joins it to the source's own packets from that keyframe on — so the cut is frame-exact while
/// 99%+ of the output stays untouched bytes.
///
/// <para><b>Two ffmpeg runs for a head re-encode (T-190):</b> the head encode, then one concat that reads
/// <c>[head, the source itself with inpoint/outpoint]</c> (<see cref="SmartCutArgsBuilder.RenderJointList"/>). The kept
/// part is never copied into a file of its own first, so it is written once, and the joint lands on its boundary frame
/// with no audio overlap. A source whose format <c>start_time</c> is not zero, or that is Matroska/WebM, is a FALLBACK
/// (<see cref="SmartCutArgsBuilder.TryResolveEncoders"/>): its keyframe times and the head's <c>-ss</c> offsets would not
/// coincide, or the head's own timestamps would shift against the copied part.</para>
///
/// <para><b>The start is snapped to a frame (T-190, SPEC-001 I43).</b> The cut starts at the first source video frame at or
/// after the requested time (within a few frame intervals: a video gap is a fall back, <see cref="MaxSnapShift"/>), found
/// before the plan is made, so the head's frames sit on the same grid as the copied part's.
/// A Start between two frames used to leave the head's frames on the encoder's 1/fps grid counted from the Start: the joint
/// 33 ms early with a muxer timestamp line, or one repeated frame. The head the engine wrote is also checked for a video
/// frame: a head with none would make the concat drop the source's video, and a cut that loses the video is a fall back,
/// not a result.</para>
///
/// <para>Every intermediate lands in a temp dir that is swept in a <c>finally</c>, and the final file is
/// only moved into place once it exists — the same cancel-safety contract <see cref="SplitEngine"/>
/// holds. A source whose codecs cannot be reproduced is reported as a FALLBACK, never guessed at.</para>
/// </summary>
public sealed class SmartCutEngine : ISmartCutEngine
{
    private readonly IFfmpegRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly IVideoFrameProbe? _frames;

    public SmartCutEngine(IFfmpegRunner runner, IMediaProbe probe)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _frames = probe as IVideoFrameProbe; // MediaProbe is one; a probe that is not leaves the frame snap and the head check off
    }

    /// <summary>
    /// True when the probe this engine was built over answers the frame questions (<see cref="IVideoFrameProbe"/>), so the frame
    /// snap and the head check run. False is silent by design - a fake probe in a unit test cuts at the request - which is why the
    /// production graph is pinned to be true (<c>SmartCutEngineCompositionTests</c>, and the composition root's source in the App
    /// tests): swapping the shared <c>MediaProbe</c> for a probe without the interface would switch both off with every test green.
    /// </summary>
    internal bool FrameQuestionsActive => _frames is not null;

    /// <inheritdoc />
    public async Task<SmartCutResult> CutAsync(
        string inputPath,
        TimeSpan start,
        TimeSpan? end,
        string outputPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (!File.Exists(inputPath))
        {
            throw new SplitException($"Input '{inputPath}' does not exist.");
        }

        var probed = await _probe.ProbeAsync(inputPath, ct).ConfigureAwait(false);
        if (probed is not ProbeResult.ProbeSucceeded ok)
        {
            var reason = probed is ProbeResult.ProbeFailed f ? f.Reason : "unknown probe error";
            throw new SplitException($"Cannot cut '{inputPath}': {reason}");
        }

        var keyframes = await _probe.GetKeyframesAsync(inputPath, ct).ConfigureAwait(false);
        var plan = SmartCutPlanner.Plan(start, end, keyframes);

        // Already on a keyframe: the lossless path yields exactly this - hand back to it rather than
        // re-encoding for no reason.
        if (plan.Strategy == SmartCutStrategy.PureCopy)
        {
            return new SmartCutResult(null, plan.Strategy, FellBack: true,
                "the requested time is already on a keyframe — the lossless cut is exact here",
                TimeSpan.Zero);
        }

        // Only re-encode when we can genuinely reproduce the source's streams.
        if (!SmartCutArgsBuilder.TryResolveEncoders(ok.Info, out var vEnc, out var aEnc, out var why, inputPath, outputPath))
        {
            return new SmartCutResult(null, plan.Strategy, FellBack: true, why, TimeSpan.Zero);
        }

        // T-190: start ON a frame. The first source video frame at or after the request (a frame within a millisecond before
        // it counts as at it) becomes the start BEFORE the head is built, so the head's frame grid is the copied part's.
        // The plan is made again from it: a start within a frame of a keyframe becomes PureCopy (the lossless cut is exact
        // there), and one between the last frame and the end of the range stays as it was. Asked only now, so a source Exact
        // refuses costs no ffprobe.
        if (_frames is not null && ok.Info.HasVideo
            && await _frames.FirstVideoFrameAtOrAfterAsync(inputPath, start, ct).ConfigureAwait(false) is { } firstFrame
            && firstFrame != start
            && (end is not { } endTime || firstFrame < endTime))
        {
            // The snap moves the start by up to a frame, not by a gap in the picture: when the next frame is further than a
            // few frame intervals away (a video that stops while its audio goes on) the cut would start there and silently
            // drop the audio in between, so the request is handed back to the lossless cut with the reason instead.
            if (firstFrame - start > MaxSnapShift(ok.Info))
            {
                return new SmartCutResult(null, plan.Strategy, FellBack: true,
                    "the video has no frame within a few frames after the requested time",
                    TimeSpan.Zero);
            }

            start = firstFrame;
            plan = SmartCutPlanner.Plan(start, end, keyframes);
            if (plan.Strategy == SmartCutStrategy.PureCopy)
            {
                return new SmartCutResult(null, plan.Strategy, FellBack: true,
                    "the first frame at or after the requested time is a keyframe — the lossless cut is exact here",
                    TimeSpan.Zero);
            }
        }

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? throw new SplitException($"Cannot resolve an output directory for '{outputPath}'.");
        Directory.CreateDirectory(outDir);

        var tempDir = Path.Combine(outDir, ".vsj-smartcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var ext = Path.GetExtension(outputPath);
            var headPath = Path.Combine(tempDir, "head" + ext);
            var finalTemp = Path.Combine(tempDir, "final" + ext);

            // --- 1. The re-encoded head fragment (bounded by one GOP). ---
            var headEnd = plan.Strategy == SmartCutStrategy.HeadReencode
                ? plan.HeadEnd!.Value
                : plan.End ?? ok.Info.Duration;

            var headArgs = SmartCutArgsBuilder.HeadReencode(
                inputPath, plan.Start, headEnd, ok.Info, vEnc!, aEnc, headPath);

            var headRun = await _runner.RunAsync(headArgs, null, null, ct).ConfigureAwait(false);
            if (headRun.ExitCode != 0 || !File.Exists(headPath))
            {
                throw new SplitException(
                    $"Exact cut failed while re-encoding the leading fragment of '{Path.GetFileName(inputPath)}'.");
            }

            // A head with no video frame (the range holds none, or the encoder dropped them) would make the concat take its
            // streams from an audio-only file and drop the source's video: the file would be Done and have no picture.
            if (_frames is not null && ok.Info.HasVideo
                && await _frames.CountVideoPacketsAsync(headPath, ct).ConfigureAwait(false) is 0)
            {
                return new SmartCutResult(null, plan.Strategy, FellBack: true,
                    "no video frame lies in the part to re-encode, so the cut would have no picture",
                    TimeSpan.Zero);
            }

            progress?.Report(0.5);

            // --- 2. No copyable tail (request sits in the final GOP) -> the head IS the output. ---
            if (plan.Strategy == SmartCutStrategy.FullReencode)
            {
                MoveIntoPlace(headPath, outputPath);
                progress?.Report(1.0);
                return new SmartCutResult(outputPath, plan.Strategy, false, null, plan.ReencodedDuration, plan.Start);
            }

            // --- 3. One concat: the head, then the SOURCE ITSELF from the boundary keyframe (stream copy - reuses the
            //        Join builder's command, no second impl). The kept bytes are written once, into the final. ---
            var headEndTime = plan.HeadEnd!.Value;
            var listPath = Path.Combine(tempDir, "concat.txt");
            await File.WriteAllTextAsync(
                listPath,
                SmartCutArgsBuilder.RenderJointList(headPath, headEndTime - plan.Start, inputPath, headEndTime, plan.End),
                ct).ConfigureAwait(false);

            var concatArgs = JoinArgsBuilder.ConcatCopy(listPath, finalTemp);
            var concatRun = await _runner.RunAsync(concatArgs, null, null, ct).ConfigureAwait(false);
            if (concatRun.ExitCode != 0 || !File.Exists(finalTemp))
            {
                throw new SplitException(
                    $"Exact cut failed while joining the fragments of '{Path.GetFileName(inputPath)}'.");
            }

            MoveIntoPlace(finalTemp, outputPath);
            progress?.Report(1.0);

            return new SmartCutResult(outputPath, plan.Strategy, false, null, plan.ReencodedDuration, plan.Start);
        }
        finally
        {
            TrySweep(tempDir);
        }
    }

    /// <summary>How many frame intervals the start may move up to a frame (the next frame is normally under one away).</summary>
    internal const int SnapShiftFrames = 3;

    /// <summary>The largest move of the start when the video's frame rate is unknown.</summary>
    internal static readonly TimeSpan SnapShiftWhenRateUnknown = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The furthest the frame snap may move the start (T-190, third pass): <see cref="SnapShiftFrames"/> frame intervals of the
    /// first video stream's frame rate, or <see cref="SnapShiftWhenRateUnknown"/> when the rate is unknown or not usable. A
    /// frame further away than that is a gap in the picture, not the next frame.
    /// </summary>
    internal static TimeSpan MaxSnapShift(MediaInfo info)
    {
        var fps = info.VideoStreams.Count > 0 ? info.VideoStreams[0].FrameRate : null;
        return fps is { } rate && double.IsFinite(rate) && rate > 0
            ? TimeSpan.FromSeconds(SnapShiftFrames / rate)
            : SnapShiftWhenRateUnknown;
    }

    private static void MoveIntoPlace(string tempFile, string destination)
    {
        var dest = Path.GetFullPath(destination);
        if (File.Exists(dest))
        {
            File.Delete(dest);
        }

        File.Move(tempFile, dest);
    }

    private static void TrySweep(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort: a stray temp dir must never fail an otherwise-successful cut.
        }
    }
}
