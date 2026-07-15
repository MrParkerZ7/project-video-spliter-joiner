namespace VideoSplitJoiner.Core.Join;

/// <summary>
/// A request to glue several media files, in the given order, into one output via lossless
/// stream-copy concat (<c>-c copy</c>). The inputs must be concat-compatible (same codecs,
/// resolution, pixel format, audio layout) — the engine runs a pre-flight check and REFUSES
/// rather than emitting a broken file when they are not.
/// </summary>
/// <param name="InputPaths">
/// The clips to join, in play order. Order is significant — the output plays them head-to-tail
/// exactly as listed.
/// </param>
/// <param name="OutputPath">Destination path for the joined file (its parent dir is created if absent).</param>
/// <param name="Overwrite">
/// When false (default), a join that would clobber an existing output file is rejected before
/// any ffmpeg runs. When true, an existing file at <see cref="OutputPath"/> is replaced.
/// </param>
public sealed record JoinRequest(
    IReadOnlyList<string> InputPaths,
    string OutputPath,
    bool Overwrite = false);
