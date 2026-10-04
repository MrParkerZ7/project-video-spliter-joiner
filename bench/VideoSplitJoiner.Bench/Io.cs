using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VideoSplitJoiner.Bench;

/// <summary>File helpers ported from the prototype. Every path they take is already inside the fixture root.</summary>
internal static class Io
{
    /// <summary>
    /// Copy <paramref name="src"/> with FILE_FLAG_NO_BUFFERING|WRITE_THROUGH so the new file's data never enters the
    /// Windows file cache — the first read of the copy is a genuinely cold (from-disk) read. This is how the bench
    /// gets "cold" numbers without admin rights to flush the standby list. The copy lands under
    /// <c>&lt;root&gt;/work/cold</c> and goes back through the guard before anything reads it.
    /// </summary>
    public static unsafe string ColdCopy(BenchContext c, string src, string tag)
    {
        var dir = Path.Combine(c.Work, "cold");
        EnsureFreeSpace(c, new FileInfo(src).Length, $"a cold copy of '{Path.GetFileName(src)}'");
        Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, $"{tag}-{Guid.NewGuid().ToString("N")[..6]}{Path.GetExtension(src)}");
        const int Buf = 8 << 20;
        var mem = (byte*)NativeMemory.AlignedAlloc(Buf, 4096);
        long len;
        try
        {
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var h = File.OpenHandle(dst, FileMode.Create, FileAccess.Write, FileShare.None, (FileOptions)0x20000000 | FileOptions.WriteThrough))
            {
                len = input.Length;
                long off = 0;
                var span = new Span<byte>(mem, Buf);
                while (off < len)
                {
                    var want = (int)Math.Min(Buf, len - off);
                    var got = 0;
                    while (got < want)
                    {
                        var n = input.Read(span.Slice(got, want - got));
                        if (n <= 0)
                        {
                            break;
                        }

                        got += n;
                    }

                    if (got == 0)
                    {
                        break;
                    }

                    var padded = (got + 4095) & ~4095;
                    if (padded > got)
                    {
                        span.Slice(got, padded - got).Clear();
                    }

                    RandomAccess.Write(h, new ReadOnlySpan<byte>(mem, padded), off);
                    off += got;
                }
            }

            using (var fs = new FileStream(dst, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(len);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
        }

        // A fresh, distinct mtime so MediaProbe's (path, mtime, length) keyframe cache never confuses it.
        File.SetLastWriteTimeUtc(dst, DateTime.UtcNow);
        return c.Root.Resolve(dst);
    }

    public static double ReadAllSeconds(string path)
    {
        var sw = Stopwatch.StartNew();
        var buf = new byte[1 << 20];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        while (fs.Read(buf, 0, buf.Length) > 0)
        {
        }

        return sw.Elapsed.TotalSeconds;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";

    /// <summary>
    /// Refuse (<see cref="ExitCodes.DiskSpace"/>) when the root's drive has less than <paramref name="bytes"/> plus a
    /// 512 MB margin free, before <paramref name="what"/> writes anything.
    /// </summary>
    public static void EnsureFreeSpace(BenchContext c, long bytes, string what)
    {
        var drive = Path.GetPathRoot(c.Root.Real);
        if (string.IsNullOrEmpty(drive))
        {
            return;
        }

        long free;
        try
        {
            free = new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        var need = bytes + (512L << 20);
        if (free < need)
        {
            throw new BenchException(ExitCodes.DiskSpace,
                $"{what} needs about {need / 1e9:0.0} GB free at '{c.Root.Real}'; {free / 1e9:0.0} GB is available. Free space or use --root on another drive.");
        }
    }
}
