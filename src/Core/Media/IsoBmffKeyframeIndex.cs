using System.Buffers.Binary;
using System.Globalization;

namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// The keyframe list of an MP4 / MOV file read from the file's own sample table (<c>moov → trak → stbl</c>: <c>stss</c>, <c>stts</c>,
/// <c>ctts</c>, with the track's <c>mdhd</c> time scale and its edit list), and the bytes the read took (T-194). The times are what
/// <c>ffprobe -show_packets</c> prints as <c>pts_time</c> for the packets it flags <c>K</c>: sorted, distinct, TimeSpan-equal.
/// </summary>
internal sealed record SampleTableKeyframes(IReadOnlyList<TimeSpan> Keyframes, long BytesRead);

/// <summary>
/// A reader of the video keyframes of an ISO base media file (MP4, MOV, M4V, 3GP) from its sample table alone, so a 4K file
/// is ready to cut without reading its media data (T-194). It reads the top-level box headers, seeks over <c>mdat</c> and reads
/// only <c>moov</c>: a few megabytes at the end or the front of a file of gigabytes (measured by <c>bench core 4k kf</c>).
///
/// <para><b>It is a proof, not a guess.</b> Every shape it has not been shown to read exactly as ffmpeg's mov demuxer does is a
/// <i>decline</i> (null): the caller then runs the whole-file packet scan it always ran. A decline is never an error and the
/// reader never throws for a file it cannot use (a truncated or corrupt file, an unreadable or locked one); only a cancellation
/// escapes. What it takes, and why each rule is there, is in SPEC-004 (I41-I44) and ADR 0025.</para>
///
/// <para><b>What it computes.</b> For the FIRST track whose handler is <c>vide</c> (the stream ffprobe's <c>v:0</c> selects, as
/// the mov demuxer creates streams in <c>trak</c> order), in integer ticks of the track's time scale: the presentation time of
/// each sync sample is <c>dts(stts) + cto(ctts) - media_time(elst) + empty edit</c>, printed as ffprobe prints a packet time (a
/// double, six decimals) and parsed by the same function as the packet scan's. An absent <c>stss</c> means every sample is a
/// sync sample, as in the demuxer.</para>
/// </summary>
internal static class IsoBmffKeyframeIndex
{
    /// <summary>A <c>moov</c> box bigger than this declines (it is read whole, into memory).</summary>
    internal const long MaxMoovBytes = 256L * 1024 * 1024;

    /// <summary>More sync samples than this decline: the list is held in memory and handed to every screen.</summary>
    internal const int MaxKeyframes = 4_000_000;

    private static readonly uint Ftyp = Fcc("ftyp");
    private static readonly uint Moov = Fcc("moov");
    private static readonly uint Moof = Fcc("moof");
    private static readonly uint Mvhd = Fcc("mvhd");
    private static readonly uint Mvex = Fcc("mvex");
    private static readonly uint Trak = Fcc("trak");
    private static readonly uint Edts = Fcc("edts");
    private static readonly uint Elst = Fcc("elst");
    private static readonly uint Mdia = Fcc("mdia");
    private static readonly uint Mdhd = Fcc("mdhd");
    private static readonly uint Hdlr = Fcc("hdlr");
    private static readonly uint Minf = Fcc("minf");
    private static readonly uint Stbl = Fcc("stbl");
    private static readonly uint Stsd = Fcc("stsd");
    private static readonly uint Stts = Fcc("stts");
    private static readonly uint Ctts = Fcc("ctts");
    private static readonly uint Cslg = Fcc("cslg");
    private static readonly uint Stss = Fcc("stss");
    private static readonly uint Stps = Fcc("stps");
    private static readonly uint Stsz = Fcc("stsz");
    private static readonly uint Stz2 = Fcc("stz2");
    private static readonly uint Stsc = Fcc("stsc");
    private static readonly uint Stco = Fcc("stco");
    private static readonly uint Co64 = Fcc("co64");
    private static readonly uint Sbgp = Fcc("sbgp");
    private static readonly uint Sgpd = Fcc("sgpd");
    private static readonly uint Vide = Fcc("vide");
    private static readonly uint GroupRap = Fcc("rap ");
    private static readonly uint GroupSync = Fcc("sync");

