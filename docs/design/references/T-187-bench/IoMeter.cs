using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace T187Bench;

/// <summary>
/// Measurement-only: run ffmpeg/ffprobe with the SAME argument tokens the app builds, and read the child's
/// GetProcessIoCounters after exit — how many bytes the operation actually read/wrote. That is what decides how it
/// scales on a slow disk (HDD/USB/network). Not the app's runner; the app's runner is used for every timing.
/// </summary>
internal static class IoMeter
{
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

    public sealed record Io(double Seconds, long ReadBytes, long WriteBytes, int ExitCode, string StdErrTail, string StdOut);

    public static Io Run(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        var err = errTask.Result;
        var stdout = outTask.Result;
        var s = sw.Elapsed.TotalSeconds;
        GetProcessIoCounters(p.Handle, out var c);
        var tail = string.Join("\n", err.Split('\n').TakeLast(6));
        return new Io(s, (long)c.ReadTransferCount, (long)c.WriteTransferCount, p.ExitCode, tail, stdout);
    }
}
