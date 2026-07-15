using System.Text.RegularExpressions;

namespace VideoSplitJoiner.Core.Ffmpeg;

/// <summary>
/// Stateful parser that extracts a monotonic progress fraction (0..1) from ffmpeg
/// stderr lines. ffmpeg prints periodic <c>time=HH:MM:SS.mmm</c> markers; given a known
/// total duration, elapsed/total gives the fraction. The value is clamped to [0,1] and
/// never allowed to decrease (some ffmpeg output can momentarily rewind).
/// </summary>
internal sealed class FfmpegProgress
{
    // Matches "time=00:01:23.45" or "time=01:23.4" style tokens in an ffmpeg status line.
    private static readonly Regex TimeRegex = new(
        @"time=\s*(?<h>\d+):(?<m>\d{1,2}):(?<s>\d{1,2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly TimeSpan? _total;
    private double _last;

    /// <summary>
    /// Create a parser for a run whose total duration is <paramref name="total"/>.
    /// If null (unknown), progress can never be computed and stays 0.
    /// </summary>
    public FfmpegProgress(TimeSpan? total)
    {
        _total = total is { Ticks: > 0 } ? total : null;
        _last = 0.0;
    }

    /// <summary>The most recently reported (monotonic, clamped) fraction.</summary>
    public double Current => _last;

    /// <summary>
    /// Feed one stderr line. Returns the new fraction if this line advanced progress,
    /// otherwise null (no <c>time=</c> token, unknown total, or a non-advancing value).
    /// </summary>
    public double? Feed(string line)
    {
        if (_total is null || string.IsNullOrEmpty(line))
        {
            return null;
        }

        var m = TimeRegex.Match(line);
        if (!m.Success)
        {
            return null;
        }

        var h = int.Parse(m.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups["m"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var sec = double.Parse(m.Groups["s"].Value, System.Globalization.CultureInfo.InvariantCulture);

        var elapsed = (h * 3600.0) + (min * 60.0) + sec;
        var fraction = elapsed / _total.Value.TotalSeconds;

        if (fraction < 0.0)
        {
            fraction = 0.0;
        }
        else if (fraction > 1.0)
        {
            fraction = 1.0;
        }

        // Monotonic: never go backwards.
        if (fraction <= _last)
        {
            return null;
        }

        _last = fraction;
        return _last;
    }
}
