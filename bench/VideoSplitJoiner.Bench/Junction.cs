using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VideoSplitJoiner.Bench;

/// <summary>
/// Selftest helper: make a directory junction (a mount-point reparse point) without admin rights and without
/// starting <c>mklink</c> — FSCTL_SET_REPARSE_POINT on an empty folder.
/// </summary>
internal static class Junction
{
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    /// <summary>Create <paramref name="junction"/> (an empty folder is made) pointing at the folder <paramref name="target"/>.</summary>
    public static void Create(string junction, string target)
    {
        var full = Path.GetFullPath(target);
        CreateRaw(junction, @"\??\" + full, full);
    }

    /// <summary>
    /// The volume-GUID form of a folder's path, as a junction's substitute name: <c>\??\Volume{GUID}\rest</c> for
    /// <c>C:\rest</c> (what <c>mountvol</c> writes). Null when the folder's drive has no volume GUID (a SUBST or network drive).
    /// </summary>
    public static string? VolumeGuidSubstitute(string folder)
    {
        var full = Path.GetFullPath(folder);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        var buffer = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, buffer, buffer.Capacity))
        {
            return null;
        }

        var volume = buffer.ToString(); // \\?\Volume{GUID}\
        return volume.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? @"\??\" + volume[4..] + full[root.Length..].TrimStart(Path.DirectorySeparatorChar)
            : null;
    }

    /// <summary>Create <paramref name="junction"/> with the given substitute (NT) name and print name.</summary>
    public static void CreateRaw(string junction, string substituteName, string printName)
    {
        Directory.CreateDirectory(junction);
        var subst = Encoding.Unicode.GetBytes(substituteName);
        var print = Encoding.Unicode.GetBytes(printName);
        var pathBytes = subst.Length + 2 + print.Length + 2;
        var buffer = new byte[16 + pathBytes];
        BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(8 + pathBytes)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes((ushort)subst.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(subst.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        subst.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + subst.Length + 2);

        using var handle = CreateFileW(junction, GenericWrite, 0, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"cannot open '{junction}' to make it a junction");
        }

        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"cannot make '{junction}' a junction to '{substituteName}'");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize, out uint returned, IntPtr overlapped);
}
