namespace VideoSplitJoiner.Core.Media;

/// <summary>What the first picture of an H.264 packet is (<see cref="H264Packet.FirstPictureKind"/>).</summary>
internal enum H264PictureKind
{
    /// <summary>The packet could not be read as H.264 NAL units in either framing, or holds no picture in what was read.</summary>
    Unknown,

    /// <summary>An IDR picture (NAL unit type 5): every later picture decodes without anything before it.</summary>
    Idr,

    /// <summary>A non-IDR picture (type 1 or 2): an I-frame flagged as a keyframe here is an OPEN-GOP keyframe.</summary>
    NonIdr,
}

/// <summary>
/// Reads the kind of the first picture of one H.264 packet from its bytes (T-223, SPEC-001 I70). A keyframe flag is not enough
/// to cut at: ffmpeg's parser also flags a non-IDR I-frame with a recovery-point SEI (what x264 <c>open-gop=1</c> writes for every
/// keyframe but the first), and the pictures after it may refer to the ones before it, so a part that starts there is not
/// self-contained. Only an IDR is.
///
/// <para>The packet is in one of two framings: ISO length-prefixed NAL units (MP4/MOV, a 4-byte length each, in practice always 4)
/// or Annex B (start codes, AVI). A packet is read in both and the answer stands only when they agree or one cannot be read: a
/// length prefix of <c>00 00 01 xx</c> looks like a start code, and a packet that reads two ways differently is
/// <see cref="H264PictureKind.Unknown"/>, never a guess. The caller treats Unknown as "not provably an IDR".</para>
/// </summary>
internal static class H264Packet
{
    private const int IdrSlice = 5;
    private const int Slice = 1;
    private const int SlicePartitionA = 2;

    /// <summary>
    /// The bytes of an ffprobe <c>-show_data</c> dump: lines of <c>00000010: 85ff d457 6d62 78ad 3e89 b4b5 2ade ca87  ascii</c>,
    /// 16 bytes each, the hex area 39 characters wide (a short last line is padded). At most <paramref name="maxBytes"/> are
    /// read: the leading NAL units are all a picture kind needs. Null when a line is not in that shape.
    /// </summary>
    internal static byte[]? BytesFromHexDump(string? dump, int maxBytes)
    {
        if (string.IsNullOrEmpty(dump))
        {
            return null;
        }

        const int hexStart = 10;
        const int hexWidth = 39;
        var bytes = new List<byte>(Math.Min(maxBytes, 1024));
        foreach (var raw in dump.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length < hexStart || line[8] != ':' || line[9] != ' ')
            {
                return null;
            }

            var high = -1;
            var end = Math.Min(line.Length, hexStart + hexWidth);
            for (var i = hexStart; i < end; i++)
            {
                var c = line[i];
                if (c == ' ')
                {
                    continue;
                }

                var nibble = HexValue(c);
                if (nibble < 0)
                {
                    return null;
                }

                if (high < 0)
                {
                    high = nibble;
                    continue;
                }

                bytes.Add((byte)((high << 4) | nibble));
                high = -1;
                if (bytes.Count >= maxBytes)
                {
                    return bytes.ToArray();
                }
            }

            if (high >= 0)
            {
                return null; // an odd number of hex digits
            }
        }

        return bytes.Count == 0 ? null : bytes.ToArray();
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>
    /// The kind of the first picture (the first slice NAL unit: type 1, 2 or 5) in <paramref name="data"/>, read as length-prefixed
    /// and as Annex B. SPS, PPS, SEI and access-unit delimiters before it are skipped. <paramref name="data"/> may be the head of the
    /// packet only; a NAL unit that is cut off before its first slice is Unknown.
    /// </summary>
    internal static H264PictureKind FirstPictureKind(ReadOnlySpan<byte> data)
    {
        var lengthPrefixed = WalkLengthPrefixed(data);
        var annexB = WalkAnnexB(data);
        if (lengthPrefixed == H264PictureKind.Unknown)
        {
            return annexB;
        }

        return annexB == H264PictureKind.Unknown || annexB == lengthPrefixed ? lengthPrefixed : H264PictureKind.Unknown;
    }

    private static H264PictureKind WalkLengthPrefixed(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        while (pos + 5 <= data.Length)
        {
            var length = ((long)data[pos] << 24) | ((long)data[pos + 1] << 16) | ((long)data[pos + 2] << 8) | data[pos + 3];
            if (length < 2)
            {
                return H264PictureKind.Unknown; // a unit has its header and a payload: a length of 1 is an Annex B start code
            }

            var picture = PictureOf(data[pos + 4]);
            if (picture is not null)
            {
                return picture.Value;
            }

            if (!IsSkippable(data[pos + 4]))
            {
                return H264PictureKind.Unknown; // not a NAL unit header this walk knows how to step over
            }

            if (pos + 4 + length > data.Length)
            {
                return H264PictureKind.Unknown; // the unit to skip runs past what was read
            }

            pos += 4 + (int)length;
        }

        return H264PictureKind.Unknown;
    }

    private static H264PictureKind WalkAnnexB(ReadOnlySpan<byte> data)
    {
        var pos = StartCodeEnd(data, 0, requireAtStart: true);
        while (pos >= 0 && pos < data.Length)
        {
            var picture = PictureOf(data[pos]);
            if (picture is not null)
            {
                return picture.Value;
            }

            if (!IsSkippable(data[pos]))
            {
                return H264PictureKind.Unknown;
            }

            pos = StartCodeEnd(data, pos + 1, requireAtStart: false);
        }

        return H264PictureKind.Unknown;
    }

    /// <summary>The index just past the next <c>00 00 01</c> at or after <paramref name="from"/>, or -1. At the start, only zeros may come before it.</summary>
    private static int StartCodeEnd(ReadOnlySpan<byte> data, int from, bool requireAtStart)
    {
        for (var i = from; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                return i + 3;
            }

            if (requireAtStart && data[i] != 0)
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// True for a NAL unit header of a unit that carries no first picture and can be stepped over: SEI, SPS, PPS, the access-unit
    /// delimiter, end of sequence or stream, filler, the SPS extension, a prefix unit, the subset SPS, an auxiliary or an extension
    /// slice. A reserved or unspecified type, or a header with the forbidden bit set, is not: the walk gives up (Unknown).
    /// </summary>
    private static bool IsSkippable(byte header) =>
        (header & 0x80) == 0 && (header & 0x1F) is 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 19 or 20;

    /// <summary>The picture kind of a NAL unit header byte, or null when the unit is not a slice (it is skipped).</summary>
    private static H264PictureKind? PictureOf(byte header)
    {
        if ((header & 0x80) != 0)
        {
            return null;
        }

        return (header & 0x1F) switch
        {
            IdrSlice => H264PictureKind.Idr,
            Slice or SlicePartitionA => H264PictureKind.NonIdr,
            _ => null,
        };
    }
}
