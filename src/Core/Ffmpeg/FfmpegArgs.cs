namespace VideoSplitJoiner.Core.Ffmpeg;

/// <summary>
/// Typed, fluent argument builder for ffmpeg/ffprobe invocations.
/// Produces a list of discrete argument tokens intended for
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> — each element is a
/// single argument, so paths with spaces, unicode, or quotes are passed verbatim
/// to the child process with no shell re-parsing or manual quoting.
/// </summary>
public sealed class FfmpegArgs
{
    private readonly List<string> _args = new();

    private FfmpegArgs()
    {
    }

    /// <summary>
    /// Start a new ffmpeg argument list. Emits <c>-hide_banner</c> and <c>-nostdin</c>
    /// so ffmpeg does not print its build banner and never blocks reading stdin.
    /// </summary>
    public static FfmpegArgs ForFfmpeg()
    {
        var a = new FfmpegArgs();
        a._args.Add("-hide_banner");
        a._args.Add("-nostdin");
        return a;
    }

    /// <summary>
    /// Start a new ffprobe argument list. Emits <c>-hide_banner</c> only
    /// (ffprobe has no <c>-nostdin</c> flag and is query-style).
    /// </summary>
    public static FfmpegArgs ForFfprobe()
    {
        var a = new FfmpegArgs();
        a._args.Add("-hide_banner");
        return a;
    }

    /// <summary>Append an <c>-i &lt;path&gt;</c> input, path as a single token.</summary>
    public FfmpegArgs Input(string path)
    {
        _args.Add("-i");
        _args.Add(path);
        return this;
    }

    /// <summary>Append an output path as a single token (last positional argument).</summary>
    public FfmpegArgs Output(string path)
    {
        _args.Add(path);
        return this;
    }

    /// <summary>Append arbitrary raw arguments, each as its own token (no splitting).</summary>
    public FfmpegArgs Raw(params string[] args)
    {
        foreach (var a in args)
        {
            _args.Add(a);
        }

        return this;
    }

    /// <summary>The accumulated argument tokens, in order, for <c>ProcessStartInfo.ArgumentList</c>.</summary>
    public IReadOnlyList<string> ToList() => _args.AsReadOnly();
}
