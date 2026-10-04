using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Thumbnails;
using VideoSplitJoiner.Core.Waveform;

namespace VideoSplitJoiner.Bench;

/// <summary>
/// Drives the REAL Core services with the same call order and arguments the view models use (file:line refs
/// are to src/ as of the T-187 measurement). Ported from the prototype's CoreScenarios: every result line now
/// carries the job's CPU time, bytes read and written and processes started for the measured run, plus the
/// machine-load sample, and every input goes through the fixture root's guard.
/// </summary>
internal static class CoreScenarios
{
    private const int WaveformBuckets = 1800;          // App/ViewModels/SplitViewModel.cs:46
    private const int HoverWidth = 160;                // App/ViewModels/ThumbnailPreviewViewModel.cs:33
    private const int ChipWidth = 64;                  // App/ViewModels/BulkItemViewModel.cs:88
    private const int ProfileWidth = 640;              // App/ViewModels/BulkCutViewModel.cs:153

    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    // ---------------------------------------------------------------- shared: probe / keyframes / waveform / thumbs

    public static async Task Probe(BenchContext c, Fixture fx, int runs)
    {
        for (var i = 1; i <= runs; i++)
        {
            var probe = c.NewProbe();
            ProbeResult? r = null;
            var (s, u) = await Meter.Run(async () => r = await probe.ProbeAsync(fx.Path));
            c.Results.Add("shared", "MediaProbe.ProbeAsync", fx.Key, "warm", i, s, u, r is ProbeResult.ProbeSucceeded ? null : "FAILED");
        }

        var cold = Io.ColdCopy(c, fx.Path, fx.Key);
        try
        {
            var probe = c.NewProbe();
            var (s, u) = await Meter.Run(() => probe.ProbeAsync(cold));
            c.Results.Add("shared", "MediaProbe.ProbeAsync", fx.Key, "cold", 1, s, u);
        }
        finally
        {
            Io.TryDelete(cold);
        }
    }

