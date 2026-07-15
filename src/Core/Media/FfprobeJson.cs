using System.Text.Json.Serialization;

namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// Internal DTOs mapping the ffprobe <c>-print_format json</c> payload. Only the fields we
/// consume are declared; unknown fields are ignored by <c>System.Text.Json</c>.
/// </summary>
internal sealed class FfprobeShowRoot
{
    [JsonPropertyName("streams")]
    public List<FfprobeStream>? Streams { get; set; }

    [JsonPropertyName("format")]
    public FfprobeFormat? Format { get; set; }
}

internal sealed class FfprobeFormat
{
    [JsonPropertyName("format_name")]
    public string? FormatName { get; set; }

    [JsonPropertyName("duration")]
    public string? Duration { get; set; }
}

internal sealed class FfprobeStream
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; set; }

    [JsonPropertyName("codec_type")]
    public string? CodecType { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("pix_fmt")]
    public string? PixFmt { get; set; }

    // ffprobe emits sample_rate / channels as strings inside JSON.
    [JsonPropertyName("sample_rate")]
    public string? SampleRate { get; set; }

    [JsonPropertyName("channels")]
    public int? Channels { get; set; }

    [JsonPropertyName("time_base")]
    public string? TimeBase { get; set; }

    [JsonPropertyName("duration")]
    public string? Duration { get; set; }
}

/// <summary>DTO for the <c>-show_frames</c>/<c>-show_entries frame=pts_time</c> keyframe payload.</summary>
internal sealed class FfprobeFramesRoot
{
    [JsonPropertyName("frames")]
    public List<FfprobeFrame>? Frames { get; set; }
}

internal sealed class FfprobeFrame
{
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("key_frame")]
    public int? KeyFrame { get; set; }

    [JsonPropertyName("pts_time")]
    public string? PtsTime { get; set; }

    [JsonPropertyName("pkt_pts_time")]
    public string? PktPtsTime { get; set; }

    [JsonPropertyName("best_effort_timestamp_time")]
    public string? BestEffortTimestampTime { get; set; }
}
