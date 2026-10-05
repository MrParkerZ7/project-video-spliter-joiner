using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// The production <see cref="IBulkTrimRequestBuilder"/>: probes each source for duration + keyframes
/// and delegates the kept-middle index math to T-094's <see cref="KeptSegmentSelector.ResolveKeptIndex"/>
/// (the index-not-always-2 cure), then assembles the single-kept-segment
/// <see cref="SplitRequest"/> that writes to the runner's collision-resolved output path.
///
/// <para>Shares the App's <see cref="IMediaProbe"/> instance: the builder's probe + the engine's
/// re-probe both hit MediaProbe's <c>(path,mtime,length)</c> caches, so the heavy keyframe scan runs
/// once per file (T-093, with in-flight dedup) and so does the metadata probe (T-193) — a row probed
/// when it was added runs no further ffprobe for it here or in the engine.</para>
/// </summary>
public sealed class KeptMiddleRequestBuilder : IBulkTrimRequestBuilder
{
    private readonly IMediaProbe _probe;

    /// <summary>Create the builder over the shared media probe.</summary>
    public KeptMiddleRequestBuilder(IMediaProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <inheritdoc />
    public async Task<SplitRequest> BuildAsync(
        BulkTrimItem item,
        string effectiveOutputPath,
        bool overwrite,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveOutputPath);

        // Probe for duration + keyframes. Both are cached by MediaProbe per file version (the metadata
        // probe since T-193; the keyframes also de-duplicated in flight), so for a row probed on add, and for
        // the engine's later re-probe of the same file, no ffprobe runs. A probe failure is never cached and
        // is a genuine error → SplitException → the runner records this row Failed.
        var probeResult = await _probe.ProbeAsync(item.InputPath, ct).ConfigureAwait(false);
        if (probeResult is not ProbeResult.ProbeSucceeded ok)
        {
            var reason = probeResult is ProbeResult.ProbeFailed f ? f.Reason : "unknown probe error";
            throw new SplitException($"Cannot trim '{item.InputPath}': {reason}");
        }

        var duration = ok.Info.Duration;
        var keyframes = await _probe.GetKeyframesAsync(item.InputPath, ct).ConfigureAwait(false);
        var averageGop = _probe.AverageGop(keyframes);

        // Delegate the kept-middle index to T-094. It throws SplitException when BOTH boundaries
        // collapse (no cut survives) — that is a NO-OP trim, so translate it into the distinct
        // NoOpTrimException the runner maps to Skipped (never Failed).
        int keptIndex;
        try
        {
            keptIndex = KeptSegmentSelector.ResolveKeptIndex(
                duration,
                keyframes,
                _probe.SnapToNearestKeyframe,
                averageGop,
                item.IntroEnd,
                item.OutroStart);
        }
        catch (SplitException ex)
        {
            throw new NoOpTrimException(
                $"'{item.InputPath}' resolves to a no-op trim — nothing would be removed.", ex);
        }

        // Kept-middle cut points: [introEnd] (keep to EOF) or [introEnd, outroStart].
        IReadOnlyList<TimeSpan> cutPoints = item.OutroStart is { } outro
            ? new[] { item.IntroEnd, outro }
            : new[] { item.IntroEnd };

        // Honor the runner's collision-resolved path: OutputDir = its folder, NamingPattern = the
        // literal file name (no {index} token, so SplitEngine.ApplyNamingPattern returns it verbatim
        // and the single selected segment lands exactly there).
        var fullOutput = Path.GetFullPath(effectiveOutputPath);
        var outputDir = Path.GetDirectoryName(fullOutput)
            ?? throw new SplitException($"Cannot resolve an output directory for '{effectiveOutputPath}'.");
        var namingPattern = Path.GetFileName(fullOutput);

        return new SplitRequest(
            InputPath: item.InputPath,
            CutPoints: cutPoints,
            OutputDir: outputDir,
            NamingPattern: namingPattern,
            Overwrite: overwrite,
            SelectedSegmentIndices: new[] { keptIndex });
    }
}
