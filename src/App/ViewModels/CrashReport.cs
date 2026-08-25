namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Pure, WPF-free composition of the global crash dialog's user-facing text (SPEC-015 I24).
/// Extracted from <c>App.OnDispatcherUnhandledException</c>/<c>ShowCrashDialog</c> so the exact
/// message the user sees — and copies to their clipboard for a bug report — is unit-testable without
/// raising a real dispatcher exception. The handler still owns logging, the clipboard copy, and the
/// <c>MessageBox</c>; this only builds the body string.
/// </summary>
public static class CrashReport
{
    /// <summary>
    /// The crash-dialog body: the friendly headline, the exception message, an optional "a crash log
    /// was saved to &lt;path&gt;" line (only when a log was written), and the clipboard-copied footer.
    /// </summary>
    public static string ComposeMessage(string headline, string exceptionMessage, string? logPath)
    {
        var savedLine = logPath is not null
            ? $"\n\nA crash log was saved to:\n{logPath}"
            : string.Empty;

        return $"{headline}\n\n{exceptionMessage}{savedLine}\n\n(The full details have been copied to your clipboard.)";
    }
}
