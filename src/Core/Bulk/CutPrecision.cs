namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// How exactly a batch honours the cut times the user set (T-125, epic G-042) — a third axis on
/// <see cref="BulkTrimOptions"/>, orthogonal to both collision policy and output destination.
/// </summary>
public enum CutPrecision
{
    /// <summary>
    /// Default. Cuts snap to the nearest keyframe so every byte is stream-copied: instant, no quality
    /// loss. The cost is that a cut can land up to half a GOP away from where it was set — which is
    /// correct and unavoidable for a pure copy, and is now shown in the row rather than hidden.
    /// </summary>
    Lossless,

    /// <summary>
    /// Honour the requested time EXACTLY by re-encoding only the fragment between the request and the
    /// next keyframe, then stream-copying the remainder (see <c>SmartCutEngine</c>). Roughly one GOP
    /// (~1-2s of video) is re-encoded per cut; the rest of the file is untouched bytes. A source whose
    /// codecs cannot be reproduced falls back to <see cref="Lossless"/> rather than risking a bad file.
    /// </summary>
    Exact,
}
