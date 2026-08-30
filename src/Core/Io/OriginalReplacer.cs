using System;
using System.IO;

namespace VideoSplitJoiner.Core.Io;

/// <summary>
/// Puts a produced file where the user's ORIGINAL lives, keeping a backup throughout so the data is
/// never in a state where it exists under no name (T-122, T-130).
///
/// <para><b>Why this is its own type.</b> It was <c>private</c> inside <c>SplitEngine</c>, which meant the
/// frame-exact cut path could not reuse it — <c>SmartCutEngine</c> finishes with its own
/// delete-then-move, so combining Exact cut with replace-originals would have hard-deleted the original
/// with no backup and no disposer. That combination had to be refused outright (SPEC-002 I54). One
/// implementation, reachable from both engines, is what lets the refusal be lifted.</para>
///
/// <para><b>The guarantee.</b> Prefers <see cref="File.Replace(string,string,string)"/>, which is atomic
/// where the filesystem supports it. Where it is not supported (exFAT, some SMB shares) it falls back to
/// rename-original-aside → move-new-into-place, and if that second step fails it puts the original back.
/// At no instant does the user's data exist only in a temp file that is about to be swept.</para>
///
/// <para>The backup's fate is the injected <see cref="IOriginalDisposer"/>'s decision — the app sends it
/// to the Recycle Bin; Core's default keeps it.</para>
/// </summary>
public sealed class OriginalReplacer
{
    /// <summary>Suffix of the pre-replacement copy, kept until the disposer decides its fate.</summary>
    public const string BackupSuffix = ".vsj-original";

    private readonly IOriginalDisposer _disposer;

    public OriginalReplacer(IOriginalDisposer disposer)
        => _disposer = disposer ?? throw new ArgumentNullException(nameof(disposer));

    /// <summary>
    /// Move <paramref name="producedFile"/> onto <paramref name="originalPath"/>, keeping the original as
    /// a backup until the swap has committed. Throws if the replacement genuinely cannot be completed —
    /// in which case the original is left exactly as it was.
    /// </summary>
    public void Replace(string producedFile, string originalPath)
    {
        var backup = originalPath + BackupSuffix;

        try
        {
            if (File.Exists(backup))
            {
                File.Delete(backup); // A stale backup from an earlier interrupted run.
            }
        }
        catch
        {
            // Non-fatal — File.Replace/the fallback will surface a real problem below.
        }

        try
        {
            File.Replace(producedFile, originalPath, backup, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Fallback: move the original aside FIRST, so the bytes always exist under one name or the
            // other, then put the new file in place. If the second step fails, restore the original.
            File.Move(originalPath, backup, overwrite: true);
            try
            {
                File.Move(producedFile, originalPath);
            }
            catch
            {
                File.Move(backup, originalPath, overwrite: true); // Put the user's file back.
                throw;
            }
        }

        _disposer.DisposeOriginalBackup(backup);
    }
}
