using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// Builds the ffmpeg commands for frame-exact ("smart") cutting (T-124, epic G-042).
///
/// <para>The head fragment is the ONLY thing re-encoded, and it must come out compatible enough with
/// the source's own packets that follow it for the concat demuxer to join them — same codec, pixel format,
/// resolution and audio shape. Those parameters are read from the source's own probe result rather than assumed,
/// and a codec this builder cannot map to an encoder is reported so the caller can fall back to the
/// lossless cut instead of producing a file that fails (or silently corrupts) at concat time.</para>
///
/// <para>The kept part is never written to a file of its own (T-190): the concat reads the source itself from the
/// boundary keyframe (<see cref="RenderJointList"/>), so the kept bytes are written once and the joint lands on time.</para>
/// </summary>
public static class SmartCutArgsBuilder
{
    /// <summary>ffprobe <c>codec_name</c> → the encoder that reproduces it.</summary>
    private static readonly IReadOnlyDictionary<string, string> VideoEncoders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["h264"] = "libx264",
            ["avc1"] = "libx264",
            // hevc/h265 are deliberately absent: an HEVC joint decodes against the head's parameter sets (T-189); T-201 repairs it.
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
    /// Video codecs whose head/tail joint is known to break, each with the reason a user reads on the row (T-189). They
    /// are checked BEFORE the generic "no known encoder" branch so the row names a cause the user can act on.
    ///
    /// <para><b>HEVC.</b> The concat output takes its codec configuration from the first file, the re-encoded head (hev1
    /// extradata), while the copied tail's slices refer to parameter sets that exist only in the source's hvc1 extradata.
    /// The concat demuxer puts parameter sets in-band automatically for H.264 only (<c>h264_mp4toannexb</c>), so an HEVC
    /// final decodes with errors and wrong frames from the joint on. Measured by <c>SmartCutJointIntegrity</c>; the
    /// repair is T-201, and it must pass that check before HEVC goes back into <see cref="VideoEncoders"/>.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> UnsupportedVideoCodecs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hevc"] = HevcUnsupportedReason,
            ["h265"] = HevcUnsupportedReason,
        };

    private const string HevcUnsupportedReason = "frame-exact cutting is not supported for HEVC/H.265 video yet";

    /// <summary>
    /// Why Exact refuses a source whose format <c>start_time</c> is not zero (T-190), in the words the Bulk Cut row and the
    /// run's warning show. No parentheses: the row warning wraps the reason in its own (T-189's copy rule).
    /// </summary>
    internal const string NonZeroStartTimeReason =
        "frame-exact cutting is not supported yet for .ts, .m2ts and .mts files or other files whose timestamps do not start at zero";

    /// <summary>
    /// Why Exact refuses a Matroska or WebM source (T-190, second pass). Measured with the engine's own commands, AFTER the
    /// frame snap, on the bench <c>mkv</c> fixture's shape (format start 0, video 21 ms late) at Starts on and off its frame
    /// grid (10.5, 10.518, 10.531, 3.3, 25.5 s): no frame lost or repeated, but at every one of them the joint was 21 ms early
    /// (-0.63 frames), 4-5 audio packets did not increase (one AAC frame of overlap) and a full decode printed 3-4 lines. The
    /// head file is Matroska too and starts at -21 ms (the AAC encoder's priming packet, which an MP4 head hides in an edit
    /// list and starts at 0), so the concat shifts the head 21 ms against the copied part; the Start is not the cause. No
    /// small fix held (the head with <c>-avoid_negative_ts make_zero</c>, an <c>inpoint 0</c> on the head entry, an
    /// <c>asetpts</c> or <c>aresample</c> filter, an MP4 head: the joint, the audio or the decode stayed wrong or got worse),
    /// and almost every ffmpeg-made Matroska/WebM file starts below zero anyway (h264+aac .mkv -0.021 s, h264+opus .mkv and
    /// vp9+opus .webm -0.007 s), so the file types are named here instead of leaving the user a reason about timestamps. No
    /// parentheses (the row warning wraps the reason in its own).
    /// </summary>
    internal const string MatroskaUnsupportedReason = "frame-exact cutting is not supported yet for .mkv and .webm files";

    /// <summary>The Matroska/WebM file extensions (<c>.mka</c> is Matroska audio): ffmpeg picks the muxer from them.</summary>
    private static readonly string[] MatroskaExtensions = { ".mkv", ".webm", ".mka" };

    /// <summary>The transport-stream extensions the Exact note names (<c>VideoFileFilter</c> accepts all three).</summary>
    private static readonly string[] TransportStreamExtensions = { ".ts", ".m2ts", ".mts" };

    /// <summary>
    /// Why Exact refuses a file by its NAME alone, or null when the name is fine (T-190, third pass). The engine writes its
    /// head and its final into the container the OUTPUT extension picks, so a source that ffprobe calls <c>mov,mp4</c> but
    /// is named <c>.mkv</c> (or any source cut into a <c>.mkv</c> / <c>.webm</c> / <c>.mka</c> output) gets a Matroska head
    /// and final that the container check never saw: measured on a 20 s source copied to <c>mislabeled.mkv</c>, Start 7.018,
    /// the final was 622.4 s long, its audio packets 1.024 s apart, and the cut was reported Done. The same MP4 content cut
    /// into a <c>.ts</c> or <c>.m2ts</c> output came out three frames early with 547 ms of audio overlap. So the names the
    /// Exact note lists count as well as the container: Matroska/WebM names get <see cref="MatroskaUnsupportedReason"/>,
    /// transport-stream names get <see cref="NonZeroStartTimeReason"/> (the sentence that names them). Case-insensitive; a
    /// null or extension-less path is fine.
    /// </summary>
    public static string? UnsupportedFileNameReason(string? path)
    {
        var ext = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetExtension(path);
        if (ext.Length == 0)
        {
            return null;
        }

        if (MatroskaExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return MatroskaUnsupportedReason;
        }

        return TransportStreamExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ? NonZeroStartTimeReason : null;
    }

    /// <summary>
    /// A format <c>start_time</c> within this of zero counts as zero (T-190). Exact assumes the probe's keyframe times and
    /// ffmpeg's <c>-ss</c> offsets coincide, which holds only when the format starts at 0; a millisecond is the mp4/mkv
    /// timestamp rounding, an MPEG-TS starts near 1.4 s and a negative composition offset about -21 ms.
    /// </summary>
    internal static readonly TimeSpan StartTimeTolerance = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Resolve the encoders needed to reproduce <paramref name="info"/>'s streams, or explain why this
    /// source cannot be smart-cut. A null result means the caller MUST fall back to the lossless cut —
    /// never guess an encoder, because a mismatch surfaces as a corrupt or failed concat.
    ///
    /// <para>This is the ONE predicate that decides whether Exact really runs for a source (G-059): the engine falls back
    /// on it, and the Bulk Cut row evaluates it on its own probed <see cref="MediaInfo"/> to decide, before Run, whether
    /// it shows the snapped cut and the reason (T-189) — so the row and the run never disagree.</para>
    ///
    /// <para><paramref name="sourcePath"/> and <paramref name="outputPath"/> (T-190, third pass) are the names the cut will
    /// read and write: the container the head and the final get comes from the OUTPUT extension, which the probed
    /// <paramref name="info"/> cannot see (<see cref="UnsupportedFileNameReason"/>). The engine passes both; the Bulk row
    /// passes its source's name, because its output keeps the extension. Both are optional so a caller with only a probe
    /// result still gets the container-level answer.</para>
    /// </summary>
    public static bool TryResolveEncoders(
        MediaInfo info,
        out string? videoEncoder,
        out string? audioEncoder,
        out string? reason,
        string? sourcePath = null,
        string? outputPath = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        videoEncoder = null;
        audioEncoder = null;
        reason = null;

        // ffprobe names both .mkv and .webm "matroska,webm". Such a file is refused at the end (MatroskaUnsupportedReason), after
        // the codecs: a codec Exact cannot reproduce is reported as itself.
        var matroska = info.Container.Contains("matroska", StringComparison.OrdinalIgnoreCase);

        // The time base (T-190). Keyframe times and the concat demuxer's inpoint are absolute source timestamps, but the
        // head's -ss is an offset from the format start_time: with a start_time that is not zero (MPEG-TS and AVCHD start
        // near 1.4 s, a negative one is real too) the head and the kept part would overlap by it. Checked on the absolute
        // value, first, because it holds for every codec. The video stream's own start is NOT read: a file whose video
        // starts one AAC frame after its audio (the app's own Join output) joins correctly (SPEC-001 I68). A Matroska/WebM
        // file is left to the container reason below, which names its file type: an ffmpeg-made one starts below zero
        // (-21 ms) and would otherwise be told about timestamps.
        if (!matroska && info.StartTime.Duration() > StartTimeTolerance)
        {
            reason = NonZeroStartTimeReason;
            return false;
        }

        if (info.HasVideo)
        {
            var codec = info.VideoStreams[0].CodecName;
            if (UnsupportedVideoCodecs.TryGetValue(codec, out var unsupported))
            {
                reason = unsupported;
                return false;
            }

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

        // The container (T-190, second pass) and the file names (third pass). See MatroskaUnsupportedReason and
        // UnsupportedFileNameReason.
        var containerReason = matroska
            ? MatroskaUnsupportedReason
            : UnsupportedFileNameReason(sourcePath) ?? UnsupportedFileNameReason(outputPath);
        if (containerReason is not null)
        {
            videoEncoder = null;
            audioEncoder = null;
            reason = containerReason;
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
    /// The concat list of the joint (T-190): the re-encoded head, then the SOURCE ITSELF from the boundary keyframe.
    ///
    /// <code>
    /// file '&lt;head&gt;'
    /// duration &lt;HeadEnd − Start&gt;
    /// file '&lt;source&gt;'
    /// inpoint &lt;HeadEnd&gt;
    /// outpoint &lt;End&gt;          (only when the kept range has an end)
    /// </code>
    ///
    /// <para><c>HeadEnd</c> is by construction a keyframe (SPEC-001 I41), so the demuxer's seek to <c>inpoint</c> lands on it
    /// and no frame before it is copied; <c>inpoint</c> and <c>outpoint</c> are absolute source timestamps, the time base of
    /// the probe's keyframe list. The head's <c>duration</c> is exactly <paramref name="headDuration"/>
    /// (<c>HeadEnd − Start</c>), whatever the head's AAC priming or padding makes its container duration, which fixes where
    /// the source leg starts. Seconds are formatted by <see cref="SplitPlanner.ToFfmpegSeconds"/> and paths are quoted by
    /// <see cref="JoinArgsBuilder.QuoteConcatPath"/>, the way Join's own list is; Join's <c>RenderConcatList</c> is
    /// untouched. The command that reads it is the unchanged <see cref="JoinArgsBuilder.ConcatCopy"/>, so it still
    /// satisfies <see cref="JoinArgsBuilder.SatisfiesCopyInvariant"/>.</para>
    /// </summary>
    public static string RenderJointList(
        string headPath, TimeSpan headDuration, string sourcePath, TimeSpan headEnd, TimeSpan? end)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var lines = new List<string>(5)
        {
            $"file {JoinArgsBuilder.QuoteConcatPath(Path.GetFullPath(headPath))}",
            $"duration {SplitPlanner.ToFfmpegSeconds(headDuration)}",
            $"file {JoinArgsBuilder.QuoteConcatPath(Path.GetFullPath(sourcePath))}",
            $"inpoint {SplitPlanner.ToFfmpegSeconds(headEnd)}",
        };

        if (end is { } e)
        {
            lines.Add($"outpoint {SplitPlanner.ToFfmpegSeconds(e)}");
        }

        return string.Join("\n", lines) + "\n";
    }
}
