using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VideoSplitJoiner.App.Io;

/// <summary>
/// Names the processes currently holding a file open, using the Windows <b>Restart Manager</b> (T-155).
///
/// <para><b>Why.</b> "Delete originals" popped a Windows dialog on every run, and the reporter asked the
/// right question: <i>can the app release the file itself first?</i> T-145 already released the preview's
/// handle, so a refusal on every run means something ELSE holds it — and this codebase has repeatedly
/// paid for answering that kind of question with a hypothesis. Restart Manager answers it with a process
/// name, which is the difference between "we think ffmpeg is lingering" and knowing.</para>
///
/// <para>It also settles the question the app cannot fix: if the holder is an antivirus scanner or the
/// shell's thumbnail cache, no amount of releasing our own handles helps, and the honest response is to
/// say which program is holding it rather than to keep trying.</para>
///
/// <para>Diagnostic only — read-only, best-effort, and never throws. Nothing in the delete path depends
/// on it succeeding.</para>
/// </summary>
internal static class FileLockOwner
{
    private const int RmRebootReasonNone = 0;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        IntPtr rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    /// <summary>
    /// Process names holding <paramref name="path"/> open, most useful first. Empty when nothing holds it,
    /// when the query fails, or on any error — a diagnostic that throws is worse than one that says
    /// nothing.
    /// </summary>
    public static IReadOnlyList<string> WhoIsHolding(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<string>();
        }

        uint session = 0;
        var started = false;

        try
        {
            var key = Guid.NewGuid().ToString();
            if (RmStartSession(out session, 0, key) != 0)
            {
                return Array.Empty<string>();
            }

            started = true;

            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, null) != 0)
            {
                return Array.Empty<string>();
            }

            uint pnProcInfo = 0;
            uint rebootReasons = RmRebootReasonNone;

            var result = RmGetList(session, out var needed, ref pnProcInfo, null, ref rebootReasons);
            if (result == 0 || needed == 0)
            {
                return Array.Empty<string>();   // nothing holds it
            }

            if (result != ErrorMoreData)
            {
                return Array.Empty<string>();
            }

            var infos = new RM_PROCESS_INFO[needed];
            pnProcInfo = needed;

            if (RmGetList(session, out _, ref pnProcInfo, infos, ref rebootReasons) != 0)
            {
                return Array.Empty<string>();
            }

            return infos
                .Take((int)pnProcInfo)
                .Select(i => string.IsNullOrWhiteSpace(i.strAppName)
                    ? $"pid {i.Process.dwProcessId}"
                    : $"{i.strAppName} (pid {i.Process.dwProcessId})")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
        finally
        {
            if (started)
            {
                try { RmEndSession(session); } catch { /* best-effort */ }
            }
        }
    }
}
