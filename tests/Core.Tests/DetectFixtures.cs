using System.Diagnostics;
using System.IO;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Generates a synthetic mp4 with KNOWN black / white / scene events for the T-006 detector,
/// once per test run, using the real ffmpeg override. Guarded by <see cref="FfmpegAvailable"/>
/// so tests skip cleanly when ffmpeg is absent. File lands in a temp dir, cleaned on dispose.
///
/// Timeline (each segment concatenated, 30fps, libx264, yuv420p, GOP=30 → keyframes ~every 1s):
///   testsrc   [0.0 .. 2.0)   busy
///   black     [2.0 .. 3.5)   → a BLACK interval starting at ~2.0
///   testsrc2  [3.5 .. 5.5)   busy
///   white     [5.5 .. 7.0)   → a WHITE interval starting at ~5.5
///   smptebars [7.0 .. 9.0)   busy
/// Hard SCENE cuts land at each 2.0 / 3.5 / 5.5 / 7.0 segment boundary.
/// </summary>
public sealed class DetectFixtures : IDisposable
{
    /// <summary>Total fixture duration in seconds.</summary>
    public const double DurationSeconds = 9.0;

    /// <summary>Expected black interval start (seconds).</summary>
    public const double BlackStartSeconds = 2.0;

    /// <summary>Expected white interval start (seconds).</summary>
    public const double WhiteStartSeconds = 5.5;

    private readonly string _dir;
    private readonly Lazy<string> _events;
    private readonly Lazy<string> _busyOnly;

    public DetectFixtures()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _events = new Lazy<string>(() => GenerateEvents(Path.Combine(_dir, "events.mp4")));
        _busyOnly = new Lazy<string>(() => GenerateBusyOnly(Path.Combine(_dir, "busy.mp4")));
    }

    /// <summary>True when the real ffmpeg override exists — gate for fixture generation.</summary>
    public static bool FfmpegAvailable => FfmpegTestBinaries.FfmpegExists;

    /// <summary>Path to the black/white/scene event fixture described in the class summary.</summary>
    public string EventsPath => _events.Value;

    /// <summary>Path to a 5s plain <c>testsrc</c> clip — no black/white, only (maybe) scene noise.</summary>
    public string BusyOnlyPath => _busyOnly.Value;

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

    private static string GenerateEvents(string outPath)
    {
        // Five lavfi inputs concatenated via filter_complex concat into one clean mp4.
        RunFfmpeg(
            "-y",
            "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=30",
            "-f", "lavfi", "-i", "color=black:duration=1.5:size=320x240:rate=30",
            "-f", "lavfi", "-i", "testsrc2=duration=2:size=320x240:rate=30",
            "-f", "lavfi", "-i", "color=white:duration=1.5:size=320x240:rate=30",
            "-f", "lavfi", "-i", "smptebars=duration=2:size=320x240:rate=30",
            "-filter_complex", "[0:v][1:v][2:v][3:v][4:v]concat=n=5:v=1:a=0[v]",
            "-map", "[v]",
            "-c:v", "libx264",
            "-g", "30",
            "-keyint_min", "30",
            "-pix_fmt", "yuv420p",
            outPath);
        return outPath;
    }

    private static string GenerateBusyOnly(string outPath)
    {
        // Plain testsrc: no black or white frames; may still produce minor scene noise.
        RunFfmpeg(
            "-y",
            "-f", "lavfi", "-i", "testsrc=duration=5:size=320x240:rate=30",
            "-c:v", "libx264",
            "-g", "30",
            "-keyint_min", "30",
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
                $"ffmpeg detect-fixture generation failed (exit {p.ExitCode}):{Environment.NewLine}{stderr}");
        }

        if (!File.Exists(args[^1]))
        {
            throw new InvalidOperationException($"ffmpeg reported success but '{args[^1]}' was not written.");
        }
    }
}

/// <summary>xUnit collection so the detect fixtures are generated once and shared across classes.</summary>
[CollectionDefinition(DetectFixturesCollection.Name)]
public sealed class DetectFixturesCollection : ICollectionFixture<DetectFixtures>
{
    public const string Name = "detect-fixtures";
}
