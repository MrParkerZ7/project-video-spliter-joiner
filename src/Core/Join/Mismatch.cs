namespace VideoSplitJoiner.Core.Join;

/// <summary>
/// One reason a set of inputs is NOT concat-compatible: the field that differs plus a
/// human-readable detail naming the offending clip (1-based) and the conflicting values.
/// </summary>
/// <param name="Field">
/// The compared field, e.g. <c>resolution</c>, <c>codec</c>, <c>pix_fmt</c>,
/// <c>audio_sample_rate</c>, <c>time_base</c>, <c>audio_channels</c>, <c>probe</c>.
/// </param>
/// <param name="Detail">
/// Human text describing the mismatch, e.g. <c>clip 2 is 1280x720, reference (clip 1) is 1920x1080</c>.
/// </param>
public sealed record Mismatch(string Field, string Detail);
