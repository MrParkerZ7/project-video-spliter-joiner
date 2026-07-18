namespace VideoSplitJoiner.Core.Thumbnails;

/// <summary>
/// UI-free service that extracts a single video frame at a given time to a temp image file (via the
/// ffmpeg CLI) for a scrub-bar hover preview. Best-effort throughout: <see cref="GetThumbnailAsync"/>
/// returns the temp file PATH on success and <c>null</c> on ANY failure — it never throws (a preview
/// that can't be produced simply shows nothing).
/// <para>
/// Core stays UI-free: this returns a filesystem PATH (string), never an <c>ImageSource</c>. The App
/// layer loads the returned path into a frozen <c>BitmapImage</c> itself (T-078).
/// </para>
/// </summary>
public interface IThumbnailService
{
    /// <summary>
    /// Extract one frame of <paramref name="inputPath"/> at (or near) <paramref name="time"/>, scaled to
    /// <paramref name="width"/> px wide (height auto), and return the temp jpg path — or <c>null</c> on
    /// any failure (missing input, ffmpeg error, cancellation, I/O). Never throws.
    /// <para>
    /// Fast (keyframe-accurate) seek: <c>-ss</c> is placed BEFORE <c>-i</c>. Requests are cached by a
    /// configurable time bucket, so repeat hovers within the same bucket reuse the file WITHOUT
    /// re-running ffmpeg. Honors <paramref name="ct"/> — a superseded request can be cancelled and never
    /// clobbers a newer result.
    /// </para>
    /// </summary>
    /// <param name="inputPath">The source media file.</param>
    /// <param name="time">The hovered time; floored to the bucket granularity for caching.</param>
    /// <param name="width">Target thumbnail width in px (height scaled to keep aspect).</param>
    /// <param name="ct">Cancellation token for superseded requests.</param>
    /// <returns>The temp jpg path, or <c>null</c> on any failure.</returns>
    Task<string?> GetThumbnailAsync(string inputPath, TimeSpan time, int width, CancellationToken ct);

    /// <summary>Best-effort delete of the cache dir for one input file (missing dir = no-op, never throws).</summary>
    void Clear(string inputPath);

    /// <summary>Best-effort delete of the whole thumbnail cache root (missing dir = no-op, never throws).</summary>
    void ClearAll();
}
