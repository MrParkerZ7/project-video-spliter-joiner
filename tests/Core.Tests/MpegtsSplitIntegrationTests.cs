using System.Diagnostics;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-035 regression coverage: splitting a broadcast-style <c>mpegts</c> / <c>.ts</c> file (the
/// exact container from the field report) must produce PLAYABLE segments — valid duration and
/// preserved video+audio streams — not just a zero exit code.
///
/// Background: the user reported <c>ffmpeg split failed (exit -28)</c> on a Japanese-named
/// <c>.ts</c> file, with a benign <c>[mpegts …] start time for stream 2 is not set …</c> warning
/// in the tail. Exit -28 == <c>AVERROR(ENOSPC)</c> — the failure was a mangled output path
/// (fixed by T-036's UTF-8/unicode-path handling), and the mpegts line was only a warning.
/// This test proves the segment-muxer copy split of a real mpegts stream is clean, and a sibling
/// test proves it stays clean at a unicode path (mirroring the report).
///
/// Guarded with <see cref="FfmpegTestBinaries.SkipIfMissing"/> so a binary-less machine stays green.
/// </summary>
public class MpegtsSplitIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public MpegtsSplitIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static FfmpegBinaryLocator MakeLocator() => new(
        ffmpegOverride: FfmpegTestBinaries.Ffmpeg,
        ffprobeOverride: FfmpegTestBinaries.Ffprobe);

    private static MediaProbe MakeProbe() => new(new FfprobeRunner(MakeLocator()));

    private static SplitEngine MakeEngine()
    {
        var locator = MakeLocator();
        return new SplitEngine(new FfmpegRunner(locator), new MediaProbe(new FfprobeRunner(locator)));
    }

    private bool ShouldSkip()
    {
        if (FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg"))
        {
            return true;
        }

        return FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
    }

    /// <summary>
    /// Generate a 12s broadcast-style mpegts clip (video + audio, GOP 60) at the given <c>.ts</c>
    /// path via the real ffmpeg override — the fixture from the ticket. Passed through
    /// <see cref="ProcessStartInfo.ArgumentList"/> so a unicode output path survives verbatim.
    /// </summary>
    private static void GenerateTs(string outPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegTestBinaries.Ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in new[]
        {
            "-y",
            "-f", "lavfi",
            "-i", "testsrc2=size=1280x720:rate=30:d=12",
            "-f", "lavfi",
            "-i", "sine=frequency=440:duration=12",
            "-c:v", "libx264",
            "-g", "60",
            "-c:a", "aac",
            "-f", "mpegts",
            outPath,
        })
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
                $"mpegts fixture generation failed (exit {p.ExitCode}):{Environment.NewLine}{stderr}");
        }

        File.Exists(outPath).Should().BeTrue($"ffmpeg should have written the .ts clip at '{outPath}'");
    }

    private static string NewDir(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), $"vsj-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [SkippableFact]
    public async Task Split_MpegtsInput_ProducesPlayableSegments_WithVideoAndAudio()
    {
        if (ShouldSkip())
        {
            return;
        }

        var work = NewDir("mpegts");
        try
        {
            var input = Path.Combine(work, "broadcast.ts");
            GenerateTs(input);

            var probe = MakeProbe();

            // Confirm the fixture really is an mpegts container — the detect path the fix keys on.
            var inputProbe = await probe.ProbeAsync(input);
            inputProbe.IsSuccess.Should().BeTrue();
            var inputInfo = ((ProbeResult.ProbeSucceeded)inputProbe).Info;
            inputInfo.Container.Should().Contain("mpegts");
            inputInfo.HasVideo.Should().BeTrue();
            inputInfo.HasAudio.Should().BeTrue();

            var outDir = Path.Combine(work, "out");
            var engine = MakeEngine();

            // Cut at 4s and 8s → three segments.
            var result = await engine.SplitAsync(new SplitRequest(
                input,
                new[] { TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) },
                outDir));

            result.Segments.Should().HaveCount(3);

            TimeSpan total = TimeSpan.Zero;
            foreach (var seg in result.Segments)
            {
                File.Exists(seg.Path).Should().BeTrue($"mpegts segment '{seg.Path}' must be written");

                var probed = await probe.ProbeAsync(seg.Path);
                probed.IsSuccess.Should().BeTrue($"segment '{seg.Path}' must probe cleanly / be playable");
                var info = ((ProbeResult.ProbeSucceeded)probed).Info;

                info.HasVideo.Should().BeTrue($"segment '{seg.Path}' must keep its video stream (-map 0)");
                info.HasAudio.Should().BeTrue($"segment '{seg.Path}' must keep its audio stream (-map 0)");
                info.Duration.Should().BeGreaterThan(TimeSpan.Zero, "a playable segment has a real duration");
                total += info.Duration;

                _output.WriteLine(
                    $"{Path.GetFileName(seg.Path)}: dur={info.Duration.TotalSeconds:F3}s container={info.Container}");
            }

            // Segment durations must sum to roughly the 12s source (keyframe snapping ⇒ small slack).
            total.Should().BeCloseTo(TimeSpan.FromSeconds(12), TimeSpan.FromMilliseconds(700));
        }
        finally
        {
            TryDelete(work);
        }
    }

    [SkippableFact]
    public async Task Split_MpegtsAtUnicodePath_ProducesPlayableSegments()
    {
        if (ShouldSkip())
        {
            return;
        }

        // Mirror the field report: a .ts file under a Japanese-named folder, output alongside it.
        var work = NewDir("mpegts-uni");
        try
        {
            var dir = Path.Combine(work, "立体テスト日本語");
            Directory.CreateDirectory(dir);
            var input = Path.Combine(dir, "映像.ts");
            GenerateTs(input);

            var outDir = Path.Combine(dir, "出力");
            var engine = MakeEngine();
            var probe = MakeProbe();

            var result = await engine.SplitAsync(new SplitRequest(
                input, new[] { TimeSpan.FromSeconds(6) }, outDir));

            result.Segments.Should().HaveCount(2);

            foreach (var seg in result.Segments)
            {
                File.Exists(seg.Path).Should().BeTrue(
                    $"segment at unicode .ts path '{seg.Path}' must be written (the exit -28 report was a mangled path)");
                var probed = await probe.ProbeAsync(seg.Path);
                probed.IsSuccess.Should().BeTrue($"segment '{seg.Path}' must be playable");
                var info = ((ProbeResult.ProbeSucceeded)probed).Info;
                info.HasVideo.Should().BeTrue();
                info.HasAudio.Should().BeTrue();
            }
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
