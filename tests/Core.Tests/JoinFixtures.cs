using System.Diagnostics;
using System.IO;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Generates deterministic synthetic mp4 fixtures for the JOIN tests using the real ffmpeg
/// override: three concat-COMPATIBLE clips (same codec/res/pix_fmt/fps, durations 3s/2s/2s)
/// and one INCOMPATIBLE clip (different resolution 640x480). Also a compatible clip carrying
/// an audio track. Guarded by <see cref="FfmpegAvailable"/> so tests skip cleanly when ffmpeg
/// is absent. Files land in a temp dir and are cleaned up on disposal. NOT copied into the repo.
/// </summary>
public sealed class JoinFixtures : IDisposable
{
    private readonly string _dir;
    private readonly Lazy<string> _clipA;
    private readonly Lazy<string> _clipB;
    private readonly Lazy<string> _clipC;
    private readonly Lazy<string> _clipDifferentRes;
    private readonly Lazy<string> _clipAWithAudio;
    private readonly Lazy<string> _clipBWithAudio;

    public JoinFixtures()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-join-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // Three compatible clips: identical codec/res/pix_fmt/fps, differing only in duration.
        _clipA = new Lazy<string>(() => GenerateVideo(Path.Combine(_dir, "clipA.mp4"), 3, "320x240"));
        _clipB = new Lazy<string>(() => GenerateVideo(Path.Combine(_dir, "clipB.mp4"), 2, "320x240"));
        _clipC = new Lazy<string>(() => GenerateVideo(Path.Combine(_dir, "clipC.mp4"), 2, "320x240"));

        // Incompatible clip: different resolution, same codec/pix_fmt otherwise.
        _clipDifferentRes = new Lazy<string>(() => GenerateVideo(Path.Combine(_dir, "clipDiffRes.mp4"), 2, "640x480"));

        // Two compatible clips WITH audio (same audio params) for the audio-preservation test.
        _clipAWithAudio = new Lazy<string>(() => GenerateVideoWithAudio(Path.Combine(_dir, "clipA_audio.mp4"), 3, "320x240"));
        _clipBWithAudio = new Lazy<string>(() => GenerateVideoWithAudio(Path.Combine(_dir, "clipB_audio.mp4"), 2, "320x240"));
    }

    /// <summary>True when the real ffmpeg override exists — gate for fixture generation.</summary>
    public static bool FfmpegAvailable => FfmpegTestBinaries.FfmpegExists;

    /// <summary>3s H.264 320x240 30fps yuv420p (video only).</summary>
    public string ClipAPath => _clipA.Value;

    /// <summary>2s H.264 320x240 30fps yuv420p (video only).</summary>
    public string ClipBPath => _clipB.Value;

    /// <summary>2s H.264 320x240 30fps yuv420p (video only).</summary>
    public string ClipCPath => _clipC.Value;

    /// <summary>2s H.264 640x480 30fps yuv420p — resolution mismatch vs A/B/C.</summary>
    public string ClipDifferentResPath => _clipDifferentRes.Value;

    /// <summary>3s H.264 320x240 + 44100Hz AAC (video + audio).</summary>
    public string ClipAWithAudioPath => _clipAWithAudio.Value;

    /// <summary>2s H.264 320x240 + 44100Hz AAC (video + audio).</summary>
    public string ClipBWithAudioPath => _clipBWithAudio.Value;

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

    private static string GenerateVideo(string outPath, int durationSeconds, string size)
    {
        // testsrc @ 30fps, libx264, GOP fixed to 30 frames (= 1.0s), sc_threshold 0 for stable keyframes.
        RunFfmpeg(
            "-y",
            "-f", "lavfi",
            "-i", $"testsrc=duration={durationSeconds}:size={size}:rate=30",
            "-c:v", "libx264",
            "-g", "30",
            "-keyint_min", "30",
            "-sc_threshold", "0",
            "-pix_fmt", "yuv420p",
            outPath);
        return outPath;
    }

    private static string GenerateVideoWithAudio(string outPath, int durationSeconds, string size)
    {
        RunFfmpeg(
            "-y",
            "-f", "lavfi",
            "-i", $"testsrc=duration={durationSeconds}:size={size}:rate=30",
            "-f", "lavfi",
            "-i", $"sine=frequency=440:duration={durationSeconds}:sample_rate=44100",
            "-c:v", "libx264",
            "-g", "30",
            "-keyint_min", "30",
            "-sc_threshold", "0",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-ar", "44100",
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
                $"ffmpeg join-fixture generation failed (exit {p.ExitCode}):{Environment.NewLine}{stderr}");
        }

        if (!File.Exists(args[^1]))
        {
            throw new InvalidOperationException($"ffmpeg reported success but '{args[^1]}' was not written.");
        }
    }
}

/// <summary>
/// xUnit collection so the (expensive) join-fixture generation happens once and is shared
/// across the join integration test classes.
/// </summary>
[CollectionDefinition(JoinFixturesCollection.Name)]
public sealed class JoinFixturesCollection : Xunit.ICollectionFixture<JoinFixtures>
{
    public const string Name = "join-fixtures";
}
