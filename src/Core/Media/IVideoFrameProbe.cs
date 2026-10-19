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
    /// reports none within the two seconds after <paramref name="time"/>, when it cannot say which packets are which frames
    /// (below), or when it cannot be read. A demux-level read (<c>ffprobe -read_intervals</c>, packet times only, no
    /// decoding) whose cost is one ffprobe start plus the packets from the keyframe BEFORE <paramref name="time"/> to two
    /// seconds after it: one GOP and the window, not the whole file (measured about 0.15-0.2 s and 18.5 MB on the bench's
    /// 2-minute 4K fixture with a 2 s GOP). It grows with the GOP: ffprobe seeks back to the previous keyframe, so a source
    /// with few keyframes reads its prefix up to <paramref name="time"/>.
    ///
    /// <para>A packet without a presentation time (an AVI with B-frames lists about a third of its video packets so) counts
    /// by its decoding time, the same fallback the keyframe scan uses, but only while the stream shows no re-ordering: when
    /// some packet carries both times and they differ, a packet without a presentation time has no frame time to give, and
    /// the answer is null rather than a decode time passed off as a display time.</para>
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
