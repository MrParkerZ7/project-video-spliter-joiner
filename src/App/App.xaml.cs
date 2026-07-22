using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VideoSplitJoiner.App.Views;
using VideoSplitJoiner.Core.Errors;

namespace VideoSplitJoiner.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        InitializeFfmpegForPreview();
        base.OnStartup(e);
        WireGlobalExceptionHandlers();
    }

    /// <summary>
    /// Global unhandled-exception safety net (T-079). Without this an unhandled exception on the UI
    /// dispatcher, a background task, or a native path silently kills the process (no dialog, no log).
    /// We wire all three managed sinks so the app always LOGS the crash (via <see cref="ErrorLogWriter"/>
    /// to <c>%LOCALAPPDATA%/VideoSplitJoiner/logs/</c>) and, for a recoverable UI-thread exception, shows
    /// a friendly copyable message and stays alive instead of vanishing. Every handler body is wrapped in
    /// its own try/catch so a throw inside a crash handler can never recurse.
    /// </summary>
    private void WireGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// UI-thread (dispatcher) unhandled exception: log it, show a friendly copyable dialog naming the
    /// saved log path, and mark it handled so a recoverable UI error does NOT tear down the app.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var logPath = TryLogCrash("Dispatcher", e.Exception);
            ShowCrashDialog(e.Exception, logPath);
            e.Handled = true; // recoverable UI-thread exception must not kill the app.
        }
        catch
        {
            // A throw inside the crash handler must never recurse / re-crash — swallow.
        }
    }

    /// <summary>
    /// AppDomain unhandled exception: last-ditch synchronous log. The process is going down regardless
    /// (managed handlers cannot recover this), so we only record it best-effort.
    /// </summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            TryLogCrash("AppDomain", e.ExceptionObject as Exception);
        }
        catch
        {
            // Best-effort last-ditch record — swallow any failure.
        }
    }

    /// <summary>
    /// Unobserved faulted background <see cref="Task"/> (e.g. keyframe index / thumbnail grab): log it and
    /// call <see cref="UnobservedTaskExceptionEventArgs.SetObserved"/> so it never tears down the process.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            TryLogCrash("UnobservedTask", e.Exception);
            e.SetObserved(); // observed → the finalizer will not escalate it to a process kill.
        }
        catch
        {
            // A throw inside the crash handler must never recurse — swallow.
        }
    }

    /// <summary>Best-effort crash log via <see cref="ErrorLogWriter"/>; returns the log path or null.</summary>
    private static string? TryLogCrash(string source, Exception? ex)
    {
        try
        {
            return new ErrorLogWriter().TryWriteCrash(source, ex);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Show a friendly, copyable crash message telling the user a log was saved (with its path). Reuses the
    /// <see cref="UserFacingError"/>/<see cref="ErrorActions"/> copy affordance, then a plain MessageBox so
    /// the app stays responsive. Entirely best-effort — never throws.
    /// </summary>
    private static void ShowCrashDialog(Exception ex, string? logPath)
    {
        try
        {
            var error = new UserFacingError(
                ErrorCategory.Unknown,
                "The app hit an unexpected error but stayed open.",
                RawTail: ex.Message,
                Hint: "A crash log was saved — please attach it if you report this.",
                LogFilePath: logPath,
                FullText: ex.ToString());

            // Put the full copyable text on the clipboard so the user can paste it into a report.
            ErrorActions.CopyError(error);

            var body = logPath is not null
                ? $"{error.Message}\n\n{ex.Message}\n\nA crash log was saved to:\n{logPath}\n\n(The full details have been copied to your clipboard.)"
                : $"{error.Message}\n\n{ex.Message}\n\n(The full details have been copied to your clipboard.)";

            MessageBox.Show(body, "VideoSplitJoiner — unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // The dialog is a courtesy; a failure here must never re-crash the crash handler.
        }
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
