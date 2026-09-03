using System;
using VideoSplitJoiner.Core.Io;

namespace VideoSplitJoiner.App.Io;

/// <summary>
/// Sends a replaced original's backup to the Windows <b>Recycle Bin</b> rather than deleting it
/// (T-122, epic G-041) — so "replace the original" stays undoable after the batch ends and even after
/// the app exits, which is what makes an otherwise-destructive feature safe to offer.
///
/// <para>Lives in the app (net8.0-windows) rather than Core (net8.0, deliberately OS/UI-free): the
/// Recycle Bin is a Windows shell concept. Core defines only the <see cref="IOriginalDisposer"/> seam
/// and defaults to keeping the backup.</para>
///
/// <para><b>T-155 — no dialogs.</b> This used
/// <c>Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(…, UIOption.OnlyErrorDialogs, …)</c>, and
/// <c>UIOption</c> offers only <c>AllDialogs</c> or <c>OnlyErrorDialogs</c> — there is no silent option.
/// Every locked file therefore raised a Windows dialog, on every run, which a user reported as
/// interrupting a feature that otherwise worked. It now goes through <see cref="ShellRecycleBin"/>
/// (<c>SHFileOperation</c> with <c>FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMATION</c>), which returns a
/// result instead of showing anything. The Bulk Cut screen already names what it could not remove
/// ("Still in use: …"), so the app was always the better place to say so.</para>
/// </summary>
public sealed class RecycleBinOriginalDisposer : IOriginalDisposer
{
    public void DisposeOriginalBackup(string backupPath)
    {
        try
        {
            // Best-effort by contract: the user's trimmed output is already safely in place, so a failure
            // to bin the backup must never fail the run — it only leaves a recoverable file. The caller
            // re-checks existence and reports the file as still in use.
            ShellRecycleBin.TryRecycle(backupPath);
        }
        catch (Exception)
        {
            // TryRecycle already swallows its own failures; this is belt-and-braces so a future change
            // to it can never turn a stray backup into a failed run.
        }
    }
}
