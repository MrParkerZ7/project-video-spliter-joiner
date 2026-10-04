using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Bench;

/// <summary>
/// One shape in the fixture matrix: how it is made (<see cref="Kind"/>), what its self-check expects, and the
/// times the scenarios use on it (the T-187 values, so "before" figures stay comparable).
/// </summary>
internal sealed record FixtureSpec(
    string Key,
    string File,
    string Kind,
    string Codec,
    int Width,
    int Height,
    string Rate,
    double Duration,
    int GopFrames,
    int BFrames,
    long VideoBitrate,
    bool CheckBitrate,
    string Container,
    string? SourceKey,
    int Repeat,
    double[] Cuts,
    double Intro,
    double Outro,
    double ThumbOnKf,
    double ThumbMidGop,
    double[] SmartAt,
    string UsedBy)
{
    /// <summary>The frame rate as a number.</summary>
    public double Fps => Rate.Contains('/')
        ? double.Parse(Rate[..Rate.IndexOf('/')], CultureInfo.InvariantCulture) / double.Parse(Rate[(Rate.IndexOf('/') + 1)..], CultureInfo.InvariantCulture)
        : double.Parse(Rate, CultureInfo.InvariantCulture);

    /// <summary>The GOP length in seconds.</summary>
    public double Gop => GopFrames / Fps;

    /// <summary>
    /// Where the video stream should start, which the self-check checks to 1 ms. An encode starts it at 0 (the mp4
    /// muxer's edit list hides the AAC encoder's 1024-sample priming). A <c>-c copy</c> concat or remux carries the
    /// priming packet over and shifts every stream so audio starts at 0, so the video starts one AAC frame
    /// (1024/48000 s, about 21 ms) later: <c>4k10m</c> and <c>mkv</c>. The scenarios plan from the probed start.
    /// </summary>
    public double ExpectedVideoStart => Kind is "concat" or "remux" ? FixtureGenerator.AacPrimingSeconds : 0;

    /// <summary>The recipe name stamped on results: the encoder that made the pixels, and how the file was derived.</summary>
    public string RecipeName(bool nvenc, string? sourceRecipe = null) => Kind switch
    {
        "concat" => $"{sourceRecipe ?? "?"} concat x{Repeat}",
        "remux" => $"{sourceRecipe ?? "?"} remux {Container}",
        _ => nvenc ? (Codec == "hevc" ? "hevc_nvenc" : "h264_nvenc") : (Codec == "hevc" ? "libx265" : "libx264"),
    };
}

/// <summary>
/// T-188: the fixture matrix. Only the shapes some now-ticket's Build log names; the first five are the T-187
/// shapes. Shapes a ticket checks for correctness rather than times are generated inside its own tests.
/// </summary>
internal static class FixtureMatrix
{
    public static readonly FixtureSpec[] All =
    {
        new("4k", "4k_h264_g2s_120s.mp4", "encode", "h264", 3840, 2160, "30", 120, 60, 2, 60_000_000, true, "mp4", null, 1,
            new[] { 30.3, 70.7 }, 10.5, 100.3, 60, 61, new[] { 10.5, 104.5 }, "all"),
        new("1080", "1080_h264_g2s_120s.mp4", "encode", "h264", 1920, 1080, "30", 120, 60, 2, 15_000_000, true, "mp4", null, 1,
            new[] { 30.3, 70.7 }, 10.5, 100.3, 60, 61, new[] { 10.5, 104.5 }, "T-192, T-193, T-195"),
        new("hevc", "4k_hevc_g2s_60s.mp4", "encode", "hevc", 3840, 2160, "30", 60, 60, 0, 50_000_000, false, "mp4", null, 1,
            new[] { 20.3, 40.7 }, 10.5, 50.3, 30, 31, new[] { 10.5, 44.5 }, "T-194, T-195"),
        new("g10", "4k_h264_g10s_60s.mp4", "encode", "h264", 3840, 2160, "30", 60, 300, 2, 60_000_000, true, "mp4", null, 1,
            new[] { 20.3, 40.7 }, 12.5, 47.3, 30, 39, new[] { 12.5, 44.5 }, "T-191, T-192, T-195, T-196"),
        new("4k10m", "4k_h264_g2s_600s.mp4", "concat", "h264", 3840, 2160, "30", 600, 60, 2, 60_000_000, true, "mp4", "4k", 5,
            new[] { 150.3, 400.7 }, 10.5, 580.3, 300, 301, new[] { 10.5, 540.5 }, "T-190, T-191, T-192, T-193, T-194, T-219"),
        new("ntsc", "1080_h264_ntsc_g2s_120s.mp4", "encode", "h264", 1920, 1080, "30000/1001", 120, 60, 2, 15_000_000, true, "mp4", null, 1,
            new[] { 30.3, 70.7 }, 10.5, 100.3, 60.06, 61, new[] { 10.5, 104.5 }, "T-196"),
        new("mkv", "4k_h264_g2s_120s.mkv", "remux", "h264", 3840, 2160, "30", 120, 60, 2, 60_000_000, true, "mkv", "4k", 1,
            new[] { 30.3, 70.7 }, 10.5, 100.3, 60, 61, new[] { 10.5, 104.5 }, "T-194, T-195"),
    };

