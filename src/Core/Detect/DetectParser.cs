using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoSplitJoiner.Core.Detect;

/// <summary>
/// A raw, pre-snap, pre-rank detection hit parsed from ffmpeg stderr: an event time, its kind,
/// and a raw score (blackdetect/white have no native score, so duration is used as the raw
/// strength; scene uses the scene_score directly).
/// </summary>
/// <param name="Time">Event time in the source.</param>
/// <param name="Kind">Black / White / Scene.</param>
/// <param name="RawScore">Kind-specific strength — interval seconds for black/white, scene_score for scene.</param>
public readonly record struct RawHit(TimeSpan Time, CandidateKind Kind, double RawScore);

/// <summary>
/// PURE stderr → candidate parsing, merging, and ranking. No I/O, no ffmpeg — every method here
/// is a deterministic function of its inputs, so the whole detection pipeline (parse, dedupe,
/// rank) is unit-testable against captured stderr strings without a binary.
/// </summary>
public static class DetectParser
{
    // "[blackdetect @ 0x..] black_start:2 black_end:3.5 black_duration:1.5"
    private static readonly Regex BlackLine = new(
        @"black_start\s*:\s*(?<start>[-+0-9.eE]+).*?black_duration\s*:\s*(?<dur>[-+0-9.eE]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "[blackdetect @ ..] black_start:5.5 black_end:7 black_duration:1.5" (no black_end/dur fallback)
    private static readonly Regex BlackStartOnly = new(
        @"black_start\s*:\s*(?<start>[-+0-9.eE]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "... pts_time:2"  (from metadata=print)
    private static readonly Regex PtsTimeLine = new(
        @"pts_time\s*:\s*(?<t>[-+0-9.eE]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "... lavfi.scene_score=1.000000"  (from metadata=print)
    private static readonly Regex SceneScoreLine = new(
        @"lavfi\.scene_score\s*=\s*(?<s>[-+0-9.eE]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parse blackdetect stderr into black (or white, per <paramref name="kind"/>) hits, one per
    /// interval, at the interval START. The interval duration becomes the raw score.
    /// </summary>
    public static IReadOnlyList<RawHit> ParseBlackLike(string stderr, CandidateKind kind)
    {
        var hits = new List<RawHit>();
        if (string.IsNullOrEmpty(stderr))
        {
            return hits;
        }

        foreach (var line in SplitLines(stderr))
        {
            if (!line.Contains("black_start", StringComparison.Ordinal))
            {
                continue;
            }

            var m = BlackLine.Match(line);
            if (m.Success
                && TryParse(m.Groups["start"].Value, out var start)
                && TryParse(m.Groups["dur"].Value, out var dur))
            {
                hits.Add(new RawHit(TimeSpan.FromSeconds(start), kind, dur));
                continue;
            }

            var m2 = BlackStartOnly.Match(line);
            if (m2.Success && TryParse(m2.Groups["start"].Value, out var s2))
            {
                hits.Add(new RawHit(TimeSpan.FromSeconds(s2), kind, 0.0));
            }
        }

        return hits;
    }

    /// <summary>
    /// Parse <c>select=gt(scene,…),metadata=print</c> stderr into scene hits. The filter prints a
    /// <c>pts_time:</c> line followed by a <c>lavfi.scene_score=</c> line per selected frame; this
    /// pairs them statefully. A pts_time with no following score defaults to a 1.0 raw score
    /// (it passed the gt() threshold, so it is a genuine cut).
    /// </summary>
    public static IReadOnlyList<RawHit> ParseScene(string stderr)
    {
        var hits = new List<RawHit>();
        if (string.IsNullOrEmpty(stderr))
        {
            return hits;
        }

        TimeSpan? pending = null;
        foreach (var line in SplitLines(stderr))
        {
            var scoreMatch = SceneScoreLine.Match(line);
            if (scoreMatch.Success)
            {
                if (pending is { } t && TryParse(scoreMatch.Groups["s"].Value, out var score))
                {
                    hits.Add(new RawHit(t, CandidateKind.Scene, score));
                    pending = null;
                }

                continue;
            }

            var ptsMatch = PtsTimeLine.Match(line);
            if (ptsMatch.Success && TryParse(ptsMatch.Groups["t"].Value, out var ts))
            {
                // A previous pts_time with no score line still counts as a cut (default score 1.0).
                if (pending is { } prev)
                {
                    hits.Add(new RawHit(prev, CandidateKind.Scene, 1.0));
                }

                pending = TimeSpan.FromSeconds(ts);
            }
        }

        if (pending is { } last)
        {
            hits.Add(new RawHit(last, CandidateKind.Scene, 1.0));
        }

        return hits;
    }

    /// <summary>
    /// Merge hits within <paramref name="mergeWindow"/> of each other into a single hit, keeping
    /// the higher-confidence one. Confidence order for merging: a black/white fade beats a bare
    /// scene hit at equal normalized strength (they are stronger natural boundaries), and within a
    /// kind the larger raw score wins. Input order independent (sorted by time first).
    /// </summary>
    public static IReadOnlyList<RawHit> Merge(IReadOnlyList<RawHit> hits, TimeSpan mergeWindow)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count <= 1)
        {
            return hits.ToList();
        }

        var ordered = hits.OrderBy(h => h.Time).ToList();
        var merged = new List<RawHit> { ordered[0] };

        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var last = merged[^1];

            if (current.Time - last.Time <= mergeWindow)
            {
                // Within the window — keep whichever is the stronger boundary.
                merged[^1] = PreferStronger(last, current);
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    /// <summary>
    /// Normalize each kind's raw scores to 0..1 (per-kind max), apply a small kind weight so
    /// black/white fades outrank a bare scene hit unless the scene score is very high, then order
    /// descending by the resulting combined confidence and assign 1-based ranks (1 = best),
    /// capping at <paramref name="maxCandidates"/>. <paramref name="snap"/> maps each hit's time
    /// to its keyframe-snapped time. PURE — no ffmpeg, deterministic.
    /// </summary>
    public static IReadOnlyList<Candidate> Rank(
        IReadOnlyList<RawHit> hits,
        Func<TimeSpan, TimeSpan> snap,
        int maxCandidates)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(snap);

        if (hits.Count == 0)
        {
            return Array.Empty<Candidate>();
        }

        // Per-kind max raw score for normalization.
        var maxByKind = new Dictionary<CandidateKind, double>();
        foreach (var h in hits)
        {
            var abs = Math.Abs(h.RawScore);
            if (!maxByKind.TryGetValue(h.Kind, out var cur) || abs > cur)
            {
                maxByKind[h.Kind] = abs;
            }
        }

        var scored = hits
            .Select(h =>
            {
                var max = maxByKind[h.Kind];
                var normalized = max > 0 ? Math.Clamp(Math.Abs(h.RawScore) / max, 0.0, 1.0) : 1.0;
                var combined = normalized * KindWeight(h.Kind);
                return (Hit: h, Score: normalized, Combined: combined);
            })
            // Order by combined confidence desc, then by time asc for a stable tie-break.
            .OrderByDescending(x => x.Combined)
            .ThenBy(x => x.Hit.Time)
            .Take(Math.Max(0, maxCandidates))
            .ToList();

        var result = new List<Candidate>(scored.Count);
        for (var i = 0; i < scored.Count; i++)
        {
            var (hit, score, _) = scored[i];
            result.Add(new Candidate(
                Time: hit.Time,
                SnappedTime: snap(hit.Time),
                Kind: hit.Kind,
                Score: Math.Round(score, 6),
                Rank: i + 1));
        }

        return result;
    }

    /// <summary>The full pure pipeline: merge → rank. Used by the detector and directly in tests.</summary>
    public static IReadOnlyList<Candidate> BuildRanked(
        IReadOnlyList<RawHit> hits,
        Func<TimeSpan, TimeSpan> snap,
        TimeSpan mergeWindow,
        int maxCandidates)
    {
        var merged = Merge(hits, mergeWindow);
        return Rank(merged, snap, maxCandidates);
    }

    // Fades are stronger, more deliberate boundaries than a bare scene cut, so weight them up;
    // a very-high scene score can still outrank a weak fade because normalization runs first.
    private static double KindWeight(CandidateKind kind) => kind switch
    {
        CandidateKind.Black => 1.0,
        CandidateKind.White => 1.0,
        CandidateKind.Scene => 0.9,
        _ => 0.9,
    };

    private static RawHit PreferStronger(RawHit a, RawHit b)
    {
        // Fade (black/white) beats scene; otherwise larger raw score wins.
        var aFade = a.Kind is CandidateKind.Black or CandidateKind.White;
        var bFade = b.Kind is CandidateKind.Black or CandidateKind.White;

        if (aFade != bFade)
        {
            return aFade ? a : b;
        }

        return Math.Abs(b.RawScore) > Math.Abs(a.RawScore) ? b : a;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

    private static bool TryParse(string raw, out double value) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && !double.IsNaN(value)
        && !double.IsInfinity(value);
}
