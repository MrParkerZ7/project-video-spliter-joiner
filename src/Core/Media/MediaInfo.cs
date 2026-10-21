namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// The probed shape of a media file: its overall duration, container format, and the
/// video/audio streams it holds.
/// </summary>
/// <param name="Duration">Total media duration.</param>
/// <param name="Container">ffprobe <c>format_name</c> (e.g. <c>mov,mp4,m4a,3gp,3g2,mj2</c>).</param>
/// <param name="VideoStreams">All video streams, in container order.</param>
/// <param name="AudioStreams">All audio streams, in container order.</param>
/// <param name="StartTime">
/// ffprobe <c>format.start_time</c>: the earliest stream's first timestamp, zero when absent or unparsable (T-219).
/// Keyframe times are source timestamps, but ffmpeg's input <c>-ss</c> is an offset from this value, so a lossless
/// cut seeks to <c>max(0, keyframe − StartTime)</c>. About 1.4 s on an MPEG-TS; 0 on an ordinary MP4. It may be
/// negative (an MP4 whose edit list starts before 0).
/// </param>
/// <param name="ExtraStreams">
/// T-224: how many streams the file holds besides real video and audio: subtitle, data (timecode, camera telemetry) and
/// attachment streams, and a video stream that is a cover picture (<c>disposition.attached_pic</c>). They are still listed in
/// <see cref="VideoStreams"/> when they are a cover picture. Zero for a file of picture and sound only, and for a
/// <see cref="MediaInfo"/> built without the count.
/// </param>
public sealed record MediaInfo(
    TimeSpan Duration,
    string Container,
    IReadOnlyList<StreamInfo> VideoStreams,
    IReadOnlyList<StreamInfo> AudioStreams,
    TimeSpan StartTime = default,
    int ExtraStreams = 0)
{
    /// <summary>True when the file has at least one video stream.</summary>
    public bool HasVideo => VideoStreams.Count > 0;

    /// <summary>True when the file has at least one audio stream.</summary>
    public bool HasAudio => AudioStreams.Count > 0;

    /// <summary>
    /// A source time (a keyframe's pts, the base of the probe's keyframe list) as an ffmpeg input <c>-ss</c> offset (T-196, SPEC-004
    /// I47): <c>max(0, sourceTime - StartTime)</c>. ffmpeg counts <c>-ss</c> from the format <c>start_time</c>, so on an MPEG-TS
    /// (about 1.4 s) a grab at the keyframe's own time lands that much late. The same conversion the lossless per-segment cut makes
    /// (T-219). Zero for a time below the start.
    /// </summary>
    public TimeSpan ToSeekOffset(TimeSpan sourceTime)
    {
        var offset = sourceTime - StartTime;
        return offset > TimeSpan.Zero ? offset : TimeSpan.Zero;
    }
}
