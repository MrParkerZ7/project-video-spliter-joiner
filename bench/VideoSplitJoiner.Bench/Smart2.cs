using System.Globalization;
using System.Text.Json;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.Bench;

/// <summary>
/// Exact-cut (SmartCutEngine head re-encode) alternatives, lighter checks than Alternatives.SmartVariants so the
/// whole matrix fits: frame-exactness is judged against the SOURCE (head vs source [start,start+dur) PSNR, and the
/// copied tail of the final file vs the source after the boundary keyframe), packet counts instead of full decodes,
/// the joint's decode errors (the T-189 HEVC finding) and the tail's stream start times (the T-190 80 ms finding).
/// Ported from the prototype. usage: <c>bench alt smart2 [4k104|hevc10|hevc44|4k10cuda|g10|4k10m540]</c>
/// </summary>
internal static class Smart2
{
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static async Task<(int Packets, double Duration)> CountPackets(BenchContext c, string path)
    {
        var (_, o) = await c.RunFfprobe("-v", "error", "-count_packets", "-select_streams", "v:0", "-show_entries", "stream=nb_read_packets:format=duration", "-of", "json", "-i", path);
        using var doc = JsonDocument.Parse(o);
        var n = int.Parse(doc.RootElement.GetProperty("streams")[0].GetProperty("nb_read_packets").GetString()!, CultureInfo.InvariantCulture);
        var d = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return (n, d);
    }

    private static async Task<string> StreamStarts(BenchContext c, string path)
    {
        var (_, o) = await c.RunFfprobe("-v", "error", "-show_entries", "stream=codec_type,start_time", "-of", "json", "-i", path);
        using var doc = JsonDocument.Parse(o);
        return string.Join(" ", doc.RootElement.GetProperty("streams").EnumerateArray()
            .Select(s => $"{(s.TryGetProperty("codec_type", out var t) ? t.GetString()?[..1] : "?")}={(s.TryGetProperty("start_time", out var st) ? st.GetString() : "n/a")}"));
    }

