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
public sealed record StreamInfo(
    int Index,
    string CodecName,
    string Type,
    int? Width,
    int? Height,
    string? PixFmt,
    int? SampleRate,
    int? Channels,
    string? TimeBase)
{
    /// <summary>True when this stream's <see cref="Type"/> is <c>video</c>.</summary>
    public bool IsVideo => string.Equals(Type, "video", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this stream's <see cref="Type"/> is <c>audio</c>.</summary>
    public bool IsAudio => string.Equals(Type, "audio", StringComparison.OrdinalIgnoreCase);
}
