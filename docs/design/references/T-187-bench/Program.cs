using System.Diagnostics;
using System.Text;
using VideoSplitJoiner.Core.Bulk;

namespace T187Bench;

/// <summary>
/// T-187 4K performance harness. Usage:
///   T187Bench floor                 IO floor (cold/warm read, file copy, ffmpeg -c copy of the whole 4K file)
///   T187Bench core [keys] [-s sec]  core scenarios on fixtures (keys: 4k 1080 hevc g10; sections: probe kf wf thumb load split join bulk smart)
///   T187Bench alt [sections]        ffmpeg/ffprobe-level alternatives for the 4K hotspots (see Alternatives.cs)
/// Results append to results/timings.jsonl (+ notes.txt); console echoes every line.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(P.Work);
        var mode = args.Length > 0 ? args[0] : "core";
        var rest = args.Skip(1).ToList();
        R.Note($"run: {string.Join(' ', args)}");
        var sw = Stopwatch.StartNew();
        switch (mode)
        {
            case "floor":
                await Floor();
                break;
            case "core":
                await Core(rest);
                break;
            case "alt":
                await Alternatives.Run(rest);
                break;
            default:
                Console.Error.WriteLine("unknown mode");
                return 2;
        }

        R.Note($"done {mode} in {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }

    private static async Task Floor()
    {
        foreach (var fx in new[] { Fixture.Get("4k"), Fixture.Get("1080") })
        {
            var len = new FileInfo(fx.Path).Length;
            for (var i = 1; i <= 2; i++)
            {
                var sw = Stopwatch.StartNew();
                var cold = Io.ColdCopy(fx.Path, fx.Key);
                R.Add("floor", "unbuffered write-through copy (real disk write)", fx.Key, "-", i, sw.Elapsed.TotalSeconds, Io.Mb(len));
                var c = Io.ReadAllSeconds(cold);
                R.Add("floor", "sequential read, cold (from SSD)", fx.Key, "cold", i, c, $"{len / c / 1e6:0} MB/s");
                var w = Io.ReadAllSeconds(cold);
                R.Add("floor", "sequential read, warm (page cache)", fx.Key, "warm", i, w, $"{len / w / 1e6:0} MB/s");
                Io.TryDelete(cold);
            }

            for (var i = 1; i <= 2; i++)
            {
                var cold = Io.ColdCopy(fx.Path, fx.Key);
                var dst = Path.Combine(P.Work, $"filecopy-{fx.Key}.mp4");
                Io.TryDelete(dst);
                var sw = Stopwatch.StartNew();
                File.Copy(cold, dst, true);
                R.Add("floor", "File.Copy cold source (plain copy, cached write)", fx.Key, "cold", i, sw.Elapsed.TotalSeconds);
                Io.TryDelete(dst);
                sw.Restart();
                File.Copy(cold, dst, true);
                R.Add("floor", "File.Copy warm source (plain copy, cached write)", fx.Key, "warm", i, sw.Elapsed.TotalSeconds);
                Io.TryDelete(dst);

                var outp = Path.Combine(P.Work, $"ffcopy-{fx.Key}.mp4");
                var (s1, r1) = await Svc.RunFfmpeg("-y", "-i", fx.Path, "-map", "0", "-c", "copy", outp);
                R.Add("floor", "ffmpeg -i in -map 0 -c copy out.mp4 (whole file)", fx.Key, "warm", i, s1, $"exit {r1.ExitCode}");
                var (s2, r2) = await Svc.RunFfmpeg("-y", "-i", cold, "-map", "0", "-c", "copy", outp);
                R.Add("floor", "ffmpeg -i in -map 0 -c copy out.mp4 (whole file)", fx.Key, "cold", i, s2, $"exit {r2.ExitCode}");
                var (s3, r3) = await Svc.RunFfmpeg("-y", "-i", fx.Path, "-map", "0", "-c", "copy", "-f", "null", "-");
                R.Add("floor", "ffmpeg -i in -map 0 -c copy -f null - (demux only, no write)", fx.Key, "warm", i, s3, $"exit {r3.ExitCode}");
                Io.TryDelete(outp);
                Io.TryDelete(cold);
            }
        }
    }

    private static async Task Core(List<string> rest)
    {
        var keys = rest.Where(a => Fixture.All.Any(f => f.Key == a)).ToList();
        if (keys.Count == 0)
        {
            keys = Fixture.All.Select(f => f.Key).ToList();
        }

        var sections = rest.Where(a => !keys.Contains(a)).ToHashSet();
        bool On(string s) => sections.Count == 0 || sections.Contains(s);
        const int Runs = 3;

        foreach (var key in keys)
        {
            var fx = Fixture.Get(key);
            Console.WriteLine($"===== {fx.Key} {fx.File} {Io.Mb(new FileInfo(fx.Path).Length)}");
            if (On("probe"))
            {
                await CoreScenarios.Probe(fx, Runs + 1);
            }

            if (On("kf"))
            {
                await CoreScenarios.Keyframes(fx, Runs);
            }

            if (On("wf"))
            {
                await CoreScenarios.Waveform(fx, Runs);
            }

            if (On("thumb"))
            {
                await CoreScenarios.Thumbnails(fx, Runs);
            }

            if (On("load"))
            {
                await CoreScenarios.SplitLoad(fx, Runs);
            }

            IReadOnlyList<string> parts = Array.Empty<string>();
            if (On("split") || On("join"))
            {
                parts = await CoreScenarios.SplitExport(fx, On("split") ? Runs : 1);
            }

            if (On("join"))
            {
                await CoreScenarios.Join(fx, parts, Runs);
            }

            if (On("bulk"))
            {
                // One file, and three distinct files (copies, so each has its own path/cache key like real rows).
                var copies = new List<string> { fx.Path };
                for (var k = 2; k <= 3; k++)
                {
                    var c = Path.Combine(P.Work, $"bulkcopy{k}-{fx.File}");
                    if (!File.Exists(c))
                    {
                        File.Copy(fx.Path, c);
                    }

                    copies.Add(c);
                }

                await CoreScenarios.BulkAdd(fx, new[] { fx.Path }, "warm");
                await CoreScenarios.BulkAdd(fx, copies, "warm");
                var coldRows = copies.Select((c, k) => Io.ColdCopy(c, $"{fx.Key}-row{k + 1}")).ToList();
                try
                {
                    await CoreScenarios.BulkAdd(fx, coldRows, "cold");
                }
                finally
                {
                    coldRows.ForEach(Io.TryDelete);
                }

                for (var i = 1; i <= 2; i++)
                {
                    await CoreScenarios.BulkRun(fx, new[] { fx.Path }, CutPrecision.Lossless, "warm", i);
                }

                await CoreScenarios.BulkRun(fx, copies, CutPrecision.Lossless, "warm", 1);
                for (var i = 1; i <= 2; i++)
                {
                    await CoreScenarios.BulkRun(fx, new[] { fx.Path }, CutPrecision.Exact, "warm", i);
                }

                await CoreScenarios.BulkRun(fx, copies, CutPrecision.Exact, "warm", 1);
            }

            if (On("smart"))
            {
                await CoreScenarios.SmartCutSteps(fx, fx.Intro, fx.Outro, $"intro@{fx.Intro}s");
                var late = fx.Duration - 15.5;
                await CoreScenarios.SmartCutSteps(fx, late, null, $"intro@{late}s");
            }
        }
    }
}