    public static async Task Run(BenchContext c, Func<string, Fixture?> get, List<string> which)
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
        foreach (var cs in cases.Where(x => which.Count == 0 || which.Contains(x.Id)))
        {
            var fx = get(cs.Fx);
            if (fx is null)
            {
                c.Results.Note($"smart2 {cs.Id}: fixture '{cs.Fx}' is not valid under this root — run bench fixtures {cs.Fx}");
                continue;
            }

            await One(c, fx, cs.Start, cs.OnlyHw);
        }
    }

    private static async Task One(BenchContext c, Fixture fx, double start, bool onlyHw)
    {
        var probe = c.NewProbe();
        if (await probe.ProbeAsync(fx.Path) is not ProbeResult.ProbeSucceeded ok)
        {
            c.Results.Note($"smart2 {fx.Key}: probe failed");
            return;
        }

        var info = ok.Info;
        var kfs = await probe.GetKeyframesAsync(fx.Path);
        if (!SmartCutArgsBuilder.TryResolveEncoders(info, out var venc, out var aenc, out var why))
        {
            c.Results.Note($"smart2 {fx.Key}: no encoder ({why})");
            return;
        }

        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(start), null, kfs);
        if (plan.HeadEnd is null)
        {
            c.Results.Note($"smart2 {fx.Key}@{start}: no head to re-encode ({plan.Strategy})");
            return;
        }

        var headEnd = plan.HeadEnd.Value;
        var dur = headEnd - plan.Start;
        var sStart = SplitPlanner.ToFfmpegSeconds(plan.Start);
        var sDur = SplitPlanner.ToFfmpegSeconds(dur);
        var sHeadEnd = SplitPlanner.ToFfmpegSeconds(headEnd);
        var dir = c.NewTempDir("smart2");
        var span = new SmartHeads.Span(info, plan.Start, headEnd, venc!, aenc);
        var all = new List<(string Name, string[] Args, bool Engine)>
        {
            ($"current engine args (SmartCutArgsBuilder.HeadReencode: -i in -ss {sStart}, {venc} default preset)", Array.Empty<string>(), true),
        };
        all.AddRange(SmartHeads.Variants(fx, span).Select(v => (v.Name, v.Args, false)));
        var variants = onlyHw ? all.Where(v => v.Engine || v.Name.StartsWith("-hwaccel", StringComparison.Ordinal)).ToList() : all;
        var fps = 30.0;
        var expectHead = (int)Math.Round(dur.TotalSeconds * fps);
        try
        {
            for (var vi = 0; vi < variants.Count; vi++)
            {
                var (name, args, engine) = variants[vi];
                var head = Path.Combine(dir, $"head{vi}{Path.GetExtension(fx.Path)}");
                var runs = engine && start > 300 ? 1 : engine && start > 60 ? 2 : 3;
                var ok2 = true;
                var times = new List<(double S, Usage U)>();
                for (var i = 0; i < runs; i++)
                {
                    if (engine)
                    {
                        var a = SmartCutArgsBuilder.HeadReencode(fx.Path, plan.Start, headEnd, info, venc!, aenc, head);
                        var (s, u) = await Meter.Run(async () => ok2 &= (await c.Ffmpeg.RunAsync(a)).Success);
                        times.Add((s, u));
                    }
                    else
                    {
                        var (s, u) = await Meter.Run(async () => ok2 &= (await c.RunFfmpeg(args.Concat(new[] { head }).ToArray())).R.Success);
                        times.Add((s, u));
                    }
                }

                var warm = times.Skip(1).OrderBy(x => x.S).ToList();
                var med = warm.Count == 0 ? times[0] : warm[warm.Count / 2];
                string eq;
                if (!ok2 || !File.Exists(head))
                {
                    eq = "FAILED";
                }
                else
                {
                    var hc = await CountPackets(c, head);
                    var vsSrc = DPsnr(await c.RunFfmpeg("-i", head, "-ss", sStart, "-t", sDur, "-i", fx.Path, "-lavfi", "[0:v][1:v]psnr", "-f", "null", "-"));
                    var tail = Path.Combine(dir, "tail" + Path.GetExtension(fx.Path));
                    var final = Path.Combine(dir, "final" + Path.GetExtension(fx.Path));
                    await c.Ffmpeg.RunAsync(SmartCutArgsBuilder.TailCopy(fx.Path, headEnd, null, tail));
                    var tailStarts = await StreamStarts(c, tail);
                    var list = Path.Combine(dir, "list.txt");
                    await File.WriteAllTextAsync(list, JoinArgsBuilder.RenderConcatList(new[] { head, tail }));
                    await c.Ffmpeg.RunAsync(JoinArgsBuilder.ConcatCopy(list, final));
                    var fc = await CountPackets(c, final);
                    var tailPsnr = DPsnr(await c.RunFfmpeg("-ss", sDur, "-t", "2", "-i", final, "-ss", sHeadEnd, "-t", "2", "-i", fx.Path, "-lavfi", "[0:v][1:v]psnr", "-f", "null", "-"));
                    var (_, dr) = await c.RunFfmpeg("-v", "error", "-t", F(dur.TotalSeconds + 3), "-i", final, "-f", "null", "-");
                    var errs = dr.StdErrTail.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                    var expectFinal = (int)Math.Round((fx.Duration - start) * fps);
                    eq = $"head {hc.Packets} pkts (expect {expectHead}) {F(hc.Duration)}s | head vs SOURCE [{sStart},+{sDur}) PSNR {vsSrc:0.0} dB | final {fc.Packets} pkts (expect ~{expectFinal}) {F(fc.Duration)}s | tail 2s vs source PSNR {tailPsnr:0.0} dB | tail starts {tailStarts} | joint decode: {(errs.Count == 0 ? "clean" : errs.Count + " line(s): " + errs[0])} | head {new FileInfo(head).Length / 1e6:0.0}MB";
                    if (vi == 0)
                    {
                        File.Copy(final, Path.Combine(c.Work, $"smart2-final-current-{fx.Key}-{start.ToString(CultureInfo.InvariantCulture)}{Path.GetExtension(fx.Path)}"), true);
                    }

                    Io.TryDelete(tail);
                    Io.TryDelete(final);
                }

                c.Results.Add("alt-smart2", $"exact head @{start}s: {name}", fx.Key, "warm", runs, med.S, med.U,
                    $"runs {string.Join(" ", times.Select(t => F(t.S)))} | {eq}");
            }
        }
        finally
        {
            Io.TryDelete(dir);
        }
    }

    private static double DPsnr((double S, VideoSplitJoiner.Core.Ffmpeg.FfmpegResult R) run) => BenchContext.ParsePsnr(run.R);
}
