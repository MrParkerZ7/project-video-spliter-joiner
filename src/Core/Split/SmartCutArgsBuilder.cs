using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// Builds the ffmpeg commands for frame-exact ("smart") cutting (T-124, epic G-042).
///
/// <para>The head fragment is the ONLY thing re-encoded, and it must come out compatible enough with
/// the stream-copied tail for the concat demuxer to join them — same codec, pixel format, resolution
/// and audio shape. Those parameters are read from the source's own probe result rather than assumed,
/// and a codec this builder cannot map to an encoder is reported so the caller can fall back to the
/// lossless cut instead of producing a file that fails (or silently corrupts) at concat time.</para>
/// </summary>
public static class SmartCutArgsBuilder
{
    /// <summary>ffprobe <c>codec_name</c> → the encoder that reproduces it.</summary>
    private static readonly IReadOnlyDictionary<string, string> VideoEncoders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["h264"] = "libx264",
            ["avc1"] = "libx264",
            ["hevc"] = "libx265",
            ["h265"] = "libx265",
            ["vp9"] = "libvpx-vp9",
            ["vp8"] = "libvpx",
            ["av1"] = "libsvtav1",
            ["mpeg4"] = "mpeg4",
            ["mpeg2video"] = "mpeg2video",
        };

    private static readonly IReadOnlyDictionary<string, string> AudioEncoders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aac"] = "aac",
            ["mp3"] = "libmp3lame",
            ["opus"] = "libopus",
            ["vorbis"] = "libvorbis",
            ["ac3"] = "ac3",
            ["eac3"] = "eac3",
            ["flac"] = "flac",
            ["pcm_s16le"] = "pcm_s16le",
        };

    /// <summary>
    /// Resolve the encoders needed to reproduce <paramref name="info"/>'s streams, or explain why this
    /// source cannot be smart-cut. A null result means the caller MUST fall back to the lossless cut —
    /// never guess an encoder, because a mismatch surfaces as a corrupt or failed concat.
    /// </summary>
    public static bool TryResolveEncoders(MediaInfo info, out string? videoEncoder, out string? audioEncoder, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(info);
        videoEncoder = null;
        audioEncoder = null;
        reason = null;

        if (info.HasVideo)
        {
            var codec = info.VideoStreams[0].CodecName;
            if (!VideoEncoders.TryGetValue(codec, out var enc))
            {
                reason = $"no known encoder for video codec '{codec}'";
                return false;
            }

            videoEncoder = enc;
        }

        if (info.HasAudio)
        {
            var codec = info.AudioStreams[0].CodecName;
            if (!AudioEncoders.TryGetValue(codec, out var enc))
            {
                reason = $"no known encoder for audio codec '{codec}'";
                return false;
            }

            audioEncoder = enc;
        }

        if (!info.HasVideo && !info.HasAudio)
        {
            reason = "the source has no video or audio streams";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The head fragment: <c>[start, end)</c> re-encoded to match the source so it can be concatenated
    /// with the copied tail. Uses an OUTPUT seek (<c>-ss</c> AFTER <c>-i</c>) so the cut is
    /// frame-exact — the whole point of this path — accepting the decode cost, which is bounded by one GOP.
    /// </summary>
    public static FfmpegArgs HeadReencode(
        string inputPath,
        TimeSpan start,
        TimeSpan end,
        MediaInfo info,
        string videoEncoder,
        string? audioEncoder,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(info);

        var duration = end - start;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var args = FfmpegArgs.ForFfmpeg()
            .Raw("-y")
            .Input(inputPath)
            // OUTPUT seek: decodes from the previous keyframe and starts the output at EXACTLY `start`.
            .Raw("-ss", SplitPlanner.ToFfmpegSeconds(start))
            .Raw("-t", SplitPlanner.ToFfmpegSeconds(duration))
            .Raw("-map", "0");

        if (info.HasVideo)
        {
            args.Raw("-c:v", videoEncoder);

            // Reproduce the source's pixel format + resolution so the concat demuxer accepts the join.
            var v = info.VideoStreams[0];
            if (!string.IsNullOrWhiteSpace(v.PixFmt))
            {
                args.Raw("-pix_fmt", v.PixFmt!);
            }

            if (v.Width is { } w && v.Height is { } h && w > 0 && h > 0)
            {
                args.Raw("-s", $"{w}x{h}");
            }
        }

        if (info.HasAudio && audioEncoder is not null)
        {
            var a = info.AudioStreams[0];
            args.Raw("-c:a", audioEncoder);
            if (a.SampleRate is { } sr && sr > 0)
            {
                args.Raw("-ar", sr.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (a.Channels is { } ch && ch > 0)
            {
                args.Raw("-ac", ch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return args.Output(outputPath);
    }

    /// <summary>
    /// The copyable tail: <c>[start, end)</c> as a pure stream copy. Identical in shape to
    /// <see cref="SplitArgsBuilder.PerSegment"/> — the tail is exactly what the lossless path
    /// would have produced, so 99%+ of the output is untouched bytes.
    /// </summary>
    public static FfmpegArgs TailCopy(string inputPath, TimeSpan start, TimeSpan? end, string outputPath)
        => SplitArgsBuilder.PerSegment(inputPath, start, end, outputPath);
}
