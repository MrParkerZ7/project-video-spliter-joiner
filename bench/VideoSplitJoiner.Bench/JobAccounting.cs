using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VideoSplitJoiner.Bench;

/// <summary>
/// What one measured stretch cost, for everything in the bench's job: the bench process itself and every
/// ffmpeg/ffprobe child the app's own <c>FfmpegRunner</c>/<c>FfprobeRunner</c> started in it.
/// </summary>
/// <param name="CpuSeconds">User + kernel CPU time of every process in the job.</param>
/// <param name="ReadBytes">Bytes read by every process in the job (file reads, plus the small pipe traffic between the bench and its children).</param>
/// <param name="WriteBytes">Bytes written by every process in the job.</param>
/// <param name="Processes">Processes started in the job (the job's <c>TotalProcesses</c> delta).</param>
/// <param name="OthersCores">Machine-load sample: logical cores kept busy by processes OUTSIDE the job, averaged over the stretch.</param>
internal sealed record Usage(double CpuSeconds, long ReadBytes, long WriteBytes, int Processes, double OthersCores)
{
    public static readonly Usage None = new(0, 0, 0, 0, 0);
}

/// <summary>A point-in-time reading of the job counters, the system CPU times and the clock.</summary>
internal readonly record struct MeterMark(
    long CpuTicks100ns, ulong ReadBytes, ulong WriteBytes, uint TotalProcesses, ulong SysBusy100ns, long ClockTicks);

/// <summary>
/// T-188: the bench's own process is put in a Windows job object at start-up, so every child that
/// <c>FfmpegRunner</c>/<c>FfprobeRunner</c> starts inherits it, and the job's
/// <c>JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION</c> gives the bytes read and written, the CPU time and the
/// number of processes of everything a scenario ran, as a delta around the scenario. This replaces the
/// prototype's IoMeter, which started processes itself and so broke the "all ffmpeg/ffprobe execution goes
/// through the runners" rule. Scenarios run one at a time, so a delta belongs to its scenario. If the
/// assignment fails the bench stops with the error; there is no fallback that starts processes itself.
/// </summary>
internal static class JobAccounting
{
    private const int JobObjectBasicAndIoAccountingInformation = 8;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static IntPtr _job;

    /// <summary>True once <see cref="Start"/> has put this process in its job.</summary>
    public static bool IsActive => _job != IntPtr.Zero;

    /// <summary>
    /// Create the job (children are killed when the bench exits, so a stopped bench leaves no ffmpeg behind)
    /// and put this process in it. Throws <see cref="BenchException"/> when either step fails.
    /// </summary>
    public static void Start()
    {
        if (IsActive)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new BenchException(ExitCodes.Environment, "the bench measures through a Windows job object and runs on Windows only.");
        }

        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new BenchException(ExitCodes.Environment, "CreateJobObject failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var err = Marshal.GetLastWin32Error();
            CloseHandle(job);
            throw new BenchException(ExitCodes.Environment, "SetInformationJobObject failed: " + new Win32Exception(err).Message);
        }

        if (!AssignProcessToJobObject(job, GetCurrentProcess()))
        {
            var err = Marshal.GetLastWin32Error();
            CloseHandle(job);
            throw new BenchException(
                ExitCodes.Environment,
                "AssignProcessToJobObject failed (" + new Win32Exception(err).Message + "). The bench reads bytes and CPU " +
                "from its job and will not measure without one.");
        }

        _job = job;
    }

    /// <summary>Read the job counters, the system CPU times and the clock now.</summary>
    public static MeterMark Mark()
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("JobAccounting.Start must run before anything is measured.");
        }

        if (!QueryInformationJobObject(_job, JobObjectBasicAndIoAccountingInformation, out var info,
                Marshal.SizeOf<JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
        {
            throw new BenchException(ExitCodes.Environment, "QueryInformationJobObject failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        GetSystemTimes(out var idle, out var kernel, out var user);
        var busy = (ToTicks(kernel) - ToTicks(idle)) + ToTicks(user);
        return new MeterMark(
            info.BasicInfo.TotalUserTime + info.BasicInfo.TotalKernelTime,
            info.IoInfo.ReadTransferCount,
            info.IoInfo.WriteTransferCount,
            info.BasicInfo.TotalProcesses,
            busy,
            Stopwatch.GetTimestamp());
    }

    /// <summary>What happened in the job between <paramref name="start"/> and now.</summary>
    public static Usage Since(MeterMark start) => Between(start, Mark());

    /// <summary>What happened in the job between two marks.</summary>
    public static Usage Between(MeterMark a, MeterMark b)
    {
        var cpu = Math.Max(0, b.CpuTicks100ns - a.CpuTicks100ns);
        var wallTicks100ns = (b.ClockTicks - a.ClockTicks) * 10_000_000.0 / Stopwatch.Frequency;
        var sysBusy = b.SysBusy100ns >= a.SysBusy100ns ? (double)(b.SysBusy100ns - a.SysBusy100ns) : 0;
        var others = wallTicks100ns > 0 ? Math.Max(0, sysBusy - cpu) / wallTicks100ns : 0;
        return new Usage(
            cpu / 1e7,
            (long)(b.ReadBytes - a.ReadBytes),
            (long)(b.WriteBytes - a.WriteBytes),
            (int)(b.TotalProcesses - a.TotalProcesses),
            others);
    }

    private static ulong ToTicks(FILETIME t) => ((ulong)(uint)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION
    {
        public JOBOBJECT_BASIC_ACCOUNTING_INFORMATION BasicInfo;
        public IO_COUNTERS IoInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob, int infoClass, out JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
}

/// <summary>Wall clock + job usage around one awaited piece of work.</summary>
internal static class Meter
{
    /// <summary>Run <paramref name="work"/> and return its wall seconds and job usage.</summary>
    public static async Task<(double Seconds, Usage Usage)> Run(Func<Task> work)
    {
        var mark = JobAccounting.Mark();
        var sw = Stopwatch.StartNew();
        await work().ConfigureAwait(false);
        var s = sw.Elapsed.TotalSeconds;
        return (s, JobAccounting.Since(mark));
    }

    /// <summary>Run <paramref name="work"/> synchronously and return its wall seconds and job usage.</summary>
    public static (double Seconds, Usage Usage) RunSync(Action work)
    {
        var mark = JobAccounting.Mark();
        var sw = Stopwatch.StartNew();
        work();
        var s = sw.Elapsed.TotalSeconds;
        return (s, JobAccounting.Since(mark));
    }
}
