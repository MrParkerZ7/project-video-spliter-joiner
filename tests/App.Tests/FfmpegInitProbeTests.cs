using System.IO;
using FluentAssertions;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Headless init probe for the FFME preview (T-019).
///
/// This does NOT render anything — it only proves that FFME can P/Invoke-load the
/// ffmpeg SHARED native libraries against the version FFME was built for
/// (FFME.Windows 7.0.361-beta.1 → FFmpeg.AutoGen 7.0.0 → ffmpeg 7.x:
/// avcodec-61 / avformat-61 / avutil-59 / ...).
///
/// If the shared build has the WRONG ffmpeg major, Library.LoadFFmpeg() throws a
/// clear FFME exception — the test then FAILS loudly (never silently broken).
///
/// If ffmpeg-shared/ is absent (e.g. CI without the fetch step), the test SKIPS with
/// a message (SkippableFact), so a lib-less checkout stays green.
/// </summary>
public class FfmpegInitProbeTests
{
    /// <summary>
    /// Locate the repo-local ffmpeg-shared/ by walking up from the test bin dir.
    /// Returns null if not found or if it holds no avcodec-*.dll.
    /// </summary>
    private static string? FindFfmpegSharedDir()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 10 && probe is not null; depth++, probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "ffmpeg-shared");
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "avcodec-*.dll").Any())
            {
                return candidate;
            }
        }
        return null;
    }

    [SkippableFact]
    public void FFME_loads_the_matched_shared_ffmpeg_and_reports_version()
    {
        var dir = FindFfmpegSharedDir();
        Skip.If(
            dir is null,
            "ffmpeg-shared/ not present (avcodec-*.dll missing). " +
            "Run packaging/fetch-ffmpeg-shared.ps1 to enable this probe.");

        // Point FFME at the shared build and force the native-lib load.
        Unosquare.FFME.Library.FFmpegDirectory = dir!;

        // LoadFFmpeg() throws a descriptive FFME exception on an ABI mismatch
        // (wrong ffmpeg major) or on missing/unloadable DLLs. A clean return means
        // the shared build MATCHES what FFME expects.
        var act = () => Unosquare.FFME.Library.LoadFFmpeg();
        act.Should().NotThrow(
            "the BtbN ffmpeg 7.1 shared build must ABI-match FFME's ffmpeg 7.x bindings");

        // Report the loaded ffmpeg version so a mismatch/regression is visible in output.
        var version = Unosquare.FFME.Library.FFmpegVersionInfo;
        version.Should().NotBeNullOrWhiteSpace(
            "FFME must report the loaded ffmpeg version once the native libs are loaded");
        Console.WriteLine($"[FFME init probe] loaded ffmpeg version = {version}");
    }
}
