using System.Diagnostics;
using System.IO;
using System.Windows;

namespace VideoSplitJoiner.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        InitializeFfmpegForPreview();
        base.OnStartup(e);
    }

    /// <summary>
    /// Point FFME at the ffmpeg SHARED build so the video preview can P/Invoke-load
    /// the native libraries (avcodec-61 / avformat-61 / avutil-59 / ..., ffmpeg 7.x).
    /// Must run before any FFME control loads (T-019). Best-effort: if no shared build
    /// is found, the preview will simply be unavailable — we never crash the app here.
    /// </summary>
    private static void InitializeFfmpegForPreview()
    {
        try
        {
            var dir = ResolveFfmpegSharedDirectory();
            if (dir is not null)
            {
                Unosquare.FFME.Library.FFmpegDirectory = dir;
            }
            else
            {
                Debug.WriteLine(
                    "[FFME] No ffmpeg shared build found (avcodec-*.dll). " +
                    "Video preview will be unavailable. Run packaging/fetch-ffmpeg-shared.ps1.");
            }
        }
        catch (Exception ex)
        {
            // Never let ffmpeg init crash startup — the preview is optional.
            Debug.WriteLine($"[FFME] FFmpegDirectory init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolve the folder holding the ffmpeg shared DLLs, trying in order:
    /// (a) &lt;BaseDirectory&gt;/ffmpeg      — packaged layout (T-021 populates it),
    /// (b) repo-local ffmpeg-shared/ found by walking up from BaseDirectory (dev),
    /// (c) an absolute dev fallback path.
    /// Returns the first that contains an avcodec-*.dll, else null.
    /// </summary>
    private static string? ResolveFfmpegSharedDirectory()
    {
        var candidates = new List<string?>
        {
            // (a) packaged: app-local ffmpeg/
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
        };

        // (b) walk up from BaseDirectory looking for a sibling ffmpeg-shared/ (dev tree).
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && probe is not null; depth++, probe = probe.Parent)
        {
            candidates.Add(Path.Combine(probe.FullName, "ffmpeg-shared"));
        }

        // (c) absolute dev fallback.
        candidates.Add(@"D:\Programing\Projects\project-video-spliter-joiner\ffmpeg-shared");

        foreach (var candidate in candidates)
        {
            if (candidate is null) continue;
            if (!Directory.Exists(candidate)) continue;
            if (Directory.EnumerateFiles(candidate, "avcodec-*.dll").Any())
            {
                return candidate;
            }
        }

        return null;
    }
}
