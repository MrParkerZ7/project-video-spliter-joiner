namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// A single media stream (video or audio) as reported by ffprobe. Carries every field the
/// later JOIN-compatibility check needs, so two clips can be compared for concat safety.
/// </summary>
/// <param name="Index">Zero-based stream index within the container.</param>
/// <param name="CodecName">ffprobe <c>codec_name</c> (e.g. <c>h264</c>, <c>aac</c>).</param>
/// <param name="Type">ffprobe <c>codec_type</c> (<c>video</c> or <c>audio</c>).</param>
/// <param name="Width">Video pixel width, or null for audio streams.</param>
/// <param name="Height">Video pixel height, or null for audio streams.</param>
/// <param name="PixFmt">Video pixel format (e.g. <c>yuv420p</c>), or null for audio.</param>
/// <param name="SampleRate">Audio sample rate in Hz, or null for video.</param>
/// <param name="Channels">Audio channel count, or null for video.</param>
/// <param name="TimeBase">Stream time base (e.g. <c>1/30</c>), or null if absent.</param>
/// <param name="AvgFrameRate">
/// ffprobe <c>avg_frame_rate</c> in frames per second (<c>30000/1001</c> → 29.97003), or null when absent,
/// unparsable or with a zero part (<c>0/0</c>, which ffprobe prints for audio). T-219.
/// </param>
/// <param name="RFrameRate">ffprobe <c>r_frame_rate</c> in frames per second, null on the same terms. T-219.</param>
/// <param name="StartTime">
/// ffprobe's per-stream <c>start_time</c> (the stream's first timestamp, in the file's own time base), or null when
/// absent or unparsable. It can differ from the format's <see cref="MediaInfo.StartTime"/>, which is the earliest
/// stream's: on a TS the video typically starts after the audio. The segment muxer counts its split times from the
/// first video packet, so the split engine converts keyframe times by the video stream's start (T-219).
/// </param>
public sealed record StreamInfo(
    int Index,
    string CodecName,
    string Type,
    int? Width,
    int? Height,
    string? PixFmt,
    int? SampleRate,
    int? Channels,
    string? TimeBase,
    double? AvgFrameRate = null,
    double? RFrameRate = null,
    TimeSpan? StartTime = null)
{
    /// <summary>
    /// The stream's frame rate in frames per second: <see cref="AvgFrameRate"/>, else <see cref="RFrameRate"/>; null
    /// means unknown. Never a Join compatibility field (<c>CompatChecker</c> compares named fields only).
    /// </summary>
    public double? FrameRate => AvgFrameRate ?? RFrameRate;

    /// <summary>True when this stream's <see cref="Type"/> is <c>video</c>.</summary>
    public bool IsVideo => string.Equals(Type, "video", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this stream's <see cref="Type"/> is <c>audio</c>.</summary>
    public bool IsAudio => string.Equals(Type, "audio", StringComparison.OrdinalIgnoreCase);
}
