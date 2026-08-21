namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// One row of a Bulk Cut batch (D-004): a single source file to trim by keeping exactly the
/// middle segment <c>[IntroEnd → OutroStart | EOF]</c>. A bulk trim IS a
/// <see cref="Split.SplitEngine"/> that keeps one segment, so every item becomes one
/// <see cref="Split.ISplitEngine.SplitAsync"/> call — no second ffmpeg path.
/// </summary>
/// <param name="InputPath">Absolute path to the source media file (never written to — the source is never a target).</param>
/// <param name="IntroEnd">Requested intro-end — the start of the kept middle segment.</param>
/// <param name="OutroStart">Optional requested outro-start; <c>null</c> ⇒ keep runs to end of file.</param>
/// <param name="DesiredOutputPath">
/// The base output path computed upstream (typically <c>&lt;dir&gt;/&lt;name&gt;_trimmed&lt;ext&gt;</c>).
/// The runner resolves the EFFECTIVE path from this per the active <see cref="CollisionPolicy"/>.
/// </param>
/// <param name="Tag">
/// Opaque correlation handle for the caller (the App VM correlates a <see cref="BulkTrimItemResult"/>
/// back to its row through this). Core never reads it — pass-through only.
/// </param>
public sealed record BulkTrimItem(
    string InputPath,
    TimeSpan IntroEnd,
    TimeSpan? OutroStart,
    string DesiredOutputPath,
    object? Tag = null);
