using System.Globalization;
using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Detect;

/// <summary>
/// Builds the DECODE-ONLY ffmpeg commands the detector runs. Every command outputs to the
/// <c>null</c> muxer (<c>-f null -</c>) — never a real file, never a re-encode: detection data
/// comes purely from stderr. Factored out so a unit test can assert the decode-only invariant
/// (<c>-f null</c> present, no real output path) on the produced token list.
/// </summary>
public static class DetectArgsBuilder
{
    /// <summary>The two tokens that make ffmpeg write to the null muxer instead of a file.</summary>
    public static readonly IReadOnlyList<string> NullMuxerTokens = new[] { "-f", "null" };

    /// <summary>
    /// Extensions / encoder tokens that must NEVER appear — their presence would mean the
    /// command is writing/encoding an output rather than decoding to null.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenOutputTokens = new[]
    {
        "-c:v", "-c:a", "-vcodec", "-acodec", "-codec:v", "-codec:a",
        "-crf", "-preset", "-b:v", "-b:a", "libx264", "libx265",
    };

    /// <summary>Build the black-interval decode pass: <c>-i in -vf blackdetect=… -an -f null -</c>.</summary>
    public static FfmpegArgs Black(string inputPath, TimeSpan minDuration, double picThreshold) =>
        FromFilter(inputPath, $"blackdetect=d={Sec(minDuration)}:pic_th={Num(picThreshold)}");

    /// <summary>
    /// Build the white-interval decode pass — <c>negate</c> the signal so white frames become
    /// black, then reuse blackdetect: <c>-i in -vf negate,blackdetect=… -an -f null -</c>.
    /// </summary>
    public static FfmpegArgs White(string inputPath, TimeSpan minDuration, double whiteThreshold) =>
        FromFilter(inputPath, $"negate,blackdetect=d={Sec(minDuration)}:pic_th={Num(whiteThreshold)}");

    /// <summary>
    /// Build the scene-cut decode pass: select frames whose scene score exceeds the threshold
    /// and print their metadata to stderr —
    /// <c>-i in -vf select='gt(scene,thr)',metadata=print -an -f null -</c>.
    /// </summary>
    public static FfmpegArgs Scene(string inputPath, double sceneThreshold) =>
        FromFilter(inputPath, $"select='gt(scene,{Num(sceneThreshold)})',metadata=print");

    /// <summary>
    /// True if <paramref name="tokens"/> is a decode-only command: it targets the null muxer
    /// (<c>-f null</c>) and contains no encoder token or real output path. The detector asserts
    /// this before every run; tests assert it on the built args directly.
    /// </summary>
    public static bool SatisfiesDecodeOnlyInvariant(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        // Must contain the "-f null" pair.
        var hasNull = false;
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (string.Equals(tokens[i], "-f", StringComparison.Ordinal)
                && string.Equals(tokens[i + 1], "null", StringComparison.Ordinal))
            {
                hasNull = true;
                break;
            }
        }

        if (!hasNull)
        {
            return false;
        }

        // No encoder tokens.
        foreach (var token in tokens)
        {
            foreach (var forbidden in ForbiddenOutputTokens)
            {
                if (string.Equals(token, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        // The final positional (the muxer sink) must be "-" (stdout/null), never a file path.
        return tokens.Count > 0 && string.Equals(tokens[^1], "-", StringComparison.Ordinal);
    }

    private static FfmpegArgs FromFilter(string inputPath, string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        return FfmpegArgs.ForFfmpeg()
            .Input(inputPath)
            .Raw("-an")               // drop audio: detection is video-only, faster decode.
            .Raw("-vf", filter)
            .Raw("-f", "null", "-");  // null muxer to stdout — decode-only, writes nothing.
    }

    private static string Sec(TimeSpan t) =>
        t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Num(double d) =>
        d.ToString("0.####", CultureInfo.InvariantCulture);
}
