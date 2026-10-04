using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace T187Bench;

/// <summary>
/// Exact-cut (SmartCutEngine head re-encode) alternatives, lighter checks than Alternatives.SmartVariants so the
/// whole matrix fits: frame-exactness is judged against the SOURCE (head vs source [start,start+dur) PSNR, and the
/// copied tail of the final file vs the source after the boundary keyframe), packet counts instead of full decodes.
/// usage: T187Bench alt smart2 [4k104|hevc10|hevc44|4k10cuda|g10]
/// </summary>
internal static class Smart2
{
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static async Task<double> PsnrArgs(params string[] inputsAndFilter)
    {
        var (_, r) = await Svc.RunFfmpeg(inputsAndFilter.Concat(new[] { "-f", "null", "-" }).ToArray());
        foreach (var line in r.StdErrTail.Reverse())
        {
            var m = Regex.Match(line, @"average:(?<v>inf|[0-9.]+)");
            if (m.Success)
            {
                return m.Groups["v"].Value == "inf" ? 99.0 : double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
            }
        }

        return double.NaN;
    }

    private static async Task<(int Packets, double Duration)> CountPackets(string path)
    {
        var (_, o) = await Svc.RunFfprobe("-v", "error", "-count_packets", "-select_streams", "v:0", "-show_entries", "stream=nb_read_packets:format=duration", "-of", "json", "-i", path);
        using var doc = JsonDocument.Parse(o);
        var n = int.Parse(doc.RootElement.GetProperty("streams")[0].GetProperty("nb_read_packets").GetString()!, CultureInfo.InvariantCulture);
        var d = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return (n, d);
    }

    public static async Task Run(List<string> which)
    {
        var cases = new List<(string Id, string Fx, double Start, bool OnlyHw)>
        {
            ("4k104", "4k", 104.5, false),
            ("hevc10", "hevc", 10.5, false),
            ("hevc44", "hevc", 44.5, false),
            ("4k10cuda", "4k", 10.5, true),
            ("g10", "g10", 12.5, false),
            ("4k10m540", "4k10m", 540.5, false),
        };
        foreach (var c in cases.Where(c => which.Count == 0 || which.Contains(c.Id)))
        {
            await One(Fixture.Get(c.Fx), c.Start, c.OnlyHw);
        }
    }

