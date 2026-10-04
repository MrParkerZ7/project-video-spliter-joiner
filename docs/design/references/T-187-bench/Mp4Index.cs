using System.Buffers.Binary;

namespace T187Bench;

/// <summary>
/// Prototype: read the video keyframe times of an ISO-BMFF (mp4/mov) file from its sample index ONLY
/// (moov → video trak → stts/ctts/stss/elst), without reading any media data. Used to measure what an index-based
/// keyframe scan would cost versus the app's whole-file ffprobe -show_packets scan, and to check its list is identical.
/// Handles the common shapes (32/64-bit boxes, moov at front or end, one edit with media_time, optional leading empty edit).
/// Not production code.
/// </summary>
internal static class Mp4Index
{
    public sealed record Result(IReadOnlyList<double> KeyframeSeconds, long BytesRead);

    public static Result? ReadKeyframes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        long bytesRead = 0;
        var len = fs.Length;
        long pos = 0;
        byte[]? moov = null;
        var hdr = new byte[16];
        while (pos + 8 <= len)
        {
            fs.Position = pos;
            fs.ReadExactly(hdr, 0, 8);
            bytesRead += 8;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var type = System.Text.Encoding.ASCII.GetString(hdr, 4, 4);
            var headerLen = 8;
            if (size == 1)
            {
                fs.ReadExactly(hdr, 8, 8);
                bytesRead += 8;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8));
                headerLen = 16;
            }
            else if (size == 0)
            {
                size = len - pos;
            }

            if (type == "moov")
            {
                moov = new byte[size - headerLen];
                fs.ReadExactly(moov);
                bytesRead += moov.Length;
                break;
            }

            pos += size;
        }

        if (moov is null)
        {
            return null;
        }

        uint movieTimescale = 1000;
        foreach (var (t, body) in Children(moov))
        {
            if (t == "mvhd")
            {
                movieTimescale = body[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(20)) : BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12));
            }
        }

        foreach (var (t, trak) in Children(moov))
        {
            if (t != "trak")
            {
                continue;
            }

            var r = TryTrack(trak, movieTimescale);
            if (r is not null)
            {
                return new Result(r, bytesRead);
            }
        }

        return null;
    }

    private static List<double>? TryTrack(ReadOnlySpan<byte> trak, uint movieTimescale)
    {
        ReadOnlySpan<byte> mdia = default, elst = default;
        foreach (var (t, b) in Children(trak))
        {
            if (t == "mdia")
            {
                mdia = b;
            }
            else if (t == "edts")
            {
                foreach (var (t2, b2) in Children(b))
                {
                    if (t2 == "elst")
                    {
                        elst = b2;
                    }
                }
            }
        }

        if (mdia.IsEmpty)
        {
            return null;
        }

        string? handler = null;
        uint timescale = 0;
        ReadOnlySpan<byte> stbl = default;
        foreach (var (t, b) in Children(mdia))
        {
            if (t == "hdlr")
            {
                handler = System.Text.Encoding.ASCII.GetString(b.AsSpan(8, 4));
            }
            else if (t == "mdhd")
            {
                timescale = b[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12));
            }
            else if (t == "minf")
            {
                foreach (var (t2, b2) in Children(b))
                {
                    if (t2 == "stbl")
                    {
                        stbl = b2;
                    }
                }
            }
        }

        if (handler != "vide" || timescale == 0 || stbl.IsEmpty)
        {
            return null;
        }

        ReadOnlySpan<byte> stts = default, ctts = default, stss = default;
        foreach (var (t, b) in Children(stbl))
        {
            switch (t)
            {
                case "stts": stts = b; break;
                case "ctts": ctts = b; break;
                case "stss": stss = b; break;
            }
        }

        // DTS per sample from stts.
        var dts = new List<long>();
        long cur = 0;
        var n = BinaryPrimitives.ReadUInt32BigEndian(stts.Slice(4));
        for (var i = 0; i < n; i++)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(stts.Slice(8 + (i * 8)));
            var delta = BinaryPrimitives.ReadUInt32BigEndian(stts.Slice(12 + (i * 8)));
            for (var k = 0; k < count; k++)
            {
                dts.Add(cur);
                cur += delta;
            }
        }

        var cto = new long[dts.Count];
        if (!ctts.IsEmpty)
        {
            var version = ctts[0];
            var cn = BinaryPrimitives.ReadUInt32BigEndian(ctts.Slice(4));
            var idx = 0;
            for (var i = 0; i < cn; i++)
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(ctts.Slice(8 + (i * 8)));
                long off = version == 1
                    ? BinaryPrimitives.ReadInt32BigEndian(ctts.Slice(12 + (i * 8)))
                    : BinaryPrimitives.ReadUInt32BigEndian(ctts.Slice(12 + (i * 8)));
                for (var k = 0; k < count && idx < cto.Length; k++)
                {
                    cto[idx++] = off;
                }
            }
        }

        // Edit list: a leading empty edit delays the track; the first real edit's media_time is subtracted.
        double emptyDelaySec = 0;
        long mediaTime = 0;
        if (!elst.IsEmpty)
        {
            var version = elst[0];
            var en = BinaryPrimitives.ReadUInt32BigEndian(elst.Slice(4));
            var p = 8;
            for (var i = 0; i < en; i++)
            {
                long segDur, mt;
                if (version == 1)
                {
                    segDur = (long)BinaryPrimitives.ReadUInt64BigEndian(elst.Slice(p));
                    mt = BinaryPrimitives.ReadInt64BigEndian(elst.Slice(p + 8));
                    p += 20;
                }
                else
                {
                    segDur = BinaryPrimitives.ReadUInt32BigEndian(elst.Slice(p));
                    mt = BinaryPrimitives.ReadInt32BigEndian(elst.Slice(p + 4));
                    p += 12;
                }

                if (mt == -1)
                {
                    emptyDelaySec += (double)segDur / movieTimescale;
                    continue;
                }

                mediaTime = mt;
                break;
            }
        }

        IEnumerable<int> syncSamples;
        if (stss.IsEmpty)
        {
            syncSamples = Enumerable.Range(0, dts.Count);
        }
        else
        {
            var sn = BinaryPrimitives.ReadUInt32BigEndian(stss.Slice(4));
            var list = new List<int>((int)sn);
            for (var i = 0; i < sn; i++)
            {
                list.Add((int)BinaryPrimitives.ReadUInt32BigEndian(stss.Slice(8 + (i * 4))) - 1);
            }

            syncSamples = list;
        }

        return syncSamples
            .Where(i => i >= 0 && i < dts.Count)
            .Select(i => ((double)(dts[i] + cto[i] - mediaTime) / timescale) + emptyDelaySec)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    private static List<(string Type, byte[] Body)> ChildrenList(ReadOnlySpan<byte> data)
    {
        var res = new List<(string, byte[])>();
        var p = 0;
        while (p + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(p));
            var type = System.Text.Encoding.ASCII.GetString(data.Slice(p + 4, 4));
            var hl = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(p + 8));
                hl = 16;
            }
            else if (size == 0)
            {
                size = data.Length - p;
            }

            if (size < hl || p + size > data.Length)
            {
                break;
            }

            res.Add((type, data.Slice(p + hl, (int)size - hl).ToArray()));
            p += (int)size;
        }

        return res;
    }

    private static List<(string Type, byte[] Body)> Children(ReadOnlySpan<byte> data) => ChildrenList(data);
}