    public static FixtureSpec? Find(string key) => All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
}

/// <summary>
/// A fixture a scenario runs on: a resolved path inside the root plus the times to use. <see cref="ThumbOnKf"/> is
/// one of the file's own keyframes and <see cref="ThumbMidGop"/> lies the same distance past it as in the matrix;
/// <see cref="VideoStart"/> is the video stream's probed start (non-zero on <c>4k10m</c> and <c>mkv</c>), and
/// <see cref="Keyframes"/> its keyframe times.
/// </summary>
internal sealed record Fixture(
    string Key, string File, string Path, double Duration, double Gop,
    double[] Cuts, double Intro, double Outro, double ThumbOnKf, double ThumbMidGop, double[] SmartAt, double VideoStart,
    double[] Keyframes)
{
    /// <summary>
    /// A matrix fixture, with its named thumbnail times snapped to the keyframe list and video start its self-check
    /// recorded. An entry from an older bench has neither: re-check it (no re-encode) with <c>bench fixtures</c>.
    /// </summary>
    public static Fixture From(FixtureSpec s, string resolvedPath, ManifestEntry entry)
    {
        if (entry.KeyframeTimes is not { Length: > 0 } kf || entry.VideoStartTime is not { } videoStart)
        {
            throw new BenchException(ExitCodes.NoFixture,
                $"fixture '{s.Key}' was checked by an older bench: its fixtures.json entry has no keyframe list or video start time. " +
                $"Run `bench fixtures {s.Key}` to re-check it (no re-encode).");
        }

        var onKf = kf.MinBy(k => Math.Abs(k - s.ThumbOnKf));
        return new(s.Key, s.File, resolvedPath, s.Duration, s.Gop, s.Cuts, s.Intro, s.Outro,
            onKf, Math.Round(onKf + (s.ThumbMidGop - s.ThumbOnKf), 6), s.SmartAt, videoStart, kf);
    }

    /// <summary>
    /// A file under the root that is not a matrix key (the selftest's clips): times derived from its probed
    /// duration and keyframes, proportional to the 2-minute matrix shapes.
    /// </summary>
    public static async Task<Fixture> FromPath(BenchContext c, string resolvedPath)
    {
        var probe = c.NewProbe();
        if (await probe.ProbeAsync(resolvedPath).ConfigureAwait(false) is not ProbeResult.ProbeSucceeded ok)
        {
            throw new BenchException(ExitCodes.NoFixture, $"'{resolvedPath}' is not a media file ffprobe can read.");
        }

        var d = ok.Info.Duration.TotalSeconds;
        var kf = await probe.GetKeyframesAsync(resolvedPath).ConfigureAwait(false);
        var gop = probe.AverageGop(kf).TotalSeconds;
        if (gop <= 0)
        {
            gop = d;
        }

        var mid = kf.Count > 0 ? kf[kf.Count / 2].TotalSeconds : d / 2;
        var name = System.IO.Path.GetFileName(resolvedPath);
        var key = name.Length > 12 ? name[..12] : name;
        return new Fixture(
            key, name, resolvedPath, d, gop,
            new[] { Math.Round(d * 0.2525, 3), Math.Round(d * 0.5892, 3) },
            Math.Round(d * 0.0875, 3), Math.Round(d * 0.8358, 3),
            mid, Math.Min(d, mid + (gop / 2)),
            new[] { Math.Round(d * 0.0875, 3), Math.Round(Math.Max(d * 0.5, d - 15.5), 3) },
            await VideoStartAsync(c, resolvedPath).ConfigureAwait(false) ?? 0,
            kf.Select(k => Math.Round(k.TotalSeconds, 6)).ToArray());
    }

    /// <summary>The first video stream's <c>start_time</c>, or null when ffprobe gives none.</summary>
    public static async Task<double?> VideoStartAsync(BenchContext c, string resolvedPath)
    {
        var (_, json) = await c.RunFfprobe("-v", "error", "-select_streams", "v:0", "-show_entries", "stream=start_time", "-of", "json", "-i", resolvedPath)
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0
            && streams[0].TryGetProperty("start_time", out var st)
            && double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }
}

