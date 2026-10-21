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
    /// Fast (keyframe-accurate) seek: <c>-ss</c> is placed BEFORE <c>-i</c>. Requests are cached by
    /// (input, time bucket, width), so a repeat request for the same bucket AT THE SAME WIDTH reuses the
    /// file WITHOUT re-running ffmpeg; the same bucket at another width is a separate file (T-179 — a
    /// caller asking for 640px must never be handed another caller's 64px frame). Honors
    /// <paramref name="ct"/> — a superseded request can be cancelled and never clobbers a newer result.
    /// </para>
    /// </summary>
    /// <param name="inputPath">The source media file.</param>
    /// <param name="time">The hovered time; floored to the bucket granularity for caching.</param>
    /// <param name="width">Target thumbnail width in px (height scaled to keep aspect).</param>
    /// <param name="ct">Cancellation token for superseded requests.</param>
    /// <returns>The temp jpg path, or <c>null</c> on any failure.</returns>
    Task<string?> GetThumbnailAsync(string inputPath, TimeSpan time, int width, CancellationToken ct);

    /// <summary>
    /// Extract the frame of <paramref name="inputPath"/> at EXACTLY <paramref name="time"/> (T-196, SPEC-005 I28-I32), scaled to
    /// <paramref name="width"/> px wide, and return the temp jpg path - or <c>null</c> on any failure. Never throws.
    /// <para>
    /// For a CUT-POINT or profile picture, where the bucketed request is wrong twice over: it shows a frame up to a second before the
    /// cut, and its floored <c>-ss</c> lands before the cut's keyframe so ffmpeg decodes most of a GOP (9-11 times the CPU of an
    /// exact seek to the keyframe, measured). The seek is the time itself at six decimals, never rounded to milliseconds. A time of
    /// zero or less is zero. Its cache key and file name are <c>x&lt;µs&gt;_w&lt;width&gt;</c>, which can never equal a bucket's, so an
    /// exact request and a hover never serve each other.
    /// </para>
    /// <para>
    /// <paramref name="time"/> is an ffmpeg SEEK OFFSET, counted from the format <c>start_time</c>; the service passes it through
    /// unchanged. A caller holding a keyframe time (a source pts) passes <see cref="Media.MediaInfo.ToSeekOffset"/> of it.
    /// </para>
    /// </summary>
    Task<string?> GetExactThumbnailAsync(string inputPath, TimeSpan time, int width, CancellationToken ct);

    /// <summary>Best-effort delete of the cache dir for one input file (missing dir = no-op, never throws).</summary>
    void Clear(string inputPath);

    /// <summary>Best-effort delete of the whole thumbnail cache root (missing dir = no-op, never throws).</summary>
    void ClearAll();
}
