using System.Buffers.Binary;
using System.Text;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Builds ISO base media files out of crafted box trees (T-194): no ffmpeg, every byte chosen by the test, so a decline rule is
/// tested with the one shape that triggers it. The defaults are one H.264 video track of 90 samples at 30 fps (time scale
/// 30000, 1000 ticks a sample) with sync samples 1, 31 and 61, so its keyframes are at 0, 1 and 2 seconds.
/// </summary>
internal sealed class Mp4Builder
{
    public uint MovieTimescale { get; set; } = 1000;

    public List<Mp4Track> Tracks { get; } = new() { new Mp4Track() };

    /// <summary>Extra boxes appended to <c>moov</c> after the tracks (<c>mvex</c>, <c>udta</c> ...).</summary>
    public List<byte[]> MoovExtras { get; } = new();

    /// <summary>Extra boxes placed in <c>moov</c> BEFORE the tracks (a <c>udta</c> with cover art ...).</summary>
    public List<byte[]> MoovPrefix { get; } = new();

    /// <summary>Boxes placed after <c>moov</c> and before the end of the file (<c>moof</c> ...).</summary>
    public List<byte[]> TopLevelAfterMoov { get; } = new();

    /// <summary>Size of the media data box body.</summary>
    public int MdatBodyBytes { get; set; } = 1000;

    /// <summary>True: <c>moov</c> before <c>mdat</c> (faststart); false: after (the default).</summary>
    public bool MoovFirst { get; set; }

    /// <summary>Write <c>ftyp</c> first.</summary>
    public bool WithFtyp { get; set; } = true;

    /// <summary>Write the <c>mdat</c> and <c>moov</c> headers with 64-bit sizes (<c>size == 1</c>, then the size as 8 bytes).</summary>
    public bool Use64BitTopLevelSizes { get; set; }

    /// <summary>Write the last top-level box with size 0: it runs to the end of the file.</summary>
    public bool LastBoxRunsToEnd { get; set; }

    public byte[] Build()
    {
        var ftyp = Box("ftyp", Concat(Ascii("isom"), U32(512), Ascii("isom"), Ascii("avc1")));
        var mdat = Use64BitTopLevelSizes ? Box64("mdat", new byte[MdatBodyBytes]) : Box("mdat", new byte[MdatBodyBytes]);

        // The chunk offset every track points at is the start of the mdat body; the moov's size does not depend on it.
        var ftypLength = WithFtyp ? ftyp.Length : 0;
        var mdatHeader = Use64BitTopLevelSizes ? 16 : 8;
        var moovLength = (Use64BitTopLevelSizes ? 16 : 8) + MoovContent(0).Length;
        var mdatBodyStart = MoovFirst ? ftypLength + moovLength + mdatHeader : ftypLength + mdatHeader;
        var moovContent = MoovContent((ulong)mdatBodyStart);

        var parts = new List<byte[]>();
        if (WithFtyp)
        {
            parts.Add(ftyp);
        }

        var moov = Use64BitTopLevelSizes ? Box64("moov", moovContent) : Box("moov", moovContent);
        if (LastBoxRunsToEnd && TopLevelAfterMoov.Count == 0 && !MoovFirst)
        {
            moov = BoxToEnd("moov", moovContent);
        }

        if (MoovFirst)
        {
            parts.Add(moov);
            parts.Add(mdat);
        }
        else
        {
            parts.Add(mdat);
            parts.Add(moov);
        }

        parts.AddRange(TopLevelAfterMoov);
        return Concat(parts.ToArray());
    }

    /// <summary>Just the <c>moov</c> box (32-bit size), for tests that assemble a file by hand.</summary>
    public byte[] BuildMoov() => Box("moov", MoovContent(48));

    private byte[] MoovContent(ulong chunkOffset)
    {
        var moovBody = new List<byte[]> { MovieHeader() };
        moovBody.AddRange(MoovPrefix);
        for (var i = 0; i < Tracks.Count; i++)
        {
            moovBody.Add(Tracks[i].Build(i + 1, MovieTimescale, chunkOffset));
        }

        moovBody.AddRange(MoovExtras);
        return Concat(moovBody.ToArray());
    }

    private byte[] MovieHeader()
    {
        var body = new List<byte[]> { U32(0), U32(0), U32(0), U32(MovieTimescale), U32(90000), U32(0x00010000), new byte[70] };
        return Box("mvhd", Concat(body.ToArray()));
    }

    // ---- box helpers -----------------------------------------------------------------------------------------------

    public static byte[] Box(string type, byte[] body) => Concat(U32((uint)(body.Length + 8)), Ascii(type), body);

    /// <summary>A box with a 64-bit size: size field 1, the type, then the real size as eight bytes.</summary>
    public static byte[] Box64(string type, byte[] body) => Concat(U32(1), Ascii(type), U64((ulong)body.Length + 16), body);

    /// <summary>A box whose size field is 0: it runs to the end of its parent (or of the file).</summary>
    public static byte[] BoxToEnd(string type, byte[] body) => Concat(U32(0), Ascii(type), body);

