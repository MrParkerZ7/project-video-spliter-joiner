using System.Diagnostics;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-036 regression coverage: non-ASCII (unicode) paths must flow through the ffmpeg/ffprobe
/// process boundary intact — the path passed IN must reach the OS as real unicode (via
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>), and the stderr/stdout captured
/// OUT must be decoded as UTF-8 so the path text is the correct unicode instead of mojibake.
///
/// The real-world bug: a user split a <c>.ts</c> file at a Japanese path and the error printed the
/// path as mojibake (<c>ç«‹...</c> instead of <c>立...</c>) because the stderr was captured with the
/// console codepage rather than UTF-8. These tests use a directory with a Japanese component
/// (<c>立体テスト日本語</c>) to prove probe / keyframes / split all succeed at a unicode path, and
/// that a deliberately-failing ffmpeg run reports the path un-garbled.
///
/// Guarded with <see cref="FfmpegTestBinaries.SkipIfMissing"/> so a machine without the binaries
/// stays green.
/// </summary>
public class UnicodePathIntegrationTests
{
    /// <summary>A directory-name component with non-ASCII (CJK + katakana) characters.</summary>
    private const string UnicodeComponent = "立体テスト日本語";

    private readonly ITestOutputHelper _output;

    public UnicodePathIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static FfmpegBinaryLocator MakeLocator() => new(
        ffmpegOverride: FfmpegTestBinaries.Ffmpeg,
        ffprobeOverride: FfmpegTestBinaries.Ffprobe);

    private static MediaProbe MakeProbe() =>
        new(new FfprobeRunner(MakeLocator()));

    private static SplitEngine MakeEngine()
    {
        var locator = MakeLocator();
        var runner = new FfmpegRunner(locator);
        var probe = new MediaProbe(new FfprobeRunner(locator));
        return new SplitEngine(runner, probe);
    }

    private bool ShouldSkip()
    {
        if (FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg"))
        {
            return true;
        }

        return FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
    }

    /// <summary>Create a fresh temp dir with a unicode-named subfolder; caller deletes the root.</summary>
    private static (string Root, string UnicodeDir) NewUnicodeDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsj-unicode-" + Guid.NewGuid().ToString("N"));
        var unicodeDir = Path.Combine(root, UnicodeComponent);
        Directory.CreateDirectory(unicodeDir);
        return (root, unicodeDir);
    }

    /// <summary>
    /// Generate a small 5s testsrc mp4 at the given path via the real ffmpeg binary. Uses
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> so the unicode output path is
    /// passed verbatim — this itself exercises the "path in" side.
    /// </summary>
    private static void GenerateClip(string outPath)
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
            "-i", "testsrc=duration=5:size=320x240:rate=30",
            "-c:v", "libx264",
            "-g", "30",
            "-keyint_min", "30",
            "-sc_threshold", "0",
            "-pix_fmt", "yuv420p",
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
                $"ffmpeg fixture generation at unicode path failed (exit {p.ExitCode}):{Environment.NewLine}{stderr}");
        }

        File.Exists(outPath).Should().BeTrue($"ffmpeg should have written the clip at '{outPath}'");
    }

    [SkippableFact]
    public async Task Probe_AtUnicodePath_Succeeds()
    {
        if (ShouldSkip())
        {
            return;
        }

        var (root, dir) = NewUnicodeDir();
        try
        {
            var input = Path.Combine(dir, "映像.mp4");
            GenerateClip(input);

            var probe = MakeProbe();
            var result = await probe.ProbeAsync(input);

            result.IsSuccess.Should().BeTrue(
                "a real media file at a unicode path must probe cleanly, not ProbeFailed");
            var info = ((ProbeResult.ProbeSucceeded)result).Info;
            info.HasVideo.Should().BeTrue();

            var keyframes = await probe.GetKeyframesAsync(input);
            keyframes.Should().NotBeEmpty("a 5s / 1s-GOP clip has several keyframes");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [SkippableFact]
    public async Task Split_AtUnicodeInputAndOutput_ProducesPlayableSegments()
    {
        if (ShouldSkip())
        {
            return;
        }

        var (root, dir) = NewUnicodeDir();
        try
        {
            var input = Path.Combine(dir, "映像.mp4");
            GenerateClip(input);

            // Output also lands under a unicode-named subfolder to exercise the write path.
            var outDir = Path.Combine(dir, "出力");
            Directory.CreateDirectory(outDir);

            var engine = MakeEngine();
            var probe = MakeProbe();

            var result = await engine.SplitAsync(new SplitRequest(
                input, new[] { TimeSpan.FromSeconds(2) }, outDir));

            result.Segments.Should().HaveCount(2);

            foreach (var seg in result.Segments)
            {
                File.Exists(seg.Path).Should().BeTrue(
                    $"segment at unicode path '{seg.Path}' must be written (exit -28/ENOSPC bug is the mangled-path symptom)");

                var probed = await probe.ProbeAsync(seg.Path);
                probed.IsSuccess.Should().BeTrue($"segment '{seg.Path}' must probe cleanly / be playable");
                ((ProbeResult.ProbeSucceeded)probed).Info.HasVideo.Should().BeTrue();
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [SkippableFact]
    public async Task FailingRun_AtUnicodePath_CapturesPathUnGarbled()
    {
        if (ShouldSkip())
        {
            return;
        }

        var (root, dir) = NewUnicodeDir();
        try
        {
            // A non-existent INPUT file at a unicode path — ffmpeg fails and echoes the path in its
            // stderr diagnostic. This is the exact shape of the real bug: the error must show the
            // real unicode characters, not mojibake.
            var missingInput = Path.Combine(dir, "存在しない.ts");

            var runner = new FfmpegRunner(MakeLocator());
            var args = FfmpegArgs.ForFfmpeg()
                .Input(missingInput)
                .Raw("-f", "null", "-");

            var result = await runner.RunAsync(args);

            result.Success.Should().BeFalse("a missing input must make ffmpeg exit non-zero");
            result.StdErrTail.Should().NotBeEmpty();

            var stderr = result.StdErrText;
            _output.WriteLine("captured stderr tail:");
            _output.WriteLine(stderr);

            // THE KEY CHECK: the captured error text contains the REAL unicode path component,
            // proving stderr was decoded as UTF-8 (not the console codepage → mojibake).
            stderr.Should().Contain(UnicodeComponent,
                "the stderr tail must show the real unicode path, not mojibake — this is the T-036 fix");
            stderr.Should().Contain("存在しない",
                "the unicode filename stem must survive intact in the captured error");

            // And it must NOT contain the classic mojibake rendering of these bytes.
            stderr.Should().NotContain("ç«‹",
                "mojibake (cp1252 mis-decode of the UTF-8 bytes) must not appear");
            stderr.Should().NotContain("�",
                "no U+FFFD replacement characters — the bytes decoded cleanly as UTF-8");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
