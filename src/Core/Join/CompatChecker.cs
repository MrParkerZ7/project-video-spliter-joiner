using System.Globalization;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Join;

/// <summary>
/// The PURE concat-compatibility comparison: given the probed <see cref="MediaInfo"/> of every
/// input (in order), decide whether they can be losslessly stream-copied into one file. Takes
/// the FIRST input as the reference and reports one <see cref="Mismatch"/> per differing field,
/// naming the offending clip (1-based). No I/O, no ffmpeg — testable directly with hand-built
/// <see cref="MediaInfo"/> / <see cref="StreamInfo"/> instances.
/// </summary>
public static class CompatChecker
{
    /// <summary>
    /// Compare <paramref name="infos"/> for concat safety. The first entry is the reference;
    /// each subsequent clip's first video stream (codec, width, height, pix_fmt, time_base) and
    /// first audio stream (codec, sample_rate, channels) must match it. A clip that lacks a
    /// video/audio stream the reference has (or vice versa) is itself a mismatch. Fewer than one
    /// input → a single mismatch (nothing to join). Video-only vs video-only is fine; the audio
    /// checks simply no-op when neither side has audio.
    /// </summary>
    public static CompatReport Compare(IReadOnlyList<MediaInfo> infos)
    {
        ArgumentNullException.ThrowIfNull(infos);

        if (infos.Count == 0)
        {
            return CompatReport.Incompatible(new[]
            {
                new Mismatch("input_count", "No inputs supplied — nothing to join."),
            });
        }

        if (infos.Count == 1)
        {
            // A single input is trivially self-compatible; the engine decides whether to
            // passthrough-copy it. Report compatible here.
            return CompatReport.Ok();
        }

        var reference = infos[0];
        var refVideo = reference.VideoStreams.Count > 0 ? reference.VideoStreams[0] : null;
        var refAudio = reference.AudioStreams.Count > 0 ? reference.AudioStreams[0] : null;

        var mismatches = new List<Mismatch>();

        for (var i = 1; i < infos.Count; i++)
        {
            var clip = i + 1; // 1-based clip number for human messages.
            var info = infos[i];
            var video = info.VideoStreams.Count > 0 ? info.VideoStreams[0] : null;
            var audio = info.AudioStreams.Count > 0 ? info.AudioStreams[0] : null;

            CompareVideo(refVideo, video, clip, mismatches);
            CompareAudio(refAudio, audio, clip, mismatches);
        }

        return mismatches.Count == 0 ? CompatReport.Ok() : CompatReport.Incompatible(mismatches);
    }

    private static void CompareVideo(StreamInfo? refVideo, StreamInfo? video, int clip, List<Mismatch> mismatches)
    {
        if (refVideo is null && video is null)
        {
            return;
        }

        if (refVideo is null || video is null)
        {
            mismatches.Add(new Mismatch(
                "video_presence",
                refVideo is null
                    ? $"clip {clip} has a video stream but the reference (clip 1) has none"
                    : $"clip {clip} has no video stream but the reference (clip 1) has one"));
            return;
        }

        if (!string.Equals(refVideo.CodecName, video.CodecName, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new Mismatch(
                "codec",
                $"clip {clip} video codec is '{video.CodecName}', reference (clip 1) is '{refVideo.CodecName}'"));
        }

        if (refVideo.Width != video.Width || refVideo.Height != video.Height)
        {
            mismatches.Add(new Mismatch(
                "resolution",
                $"clip {clip} is {Res(video)}, reference (clip 1) is {Res(refVideo)}"));
        }

        if (!string.Equals(refVideo.PixFmt, video.PixFmt, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new Mismatch(
                "pix_fmt",
                $"clip {clip} pixel format is '{video.PixFmt ?? "unknown"}', reference (clip 1) is '{refVideo.PixFmt ?? "unknown"}'"));
        }

        if (!string.Equals(refVideo.TimeBase, video.TimeBase, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new Mismatch(
                "time_base",
                $"clip {clip} time base is '{video.TimeBase ?? "unknown"}', reference (clip 1) is '{refVideo.TimeBase ?? "unknown"}'"));
        }
    }

    private static void CompareAudio(StreamInfo? refAudio, StreamInfo? audio, int clip, List<Mismatch> mismatches)
    {
        if (refAudio is null && audio is null)
        {
            return;
        }

        if (refAudio is null || audio is null)
        {
            mismatches.Add(new Mismatch(
                "audio_presence",
                refAudio is null
                    ? $"clip {clip} has an audio stream but the reference (clip 1) has none"
                    : $"clip {clip} has no audio stream but the reference (clip 1) has one"));
            return;
        }

        if (!string.Equals(refAudio.CodecName, audio.CodecName, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new Mismatch(
                "audio_codec",
                $"clip {clip} audio codec is '{audio.CodecName}', reference (clip 1) is '{refAudio.CodecName}'"));
        }

        if (refAudio.SampleRate != audio.SampleRate)
        {
            mismatches.Add(new Mismatch(
                "audio_sample_rate",
                $"clip {clip} audio sample rate is {Hz(audio.SampleRate)}, reference (clip 1) is {Hz(refAudio.SampleRate)}"));
        }

        if (refAudio.Channels != audio.Channels)
        {
            mismatches.Add(new Mismatch(
                "audio_channels",
                $"clip {clip} has {ChannelText(audio.Channels)}, reference (clip 1) has {ChannelText(refAudio.Channels)}"));
        }
    }

    private static string Res(StreamInfo v) =>
        v.Width is { } w && v.Height is { } h
            ? string.Create(CultureInfo.InvariantCulture, $"{w}x{h}")
            : "unknown resolution";

    private static string Hz(int? sampleRate) =>
        sampleRate is { } s ? string.Create(CultureInfo.InvariantCulture, $"{s}Hz") : "unknown";

    private static string ChannelText(int? channels) =>
        channels is { } c ? string.Create(CultureInfo.InvariantCulture, $"{c} channel(s)") : "unknown channels";
}
