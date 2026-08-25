namespace VideoSplitJoiner.Core.Io;

/// <summary>
/// Measures free space for the disk pre-flight. Abstracted (rather than calling
/// <see cref="System.IO.DriveInfo"/> inline) so the pre-flight's block/skip decision is
/// deterministically unit-testable without filling a real disk. Shared by both
/// <c>SplitEngine.EnsureEnoughFreeSpace</c> (per-run) and <c>BulkTrimEngine</c> (batch) — a
/// neutral abstraction both engines depend on, not one engine depending on the other.
/// </summary>
public interface IDiskSpaceProbe
{
    /// <summary>
    /// Available free bytes on the drive rooted at <paramref name="driveRoot"/>, or <c>null</c> when
    /// the drive cannot be measured (unknown/UNC/removable/not-ready) — an unmeasurable drive is
    /// SKIPPED by the pre-flight, never a false-positive block.
    /// </summary>
    long? GetAvailableFreeBytes(string driveRoot);
}

/// <summary>
/// Default <see cref="IDiskSpaceProbe"/> over <see cref="System.IO.DriveInfo"/>, mirroring the
/// engines' best-effort semantics: any measurement failure (unknown drive, UNC path, not ready,
/// thrown query) returns <c>null</c> = "skip this drive".
/// </summary>
internal sealed class DriveInfoDiskSpaceProbe : IDiskSpaceProbe
{
    public long? GetAvailableFreeBytes(string driveRoot)
    {
        try
        {
            if (string.IsNullOrEmpty(driveRoot))
            {
                return null;
            }

            var drive = new DriveInfo(driveRoot);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }
}
