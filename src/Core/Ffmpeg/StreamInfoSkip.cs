namespace VideoSplitJoiner.Core.Ffmpeg;

/// <summary>
/// T-195: which ffmpeg runs may skip the stream-info decode. Before it reads an input, ffmpeg decodes its first frames
/// to fill in stream information (<c>avformat_find_stream_info</c>). On the bench's 4K H.264 fixtures that cost about
/// 0.035-0.085 s per waveform run, roughly 25-35% (0.045 s on 4k, 0.035 s on g10, 0.085 s on the NVENC 4K fixture;
/// T-195's Build log). An MP4/MOV container already states what a waveform extraction
/// needs, so that run puts <see cref="Flag"/> before <c>-i</c> for such an input, and wrote byte-identical PCM in the
/// T-195 measurements.
///
/// <para><b>Only the waveform uses it.</b> The pass also computes the format <c>start_time</c>, and an input
/// <c>-ss</c> counts from it; without it the <c>-ss</c> counts from timestamp 0. The waveform does not seek, so its PCM
/// is the same either way, on files that start at 0 and on files that do not. The frame grab seeks, so it keeps the
/// pass (SPEC-005 I27): on an MP4/MOV whose start time is not 0 a skipping grab exits 0 with the wrong frame, and the
/// first part of the app's own lossless split of an H.264 B-frame + AAC MP4 starts at about 0.043 s (a skipping grab
/// came back one frame early). Letting the grab skip the pass only when the probed start time is 0 is a follow-up
/// ticket. The metadata probe needs <c>pix_fmt</c> and the format duration, which the flag loses, and the
/// <c>-c copy</c> runs are not touched (SPEC-006 I3).</para>
///
/// <para><b>The extension is a hint, and the retry backs a failed run.</b> A file named <c>.mp4</c> that holds something
/// else (an MPEG-TS stream, say) may fail the flagged run; the waveform then runs once more without the flag
/// (SPEC-006 I24). Reading the file's signature instead would add file I/O to every run.</para>
/// </summary>
internal static class StreamInfoSkip
{
    /// <summary>The ffmpeg input option that skips the stream-info decode. It goes immediately before <c>-i</c>.</summary>
    public const string Flag = "-nofind_stream_info";

    private static readonly string[] IsoBmffExtensions = { ".mp4", ".m4v", ".mov" };

    /// <summary>
    /// True when <paramref name="path"/> ends in <c>.mp4</c>, <c>.m4v</c> or <c>.mov</c>, in any case: the
    /// ISO-BMFF/QuickTime family whose header carries the codec parameters a waveform extraction needs. Only the last
    /// extension counts, so <c>clip.mp4.mkv</c> is not one. Null, blank or extension-less paths are not.
    /// </summary>
    public static bool IsIsoBmffExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        foreach (var candidate in IsoBmffExtensions)
        {
            if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
