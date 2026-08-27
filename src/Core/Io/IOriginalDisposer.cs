namespace VideoSplitJoiner.Core.Io;

/// <summary>
/// Decides what happens to the BACKUP of an original file after it has been successfully replaced
/// (T-122, epic G-041). Abstracted — like <see cref="IDiskSpaceProbe"/> — so the destructive step is
/// deterministically unit-testable, and so the platform-specific "send to Recycle Bin" implementation
/// can live in the Windows app layer while Core stays UI- and OS-free.
///
/// <para>The engine only ever calls this AFTER a verified-complete output has atomically taken the
/// original's place, so a failed or cancelled run never reaches it.</para>
/// </summary>
public interface IOriginalDisposer
{
    /// <summary>
    /// Dispose of <paramref name="backupPath"/> — the pre-replacement copy of the user's original file.
    /// Implementations should be best-effort: a failure here means a stray backup file, which must never
    /// fail the run (the user's trimmed output is already safely in place).
    /// </summary>
    void DisposeOriginalBackup(string backupPath);
}

/// <summary>
/// Keeps the backup on disk. The safest possible behaviour and the default for Core-only/headless use —
/// nothing is ever destroyed, at the cost of leaving a <c>.vsj-original</c> file beside the output.
/// </summary>
public sealed class KeepOriginalBackupDisposer : IOriginalDisposer
{
    public void DisposeOriginalBackup(string backupPath)
    {
        // Intentionally nothing — the backup is retained for the user.
    }
}

/// <summary>
/// Permanently deletes the backup. Use only where an undo path is genuinely unwanted; the app wires the
/// Recycle-Bin implementation instead so a replaced original stays recoverable.
/// </summary>
public sealed class DeleteOriginalBackupDisposer : IOriginalDisposer
{
    public void DisposeOriginalBackup(string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch
        {
            // Best-effort: a stray backup must never fail an otherwise-successful run.
        }
    }
}
