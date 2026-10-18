using System.Diagnostics;
using System.Globalization;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Thumbnails;
using VideoSplitJoiner.Core.Waveform;

namespace T187Bench;

/// <summary>
/// Drives the REAL Core services with the same call order + arguments the view models use (file:line refs are to
/// D:/Programing/Projects/project-video-spliter-joiner/src/...).
/// </summary>
internal static class CoreScenarios
{
    private const int WaveformBuckets = 1800;          // App/ViewModels/SplitViewModel.cs:46
    private const int HoverWidth = 160;                // App/ViewModels/ThumbnailPreviewViewModel.cs:33
    private const int ChipWidth = 64;                  // App/ViewModels/BulkItemViewModel.cs:88
    private const int ProfileWidth = 640;              // App/ViewModels/BulkCutViewModel.cs:153

    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    private static async Task<double> T(Func<Task> f)
    {
        var sw = Stopwatch.StartNew();
        await f().ConfigureAwait(false);
        return sw.Elapsed.TotalSeconds;
    }

    // ---------------------------------------------------------------- shared: probe / keyframes / waveform / thumbs

    public static async Task Probe(Fixture fx, int runs)
    {
        for (var i = 1; i <= runs; i++)
        {
            var probe = Svc.NewProbe();
            ProbeResult? r = null;
            var s = await T(async () => r = await probe.ProbeAsync(fx.Path));
            R.Add("shared", "MediaProbe.ProbeAsync", fx.Key, "warm", i, s, r is ProbeResult.ProbeSucceeded ? null : "FAILED");
        }

        var cold = Io.ColdCopy(fx.Path, fx.Key);
        try
        {
            var probe = Svc.NewProbe();
            var s = await T(() => probe.ProbeAsync(cold));
            R.Add("shared", "MediaProbe.ProbeAsync", fx.Key, "cold", 1, s);
        }
        finally
        {
            Io.TryDelete(cold);
        }
    }

