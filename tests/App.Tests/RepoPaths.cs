using System.IO;
using FluentAssertions;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Walks up from the test binary to the repo root, for the handful of tests that assert against SOURCE
/// rather than behaviour (the composition-root guards, the installer script check).
///
/// <para>Extracted when a third copy of the same while-loop was about to be written. Asserting against
/// source is a deliberate, narrow tool — used where constructing the real object would build the FFME
/// player and the ffmpeg graph, which is heavier and flakier than reading the one line that matters —
/// but the walk itself is incidental, and three hand-maintained copies of an incidental walk is how the
/// picker filter went stale (T-158).</para>
/// </summary>
internal static class RepoPaths
{
    /// <summary>The repo root — the directory holding <c>VideoSplitJoiner.sln</c>.</summary>
    internal static string Root()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VideoSplitJoiner.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must be able to find the repo root");
        return dir!.FullName;
    }

    /// <summary>Full path to a repo-relative file, e.g. <c>Source("src", "App", "App.xaml")</c>.</summary>
    internal static string Source(params string[] parts) => Path.Combine(Root(), Path.Combine(parts));
}