    public static byte[] Box(string type, params byte[][] children) => Box(type, Concat(children));

    /// <summary>A full box: version, 24-bit flags, then the body.</summary>
    public static byte[] FullBox(string type, byte version, uint flags, byte[] body) =>
        Box(type, Concat(new[] { version }, U24(flags), body));

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    public static byte[] U16(ushort v) => new[] { (byte)(v >> 8), (byte)v };

    public static byte[] U24(uint v) => new[] { (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    public static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    public static byte[] I32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        return b;
    }

    public static byte[] U64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }

    public static byte[] I64(long v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        return b;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, all, at, p.Length);
            at += p.Length;
        }

        return all;
    }
}

/// <summary>One <c>trak</c> of an <see cref="Mp4Builder"/>: the knobs are the boxes of its sample table.</summary>
internal sealed class Mp4Track
{
    public string Handler { get; set; } = "vide";

    /// <summary>The sample entry codec: <c>avc1</c>, <c>hvc1</c>, <c>mp4v</c> ...</summary>
    public string Entry { get; set; } = "avc1";

    public int EntryCount { get; set; } = 1;

    public uint Timescale { get; set; } = 30000;

    /// <summary>The <c>mdhd</c> version (1 writes 64-bit times).</summary>
    public byte MdhdVersion { get; set; }

    public (uint Count, uint Delta)[] Stts { get; set; } = { (90, 1000) };

    /// <summary>The composition offsets as (count, offset) runs, or null for no <c>ctts</c>.</summary>
    public (uint Count, int Offset)[]? Ctts { get; set; }

    public byte CttsVersion { get; set; }

    /// <summary>The sync sample numbers (1-based), or null for no <c>stss</c>.</summary>
    public uint[]? Stss { get; set; } = { 1, 31, 61 };

    /// <summary>The sample count in <c>stsz</c>; null: the sum of the <c>stts</c> counts.</summary>
    public uint? SampleCount { get; set; }

    /// <summary>The edit list as (duration in movie ticks, media time, media rate 16.16), or null for no <c>edts</c>.</summary>
    public (ulong Duration, long MediaTime, uint Rate)[]? Edits { get; set; }

    public byte ElstVersion { get; set; }

    public bool WithStsc { get; set; } = true;

    public bool WithStco { get; set; } = true;

    /// <summary>Write the chunk offsets as <c>co64</c> instead of <c>stco</c>.</summary>
    public bool UseCo64 { get; set; }

    /// <summary>Extra boxes appended to <c>stbl</c> (<c>stps</c>, <c>sgpd</c>, <c>cslg</c> ...).</summary>
    public List<byte[]> StblExtras { get; } = new();

    /// <summary>Extra boxes appended to <c>trak</c>.</summary>
    public List<byte[]> TrakExtras { get; } = new();

    /// <summary>Extra bytes appended to <c>stsz</c>'s body: a long table, to make the moov big.</summary>
    public int StszPaddingBytes { get; set; }

    public bool WithStsz { get; set; } = true;

    /// <summary>The chunk offset written to the track's single chunk; null: the start of the media data (the builder's choice).</summary>
    public ulong? ChunkOffsetOverride { get; set; }