/// <summary>One fixture that passed its self-check.</summary>
internal sealed class ManifestEntry
{
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("recipe")]
    public string Recipe { get; set; } = string.Empty;

    [JsonPropertyName("nvenc")]
    public bool Nvenc { get; set; }

    [JsonPropertyName("length")]
    public long Length { get; set; }

    [JsonPropertyName("mtime_utc_ticks")]
    public long MtimeUtcTicks { get; set; }

    /// <summary><c>format.start_time</c> (the earliest stream's start).</summary>
    [JsonPropertyName("start_time")]
    public double? StartTime { get; set; }

    /// <summary>The video stream's own <c>start_time</c> (about 0.021 s on the <c>-c copy</c> derived fixtures).</summary>
    [JsonPropertyName("video_start_time")]
    public double? VideoStartTime { get; set; }

    /// <summary>The audio stream's own <c>start_time</c>.</summary>
    [JsonPropertyName("audio_start_time")]
    public double? AudioStartTime { get; set; }

    [JsonPropertyName("bit_rate")]
    public long? BitRate { get; set; }

    [JsonPropertyName("keyframes")]
    public int Keyframes { get; set; }

    /// <summary>The keyframe times (s) from the self-check's packet scan; the named thumbnail times snap to them.</summary>
    [JsonPropertyName("keyframe_times")]
    public double[]? KeyframeTimes { get; set; }

    [JsonPropertyName("checked_utc")]
    public string CheckedUtc { get; set; } = string.Empty;
}

/// <summary>
/// <c>&lt;root&gt;/fixtures.json</c>: the fixtures that passed their self-check, each with the length and time
/// stamp it had then. A fixture counts as valid only while its file still matches — <c>bench core</c> refuses
/// anything else.
/// </summary>
internal sealed class Manifest
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    [JsonPropertyName("fixtures")]
    public Dictionary<string, ManifestEntry> Fixtures { get; set; } = new(StringComparer.Ordinal);

    public static Manifest Load(FixtureRoot root)
    {
        try
        {
            if (File.Exists(root.ManifestFile))
            {
                return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(root.ManifestFile), Options) ?? new Manifest();
            }
        }
        catch (JsonException)
        {
            // A corrupt manifest counts as empty: every fixture is checked again.
        }

        return new Manifest();
    }

    public void Save(FixtureRoot root)
    {
        Directory.CreateDirectory(root.Real);
        var tmp = root.ManifestFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, root.ManifestFile, overwrite: true);
    }

    /// <summary>The entry for <paramref name="key"/> when its file is still exactly what passed the self-check.</summary>
    public ManifestEntry? Valid(FixtureRoot root, string key)
    {
        if (!Fixtures.TryGetValue(key, out var e))
        {
            return null;
        }

        var path = root.Child(e.File);
        if (!File.Exists(path))
        {
            return null;
        }

        var fi = new FileInfo(path);
        return fi.Length == e.Length && fi.LastWriteTimeUtc.Ticks == e.MtimeUtcTicks ? e : null;
    }

    public IEnumerable<string> ValidKeys(FixtureRoot root) => Fixtures.Keys.Where(k => Valid(root, k) is not null);
}

