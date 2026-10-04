using System.Globalization;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.Bench;

/// <summary>
/// The Exact-cut head variants the T-187 evidence compared against SmartCutEngine's own head (output seek,
/// encoder defaults): an input seek, two libx264/libx265 presets, and NVENC with and without CUDA decode. Shared
/// by the <c>smart</c> core scenario (timing only) and <c>alt smart2</c> (timing plus equivalence checks).
/// </summary>
internal static class SmartHeads
{
    /// <summary>The span a head re-encodes and the encoders SmartCutArgsBuilder resolved for it.</summary>
    internal sealed record Span(MediaInfo Info, TimeSpan Start, TimeSpan HeadEnd, string VideoEncoder, string? AudioEncoder);

    /// <summary>The variants as raw ffmpeg tokens (output path not included).</summary>
    public static List<(string Name, string[] Args)> Variants(Fixture fx, Span span)
    {
        var vs = span.Info.VideoStreams[0];
        var aud = span.Info.AudioStreams.FirstOrDefault();
        var venc = span.VideoEncoder;
        var sStart = SplitPlanner.ToFfmpegSeconds(span.Start);
        var sDur = SplitPlanner.ToFfmpegSeconds(span.HeadEnd - span.Start);
        string[] Enc(params string[] videoEnc)
        {
            var tokens = new List<string> { "-map", "0", "-c:v" };
            tokens.AddRange(videoEnc);
            tokens.AddRange(new[] { "-pix_fmt", vs.PixFmt ?? "yuv420p", "-s", $"{vs.Width}x{vs.Height}" });
            if (aud is not null && span.AudioEncoder is not null)
            {
                tokens.AddRange(new[] { "-c:a", span.AudioEncoder });
                if (aud.SampleRate is { } sr)
                {
                    tokens.AddRange(new[] { "-ar", sr.ToString(CultureInfo.InvariantCulture) });
                }

                if (aud.Channels is { } ch)
                {
                    tokens.AddRange(new[] { "-ac", ch.ToString(CultureInfo.InvariantCulture) });
                }
            }
            else
            {
                tokens.Add("-an");
            }

            return tokens.ToArray();
        }

        var nvenc = venc == "libx265" ? "hevc_nvenc" : "h264_nvenc";
        var inSeek = new[] { "-y", "-ss", sStart, "-i", fx.Path, "-t", sDur };
        return new List<(string, string[])>
        {
            ($"-ss {sStart} -i in (input seek, accurate) {venc} default preset", inSeek.Concat(Enc(venc)).ToArray()),
            ($"input seek + {venc} -preset veryfast", inSeek.Concat(Enc(venc, "-preset", "veryfast")).ToArray()),
            ($"input seek + {venc} -preset ultrafast", inSeek.Concat(Enc(venc, "-preset", "ultrafast")).ToArray()),
            ($"input seek + {nvenc} -preset p4 -cq 19", inSeek.Concat(Enc(nvenc, "-preset", "p4", "-rc", "vbr", "-cq", "19", "-b:v", "0")).ToArray()),
            ($"-hwaccel cuda + input seek + {nvenc} p4 cq19",
                new[] { "-y", "-hwaccel", "cuda", "-ss", sStart, "-i", fx.Path, "-t", sDur }.Concat(Enc(nvenc, "-preset", "p4", "-rc", "vbr", "-cq", "19", "-b:v", "0")).ToArray()),
        };
    }

    /// <summary>Time every variant once on the span (NVENC rows fail fast on a machine without an NVIDIA GPU; they are recorded as FAILED).</summary>
    public static async Task TimeVariants(BenchContext c, Fixture fx, Span span, string tag)
    {
        var dir = c.NewTempDir("smartvar");
        try
        {
            var size = new FileInfo(fx.Path).Length;
            var i = 0;
            foreach (var (name, args) in Variants(fx, span))
            {
                var head = Path.Combine(dir, $"head{i++}{Path.GetExtension(fx.Path)}");
                var ok = false;
                var (s, u) = await Meter.Run(async () => ok = (await c.RunFfmpeg(args.Concat(new[] { head }).ToArray())).R.Success);
                c.Results.Add("bulk", $"  Exact {tag}: head variant: {name}", fx.Key, "warm", 1, s, u,
                    ok && File.Exists(head)
                        ? $"head {Io.Mb(new FileInfo(head).Length)}; read {100.0 * u.ReadBytes / size:0}% of the file"
                        : "FAILED");
                Io.TryDelete(head);
            }
        }
        finally
        {
            Io.TryDelete(dir);
        }
    }
}
