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
/// this, not the time it asked for. For a cut that only trims the end (T-224) it is the keyframe the file starts on.
/// </param>
/// <param name="EndSnapped">
/// T-224: true when the request starts on a keyframe (so <paramref name="Strategy"/> is <see cref="SmartCutStrategy.PureCopy"/>)
/// but ends inside a GOP and the engine could not cut it exactly (<paramref name="FallbackReason"/> says why): the lossless cut
/// the caller falls back to ends at a keyframe, not at the end that was asked for, which is worth telling the user. False for a
/// start on a keyframe with the end on one too, or with no end - there the lossless cut is exact and needs no note.
/// </param>
public sealed record SmartCutResult(
    string? OutputPath,
    SmartCutStrategy Strategy,
    bool FellBack,
    string? FallbackReason,
    TimeSpan ReencodedDuration,
    TimeSpan? EffectiveStart = null,
    bool EndSnapped = false);

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
/// 33 ms early with a muxer timestamp line, or one repeated frame. The head the engine wrote is also checked against the
/// source (T-191, SPEC-001 I46): it must hold as many video frames as the source has in the span it was cut from, the first
/// at its time 0, and not none - a head with none would make the concat drop the source's video. A head that fails is a fall
/// back to the lossless cut, not a result.</para>
///
/// <para><b>The head seeks to its keyframe before it decodes (T-191, SPEC-001 I44).</b> <see cref="SmartCutArgsBuilder.HeadReencode"/>
/// puts <c>-ss</c> before <c>-i</c>, so the decode starts at the keyframe before the start whatever the position in the file;
/// the output seek it replaced decoded everything from the start of the file up to the cut.</para>
///
/// <para><b>Real progress (T-191, SPEC-001 I69).</b> Each ffmpeg run reports into its own slice of the row's fraction, below
/// 1.0: the head encode into <c>[0, w)</c>, the concat into <c>[w, 1)</c>, with <c>w = h·k / (h·k + c)</c> from
/// <see cref="SmartCutCost"/> (the same function the Bulk Cut batch weighs the row by). A <c>FullReencode</c> reports its one
/// run across <c>[0, 1)</c>. The fraction never goes backwards, a run that reports nothing still ends at its slice's end, and
/// 1.0 is reported once, after the output is in place — never by a cut that fails, is cancelled or falls back. The ffprobe
/// reads around the head (the frame snap before it, the head's frame times and the source's after it) carry no progress of
/// their own and no weight: each is bounded by a GOP and costs a fraction of a second against a head of seconds (measured
/// 0.06-0.15 s each on the bench's 4K fixtures against a head of 1.5-5 s), so the row sits at 0 while the snap is read, at or
/// just below the end of the head's slice while the head is checked (a head that fails the check is handed back to the lossless cut, which the Bulk
/// engine keeps from moving the row's bar back).</para>
///
/// <para><b>An end inside a GOP under a start on a keyframe is an end trim (T-224, SPEC-001 I71).</b> The planner calls a start
/// on a keyframe a <c>PureCopy</c> whatever the end is, and the lossless cut ends at a keyframe, so that request used to come back
/// with its outro snapped. It is now the joint without its head: one concat of the source itself, <c>inpoint</c> the keyframe and
/// <c>outpoint</c> the end (<see cref="SmartCutArgsBuilder.RenderEndTrimList"/>), a stream copy. It asks the same encoder gate as
/// every Exact cut, and a source with a subtitle, data or cover-picture stream keeps the lossless cut with the end's snap
/// announced (<see cref="SmartCutResult.EndSnapped"/>).</para>
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

        // T-224: a start on a keyframe is a PureCopy whatever the end is, and the lossless cut ends at a keyframe - so with an end
        // inside a GOP it would snap the end the user set. That one case is not handed back: it is the joint without its head.
        var endTrim = plan.Strategy == SmartCutStrategy.PureCopy && EndIsInsideAGop(plan.End, keyframes, ok.Info.Duration);

        // Already on a keyframe, and so is the end (or there is none): the lossless path yields exactly this - hand back to it
        // rather than re-encoding for no reason.
        if (plan.Strategy == SmartCutStrategy.PureCopy && !endTrim)
        {
            return new SmartCutResult(null, plan.Strategy, FellBack: true,
                "the requested time is already on a keyframe — the lossless cut is exact here",
                TimeSpan.Zero);
        }

        // Only re-encode when we can genuinely reproduce the source's streams. The end trim re-encodes nothing, but it asks the
        // same question: the Bulk Cut row decides from this one predicate whether it shows the end as requested, so the row and
        // the run must not disagree (SPEC-001 I71). A source with a subtitle, data or cover-picture stream is refused here too, for
        // the head leg and the end trim alike (T-224, T-238): the lossless cut carries every stream and the reason says so.
        if (!SmartCutArgsBuilder.TryResolveEncoders(ok.Info, out var vEnc, out var aEnc, out var why, inputPath, outputPath))
        {
            return new SmartCutResult(null, plan.Strategy, FellBack: true, why, TimeSpan.Zero, EndSnapped: endTrim);
        }

        // T-190: start ON a frame. The first source video frame at or after the request (a frame within a millisecond before
        // it counts as at it) becomes the start BEFORE the head is built, so the head's frame grid is the copied part's.
        // The plan is made again from it: a start within a frame of a keyframe becomes PureCopy (the lossless cut is exact
        // there), and one between the last frame and the end of the range stays as it was. Asked only now, so a source Exact
        // refuses costs no ffprobe. An end trim starts on a keyframe, which is a frame: nothing to snap.
        if (!endTrim
            && _frames is not null && ok.Info.HasVideo
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
                // The snapped start sits on a keyframe: with the end inside a GOP that is an end trim too (T-224).
                endTrim = EndIsInsideAGop(plan.End, keyframes, ok.Info.Duration);
                if (!endTrim)
                {
                    return new SmartCutResult(null, plan.Strategy, FellBack: true,
                        "the first frame at or after the requested time is a keyframe — the lossless cut is exact here",
                        TimeSpan.Zero);
                }
            }
        }

        // T-223: the copied part starts at HeadEnd, and behind a keyframe that is not an IDR (an open GOP: x264 open-gop=1, some
        // cameras) it is not self-contained - its frames refer to the GOP before it and the SPS/PPS are the head's - so the final
        // printed decoder errors and lost frames at the joint with exit code 0. Asked per HeadEnd (a file can mix both), only
        // where there is a joint (a FullReencode has no copied part), and before anything is written: a keyframe that is not
        // provably an IDR (the probe could not say included) is a fall back, never an Exact cut on unknown ground. An end trim has
        // no head and no joint: its file is the source's own packets from the keyframe, as the lossless cut's is.
        if (plan.Strategy == SmartCutStrategy.HeadReencode
            && _frames is not null
            && IsH264(ok.Info)
            && await IsIdrAsync(_frames, inputPath, plan.HeadEnd!.Value, ct).ConfigureAwait(false) is not true)
        {
            return new SmartCutResult(null, plan.Strategy, FellBack: true, NotAnIdrKeyframeReason, TimeSpan.Zero);
        }

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? throw new SplitException($"Cannot resolve an output directory for '{outputPath}'.");
        Directory.CreateDirectory(outDir);

        var tempDir = Path.Combine(outDir, ".vsj-smartcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var ext = Path.GetExtension(outputPath);

            if (endTrim)
            {
                return await TrimEndAsync(inputPath, plan, keyframes, outputPath, tempDir, progress, ct).ConfigureAwait(false);
            }

            var headPath = Path.Combine(tempDir, "head" + ext);
            var finalTemp = Path.Combine(tempDir, "final" + ext);

            // The row's fraction (T-191): the head encode owns [0, w), the concat [w, 1), w = the head's share of the cut's cost
            // (SmartCutCost, the function the Bulk Cut batch weighs this row by). A FullReencode's one run owns all of [0, 1).
            var rowProgress = new RowProgress(progress);
            var headShare = plan.Strategy == SmartCutStrategy.HeadReencode
                ? SmartCutCost.Of(plan, ok.Info.Duration).ReencodeShare
                : 1d;

            // --- 1. The re-encoded head fragment (the decode starts at the keyframe before the start: one GOP at most). ---
            var headEnd = plan.Strategy == SmartCutStrategy.HeadReencode
                ? plan.HeadEnd!.Value
                : plan.End ?? ok.Info.Duration;

            var headArgs = SmartCutArgsBuilder.HeadReencode(
                inputPath, plan.Start, headEnd, ok.Info, vEnc!, aEnc, headPath);

            // ffmpeg's time= counts the head's OUTPUT time, 0 .. HeadEnd - Start.
            var headRun = await _runner.RunAsync(
                headArgs, headEnd - plan.Start, rowProgress.Slice(0d, headShare), ct).ConfigureAwait(false);
            if (headRun.ExitCode != 0 || !File.Exists(headPath))
            {
                throw new SplitException(
                    $"Exact cut failed while re-encoding the leading fragment of '{Path.GetFileName(inputPath)}'.");
            }

            // The head must be the frames it should be (T-190: not empty; T-191: the source's own frames of [Start, HeadEnd), in
            // number, starting on the first of them). A head that is not is a fall back, never a Done.
            if (_frames is not null && ok.Info.HasVideo
                && await CheckHeadAsync(_frames, inputPath, headPath, plan.Start, headEnd, ok.Info, ct).ConfigureAwait(false) is { } whyNot)
            {
                return new SmartCutResult(null, plan.Strategy, FellBack: true, whyNot, TimeSpan.Zero);
            }

            // The head is verified: its slice is done, even when ffmpeg printed no time= for a run this short.
            rowProgress.Reach(headShare);

            // --- 2. No copyable tail (request sits in the final GOP) -> the head IS the output. ---
            if (plan.Strategy == SmartCutStrategy.FullReencode)
            {
                MoveIntoPlace(headPath, outputPath);
                rowProgress.Complete();
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

            // ffmpeg's time= counts the concat's output time from the START of the head, so the whole kept range is the total
            // and the head's own part of it (already reported by the head's slice) is where the concat's slice begins.
            var kept = (plan.End ?? ok.Info.Duration) - plan.Start;
            var headFraction = kept > TimeSpan.Zero ? (headEndTime - plan.Start).TotalSeconds / kept.TotalSeconds : 0d;

            var concatArgs = JoinArgsBuilder.ConcatCopy(listPath, finalTemp);
            var concatRun = await _runner.RunAsync(
                concatArgs, kept, rowProgress.Slice(headShare, 1d, headFraction), ct).ConfigureAwait(false);
            if (concatRun.ExitCode != 0 || !File.Exists(finalTemp))
            {
                throw new SplitException(
                    $"Exact cut failed while joining the fragments of '{Path.GetFileName(inputPath)}'.");
            }

            rowProgress.Reach(1d);
            MoveIntoPlace(finalTemp, outputPath);
            rowProgress.Complete();

            return new SmartCutResult(outputPath, plan.Strategy, false, null, plan.ReencodedDuration, plan.Start);
        }
        finally
        {
            TrySweep(tempDir);
        }
    }

    /// <summary>
    /// True when <paramref name="end"/> is a time inside a GOP: there is one, it is before the end of the file and it is not on a
    /// keyframe (within <see cref="SmartCutPlanner.OnKeyframeTolerance"/>). A lossless cut ends at a keyframe, so only then does it
    /// not end where it was asked to (T-224); an end on a keyframe, or at or past the end of the file, is exact either way. The
    /// end of the file is exact: the lossless pass drops a cut only when it is at or past the duration, so an end a few
    /// milliseconds short of it still snaps to a keyframe there.
    /// </summary>
    private static bool EndIsInsideAGop(TimeSpan? end, IReadOnlyList<TimeSpan> keyframes, TimeSpan duration) =>
        end is { } e
        && e < duration
        && !SmartCutPlanner.IsOnKeyframe(e, keyframes);

    /// <summary>
    /// A cut that starts on a keyframe and ends inside a GOP (T-224, SPEC-001 I71): the Exact joint without its head. One concat of
    /// the source itself, <c>inpoint</c> the keyframe the cut starts on and <c>outpoint</c> the requested end
    /// (<see cref="SmartCutArgsBuilder.RenderEndTrimList"/>) - a stream copy through the unchanged
    /// <see cref="JoinArgsBuilder.ConcatCopy"/>, so nothing is re-encoded and the kept bytes are written once. The end is a decoding
    /// time to the concat demuxer, so on a source with B-frames the file keeps up to <c>has_b_frames</c> frames past it (T-220), as
    /// the end of every Exact cut with an end does.
    /// </summary>
    private async Task<SmartCutResult> TrimEndAsync(
        string inputPath,
        SmartCutPlan plan,
        IReadOnlyList<TimeSpan> keyframes,
        string outputPath,
        string tempDir,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // The lossless cut starts on the keyframe the request sits on (within the planner's 10 ms): so does this one. The keyframe's
        // own time, not the request, because the demuxer's seek to an inpoint a few ms past it still lands on the keyframe and the
        // list should say what happens.
        var inpoint = keyframes.OrderBy(k => (k - plan.Start).Duration()).First();
        var end = plan.End!.Value;

        var listPath = Path.Combine(tempDir, "concat.txt");
        await File.WriteAllTextAsync(listPath, SmartCutArgsBuilder.RenderEndTrimList(inputPath, inpoint, end), ct).ConfigureAwait(false);

        var finalTemp = Path.Combine(tempDir, "final" + Path.GetExtension(outputPath));
        var rowProgress = new RowProgress(progress);

        // ffmpeg's time= counts the concat's output time, 0 .. End - inpoint: its one run owns the whole row.
        var run = await _runner.RunAsync(
            JoinArgsBuilder.ConcatCopy(listPath, finalTemp), end - inpoint, rowProgress.Slice(0d, 1d), ct).ConfigureAwait(false);
        if (run.ExitCode != 0 || !File.Exists(finalTemp))
        {
            throw new SplitException(
                $"Exact cut failed while cutting the end of '{Path.GetFileName(inputPath)}'.");
        }

        rowProgress.Reach(1d);
        MoveIntoPlace(finalTemp, outputPath);
        rowProgress.Complete();

        return new SmartCutResult(outputPath, plan.Strategy, false, null, TimeSpan.Zero, inpoint);
    }

    /// <summary>
    /// Why a head with no video frame is not used (T-190): the concat would take its streams from an audio-only file and drop
    /// the source's video, and the file would be Done and have no picture. No parentheses (the row warning wraps the reason).
    /// </summary>
    internal const string NoPictureReason = "no video frame lies in the part to re-encode, so the cut would have no picture";

    /// <summary>
    /// Why a cut whose copied part would start at a keyframe that is not an IDR picture is not used (T-223, SPEC-001 I70). No
    /// parentheses and no internal terms (the row warning wraps the reason in its own parentheses and the user reads it).
    /// </summary>
    public const string NotAnIdrKeyframeReason = "frame-exact cutting is not supported yet for this video's keyframe type";

    /// <summary>
    /// The probe's answer to "is the keyframe an IDR?"; a probe that throws (anything but a cancellation) cannot say, which is the
    /// same as null: a fall back, never an Exact cut on unknown ground (SPEC-001 I70).
    /// </summary>
    private static async Task<bool?> IsIdrAsync(IVideoFrameProbe frames, string path, TimeSpan keyframe, CancellationToken ct)
    {
        try
        {
            return await frames.IsIdrKeyframeAsync(path, keyframe, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>True for an H.264 first video stream (the codec the IDR question is about).</summary>
    private static bool IsH264(MediaInfo info) =>
        info.HasVideo
        && (string.Equals(info.VideoStreams[0].CodecName, "h264", StringComparison.OrdinalIgnoreCase)
            || string.Equals(info.VideoStreams[0].CodecName, "avc1", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Why a head that is not the source's frames is not used (T-191). No parentheses and no internal terms (the row warning
    /// wraps the reason in its own parentheses and the user reads it).
    /// </summary>
    internal const string HeadNotFrameExactReason = "the cut cannot be made frame-exact for this video yet";

    /// <summary>
    /// The end-to-end check of the head the engine wrote (T-191, SPEC-001 I46): its frames against the source's. Null when the head
    /// passes or the probe cannot say; otherwise the reason it is not used.
    ///
    /// <para>The input seek lands on the key frame before the Start by its DECODING time and decodes from there. Where that key
    /// frame has leading pictures - B-frames displayed before it that need the GOP before it (an open GOP, MPEG-4 part 2 with
    /// B-frames) - the decoder cannot output them, so a head that should hold the source's two frames of <c>[Start, HeadEnd)</c>
    /// holds one, starting a frame late, or none, with exit code 0. (The output seek decoded from the start of the file and had
    /// them.) So the head must hold exactly as many frames as the source has in the span it was cut from, the first at the head's
    /// time 0. A count or a first frame that does not match - also a variable-frame-rate source ffmpeg retimed, whatever the
    /// cause - is a fall back to the lossless cut. A head with none keeps its own reason (<see cref="NoPictureReason"/>).</para>
    /// </summary>
    private static async Task<string?> CheckHeadAsync(
        IVideoFrameProbe frames, string sourcePath, string headPath, TimeSpan start, TimeSpan headEnd, MediaInfo info, CancellationToken ct)
    {
        // The head's whole file is the question, so the window runs a second past its length: a frame beyond it would be an extra
        // frame (and an AVI head lists its last frame at the length itself, its times being decoding times).
        var head = await frames.GetVideoFrameTimesAsync(headPath, TimeSpan.Zero, headEnd - start + HeadReadSlack, ct).ConfigureAwait(false);
        if (head is null)
        {
            return null; // the head's frames cannot be told apart: not called wrong
        }

        if (head.Count == 0)
        {
            return NoPictureReason;
        }

        // Half a frame of the source's rate, at least 2 ms (the encoder's time base): the first frame of a head is at its time 0.
        var rate = info.VideoStreams.Count > 0 ? info.VideoStreams[0].FrameRate : null;
        var tolerance = rate is { } fps && double.IsFinite(fps) && fps > 0
            ? TimeSpan.FromSeconds(Math.Max(0.5 / fps, 0.002))
            : TimeSpan.FromSeconds(1d / 60);
        if (head[0] > tolerance)
        {
            return HeadNotFrameExactReason;
        }

        var source = await frames.GetVideoFrameTimesAsync(sourcePath, start, headEnd, ct).ConfigureAwait(false);
        return source is not null && source.Count != head.Count ? HeadNotFrameExactReason : null;
    }

    /// <summary>How far past its length the head is read for its frames (<see cref="CheckHeadAsync"/>).</summary>
    private static readonly TimeSpan HeadReadSlack = TimeSpan.FromSeconds(1);

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

    /// <summary>
    /// Copy-equivalent seconds per second of re-encoded video (T-191): a second of head the engine re-encodes costs this many
    /// times the wall time of a second it stream-copies. It weights the cut's two runs against each other
    /// (<see cref="SmartCutCost"/>), both in the row's own fraction (<c>w = h·k / (h·k + c)</c>) and in the Bulk Cut batch bar and
    /// time left, which weigh an Exact row by <c>h·k + c</c>. Chosen from the bench's <c>smart</c> scenario (T-191's and T-192's Build
    /// logs): at each cut position the row's measured head share of the wall time, head over head plus concat, is matched by
    /// <c>h·k / (h·k + c)</c>. T-191 fitted 38 to 244 (median 120, geometric mean 102) for a head at libx264's default <c>medium</c>
    /// preset and chose 100. T-192 encodes the head at <c>veryfast</c> (<see cref="SmartCutArgsBuilder.X264HeadPreset"/>), 1.5-1.9
    /// times faster, and re-measured it in one run with the medium head beside it: <c>k</c> 24 to 93 (median 57, geometric mean 51)
    /// on <c>4k</c>, <c>1080</c>, <c>g10</c> and <c>4k10m</c> at their named positions, against 39 to 150 (median 96, geometric
    /// mean 84) for the medium head of the same run, because a head now costs 0.2-0.6 s of wall time per second re-encoded and the
    /// concat 3-18 ms per second copied (its start-up included), and the concat's cost depends on the disk and the bitrate. 50 is the
    /// round number in that range. The fixtures are libx264 only: a head for another encoder (vp9, vp8, av1, mpeg4, mpeg2) did not get
    /// faster and is now weighed about half as heavy as it should be, which moves the bars' shape and the estimate, not what is cut. It is approximate by nature and shapes only how the bars move, never what is cut: the row's
    /// fraction is monotonic whatever it is.
    /// </summary>
    public const double EncodeCostFactor = 50d;

    /// <summary>
    /// The highest fraction a RUN may report (T-191). 1.0 is reported once, by the engine, after the output is in place - never by
    /// an ffmpeg run, whose last <c>time=</c> reaches its total a moment before the file is closed and moved.
    /// </summary>
    internal const double MaxRunProgress = 0.999;

    /// <summary>
    /// One cut's reports into the row's fraction (T-191, SPEC-001 I69): a run reports into a slice of it, the value never goes
    /// below an earlier one, no run reports 1.0, and 1.0 is reported once, by <see cref="Complete"/>. Every call comes from the
    /// ffmpeg run in flight or from the engine between runs (the runner returns only after its stderr is read), and each is
    /// serialized by the lock anyway.
    /// </summary>
    private sealed class RowProgress
    {
        private readonly IProgress<double>? _sink;
        private readonly object _gate = new();
        private double _last;
        private bool _completed;

        public RowProgress(IProgress<double>? sink) => _sink = sink;

        /// <summary>
        /// The progress handed to a run: its fractions 0..1 land in <c>[<paramref name="lo"/>, <paramref name="hi"/>]</c>.
        /// <paramref name="from"/> is the run's own fraction at which the slice begins (the concat's <c>time=</c> starts at the head's
        /// length, which the head's slice already reported). Null when nobody listens, so the runner is handed no progress.
        /// </summary>
        public IProgress<double>? Slice(double lo, double hi, double from = 0d) =>
            _sink is null ? null : new SliceProgress(this, lo, hi, from);

        /// <summary>A run finished without reporting its slice's end (very short, or no <c>time=</c>): the row is at least there.</summary>
        public void Reach(double value) => Report(value);

        /// <summary>The output is in place: report 1.0, once.</summary>
        public void Complete()
        {
            lock (_gate)
            {
                if (_completed || _sink is null)
                {
                    return;
                }

                _completed = true;
                _last = 1d;
                _sink.Report(1d);
            }
        }

        private void Report(double value)
        {
            if (_sink is null || double.IsNaN(value))
            {
                return;
            }

            value = Math.Min(Math.Max(value, 0d), MaxRunProgress);
            lock (_gate)
            {
                if (_completed || value <= _last)
                {
                    return;
                }

                _last = value;
                _sink.Report(value);
            }
        }

        private sealed class SliceProgress : IProgress<double>
        {
            private readonly RowProgress _row;
            private readonly double _lo;
            private readonly double _hi;
            private readonly double _from;

            public SliceProgress(RowProgress row, double lo, double hi, double from)
            {
                _row = row;
                _lo = lo;
                _hi = hi;
                _from = from;
            }

            public void Report(double fraction)
            {
                var span = 1d - _from;
                var within = span > 0d ? Math.Clamp((fraction - _from) / span, 0d, 1d) : 1d;
                _row.Report(_lo + ((_hi - _lo) * within));
            }
        }
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
