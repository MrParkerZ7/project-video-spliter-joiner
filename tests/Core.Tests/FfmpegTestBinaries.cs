using System.IO;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Env-independent constants pointing at the real ffmpeg/ffprobe binaries used for
/// integration tests. Integration tests <see cref="File.Exists(string)"/>-guard these so a
/// CI machine without the binaries stays green (the test no-ops and prints that it skipped).
/// The binaries are NOT copied into the repo — this only references them as an override.
/// </summary>
internal static class FfmpegTestBinaries
{
    public const string Ffmpeg = @"D:\_env_storeage\ffmpeg-7.1.1-essentials_build\bin\ffmpeg.exe";
    public const string Ffprobe = @"D:\_env_storeage\ffmpeg-7.1.1-essentials_build\bin\ffprobe.exe";

    public static bool FfmpegExists => File.Exists(Ffmpeg);

    public static bool FfprobeExists => File.Exists(Ffprobe);

    /// <summary>
    /// Returns true and prints a skip notice if the binary is absent — call at the top of an
    /// integration test and early-return when it returns true.
    /// </summary>
    public static bool SkipIfMissing(ITestOutputHelper output, bool exists, string name)
    {
        if (!exists)
        {
            output.WriteLine($"[SKIPPED] {name} not found at expected path — integration test skipped (environment has no real binary).");
            return true;
        }

        return false;
    }
}