/// <summary>A fixture's self-check outcome, with the probed values the manifest records.</summary>
internal sealed record SelfCheck(
    IReadOnlyList<(string Name, bool Ok, string Detail)> Checks,
    double? StartTime,
    long? BitRate,
    int Keyframes,
    double? VideoStart = null,
    double? AudioStart = null,
    double[]? KeyframeTimes = null)
{
    public bool Passed => Checks.All(c => c.Ok);

    /// <summary>Copy the probed values into a manifest entry.</summary>
    public void CopyTo(ManifestEntry e)
    {
        e.StartTime = StartTime;
        e.VideoStartTime = VideoStart;
        e.AudioStartTime = AudioStart;
        e.BitRate = BitRate;
        e.Keyframes = Keyframes;
        e.KeyframeTimes = KeyframeTimes;
    }

    public string Failures => string.Join("; ", Checks.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}"));

    public string Summary => Passed ? $"self-check PASS ({Checks.Count} checks)" : "self-check FAIL: " + Failures;
}

/// <summary>
/// T-188: <c>bench fixtures [--nvenc] [keys…]</c>. Builds the matrix into the root from lavfi <c>testsrc2</c> +
/// <c>sine</c> through the app's <c>FfmpegRunner</c> and typed <c>FfmpegArgs</c> (libx264/libx265 by default, so
/// it needs no NVIDIA GPU; <c>--nvenc</c> reproduces the T-187 evidence recipe), self-checks every output with
/// ffprobe before it counts, and records each fixture's <c>format.start_time</c> in the results file.
/// </summary>
internal static class FixtureGenerator
{
    /// <summary>One AAC frame of encoder priming at 48 kHz: 1024 samples.</summary>
    public const double AacPrimingSeconds = 1024.0 / 48000;

    private const long AudioBitrate = 192_000;

