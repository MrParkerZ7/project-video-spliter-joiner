using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.Bench;

/// <summary>
/// ffmpeg/ffprobe-level alternatives for the measured 4K hotspots, each timed through the app's own runners and
/// checked for output equivalence against the current command. Ported from the prototype with two changes: bytes
/// read come from the job object on every line (the prototype's separate IoMeter, which started processes itself,
/// is gone — the <c>io</c> section runs the same commands through the runners), and the managed sample-table
/// reader (Mp4Index) is not here, because T-194 builds the production reader in Core and the bench then times it
/// through <c>MediaProbe</c>. <c>floor</c> is the prototype's IO-floor mode.
/// </summary>
internal static class Alternatives
{
    public static readonly string[] Sections = { "floor", "io", "probe", "kf", "thumb", "smart", "join", "wf", "decode", "nofsi", "thumbframe", "smart2" };

    public static async Task Run(BenchContext c, IReadOnlyList<Fixture> fixtures, Func<string, Fixture?> get, List<string> sections)
    {
        // smart2 takes case ids after it; the others run on every given fixture. Each section runs through
        // RunSection: one that throws gets a FAILED row and the next section still runs.
        bool On(string s) => sections.Count == 0 ? s != "smart2" : sections.Contains(s);
        var all = string.Join(' ', fixtures.Select(f => f.Key));
        var steps = new (string Name, Func<Task> Work)[]
        {
            ("floor", () => Floor(c, fixtures)),
            ("io", () => IoBytes(c, fixtures)),
            ("probe", () => ProbeVariants(c, fixtures)),
            ("kf", () => KeyframeVariants(c, fixtures)),
            ("thumb", () => ThumbVariants(c, fixtures)),
            ("smart", () => SmartVariants(c, fixtures)),
            ("join", () => JoinVariants(c, get)),
            ("wf", () => WaveformVariants(c, fixtures)),
            ("decode", () => DecodeVariants(c, fixtures)),
            ("nofsi", () => NoFindStreamInfo(c, fixtures)),
            ("thumbframe", () => ThumbWhichFrame(c, fixtures)),
        };
        foreach (var (name, work) in steps)
        {
            if (On(name))
            {
                await c.RunSection(name, name == "join" ? "4k" : all, work);
            }
        }

        if (sections.Contains("smart2"))
        {
            await c.RunSection("smart2", "-", () => Smart2.Run(c, get, sections.Where(x => !Sections.Contains(x)).ToList()));
        }
    }

    // ------------------------------------------------------------------ IO floor (the prototype's `floor` mode)

    private static async Task Floor(BenchContext c, IReadOnlyList<Fixture> fixtures)
    {
        foreach (var fx in fixtures)
        {
            var len = new FileInfo(fx.Path).Length;
            for (var i = 1; i <= 2; i++)
            {
                var cold = string.Empty;
                var (sc, uc) = Meter.RunSync(() => cold = Io.ColdCopy(c, fx.Path, fx.Key));
                c.Results.Add("floor", "unbuffered write-through copy (real disk write)", fx.Key, "-", i, sc, uc, Io.Mb(len));
                var (s1, u1) = Meter.RunSync(() => Io.ReadAllSeconds(cold));
                c.Results.Add("floor", "sequential read, cold (from disk)", fx.Key, "cold", i, s1, u1, $"{len / s1 / 1e6:0} MB/s");
                var (s2, u2) = Meter.RunSync(() => Io.ReadAllSeconds(cold));
                c.Results.Add("floor", "sequential read, warm (page cache)", fx.Key, "warm", i, s2, u2, $"{len / s2 / 1e6:0} MB/s");
                Io.TryDelete(cold);
            }

            for (var i = 1; i <= 2; i++)
            {
                var cold = Io.ColdCopy(c, fx.Path, fx.Key);
                var dst = Path.Combine(c.Work, $"filecopy-{fx.Key}{Path.GetExtension(fx.Path)}");
                Io.TryDelete(dst);
                var (s1, u1) = Meter.RunSync(() => File.Copy(cold, dst, true));
                c.Results.Add("floor", "File.Copy cold source (plain copy, cached write)", fx.Key, "cold", i, s1, u1);
                Io.TryDelete(dst);
                var (s2, u2) = Meter.RunSync(() => File.Copy(cold, dst, true));
                c.Results.Add("floor", "File.Copy warm source (plain copy, cached write)", fx.Key, "warm", i, s2, u2);
                Io.TryDelete(dst);

                var outp = Path.Combine(c.Work, $"ffcopy-{fx.Key}{Path.GetExtension(fx.Path)}");
                var exit = 0;
                var (s3, u3) = await Meter.Run(async () => exit = (await c.RunFfmpeg("-y", "-i", fx.Path, "-map", "0", "-c", "copy", outp)).R.ExitCode);
                c.Results.Add("floor", "ffmpeg -i in -map 0 -c copy out (whole file)", fx.Key, "warm", i, s3, u3, $"exit {exit}");
                var (s4, u4) = await Meter.Run(async () => exit = (await c.RunFfmpeg("-y", "-i", cold, "-map", "0", "-c", "copy", outp)).R.ExitCode);
                c.Results.Add("floor", "ffmpeg -i in -map 0 -c copy out (whole file)", fx.Key, "cold", i, s4, u4, $"exit {exit}");
                var (s5, u5) = await Meter.Run(async () => exit = (await c.RunFfmpeg("-y", "-i", fx.Path, "-map", "0", "-c", "copy", "-f", "null", "-")).R.ExitCode);
                c.Results.Add("floor", "ffmpeg -i in -map 0 -c copy -f null - (demux only, no write)", fx.Key, "warm", i, s5, u5, $"exit {exit}");
                Io.TryDelete(outp);
                Io.TryDelete(cold);
            }
        }
    }

