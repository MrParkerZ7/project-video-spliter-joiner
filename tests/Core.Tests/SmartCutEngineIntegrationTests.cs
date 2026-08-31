using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Frame-exact cutting against REAL media, through the real ffmpeg (G-042 / T-125).
///
/// <para><b>Why this exists.</b> Every other smart-cut test drives fakes: a scripted
/// <c>IFfmpegRunner</c> and test-supplied probe results. Those prove the planner's arithmetic and the
/// orchestration, and prove nothing about whether the produced FILE is what was asked for. "Exact cut has
/// never run against real media" was carried as an open caveat in ADR-0018, the release notes and several
/// board renders — while this repo already had the pattern for real-ffmpeg tests
/// (<see cref="SplitEngineIntegrationTests"/>, <see cref="JoinEngineIntegrationTests"/>). The gap was
/// that nobody applied it here.</para>
///
/// <para>The fixture is built deliberately COARSE — a 4s GOP — because that is the shape of the original
/// report: on a 4s keyframe grid, asking for a cut at 5s lands the lossless path at 4s, leaving a second
/// of intro behind. A 1s-GOP fixture would make the difference too small to be convincing.</para>
///
/// <para>Skips when the ffmpeg binaries are absent, like its sibling integration suites.</para>
/// </summary>
public sealed class SmartCutEngineIntegrationTests : IDisposable
{
    private const double SourceSeconds = 30.0;
    private const double GopSeconds = 4.0;

    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SmartCutEngineIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-smartcut-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private bool ShouldSkip() =>
        FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg")
        || FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");

    private static FfmpegBinaryLocator Locator() => new(
        ffmpegOverride: FfmpegTestBinaries.Ffmpeg,
        ffprobeOverride: FfmpegTestBinaries.Ffprobe);

    private static SmartCutEngine MakeEngine()
    {
        var locator = Locator();
        return new SmartCutEngine(new FfmpegRunner(locator), new MediaProbe(new FfprobeRunner(locator)));
    }

    private static MediaProbe MakeProbe() => new(new FfprobeRunner(Locator()));

    /// <summary>Run a binary and return stdout — used only to build/inspect the fixture, never under test.</summary>
    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return stdout;
    }

    /// <summary>A real 30s H.264+AAC clip whose keyframes land exactly every 4s (0, 4, 8 … 28).</summary>
    private string MakeCoarseGopSource()
    {
        var path = Path.Combine(_dir, "src.mp4");
        Run(FfmpegTestBinaries.Ffmpeg,
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=25:duration={SourceSeconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={SourceSeconds}",
            "-c:v", "libx264", "-g", "100", "-keyint_min", "100", "-sc_threshold", "0",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path);

        File.Exists(path).Should().BeTrue("the fixture must actually be produced before anything is asserted");
        return path;
    }

    private static double DurationOf(string path)
    {
        var raw = Run(FfmpegTestBinaries.Ffprobe,
            "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path).Trim();
        return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- The fixture is what we think it is -------------------------------------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task TheFixtureReallyHasACoarseKeyframeGrid()
    {
        if (ShouldSkip())
        {
            return;
        }

        var src = MakeCoarseGopSource();

        DurationOf(src).Should().BeApproximately(SourceSeconds, 0.35);

        var keyframes = await MakeProbe().GetKeyframesAsync(src);
        keyframes.Should().NotBeEmpty();

        // The whole point of the report: 5s is nearer 4s than 8s, so a lossless cut cannot honour it.
        var snapped = MakeProbe().SnapToNearestKeyframe(keyframes, TimeSpan.FromSeconds(5));
        snapped.Snapped.Should().Be(
            TimeSpan.FromSeconds(GopSeconds),
            "a 4s grid is what makes a 5s request unreachable losslessly — if this fixture drifted, the " +
            "tests below would prove nothing");
    }

    // ---- The claim the whole feature rests on -----------------------------------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ExactCut_At5s_ProducesAFileThatReallyStartsAt5s_NotAtTheKeyframe()
    {
        if (ShouldSkip())
        {
            return;
        }

        var src = MakeCoarseGopSource();
        var dest = Path.Combine(_dir, "exact.mp4");

        var result = await MakeEngine().CutAsync(src, TimeSpan.FromSeconds(5), null, dest);

        result.FellBack.Should().BeFalse(
            $"the fixture is plain H.264/AAC, so exact cutting must be available (reason: {result.FallbackReason})");
        File.Exists(dest).Should().BeTrue();

        // THE assertion. Lossless would snap to 4s and leave 26s; exact must honour the request and leave 25s.
        DurationOf(dest).Should().BeApproximately(
            SourceSeconds - 5.0,
            0.35,
            "a 5s cut on a 4s grid must remove FIVE seconds — 26s would mean it silently snapped back to " +
            "the keyframe, which is the entire defect Exact cut exists to fix");

        var probed = await MakeProbe().ProbeAsync(dest);
        probed.IsSuccess.Should().BeTrue("the produced file must be readable, not merely present");
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task TheLosslessPathReallyDoesSnap_SoTheDifferenceIsDemonstrated_NotAssumed()
    {
        if (ShouldSkip())
        {
            return;
        }

        var src = MakeCoarseGopSource();
        var dest = Path.Combine(_dir, "lossless.mp4");

        // Stream-copy from the SNAPPED time, which is what the lossless route does.
        Run(FfmpegTestBinaries.Ffmpeg,
            "-y", "-hide_banner", "-loglevel", "error",
            "-ss", GopSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", src, "-c", "copy", dest);

        DurationOf(dest).Should().BeApproximately(
            SourceSeconds - GopSeconds,
            0.35,
            "26s, not 25s — this is the behaviour the user reported, reproduced against real media so the " +
            "exact-cut assertion above is measured against something real rather than an assumption");
    }

    // ---- The tail is still copied, not re-encoded --------------------------------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ExactCut_ReEncodesOnlyTheHead_TheRestStaysCopied()
    {
        if (ShouldSkip())
        {
            return;
        }

        var src = MakeCoarseGopSource();
        var dest = Path.Combine(_dir, "exact2.mp4");

        var result = await MakeEngine().CutAsync(src, TimeSpan.FromSeconds(5), null, dest);
        result.FellBack.Should().BeFalse();

        result.ReencodedDuration.Should().BeLessThan(
            TimeSpan.FromSeconds(GopSeconds + 1),
            "only the fragment from the cut to the next keyframe is re-encoded — that bound is the whole " +
            "reason this is acceptable on the lossless path's tab");

        // A near-whole-file re-encode would change the size dramatically; a head-only one should not.
        var srcLen = new FileInfo(src).Length;
        var outLen = new FileInfo(dest).Length;
        outLen.Should().BeLessThan(srcLen, "five seconds were removed");
        outLen.Should().BeGreaterThan(
            (long)(srcLen * 0.5),
            "the remaining 25 of 30 seconds are stream-copied, so the output keeps most of the source's bytes");
    }

    // ---- An end cut, since the tail path differs -----------------------------------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ExactCut_WithBothEnds_KeepsExactlyTheRequestedMiddle()
    {
        if (ShouldSkip())
        {
            return;
        }

        var src = MakeCoarseGopSource();
        var dest = Path.Combine(_dir, "middle.mp4");

        var result = await MakeEngine().CutAsync(
            src, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(22), dest);

        result.FellBack.Should().BeFalse($"reason: {result.FallbackReason}");
        DurationOf(dest).Should().BeApproximately(
            17.0, 0.5, "22s minus 5s — neither end may drift to a keyframe");
    }
}