    private static async Task One(Fixture fx, double start, bool onlyHw)
    {
        var probe = Svc.NewProbe();
        var info = ((ProbeResult.ProbeSucceeded)await probe.ProbeAsync(fx.Path)).Info;
        var kfs = await probe.GetKeyframesAsync(fx.Path);
        SmartCutArgsBuilder.TryResolveEncoders(info, out var venc, out var aenc, out _);
        var vs = info.VideoStreams[0];
        var aud = info.AudioStreams[0];
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(start), null, kfs);
        var headEnd = plan.HeadEnd!.Value;
        var dur = headEnd - plan.Start;
        var sStart = SplitPlanner.ToFfmpegSeconds(plan.Start);
        var sDur = SplitPlanner.ToFfmpegSeconds(dur);
        var sHeadEnd = SplitPlanner.ToFfmpegSeconds(headEnd);
        var dir = Svc.NewTempDir("smart2");
        string[] Enc(params string[] videoEnc) => new[] { "-map", "0", "-c:v" }.Concat(videoEnc)
            .Concat(new[] { "-pix_fmt", vs.PixFmt!, "-s", $"{vs.Width}x{vs.Height}", "-c:a", aenc!, "-ar", aud.SampleRate!.Value.ToString(CultureInfo.InvariantCulture), "-ac", aud.Channels!.Value.ToString(CultureInfo.InvariantCulture) }).ToArray();
        var nvenc = venc == "libx265" ? "hevc_nvenc" : "h264_nvenc";
        var inSeek = new[] { "-y", "-ss", sStart, "-i", fx.Path, "-t", sDur };
        var all = new List<(string Name, string[] Args, bool Engine)>
        {
            ($"current engine args (SmartCutArgsBuilder.HeadReencode: -i in -ss {sStart}, {venc} default preset)", Array.Empty<string>(), true),
            ($"-ss {sStart} -i in (input seek, accurate) {venc} default preset", inSeek.Concat(Enc(venc!)).ToArray(), false),
            ($"input seek + {venc} -preset veryfast", inSeek.Concat(Enc(venc!, "-preset", "veryfast")).ToArray(), false),
            ($"input seek + {venc} -preset ultrafast", inSeek.Concat(Enc(venc!, "-preset", "ultrafast")).ToArray(), false),
            ($"input seek + {nvenc} -preset p4 -cq 19", inSeek.Concat(Enc(nvenc, "-preset", "p4", "-rc", "vbr", "-cq", "19", "-b:v", "0")).ToArray(), false),
            ($"-hwaccel cuda + input seek + {nvenc} p4 cq19", new[] { "-y", "-hwaccel", "cuda", "-ss", sStart, "-i", fx.Path, "-t", sDur }.Concat(Enc(nvenc, "-preset", "p4", "-rc", "vbr", "-cq", "19", "-b:v", "0")).ToArray(), false),
        };
        var variants = onlyHw ? all.Where(v => v.Engine || v.Name.StartsWith("-hwaccel", StringComparison.Ordinal)).ToList() : all;
        var expectHead = (int)Math.Round(dur.TotalSeconds * 30);
        for (var vi = 0; vi < variants.Count; vi++)
        {
            var (name, args, engine) = variants[vi];
            var head = Path.Combine(dir, $"head{vi}{Path.GetExtension(fx.Path)}");
            var runs = engine && start > 300 ? 1 : engine && start > 60 ? 2 : 3;
            var ok = true;
            var times = new List<double>();
            for (var i = 0; i < runs; i++)
            {
                double s;
                if (engine)
                {
                    var a = SmartCutArgsBuilder.HeadReencode(fx.Path, plan.Start, headEnd, info, venc!, aenc, head);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = await Svc.Ffmpeg.RunAsync(a);
                    s = sw.Elapsed.TotalSeconds;
                    ok &= r.Success;
                }
                else
                {
                    var (s1, r) = await Svc.RunFfmpeg(args.Concat(new[] { head }).ToArray());
                    s = s1;
                    ok &= r.Success;
                }

                times.Add(s);
            }

            var warm = times.Skip(1).OrderBy(x => x).ToList();
            var med = warm.Count == 0 ? times[0] : warm[warm.Count / 2];
            string eq;
            if (!ok || !File.Exists(head))
            {
                eq = "FAILED";
            }
            else
            {
                var hc = await CountPackets(head);
                var vsSrc = await PsnrArgs("-i", head, "-ss", sStart, "-t", sDur, "-i", fx.Path, "-lavfi", "[0:v][1:v]psnr");
                var tail = Path.Combine(dir, "tail" + Path.GetExtension(fx.Path));
                var final = Path.Combine(dir, "final" + Path.GetExtension(fx.Path));
                await Svc.Ffmpeg.RunAsync(SmartCutArgsBuilder.TailCopy(fx.Path, headEnd, null, tail));
                var list = Path.Combine(dir, "list.txt");
                await File.WriteAllTextAsync(list, JoinArgsBuilder.RenderConcatList(new[] { head, tail }));
                await Svc.Ffmpeg.RunAsync(JoinArgsBuilder.ConcatCopy(list, final));
                var fc = await CountPackets(final);
                var tailPsnr = await PsnrArgs("-ss", sDur, "-t", "2", "-i", final, "-ss", sHeadEnd, "-t", "2", "-i", fx.Path, "-lavfi", "[0:v][1:v]psnr");
                var (_, dr) = await Svc.RunFfmpeg("-v", "error", "-t", F(dur.TotalSeconds + 3), "-i", final, "-f", "null", "-");
                var errs = dr.StdErrTail.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                var expectFinal = (int)Math.Round((fx.Duration - start) * 30);
                eq = $"head {hc.Packets} pkts (expect {expectHead}) {F(hc.Duration)}s | head vs SOURCE [{sStart},+{sDur}) PSNR {vsSrc:0.0} dB | final {fc.Packets} pkts (expect ~{expectFinal}) {F(fc.Duration)}s | tail 2s vs source PSNR {tailPsnr:0.0} dB | joint decode: {(errs.Count == 0 ? "clean" : errs.Count + " line(s): " + errs[0])} | head {new FileInfo(head).Length / 1e6:0.0}MB";
                if (vi == 0)
                {
                    File.Copy(final, Path.Combine(P.Work, $"smart2-final-current-{fx.Key}-{start.ToString(CultureInfo.InvariantCulture)}.mp4"), true);
                }

                Io.TryDelete(tail);
                Io.TryDelete(final);
            }

            R.Add("alt-smart2", $"exact head @{start}s: {name}", fx.Key, "warm", runs, med, $"runs {string.Join(" ", times.Select(F))} | {eq}");
        }

        Io.TryDelete(dir);
    }
}
