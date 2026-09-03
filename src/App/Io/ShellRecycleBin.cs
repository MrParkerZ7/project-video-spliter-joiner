using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace VideoSplitJoiner.App.Io;

/// <summary>
/// Sends a file to the Windows Recycle Bin <b>without ever showing a dialog</b> (T-155).
///
/// <para><b>Why not the framework helper.</b> <c>Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile</c>
/// takes a <c>UIOption</c> with exactly two values — <c>AllDialogs</c> and <c>OnlyErrorDialogs</c>. There
/// is no "no dialogs". The quieter one still puts a shell error dialog on screen whenever a file is
/// locked, which is what made "Delete originals" interrupt the user on <i>every</i> run. It cannot be
/// configured away, so the API had to go.</para>
///
/// <para><c>SHFileOperation</c> with <c>FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI</c> does the same
/// job and returns a <b>result code</b> instead. That is strictly better here: the screen already reports
/// "Sent N to the Recycle Bin. Still in use: …" in its own words, so it never needed Windows to tell the
/// user anything — and a failure it can describe beats a modal it cannot control.</para>
/// </summary>
internal static class ShellRecycleBin
{
    private const int FO_DELETE = 0x0003;

    private const ushort FOF_SILENT = 0x0004;            // no progress UI
    private const ushort FOF_NOCONFIRMATION = 0x0010;    // no "are you sure"
    private const ushort FOF_ALLOWUNDO = 0x0040;         // THIS is what makes it recycle rather than delete
    private const ushort FOF_NOERRORUI = 0x0400;         // no error dialog — the point of the exercise
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;

    // NOTE: no Pack. SHFILEOPSTRUCT must use the platform's NATURAL alignment — forcing Pack = 1 removes
    // the 4 bytes of padding after wFunc on x64, so shell32 reads pFrom from the wrong offset and
    // dereferences garbage. That is an AccessViolationException that takes the whole process with it, and
    // it killed the test host on the first run of this file.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// Try to bin <paramref name="path"/>. Returns true when the file is genuinely gone afterwards.
    ///
    /// <para>Verified rather than assumed: <c>SHFileOperation</c> can report success in cases where the
    /// file survives, and the caller's summary claims a count to the user — so the check is "is it
    /// actually gone", the same discipline the delete-originals sweep already applies.</para>
    ///
    /// <para><paramref name="attempts"/> covers the common case of a handle that is about to close: a
    /// frame grab or probe finishing a moment after the batch did. Bounded and short — a long wait would
    /// turn a responsive app into a hung one, and a file held for a full second is held by something that
    /// is not about to let go.</para>
    /// </summary>
    public static bool TryRecycle(string path, int attempts = 3, int delayMs = 120)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            if (!File.Exists(path))
            {
                // Already gone (possibly by our own earlier attempt) — that is the desired end state.
                return true;
            }

            if (RecycleOnce(path) && !File.Exists(path))
            {
                return true;
            }

            if (attempt < attempts - 1)
            {
                Thread.Sleep(delayMs);
            }
        }

        return !File.Exists(path);
    }

    private static bool RecycleOnce(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,

                // pFrom is a DOUBLE-null-terminated list. One trailing '\0' comes from the marshaller,
                // so the string itself must supply the other; without it the call reads past the buffer.
                pFrom = path + "\0",
                pTo = null,
                fFlags = FOF_ALLOWUNDO | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR,
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null,
            };

            return SHFileOperationW(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        catch (Exception)
        {
            // A P/Invoke failure must not fail the run — the caller reports the file as still in use.
            return false;
        }
    }
}
