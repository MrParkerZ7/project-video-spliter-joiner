namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// The two frame-level questions the frame-exact cut asks of a source (T-190), kept apart from <see cref="IMediaProbe"/> so
/// that probe stays the narrow seam every screen and fake already implements. <see cref="MediaProbe"/> implements both; an
/// <see cref="IMediaProbe"/> that does not leaves <c>SmartCutEngine</c> without them (it then cuts at the requested time
/// exactly as before, and does not check the head it wrote), never throws.
/// </summary>
public interface IVideoFrameProbe
{
    /// <summary>
    /// The presentation time of the first video frame of <paramref name="path"/> at or after <paramref name="time"/> (a frame
    /// within 1 ms before it counts as at it, the rounding of a timestamp printed to six decimals), or null when the file
    /// reports none within the two seconds after <paramref name="time"/> or cannot be read. A bounded demux-level read
    /// (<c>ffprobe -read_intervals</c>, packet times only, no decoding), so it costs about one ffprobe start whatever the
    /// file's length.
    ///
    /// <para>Why the engine asks: ffmpeg's output <c>-ss</c> keeps the frames at or after the requested time and counts the
    /// head's timestamps from the REQUESTED time, but the head encoder puts its first frame on the 1/fps grid counted from
    /// there. With a Start between two source frames the head's frames sit up to one frame off the copied part's, and the
    /// joint is 33 ms early with a muxer timestamp line, or one frame is repeated. Starting the cut ON the first frame
    /// makes the two grids the same (SPEC-001 I43).</para>
    /// </summary>
    Task<TimeSpan?> FirstVideoFrameAtOrAfterAsync(string path, TimeSpan time, CancellationToken ct = default);

    /// <summary>
    /// The number of video packets in <paramref name="path"/> (0 for a file with no video stream), or null when it cannot be
    /// read. A demux-level count, no decoding; used on the re-encoded head to refuse a head that lost the source's video.
    /// </summary>
    Task<int?> CountVideoPacketsAsync(string path, CancellationToken ct = default);
}
