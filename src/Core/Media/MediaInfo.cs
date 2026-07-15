namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// The probed shape of a media file: its overall duration, container format, and the
/// video/audio streams it holds.
/// </summary>
/// <param name="Duration">Total media duration.</param>
/// <param name="Container">ffprobe <c>format_name</c> (e.g. <c>mov,mp4,m4a,3gp,3g2,mj2</c>).</param>
/// <param name="VideoStreams">All video streams, in container order.</param>
/// <param name="AudioStreams">All audio streams, in container order.</param>
public sealed record MediaInfo(
    TimeSpan Duration,
    string Container,
    IReadOnlyList<StreamInfo> VideoStreams,
    IReadOnlyList<StreamInfo> AudioStreams)
{
    /// <summary>True when the file has at least one video stream.</summary>
    public bool HasVideo => VideoStreams.Count > 0;

    /// <summary>True when the file has at least one audio stream.</summary>
    public bool HasAudio => AudioStreams.Count > 0;
}
