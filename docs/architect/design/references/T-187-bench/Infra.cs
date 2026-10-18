using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;

namespace T187Bench;

/// <summary>Fixed paths. Everything the harness writes lives under the scratchpad (never the repo, never %APPDATA%/%LOCALAPPDATA%).</summary>
internal static class P
{
    public const string Repo = "D:/Programing/Projects/project-video-spliter-joiner";
    public static readonly string Ff = Repo + "/ffmpeg-shared/ffmpeg.exe";
    public static readonly string Fp = Repo + "/ffmpeg-shared/ffprobe.exe";
    public static readonly string Scratch = @"C:\Users\priva\AppData\Local\Temp\claude\D--Programing-claude-prompt-root-master\2c9ff7ab-8298-4632-b799-b6600a56a1a9\scratchpad";
    public static readonly string Fx = Path.Combine(Scratch, "t187-fixtures");
    public static readonly string Bench = Path.Combine(Scratch, "t187-bench");
    public static readonly string Work = Path.Combine(Fx, "work");
    public static readonly string Results = Path.Combine(Bench, "results");
}

/// <summary>One synthetic fixture plus the times the scenarios use on it.</summary>
internal sealed record Fixture(
    string Key, string File, double Duration, double Gop,
    double[] Cuts, double Intro, double Outro, double ThumbOnKf, double ThumbMidGop)
{
    public string Path => System.IO.Path.Combine(P.Fx, File);

    public static readonly Fixture[] All =
    {
        new("4k",   "4k_h264_g2s_120s.mp4",   120, 2,  new[] { 30.3, 70.7 }, 10.5, 100.3, 60, 61),
        new("1080", "1080_h264_g2s_120s.mp4", 120, 2,  new[] { 30.3, 70.7 }, 10.5, 100.3, 60, 61),
        new("hevc", "4k_hevc_g2s_60s.mp4",    60,  2,  new[] { 20.3, 40.7 }, 10.5, 50.3,  30, 31),
        new("g10",  "4k_h264_g10s_60s.mp4",   60,  10, new[] { 20.3, 40.7 }, 12.5, 47.3,  30, 39),
        // Scaling check: the 4K fixture concatenated 5x (-c copy) = a 10-minute, ~4.5 GB file, closer to real footage.
        new("4k10m", "4k_h264_g2s_600s.mp4",  600, 2,  new[] { 150.3, 400.7 }, 10.5, 580.3, 300, 301),
    };

    public static Fixture Get(string key) => All.First(f => f.Key == key);
}

/// <summary>The real Core services, composed exactly like MainViewModel's composition root, minus the per-user folders.</summary>
internal static class Svc
{
    public static readonly FfmpegBinaryLocator Loc = new(P.Ff, P.Fp);
    public static readonly FfmpegRunner Ffmpeg = new(Loc);
    public static readonly FfprobeRunner Ffprobe = new(Loc);
    public static readonly ErrorLogWriter Log = new(System.IO.Path.Combine(P.Bench, "errlogs"));

    public static MediaProbe NewProbe() => new(Ffprobe);