    public static async Task Keyframes(Fixture fx, int runs)
    {
        for (var i = 1; i <= runs; i++)
        {
            var probe = Svc.NewProbe();
            IReadOnlyList<TimeSpan>? kf = null;
            var s = await T(async () => kf = await probe.GetKeyframesAsync(fx.Path));
            R.Add("shared", "MediaProbe.GetKeyframesAsync (packet scan)", fx.Key, "warm", i, s, $"{kf!.Count} keyframes");
        }

        // Breakdown: the ffprobe process alone (same args as MediaProbe.cs:253-259) vs the whole call.
        var (ps, json) = await Svc.RunFfprobe("-select_streams", "v:0", "-show_packets",
            "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path);
        R.Add("shared", "  ffprobe -show_packets json (process only)", fx.Key, "warm", 1, ps, $"stdout {json.Length / 1024.0:0}KB");

        var cold = Io.ColdCopy(fx.Path, fx.Key);
        try
        {
            var probe = Svc.NewProbe();
            var s = await T(() => probe.GetKeyframesAsync(cold));
            R.Add("shared", "MediaProbe.GetKeyframesAsync (packet scan)", fx.Key, "cold", 1, s);
        }
        finally
        {
            Io.TryDelete(cold);
        }
    }

    public static async Task Waveform(Fixture fx, int runs)
    {
        for (var i = 1; i <= runs; i++)
        {
            var svc = new FfmpegWaveformService(Svc.Ffmpeg, Svc.NewTempDir("wf"));
            float[]? peaks = null;
            var s = await T(async () => peaks = await svc.GetPeaksAsync(fx.Path, WaveformBuckets, CancellationToken.None));
            R.Add("split", "FfmpegWaveformService.GetPeaksAsync(1800)", fx.Key, "warm", i, s, peaks is null ? "NULL" : $"{peaks.Length} peaks");
        }

        var cold = Io.ColdCopy(fx.Path, fx.Key);
        try
        {
            var svc = new FfmpegWaveformService(Svc.Ffmpeg, Svc.NewTempDir("wf"));
            var s = await T(() => svc.GetPeaksAsync(cold, WaveformBuckets, CancellationToken.None));
            R.Add("split", "FfmpegWaveformService.GetPeaksAsync(1800)", fx.Key, "cold", 1, s);
        }
        finally
        {
            Io.TryDelete(cold);
        }
    }

    public static async Task Thumbnails(Fixture fx, int runs)
    {
        foreach (var (label, width) in new[] { ("hover", HoverWidth), ("bulk chip", ChipWidth), ("profile", ProfileWidth) })
        {
            foreach (var (where, t) in new[] { ("on keyframe", fx.ThumbOnKf), ($"{fx.ThumbMidGop - fx.ThumbOnKf:0}s into GOP", fx.ThumbMidGop) })
            {
                for (var i = 1; i <= runs; i++)
                {
                    var svc = new FfmpegThumbnailService(Svc.Ffmpeg, Svc.NewTempDir("th"));
                    string? path = null;
                    var s = await T(async () => path = await svc.GetThumbnailAsync(fx.Path, S(t), width, CancellationToken.None));
                    R.Add("shared", $"Thumbnail {label} {width}px @{t}s ({where})", fx.Key, "warm", i, s, path is null ? "NULL" : null);
                }
            }
        }

        // Cache hit (same bucket, same width): what a repeated hover costs.
        {
            var svc = new FfmpegThumbnailService(Svc.Ffmpeg, Svc.NewTempDir("th"));
            await svc.GetThumbnailAsync(fx.Path, S(fx.ThumbMidGop), HoverWidth, CancellationToken.None);
            var s = await T(() => svc.GetThumbnailAsync(fx.Path, S(fx.ThumbMidGop), HoverWidth, CancellationToken.None));
            R.Add("shared", "Thumbnail hover 160px cache HIT", fx.Key, "warm", 1, s);
        }

        // A hover sweep: 10 distinct 1-s buckets in a row (each a new ffmpeg process, sequential like latest-wins).
        {
            var svc = new FfmpegThumbnailService(Svc.Ffmpeg, Svc.NewTempDir("th"));
            var start = Math.Max(1, fx.ThumbOnKf - 5);
            var s = await T(async () =>
            {
                for (var k = 0; k < 10; k++)
                {
                    await svc.GetThumbnailAsync(fx.Path, S(start + k + 0.4), HoverWidth, CancellationToken.None);
                }
            });
            R.Add("split", "Hover sweep: 10 new buckets, sequential (total)", fx.Key, "warm", 1, s, $"{s / 10:0.000}s per grab");
        }

        var cold = Io.ColdCopy(fx.Path, fx.Key);
        try
        {
            var svc = new FfmpegThumbnailService(Svc.Ffmpeg, Svc.NewTempDir("th"));
            var s = await T(() => svc.GetThumbnailAsync(cold, S(fx.ThumbMidGop), HoverWidth, CancellationToken.None));
            R.Add("shared", $"Thumbnail hover 160px @{fx.ThumbMidGop}s", fx.Key, "cold", 1, s);
        }
        finally
        {
            Io.TryDelete(cold);
        }
    }

    // ---------------------------------------------------------------- Split tab

    /// <summary>
    /// SplitViewModel.LoadAsync (SplitViewModel.cs:894-972): await ProbeAsync (the only gate) → Player.Open (FFME,
    /// not reproducible here) → StartKeyframeIndex (997) and StartWaveformExtraction (1134) in parallel, background.
    /// </summary>
    public static async Task SplitLoad(Fixture fx, int runs)
    {
        async Task One(string path, string mode, int run)
        {
            var probe = Svc.NewProbe();
            var wf = new FfmpegWaveformService(Svc.Ffmpeg, Svc.NewTempDir("wf"));
            var sw = Stopwatch.StartNew();
            var pr = await probe.ProbeAsync(path);
            var tProbe = sw.Elapsed.TotalSeconds;
            var kfTask = probe.GetKeyframesAsync(path, CancellationToken.None);
            var wfTask = wf.GetPeaksAsync(path, WaveformBuckets, CancellationToken.None);
            var tk = kfTask.ContinueWith(_ => sw.Elapsed.TotalSeconds, TaskScheduler.Default);
            var tw = wfTask.ContinueWith(_ => sw.Elapsed.TotalSeconds, TaskScheduler.Default);
            await Task.WhenAll(tk, tw);
            R.Add("split", "LOAD: probe done (info + preview can open)", fx.Key, mode, run, tProbe);
            R.Add("split", "LOAD: keyframes ready (cuts snap / Split enabled)", fx.Key, mode, run, tk.Result);
            R.Add("split", "LOAD: waveform drawn", fx.Key, mode, run, tw.Result);
            R.Add("split", "LOAD: everything settled", fx.Key, mode, run, Math.Max(tk.Result, tw.Result), pr is ProbeResult.ProbeSucceeded ? null : "PROBE FAILED");
        }

        for (var i = 1; i <= runs; i++)
        {
            await One(fx.Path, "warm", i);
        }

        var cold = Io.ColdCopy(fx.Path, fx.Key);
        try
        {
            await One(cold, "cold", 1);
        }
        finally
        {
            Io.TryDelete(cold);
        }
    }

    /// <summary>
    /// SplitViewModel.RunSplitAsync (SplitViewModel.cs:1360-1412): all parts selected → null selection → segment muxer.
    /// The probe's keyframe cache is warm (the load already scanned) unless mode == cold.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SplitExport(Fixture fx, int runs)
    {
        var outDir = Path.Combine(P.Work, "split-" + fx.Key);
        Directory.CreateDirectory(outDir);
        IReadOnlyList<string> produced = Array.Empty<string>();

        async Task<SplitResult> One(string path, MediaProbe probe, IReadOnlyList<int>? selection, string mode, int run, string label)
        {
            var eng = new SplitEngine(Svc.Ffmpeg, probe, Svc.Log);
            var req = new SplitRequest(path, fx.Cuts.Select(S).ToList(), outDir, SplitRequest.DefaultNamingPattern, Overwrite: true, selection);
            var stages = new List<(string Stage, double T)>();
            var sw = Stopwatch.StartNew();
            var reports = 0;
            var res = await eng.SplitAsync(
                req,
                new SyncProgress<double>(_ => reports++),
                CancellationToken.None,
                new SyncProgress<OperationStatus>(st => stages.Add((st.Stage, sw.Elapsed.TotalSeconds))),
                new SyncProgress<PartProgress>(_ => { }));
            var total = sw.Elapsed.TotalSeconds;
            double At(string st) => stages.FirstOrDefault(x => x.Stage == st).T;
            var prep = At("Splitting") - At("Preparing");
            var ffm = At("Finalizing") - At("Splitting");
            var fin = At("Done") - At("Finalizing");
            var bytes = res.Segments.Sum(sg => new FileInfo(sg.Path).Length);
            R.Add("split", label, fx.Key, mode, run, total,
                $"prepare {prep:0.000}s | ffmpeg {ffm:0.000}s | finalize {fin:0.000}s | {res.Segments.Count} parts {Io.Mb(bytes)} | {reports} progress ticks");
            return res;
        }

        for (var i = 1; i <= runs; i++)
        {
            var probe = Svc.NewProbe();
            await probe.GetKeyframesAsync(fx.Path); // the load already did this in the app
            var res = await One(fx.Path, probe, null, "warm", i, "SplitEngine.SplitAsync 3 parts (segment muxer)");
            produced = res.Segments.Select(sg => sg.Path).ToList();
        }

        for (var i = 1; i <= runs; i++)
        {
            var probe = Svc.NewProbe();
            await probe.GetKeyframesAsync(fx.Path);
            await One(fx.Path, probe, new[] { 2 }, "warm", i, "SplitEngine.SplitAsync part 2 only (per-segment)");
        }

        // Cold source + cold probe (no keyframe cache): a split right after picking a file that was never cached.
        var cold = Io.ColdCopy(fx.Path, fx.Key);
        var coldOut = Path.Combine(P.Work, "split-cold-" + fx.Key);
        try
        {
            var probe = Svc.NewProbe();
            var eng = new SplitEngine(Svc.Ffmpeg, probe, Svc.Log);
            var req = new SplitRequest(cold, fx.Cuts.Select(S).ToList(), coldOut, SplitRequest.DefaultNamingPattern, Overwrite: true);
            var s = await T(() => eng.SplitAsync(req));
            R.Add("split", "SplitEngine.SplitAsync 3 parts, cold file + cold keyframe cache", fx.Key, "cold", 1, s);
        }
        finally
        {
            Io.TryDelete(cold);
            Io.TryDelete(coldOut);
        }

        return produced;
    }

    // ---------------------------------------------------------------- Join tab

    public static async Task Join(Fixture fx, IReadOnlyList<string> parts, int runs)
    {
        var outPath = Path.Combine(P.Work, $"join-{fx.Key}.mp4");

        // JoinViewModel.AddFilesAsync (JoinViewModel.cs:283-328): probe each new clip for the chip, then RefreshCompatAsync
        // → JoinEngine.CheckCompatibilityAsync probes EVERY clip again (JoinEngine.cs:83-96).
        for (var i = 1; i <= runs; i++)
        {
            var probe = Svc.NewProbe();
            var eng = new JoinEngine(Svc.Ffmpeg, probe, Svc.Log);
            var s = await T(async () =>
            {
                foreach (var p in parts)
                {
                    await probe.ProbeAsync(p);
                }

                await eng.CheckCompatibilityAsync(parts);
            });
            R.Add("join", "ADD 3 clips at once (3 chip probes + compat = 6 probes)", fx.Key, "warm", i, s);
        }

        // Adding the clips one at a time: add #k = 1 chip probe + (k>=2 ? compat over k clips : 0).
        {
            var probe = Svc.NewProbe();
            var eng = new JoinEngine(Svc.Ffmpeg, probe, Svc.Log);
            var list = new List<string>();
            foreach (var p in parts)
            {
                list.Add(p);
                var s = await T(async () =>
                {
                    await probe.ProbeAsync(p);
                    if (list.Count >= 2)
                    {
                        await eng.CheckCompatibilityAsync(list.ToList());
                    }
                });
                R.Add("join", $"ADD clip #{list.Count} (one at a time)", fx.Key, "warm", 1, s);
            }
        }

        // JoinViewModel run → JoinEngine.JoinAsync: compat (N probes) + SumDurations (N probes) + concat -c copy + move.
        for (var i = 1; i <= runs; i++)
        {
            var eng = new JoinEngine(Svc.Ffmpeg, Svc.NewProbe(), Svc.Log);
            var stages = new List<(string Stage, double T)>();
            var sw = Stopwatch.StartNew();
            var res = await eng.JoinAsync(new JoinRequest(parts, outPath, Overwrite: true), new SyncProgress<double>(_ => { }),
                CancellationToken.None, new SyncProgress<OperationStatus>(st => stages.Add((st.Stage, sw.Elapsed.TotalSeconds))));
            var total = sw.Elapsed.TotalSeconds;
            double At(string st) => stages.FirstOrDefault(x => x.Stage == st).T;
            R.Add("join", "JoinEngine.JoinAsync 3 parts", fx.Key, "warm", i, total,
                $"compat+probes {At("Joining") - At("Checking compatibility"):0.000}s | ffmpeg concat {At("Finalizing") - At("Joining"):0.000}s | finalize {At("Done") - At("Finalizing"):0.000}s | {(res.Success ? Io.Mb(new FileInfo(outPath).Length) : "REFUSED " + res.Refusal?.Mismatches.FirstOrDefault()?.Detail)}");
        }

        // Cold: parts copied cold.
        var coldParts = parts.Select((p, k) => Io.ColdCopy(p, $"{fx.Key}-part{k + 1}")).ToList();
        try
        {
            var eng = new JoinEngine(Svc.Ffmpeg, Svc.NewProbe(), Svc.Log);
            var s = await T(() => eng.JoinAsync(new JoinRequest(coldParts, outPath, Overwrite: true)));
            R.Add("join", "JoinEngine.JoinAsync 3 parts", fx.Key, "cold", 1, s);
        }
        finally
        {
            foreach (var c in coldParts)
            {
                Io.TryDelete(c);
            }
        }
    }

    // ---------------------------------------------------------------- Bulk Cut tab

    /// <summary>
    /// BulkCutViewModel.AddFilesAsync (BulkCutViewModel.cs:1387-1424): probe each row (awaited, sequential) →
    /// row.StartKeyframeScanAsync under a 3-wide gate (BulkItemViewModel.cs:754-761, gate BulkCutViewModel.cs:173) →
    /// on scan, intro + outro chips at 64px under a 3-wide thumbnail gate (BulkCutViewModel.cs:178; 200 ms debounce
    /// BulkItemViewModel.cs:95 NOT included here).
    /// </summary>
    public static async Task BulkAdd(Fixture fx, IReadOnlyList<string> files, string mode)
    {
        var probe = Svc.NewProbe();
        var thumbs = new FfmpegThumbnailService(Svc.Ffmpeg, Svc.NewTempDir("th"));
        var scanGate = new SemaphoreSlim(3, 3);
        var thumbGate = new SemaphoreSlim(3, 3);
        var sw = Stopwatch.StartNew();
        var scanTasks = new List<Task<double>>();
        foreach (var f in files)
        {
            await probe.ProbeAsync(f);
            scanTasks.Add(Task.Run(async () =>
            {
                await scanGate.WaitAsync();
                try
                {
                    await probe.GetKeyframesAsync(f);
                }
                finally
                {
                    scanGate.Release();
                }

                var tScan = sw.Elapsed.TotalSeconds;
                async Task Grab(double t)
                {
                    await thumbGate.WaitAsync();
                    try
                    {
                        await thumbs.GetThumbnailAsync(f, S(t), ChipWidth, CancellationToken.None);
                    }
                    finally
                    {
                        thumbGate.Release();
                    }
                }

                await Task.WhenAll(Grab(fx.Intro), Grab(fx.Outro));
                return tScan;
            }));
        }

        var tProbed = sw.Elapsed.TotalSeconds;
        var scans = await Task.WhenAll(scanTasks);
        var tAll = sw.Elapsed.TotalSeconds;
        R.Add("bulk", $"ADD {files.Count} row(s): all probed (rows show duration)", fx.Key, mode, 1, tProbed);
        R.Add("bulk", $"ADD {files.Count} row(s): all keyframe scans done (rows runnable)", fx.Key, mode, 1, scans.Max());
        R.Add("bulk", $"ADD {files.Count} row(s): all intro/outro chips drawn", fx.Key, mode, 1, tAll);
    }

    /// <summary>BulkCutViewModel run (BulkCutViewModel.cs:2737-2750) → BulkTrimEngine.RunAsync. Keyframes warm (row add scanned).</summary>
    public static async Task BulkRun(Fixture fx, IReadOnlyList<string> files, CutPrecision precision, string mode, int run)
    {
        var probe = Svc.NewProbe();
        foreach (var f in files)
        {
            await probe.GetKeyframesAsync(f);
        }

        var split = new SplitEngine(Svc.Ffmpeg, probe, Svc.Log);
        var smart = new SmartCutEngine(Svc.Ffmpeg, probe);
        var eng = new BulkTrimEngine(split, new KeptMiddleRequestBuilder(probe), smart);
        var outDir = Path.Combine(P.Work, "bulk-out");
        Directory.CreateDirectory(outDir);
        var items = files.Select(f => new BulkTrimItem(f, S(fx.Intro), S(fx.Outro),
            Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "_trimmed" + Path.GetExtension(f)))).ToList();
        var opts = new BulkTrimOptions(CollisionPolicy.Overwrite, OutputMode.NewFile, precision);
        BatchResult? batch = null;
        var s = await T(async () => batch = await eng.RunAsync(items, opts, new SyncProgress<BulkTrimProgress>(_ => { })));
        var warn = string.Join(" / ", batch!.Items.SelectMany(it => it.Warnings).Distinct());
        R.Add("bulk", $"BulkTrimEngine.RunAsync {precision} x{files.Count} (intro {fx.Intro}s, outro {fx.Outro}s)", fx.Key, mode, run, s,
            $"{batch.Outcome} done={batch.DoneCount} failed={batch.FailedCount} {warn} {string.Join(";", batch.Items.Where(x => x.Error is not null).Select(x => x.Error!.Message))}");
    }

