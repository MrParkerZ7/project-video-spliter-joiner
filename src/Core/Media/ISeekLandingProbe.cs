namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// Where an input seek aimed at a time really lands (T-237, SPEC-001 I75). ffmpeg's <c>-ss</c> before <c>-i</c> and the concat
/// demuxer's <c>inpoint</c> both seek with <c>avformat_seek_file(…, aim, aim)</c>, which lands on the last keyframe whose INDEX time
/// is not after the aim. The index time is the file's own decoding time, not the presentation time the keyframe list holds, and in
/// an MP4 or QuickTime file with B-frames and irregular frame times the two can differ by anything from nothing to the whole of a
/// quiet stretch, so an aim at a keyframe's presentation time can land on the keyframe before it, and no rule on times alone says
/// when. ffprobe's <c>-read_intervals</c> makes the same call, so asking it is asking the demuxer (the two agreed on 72 of 72 aims
/// across a variable-frame-rate file with idle stretches).
/// </summary>
public interface ISeekLandingProbe
{
    /// <summary>
    /// The presentation time of the keyframe a seek aimed at <paramref name="aim"/> (a source timestamp, as the keyframe list's) lands
    /// on: the first video packet read after the seek. Null when it cannot be said (no file, ffprobe failed, no packet, a packet that is
    /// not a keyframe); never an error.
    /// </summary>
    Task<TimeSpan?> GetSeekLandingAsync(string path, TimeSpan aim, CancellationToken ct = default);
}
