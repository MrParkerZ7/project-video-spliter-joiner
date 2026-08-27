using System;
using Microsoft.VisualBasic.FileIO;
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
/// </summary>
public sealed class RecycleBinOriginalDisposer : IOriginalDisposer
{
    public void DisposeOriginalBackup(string backupPath)
    {
        try
        {
            if (System.IO.File.Exists(backupPath))
            {
                FileSystem.DeleteFile(backupPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }
        catch (Exception)
        {
            // Best-effort by contract: the user's trimmed output is already safely in place, so a
            // failure to bin the backup must never fail the run - it only leaves a recoverable file.
        }
    }
}
