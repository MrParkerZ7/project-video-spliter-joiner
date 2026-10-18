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
public sealed record SmartCutResult(
    string? OutputPath,
    SmartCutStrategy Strategy,
    bool FellBack,
    string? FallbackReason,
    TimeSpan ReencodedDuration);

/// <summary>
/// Frame-exact cutting (T-124, epic G-042): produce <c>[start, end)</c> starting at EXACTLY the
/// requested time rather than at the nearest keyframe.
/// </summary>
public interface ISmartCutEngine
{
    /// <summary>
    /// Cut <paramref name="inputPath"/> from <paramref name="start"/> to <paramref name="end"/> (null =
    /// end of file) into <paramref name="outputPath"/>, honouring <paramref name="start"/> exactly.
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
/// with no audio overlap. A source whose format <c>start_time</c> is not zero is a FALLBACK
/// (<see cref="SmartCutArgsBuilder.TryResolveEncoders"/>): its keyframe times and the head's <c>-ss</c> offsets would not
/// coincide.</para>
///
/// <para>Every intermediate lands in a temp dir that is swept in a <c>finally</c>, and the final file is
/// only moved into place once it exists — the same cancel-safety contract <see cref="SplitEngine"/>
/// holds. A source whose codecs cannot be reproduced is reported as a FALLBACK, never guessed at.</para>
/// </summary>
public sealed class SmartCutEngine : ISmartCutEngine
{
    private readonly IFfmpegRunner _runner;
    private readonly IMediaProbe _probe;

    public SmartCutEngine(IFfmpegRunner runner, IMediaProbe probe)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

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
        if (!SmartCutArgsBuilder.TryResolveEncoders(ok.Info, out var vEnc, out var aEnc, out var why))
        {
            return new SmartCutResult(null, plan.Strategy, FellBack: true, why, TimeSpan.Zero);
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

            progress?.Report(0.5);

            // --- 2. No copyable tail (request sits in the final GOP) -> the head IS the output. ---
            if (plan.Strategy == SmartCutStrategy.FullReencode)
            {
                MoveIntoPlace(headPath, outputPath);
                progress?.Report(1.0);
                return new SmartCutResult(outputPath, plan.Strategy, false, null, plan.ReencodedDuration);
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

            return new SmartCutResult(outputPath, plan.Strategy, false, null, plan.ReencodedDuration);
        }
        finally
        {
            TrySweep(tempDir);
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