    public static async Task<int> Run(BenchContext c, IReadOnlyList<string> keys, bool nvenc, bool force)
    {
        var wanted = new List<FixtureSpec>();
        foreach (var k in keys)
        {
            wanted.Add(FixtureMatrix.Find(k) ?? throw new BenchException(ExitCodes.Usage,
                $"unknown fixture '{k}'. The matrix is: {string.Join(", ", FixtureMatrix.All.Select(f => f.Key))}."));
        }

        if (wanted.Count == 0)
        {
            wanted.AddRange(FixtureMatrix.All);
        }

        // A derived fixture needs its source first.
        foreach (var s in wanted.ToList())
        {
            if (s.SourceKey is { } src && wanted.All(w => w.Key != src))
            {
                wanted.Add(FixtureMatrix.Find(src)!);
            }
        }

        var order = FixtureMatrix.All.Where(m => wanted.Any(w => w.Key == m.Key)).ToList();
        Directory.CreateDirectory(c.Root.Real);
        c.Started = true;
        var failed = new List<string>();
        foreach (var spec in order)
        {
            var manifest = Manifest.Load(c.Root);
            var existing = manifest.Valid(c.Root, spec.Key);
            var sourceOk = spec.SourceKey is null || failed.All(f => f != spec.SourceKey);
            if (!sourceOk)
            {
                failed.Add(spec.Key);
                c.Results.Note($"{spec.Key}: skipped, its source '{spec.SourceKey}' failed");
                continue;
            }

            if (!force && existing is not null && existing.Nvenc == nvenc)
            {
                // Already made with the requested recipe: check it again (cheap) rather than re-encode.
                c.Recipes[spec.Key] = existing.Recipe;
                var path = c.Root.Resolve(c.Root.Child(spec.File));
                var (ok, recheck) = await CheckAndRecord(c, spec, path, existing.Recipe, existing.Nvenc, "re-check").ConfigureAwait(false);
                if (ok)
                {
                    // Refresh what the self-check probed (an older bench recorded no keyframe list or stream starts).
                    var m = Manifest.Load(c.Root);
                    if (m.Fixtures.TryGetValue(spec.Key, out var e))
                    {
                        recheck.CopyTo(e);
                        m.Save(c.Root);
                    }

                    continue;
                }
            }

            if ((await GenerateOne(c, spec, nvenc).ConfigureAwait(false)) is not { Passed: true })
            {
                failed.Add(spec.Key);
            }
        }

        if (failed.Count > 0)
        {
            throw new BenchException(ExitCodes.CheckFailed, $"fixtures not valid: {string.Join(", ", failed)} (see the self-check lines above).");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Generate <paramref name="spec"/> into the root, self-check it, and record it as valid only when every check
    /// passes. Returns the self-check (null when ffmpeg itself failed); a failed check leaves no file and no entry.
    /// <paramref name="encodeAs"/> (selftest only) encodes a different recipe while still checking against
    /// <paramref name="spec"/> — a deliberately wrong fixture, such as the 4k recipe made with <c>-bf 0</c>.
    /// </summary>
    public static async Task<SelfCheck?> GenerateOne(BenchContext c, FixtureSpec spec, bool nvenc, FixtureSpec? encodeAs = null)
    {
        // Forget any earlier entry first, so a failed regeneration can never leave a stale "valid" behind.
        var manifest = Manifest.Load(c.Root);
        string? sourceRecipe = null;
        string? sourcePath = null;
        if (spec.SourceKey is { } srcKey)
        {
            var src = manifest.Valid(c.Root, srcKey) ?? throw new BenchException(ExitCodes.NoFixture, $"'{spec.Key}' needs '{srcKey}' first.");
            sourceRecipe = src.Recipe;
            sourcePath = c.Root.Resolve(c.Root.Child(src.File));
        }

        if (manifest.Fixtures.Remove(spec.Key))
        {
            manifest.Save(c.Root);
        }

        var finalPath = c.Root.Child(spec.File);
        var partial = c.Root.Child(Path.GetFileNameWithoutExtension(spec.File) + ".partial" + Path.GetExtension(spec.File));
        PreflightSpace(c, spec, sourcePath);

        var recipe = spec.RecipeName(nvenc, sourceRecipe);
        c.Recipes[spec.Key] = recipe;
        FfmpegArgs args;
        string? listFile = null;
        switch (spec.Kind)
        {
            case "concat":
                Directory.CreateDirectory(c.Work);
                listFile = Path.Combine(c.Work, $"concat-{spec.Key}.txt");
                await File.WriteAllTextAsync(listFile, JoinArgsBuilder.RenderConcatList(Enumerable.Repeat(sourcePath!, spec.Repeat).ToList())).ConfigureAwait(false);
                args = JoinArgsBuilder.ConcatCopy(listFile, partial);
                break;
            case "remux":
                args = FfmpegArgs.ForFfmpeg().Raw("-y").Input(sourcePath!).Raw("-map", "0", "-c", "copy").Output(partial);
                break;
            default:
                args = EncodeArgs(encodeAs ?? spec, nvenc, partial);
                break;
        }

        Io.TryDelete(partial);
        FfmpegResult? run = null;
        var (s, u) = await Meter.Run(async () => run = await c.Ffmpeg.RunAsync(args).ConfigureAwait(false)).ConfigureAwait(false);
        if (listFile is not null)
        {
            Io.TryDelete(listFile);
        }

        if (run is null || !run.Success || !File.Exists(partial))
        {
            var tail = run is null ? "no result" : string.Join(" / ", run.StdErrTail.TakeLast(3));
            c.Results.Add("fixtures", $"generate {spec.Key}", spec.Key, "-", 1, s, u, $"FAILED exit {run?.ExitCode}: {tail}");
            Io.TryDelete(partial);
            return null;
        }

        var size = new FileInfo(partial).Length;
        c.Results.Add("fixtures", $"generate {spec.Key}", spec.Key, "-", 1, s, u,
            $"{Io.Mb(size)}, {size * 8.0 / spec.Duration / 1e6:0.0} Mbit/s, {string.Join(' ', args.ToList().Skip(2)).Replace(c.Root.Real, "<root>", StringComparison.OrdinalIgnoreCase)}");

        var (ok, check) = await CheckAndRecord(c, spec, c.Root.Resolve(partial), recipe, nvenc, "check").ConfigureAwait(false);
        if (!ok)
        {
            Io.TryDelete(partial);
            return check;
        }

        File.Move(partial, finalPath, overwrite: true);
        var fi = new FileInfo(finalPath);
        manifest = Manifest.Load(c.Root);
        var entry = new ManifestEntry
        {
            File = spec.File,
            Recipe = recipe,
            Nvenc = nvenc,
            Length = fi.Length,
            MtimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
            CheckedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        check.CopyTo(entry);
        manifest.Fixtures[spec.Key] = entry;
        manifest.Save(c.Root);
        return check;
    }

    private static async Task<(bool Ok, SelfCheck Check)> CheckAndRecord(BenchContext c, FixtureSpec spec, string path, string recipe, bool nvenc, string verb)
    {
        SelfCheck? check = null;
        var (s, u) = await Meter.Run(async () => check = await SelfCheckAsync(c, spec, path).ConfigureAwait(false)).ConfigureAwait(false);
        static object Num(double? v) => v is { } x ? x : "n/a";
        static string Txt(double? v) => v?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "n/a";
        var extra = new Dictionary<string, object>
        {
            ["start_time"] = Num(check!.StartTime),
            ["video_start_time"] = Num(check.VideoStart),
            ["audio_start_time"] = Num(check.AudioStart),
            ["checks"] = check.Checks.ToDictionary(x => x.Name, x => (object)((x.Ok ? "ok " : "FAIL ") + x.Detail)),
        };
        c.Results.Add("fixtures", $"selfcheck {spec.Key} ({verb})", spec.Key, "-", 1, s, u,
            $"{check.Summary}; format.start_time {Txt(check.StartTime)}; video start_time {Txt(check.VideoStart)}; audio start_time {Txt(check.AudioStart)}; {recipe}",
            extra);
        return (check.Passed, check);
    }

    private static FfmpegArgs EncodeArgs(FixtureSpec s, bool nvenc, string output)
    {
        var d = s.Duration.ToString("0.###", CultureInfo.InvariantCulture);
        var a = FfmpegArgs.ForFfmpeg()
            .Raw("-y")
            .Raw("-f", "lavfi").Input($"testsrc2=size={s.Width}x{s.Height}:rate={s.Rate}:duration={d}")
            .Raw("-f", "lavfi").Input($"sine=frequency=440:sample_rate=48000:duration={d}")
            .Raw("-map", "0:v:0", "-map", "1:a:0");
        var g = s.GopFrames.ToString(CultureInfo.InvariantCulture);
        var br = s.VideoBitrate.ToString(CultureInfo.InvariantCulture);
        var br2 = (s.VideoBitrate * 2).ToString(CultureInfo.InvariantCulture);
        if (s.Codec == "hevc")
        {
            if (nvenc)
            {
                a.Raw("-c:v", "hevc_nvenc", "-preset", "p2", "-rc", "cbr", "-b:v", br, "-maxrate", br, "-bufsize", br2,
                    "-profile:v", "main", "-pix_fmt", "yuv420p", "-g", g, "-no-scenecut", "1");
            }
            else
            {
                a.Raw("-c:v", "libx265", "-preset", "ultrafast", "-profile:v", "main", "-pix_fmt", "yuv420p",
                    "-b:v", br, "-maxrate", br, "-bufsize", br2,
                    "-x265-params", $"keyint={g}:min-keyint={g}:scenecut=0:open-gop=0:log-level=error");
            }

            a.Raw("-tag:v", "hvc1");
        }
        else if (nvenc)
        {
            a.Raw("-c:v", "h264_nvenc", "-preset", "p2", "-rc", "cbr", "-b:v", br, "-maxrate", br, "-bufsize", br2,
                "-profile:v", "high", "-pix_fmt", "yuv420p", "-g", g, "-bf", s.BFrames.ToString(CultureInfo.InvariantCulture), "-no-scenecut", "1");
        }
        else
        {
            a.Raw("-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-pix_fmt", "yuv420p",
                "-g", g, "-keyint_min", g, "-sc_threshold", "0", "-bf", s.BFrames.ToString(CultureInfo.InvariantCulture));
            if (s.VideoBitrate > 0)
            {
                // CBR with filler, so the file has the ~60 Mbit/s of a real 4K camera file even from a simple test pattern.
                a.Raw("-b:v", br, "-minrate", br, "-maxrate", br, "-bufsize", br, "-x264-params", "nal-hrd=cbr");
            }
        }

        return a.Raw("-c:a", "aac", "-b:a", AudioBitrate.ToString(CultureInfo.InvariantCulture), "-ar", "48000", "-ac", "2", "-t", d)
            .Output(output);
    }

    /// <summary>Refuse to start when the root's drive cannot hold the fixture (4k10m needs about 5 GB).</summary>
    private static void PreflightSpace(BenchContext c, FixtureSpec spec, string? sourcePath)
    {
        var estimate = spec.Kind switch
        {
            "concat" => (long)(new FileInfo(sourcePath!).Length * spec.Repeat * 1.05),
            "remux" => (long)(new FileInfo(sourcePath!).Length * 1.05),
            _ => (long)((Math.Max(spec.VideoBitrate, 20_000_000) + AudioBitrate) * spec.Duration / 8 * 1.15),
        };
        Io.EnsureFreeSpace(c, estimate, $"generating '{spec.Key}'");
    }

    /// <summary>The ffprobe self-check: codec, size, frame rate, GOP, B-frames, tag, audio, duration, moov position, start time, bitrate.</summary>
    public static async Task<SelfCheck> SelfCheckAsync(BenchContext c, FixtureSpec spec, string path)
    {
        var checks = new List<(string, bool, string)>();
        void Check(string name, bool ok, string detail) => checks.Add((name, ok, detail));

        var json = await c.Ffprobe.RunJsonAsync(FfmpegArgs.ForFfprobe()
            .Raw("-v", "error", "-show_streams", "-show_format", "-print_format", "json").Input(path)).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var streams = root.TryGetProperty("streams", out var st) ? st.EnumerateArray().ToList() : new List<JsonElement>();
        var format = root.TryGetProperty("format", out var f) ? f : default;
        string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v.ToString() : string.Empty;
        double? D(JsonElement e, string k) => double.TryParse(S(e, k), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null;

        var video = streams.Where(x => S(x, "codec_type") == "video").ToList();
        var audio = streams.Where(x => S(x, "codec_type") == "audio").ToList();
        var formatName = S(format, "format_name");
        var wantContainer = spec.Container == "mkv" ? "matroska" : "mp4";
        Check("container", formatName.Contains(wantContainer, StringComparison.Ordinal), formatName);
        Check("one video stream", video.Count == 1, $"{video.Count}");
        var v = video.FirstOrDefault();
        Check("codec", S(v, "codec_name") == spec.Codec, S(v, "codec_name"));
        var wantProfile = spec.Codec == "hevc" ? "Main" : "High";
        Check("profile", S(v, "profile") == wantProfile, S(v, "profile"));
        if (spec.Container == "mp4")
        {
            var wantTag = spec.Codec == "hevc" ? "hvc1" : "avc1";
            Check("tag", S(v, "codec_tag_string") == wantTag, S(v, "codec_tag_string"));
        }

        Check("size", S(v, "width") == spec.Width.ToString(CultureInfo.InvariantCulture) && S(v, "height") == spec.Height.ToString(CultureInfo.InvariantCulture),
            $"{S(v, "width")}x{S(v, "height")}");
        var wantRate = spec.Rate.Contains('/') ? spec.Rate : spec.Rate + "/1";
        Check("frame rate", S(v, "r_frame_rate") == wantRate, S(v, "r_frame_rate"));
        Check("pixel format", S(v, "pix_fmt") == "yuv420p", S(v, "pix_fmt"));
        if (spec.Codec == "h264")
        {
            var hb = int.TryParse(S(v, "has_b_frames"), out var h) ? h : -1;
            Check("B-frames", spec.BFrames > 0 ? hb >= 1 : hb == 0,
                $"has_b_frames={hb}, expected {(spec.BFrames > 0 ? ">= 1" : "0")} (recipe -bf {spec.BFrames})");
        }

        var a = audio.FirstOrDefault();
        Check("audio", audio.Count == 1 && S(a, "codec_name") == "aac" && S(a, "sample_rate") == "48000" && S(a, "channels") == "2",
            $"{audio.Count} stream(s) {S(a, "codec_name")} {S(a, "sample_rate")} Hz {S(a, "channels")} ch");

        var duration = D(format, "duration");
        var tolerance = spec.Kind == "concat" ? 0.5 : 0.25;
        Check("duration", duration is { } du && Math.Abs(du - spec.Duration) <= tolerance,
            $"{duration?.ToString("0.000", CultureInfo.InvariantCulture) ?? "n/a"} s, expected {spec.Duration} ± {tolerance}");

        // GOP: the keyframe list from the app's own probe (the packet scan), evenly spaced at the recipe's GOP.
        var kf = await c.NewProbe().GetKeyframesAsync(path).ConfigureAwait(false);
        var frames = (int)Math.Round(spec.Duration * spec.Fps);
        var wantCount = (int)Math.Ceiling(frames / (double)spec.GopFrames);
        var halfFrame = 0.5 / spec.Fps;
        var worst = 0.0;
        for (var i = 1; i < kf.Count; i++)
        {
            worst = Math.Max(worst, Math.Abs((kf[i] - kf[i - 1]).TotalSeconds - spec.Gop));
        }

        Check("GOP", kf.Count > 0 && Math.Abs(kf.Count - wantCount) <= 1 && worst <= halfFrame,
            $"{kf.Count} keyframes (expected {wantCount}), spacing off by at most {worst * 1000:0.0} ms from {spec.Gop:0.###} s");

        if (spec.Container == "mp4")
        {
            var (moov, mdat) = TopLevelBoxes(path);
            Check("moov at end", moov > 0 && mdat >= 0 && moov > mdat, $"moov at {moov}, mdat at {mdat}");
        }

        // format.start_time is the earliest stream's start, so it hides a late video stream; the video's own start is
        // checked against the recipe's expected value (0 for an encode, one AAC priming frame for a -c copy derivative).
        static string T(double? v) => v?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "n/a";
        var start = D(format, "start_time");
        Check("start_time", start is { } s0 && Math.Abs(s0) < 0.001,
            $"format.start_time {T(start)} (zero expected: the audio starts at 0 in every recipe)");
        var videoStart = D(v, "start_time");
        var audioStart = D(a, "start_time");
        var wantVideo = spec.ExpectedVideoStart;
        Check("video start_time", videoStart is { } vs && Math.Abs(vs - wantVideo) < 0.001,
            $"video start_time {T(videoStart)}, expected {T(wantVideo)} " +
            (wantVideo == 0
                ? "(an encode: the mp4 muxer's edit list starts the video at 0)"
                : "(a -c copy concat/remux carries the AAC priming over, so the video starts one AAC frame after the audio)") +
            $"; audio start_time {T(audioStart)}");

        long? bitRate = long.TryParse(S(format, "bit_rate"), out var b) ? b : null;
        if (spec.CheckBitrate)
        {
            var want = spec.VideoBitrate + AudioBitrate;
            Check("bitrate", bitRate is { } br && br >= want * 0.85 && br <= want * 1.2,
                $"{(bitRate ?? 0) / 1e6:0.0} Mbit/s, expected about {want / 1e6:0.0}");
        }

        return new SelfCheck(checks, start, bitRate, kf.Count, videoStart, audioStart,
            kf.Select(k => Math.Round(k.TotalSeconds, 6)).ToArray());
    }

    /// <summary>Offsets of the top-level <c>moov</c> and <c>mdat</c> boxes (-1 when absent). Reads box headers only.</summary>
    private static (long Moov, long Mdat) TopLevelBoxes(string path)
    {
        long moov = -1, mdat = -1;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        var header = new byte[16];
        long pos = 0;
        for (var guard = 0; pos + 8 <= fs.Length && guard < 1000; guard++)
        {
            fs.Position = pos;
            if (fs.Read(header, 0, 8) < 8)
            {
                break;
            }

            long size = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            if (size == 1)
            {
                if (fs.Read(header, 8, 8) < 8)
                {
                    break;
                }

                size = 0;
                for (var i = 8; i < 16; i++)
                {
                    size = (size << 8) | header[i];
                }
            }
            else if (size == 0)
            {
                size = fs.Length - pos;
            }

            if (type == "moov" && moov < 0)
            {
                moov = pos;
            }
            else if (type == "mdat" && mdat < 0)
            {
                mdat = pos;
            }

            if (size < 8)
            {
                break;
            }

            pos += size;
        }

        return (moov, mdat);
    }
}
