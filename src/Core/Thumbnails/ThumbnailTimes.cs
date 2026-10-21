namespace VideoSplitJoiner.Core.Thumbnails;

/// <summary>
/// Time rules for an EXACT frame request that need the file's length (T-196 review, SPEC-005 I28).
///
/// <para>An exact seek to the time itself returns no frame at all when that time is at or past the last frame (ffmpeg exits with no
/// file): the floored grab it replaces landed a second earlier and always found one. A chip for an outro dragged to the very end, a
/// profile with no tail, and Use current frame on a finished video all ask for such a time, so a caller that knows the file's length
/// clamps with <see cref="ClampBeforeEnd"/>.</para>
/// </summary>
public static class ThumbnailTimes
{
    /// <summary>
    /// How far before the end of the file an exact request stops: the last frame of any video worth cutting (up to 10 fps) is at least
    /// this close to the end, and a frame this far back is the picture the user sees as "the end".
    /// </summary>
    public static readonly TimeSpan EndGuard = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// <paramref name="seek"/> as it is, or <c>duration - EndGuard</c> when it lies past that (and the file is longer than the guard).
    /// A null duration (not known yet) leaves it alone.
    /// </summary>
    public static TimeSpan ClampBeforeEnd(TimeSpan seek, TimeSpan? duration) =>
        duration is { } d && d > EndGuard && seek > d - EndGuard ? d - EndGuard : seek;
}
