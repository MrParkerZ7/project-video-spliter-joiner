using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace T187Bench;

/// <summary>
/// ffmpeg/ffprobe-level alternatives for the measured 4K hotspots, each timed through the app's own runners and
/// checked for output equivalence against the current command.
/// </summary>
internal static class Alternatives
{
    private static readonly string Ff = P.Ff;
    private static readonly string Fp = P.Fp;

    public static async Task Run(List<string> sections)
    {
        bool On(string s) => sections.Count == 0 || sections.Contains(s);
        if (On("io"))
        {
            await IoBytes();
        }

        if (On("probe"))
        {
            await ProbeVariants();
        }

        if (On("kf"))
        {
            await KeyframeVariants();
        }

        if (On("thumb"))
        {
            await ThumbVariants();
        }

        if (On("smart"))
        {
            await SmartVariants();
        }

        if (On("join"))
        {
            await JoinVariants();
        }

        if (On("wf"))
        {
            await WaveformVariants();
        }

        if (On("decode"))
        {
            await DecodeVariants();
        }

        if (On("nofsi"))
        {
            await NoFindStreamInfo();
        }

        if (On("thumbframe"))
        {
            await ThumbWhichFrame();
        }

        if (sections.Contains("smart2"))
        {
            await Smart2.Run(sections.Where(x => x != "smart2").ToList());
        }
    }

    // ------------------------------------------------------------------ -nofind_stream_info (skip the H.264 probe-decode every ffmpeg/ffprobe run pays)

