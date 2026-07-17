using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using VideoSplitJoiner.Core.Errors;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Thin WPF/OS glue for the copyable-error surface (T-037): copy the full error text to the clipboard
/// and reveal the saved full-log file. Deliberately tiny and code-behind-only — the testable text and
/// log path live on <see cref="UserFacingError"/> (<see cref="UserFacingError.CopyText"/> /
/// <see cref="UserFacingError.LogFilePath"/>); these methods just push OS actions. Every call is
/// wrapped so a clipboard/shell failure never bubbles up as a crash.
/// </summary>
internal static class ErrorActions
{
    /// <summary>Copy the error's full copyable text to the clipboard. Best-effort — swallows failures.</summary>
    public static void CopyError(UserFacingError? error)
    {
        if (error is null)
        {
            return;
        }

        TryCopy(error.CopyText);
    }

    /// <summary>Copy an arbitrary string to the clipboard. Best-effort — the clipboard can throw.</summary>
    public static void TryCopy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard access can fail (another app holds it, no desktop session) — never crash.
        }
    }

    /// <summary>
    /// Reveal the saved full-log file in Explorer (selected), or open its folder if selection fails.
    /// No-op when the error has no log path. Best-effort — swallows shell failures.
    /// </summary>
    public static void OpenLog(UserFacingError? error)
    {
        var path = error?.LogFilePath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                // Select the file in a new Explorer window.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch
        {
            // Opening Explorer is non-critical; a failure must never crash the app.
        }
    }
}
