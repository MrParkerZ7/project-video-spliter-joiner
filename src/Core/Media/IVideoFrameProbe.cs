namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// The two frame-level questions the frame-exact cut asks of a source and of the head it re-encodes (T-190, T-191), kept apart
/// from <see cref="IMediaProbe"/> so that probe stays the narrow seam every screen and fake already implements.
/// <see cref="MediaProbe"/> implements both; an <see cref="IMediaProbe"/> that does not leaves <c>SmartCutEngine</c> without them
/// (it then cuts at the requested time exactly as before, and does not check the head it wrote), never throws.
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
    /// The presentation times of the video frames of <paramref name="path"/> that lie in <c>[from, to)</c>, ascending (a frame
    /// within 1 ms before <paramref name="from"/> counts as at it, one within 1 ms before <paramref name="to"/> as at it, so is
    /// out): an empty list for a file with no video stream or no frame in the window, and null when the answer cannot be given
    /// (the frame times of a packet cannot be told, as for <see cref="FirstVideoFrameAtOrAfterAsync"/>, or the file cannot be
    /// read). A demux-level read like the first, whose cost is the GOP before <paramref name="from"/> and the window
    /// to two seconds past <paramref name="to"/>: that last stretch is what puts the leading B-frames of an OPEN GOP in the answer,
    /// because the key frame that ends the window is decoded before the frames displayed ahead of it.
    ///
    /// <para>Why the engine asks (T-191): the count of the frames a head HOLDS against the count of the source's frames in the
    /// span it was cut from, and the time of its first frame, are the end-to-end check that a head is the frames it should be.
    /// The input seek lands on the key frame by its decoding time; where that key frame has leading pictures that cannot be
    /// decoded without the GOP before it (open GOP, MPEG-4 part 2 with B-frames) the head loses frames at its start or all of
    /// them. A head that does not match is a fall back, never a Done.</para>
    /// </summary>
    Task<IReadOnlyList<TimeSpan>?> GetVideoFrameTimesAsync(string path, TimeSpan from, TimeSpan to, CancellationToken ct = default);

    /// <summary>
    /// Whether the H.264 keyframe of <paramref name="path"/> at <paramref name="keyframeTime"/> is an IDR picture: true for an
    /// IDR, false for a non-IDR I-frame that ffmpeg's parser flags as a keyframe (an OPEN-GOP keyframe: the pictures after it may
    /// refer to the ones before it), and null when the answer cannot be given - the file cannot be read, the packet at that time
    /// is not a keyframe, or its bytes cannot be read as H.264 NAL units (not H.264, an unusual framing, a unit cut off). A
    /// demux-level read of ONE packet (<c>ffprobe -read_intervals T%+#1 -show_data</c>): one ffprobe start and the keyframe's
    /// bytes, bounded by the picture, not the GOP or the file.
    ///
    /// <para>Why the engine asks (T-223, SPEC-001 I70): the part an Exact cut copies starts at <c>HeadEnd</c>. Behind an IDR it is
    /// self-contained, with the SPS/PPS <c>h264_mp4toannexb</c> puts before it. Behind a non-IDR keyframe its frames refer to the
    /// GOP before it and the SPS/PPS are the head's, and the final printed decoder errors and lost frames at the joint with
    /// ffmpeg's exit code 0. A keyframe that is not provably an IDR is a fall back to the lossless cut, never a Done.</para>
    /// </summary>
    Task<bool?> IsIdrKeyframeAsync(string path, TimeSpan keyframeTime, CancellationToken ct = default);
}
