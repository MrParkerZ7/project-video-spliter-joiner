using System;
using System.IO;
using FluentAssertions;
using Xunit;
using Xunit.Sdk;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-149 — the guard that decides whether the release gate can fail is itself tested.
///
/// <para>The old guard returned <c>true</c> when ffmpeg was missing and every caller early-returned, so
/// xUnit recorded the method as <b>PASSED</b>. On CI — where the hardcoded <c>D:\_env_storeage\…</c>
/// path does not exist — 43 integration tests across split, join, smart-cut and the probe suites
/// reported green <b>without executing anything</b>, and the release gate was incapable of failing.</para>
///
/// <para>These pin the two halves that have to hold: the <b>search</b> actually finds a binary somewhere
/// other than one developer's machine, and the <b>outcome</b> when it does not is skip-or-fail, never a
/// silent pass. Both are asserted by behaviour — "the gate works" is exactly the claim you cannot verify
/// by watching a green run.</para>
/// </summary>
public sealed class FfmpegTestBinariesTests : IDisposable
{
    private readonly string? _priorRequire =
        Environment.GetEnvironmentVariable(FfmpegTestBinaries.RequireEnvVar);

    private readonly string? _priorDir =
        Environment.GetEnvironmentVariable(FfmpegTestBinaries.DirEnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FfmpegTestBinaries.RequireEnvVar, _priorRequire);
        Environment.SetEnvironmentVariable(FfmpegTestBinaries.DirEnvVar, _priorDir);
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-t149-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void TryDelete(string d)
    {
        try { Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
    }

    // ---- The outcome when a binary is missing --------------------------------------------------------

    [Fact]
    public void APresentBinaryJustRuns()
    {
        var act = () => FfmpegTestBinaries.Require(null, exists: true, "ffmpeg");

        act.Should().NotThrow("a found binary means the test body should execute");
    }

    [Fact]
    public void AMissingBinarySkipsVisibly_RatherThanPassingSilently()
    {
        Environment.SetEnvironmentVariable(FfmpegTestBinaries.RequireEnvVar, null);

        var act = () => FfmpegTestBinaries.Require(null, exists: false, "ffmpeg");

        // Throwing a skip is how xUnit is told "Skipped". Returning normally is how the old guard
        // produced a silent pass — that distinction IS the bug this ticket exists for.
        act.Should().Throw<Exception>("a missing binary must never look like a passing test")
            .Which.GetType().Name.Should().Contain("Skip");
    }

    [Fact]
    public void WhenTheEnvironmentDeclaresFfmpegRequired_AMissingBinaryFAILS()
    {
        Environment.SetEnvironmentVariable(FfmpegTestBinaries.RequireEnvVar, "1");

        var act = () => FfmpegTestBinaries.Require(null, exists: false, "ffmpeg");

        act.Should().Throw<XunitException>(
            "CI sets this precisely so a release gate cannot be green by running nothing");
    }

    [Fact]
    public void TheOldShimCanNoLongerReturnTrue()
    {
        // Every one of the ~50 call sites is written `if (SkipIfMissing(...)) return;`. If this ever
        // returns true again, all of them go back to passing silently.
        Environment.SetEnvironmentVariable(FfmpegTestBinaries.RequireEnvVar, null);

        FfmpegTestBinaries.SkipIfMissing(null, exists: true, "ffmpeg").Should().BeFalse();

        var act = () => FfmpegTestBinaries.SkipIfMissing(null, exists: false, "ffmpeg");
        act.Should().Throw<Exception>("absence throws now — it never returns true to be early-returned on");
    }

    // ---- The search -----------------------------------------------------------------------------------

    [Fact]
    public void TheSearchFindsABinaryInTheFirstCandidateThatHasIt()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "stand-in");

            var found = FfmpegTestBinaries.ResolveFrom(
                new[] { @"Z:\does-not-exist", null, string.Empty, dir }, "ffmpeg.exe");

            found.Should().Be(
                Path.Combine(dir, "ffmpeg.exe"),
                "missing, null and empty candidates are stepped over rather than ending the search");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void EarlierCandidatesWin()
    {
        var first = NewDir();
        var second = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(first, "ffmpeg.exe"), "the one we want");
            File.WriteAllText(Path.Combine(second, "ffmpeg.exe"), "the fallback");

            FfmpegTestBinaries.ResolveFrom(new[] { first, second }, "ffmpeg.exe")
                .Should().Be(
                    Path.Combine(first, "ffmpeg.exe"),
                    "an explicit override must beat the legacy hardcoded path, which is searched LAST");
        }
        finally
        {
            TryDelete(first);
            TryDelete(second);
        }
    }

    [Fact]
    public void NothingAnywhereResolvesToNull_WhichIsWhatMakesTheGuardFire()
    {
        FfmpegTestBinaries.ResolveFrom(new[] { @"Z:\nope", @"Q:\also-nope" }, "ffmpeg.exe")
            .Should().BeNull("this is the CI case — and it must be detectable, not silently absent");
    }

    [Fact]
    public void AMalformedCandidateDoesNotAbortTheSearch()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "stand-in");

            // A PATH entry with characters no filesystem accepts. One bad PATH entry taking the search
            // down would look exactly like "ffmpeg is not installed" — the failure mode being removed.
            var found = FfmpegTestBinaries.ResolveFrom(new[] { "bad|path<>", dir }, "ffmpeg.exe");

            found.Should().Be(Path.Combine(dir, "ffmpeg.exe"));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void OnThisMachineTheBinariesResolve_SoTheIntegrationSuitesReallyRun()
    {
        // Not a tautology: if resolution breaks, every integration test quietly turns into a skip and
        // the suite still reports green. This is the canary for that.
        FfmpegTestBinaries.FfmpegExists.Should().BeTrue(
            "ffmpeg must be found here, or the 43 integration tests are not exercising anything");
        FfmpegTestBinaries.FfprobeExists.Should().BeTrue();
        File.Exists(FfmpegTestBinaries.Ffmpeg!).Should().BeTrue();
    }
}
