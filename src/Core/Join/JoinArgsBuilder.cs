using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Join;

/// <summary>
/// The SINGLE choke-point that builds the ffmpeg command the join engine runs, plus the
/// concat-list-file rendering it depends on. The INVARIANT is that join is pure stream-copy
/// (<c>-c copy</c>) via the concat demuxer, never a re-encode — factored out so a unit test
/// can inspect the produced token list and assert <c>copy</c> is present and no encoder token
/// leaks in.
/// </summary>
public static class JoinArgsBuilder
{
    /// <summary>
    /// Encoder-ish tokens that must NEVER appear in a join invocation. Mirrors the split-side
    /// forbidden set: used both to assert the invariant on any produced token list and to catch
    /// an accidental re-encode leak.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenEncoderTokens = new[]
    {
        "-c:v", "-c:a", "-vcodec", "-acodec", "-codec:v", "-codec:a",
        "-crf", "-preset", "-b:v", "-b:a", "-qp", "-x264-params", "-x265-params",
        "libx264", "libx265", "h264", "hevc", "aac", "libmp3lame", "mpeg4", "vp9", "libvpx",
        "-filter:v", "-filter:a", "-vf", "-af", "-filter_complex",
    };

    /// <summary>
    /// Build the concat-demuxer stream-copy command:
    /// <c>-y -f concat -safe 0 -i &lt;listFile&gt; -map 0 -c copy &lt;out&gt;</c>.
    /// The list file is a temp file of <c>file '&lt;abs-path&gt;'</c> lines (see
    /// <see cref="RenderConcatList"/>); <c>-safe 0</c> allows the absolute paths it holds.
    /// </summary>
    public static FfmpegArgs ConcatCopy(string listFilePath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return FfmpegArgs.ForFfmpeg()
            .Raw("-y")
            .Raw("-f", "concat")
            .Raw("-safe", "0")
            .Input(listFilePath)
            .Raw("-map", "0")
            .Raw("-c", "copy")
            .Output(outputPath);
    }

    /// <summary>
    /// Render the concat demuxer list-file body for the given inputs, one
    /// <c>file '&lt;absolute-path&gt;'</c> line per input, in order. Paths are made absolute; a
    /// single-quote inside a path is escaped the concat-demuxer way — close the quote, emit an
    /// escaped quote, reopen — e.g. <c>a'b</c> → <c>'a'\''b'</c>. Lines are joined with <c>\n</c>.
    /// </summary>
    public static string RenderConcatList(IReadOnlyList<string> inputPaths)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);

        var lines = new List<string>(inputPaths.Count);
        foreach (var p in inputPaths)
        {
            var abs = Path.GetFullPath(p);
            lines.Add($"file {QuoteConcatPath(abs)}");
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Single-quote a path for a concat list line, escaping embedded single quotes as
    /// <c>'\''</c> (end quote, literal escaped quote, start quote — the standard shell-style
    /// escape the concat demuxer understands).
    /// </summary>
    internal static string QuoteConcatPath(string path)
    {
        var escaped = path.Replace("'", "'\\''", StringComparison.Ordinal);
        return "'" + escaped + "'";
    }

    /// <summary>
    /// True if the token list satisfies the join invariant: it contains a bare <c>copy</c>
    /// token AND none of <see cref="ForbiddenEncoderTokens"/>. The engine asserts this on the
    /// built command before launching; tests assert it on the built args directly.
    /// </summary>
    public static bool SatisfiesCopyInvariant(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var hasCopy = tokens.Any(t => string.Equals(t, "copy", StringComparison.Ordinal));
        if (!hasCopy)
        {
            return false;
        }

        foreach (var token in tokens)
        {
            foreach (var forbidden in ForbiddenEncoderTokens)
            {
                if (string.Equals(token, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
