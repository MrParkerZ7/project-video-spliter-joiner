namespace VideoSplitJoiner.Core.Detect;

/// <summary>
/// Tunables for <see cref="ISplitPointDetector.DetectAsync"/>. Defaults match the ticket:
/// all three detectors on, a 0.1s minimum black/white interval, and mid-range thresholds.
/// </summary>
/// <param name="EnableBlack">Run the black-interval pass.</param>
/// <param name="EnableWhite">Run the white-interval pass (negate + blackdetect).</param>
/// <param name="EnableScene">Run the hard-scene-cut pass.</param>
/// <param name="MinBlackDuration">
/// blackdetect <c>d=</c>: minimum duration of a black (or, on the negated stream, white)
/// interval to report. Applies to BOTH black and white passes.
/// </param>
/// <param name="BlackPicThreshold">blackdetect <c>pic_th=</c>: fraction of pixels below the
/// per-pixel black threshold for a frame to count as black. Reused for the white pass.</param>
/// <param name="WhiteThreshold">Luma fraction near max for a frame to count as white — maps to
/// the <c>pic_th</c> of the negated-stream blackdetect pass.</param>
/// <param name="SceneThreshold">scene score (0..1) above which a frame is a hard cut.</param>
/// <param name="MaxCandidates">Cap on the returned, ranked candidate list.</param>
public sealed record DetectOptions(
    bool EnableBlack = true,
    bool EnableWhite = true,
    bool EnableScene = true,
    TimeSpan MinBlackDuration = default,
    double BlackPicThreshold = 0.98,
    double WhiteThreshold = 0.98,
    double SceneThreshold = 0.4,
    int MaxCandidates = 50)
{
    /// <summary>The default minimum black/white interval when <see cref="MinBlackDuration"/> is unset.</summary>
    public static readonly TimeSpan DefaultMinBlackDuration = TimeSpan.FromSeconds(0.1);

    /// <summary>
    /// <see cref="MinBlackDuration"/> with the 0.1s default applied — a record's <c>default</c>
    /// <see cref="TimeSpan"/> is <see cref="TimeSpan.Zero"/>, which we treat as "use the default".
    /// </summary>
    public TimeSpan EffectiveMinBlackDuration =>
        MinBlackDuration <= TimeSpan.Zero ? DefaultMinBlackDuration : MinBlackDuration;
}
