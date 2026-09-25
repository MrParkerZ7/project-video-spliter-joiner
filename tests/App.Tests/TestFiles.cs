using System.IO;

namespace VideoSplitJoiner.App.Tests;

/// <summary>Small on-disk fixtures shared by more than one test class.</summary>
internal static class TestFiles
{
    /// <summary>
    /// A file that exists and is definitely NOT an image (T-170's actual defect: a 1-byte text file named
    /// <c>.jpg</c>). Promoted from <c>BulkCutProfileThumbnailTests</c> by T-180, whose hover card must collapse its
    /// picture box for such a file rather than show an empty frame.
    /// </summary>
    public static string MakeNonImage(string directory, string fileName, string content = "x")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