    // ------------------------------------------------------------------ -nofind_stream_info (skip the H.264 probe-decode every ffmpeg/ffprobe run pays)

    private static async Task NoFindStreamInfo(BenchContext c, IReadOnlyList<Fixture> fixtures)
    {
        foreach (var fx in fixtures)
        {
            var dir = c.NewTempDir("nofsi");

            // probe
            string? jA = null, jB = null;
            var pA = await Times(5, async _ => { var (s, o) = await c.RunFfprobe("-show_streams", "-show_format", "-print_format", "json", "-i", fx.Path); jA = o; return s; });
            var pB = await Times(5, async _ => { var (s, o) = await c.RunFfprobe("-nofind_stream_info", "-show_streams", "-show_format", "-print_format", "json", "-i", fx.Path); jB = o; return s; });
            var cA = CanonicalProbe(jA!);
            var cB = CanonicalProbe(jB!);
            c.Results.Add("alt-nofsi", "probe current", fx.Key, "warm", 5, pA.Median, pA.MedianUsage, $"runs {Fmt(pA.All)}");
            c.Results.Add("alt-nofsi", "probe -nofind_stream_info", fx.Key, "warm", 5, pB.Median, pB.MedianUsage, $"runs {Fmt(pB.All)} | mapped fields {(cA == cB ? "IDENTICAL" : "DIFFER: " + cB + " vs " + cA)}");

            // keyframe packet scan
            List<double>? kA = null, kB = null;
            var kfA = await Times(4, async _ => { var (s, o) = await c.RunFfprobe("-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path); kA = PacketKeyframes(o); return s; });
            var kfB = await Times(4, async _ => { var (s, o) = await c.RunFfprobe("-nofind_stream_info", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path); kB = PacketKeyframes(o); return s; });
            c.Results.Add("alt-nofsi", "keyframe packet scan current", fx.Key, "warm", 4, kfA.Median, kfA.MedianUsage, $"runs {Fmt(kfA.All)}");
            c.Results.Add("alt-nofsi", "keyframe packet scan -nofind_stream_info", fx.Key, "warm", 4, kfB.Median, kfB.MedianUsage, $"runs {Fmt(kfB.All)} | {Compare(kA!, kB!)}");

            // thumbnail (hover 160px, mid-GOP and on keyframe)
            foreach (var t in new[] { fx.ThumbOnKf, fx.ThumbMidGop })
            {
                var ts = t.ToString(CultureInfo.InvariantCulture);
                var a = Path.Combine(dir, "a.jpg");
                var b = Path.Combine(dir, "b.jpg");
                var tA = await Times(4, async _ => (await c.RunFfmpeg("-ss", ts, "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", a)).S);
                var tB = await Times(4, async _ => (await c.RunFfmpeg("-nofind_stream_info", "-ss", ts, "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", b)).S);
                var psnr = File.Exists(b) ? await c.Psnr(a, b) : double.NaN;
                c.Results.Add("alt-nofsi", $"thumb 160px @{ts}s current", fx.Key, "warm", 4, tA.Median, tA.MedianUsage, $"runs {Fmt(tA.All)}");
                c.Results.Add("alt-nofsi", $"thumb 160px @{ts}s -nofind_stream_info", fx.Key, "warm", 4, tB.Median, tB.MedianUsage, $"runs {Fmt(tB.All)} | PSNR vs current {psnr:0.0} dB");
            }

            // waveform
            {
                var a = Path.Combine(dir, "a.pcm");
                var b = Path.Combine(dir, "b.pcm");
                var tA = await Times(4, async _ => (await c.RunFfmpeg("-i", fx.Path, "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y", a)).S);
                var tB = await Times(4, async _ => (await c.RunFfmpeg("-nofind_stream_info", "-i", fx.Path, "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y", b)).S);
                var same = File.Exists(b) && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
                c.Results.Add("alt-nofsi", "waveform current", fx.Key, "warm", 4, tA.Median, tA.MedianUsage, $"runs {Fmt(tA.All)}");
                c.Results.Add("alt-nofsi", "waveform -nofind_stream_info", fx.Key, "warm", 4, tB.Median, tB.MedianUsage, $"runs {Fmt(tB.All)} | pcm {(same ? "IDENTICAL" : "DIFFERS")}");
            }

            Io.TryDelete(dir);
        }
    }

    /// <summary>Which frame do the keyframe-only thumbnail variants return? Compare against references at the KF before and after t.</summary>
    private static async Task ThumbWhichFrame(BenchContext c, IReadOnlyList<Fixture> fixtures)
    {
        foreach (var fx in fixtures)
        {
            var dir = c.NewTempDir("which");
            var t = fx.ThumbMidGop;
            var before = Math.Floor(t / fx.Gop) * fx.Gop;
            var after = before + fx.Gop;
            async Task<string> Grab(string name, params string[] pre)
            {
                var p = Path.Combine(dir, name + ".jpg");
                await c.RunFfmpeg(pre.Concat(new[] { "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", p }).ToArray());
                return p;
            }

            var refBefore = await Grab("before", "-ss", before.ToString(CultureInfo.InvariantCulture));
            var refAfter = await Grab("after", "-ss", after.ToString(CultureInfo.InvariantCulture));
            var refT = await Grab("t", "-ss", t.ToString(CultureInfo.InvariantCulture));
            foreach (var (name, pre) in new[]
            {
                ("-noaccurate_seek", new[] { "-noaccurate_seek", "-ss", t.ToString(CultureInfo.InvariantCulture) }),
                ("-skip_frame nokey", new[] { "-skip_frame", "nokey", "-ss", t.ToString(CultureInfo.InvariantCulture) }),
            })
            {
                var g = await Grab(name.Trim('-').Replace(' ', '_'), pre);
                c.Results.Add("alt-thumb", $"which frame: {name} @{t}s", fx.Key, "-", 1, 0, Usage.None,
                    $"PSNR vs KF@{before}s {await c.Psnr(refBefore, g):0.0} dB | vs KF@{after}s {await c.Psnr(refAfter, g):0.0} dB | vs exact @{t}s {await c.Psnr(refT, g):0.0} dB");
            }

            Io.TryDelete(dir);
        }
    }

    /// <summary>Run <paramref name="f"/> n times; the median of the warm runs (all but the first), with that run's job usage.</summary>
    private static async Task<(double Median, double First, double[] All, Usage MedianUsage)> Times(int n, Func<int, Task<double>> f)
    {
        var all = new (double S, Usage U)[n];
        for (var i = 0; i < n; i++)
        {
            var mark = JobAccounting.Mark();
            var s = await f(i);
            all[i] = (s, JobAccounting.Since(mark));
        }

        var warm = all.Skip(1).OrderBy(x => x.S).ToArray();
        var med = warm.Length == 0 ? all[0] : warm[warm.Length / 2];
        return (med.S, all[0].S, all.Select(x => x.S).ToArray(), med.U);
    }

    private static string Fmt(double[] all) => string.Join(" ", all.Select(x => x.ToString("0.000", CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ bytes read per operation (current args)

    /// <summary>
    /// The prototype measured these with IoMeter, which started each process itself. Here the same argument tokens
    /// run through the app's runners and the bytes come from the job — one run each.
    /// </summary>
    private static async Task IoBytes(BenchContext c, IReadOnlyList<Fixture> fixtures)
    {
        var (bs, bu) = await Meter.Run(() => c.RunFfmpeg("-version"));
        c.Results.Add("alt-io", "baseline: ffmpeg -version", "-", "-", 1, bs, bu, $"read {Io.Mb(bu.ReadBytes)}");
        foreach (var fx in fixtures)
        {
            var size = new FileInfo(fx.Path).Length;
            async Task Rec(string op, string[] args, bool ffprobe)
            {
                var exit = 0;
                var (s, u) = await Meter.Run(async () =>
                {
                    if (ffprobe)
                    {
                        try
                        {
                            await c.RunFfprobe(args);
                        }
                        catch (VideoSplitJoiner.Core.Ffmpeg.FfprobeException e)
                        {
                            exit = e.ExitCode;
                        }
                    }
                    else
                    {
                        exit = (await c.RunFfmpeg(args)).R.ExitCode;
                    }
                });
                c.Results.Add("alt-io", op, fx.Key, "warm", 1, s, u,
                    $"read {Io.Mb(u.ReadBytes)} ({100.0 * u.ReadBytes / size:0.0}% of file) write {Io.Mb(u.WriteBytes)} exit {exit}");
            }

            var tmp = c.NewTempDir("io");
            await Rec("probe (MediaProbe.cs:90)", new[] { "-show_streams", "-show_format", "-print_format", "json", "-i", fx.Path }, true);
            await Rec("keyframes packet scan (MediaProbe.cs:253)", new[] { "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path }, true);
            await Rec("waveform (FfmpegWaveformService.cs:227)", new[] { "-i", fx.Path, "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y", Path.Combine(tmp, "a.pcm") }, false);
            await Rec($"thumbnail 160px @{fx.ThumbMidGop}s (FfmpegThumbnailService.cs:207)", new[] { "-ss", fx.ThumbMidGop.ToString(CultureInfo.InvariantCulture), "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", Path.Combine(tmp, "t.jpg") }, false);
            var seg = SplitArgsBuilder.SegmentMuxer(fx.Path, new[] { TimeSpan.FromSeconds(Math.Round(fx.Cuts[0] / fx.Gop) * fx.Gop), TimeSpan.FromSeconds(Math.Round(fx.Cuts[1] / fx.Gop) * fx.Gop) }, Path.Combine(tmp, "part%03d" + Path.GetExtension(fx.Path)));
            await Rec("split segment muxer 3 parts (SplitArgsBuilder.cs:47)", seg.ToList().Skip(2).ToArray(), false);

            var probe = c.NewProbe();
            if (await probe.ProbeAsync(fx.Path) is ProbeResult.ProbeSucceeded ok
                && SmartCutArgsBuilder.TryResolveEncoders(ok.Info, out var v, out var a, out _))
            {
                foreach (var start in new[] { fx.Intro, fx.Duration - 15.5 })
                {
                    var headEnd = Math.Ceiling(start / fx.Gop) * fx.Gop;
                    var head = SmartCutArgsBuilder.HeadReencode(fx.Path, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(headEnd), ok.Info, v!, a, Path.Combine(tmp, "head" + Path.GetExtension(fx.Path)));
                    await Rec($"exact-cut head re-encode @{start}s, output -ss (SmartCutArgsBuilder.cs:317)", head.ToList().Skip(2).ToArray(), false);
                }
            }

            Io.TryDelete(tmp);
        }
    }

    // ------------------------------------------------------------------ probe

    private static string CanonicalProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var parts = new List<string>();
        string G(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ToString() : "-";
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            parts.Add(string.Join("|", G(s, "index"), G(s, "codec_name"), G(s, "codec_type"), G(s, "width"), G(s, "height"), G(s, "pix_fmt"),
                G(s, "sample_rate"), G(s, "channels"), G(s, "time_base"), G(s, "duration")));
        }

        var f = doc.RootElement.GetProperty("format");
        parts.Add(G(f, "duration") + "|" + G(f, "format_name"));
        return string.Join(";", parts);
    }

    private static async Task ProbeVariants(BenchContext c, IReadOnlyList<Fixture> fixtures)
    {
        var variants = new (string Name, string[] Pre)[]
        {
            ("current (MediaProbe.cs:90-92)", Array.Empty<string>()),
            ("-threads 1", new[] { "-threads", "1" }),
            ("-skip_frame nokey", new[] { "-skip_frame", "nokey" }),
            ("-analyzeduration 0", new[] { "-analyzeduration", "0" }),
            ("-threads 1 -analyzeduration 0", new[] { "-threads", "1", "-analyzeduration", "0" }),
        };
        foreach (var fx in fixtures)
        {
            string? baseCanon = null;
            foreach (var (name, pre) in variants)
            {
                string? json = null;
                var t = await Times(5, async _ =>
                {
                    var (s, o) = await c.RunFfprobe(pre.Concat(new[] { "-show_streams", "-show_format", "-print_format", "json", "-i", fx.Path }).ToArray());
                    json = o;
                    return s;
                });
                var canon = CanonicalProbe(json!);
                baseCanon ??= canon;
                c.Results.Add("alt-probe", "probe " + name, fx.Key, "warm", 5, t.Median, t.MedianUsage,
                    $"runs {Fmt(t.All)} | mapped fields {(canon == baseCanon ? "IDENTICAL" : "DIFFER: " + canon)}");
            }
        }
    }

    // ------------------------------------------------------------------ keyframes

    private static List<double> ParseTimes(string text, string key)
    {
        var list = new List<double>();
        foreach (Match m in Regex.Matches(text, "\"" + key + "\": \"(?<v>[0-9.]+)\""))
        {
            list.Add(double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture));
        }

        return list;
    }

    private static List<double> PacketKeyframes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var set = new SortedSet<double>();
        foreach (var p in doc.RootElement.GetProperty("packets").EnumerateArray())
        {
            var flags = p.TryGetProperty("flags", out var fl) ? fl.GetString() : null;
            if (flags is null || !flags.Contains('K'))
            {
                continue;
            }

            var raw = p.TryGetProperty("pts_time", out var pt) ? pt.GetString() : p.TryGetProperty("dts_time", out var dt) ? dt.GetString() : null;
            if (raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                set.Add(d);
            }
        }

        return set.ToList();
    }

    private static string Compare(IReadOnlyList<double> reference, IReadOnlyList<double> candidate)
    {
        if (reference.Count != candidate.Count)
        {
            return $"DIFFER count {candidate.Count} vs {reference.Count}";
        }

        var max = reference.Zip(candidate, (x, y) => Math.Abs(x - y)).DefaultIfEmpty(0).Max();
        return max < 1e-5 ? $"IDENTICAL ({candidate.Count} kf)" : $"DIFFER max {max:0.000000}s";
    }

    private static async Task KeyframeVariants(BenchContext c, IReadOnlyList<Fixture> fixtures)
    {
        foreach (var fx in fixtures)
        {
            var size = new FileInfo(fx.Path).Length;
            List<double>? reference = null;
            var t0 = await Times(4, async _ =>
            {
                var (s, o) = await c.RunFfprobe("-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path);
                reference = PacketKeyframes(o);
                return s;
            });
            c.Results.Add("alt-kf", "current: ffprobe -show_packets json (MediaProbe.cs:253)", fx.Key, "warm", 4, t0.Median, t0.MedianUsage, $"runs {Fmt(t0.All)} | {reference!.Count} kf");

            // -threads 1 (only affects the find_stream_info decode every ffprobe run does).
            List<double>? l1 = null;
            var t1 = await Times(4, async _ =>
            {
                var (s, o) = await c.RunFfprobe("-threads", "1", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path);
                l1 = PacketKeyframes(o);
                return s;
            });
            c.Results.Add("alt-kf", "-threads 1 + same packet scan", fx.Key, "warm", 4, t1.Median, t1.MedianUsage, $"runs {Fmt(t1.All)} | {Compare(reference, l1!)}");

            // csv output (smaller stdout, no JSON) — same demux work.
            List<double>? l2 = null;
            var t2 = await Times(4, async _ =>
            {
                var (s, o) = await c.RunFfprobe("-threads", "1", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", "-i", fx.Path);
                l2 = o.Split('\n').Select(x => x.Trim().Split(',')).Where(x => x.Length >= 2 && x[1].Contains('K'))
                    .Select(x => double.Parse(x[0], CultureInfo.InvariantCulture)).Distinct().OrderBy(x => x).ToList();
                return s;
            });
            c.Results.Add("alt-kf", "-threads 1 + packet scan as csv (no JSON)", fx.Key, "warm", 4, t2.Median, t2.MedianUsage, $"runs {Fmt(t2.All)} | {Compare(reference, l2!)}");

            // The fallback path: decode keyframes only.
            List<double>? l3 = null;
            var t3 = await Times(2, async _ =>
            {
                var (s, o) = await c.RunFfprobe("-select_streams", "v:0", "-skip_frame", "nokey", "-show_entries", "frame=pts_time", "-print_format", "json", "-i", fx.Path);
                l3 = ParseTimes(o, "pts_time").Distinct().OrderBy(x => x).ToList();
                return s;
            });
            c.Results.Add("alt-kf", "fallback: -skip_frame nokey frame scan (MediaProbe.cs:293)", fx.Key, "warm", 2, t3.Median, t3.MedianUsage, $"runs {Fmt(t3.All)} | {Compare(reference, l3!)}");

            // ffmpeg -discard nokey: does the demuxer skip non-key packets (fewer bytes read)? framecrc goes to a file
            // under work/ (the runner drains stdout without returning it).
            var crc = Path.Combine(c.NewTempDir("kfcrc"), "framecrc.txt");
            var (ds, du) = await Meter.Run(() => c.RunFfmpeg("-discard", "nokey", "-i", fx.Path, "-map", "0:v:0", "-c", "copy", "-f", "framecrc", "-y", crc));
            var lines = File.Exists(crc) ? File.ReadLines(crc).Count(l => l.StartsWith("0,", StringComparison.Ordinal)) : 0;
            c.Results.Add("alt-kf", "ffmpeg -discard nokey -c copy -f framecrc (demux-level skip?)", fx.Key, "warm", 1, ds, du, $"{lines} packets out, read {Io.Mb(du.ReadBytes)} of {Io.Mb(size)}");
            Io.TryDelete(Path.GetDirectoryName(crc)!);
        }
    }

    // ------------------------------------------------------------------ thumbnails

    private static async Task ThumbVariants(BenchContext c, IReadOnlyList<Fixture> fixtures)
    {
        foreach (var fx in fixtures)
        {
            var t = fx.ThumbMidGop.ToString(CultureInfo.InvariantCulture);
            var kf = fx.ThumbOnKf.ToString(CultureInfo.InvariantCulture);
            var dir = c.NewTempDir("thalt");
            var refJpg = Path.Combine(dir, "ref.jpg");
            var kfJpg = Path.Combine(dir, "kf.jpg");
            await c.RunFfmpeg("-ss", t, "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", refJpg);
            await c.RunFfmpeg("-ss", kf, "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", kfJpg);

            var variants = new (string Name, string[] Pre, string Vf)[]
            {
                ("current (FfmpegThumbnailService.cs:207-214)", Array.Empty<string>(), "scale=160:-1"),
                ("-threads 1", new[] { "-threads", "1" }, "scale=160:-1"),
                ("-thread_type slice", new[] { "-thread_type", "slice" }, "scale=160:-1"),
                ("scale flags=fast_bilinear", Array.Empty<string>(), "scale=160:-1:flags=fast_bilinear"),
                ("-hwaccel d3d11va", new[] { "-hwaccel", "d3d11va" }, "scale=160:-1"),
                ("-hwaccel dxva2", new[] { "-hwaccel", "dxva2" }, "scale=160:-1"),
                ("-hwaccel cuda", new[] { "-hwaccel", "cuda" }, "scale=160:-1"),
                ("-hwaccel cuda + scale_cuda on GPU", new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" }, "scale_cuda=160:-2,hwdownload,format=nv12"),
                ("-noaccurate_seek (keyframe <= t)", new[] { "-noaccurate_seek" }, "scale=160:-1"),
                ("-skip_frame nokey (keyframe <= t)", new[] { "-skip_frame", "nokey" }, "scale=160:-1"),
            };

            foreach (var (name, pre, vf) in variants)
            {
                var outJpg = Path.Combine(dir, "v.jpg");
                var ok = true;
                var tm = await Times(4, async i =>
                {
                    Io.TryDelete(outJpg);
                    var args = pre.Concat(new[] { "-ss", t, "-i", fx.Path, "-frames:v", "1", "-vf", vf }).Concat(new[] { "-y", outJpg }).ToArray();
                    var (s, r) = await c.RunFfmpeg(args);
                    ok &= r.Success && File.Exists(outJpg);
                    return s;
                });
                string eq;
                if (!ok)
                {
                    eq = "FAILED";
                }
                else
                {
                    var pRef = await c.Psnr(refJpg, outJpg, "160:90");
                    var pKf = await c.Psnr(kfJpg, outJpg, "160:90");
                    eq = $"PSNR vs current {pRef:0.0} dB, vs keyframe frame {pKf:0.0} dB";
                }

                c.Results.Add("alt-thumb", $"thumb 160px @{t}s {name}", fx.Key, "warm", 4, tm.Median, tm.MedianUsage, $"runs {Fmt(tm.All)} | {eq}");
            }

            Io.TryDelete(dir);
        }
    }

    // ------------------------------------------------------------------ exact cut (SmartCutEngine head re-encode)

    private static async Task<(int Frames, double Duration)> CountFrames(BenchContext c, string path)
    {
        var (_, o) = await c.RunFfprobe("-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames:format=duration", "-of", "json", "-i", path);
        using var doc = JsonDocument.Parse(o);
        var frames = int.Parse(doc.RootElement.GetProperty("streams")[0].GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture);
        var dur = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return (frames, dur);
    }

    private static async Task<string> DecodeErrors(BenchContext c, string path)
    {
        var (_, r) = await c.RunFfmpeg("-v", "error", "-i", path, "-f", "null", "-");
        var errs = r.StdErrTail.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return errs.Count == 0 ? "decodes clean" : $"{errs.Count} decode error line(s): {errs[0]}";
    }

    private static async Task SmartVariants(BenchContext c, IReadOnlyList<Fixture> fixtures)
    {
        foreach (var fx in fixtures)
        {
            var probe = c.NewProbe();
            if (await probe.ProbeAsync(fx.Path) is not ProbeResult.ProbeSucceeded okProbe
                || !SmartCutArgsBuilder.TryResolveEncoders(okProbe.Info, out var venc, out var aenc, out _))
            {
                c.Results.Note($"alt smart {fx.Key}: probe failed or no encoder");
                continue;
            }

            var info = okProbe.Info;
            var kfs = await probe.GetKeyframesAsync(fx.Path);
            foreach (var start in new[] { fx.Intro, fx.Duration - 15.5 })
            {
                var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(start), null, kfs);
                if (plan.HeadEnd is null)
                {
                    continue;
                }

                var headEnd = plan.HeadEnd.Value;
                var dir = c.NewTempDir("smartalt");
                var span = new SmartHeads.Span(info, plan.Start, headEnd, venc!, aenc);
                var sStart = SplitPlanner.ToFfmpegSeconds(plan.Start);
                var sDur = SplitPlanner.ToFfmpegSeconds(headEnd - plan.Start);
                var variants = new List<(string Name, string[] Args)>
                {
                    // The engine's own head tokens, minus the runner's -hide_banner -nostdin and the output path.
                    ($"current: -i in -ss {sStart} (output seek) {venc} default preset",
                        SmartCutArgsBuilder.HeadReencode(fx.Path, plan.Start, headEnd, info, venc!, aenc, "<out>").ToList().Skip(2).SkipLast(1).ToArray()),
                };
                variants.AddRange(SmartHeads.Variants(fx, span));

                string? baseHead = null;
                (int Frames, double Duration) baseCount = default;
                for (var vi = 0; vi < variants.Count; vi++)
                {
                    var (name, args) = variants[vi];
                    var head = Path.Combine(dir, $"head{vi}{Path.GetExtension(fx.Path)}");
                    var a2 = args.Concat(new[] { head }).ToArray();
                    var runs = name.StartsWith("current", StringComparison.Ordinal) ? 2 : 3;
                    var ok = true;
                    var tm = await Times(runs, async _ =>
                    {
                        var (s, r) = await c.RunFfmpeg(a2);
                        ok &= r.Success;
                        return s;
                    });
                    string eq;
                    if (!ok || !File.Exists(head))
                    {
                        eq = "FAILED";
                    }
                    else
                    {
                        var cnt = await CountFrames(c, head);
                        if (baseHead is null)
                        {
                            baseHead = head;
                            baseCount = cnt;
                            eq = $"head {cnt.Frames} frames {cnt.Duration:0.000}s (reference)";
                        }
                        else
                        {
                            var psnr = await c.Psnr(baseHead, head);
                            eq = $"head {cnt.Frames} frames {cnt.Duration:0.000}s (ref {baseCount.Frames}/{baseCount.Duration:0.000}) | PSNR vs current head {psnr:0.0} dB";
                        }

                        // Build the full exact output the engine would ship (tail copy + concat via the engine's builders).
                        var tail = Path.Combine(dir, "tail" + Path.GetExtension(fx.Path));
                        var final = Path.Combine(dir, "final" + Path.GetExtension(fx.Path));
                        await c.Ffmpeg.RunAsync(SmartCutArgsBuilder.TailCopy(fx.Path, headEnd, null, tail));
                        var list = Path.Combine(dir, "list.txt");
                        await File.WriteAllTextAsync(list, JoinArgsBuilder.RenderConcatList(new[] { head, tail }));
                        await c.Ffmpeg.RunAsync(JoinArgsBuilder.ConcatCopy(list, final));
                        var fc = await CountFrames(c, final);
                        var expectFrames = (int)Math.Round((fx.Duration - start) * 30);
                        eq += $" | final {fc.Frames} frames (expect ~{expectFrames}) {fc.Duration:0.000}s | {await DecodeErrors(c, final)}";
                        Io.TryDelete(tail);
                        Io.TryDelete(final);
                    }

                    c.Results.Add("alt-smart", $"exact head @{start}s: {name}", fx.Key, "warm", runs, tm.Median, tm.MedianUsage, $"runs {Fmt(tm.All)} | {eq}");
                }

                Io.TryDelete(dir);
            }
        }
    }

    // ------------------------------------------------------------------ join

    private static async Task JoinVariants(BenchContext c, Func<string, Fixture?> get)
    {
        var fx = get("4k");
        var splitDir = Path.Combine(c.Work, "split-4k");
        var parts = fx is null || !Directory.Exists(splitDir)
            ? new List<string>()
            : Directory.GetFiles(splitDir, "*_part0*.mp4").OrderBy(x => x).Select(c.Root.Resolve).ToList();
        if (fx is null || parts.Count != 3)
        {
            c.Results.Note("join alt: run `bench core 4k split` first");
            return;
        }

        var dir = c.NewTempDir("joinalt");
        var list = Path.Combine(dir, "list.txt");
        await File.WriteAllTextAsync(list, JoinArgsBuilder.RenderConcatList(parts));
        var outA = Path.Combine(dir, "a.mp4");
        var outB = Path.Combine(dir, "b.mp4");
        var tA = await Times(4, async _ => (await c.RunFfmpeg(JoinArgsBuilder.ConcatCopy(list, outA).ToList().Skip(2).ToArray())).S);
        c.Results.Add("alt-join", "current concat (JoinArgsBuilder.cs:38)", fx.Key, "warm", 4, tA.Median, tA.MedianUsage, $"runs {Fmt(tA.All)}");
        var tB = await Times(4, async _ => (await c.RunFfmpeg("-y", "-f", "concat", "-safe", "0", "-auto_convert", "0", "-i", list, "-map", "0", "-c", "copy", outB)).S);
        var hA = (await c.RunFfmpeg("-i", outA, "-map", "0", "-c", "copy", "-f", "streamhash", "-hash", "md5", "-y", Path.Combine(dir, "ha.txt"))).R;
        var hB = (await c.RunFfmpeg("-i", outB, "-map", "0", "-c", "copy", "-f", "streamhash", "-hash", "md5", "-y", Path.Combine(dir, "hb.txt"))).R;
        var same = File.ReadAllText(Path.Combine(dir, "ha.txt")) == File.ReadAllText(Path.Combine(dir, "hb.txt"));
        c.Results.Add("alt-join", "concat -auto_convert 0", fx.Key, "warm", 4, tB.Median, tB.MedianUsage, $"runs {Fmt(tB.All)} | packet streamhash {(same ? "IDENTICAL" : "DIFFERS")} (exit {hA.ExitCode}/{hB.ExitCode})");
        var tC = await Times(3, async _ => (await c.RunFfmpeg("-y", "-i", fx.Path, "-map", "0", "-c", "copy", Path.Combine(dir, "c.mp4"))).S);
        c.Results.Add("alt-join", "reference: ffmpeg -c copy of the same bytes as ONE file", fx.Key, "warm", 3, tC.Median, tC.MedianUsage, $"runs {Fmt(tC.All)}");
        Io.TryDelete(dir);
    }

    // ------------------------------------------------------------------ waveform

    private static async Task WaveformVariants(BenchContext c, IReadOnlyList<Fixture> fixtures)
    {
        foreach (var fx in fixtures)
        {
            var dir = c.NewTempDir("wfalt");
            var variants = new (string Name, string[] Args)[]
            {
                ("current (FfmpegWaveformService.cs:227)", new[] { "-i", fx.Path, "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y" }),
                ("-map 0:a:0 -vn -sn -dn", new[] { "-i", fx.Path, "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y" }),
                ("-threads 1 (+ -vn)", new[] { "-threads", "1", "-i", fx.Path, "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y" }),
            };
            byte[]? reference = null;
            foreach (var (name, args) in variants)
            {
                var pcm = Path.Combine(dir, "a.pcm");
                var tm = await Times(4, async _ => (await c.RunFfmpeg(args.Concat(new[] { pcm }).ToArray())).S);
                var bytes = await File.ReadAllBytesAsync(pcm);
                reference ??= bytes;
                c.Results.Add("alt-wf", "waveform " + name, fx.Key, "warm", 4, tm.Median, tm.MedianUsage, $"runs {Fmt(tm.All)} | pcm {(bytes.AsSpan().SequenceEqual(reference) ? "IDENTICAL" : "DIFFERS")}");
            }

            Io.TryDelete(dir);
        }
    }

    // ------------------------------------------------------------------ decode throughput (proxy for the FFME preview)

    private static async Task DecodeVariants(BenchContext c, IReadOnlyList<Fixture> fixtures)
    {
        foreach (var fx in fixtures)
        {
            var variants = new (string Name, string[] Pre, string[] Post)[]
            {
                ("software decode", Array.Empty<string>(), Array.Empty<string>()),
                ("software decode + scale to 1080p (preview filter)", Array.Empty<string>(), new[] { "-vf", "scale=-2:1080" }),
                ("-hwaccel d3d11va (frames copied to RAM)", new[] { "-hwaccel", "d3d11va" }, Array.Empty<string>()),
                ("-hwaccel d3d11va + scale to 1080p", new[] { "-hwaccel", "d3d11va" }, new[] { "-vf", "scale=-2:1080" }),
                ("-hwaccel cuda (frames copied to RAM)", new[] { "-hwaccel", "cuda" }, Array.Empty<string>()),
            };
            foreach (var (name, pre, post) in variants)
            {
                var tm = await Times(3, async _ =>
                    (await c.RunFfmpeg(pre.Concat(new[] { "-ss", "20", "-i", fx.Path, "-t", "10", "-an" }).Concat(post).Concat(new[] { "-f", "null", "-" }).ToArray())).S);
                c.Results.Add("alt-dec", $"decode 10s (300 frames): {name}", fx.Key, "warm", 3, tm.Median, tm.MedianUsage, $"runs {Fmt(tm.All)} | {300 / tm.Median:0} fps = {300 / tm.Median / 30:0.0}x realtime");
            }
        }
    }
}