    // The sample entries of the codecs the reader has been checked against: H.264 and HEVC, with their parameter sets
    // in the sample entry (avc1, hvc1) or in the stream (avc3, hev1).
    private static readonly uint[] SupportedEntries = [Fcc("avc1"), Fcc("avc3"), Fcc("hvc1"), Fcc("hev1")];

    private static readonly byte[] CoverArtTag = "covr"u8.ToArray();

    // The edit list's media rate 1.0 as a 16.16 fixed-point number.
    private const uint RateOne = 0x00010000;

    /// <summary>
    /// Read the keyframes of the file <paramref name="open"/> opens, or null when it cannot prove them (see the type's summary).
    /// <paramref name="open"/> is called once; the stream is disposed here. A cancellation of <paramref name="ct"/> throws
    /// <see cref="OperationCanceledException"/> (it is not a decline).
    /// </summary>
    internal static SampleTableKeyframes? TryRead(Func<Stream> open, CancellationToken ct = default) =>
        TryRead(open, out _, ct);

    /// <inheritdoc cref="TryRead(Func{Stream}, CancellationToken)" />
    /// <param name="open">Opens the file read-only (<c>MediaProbe</c> shares it with writers and deleters).</param>
    /// <param name="declineReason">Why the reader declined, or null when it returned a list. For tests and the bench.</param>
    /// <param name="ct">Cancellation, checked between boxes.</param>
    internal static SampleTableKeyframes? TryRead(Func<Stream> open, out string? declineReason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(open);
        declineReason = null;
        long bytesRead = 0;
        try
        {
            using var stream = open();
            var moov = ReadMoov(stream, out var fileLength, ref bytesRead, ct);
            var keyframes = ReadKeyframes(moov, fileLength, ct);
            return new SampleTableKeyframes(keyframes, bytesRead);
        }
        catch (DeclineException ex)
        {
            declineReason = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ObjectDisposedException or OutOfMemoryException or OverflowException
                                       or ArgumentException or InvalidDataException or IndexOutOfRangeException)
        {
            // An unreadable, locked or vanishing file, an unseekable stream, a box so large it cannot be held, arithmetic that
            // does not fit: none is a scan failure, each hands the file to the packet scan.
            declineReason = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    // ---- The file: top-level boxes --------------------------------------------------------------------------------------

    /// <summary>
    /// Walks the top-level boxes (headers only, <c>mdat</c> and every other box skipped by seeking) and returns the body of the
    /// one <c>moov</c>. Declines a file without <c>ftyp</c> first, a fragment (<c>moof</c>), a second <c>moov</c>, a box that
    /// runs past the end of the file (a truncated file) and an oversized <c>moov</c>.
    /// </summary>
    private static byte[] ReadMoov(Stream stream, out long fileLength, ref long bytesRead, CancellationToken ct)
    {
        fileLength = 0;
        if (!stream.CanSeek)
        {
            throw new DeclineException("stream is not seekable");
        }

        var length = stream.Length;
        fileLength = length;
        var header = new byte[16];
        byte[]? moov = null;
        long position = 0;
        var first = true;

        while (length - position >= 8)
        {
            ct.ThrowIfCancellationRequested();
            stream.Position = position;
            ReadExactly(stream, header, 8, ref bytesRead);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            var headerLength = 8;

            if (first && type != Ftyp)
            {
                throw new DeclineException("no ftyp box first (not an ISO base media file)");
            }

            first = false;

            if (size == 1)
            {
                ReadExactly(stream, header.AsSpan(8, 8), ref bytesRead);
                var large = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (large > long.MaxValue)
                {
                    throw new DeclineException("64-bit box size out of range");
                }

                size = (long)large;
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = length - position; // runs to the end of the file
            }

            if (size < headerLength)
            {
                throw new DeclineException("box smaller than its own header");
            }

            if (size > length - position)
            {
                throw new DeclineException("a box runs past the end of the file (truncated)");
            }

            if (type == Moof)
            {
                throw new DeclineException("fragmented file (moof)");
            }

            if (type == Moov)
            {
                if (moov is not null)
                {
                    throw new DeclineException("more than one moov box");
                }

                var bodyLength = size - headerLength;
                if (bodyLength > MaxMoovBytes)
                {
                    throw new DeclineException("moov larger than " + MaxMoovBytes + " bytes");
                }

                moov = new byte[bodyLength];
                ReadExactly(stream, moov, moov.Length, ref bytesRead);
            }

            position += size;
        }

        return moov ?? throw new DeclineException(first ? "empty file" : "no moov box");
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count, ref long bytesRead) =>
        ReadExactly(stream, buffer.AsSpan(0, count), ref bytesRead);

    private static void ReadExactly(Stream stream, Span<byte> buffer, ref long bytesRead)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n <= 0)
            {
                throw new DeclineException("unexpected end of file");
            }

            total += n;
            bytesRead += n;
        }
    }

    // ---- The movie: which track, which subset ---------------------------------------------------------------------------

    private static IReadOnlyList<TimeSpan> ReadKeyframes(byte[] moov, long fileLength, CancellationToken ct)
    {
        uint movieTimescale = 0;
        Box? videoTrack = null;
        foreach (var box in Children(moov, 0, moov.Length))
        {
            if (box.Type == Mvex)
            {
                throw new DeclineException("fragmented file (mvex)");
            }

            if (box.Type == Mvhd)
            {
                movieTimescale = ReadTimescale(moov, box);
            }
            else if (box.Type == Trak)
            {
                if (videoTrack is null && IsVideoTrack(moov, box))
                {
                    videoTrack = box;
                }
            }
            else if (moov.AsSpan(box.Start, box.End - box.Start).IndexOf(CoverArtTag) >= 0)
            {
                // A cover picture (moov/udta/meta/ilst/covr) becomes a video stream of its own and, placed before the tracks,
                // ffprobe's v:0. Any other box that mentions covr declines, whatever the track order says.
                throw new DeclineException("cover art (covr)");
            }
        }

        if (videoTrack is null)
        {
            throw new DeclineException("no video track");
        }

        ct.ThrowIfCancellationRequested();
        return ReadVideoTrack(moov, fileLength, videoTrack.Value, movieTimescale, ct);
    }

    private static bool IsVideoTrack(byte[] moov, Box trak)
    {
        foreach (var mdia in Children(moov, trak.Start, trak.End))
        {
            if (mdia.Type != Mdia)
            {
                continue;
            }

            foreach (var hdlr in Children(moov, mdia.Start, mdia.End))
            {
                if (hdlr.Type == Hdlr)
                {
                    Require(hdlr, 12, "hdlr");
                    return BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(hdlr.Start + 8)) == Vide;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<TimeSpan> ReadVideoTrack(byte[] moov, long fileLength, Box trak, uint movieTimescale, CancellationToken ct)
    {
        Box? mdhd = null;
        Box? stbl = null;
        Box? elst = null;
        foreach (var child in Children(moov, trak.Start, trak.End))
        {
            if (child.Type == Edts)
            {
                foreach (var e in Children(moov, child.Start, child.End).Where(c => c.Type == Elst))
                {
                    Once(ref elst, e, "elst");
                }
            }
            else if (child.Type == Mdia)
            {
                foreach (var m in Children(moov, child.Start, child.End))
                {
                    if (m.Type == Mdhd)
                    {
                        Once(ref mdhd, m, "mdhd");
                    }
                    else if (m.Type == Minf)
                    {
                        foreach (var s in Children(moov, m.Start, m.End).Where(c => c.Type == Stbl))
                        {
                            Once(ref stbl, s, "stbl");
                        }
                    }
                }
            }
        }

        if (mdhd is null || stbl is null)
        {
            throw new DeclineException("video track without mdhd or stbl");
        }

        var timescale = ReadTimescale(moov, mdhd.Value);
        if (timescale == 0)
        {
            throw new DeclineException("track time scale is zero");
        }

        // ---- the sample table ----
        Box? stsd = null, stts = null, ctts = null, stss = null, stsz = null, chunks = null;
        var chunks64 = false;
        var hasSampleToChunk = false;
        foreach (var box in Children(moov, stbl.Value.Start, stbl.Value.End))
        {
            if (box.Type == Stsd)
            {
                Once(ref stsd, box, "stsd");
            }
            else if (box.Type == Stts)
            {
                Once(ref stts, box, "stts");
            }
            else if (box.Type == Ctts)
            {
                Once(ref ctts, box, "ctts");
            }
            else if (box.Type == Stss)
            {
                Once(ref stss, box, "stss");
            }
            else if (box.Type == Stsz)
            {
                Once(ref stsz, box, "stsz");
            }
            else if (box.Type == Stsc)
            {
                hasSampleToChunk = true;
            }
            else if (box.Type == Stco || box.Type == Co64)
            {
                Once(ref chunks, box, "chunk offset");
                chunks64 = box.Type == Co64;
            }
            else if (box.Type == Stps)
            {
                throw new DeclineException("partial sync samples (stps)");
            }
            else if (box.Type == Stz2)
            {
                throw new DeclineException("compact sample sizes (stz2)");
            }
            else if (box.Type == Cslg)
            {
                throw new DeclineException("composition shift (cslg)");
            }
            else if (box.Type == Sbgp || box.Type == Sgpd)
            {
                // ffmpeg flags the random-access points of an open GOP K, and stss does not list them.
                Require(box, 8, "sample group");
                var grouping = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(box.Start + 4));
                if (grouping == GroupRap || grouping == GroupSync)
                {
                    throw new DeclineException("random-access sample groups (rap / sync)");
                }
            }
        }

        if (stsd is null || stts is null || stsz is null || chunks is null || !hasSampleToChunk)
        {
            throw new DeclineException("incomplete sample table");
        }

        CheckSampleEntry(moov, stsd.Value);
        CheckChunksInsideFile(moov, chunks.Value, chunks64, fileLength);

        // The sample count: stsz, stts and ctts must agree on it (a table that does not is not read).
        Require(stsz.Value, 12, "stsz");
        var sampleCount = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stsz.Value.Start + 8));
        var timeline = new Timeline(moov, stts.Value, ctts);
        if (timeline.SampleCount != sampleCount)
        {
            throw new DeclineException("stts and stsz disagree on the sample count");
        }

        if (timeline.HasCtts && timeline.CttsSampleCount != sampleCount)
        {
            throw new DeclineException("ctts and stsz disagree on the sample count");
        }

        var edit = ReadEditList(moov, elst, movieTimescale, timescale);

        // ---- the sync samples ----
        var syncSamples = ReadSyncSamples(moov, stss, sampleCount);
        if (syncSamples.Count < 2)
        {
            throw new DeclineException("fewer than two sync samples");
        }

        // Their composition times, in decode order.
        var cts = new long[syncSamples.Count];
        var previous = long.MinValue;
        for (var i = 0; i < cts.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            cts[i] = timeline.CtsAt(syncSamples.At(i));
            if (cts[i] <= previous)
            {
                throw new DeclineException("sync samples whose composition times do not increase");
            }

            previous = cts[i];
        }

        // ---- the edit list ----
        var offset = 0L; // what is subtracted from a composition time to get the packet's pts
        var first = 0;   // the first sync sample ffmpeg lists
        if (edit.HasMediaEdit)
        {
            // ffmpeg starts the stream at the first sample whose composition time is at or after the edit's media time, and that
            // sample is at pts 0 (+ the empty edit). A media time that is not exactly the time of a sample (a stream copy cut
            // between two frames writes one) moves the start forward to the next sample: measured against ffprobe on 150 files.
            var start = timeline.MinCtsAtOrAfter(edit.MediaTime)
                        ?? throw new DeclineException("edit list starts after the last sample");
            offset = start;

            // The pre-roll: ffmpeg also lists the last sync sample at or before the media time (the keyframe of the first GOP,
            // flagged discard, its pts negative; the start sample itself when that is a sync sample at the media time) and none
            // of the sync samples before it.
            var before = 0;
            while (before < cts.Length && cts[before] <= edit.MediaTime)
            {
                before++;
            }

            first = Math.Max(0, before - 1);

            // ffmpeg stops a little after the end of the edit, how far is its business: a sync sample that is not inside the edit
            // is not read.
            var mediaEnd = checked(edit.MediaTime + edit.MediaDuration);
            if (cts[^1] >= mediaEnd)
            {
                throw new DeclineException("a sync sample lies at or after the end of the edit list");
            }
        }

        var times = new SortedSet<TimeSpan>();
        var tick = 1.0 / timescale; // ffprobe multiplies by av_q2d(time_base), which is this
        for (var i = first; i < cts.Length; i++)
        {
            var pts = checked(cts[i] - offset + edit.EmptyTicks);
            var text = ((double)pts * tick).ToString("F6", CultureInfo.InvariantCulture);
            if (!MediaProbe.TryParseSeconds(text, out var time))
            {
                throw new DeclineException("a keyframe time does not parse");
            }

            times.Add(time);
        }

        if (times.Count < 2)
        {
            throw new DeclineException("fewer than two keyframes");
        }

        return times.ToList().AsReadOnly();
    }

    private static void CheckSampleEntry(byte[] moov, Box stsd)
    {
        // version/flags (4), entry count (4), then the entries, each a box whose type is the codec.
        Require(stsd, 16, "stsd");
        var count = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stsd.Start + 4));
        if (count != 1)
        {
            throw new DeclineException("stsd holds " + count + " sample entries, not one");
        }

        var entryType = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stsd.Start + 12));
        if (Array.IndexOf(SupportedEntries, entryType) < 0)
        {
            throw new DeclineException("sample entry '" + FccText(entryType) + "' is not avc1, avc3, hvc1 or hev1");
        }
    }

    /// <summary>
    /// Every chunk of media data starts inside the file. The sample table of a truncated file (a download that stopped, a copy
    /// that ran out of space) still lists samples whose bytes are not there, and ffprobe lists only the packets it can read:
    /// the box sizes catch a cut inside a box, this catches a cut between boxes (<c>moov</c> first, <c>mdat</c> missing).
    /// <c>stco</c> holds 32-bit offsets, <c>co64</c> 64-bit ones.
    /// </summary>
    private static void CheckChunksInsideFile(byte[] moov, Box chunks, bool is64, long fileLength)
    {
        Require(chunks, 8, "chunk offset");
        var count = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(chunks.Start + 4));
        var width = is64 ? 8 : 4;
        if (count == 0 || 8L + (width * (long)count) > chunks.End - chunks.Start)
        {
            throw new DeclineException("chunk offset table is empty or too long for its box");
        }

        for (var i = 0; i < count; i++)
        {
            var at = chunks.Start + 8 + (width * i);
            var offset = is64
                ? BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(at))
                : BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at));
            if (offset >= (ulong)fileLength)
            {
                throw new DeclineException("sample data lies beyond the end of the file (truncated)");
            }
        }
    }

    /// <summary>The sync sample numbers (zero-based): the <c>stss</c> list, or every sample when there is none.</summary>
    private static SyncSamples ReadSyncSamples(byte[] moov, Box? stss, uint sampleCount)
    {
        if (stss is null)
        {
            if (sampleCount > MaxKeyframes)
            {
                throw new DeclineException("more than " + MaxKeyframes + " sync samples");
            }

            return new SyncSamples(null, 0, (int)sampleCount);
        }

        Require(stss.Value, 8, "stss");
        var count = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stss.Value.Start + 4));
        if (count > MaxKeyframes || 8L + (4L * count) > stss.Value.End - stss.Value.Start)
        {
            throw new DeclineException("stss is too long for its box, or longer than " + MaxKeyframes);
        }

        var first = stss.Value.Start + 8;
        uint previous = 0;
        for (var i = 0; i < count; i++)
        {
            // Sample numbers count from 1; the demuxer matches them in order, so a list that is not strictly ascending, or that
            // names a sample the track does not have, is not read.
            var number = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(first + (4 * i)));
            if (number <= previous || number > sampleCount)
            {
                throw new DeclineException("stss is not strictly ascending within the sample count");
            }

            previous = number;
        }

        return new SyncSamples(moov, first, (int)count);
    }

    // ---- The edit list --------------------------------------------------------------------------------------------------

    private readonly record struct Edit(bool HasMediaEdit, long MediaTime, long MediaDuration, long EmptyTicks);

    /// <summary>
    /// No edit list; or one media edit at rate 1 with the media time of a sample and a duration; or that behind ONE leading empty
    /// edit (a video that starts late). Everything else declines. <c>MediaDuration</c> and <c>EmptyTicks</c> are in track ticks:
    /// the demuxer rescales a movie-time duration with rounding to nearest.
    /// </summary>
    private static Edit ReadEditList(byte[] moov, Box? elst, uint movieTimescale, uint timescale)
    {
        if (elst is null)
        {
            return new Edit(false, 0, 0, 0);
        }

        Require(elst.Value, 8, "elst");
        var version = moov[elst.Value.Start];
        if (version > 1)
        {
            throw new DeclineException("edit list version " + version);
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(elst.Value.Start + 4));
        var entrySize = version == 1 ? 20 : 12;
        if (count is 0 or > 2 || 8L + (entrySize * (long)count) > elst.Value.End - elst.Value.Start)
        {
            throw new DeclineException("edit list with " + count + " entries");
        }

        if (movieTimescale == 0)
        {
            throw new DeclineException("movie time scale is zero");
        }

        var entries = new (ulong Duration, long MediaTime, uint Rate)[count];
        for (var i = 0; i < count; i++)
        {
            var p = moov.AsSpan(elst.Value.Start + 8 + (i * entrySize));
            entries[i] = version == 1
                ? (BinaryPrimitives.ReadUInt64BigEndian(p), BinaryPrimitives.ReadInt64BigEndian(p[8..]), BinaryPrimitives.ReadUInt32BigEndian(p[16..]))
                : (BinaryPrimitives.ReadUInt32BigEndian(p), BinaryPrimitives.ReadInt32BigEndian(p[4..]), BinaryPrimitives.ReadUInt32BigEndian(p[8..]));
        }

        var emptyTicks = 0L;
        var mediaIndex = 0;
        if (entries[0].MediaTime == -1)
        {
            if (count != 2)
            {
                throw new DeclineException("edit list holds an empty edit and nothing else");
            }

            emptyTicks = Rescale(entries[0].Duration, timescale, movieTimescale);
            mediaIndex = 1;
        }
        else if (count != 1)
        {
            throw new DeclineException("more than one media edit");
        }

        var media = entries[mediaIndex];
        if (media.MediaTime < 0)
        {
            throw new DeclineException("edit list media time " + media.MediaTime);
        }

        if (media.Rate != RateOne)
        {
            throw new DeclineException("edit list media rate is not 1");
        }

        if (media.Duration == 0)
        {
            throw new DeclineException("edit list media edit with no duration");
        }

        return new Edit(true, media.MediaTime, Rescale(media.Duration, timescale, movieTimescale), emptyTicks);
    }

    /// <summary><c>av_rescale</c>: <c>value * numerator / denominator</c> rounded to nearest, halves away from zero.</summary>
    private static long Rescale(ulong value, uint numerator, uint denominator)
    {
        var product = (Int128)value * numerator;
        var rescaled = (product + (denominator / 2)) / denominator;
        return checked((long)rescaled);
    }

    // ---- The sample times -----------------------------------------------------------------------------------------------

    /// <summary>The sync sample numbers: an <c>stss</c> table (in the movie bytes), or 0..count-1.</summary>
    private readonly struct SyncSamples
    {
        private readonly byte[]? _data;
        private readonly int _first;

        internal SyncSamples(byte[]? data, int first, int count)
        {
            _data = data;
            _first = first;
            Count = count;
        }

        internal int Count { get; }

        internal long At(int index) =>
            _data is null ? index : BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_first + (4 * index))) - 1L;
    }

    /// <summary>
    /// The decode time (<c>stts</c>) plus the composition offset (<c>ctts</c>) of a sample, for sample numbers that only grow, and
    /// the question "has any sample this composition time". It never builds a per-sample array: a long recording has millions of
    /// samples and a few hundred runs.
    /// </summary>
    private sealed class Timeline
    {
        private readonly byte[] _data;
        private readonly int _sttsFirst;
        private readonly uint _sttsRuns;
        private readonly int _cttsFirst;
        private readonly uint _cttsRuns;

        // The cursor for CtsAt: the current stts run (its first sample, the decode time of that sample) and ctts run.
        private uint _sttsRun;
        private long _sttsRunFirstSample;
        private long _sttsRunDts;
        private uint _cttsRun;
        private long _cttsRunFirstSample;

        internal Timeline(byte[] data, Box stts, Box? ctts)
        {
            _data = data;

            Require(stts, 8, "stts");
            _sttsRuns = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(stts.Start + 4));
            if (8L + (8L * _sttsRuns) > stts.End - stts.Start)
            {
                throw new DeclineException("stts is too long for its box");
            }

            _sttsFirst = stts.Start + 8;
            for (var i = 0u; i < _sttsRuns; i++)
            {
                SampleCount += SttsCount(i);
            }

            if (ctts is { } c)
            {
                Require(c, 8, "ctts");
                if (data[c.Start] > 1)
                {
                    throw new DeclineException("ctts version " + data[c.Start]);
                }

                _cttsRuns = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(c.Start + 4));
                if (8L + (8L * _cttsRuns) > c.End - c.Start)
                {
                    throw new DeclineException("ctts is too long for its box");
                }

                _cttsFirst = c.Start + 8;
                HasCtts = true;
                for (var i = 0u; i < _cttsRuns; i++)
                {
                    CttsSampleCount += CttsCount(i);

                    // Version 0 offsets are unsigned and version 1 signed; ffmpeg reads both as signed. A negative one (also a
                    // version 0 value with the high bit set) makes the demuxer shift the decode times: not read.
                    if (CttsOffsetRaw(i) < 0)
                    {
                        throw new DeclineException("negative composition offset");
                    }
                }
            }
        }

        internal long SampleCount { get; }

        internal bool HasCtts { get; }

        internal long CttsSampleCount { get; }

        private long SttsCount(uint run) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_sttsFirst + (8 * (int)run)));

        private long SttsDelta(uint run) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_sttsFirst + (8 * (int)run) + 4));

        private long CttsCount(uint run) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_cttsFirst + (8 * (int)run)));

        private long CttsOffsetRaw(uint run) => BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(_cttsFirst + (8 * (int)run) + 4));

        /// <summary>The composition time of <paramref name="sample"/> (zero-based), in ticks. Sample numbers must not decrease.</summary>
        internal long CtsAt(long sample)
        {
            while (sample >= _sttsRunFirstSample + SttsCount(_sttsRun))
            {
                if (_sttsRun + 1 >= _sttsRuns)
                {
                    throw new DeclineException("sample beyond the stts table");
                }

                _sttsRunDts = checked(_sttsRunDts + (SttsCount(_sttsRun) * SttsDelta(_sttsRun)));
                _sttsRunFirstSample += SttsCount(_sttsRun);
                _sttsRun++;
            }

            var dts = checked(_sttsRunDts + ((sample - _sttsRunFirstSample) * SttsDelta(_sttsRun)));
            if (!HasCtts)
            {
                return dts;
            }

            while (sample >= _cttsRunFirstSample + CttsCount(_cttsRun))
            {
                if (_cttsRun + 1 >= _cttsRuns)
                {
                    throw new DeclineException("sample beyond the ctts table");
                }

                _cttsRunFirstSample += CttsCount(_cttsRun);
                _cttsRun++;
            }

            return checked(dts + CttsOffsetRaw(_cttsRun));
        }

        /// <summary>
        /// The smallest composition time among ALL the samples of the track (not only the sync samples) that is at or after
        /// <paramref name="target"/>; null when there is none.
        /// </summary>
        internal long? MinCtsAtOrAfter(long target)
        {
            // Walk the stts and ctts runs together: each stretch of samples has one decode delta and one offset, so its
            // composition times are an arithmetic progression and the smallest one at or after the target is a division.
            long sample = 0;
            long dts = 0;
            uint sttsRun = 0;
            uint cttsRun = 0;
            long sttsLeft = _sttsRuns > 0 ? SttsCount(0) : 0;
            long cttsLeft = HasCtts && _cttsRuns > 0 ? CttsCount(0) : long.MaxValue;
            long? best = null;
            while (sample < SampleCount)
            {
                if (sttsLeft == 0)
                {
                    sttsRun++;
                    sttsLeft = SttsCount(sttsRun);
                    continue;
                }

                if (cttsLeft == 0)
                {
                    cttsRun++;
                    cttsLeft = CttsCount(cttsRun);
                    continue;
                }

                var stretch = Math.Min(sttsLeft, cttsLeft);
                var delta = SttsDelta(sttsRun);
                var offset = HasCtts ? CttsOffsetRaw(cttsRun) : 0;
                var firstCts = checked(dts + offset);
                long? candidate = null;
                if (firstCts >= target)
                {
                    candidate = firstCts;
                }
                else if (delta > 0)
                {
                    var steps = (target - firstCts + delta - 1) / delta;
                    if (steps < stretch)
                    {
                        candidate = checked(firstCts + (steps * delta));
                    }
                }

                if (candidate is { } c && (best is null || c < best))
                {
                    best = c;
                }

                dts = checked(dts + (stretch * delta));
                sample += stretch;
                sttsLeft -= stretch;
                if (cttsLeft != long.MaxValue)
                {
                    cttsLeft -= stretch;
                }
            }

            return best;
        }
    }

    // ---- Boxes -----------------------------------------------------------------------------------------------------------

    /// <summary>A box inside a byte array: its type and the extent of its body (after its header).</summary>
    private readonly record struct Box(uint Type, int Start, int End);

    /// <summary>The boxes tiled in <c>data[start..end]</c>. A size that runs past the end, or smaller than a header, declines.</summary>
    private static List<Box> Children(byte[] data, int start, int end)
    {
        var boxes = new List<Box>();
        var position = start;
        while (end - position >= 8)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            var type = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 4));
            var headerLength = 8;
            if (size == 1)
            {
                if (end - position < 16)
                {
                    throw new DeclineException("a 64-bit box size runs past its parent");
                }

                var large = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position + 8));
                if (large > int.MaxValue)
                {
                    throw new DeclineException("a box inside moov is larger than 2 GB");
                }

                size = (long)large;
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = end - position; // runs to the end of its parent
            }

            if (size < headerLength || size > end - position)
            {
                throw new DeclineException("a box size overruns its parent or is smaller than its header");
            }

            boxes.Add(new Box(type, position + headerLength, position + (int)size));
            position += (int)size;
        }

        return boxes;
    }

    /// <summary>Remember the first and only box of a kind; a second one is a shape the reader has not seen.</summary>
    private static void Once(ref Box? slot, Box box, string name)
    {
        if (slot is not null)
        {
            throw new DeclineException("more than one " + name + " box");
        }

        slot = box;
    }

    private static void Require(Box box, int minimumBodyLength, string name)
    {
        if (box.End - box.Start < minimumBodyLength)
        {
            throw new DeclineException(name + " box is too short");
        }
    }

    /// <summary>The time scale of an <c>mvhd</c> or <c>mdhd</c> (version 0: four bytes at 12; version 1: eight-byte times, at 20).</summary>
    private static uint ReadTimescale(byte[] data, Box box)
    {
        Require(box, 4, "mvhd/mdhd");
        var version = data[box.Start];
        if (version > 1)
        {
            throw new DeclineException("mvhd/mdhd version " + version);
        }

        var offset = version == 1 ? 20 : 12;
        Require(box, offset + 4, "mvhd/mdhd");
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.Start + offset));
    }

    private static uint Fcc(string text) =>
        BinaryPrimitives.ReadUInt32BigEndian([(byte)text[0], (byte)text[1], (byte)text[2], (byte)text[3]]);

    private static string FccText(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        var chars = new char[4];
        for (var i = 0; i < 4; i++)
        {
            chars[i] = bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : '?';
        }

        return new string(chars);
    }

    /// <summary>A shape the reader does not read: the caller falls back to the packet scan.</summary>
    private sealed class DeclineException : Exception
    {
        internal DeclineException(string reason)
            : base(reason)
        {
        }
    }
}
