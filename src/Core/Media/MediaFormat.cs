using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// Pure, WPF-free formatting helpers for the sample-layout UI (T-059): the file-info meta line
/// ("container · duration · size"), the header format badge ("HEVC · MKV"), the Join screen's
/// estimated-result values (total duration + approximate size summed across clips), and the shared
/// human-readable duration / byte-size formatters those build on. Deliberately dependency-free and
/// culture-invariant so they are fully unit-testable and render identically on every machine.
/// </summary>
public static class MediaFormat
{
    /// <summary>
    /// A concise mono meta line for a loaded file's info card, e.g. <c>"matroska · 10:00 · 1.4 GB"</c>.
    /// The container is the ffprobe <c>format_name</c> shortened to its first alias
    /// (<see cref="ShortContainer"/>); the duration uses <see cref="FormatDuration"/>; the size uses
    /// <see cref="FormatSize"/>. A non-positive <paramref name="sizeBytes"/> (size unknown) drops the
    /// size segment rather than printing "0 B".
    /// </summary>
    public static string MetaLine(MediaInfo info, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(info);

        var container = ShortContainer(info.Container);
        var duration = FormatDuration(info.Duration);

        return sizeBytes > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{container} · {duration} · {FormatSize(sizeBytes)}")
            : string.Create(CultureInfo.InvariantCulture, $"{container} · {duration}");
    }

    /// <summary>
    /// The header format/status badge for a loaded file, e.g. <c>"HEVC · MKV"</c> — the first video
    /// codec (upper-cased friendly name) joined with the short container (upper-cased). Falls back to
    /// just the container badge when the file has no video stream, and to <c>null</c> when
    /// <paramref name="info"/> is null (no file loaded → no badge shown).
    /// </summary>
    public static string? Badge(MediaInfo? info)
    {
        if (info is null)
        {
            return null;
        }

        var container = ShortContainer(info.Container).ToUpperInvariant();
        var video = info.VideoStreams.Count > 0 ? info.VideoStreams[0] : null;
        if (video is null)
        {
            return container;
        }

        var codec = FriendlyCodec(video.CodecName).ToUpperInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"{codec} · {container}");
    }

    /// <summary>
    /// The estimated result of joining a set of clips (T-059): the summed total duration and the
    /// summed approximate byte size over every clip's probed duration/size. Nulls / negatives are
    /// treated as zero so a not-yet-probed clip contributes nothing rather than corrupting the total.
    /// Stream-copy concat is lossless, so the summed input size is a faithful estimate of the output.
    /// </summary>
    public static (TimeSpan TotalDuration, long ApproxBytes) Estimate(IEnumerable<(TimeSpan Duration, long Bytes)> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        var total = TimeSpan.Zero;
        long bytes = 0;
        foreach (var (duration, size) in clips)
        {
            if (duration > TimeSpan.Zero)
            {
                total += duration;
            }

            if (size > 0)
            {
                bytes += size;
            }
        }

        return (total, bytes);
    }

    /// <summary>
    /// Format a LENGTH as <c>M:SS</c> (unpadded minutes) or <c>H:MM:SS</c> past an hour — matches the
    /// compact duration style used across the Split parts list. Negative inputs are shown as their
    /// magnitude.
    /// </summary>
    public static string FormatDuration(TimeSpan t)
    {
        var a = t < TimeSpan.Zero ? t.Negate() : t;
        return a.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)a.TotalHours}:{a.Minutes:00}:{a.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{a.Minutes}:{a.Seconds:00}");
    }

    /// <summary>
    /// Format a byte count in binary units (KB/MB/GB/TB @ 1024) with one decimal place for MB and up,
    /// no decimals for bytes/KB, e.g. <c>1503238553 → "1.4 GB"</c>, <c>2048 → "2 KB"</c>,
    /// <c>512 → "512 B"</c>. Non-positive input is <c>"0 B"</c>.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Bytes and KB read cleaner without a decimal; MB and above keep one place of precision.
        var value = unit <= 1
            ? size.ToString("0", CultureInfo.InvariantCulture)
            : size.ToString("0.#", CultureInfo.InvariantCulture);

        return string.Create(CultureInfo.InvariantCulture, $"{value} {units[unit]}");
    }

    /// <summary>
    /// Shorten an ffprobe container <c>format_name</c> — often a comma-joined alias list such as
    /// <c>"mov,mp4,m4a,3gp,3g2,mj2"</c> — to its first, most-recognizable alias (<c>"mov"</c>). A
    /// single-name container (e.g. <c>"matroska"</c>) is returned unchanged. Null/blank → empty.
    /// </summary>
    public static string ShortContainer(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return string.Empty;
        }

        var first = container.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return first ?? container.Trim();
    }

    /// <summary>
    /// Map an ffprobe <c>codec_name</c> to the friendly badge label the sample uses — <c>"hevc" → "HEVC"</c>,
    /// <c>"h264" → "H.264"</c>, <c>"av1" → "AV1"</c>, etc. Unknown codecs pass through unchanged (the
    /// caller upper-cases for the badge). The mapping is intentionally small and additive.
    /// </summary>
    public static string FriendlyCodec(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
        {
            return string.Empty;
        }

        return codecName.Trim().ToLowerInvariant() switch
        {
            "hevc" or "h265" => "HEVC",
            "h264" or "avc" => "H.264",
            "av1" => "AV1",
            "vp9" => "VP9",
            "vp8" => "VP8",
            "mpeg2video" => "MPEG-2",
            "mpeg4" => "MPEG-4",
            var other => other,
        };
    }
}