    public static async Task Keyframes(BenchContext c, Fixture fx, int runs)
    {
        var size = new FileInfo(fx.Path).Length;
        for (var i = 1; i <= runs; i++)
        {
            var probe = c.NewProbe();
            IReadOnlyList<TimeSpan>? kf = null;
            var (s, u) = await Meter.Run(async () => kf = await probe.GetKeyframesAsync(fx.Path));
            c.Results.Add("shared", "MediaProbe.GetKeyframesAsync (packet scan)", fx.Key, "warm", i, s, u,
                $"{kf!.Count} keyframes, read {100.0 * u.ReadBytes / size:0}% of the file");
        }

        // Breakdown: the ffprobe process alone (same args as MediaProbe.cs:253-259) vs the whole call.
        var json = string.Empty;
        var (ps, pu) = await Meter.Run(async () => (_, json) = await c.RunFfprobe("-select_streams", "v:0", "-show_packets",
            "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path));
        c.Results.Add("shared", "  ffprobe -show_packets json (process only)", fx.Key, "warm", 1, ps, pu, $"stdout {json.Length / 1024.0:0}KB");

        var cold = Io.ColdCopy(c, fx.Path, fx.Key);
        try
        {
            var probe = c.NewProbe();
            var (s, u) = await Meter.Run(() => probe.GetKeyframesAsync(cold));
            c.Results.Add("shared", "MediaProbe.GetKeyframesAsync (packet scan)", fx.Key, "cold", 1, s, u,
                $"read {100.0 * u.ReadBytes / size:0}% of the file");
        }
        finally
        {
            Io.TryDelete(cold);
        }
    }

    public static async Task Waveform(BenchContext c, Fixture fx, int runs)
    {
        for (var i = 1; i <= runs; i++)
        {
            var svc = new FfmpegWaveformService(c.Ffmpeg, c.NewTempDir("wf"));
            float[]? peaks = null;
            var (s, u) = await Meter.Run(async () => peaks = await svc.GetPeaksAsync(fx.Path, WaveformBuckets, CancellationToken.None));
            c.Results.Add("split", "FfmpegWaveformService.GetPeaksAsync(1800)", fx.Key, "warm", i, s, u, peaks is null ? "NULL" : $"{peaks.Length} peaks");
        }

        var cold = Io.ColdCopy(c, fx.Path, fx.Key);
        try
        {
            var svc = new FfmpegWaveformService(c.Ffmpeg, c.NewTempDir("wf"));
            var (s, u) = await Meter.Run(() => svc.GetPeaksAsync(cold, WaveformBuckets, CancellationToken.None));
            c.Results.Add("split", "FfmpegWaveformService.GetPeaksAsync(1800)", fx.Key, "cold", 1, s, u);
        }
        finally
        {
            Io.TryDelete(cold);
        }
    }

    /// <summary>
    /// The thumbnail grabs. "On keyframe" asks for one of the file's own keyframes (the matrix time snapped to the
    /// list its self-check recorded: 300.021 s on <c>4k10m</c>, whose video starts 21 ms in), and mid-GOP the same
    /// distance past it. Each row's note says where the service really seeked (it floors a request to its time
    /// bucket, read back from the bucket in the file it returns) and how far past the keyframe before it that is,
    /// i.e. how much of a GOP ffmpeg decoded: on <c>4k10m</c> and <c>mkv</c> the 1 s bucket floors 300.021 to 300,
    /// 21 ms before the keyframe, so the "on keyframe" grab decodes almost a whole GOP.
    /// </summary>
    public static async Task Thumbnails(BenchContext c, Fixture fx, int runs)
    {
        static string At(double t) => t.ToString("0.###", CultureInfo.InvariantCulture);
        foreach (var (label, width) in new[] { ("hover", HoverWidth), ("bulk chip", ChipWidth), ("profile", ProfileWidth) })
        {
            foreach (var (where, t) in new[] { ("on keyframe", fx.ThumbOnKf), ($"{fx.ThumbMidGop - fx.ThumbOnKf:0.##}s into GOP", fx.ThumbMidGop) })
            {
                for (var i = 1; i <= runs; i++)
                {
                    var svc = new FfmpegThumbnailService(c.Ffmpeg, c.NewTempDir("th"));
                    string? path = null;
                    var (s, u) = await Meter.Run(async () => path = await svc.GetThumbnailAsync(fx.Path, S(t), width, CancellationToken.None));
                    c.Results.Add("shared", $"Thumbnail {label} {width}px @{At(t)}s ({where})", fx.Key, "warm", i, s, u, SeekNote(fx, path));
                }
            }
        }

        // Cache hit (same bucket, same width): what a repeated hover costs.
        {
            var svc = new FfmpegThumbnailService(c.Ffmpeg, c.NewTempDir("th"));
            await svc.GetThumbnailAsync(fx.Path, S(fx.ThumbMidGop), HoverWidth, CancellationToken.None);
            var (s, u) = await Meter.Run(() => svc.GetThumbnailAsync(fx.Path, S(fx.ThumbMidGop), HoverWidth, CancellationToken.None));
            c.Results.Add("shared", "Thumbnail hover 160px cache HIT", fx.Key, "warm", 1, s, u);
        }

        // A hover sweep: 10 distinct 1-s buckets in a row (each a new ffmpeg process, sequential like latest-wins).
        {
            var svc = new FfmpegThumbnailService(c.Ffmpeg, c.NewTempDir("th"));
            var start = Math.Max(1, fx.ThumbOnKf - 5);
            var (s, u) = await Meter.Run(async () =>
            {
                for (var k = 0; k < 10; k++)
                {
                    await svc.GetThumbnailAsync(fx.Path, S(start + k + 0.4), HoverWidth, CancellationToken.None);
                }
            });
            c.Results.Add("split", "Hover sweep: 10 new buckets, sequential (total)", fx.Key, "warm", 1, s, u, $"{s / 10:0.000}s per grab");
        }

        var cold = Io.ColdCopy(c, fx.Path, fx.Key);
        try
        {
            var svc = new FfmpegThumbnailService(c.Ffmpeg, c.NewTempDir("th"));
            string? path = null;
            var (s, u) = await Meter.Run(async () => path = await svc.GetThumbnailAsync(cold, S(fx.ThumbMidGop), HoverWidth, CancellationToken.None));
            c.Results.Add("shared", $"Thumbnail hover 160px @{At(fx.ThumbMidGop)}s", fx.Key, "cold", 1, s, u, SeekNote(fx, path));
        }
        finally
        {
            Io.TryDelete(cold);
        }
    }

    /// <summary>
    /// Where a thumbnail grab seeked: the bucket the service floored the request to (read back from the
    /// <c>&lt;bucketMs&gt;_w&lt;width&gt;.jpg</c> it returned) against the keyframe at or before it. NULL when the grab failed.
    /// </summary>
    private static string? SeekNote(Fixture fx, string? path)
    {
        if (path is null)
        {
            return "NULL";
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var cut = name.IndexOf("_w", StringComparison.Ordinal);
        if (cut <= 0 || !long.TryParse(name[..cut], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) || fx.Keyframes.Length == 0)
        {
            return null;
        }

        var seek = ms / 1000.0;
        var at = seek.ToString("0.###", CultureInfo.InvariantCulture);
        var earlier = fx.Keyframes.Where(k => k <= seek + 0.0005).ToList();
        if (earlier.Count == 0)
        {
            return $"seek {at}s, before the first keyframe at {fx.Keyframes[0].ToString("0.###", CultureInfo.InvariantCulture)}s";
        }

        var before = earlier.Max();
        var past = seek - before;
        return past < 0.0005
            ? $"seek {at}s, on a keyframe"
            : $"seek {at}s, {past:0.000}s past the keyframe at {before.ToString("0.###", CultureInfo.InvariantCulture)}s";
    }

    // ---------------------------------------------------------------- Split tab

    /// <summary>
    /// SplitViewModel.LoadAsync (SplitViewModel.cs:894-972): await ProbeAsync (the only gate) → Player.Open (FFME,
    /// not reproducible here) → StartKeyframeIndex (997) and StartWaveformExtraction (1134) in parallel, background.
    /// The four lines of one run share that run's job usage. A keyframe scan that throws or a waveform that comes
    /// back null is noted on its line (FAILED / NULL), never recorded as a plain time.
    /// </summary>
    public static async Task SplitLoad(BenchContext c, Fixture fx, int runs)
    {
        async Task One(string path, string mode, int run)
        {
            var probe = c.NewProbe();
            var wf = new FfmpegWaveformService(c.Ffmpeg, c.NewTempDir("wf"));
            var mark = JobAccounting.Mark();
            var sw = Stopwatch.StartNew();
            var pr = await probe.ProbeAsync(path);
            var tProbe = sw.Elapsed.TotalSeconds;
            var kfTask = probe.GetKeyframesAsync(path, CancellationToken.None);
            var wfTask = wf.GetPeaksAsync(path, WaveformBuckets, CancellationToken.None);
            var tk = kfTask.ContinueWith(_ => sw.Elapsed.TotalSeconds, TaskScheduler.Default);
            var tw = wfTask.ContinueWith(_ => sw.Elapsed.TotalSeconds, TaskScheduler.Default);
            await Task.WhenAll(tk, tw);
            var u = JobAccounting.Since(mark);
            var kfNote = Outcome(kfTask, r => r.Count == 0 ? "no keyframes" : null);
            var wfNote = Outcome(wfTask, r => r is null ? "NULL" : null);
            var probeNote = pr is ProbeResult.ProbeSucceeded ? null : "PROBE FAILED";
            c.Results.Add("split", "LOAD: probe done (info + preview can open)", fx.Key, mode, run, tProbe, u, probeNote);
            c.Results.Add("split", "LOAD: keyframes ready (cuts snap / Split enabled)", fx.Key, mode, run, tk.Result, u, kfNote);
            c.Results.Add("split", "LOAD: waveform drawn", fx.Key, mode, run, tw.Result, u, wfNote);
            var settled = string.Join("; ", new[] { probeNote, kfNote is null ? null : "keyframes " + kfNote, wfNote is null ? null : "waveform " + wfNote }.OfType<string>());
            c.Results.Add("split", "LOAD: everything settled", fx.Key, mode, run, Math.Max(tk.Result, tw.Result), u, settled.Length == 0 ? null : settled);
        }

        for (var i = 1; i <= runs; i++)
        {
            await One(fx.Path, "warm", i);
        }

        var cold = Io.ColdCopy(c, fx.Path, fx.Key);
        try
        {
            await One(cold, "cold", 1);
        }
        finally
        {
            Io.TryDelete(cold);
        }
    }

    /// <summary>A finished background task's note: null when it went well, FAILED / CANCELLED, or what <paramref name="onResult"/> says.</summary>
    private static string? Outcome<T>(Task<T> task, Func<T, string?> onResult) => task.Status switch
    {
        TaskStatus.RanToCompletion => onResult(task.Result),
        TaskStatus.Faulted => "FAILED: " + Describe(task.Exception!),
        TaskStatus.Canceled => "CANCELLED",
        _ => null,
    };

    private static string Describe(AggregateException ae)
    {
        var e = ae.InnerExceptions.Count == 1 ? ae.InnerExceptions[0] : ae;
        return $"{e.GetType().Name}: {e.Message}";
    }

    /// <summary>
    /// SplitViewModel.RunSplitAsync (SplitViewModel.cs:1360-1412): all parts selected → null selection → segment muxer.
    /// The probe's keyframe cache is warm (the load already scanned) unless mode == cold. <c>work/split-&lt;key&gt;</c>
    /// holds exactly the full split's parts (the join scenario and <c>alt join</c> read them); the part-2-only run
    /// writes to <c>work/split-&lt;key&gt;-sel</c> and the cold run to <c>work/split-cold-&lt;key&gt;</c>. With
    /// <paramref name="full"/> false (only <c>join</c> was asked) just the full split runs, to make the parts.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SplitExport(BenchContext c, Fixture fx, int runs, bool full)
    {
        var outDir = Path.Combine(c.Work, "split-" + fx.Key);
        Io.TryDelete(outDir);
        Directory.CreateDirectory(outDir);
        IReadOnlyList<string> produced = Array.Empty<string>();

        async Task<SplitResult> One(string path, MediaProbe probe, IReadOnlyList<int>? selection, string dir, string mode, int run, string label)
        {
            var eng = new SplitEngine(c.Ffmpeg, probe, c.Log);
            var req = new SplitRequest(path, fx.Cuts.Select(S).ToList(), dir, SplitRequest.DefaultNamingPattern, Overwrite: true, selection);
            var stages = new List<(string Stage, double T)>();
            var reports = 0;
            var mark = JobAccounting.Mark();
            var sw = Stopwatch.StartNew();
            var res = await eng.SplitAsync(
                req,
                new SyncProgress<double>(_ => reports++),
                CancellationToken.None,
                new SyncProgress<OperationStatus>(st => stages.Add((st.Stage, sw.Elapsed.TotalSeconds))),
                new SyncProgress<PartProgress>(_ => { }));
            var total = sw.Elapsed.TotalSeconds;
            var u = JobAccounting.Since(mark);
            double At(string st) => stages.FirstOrDefault(x => x.Stage == st).T;
            var prep = At("Splitting") - At("Preparing");
            var ffm = At("Finalizing") - At("Splitting");
            var fin = At("Done") - At("Finalizing");
            var bytes = res.Segments.Sum(sg => new FileInfo(sg.Path).Length);
            c.Results.Add("split", label, fx.Key, mode, run, total, u,
                $"prepare {prep:0.000}s | ffmpeg {ffm:0.000}s | finalize {fin:0.000}s | {res.Segments.Count} parts {Io.Mb(bytes)} | {reports} progress ticks");
            return res;
        }

        for (var i = 1; i <= runs; i++)
        {
            var probe = c.NewProbe();
            await probe.GetKeyframesAsync(fx.Path); // the load already did this in the app
            var res = await One(fx.Path, probe, null, outDir, "warm", i, "SplitEngine.SplitAsync 3 parts (segment muxer)");
            produced = res.Segments.Select(sg => c.Root.Resolve(sg.Path)).ToList();
            if (i == 1 && full)
            {
                await PartFrames(c, fx, probe, produced, "segment muxer");
            }
        }

        if (!full)
        {
            return produced;
        }

        // The selection runs write to their own folder, so the full split's _part02 (which join reads) stays its own.
        var selDir = Path.Combine(c.Work, "split-" + fx.Key + "-sel");
        try
        {
            Directory.CreateDirectory(selDir);
            for (var i = 1; i <= runs; i++)
            {
                var probe = c.NewProbe();
                await probe.GetKeyframesAsync(fx.Path);
                await One(fx.Path, probe, new[] { 2 }, selDir, "warm", i, "SplitEngine.SplitAsync part 2 only (per-segment)");
            }
        }
        finally
        {
            Io.TryDelete(selDir);
        }

        // Cold source + cold probe (no keyframe cache): a split right after picking a file that was never cached.
        var cold = Io.ColdCopy(c, fx.Path, fx.Key);
        var coldOut = Path.Combine(c.Work, "split-cold-" + fx.Key);
        try
        {
            var probe = c.NewProbe();
            var eng = new SplitEngine(c.Ffmpeg, probe, c.Log);
            var req = new SplitRequest(cold, fx.Cuts.Select(S).ToList(), coldOut, SplitRequest.DefaultNamingPattern, Overwrite: true);
            var (s, u) = await Meter.Run(() => eng.SplitAsync(req));
            c.Results.Add("split", "SplitEngine.SplitAsync 3 parts, cold file + cold keyframe cache", fx.Key, "cold", 1, s, u);
        }
        finally
        {
            Io.TryDelete(cold);
            Io.TryDelete(coldOut);
        }

        return produced;
    }

    /// <summary>
    /// Not timed: each part's video frame count against the plan — the T-219 lossless-boundary check (a B-frame
    /// source's segment-muxer parts ending one GOP late). The plan runs from the source's first video frame (its
    /// probed <c>start_time</c>, 21 ms in on <c>4k10m</c> and <c>mkv</c>) through the snapped cuts to its last video
    /// frame: each part but the last is (next boundary − boundary) × frame rate, and the last is what remains of the
    /// source's counted video frames, so the plan is exact whatever the stream's start and real end.
    /// </summary>
    private static async Task PartFrames(BenchContext c, Fixture fx, MediaProbe probe, IReadOnlyList<string> parts, string path)
    {
        try
        {
            var kf = await probe.GetKeyframesAsync(fx.Path);
            var (_, srcJson) = await c.RunFfprobe("-v", "error", "-count_packets", "-select_streams", "v:0",
                "-show_entries", "stream=r_frame_rate,start_time,nb_read_packets", "-of", "json", "-i", fx.Path);
            var (fps, videoStart, total) = SourceVideo(srcJson, fx.VideoStart);
            var bounds = new List<double> { videoStart };
            bounds.AddRange(fx.Cuts.Select(t => probe.SnapToNearestKeyframe(kf, S(t)).Snapped.TotalSeconds));
            var actual = new List<int>();
            foreach (var p in parts)
            {
                var (_, o) = await c.RunFfprobe("-v", "error", "-count_packets", "-select_streams", "v:0", "-show_entries", "stream=nb_read_packets", "-of", "json", "-i", p);
                using var doc = JsonDocument.Parse(o);
                actual.Add(int.Parse(doc.RootElement.GetProperty("streams")[0].GetProperty("nb_read_packets").GetString()!, CultureInfo.InvariantCulture));
            }

            var planned = new List<int>();
            for (var i = 0; i + 1 < bounds.Count; i++)
            {
                planned.Add((int)Math.Round((bounds[i + 1] - bounds[i]) * fps));
            }

            planned.Add(total - planned.Sum());
            var same = actual.SequenceEqual(planned);
            c.Results.Add("split", $"  part video frames ({path}, not timed)", fx.Key, "-", 1, 0, Usage.None,
                $"{string.Join('/', actual)} against planned {string.Join('/', planned)} (video from {videoStart.ToString("0.######", CultureInfo.InvariantCulture)} s, " +
                $"{total} frames; cuts snapped to {string.Join(", ", bounds.Skip(1).Select(b => b.ToString("0.###", CultureInfo.InvariantCulture)))} s)" +
                $"{(same ? string.Empty : " — parts do not match the plan (T-219)")}");
        }
        catch (Exception e) when (e is FfprobeException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            c.Results.Note($"part frame count failed on {fx.Key}: {e.Message}");
        }
    }

    /// <summary>The source video's frame rate, start time (or <paramref name="fallbackStart"/>) and counted frames.</summary>
    private static (double Fps, double Start, int Frames) SourceVideo(string json, double fallbackStart)
    {
        using var doc = JsonDocument.Parse(json);
        var s = doc.RootElement.GetProperty("streams")[0];
        var r = s.GetProperty("r_frame_rate").GetString() ?? "0/1";
        var slash = r.IndexOf('/');
        var fps = slash < 0
            ? double.Parse(r, CultureInfo.InvariantCulture)
            : double.Parse(r[..slash], CultureInfo.InvariantCulture) / double.Parse(r[(slash + 1)..], CultureInfo.InvariantCulture);
        var start = s.TryGetProperty("start_time", out var st)
            && double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallbackStart;
        var frames = int.Parse(s.GetProperty("nb_read_packets").GetString()!, CultureInfo.InvariantCulture);
        return (fps, start, frames);
    }

    // ---------------------------------------------------------------- Join tab

    public static async Task Join(BenchContext c, Fixture fx, IReadOnlyList<string> parts, int runs)
    {
        var outPath = Path.Combine(c.Work, $"join-{fx.Key}{Path.GetExtension(fx.Path)}");

        // JoinViewModel.AddFilesAsync (JoinViewModel.cs:283-328): probe each new clip for the chip, then RefreshCompatAsync
        // → JoinEngine.CheckCompatibilityAsync probes EVERY clip again (JoinEngine.cs:83-96).
        for (var i = 1; i <= runs; i++)
        {
            var probe = c.NewProbe();
            var eng = new JoinEngine(c.Ffmpeg, probe, c.Log);
            var (s, u) = await Meter.Run(async () =>
            {
                foreach (var p in parts)
                {
                    await probe.ProbeAsync(p);
                }

                await eng.CheckCompatibilityAsync(parts);
            });
            c.Results.Add("join", "ADD 3 clips at once (3 chip probes + compat = 6 probes)", fx.Key, "warm", i, s, u);
        }

        // Adding the clips one at a time: add #k = 1 chip probe + (k>=2 ? compat over k clips : 0).
        {
            var probe = c.NewProbe();
            var eng = new JoinEngine(c.Ffmpeg, probe, c.Log);
            var list = new List<string>();
            foreach (var p in parts)
            {
                list.Add(p);
                var (s, u) = await Meter.Run(async () =>
                {
                    await probe.ProbeAsync(p);
                    if (list.Count >= 2)
                    {
                        await eng.CheckCompatibilityAsync(list.ToList());
                    }
                });
                c.Results.Add("join", $"ADD clip #{list.Count} (one at a time)", fx.Key, "warm", 1, s, u);
            }
        }

        // JoinViewModel run → JoinEngine.JoinAsync: compat (N probes) + SumDurations (N probes) + concat -c copy + move.
        for (var i = 1; i <= runs; i++)
        {
            var eng = new JoinEngine(c.Ffmpeg, c.NewProbe(), c.Log);
            var stages = new List<(string Stage, double T)>();
            var mark = JobAccounting.Mark();
            var sw = Stopwatch.StartNew();
            var res = await eng.JoinAsync(new JoinRequest(parts, outPath, Overwrite: true), new SyncProgress<double>(_ => { }),
                CancellationToken.None, new SyncProgress<OperationStatus>(st => stages.Add((st.Stage, sw.Elapsed.TotalSeconds))));
            var total = sw.Elapsed.TotalSeconds;
            var u = JobAccounting.Since(mark);
            double At(string st) => stages.FirstOrDefault(x => x.Stage == st).T;
            c.Results.Add("join", "JoinEngine.JoinAsync 3 parts", fx.Key, "warm", i, total, u,
                $"compat+probes {At("Joining") - At("Checking compatibility"):0.000}s | ffmpeg concat {At("Finalizing") - At("Joining"):0.000}s | finalize {At("Done") - At("Finalizing"):0.000}s | {(res.Success ? Io.Mb(new FileInfo(outPath).Length) : "REFUSED " + res.Refusal?.Mismatches.FirstOrDefault()?.Detail)}");
        }

        // Cold: parts copied cold.
        var coldParts = parts.Select((p, k) => Io.ColdCopy(c, p, $"{fx.Key}-part{k + 1}")).ToList();
        try
        {
            var eng = new JoinEngine(c.Ffmpeg, c.NewProbe(), c.Log);
            var (s, u) = await Meter.Run(() => eng.JoinAsync(new JoinRequest(coldParts, outPath, Overwrite: true)));
            c.Results.Add("join", "JoinEngine.JoinAsync 3 parts", fx.Key, "cold", 1, s, u);
        }
        finally
        {
            foreach (var p in coldParts)
            {
                Io.TryDelete(p);
            }

            Io.TryDelete(outPath);
        }
    }

    // ---------------------------------------------------------------- Bulk Cut tab

    /// <summary>
    /// The bulk section: rows added (one file, three files, three cold files), then Lossless and Exact runs.
    /// The two extra rows are copies of the fixture under work/ (each its own path and cache key, like real rows),
    /// made fresh every time (a leftover from a stopped run may be partial, or of an older fixture) and deleted at
    /// the end of the section (the prototype kept them; on the 10-minute file they are 9 GB). The section needs
    /// about five times the fixture free at its peak (two row copies plus three cold row copies), checked first.
    /// </summary>
    public static async Task Bulk(BenchContext c, Fixture fx)
    {
        var size = new FileInfo(fx.Path).Length;
        var copies = new List<string> { fx.Path };
        var made = new List<string>();
        try
        {
            for (var k = 2; k <= 3; k++)
            {
                var copy = Path.Combine(c.Work, $"bulkcopy{k}-{fx.File}");
                Io.TryDelete(copy);
                Io.TryDelete(copy + ".partial");
            }

            Io.EnsureFreeSpace(c, 5 * size, $"bulk on '{fx.Key}' (2 row copies + 3 cold row copies of {Io.Mb(size)})");
            Directory.CreateDirectory(c.Work);
            for (var k = 2; k <= 3; k++)
            {
                // Copied under a temporary name and renamed when complete, so a stopped copy never passes for a row.
                var copy = Path.Combine(c.Work, $"bulkcopy{k}-{fx.File}");
                made.Add(copy);
                File.Copy(fx.Path, copy + ".partial");
                File.Move(copy + ".partial", copy);
                copies.Add(c.Root.Resolve(copy));
            }

            await BulkAdd(c, fx, new[] { fx.Path }, "warm");
            await BulkAdd(c, fx, copies, "warm");
            var coldRows = new List<string>();
            try
            {
                for (var k = 0; k < copies.Count; k++)
                {
                    coldRows.Add(Io.ColdCopy(c, copies[k], $"{fx.Key}-row{k + 1}"));
                }

                await BulkAdd(c, fx, coldRows, "cold");
            }
            finally
            {
                coldRows.ForEach(Io.TryDelete);
            }

            for (var i = 1; i <= 2; i++)
            {
                await BulkRun(c, fx, new[] { fx.Path }, CutPrecision.Lossless, "warm", i);
            }

            await BulkRun(c, fx, copies, CutPrecision.Lossless, "warm", 1);
            for (var i = 1; i <= 2; i++)
            {
                await BulkRun(c, fx, new[] { fx.Path }, CutPrecision.Exact, "warm", i);
            }

            await BulkRun(c, fx, copies, CutPrecision.Exact, "warm", 1);
        }
        finally
        {
            foreach (var copy in made)
            {
                Io.TryDelete(copy);
                Io.TryDelete(copy + ".partial");
            }
        }
    }

    /// <summary>
    /// BulkCutViewModel.AddFilesAsync (BulkCutViewModel.cs:1387-1424): probe each row (awaited, sequential) →
    /// row.StartKeyframeScanAsync under a 3-wide gate (BulkItemViewModel.cs:754-761, gate BulkCutViewModel.cs:173) →
    /// on scan, intro + outro chips at 64px under a 3-wide thumbnail gate (BulkCutViewModel.cs:178; 200 ms debounce
    /// BulkItemViewModel.cs:95 NOT included here). The three lines share the run's job usage.
    /// </summary>
    public static async Task BulkAdd(BenchContext c, Fixture fx, IReadOnlyList<string> files, string mode)
    {
        var probe = c.NewProbe();
        var thumbs = new FfmpegThumbnailService(c.Ffmpeg, c.NewTempDir("th"));
        var scanGate = new SemaphoreSlim(3, 3);
        var thumbGate = new SemaphoreSlim(3, 3);
        var mark = JobAccounting.Mark();
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
        var u = JobAccounting.Since(mark);
        c.Results.Add("bulk", $"ADD {files.Count} row(s): all probed (rows show duration)", fx.Key, mode, 1, tProbed, u);
        c.Results.Add("bulk", $"ADD {files.Count} row(s): all keyframe scans done (rows runnable)", fx.Key, mode, 1, scans.Max(), u);
        c.Results.Add("bulk", $"ADD {files.Count} row(s): all intro/outro chips drawn", fx.Key, mode, 1, tAll, u);
    }

    /// <summary>BulkCutViewModel run (BulkCutViewModel.cs:2737-2750) → BulkTrimEngine.RunAsync. Keyframes warm (row add scanned).</summary>
    public static async Task BulkRun(BenchContext c, Fixture fx, IReadOnlyList<string> files, CutPrecision precision, string mode, int run)
    {
        var probe = c.NewProbe();
        foreach (var f in files)
        {
            await probe.GetKeyframesAsync(f);
        }

        var split = new SplitEngine(c.Ffmpeg, probe, c.Log);
        var smart = new SmartCutEngine(c.Ffmpeg, probe);
        var eng = new BulkTrimEngine(split, new KeptMiddleRequestBuilder(probe), smart);
        var outDir = Path.Combine(c.Work, "bulk-out");
        Directory.CreateDirectory(outDir);
        var items = files.Select(f => new BulkTrimItem(f, S(fx.Intro), S(fx.Outro),
            Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "_trimmed" + Path.GetExtension(f)))).ToList();
        var opts = new BulkTrimOptions(CollisionPolicy.Overwrite, OutputMode.NewFile, precision);
        BatchResult? batch = null;
        try
        {
            var (s, u) = await Meter.Run(async () => batch = await eng.RunAsync(items, opts, new SyncProgress<BulkTrimProgress>(_ => { })));
            var warn = string.Join(" / ", batch!.Items.SelectMany(it => it.Warnings).Distinct());
            c.Results.Add("bulk", $"BulkTrimEngine.RunAsync {precision} x{files.Count} (intro {fx.Intro}s, outro {fx.Outro}s)", fx.Key, mode, run, s, u,
                $"{batch.Outcome} done={batch.DoneCount} failed={batch.FailedCount} {warn} {string.Join(";", batch.Items.Where(x => x.Error is not null).Select(x => x.Error!.Message))}");
        }
        finally
        {
            Io.TryDelete(outDir);
        }
    }

    // ---------------------------------------------------------------- Exact cut (SmartCutEngine)

    /// <summary>
    /// The <c>smart</c> scenario: at each of the fixture's named cut positions, SmartCutEngine's three ffmpeg runs
    /// (the engine's own public builders: head re-encode, tail copy, concat), then the head variants (input seek,
    /// presets, NVENC) on the same span, one run each. The first position keeps the fixture's outro; later ones
    /// cut to the end, as the prototype did.
    /// </summary>
    public static async Task Smart(BenchContext c, Fixture fx)
    {
        for (var i = 0; i < fx.SmartAt.Length; i++)
        {
            var start = fx.SmartAt[i];
            double? end = i == 0 ? fx.Outro : null;
            var tag = $"intro@{start.ToString(CultureInfo.InvariantCulture)}s";
            var heads = await SmartCutSteps(c, fx, start, end, tag);
            if (heads is not null)
            {
                await SmartHeads.TimeVariants(c, fx, heads, tag);
            }
        }
    }

    /// <summary>SmartCutEngine.CutAsync broken into its three ffmpeg runs, using the engine's own public builders.</summary>
    public static async Task<SmartHeads.Span?> SmartCutSteps(BenchContext c, Fixture fx, double start, double? end, string tag)
    {
        var probe = c.NewProbe();
        if (await probe.ProbeAsync(fx.Path) is not ProbeResult.ProbeSucceeded ok)
        {
            c.Results.Note($"smart {fx.Key}: probe failed");
            return null;
        }

        var info = ok.Info;
        var kf = await probe.GetKeyframesAsync(fx.Path);
        var plan = SmartCutPlanner.Plan(S(start), end is { } e ? S(e) : null, kf);
        if (!SmartCutArgsBuilder.TryResolveEncoders(info, out var v, out var a, out var why))
        {
            c.Results.Note($"smart {fx.Key}: no encoder ({why})");
            return null;
        }

        var dir = c.NewTempDir("smart");
        try
        {
            var ext = Path.GetExtension(fx.Path);
            var head = Path.Combine(dir, "head" + ext);
            var tail = Path.Combine(dir, "tail" + ext);
            var final = Path.Combine(dir, "final" + ext);
            var headEnd = plan.HeadEnd ?? plan.End ?? info.Duration;
            var headArgs = SmartCutArgsBuilder.HeadReencode(fx.Path, plan.Start, headEnd, info, v!, a, head);
            FfmpegResult? hr = null;
            var (tHead, hu) = await Meter.Run(async () => hr = await c.Ffmpeg.RunAsync(headArgs));
            c.Results.Add("bulk", $"  Exact {tag}: head re-encode [{start}s..{headEnd.TotalSeconds}s] ({v}, output -ss)", fx.Key, "warm", 1, tHead, hu,
                $"exit {hr!.ExitCode}; read {100.0 * hu.ReadBytes / new FileInfo(fx.Path).Length:0}% of the file; args: {string.Join(' ', headArgs.ToList().Skip(2)).Replace(fx.Path, "<in>", StringComparison.Ordinal).Replace(head, "<head>", StringComparison.Ordinal)}");
            var span = new SmartHeads.Span(info, plan.Start, headEnd, v!, a);
            if (plan.HeadEnd is null)
            {
                return span;
            }

            var tailArgs = SmartCutArgsBuilder.TailCopy(fx.Path, plan.HeadEnd.Value, plan.End, tail);
            FfmpegResult? tr = null;
            var (tTail, tu) = await Meter.Run(async () => tr = await c.Ffmpeg.RunAsync(tailArgs));
            c.Results.Add("bulk", $"  Exact {tag}: tail -c copy [{plan.HeadEnd.Value.TotalSeconds}s..{end}]", fx.Key, "warm", 1, tTail, tu, $"exit {tr!.ExitCode}");
            var list = Path.Combine(dir, "concat.txt");
            await File.WriteAllTextAsync(list, JoinArgsBuilder.RenderConcatList(new[] { head, tail }));
            FfmpegResult? cr = null;
            var (tCat, cu) = await Meter.Run(async () => cr = await c.Ffmpeg.RunAsync(JoinArgsBuilder.ConcatCopy(list, final)));
            c.Results.Add("bulk", $"  Exact {tag}: concat head+tail -c copy", fx.Key, "warm", 1, tCat, cu, $"exit {cr!.ExitCode}");
            return span;
        }
        finally
        {
            Io.TryDelete(dir);
        }
    }
}