    public static string NewTempDir(string kind)
    {
        var d = System.IO.Path.Combine(P.Work, "cache", kind + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Run ffmpeg through the app's own runner; returns (seconds, result).</summary>
    public static async Task<(double S, FfmpegResult R)> RunFfmpeg(params string[] args)
    {
        var a = FfmpegArgs.ForFfmpeg().Raw(args);
        var sw = Stopwatch.StartNew();
        var r = await Ffmpeg.RunAsync(a).ConfigureAwait(false);
        return (sw.Elapsed.TotalSeconds, r);
    }

    /// <summary>Run ffprobe through the app's own runner; returns (seconds, stdout).</summary>
    public static async Task<(double S, string Out)> RunFfprobe(params string[] args)
    {
        var a = FfmpegArgs.ForFfprobe().Raw(args);
        var sw = Stopwatch.StartNew();
        var r = await Ffprobe.RunJsonAsync(a).ConfigureAwait(false);
        return (sw.Elapsed.TotalSeconds, r);
    }

    /// <summary>PSNR (average, dB) between two images/videos via ffmpeg's psnr filter. inf → 99.</summary>
    public static async Task<double> Psnr(string a, string b, string? scaleTo = null)
    {
        var filter = scaleTo is null
            ? "[0:v][1:v]psnr"
            : $"[0:v]scale={scaleTo}:flags=bicubic[a];[1:v]scale={scaleTo}:flags=bicubic[b];[a][b]psnr";
        var (_, r) = await RunFfmpeg("-i", a, "-i", b, "-lavfi", filter, "-f", "null", "-");
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
}

/// <summary>Append-only timing ledger (results/timings.jsonl) + console echo.</summary>
internal static class R
{
    private static readonly object Gate = new();
    public static string Session = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    public static void Add(string tab, string op, string fx, string mode, int run, double s, string? note = null)
    {
        Directory.CreateDirectory(P.Results);
        var line = JsonSerializer.Serialize(new { session = Session, tab, op, fx, mode, run, s = Math.Round(s, 4), note });
        lock (Gate)
        {
            File.AppendAllText(System.IO.Path.Combine(P.Results, "timings.jsonl"), line + "\n");
            Console.WriteLine($"{tab,-6} {op,-52} {fx,-5} {mode,-5} #{run} {s,8:0.000}s {note}");
        }
    }

    public static void Note(string text)
    {
        Directory.CreateDirectory(P.Results);
        lock (Gate)
        {
            File.AppendAllText(System.IO.Path.Combine(P.Results, "notes.txt"), $"[{Session}] {text}\n");
            Console.WriteLine("NOTE " + text);
        }
    }
}

internal static class Io
{
    /// <summary>
    /// Copy <paramref name="src"/> with FILE_FLAG_NO_BUFFERING|WRITE_THROUGH so the new file's data never enters the
    /// Windows file cache — the first read of the copy is a genuinely cold (from-SSD) read. This is how the harness gets
    /// "cold" numbers without admin rights to flush the standby list.
    /// </summary>
    public static unsafe string ColdCopy(string src, string tag)
    {
        var dir = System.IO.Path.Combine(P.Work, "cold");
        Directory.CreateDirectory(dir);
        var dst = System.IO.Path.Combine(dir, $"{tag}-{Guid.NewGuid().ToString("N")[..6]}{System.IO.Path.GetExtension(src)}");
        const int Buf = 8 << 20;
        var mem = (byte*)NativeMemory.AlignedAlloc(Buf, 4096);
        long len;
        try
        {
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var h = File.OpenHandle(dst, FileMode.Create, FileAccess.Write, FileShare.None, (FileOptions)0x20000000 | FileOptions.WriteThrough))
            {
                len = input.Length;
                long off = 0;
                var span = new Span<byte>(mem, Buf);
                while (off < len)
                {
                    var want = (int)Math.Min(Buf, len - off);
                    var got = 0;
                    while (got < want)
                    {
                        var n = input.Read(span.Slice(got, want - got));
                        if (n <= 0)
                        {
                            break;
                        }

                        got += n;
                    }

                    var padded = (got + 4095) & ~4095;
                    if (padded > got)
                    {
                        span.Slice(got, padded - got).Clear();
                    }

                    RandomAccess.Write(h, new ReadOnlySpan<byte>(mem, padded), off);
                    off += got;
                }
            }

            using (var fs = new FileStream(dst, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(len);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
        }

        // Give the copy a fresh, distinct mtime so MediaProbe's (path,mtime,len) cache never confuses it.
        File.SetLastWriteTimeUtc(dst, DateTime.UtcNow);
        return dst;
    }

    public static double ReadAllSeconds(string path)
    {
        var sw = Stopwatch.StartNew();
        var buf = new byte[1 << 20];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        while (fs.Read(buf, 0, buf.Length) > 0)
        {
        }

        return sw.Elapsed.TotalSeconds;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }

    public static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
}

internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _h;

    public SyncProgress(Action<T> h) => _h = h;

    public void Report(T value) => _h(value);
}