    public byte[] Build(int trackId, uint movieTimescale, ulong chunkOffset)
    {
        _ = trackId;
        _ = movieTimescale;
        chunkOffset = ChunkOffsetOverride ?? chunkOffset;
        var total = (uint)Stts.Sum(s => (long)s.Count);
        var mdhd = MdhdVersion == 1
            ? Mp4Builder.FullBox("mdhd", 1, 0, Mp4Builder.Concat(Mp4Builder.U64(0), Mp4Builder.U64(0), Mp4Builder.U32(Timescale), Mp4Builder.U64(total), Mp4Builder.U32(0)))
            : Mp4Builder.FullBox("mdhd", 0, 0, Mp4Builder.Concat(Mp4Builder.U32(0), Mp4Builder.U32(0), Mp4Builder.U32(Timescale), Mp4Builder.U32(total), Mp4Builder.U32(0)));
        var hdlr = Mp4Builder.FullBox("hdlr", 0, 0, Mp4Builder.Concat(Mp4Builder.U32(0), Mp4Builder.Ascii(Handler), new byte[12], new byte[] { 0 }));

        var stbl = new List<byte[]> { StsdBox() };
        stbl.Add(Mp4Builder.FullBox("stts", 0, 0, Mp4Builder.Concat(Mp4Builder.U32((uint)Stts.Length), Mp4Builder.Concat(Stts.Select(s => Mp4Builder.Concat(Mp4Builder.U32(s.Count), Mp4Builder.U32(s.Delta))).ToArray()))));
        if (Ctts is not null)
        {
            stbl.Add(Mp4Builder.FullBox("ctts", CttsVersion, 0, Mp4Builder.Concat(Mp4Builder.U32((uint)Ctts.Length), Mp4Builder.Concat(Ctts.Select(c => Mp4Builder.Concat(Mp4Builder.U32(c.Count), Mp4Builder.I32(c.Offset))).ToArray()))));
        }

        if (Stss is not null)
        {
            stbl.Add(Mp4Builder.FullBox("stss", 0, 0, Mp4Builder.Concat(Mp4Builder.U32((uint)Stss.Length), Mp4Builder.Concat(Stss.Select(Mp4Builder.U32).ToArray()))));
        }

        if (WithStsz)
        {
            stbl.Add(Mp4Builder.FullBox("stsz", 0, 0, Mp4Builder.Concat(Mp4Builder.U32(0), Mp4Builder.U32(SampleCount ?? total), new byte[StszPaddingBytes])));
        }

        if (WithStsc)
        {
            stbl.Add(Mp4Builder.FullBox("stsc", 0, 0, Mp4Builder.Concat(Mp4Builder.U32(1), Mp4Builder.U32(1), Mp4Builder.U32(total), Mp4Builder.U32(1))));
        }

        if (WithStco)
        {
            stbl.Add(UseCo64
                ? Mp4Builder.FullBox("co64", 0, 0, Mp4Builder.Concat(Mp4Builder.U32(1), Mp4Builder.U64(chunkOffset)))
                : Mp4Builder.FullBox("stco", 0, 0, Mp4Builder.Concat(Mp4Builder.U32(1), Mp4Builder.U32((uint)chunkOffset))));
        }

        stbl.AddRange(StblExtras);

        var mdiaBody = new List<byte[]> { mdhd, hdlr, Mp4Builder.Box("minf", Mp4Builder.Box("stbl", Mp4Builder.Concat(stbl.ToArray()))) };
        var trak = new List<byte[]> { Mp4Builder.FullBox("tkhd", 0, 3, new byte[80]) };
        if (Edits is not null)
        {
            trak.Add(Mp4Builder.Box("edts", EditListBox()));
        }

        trak.Add(Mp4Builder.Box("mdia", Mp4Builder.Concat(mdiaBody.ToArray())));
        trak.AddRange(TrakExtras);
        return Mp4Builder.Box("trak", Mp4Builder.Concat(trak.ToArray()));
    }

    private byte[] StsdBox()
    {
        var entry = Mp4Builder.Box(Entry, new byte[78]);
        var entries = Enumerable.Repeat(entry, EntryCount).ToArray();
        return Mp4Builder.FullBox("stsd", 0, 0, Mp4Builder.Concat(Mp4Builder.U32((uint)EntryCount), Mp4Builder.Concat(entries)));
    }

    private byte[] EditListBox()
    {
        var edits = Edits!;
        var body = ElstVersion == 1
            ? edits.Select(e => Mp4Builder.Concat(Mp4Builder.U64(e.Duration), Mp4Builder.I64(e.MediaTime), Mp4Builder.U32(e.Rate)))
            : edits.Select(e => Mp4Builder.Concat(Mp4Builder.U32((uint)e.Duration), Mp4Builder.I32((int)e.MediaTime), Mp4Builder.U32(e.Rate)));
        return Mp4Builder.FullBox("elst", ElstVersion, 0, Mp4Builder.Concat(Mp4Builder.U32((uint)edits.Length), Mp4Builder.Concat(body.ToArray())));
    }
}

/// <summary>
/// A read-only, seekable stream over segments (byte arrays and runs of zeros), so a test can present a file of hundreds of
/// megabytes without allocating it. It counts the bytes read.
/// </summary>
internal sealed class VirtualFileStream : Stream
{
    private readonly List<(long Start, long Length, byte[]? Bytes)> _segments = new();
    private long _length;
    private long _position;

    public long BytesRead { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Called with every read's (position, count) before it is served; a test hook (cancel, throw).</summary>
    public Action<long, int>? OnRead { get; set; }

    public VirtualFileStream Add(byte[] bytes)
    {
        _segments.Add((_length, bytes.Length, bytes));
        _length += bytes.Length;
        return this;
    }

    public VirtualFileStream AddZeros(long count)
    {
        _segments.Add((_length, count, null));
        _length += count;
        return this;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        OnRead?.Invoke(_position, buffer.Length);
        var copied = 0;
        while (copied < buffer.Length && _position < _length)
        {
            var segment = _segments.First(s => _position >= s.Start && _position < s.Start + s.Length);
            var within = _position - segment.Start;
            var n = (int)Math.Min(buffer.Length - copied, segment.Length - within);
            if (segment.Bytes is null)
            {
                buffer.Slice(copied, n).Clear();
            }
            else
            {
                segment.Bytes.AsSpan((int)within, n).CopyTo(buffer[copied..]);
            }

            copied += n;
            _position += n;
        }

        BytesRead += copied;
        return copied;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>Wraps a stream and counts the bytes it hands out.</summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;

    public CountingStream(Stream inner)
    {
        _inner = inner;
    }

    public long BytesRead { get; private set; }

    public bool Disposed { get; private set; }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        BytesRead += n;
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        BytesRead += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
