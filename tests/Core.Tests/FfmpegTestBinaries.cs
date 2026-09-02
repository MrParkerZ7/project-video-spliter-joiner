using System;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Locates the real ffmpeg/ffprobe binaries the integration tests drive, and decides — honestly —
/// what happens when they are absent (T-149).
///
/// <para><b>What was wrong.</b> The path was a hardcoded absolute
/// (<c>D:\_env_storeage\ffmpeg-…\bin\ffmpeg.exe</c>) that exists on exactly one machine, and the guard
/// returned <c>true</c> so callers could early-<c>return</c>. xUnit records a method that returns
/// normally as <b>PASSED</b> — so on CI, where that path does not exist, 43 integration tests across the
/// split / join / smart-cut / probe suites reported green <b>without executing anything</b>, and the
/// release gate could not fail. The old summary comment said as much out loud: "a CI machine without the
/// binaries stays green". Staying green by not running is not a gate.</para>
///
/// <para><b>What it does now.</b> Resolves the binaries by search rather than by assumption, and when
/// they genuinely cannot be found the test is <b>skipped visibly</b> (<see cref="Skip"/> throws, which
/// xUnit records as Skipped and shows in the run summary) instead of passing silently. Setting
/// <c>VSJ_REQUIRE_FFMPEG=1</c> — which CI does — turns absence into a hard <b>failure</b>, so a release
/// gate can never again be green because it ran nothing.</para>
/// </summary>
internal static class FfmpegTestBinaries
{
    /// <summary>Set to <c>1</c>/<c>true</c> to make a missing binary FAIL rather than skip. CI sets it.</summary>
    public const string RequireEnvVar = "VSJ_REQUIRE_FFMPEG";

    /// <summary>Points the search straight at a directory holding ffmpeg.exe/ffprobe.exe.</summary>
    public const string DirEnvVar = "VSJ_FFMPEG_DIR";

    private static readonly Lazy<string?> FfmpegPath = new(() => Resolve("ffmpeg.exe"));
    private static readonly Lazy<string?> FfprobePath = new(() => Resolve("ffprobe.exe"));

    /// <summary>Full path to ffmpeg.exe, or null when it cannot be found anywhere.</summary>
    public static string? Ffmpeg => FfmpegPath.Value;

    /// <summary>Full path to ffprobe.exe, or null when it cannot be found anywhere.</summary>
    public static string? Ffprobe => FfprobePath.Value;

    public static bool FfmpegExists => Ffmpeg is not null;

    public static bool FfprobeExists => Ffprobe is not null;

    /// <summary>
    /// The search, in order of how deliberate each location is:
    /// <list type="number">
    /// <item><c>VSJ_FFMPEG_DIR</c> — an explicit override, so a developer or CI can be unambiguous.</item>
    /// <item><c>ffmpeg-shared/</c> beside the repo — where <c>packaging/fetch-ffmpeg-shared.ps1</c> puts
    /// it, which is what CI runs.</item>
    /// <item>Beside the test binaries (<c>ffmpeg/</c> or the output folder itself) — the app's own
    /// bundled layout.</item>
    /// <item><c>PATH</c> — the ordinary developer install.</item>
    /// <item>The historical hardcoded location, kept LAST so the machine it works on keeps working.</item>
    /// </list>
    /// </summary>
    private static string? Resolve(string exe) => ResolveFrom(CandidateDirectories(), exe);

    /// <summary>
    /// The search itself, over an explicit candidate list so it is directly testable — the point of the
    /// ticket was that resolution had never been exercised anywhere but one developer's machine.
    /// </summary>
    internal static string? ResolveFrom(System.Collections.Generic.IEnumerable<string?> candidates, string exe)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var full = Path.Combine(candidate, exe);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // A malformed PATH entry must not take the whole search down with it.
            }
        }

        return null;
    }

    private static System.Collections.Generic.IEnumerable<string> CandidateDirectories()
    {
        var explicitDir = Environment.GetEnvironmentVariable(DirEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            yield return explicitDir;
        }

        // Walk up from the test binaries to the repo root, checking the packaging fetch destination
        // and the app's bundled layout on the way.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            yield return Path.Combine(dir.FullName, "ffmpeg-shared");
            yield return Path.Combine(dir.FullName, "ffmpeg");
            dir = dir.Parent;
        }

        yield return AppContext.BaseDirectory;

        foreach (var onPath in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator))
        {
            yield return onPath;
        }

        // Last, not first: this is one developer's machine, not a contract.
        yield return @"D:\_env_storeage\ffmpeg-7.1.1-essentials_build\bin";
    }

    /// <summary>
    /// Require the binary. Call at the top of an integration test on a
    /// <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>.
    ///
    /// <para>Absent + <c>VSJ_REQUIRE_FFMPEG</c> set → <b>fails</b> (a release gate that silently runs
    /// nothing is worse than a red one). Absent otherwise → <b>skips visibly</b>. Present → returns and
    /// the test runs. It can no longer end in a silent pass.</para>
    /// </summary>
    public static void Require(ITestOutputHelper? output, bool exists, string name)
    {
        if (exists)
        {
            return;
        }

        var required = Environment.GetEnvironmentVariable(RequireEnvVar);
        var mustHave = required is "1" or "true" or "TRUE" or "True";

        var where = Environment.GetEnvironmentVariable(DirEnvVar) is { Length: > 0 } d
            ? $" ({DirEnvVar}={d})"
            : string.Empty;

        var message =
            $"{name} could not be found{where}. Searched {DirEnvVar}, ffmpeg-shared/ and ffmpeg/ up from " +
            $"the test output, PATH, and the legacy hardcoded path. " +
            $"Run packaging/fetch-ffmpeg-shared.ps1, or set {DirEnvVar}.";

        output?.WriteLine((mustHave ? "[FAILED] " : "[SKIPPED] ") + message);

        if (mustHave)
        {
            // The release gate asked for these to run. Not running them is the failure.
            Assert.Fail(
                $"{RequireEnvVar} is set, so the integration tests must actually run — but {message}");
        }

        Skip.If(true, message);
    }

    /// <summary>
    /// Back-compat shim for the old call shape. Never returns true any more: it either returns false
    /// (binary present, carry on) or throws a skip/failure. Kept so the 50-odd call sites did not all
    /// have to change shape in one commit.
    /// </summary>
    public static bool SkipIfMissing(ITestOutputHelper? output, bool exists, string name)
    {
        Require(output, exists, name);
        return false;
    }
}
