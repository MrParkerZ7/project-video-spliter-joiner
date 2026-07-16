using System.Diagnostics;
using System.IO;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Generates deterministic synthetic mp4 fixtures with KNOWN keyframe positions using the real
/// ffmpeg override, lazily and once per test run. Guarded by <see cref="FfmpegAvailable"/> so
/// tests skip cleanly when ffmpeg is absent. Files land in a temp dir and are cleaned up on
/// process exit. NOT copied into the repo.
/// </summary>
public sealed class MediaFixtures : IDisposable
{
    /// <summary>Fixed GOP length in seconds (see the ffmpeg -g/-keyint_min flags below).</summary>
    public const double GopSeconds = 1.0;

    /// <summary>Fixture duration in seconds.</summary>
    public const double DurationSeconds = 10.0;

    /// <summary>Length of the 4K / matched-1080p perf fixtures in seconds (T-024).</summary>
    public const double PerfDurationSeconds = 15.0;

    private readonly string _dir;
    private readonly Lazy<string> _videoOnly;
    private readonly Lazy<string> _videoWithAudio;
    private readonly Lazy<string> _uhd4k;
    private readonly Lazy<string> _fullHd1080;

    public MediaFixtures()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _videoOnly = new Lazy<string>(() => GenerateVideoOnly(Path.Combine(_dir, "video_only.mp4")));
        _videoWithAudio = new Lazy<string>(() => GenerateVideoWithAudio(Path.Combine(_dir, "video_audio.mp4")));
        _uhd4k = new Lazy<string>(() => GeneratePerfClip(Path.Combine(_dir, "uhd_4k.mp4"), 3840, 2160));
        _fullHd1080 = new Lazy<string>(() => GeneratePerfClip(Path.Combine(_dir, "fhd_1080.mp4"), 1920, 1080));
    }

    /// <summary>True when the real ffmpeg override exists — gate for fixture generation.</summary>
    public static bool FfmpegAvailable => FfmpegTestBinaries.FfmpegExists;

    /// <summary>Path to a 10s H.264 320x240 mp4 at 30fps with a fixed 1s GOP (video only).</summary>
    public string VideoOnlyPath => _videoOnly.Value;

    /// <summary>Path to the same clip plus a 440Hz AAC audio track (video + audio).</summary>
    public string VideoWithAudioPath => _videoWithAudio.Value;

    /// <summary>Path to a 15s 3840×2160 H.264 clip at 30fps with a 2s GOP (T-024 perf fixture).</summary>
    public string Uhd4kPath => _uhd4k.Value;

    /// <summary>Path to a length-matched 1920×1080 clip — the 1080p baseline for split timing.</summary>
    public string FullHd1080Path => _fullHd1080.Value;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; a locked temp file is not a test failure.
        }
    }

    private static string GenerateVideoOnly(string outPath)
    {
        // testsrc 10s @ 30fps, libx264 with GOP fixed to 30 frames (= 1.0s) so keyframes land at
        // ~0,1,2,…,10. -sc_threshold 0 disables scene-cut keyframes so spacing stays exactly GOP.
        RunFfmpeg(
            "-y",
            "-f", "lavfi",
            "-i", "testsrc=duration=10:size=320x240:rate=30",
            "-c:v", "libx264",
            "-g", "30",
            "-keyint_min", "30",
            "-sc_threshold", "0",
            "-pix_fmt", "yuv420p",
            outPath);
        return outPath;
    }

    private static string GenerateVideoWithAudio(string outPath)
    {
        RunFfmpeg(
            "-y",
            "-f", "lavfi",
            "-i", "testsrc=duration=10:size=320x240:rate=30",
            "-f", "lavfi",
            "-i", "sine=frequency=440:duration=10",
            "-c:v", "libx264",
            "-g", "30",
            "-keyint_min", "30",
            "-sc_threshold", "0",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            outPath);
        return outPath;
    }

    /// <summary>
    /// Generate a 15s testsrc2 clip at the given resolution, 30fps, GOP 60 (2s), veryfast x264 —
    /// the T-024 perf fixtures. Used to confirm 4K split/scan correctness and to record 4K-vs-1080p
    /// timings; the two resolutions share length + GOP so their split times are comparable.
    /// </summary>
    private static string GeneratePerfClip(string outPath, int width, int height)
    {
        RunFfmpeg(
            "-y",
            "-f", "lavfi",
            "-i", $"testsrc2=size={width}x{height}:rate=30:d={(int)PerfDurationSeconds}",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-g", "60",
            "-pix_fmt", "yuv420p",
            outPath);
        return outPath;
    }

    private static void RunFfmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegTestBinaries.Ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        p.Start();
        var stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg fixture generation failed (exit {p.ExitCode}):{Environment.NewLine}{stderr}");
        }

        if (!File.Exists(args[^1]))
        {
            throw new InvalidOperationException($"ffmpeg reported success but '{args[^1]}' was not written.");
        }
    }
}

/// <summary>
/// xUnit collection so the (expensive) fixture generation happens once and is shared across the
/// integration test classes.
/// </summary>
[CollectionDefinition(MediaFixturesCollection.Name)]
public sealed class MediaFixturesCollection : ICollectionFixture<MediaFixtures>
{
    public const string Name = "media-fixtures";
}