    /// <summary>SmartCutEngine.CutAsync broken into its three ffmpeg runs, using the engine's own public builders.</summary>
    public static async Task SmartCutSteps(Fixture fx, double start, double? end, string tag)
    {
        var probe = Svc.NewProbe();
        var info = ((ProbeResult.ProbeSucceeded)await probe.ProbeAsync(fx.Path)).Info;
        var kf = await probe.GetKeyframesAsync(fx.Path);
        var plan = SmartCutPlanner.Plan(S(start), end is { } e ? S(e) : null, kf);
        SmartCutArgsBuilder.TryResolveEncoders(info, out var v, out var a, out _);
        var dir = Svc.NewTempDir("smart");
        var ext = Path.GetExtension(fx.Path);
        var head = Path.Combine(dir, "head" + ext);
        var tail = Path.Combine(dir, "tail" + ext);
        var final = Path.Combine(dir, "final" + ext);
        var headEnd = plan.HeadEnd ?? plan.End ?? info.Duration;
        var headArgs = SmartCutArgsBuilder.HeadReencode(fx.Path, plan.Start, headEnd, info, v!, a, head);
        var sw = Stopwatch.StartNew();
        var hr = await Svc.Ffmpeg.RunAsync(headArgs);
        var tHead = sw.Elapsed.TotalSeconds;
        R.Add("bulk", $"  Exact {tag}: head re-encode [{start}s..{headEnd.TotalSeconds}s] ({v}, output -ss)", fx.Key, "warm", 1, tHead,
            $"exit {hr.ExitCode}; args: {string.Join(' ', headArgs.ToList().Skip(2)).Replace(fx.Path, "<in>").Replace(head, "<head>")}");
        if (plan.HeadEnd is null)
        {
            return;
        }

        var tailArgs = SmartCutArgsBuilder.TailCopy(fx.Path, plan.HeadEnd.Value, plan.End, tail);
        sw.Restart();
        var tr = await Svc.Ffmpeg.RunAsync(tailArgs);
        R.Add("bulk", $"  Exact {tag}: tail -c copy [{plan.HeadEnd.Value.TotalSeconds}s..{end}]", fx.Key, "warm", 1, sw.Elapsed.TotalSeconds, $"exit {tr.ExitCode}");
        var list = Path.Combine(dir, "concat.txt");
        await File.WriteAllTextAsync(list, JoinArgsBuilder.RenderConcatList(new[] { head, tail }));
        sw.Restart();
        var cr = await Svc.Ffmpeg.RunAsync(JoinArgsBuilder.ConcatCopy(list, final));
        R.Add("bulk", $"  Exact {tag}: concat head+tail -c copy", fx.Key, "warm", 1, sw.Elapsed.TotalSeconds, $"exit {cr.ExitCode}");
        Io.TryDelete(dir);
    }
}
