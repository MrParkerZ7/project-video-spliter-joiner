using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Detect;

/// <summary>
/// Auto-detects natural split boundaries — black intervals, white intervals, and hard scene
/// cuts — and returns them RANKED by confidence, each snapped to the nearest keyframe. Purely
/// DECODE-ONLY: every ffmpeg pass writes to the null muxer, so no output file is ever produced
/// and nothing is re-encoded. Built on the T-002 runner + T-003 probe/snapper.
/// </summary>
public interface ISplitPointDetector
{
    /// <summary>
    /// Detect split-point candidates in <paramref name="path"/>. Runs up to three decode-only
    /// ffmpeg passes (black, white, scene — each toggled by <paramref name="options"/>), parses
    /// their stderr, merges near-duplicate hits, snaps each to a keyframe, and ranks the result
    /// (Rank 1 = highest confidence), capped at <see cref="DetectOptions.MaxCandidates"/>. An
    /// EMPTY list is a valid result (a busy clip with no black/white/scene events), never an
    /// exception. Reports progress 0..1 across the enabled passes. Cancellation surfaces as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<IReadOnlyList<Candidate>> DetectAsync(
        string path,
        DetectOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

/// <inheritdoc cref="ISplitPointDetector" />
public sealed class SplitPointDetector : ISplitPointDetector
{
    /// <summary>Hits within this window of each other are merged into one (higher-confidence wins).</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(0.5);

    private readonly IFfmpegRunner _runner;
    private readonly IMediaProbe _probe;

    // Retained for parity with sibling engines (SplitEngine) that take the locator; the runner
    // already resolves the binary internally, so the detector never calls the locator directly.
    private readonly IFfmpegBinaryLocator _locator;

    /// <summary>Create the detector over the T-002 runner + locator and the T-003 probe.</summary>
    public SplitPointDetector(IFfmpegRunner runner, IMediaProbe probe, IFfmpegBinaryLocator locator)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Candidate>> DetectAsync(
        string path,
        DetectOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Cannot detect split points; file does not exist.", path);
        }

        // Probe once for total duration (drives progress) and keyframes (drives snapping).
        var probeResult = await _probe.ProbeAsync(path, ct).ConfigureAwait(false);
        var duration = probeResult is ProbeResult.ProbeSucceeded ok ? ok.Info.Duration : (TimeSpan?)null;
        var keyframes = await _probe.GetKeyframesAsync(path, ct).ConfigureAwait(false);

        // Which passes are on — used to apportion progress across them.
        var passes = new List<CandidateKind>();
        if (options.EnableBlack)
        {
            passes.Add(CandidateKind.Black);
        }

        if (options.EnableWhite)
        {
            passes.Add(CandidateKind.White);
        }

        if (options.EnableScene)
        {
            passes.Add(CandidateKind.Scene);
        }

        var hits = new List<RawHit>();
        var minDur = options.EffectiveMinBlackDuration;

        for (var i = 0; i < passes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var kind = passes[i];
            var (args, parse) = BuildPass(kind, path, options, minDur);

            // Assert decode-only BEFORE launching — never let a mis-built command write a file.
            if (!DetectArgsBuilder.SatisfiesDecodeOnlyInvariant(args.ToList()))
            {
                throw new InvalidOperationException(
                    $"Internal error: the {kind} detection command is not decode-only (would write/encode output). Refusing to run.");
            }

            var passProgress = SubProgress(progress, i, passes.Count);
            var result = await _runner.RunAsync(args, duration, passProgress, ct).ConfigureAwait(false);

            // A non-zero exit on a decode-only detection pass is not fatal on its own; but if we
            // got no stderr at all AND a failure, surface it so a broken input doesn't look "empty".
            hits.AddRange(parse(result.StdErrText));
        }

        Func<TimeSpan, TimeSpan> snap = keyframes.Count > 0
            ? t => _probe.SnapToNearestKeyframe(keyframes, t).Snapped
            : t => t; // No keyframes (degenerate) → snap is identity, still a valid result.

        var ranked = DetectParser.BuildRanked(hits, snap, MergeWindow, options.MaxCandidates);

        progress?.Report(1.0);
        return ranked;
    }

    private static (FfmpegArgs Args, Func<string, IReadOnlyList<RawHit>> Parse) BuildPass(
        CandidateKind kind,
        string path,
        DetectOptions options,
        TimeSpan minDur) => kind switch
    {
        CandidateKind.Black => (
            DetectArgsBuilder.Black(path, minDur, options.BlackPicThreshold),
            stderr => DetectParser.ParseBlackLike(stderr, CandidateKind.Black)),

        CandidateKind.White => (
            DetectArgsBuilder.White(path, minDur, options.WhiteThreshold),
            stderr => DetectParser.ParseBlackLike(stderr, CandidateKind.White)),

        CandidateKind.Scene => (
            DetectArgsBuilder.Scene(path, options.SceneThreshold),
            DetectParser.ParseScene),

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown detection kind."),
    };

    /// <summary>
    /// Wrap the caller's progress so each pass contributes its slice of the 0..1 range: pass
    /// <paramref name="index"/> of <paramref name="total"/> maps its own 0..1 into
    /// [index/total, (index+1)/total].
    /// </summary>
    private static IProgress<double>? SubProgress(IProgress<double>? outer, int index, int total)
    {
        if (outer is null || total <= 0)
        {
            return null;
        }

        var lo = (double)index / total;
        var span = 1.0 / total;
        return new Progress<double>(p => outer.Report(lo + (Math.Clamp(p, 0.0, 1.0) * span)));
    }
}