    private static async Task NoFindStreamInfo()
    {
        foreach (var fx in Fixture.All)
        {
            var dir = Svc.NewTempDir("nofsi");

            // probe
            string? jA = null, jB = null;
            var pA = await Times(5, async _ => { var (s, o) = await Svc.RunFfprobe("-show_streams", "-show_format", "-print_format", "json", "-i", fx.Path); jA = o; return s; });
            var pB = await Times(5, async _ => { var (s, o) = await Svc.RunFfprobe("-nofind_stream_info", "-show_streams", "-show_format", "-print_format", "json", "-i", fx.Path); jB = o; return s; });
            var cA = CanonicalProbe(jA!);
            var cB = CanonicalProbe(jB!);
            R.Add("alt-nofsi", "probe current", fx.Key, "warm", 5, pA.Median, $"runs {Fmt(pA.All)}");
            R.Add("alt-nofsi", "probe -nofind_stream_info", fx.Key, "warm", 5, pB.Median, $"runs {Fmt(pB.All)} | mapped fields {(cA == cB ? "IDENTICAL" : "DIFFER: " + cB + " vs " + cA)}");

            // keyframe packet scan
            List<double>? kA = null, kB = null;
            var kfA = await Times(4, async _ => { var (s, o) = await Svc.RunFfprobe("-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path); kA = PacketKeyframes(o); return s; });
            var kfB = await Times(4, async _ => { var (s, o) = await Svc.RunFfprobe("-nofind_stream_info", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path); kB = PacketKeyframes(o); return s; });
            R.Add("alt-nofsi", "keyframe packet scan current", fx.Key, "warm", 4, kfA.Median, $"runs {Fmt(kfA.All)}");
            R.Add("alt-nofsi", "keyframe packet scan -nofind_stream_info", fx.Key, "warm", 4, kfB.Median, $"runs {Fmt(kfB.All)} | {Compare(kA!, kB!)}");

            // thumbnail (hover 160px, mid-GOP and on keyframe)
            foreach (var t in new[] { fx.ThumbOnKf, fx.ThumbMidGop })
            {
                var ts = t.ToString(CultureInfo.InvariantCulture);
                var a = Path.Combine(dir, "a.jpg");
                var b = Path.Combine(dir, "b.jpg");
                var tA = await Times(4, async _ => (await Svc.RunFfmpeg("-ss", ts, "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", a)).S);
                var tB = await Times(4, async _ => (await Svc.RunFfmpeg("-nofind_stream_info", "-ss", ts, "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", b)).S);
                var psnr = File.Exists(b) ? await Svc.Psnr(a, b) : double.NaN;
                R.Add("alt-nofsi", $"thumb 160px @{ts}s current", fx.Key, "warm", 4, tA.Median, $"runs {Fmt(tA.All)}");
                R.Add("alt-nofsi", $"thumb 160px @{ts}s -nofind_stream_info", fx.Key, "warm", 4, tB.Median, $"runs {Fmt(tB.All)} | PSNR vs current {psnr:0.0} dB");
            }

            // waveform
            {
                var a = Path.Combine(dir, "a.pcm");
                var b = Path.Combine(dir, "b.pcm");
                var tA = await Times(4, async _ => (await Svc.RunFfmpeg("-i", fx.Path, "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y", a)).S);
                var tB = await Times(4, async _ => (await Svc.RunFfmpeg("-nofind_stream_info", "-i", fx.Path, "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y", b)).S);
                var same = File.Exists(b) && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
                R.Add("alt-nofsi", "waveform current", fx.Key, "warm", 4, tA.Median, $"runs {Fmt(tA.All)}");
                R.Add("alt-nofsi", "waveform -nofind_stream_info", fx.Key, "warm", 4, tB.Median, $"runs {Fmt(tB.All)} | pcm {(same ? "IDENTICAL" : "DIFFERS")}");
            }

            Io.TryDelete(dir);
        }
    }

    /// <summary>Which frame do the keyframe-only thumbnail variants return? Compare against references at the KF before and after t.</summary>
    private static async Task ThumbWhichFrame()
    {
        foreach (var fx in new[] { Fixture.Get("g10"), Fixture.Get("4k") })
        {
            var dir = Svc.NewTempDir("which");
            var t = fx.ThumbMidGop;
            var before = Math.Floor(t / fx.Gop) * fx.Gop;
            var after = before + fx.Gop;
            async Task<string> Grab(string name, params string[] pre)
            {
                var p = Path.Combine(dir, name + ".jpg");
                await Svc.RunFfmpeg(pre.Concat(new[] { "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", p }).ToArray());
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
                R.Add("alt-thumb", $"which frame: {name} @{t}s", fx.Key, "-", 1, 0,
                    $"PSNR vs KF@{before}s {await Svc.Psnr(refBefore, g):0.0} dB | vs KF@{after}s {await Svc.Psnr(refAfter, g):0.0} dB | vs exact @{t}s {await Svc.Psnr(refT, g):0.0} dB");
            }

            Io.TryDelete(dir);
        }
    }

    private static async Task<(double Median, double First, double[] All)> Times(int n, Func<int, Task<double>> f)
    {
        var all = new double[n];
        for (var i = 0; i < n; i++)
        {
            all[i] = await f(i);
        }

        var warm = all.Skip(1).OrderBy(x => x).ToArray();
        var med = warm.Length == 0 ? all[0] : warm[warm.Length / 2];
        return (med, all[0], all);
    }

    private static string Fmt(double[] all) => string.Join(" ", all.Select(x => x.ToString("0.000", CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ bytes read per operation (current args)

    private static async Task IoBytes()
    {
        await Task.CompletedTask;
        var baseline = IoMeter.Run(Ff, new[] { "-hide_banner", "-version" });
        R.Add("alt-io", "baseline: ffmpeg -version", "-", "-", 1, baseline.Seconds, $"read {Io.Mb(baseline.ReadBytes)}");
        foreach (var fx in Fixture.All)
        {
            var size = new FileInfo(fx.Path).Length;
            void Rec(string op, IReadOnlyList<string> exeArgs, string exe)
            {
                var io = IoMeter.Run(exe, exeArgs);
                R.Add("alt-io", op, fx.Key, "warm", 1, io.Seconds,
                    $"read {Io.Mb(io.ReadBytes)} ({100.0 * io.ReadBytes / size:0.0}% of file) write {Io.Mb(io.WriteBytes)} exit {io.ExitCode}");
            }

            var tmp = Svc.NewTempDir("io");
            Rec("probe (MediaProbe.cs:90)", new[] { "-hide_banner", "-show_streams", "-show_format", "-print_format", "json", "-i", fx.Path }, Fp);
            Rec("keyframes packet scan (MediaProbe.cs:253)", new[] { "-hide_banner", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path }, Fp);
            Rec("waveform (FfmpegWaveformService.cs:227)", new[] { "-hide_banner", "-nostdin", "-i", fx.Path, "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y", Path.Combine(tmp, "a.pcm") }, Ff);
            Rec($"thumbnail 160px @{fx.ThumbMidGop}s (FfmpegThumbnailService.cs:207)", new[] { "-hide_banner", "-nostdin", "-ss", fx.ThumbMidGop.ToString(CultureInfo.InvariantCulture), "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", Path.Combine(tmp, "t.jpg") }, Ff);
            var seg = SplitArgsBuilder.SegmentMuxer(fx.Path, new[] { TimeSpan.FromSeconds(Math.Round(fx.Cuts[0] / fx.Gop) * fx.Gop), TimeSpan.FromSeconds(Math.Round(fx.Cuts[1] / fx.Gop) * fx.Gop) }, Path.Combine(tmp, "part%03d.mp4"));
            Rec("split segment muxer 3 parts (SplitArgsBuilder.cs:47)", seg.ToList(), Ff);

            var probe = Svc.NewProbe();
            var info = ((ProbeResult.ProbeSucceeded)await probe.ProbeAsync(fx.Path)).Info;
            SmartCutArgsBuilder.TryResolveEncoders(info, out var v, out var a, out _);
            foreach (var start in new[] { fx.Intro, fx.Duration - 15.5 })
            {
                var headEnd = Math.Ceiling(start / fx.Gop) * fx.Gop;
                var head = SmartCutArgsBuilder.HeadReencode(fx.Path, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(headEnd), info, v!, a, Path.Combine(tmp, "head.mp4"));
                Rec($"exact-cut head re-encode @{start}s, output -ss (SmartCutArgsBuilder.cs:317)", head.ToList(), Ff);
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

    private static async Task ProbeVariants()
    {
        var variants = new (string Name, string[] Pre)[]
        {
            ("current (MediaProbe.cs:90-92)", Array.Empty<string>()),
            ("-threads 1", new[] { "-threads", "1" }),
            ("-skip_frame nokey", new[] { "-skip_frame", "nokey" }),
            ("-analyzeduration 0", new[] { "-analyzeduration", "0" }),
            ("-threads 1 -analyzeduration 0", new[] { "-threads", "1", "-analyzeduration", "0" }),
        };
        foreach (var fx in Fixture.All)
        {
            string? baseCanon = null;
            foreach (var (name, pre) in variants)
            {
                string? json = null;
                var t = await Times(5, async _ =>
                {
                    var (s, o) = await Svc.RunFfprobe(pre.Concat(new[] { "-show_streams", "-show_format", "-print_format", "json", "-i", fx.Path }).ToArray());
                    json = o;
                    return s;
                });
                var canon = CanonicalProbe(json!);
                baseCanon ??= canon;
                R.Add("alt-probe", "probe " + name, fx.Key, "warm", 5, t.Median,
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

    private static async Task KeyframeVariants()
    {
        foreach (var fx in Fixture.All)
        {
            List<double>? reference = null;
            var t0 = await Times(4, async _ =>
            {
                var (s, o) = await Svc.RunFfprobe("-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path);
                reference = PacketKeyframes(o);
                return s;
            });
            R.Add("alt-kf", "current: ffprobe -show_packets json (MediaProbe.cs:253)", fx.Key, "warm", 4, t0.Median, $"runs {Fmt(t0.All)} | {reference!.Count} kf");

            // -threads 1 (only affects the find_stream_info decode every ffprobe run does).
            List<double>? l1 = null;
            var t1 = await Times(4, async _ =>
            {
                var (s, o) = await Svc.RunFfprobe("-threads", "1", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", "-i", fx.Path);
                l1 = PacketKeyframes(o);
                return s;
            });
            R.Add("alt-kf", "-threads 1 + same packet scan", fx.Key, "warm", 4, t1.Median, $"runs {Fmt(t1.All)} | {Compare(reference, l1!)}");

            // csv output (smaller stdout, no JSON) — same demux work.
            List<double>? l2 = null;
            var t2 = await Times(4, async _ =>
            {
                var (s, o) = await Svc.RunFfprobe("-threads", "1", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", "-i", fx.Path);
                l2 = o.Split('\n').Select(x => x.Trim().Split(',')).Where(x => x.Length >= 2 && x[1].Contains('K'))
                    .Select(x => double.Parse(x[0], CultureInfo.InvariantCulture)).Distinct().OrderBy(x => x).ToList();
                return s;
            });
            R.Add("alt-kf", "-threads 1 + packet scan as csv (no JSON)", fx.Key, "warm", 4, t2.Median, $"runs {Fmt(t2.All)} | {Compare(reference, l2!)}");

            // The fallback path: decode keyframes only.
            List<double>? l3 = null;
            var t3 = await Times(2, async _ =>
            {
                var (s, o) = await Svc.RunFfprobe("-select_streams", "v:0", "-skip_frame", "nokey", "-show_entries", "frame=pts_time", "-print_format", "json", "-i", fx.Path);
                l3 = ParseTimes(o, "pts_time").Distinct().OrderBy(x => x).ToList();
                return s;
            });
            R.Add("alt-kf", "fallback: -skip_frame nokey frame scan (MediaProbe.cs:293)", fx.Key, "warm", 2, t3.Median, $"runs {Fmt(t3.All)} | {Compare(reference, l3!)}");

            // ffmpeg -discard nokey: does the mov demuxer skip non-key packets (fewer bytes read)?
            var io = IoMeter.Run(Ff, new[] { "-hide_banner", "-nostdin", "-discard", "nokey", "-i", fx.Path, "-map", "0:v:0", "-c", "copy", "-f", "framecrc", "-" });
            var lines = io.StdOut.Split('\n').Count(l => l.StartsWith("0,", StringComparison.Ordinal));
            R.Add("alt-kf", "ffmpeg -discard nokey -c copy -f framecrc (demux-level skip?)", fx.Key, "warm", 1, io.Seconds, $"{lines} packets out, read {Io.Mb(io.ReadBytes)} of {Io.Mb(new FileInfo(fx.Path).Length)}");

            // Index-only: parse the mp4 sample tables (stss/stts/ctts/elst) in managed code — reads the moov box only.
            Mp4Index.Result? idx = null;
            var t4 = await Times(5, _ =>
            {
                var sw = Stopwatch.StartNew();
                idx = Mp4Index.ReadKeyframes(fx.Path);
                return Task.FromResult(sw.Elapsed.TotalSeconds);
            });
            R.Add("alt-kf", "managed mp4 index read (moov stss/stts/ctts/elst only)", fx.Key, "warm", 5, t4.Median,
                $"runs {Fmt(t4.All)} | read {Io.Mb(idx?.BytesRead ?? 0)} | {(idx is null ? "NO RESULT" : Compare(reference, idx.KeyframeSeconds))}");

            var cold = Io.ColdCopy(fx.Path, fx.Key);
            try
            {
                var sw = Stopwatch.StartNew();
                var ci = Mp4Index.ReadKeyframes(cold);
                R.Add("alt-kf", "managed mp4 index read (moov only)", fx.Key, "cold", 1, sw.Elapsed.TotalSeconds, ci is null ? "NO RESULT" : Compare(reference, ci.KeyframeSeconds));
            }
            finally
            {
                Io.TryDelete(cold);
            }
        }
    }

    // ------------------------------------------------------------------ thumbnails

    private static async Task ThumbVariants()
    {
        foreach (var fx in new[] { Fixture.Get("4k"), Fixture.Get("g10"), Fixture.Get("hevc"), Fixture.Get("1080") })
        {
            var t = fx.ThumbMidGop.ToString(CultureInfo.InvariantCulture);
            var kf = fx.ThumbOnKf.ToString(CultureInfo.InvariantCulture);
            var dir = Svc.NewTempDir("thalt");
            var refJpg = Path.Combine(dir, "ref.jpg");
            var kfJpg = Path.Combine(dir, "kf.jpg");
            await Svc.RunFfmpeg("-ss", t, "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", refJpg);
            await Svc.RunFfmpeg("-ss", kf, "-i", fx.Path, "-frames:v", "1", "-vf", "scale=160:-1", "-y", kfJpg);

            var variants = new (string Name, string[] Pre, string Vf, string[] Post)[]
            {
                ("current (FfmpegThumbnailService.cs:207-214)", Array.Empty<string>(), "scale=160:-1", Array.Empty<string>()),
                ("-threads 1", new[] { "-threads", "1" }, "scale=160:-1", Array.Empty<string>()),
                ("-thread_type slice", new[] { "-thread_type", "slice" }, "scale=160:-1", Array.Empty<string>()),
                ("scale flags=fast_bilinear", Array.Empty<string>(), "scale=160:-1:flags=fast_bilinear", Array.Empty<string>()),
                ("-hwaccel d3d11va", new[] { "-hwaccel", "d3d11va" }, "scale=160:-1", Array.Empty<string>()),
                ("-hwaccel dxva2", new[] { "-hwaccel", "dxva2" }, "scale=160:-1", Array.Empty<string>()),
                ("-hwaccel cuda", new[] { "-hwaccel", "cuda" }, "scale=160:-1", Array.Empty<string>()),
                ("-hwaccel cuda + scale_cuda on GPU", new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" }, "scale_cuda=160:-2,hwdownload,format=nv12", Array.Empty<string>()),
                ("-noaccurate_seek (keyframe <= t)", new[] { "-noaccurate_seek" }, "scale=160:-1", Array.Empty<string>()),
                ("-skip_frame nokey (keyframe <= t)", new[] { "-skip_frame", "nokey" }, "scale=160:-1", Array.Empty<string>()),
            };

            foreach (var (name, pre, vf, post) in variants)
            {
                var outJpg = Path.Combine(dir, "v.jpg");
                var ok = true;
                var tm = await Times(4, async i =>
                {
                    Io.TryDelete(outJpg);
                    var args = pre.Concat(new[] { "-ss", t, "-i", fx.Path, "-frames:v", "1", "-vf", vf }).Concat(post).Concat(new[] { "-y", outJpg }).ToArray();
                    var (s, r) = await Svc.RunFfmpeg(args);
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
                    var pRef = await Svc.Psnr(refJpg, outJpg, "160:90");
                    var pKf = await Svc.Psnr(kfJpg, outJpg, "160:90");
                    eq = $"PSNR vs current {pRef:0.0} dB, vs keyframe frame {pKf:0.0} dB";
                }

                R.Add("alt-thumb", $"thumb 160px @{t}s {name}", fx.Key, "warm", 4, tm.Median, $"runs {Fmt(tm.All)} | {eq}");
            }

            Io.TryDelete(dir);
        }
    }

    // ------------------------------------------------------------------ exact cut (SmartCutEngine head re-encode)

    private static async Task<(int Frames, double Duration)> CountFrames(string path)
    {
        var (_, o) = await Svc.RunFfprobe("-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames:format=duration", "-of", "json", "-i", path);
        using var doc = JsonDocument.Parse(o);
        var frames = int.Parse(doc.RootElement.GetProperty("streams")[0].GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture);
        var dur = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return (frames, dur);
    }

    private static async Task<string> DecodeErrors(string path)
    {
        var (_, r) = await Svc.RunFfmpeg("-v", "error", "-i", path, "-f", "null", "-");
        var errs = r.StdErrTail.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return errs.Count == 0 ? "decodes clean" : $"{errs.Count} decode error line(s): {errs[0]}";
    }

    private static async Task SmartVariants()
    {
        foreach (var fx in new[] { Fixture.Get("4k"), Fixture.Get("hevc") })
        {
            var probe = Svc.NewProbe();
            var info = ((ProbeResult.ProbeSucceeded)await probe.ProbeAsync(fx.Path)).Info;
            var kfs = await probe.GetKeyframesAsync(fx.Path);
            SmartCutArgsBuilder.TryResolveEncoders(info, out var venc, out var aenc, out _);
            var vs = info.VideoStreams[0];
            var aud = info.AudioStreams[0];

            foreach (var start in new[] { fx.Intro, fx.Duration - 15.5 })
            {
                var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(start), null, kfs);
                var headEnd = plan.HeadEnd!.Value;
                var dur = headEnd - plan.Start;
                var dir = Svc.NewTempDir("smartalt");
                var sStart = SplitPlanner.ToFfmpegSeconds(plan.Start);
                var sDur = SplitPlanner.ToFfmpegSeconds(dur);
                string[] Tail(string vcodecArgsKey) => Array.Empty<string>();
                string[] Enc(params string[] videoEnc) => new[] { "-map", "0" }
                    .Concat(new[] { "-c:v" }).Concat(videoEnc)
                    .Concat(new[] { "-pix_fmt", vs.PixFmt!, "-s", $"{vs.Width}x{vs.Height}", "-c:a", aenc!, "-ar", aud.SampleRate!.Value.ToString(CultureInfo.InvariantCulture), "-ac", aud.Channels!.Value.ToString(CultureInfo.InvariantCulture) })
                    .ToArray();

                var nvenc = venc == "libx265" ? "hevc_nvenc" : "h264_nvenc";
                var variants = new (string Name, string[] Args)[]
                {
                    ($"current: -i in -ss {sStart} (output seek) {venc} default preset", new[] { "-y", "-i", fx.Path, "-ss", sStart, "-t", sDur }.Concat(Enc(venc!)).ToArray()),
                    ($"-ss {sStart} -i in (input seek, accurate) {venc} default preset", new[] { "-y", "-ss", sStart, "-i", fx.Path, "-t", sDur }.Concat(Enc(venc!)).ToArray()),
                    ($"input seek + {venc} -preset veryfast", new[] { "-y", "-ss", sStart, "-i", fx.Path, "-t", sDur }.Concat(Enc(venc!, "-preset", "veryfast")).ToArray()),
                    ($"input seek + {venc} -preset ultrafast", new[] { "-y", "-ss", sStart, "-i", fx.Path, "-t", sDur }.Concat(Enc(venc!, "-preset", "ultrafast")).ToArray()),
                    ($"input seek + {nvenc} -preset p4 -cq 19", new[] { "-y", "-ss", sStart, "-i", fx.Path, "-t", sDur }.Concat(Enc(nvenc, "-preset", "p4", "-rc", "vbr", "-cq", "19", "-b:v", "0")).ToArray()),
                    ($"input seek + -hwaccel cuda decode + {nvenc}", new[] { "-y", "-hwaccel", "cuda", "-ss", sStart, "-i", fx.Path, "-t", sDur }.Concat(Enc(nvenc, "-preset", "p4", "-rc", "vbr", "-cq", "19", "-b:v", "0")).ToArray()),
                };

                string? baseHead = null;
                (int Frames, double Duration) baseCount = default;
                foreach (var (name, args) in variants)
                {
                    var head = Path.Combine(dir, $"head{Array.IndexOf(variants, (name, args))}{Path.GetExtension(fx.Path)}");
                    var a2 = args.Concat(new[] { head }).ToArray();
                    var runs = name.StartsWith("current", StringComparison.Ordinal) ? 2 : 3;
                    var ok = true;
                    var tm = await Times(runs, async _ =>
                    {
                        var (s, r) = await Svc.RunFfmpeg(a2);
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
                        var cnt = await CountFrames(head);
                        if (baseHead is null)
                        {
                            baseHead = head;
                            baseCount = cnt;
                            eq = $"head {cnt.Frames} frames {cnt.Duration:0.000}s (reference)";
                        }
                        else
                        {
                            var psnr = await Svc.Psnr(baseHead, head);
                            eq = $"head {cnt.Frames} frames {cnt.Duration:0.000}s (ref {baseCount.Frames}/{baseCount.Duration:0.000}) | PSNR vs current head {psnr:0.0} dB";
                        }

                        // Build the full exact output the engine would ship (tail copy + concat via the engine's builders).
                        var tail = Path.Combine(dir, "tail" + Path.GetExtension(fx.Path));
                        var final = Path.Combine(dir, "final" + Path.GetExtension(fx.Path));
                        await Svc.Ffmpeg.RunAsync(SmartCutArgsBuilder.TailCopy(fx.Path, headEnd, null, tail));
                        var list = Path.Combine(dir, "list.txt");
                        await File.WriteAllTextAsync(list, JoinArgsBuilder.RenderConcatList(new[] { head, tail }));
                        await Svc.Ffmpeg.RunAsync(JoinArgsBuilder.ConcatCopy(list, final));
                        var fc = await CountFrames(final);
                        var expectFrames = (int)Math.Round((fx.Duration - start) * 30);
                        eq += $" | final {fc.Frames} frames (expect ~{expectFrames}) {fc.Duration:0.000}s | {await DecodeErrors(final)}";
                        Io.TryDelete(tail);
                        Io.TryDelete(final);
                    }

                    R.Add("alt-smart", $"exact head @{start}s: {name}", fx.Key, "warm", runs, tm.Median, $"runs {Fmt(tm.All)} | {eq}");
                }

                Io.TryDelete(dir);
            }
        }
    }

    // ------------------------------------------------------------------ join

    private static async Task JoinVariants()
    {
        var fx = Fixture.Get("4k");
        var parts = Directory.GetFiles(Path.Combine(P.Work, "split-4k"), "*_part0*.mp4").OrderBy(x => x).ToList();
        if (parts.Count != 3)
        {
            R.Note("join alt: run `core 4k split` first");
            return;
        }

        var dir = Svc.NewTempDir("joinalt");
        var list = Path.Combine(dir, "list.txt");
        await File.WriteAllTextAsync(list, JoinArgsBuilder.RenderConcatList(parts));
        var outA = Path.Combine(dir, "a.mp4");
        var outB = Path.Combine(dir, "b.mp4");
        var tA = await Times(4, async _ => (await Svc.RunFfmpeg(JoinArgsBuilder.ConcatCopy(list, outA).ToList().Skip(2).ToArray())).S);
        R.Add("alt-join", "current concat (JoinArgsBuilder.cs:38)", fx.Key, "warm", 4, tA.Median, $"runs {Fmt(tA.All)}");
        var tB = await Times(4, async _ => (await Svc.RunFfmpeg("-y", "-f", "concat", "-safe", "0", "-auto_convert", "0", "-i", list, "-map", "0", "-c", "copy", outB)).S);
        var hA = (await Svc.RunFfmpeg("-i", outA, "-map", "0", "-c", "copy", "-f", "streamhash", "-hash", "md5", Path.Combine(dir, "ha.txt"))).R;
        var hB = (await Svc.RunFfmpeg("-i", outB, "-map", "0", "-c", "copy", "-f", "streamhash", "-hash", "md5", Path.Combine(dir, "hb.txt"))).R;
        var same = File.ReadAllText(Path.Combine(dir, "ha.txt")) == File.ReadAllText(Path.Combine(dir, "hb.txt"));
        R.Add("alt-join", "concat -auto_convert 0", fx.Key, "warm", 4, tB.Median, $"runs {Fmt(tB.All)} | packet streamhash {(same ? "IDENTICAL" : "DIFFERS")} (exit {hA.ExitCode}/{hB.ExitCode})");
        var tC = await Times(3, async _ => (await Svc.RunFfmpeg("-y", "-i", fx.Path, "-map", "0", "-c", "copy", Path.Combine(dir, "c.mp4"))).S);
        R.Add("alt-join", "reference: ffmpeg -c copy of the same bytes as ONE file", fx.Key, "warm", 3, tC.Median, $"runs {Fmt(tC.All)}");
        Io.TryDelete(dir);
    }

    // ------------------------------------------------------------------ waveform

    private static async Task WaveformVariants()
    {
        foreach (var fx in new[] { Fixture.Get("4k"), Fixture.Get("1080") })
        {
            var dir = Svc.NewTempDir("wfalt");
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
                var tm = await Times(4, async _ => (await Svc.RunFfmpeg(args.Concat(new[] { pcm }).ToArray())).S);
                var bytes = await File.ReadAllBytesAsync(pcm);
                reference ??= bytes;
                R.Add("alt-wf", "waveform " + name, fx.Key, "warm", 4, tm.Median, $"runs {Fmt(tm.All)} | pcm {(bytes.AsSpan().SequenceEqual(reference) ? "IDENTICAL" : "DIFFERS")}");
            }

            Io.TryDelete(dir);
        }
    }

    // ------------------------------------------------------------------ decode throughput (proxy for the FFME preview)

    private static async Task DecodeVariants()
    {
        foreach (var fx in new[] { Fixture.Get("4k"), Fixture.Get("hevc"), Fixture.Get("1080") })
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
                    (await Svc.RunFfmpeg(pre.Concat(new[] { "-ss", "20", "-i", fx.Path, "-t", "10", "-an" }).Concat(post).Concat(new[] { "-f", "null", "-" }).ToArray())).S);
                R.Add("alt-dec", $"decode 10s (300 frames): {name}", fx.Key, "warm", 3, tm.Median, $"runs {Fmt(tm.All)} | {300 / tm.Median:0} fps = {300 / tm.Median / 30:0.0}x realtime");
            }
        }
    }
}
