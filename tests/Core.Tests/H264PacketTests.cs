using FluentAssertions;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-223 (SPEC-001 I70) - reading the kind of the first picture of an H.264 packet (<see cref="H264Packet"/>): an IDR, a non-IDR
/// I-frame (an open-GOP keyframe), or "cannot tell". Pure bytes in, no ffmpeg: the real-file rows (a packet of an x264 open-GOP
/// file and of a closed-GOP one) are in <see cref="SmartCutOpenGopIntegrationTests"/>.
///
/// <para>The framing (the length size of an ISO packet, 0 for Annex B) is GIVEN, as ffprobe's <c>nal_length_size</c> gives it;
/// the review of the first version, which tried both framings, found packets that read as an IDR in the one they are not in.
/// The rows named after it below are those packets.</para>
/// </summary>
public sealed class H264PacketTests
{
    private static byte[] B(params int[] bytes) => bytes.Select(b => (byte)b).ToArray();

    /// <summary>A length-prefixed NAL unit with a 4-byte length: the length, the header byte, then <paramref name="payload"/> more bytes.</summary>
    private static byte[] Unit(int header, int payload = 3)
    {
        var length = 1 + payload;
        return B(length >> 24 & 0xFF, length >> 16 & 0xFF, length >> 8 & 0xFF, length & 0xFF, header).Concat(Enumerable.Repeat((byte)0x11, payload)).ToArray();
    }

    /// <summary>An Annex B NAL unit: a 4-byte start code, the header byte, then <paramref name="payload"/> bytes with no zero run.</summary>
    private static byte[] AnnexUnit(int header, int payload = 3) =>
        B(0, 0, 0, 1, header).Concat(Enumerable.Repeat((byte)0x22, payload)).ToArray();

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>The packet read as ISO length-prefixed units with 4-byte lengths, the whole packet given.</summary>
    private static H264PictureKind Iso(byte[] packet) => H264Packet.FirstPictureKind(packet, 4, packet.Length);

    /// <summary>The packet read as Annex B, the whole packet given.</summary>
    private static H264PictureKind AnnexB(byte[] packet) => H264Packet.FirstPictureKind(packet, 0, packet.Length);

    // serves-spec: SPEC-001#I70 - the ISO framing: an IDR slice (type 5) is an IDR, a plain slice (type 1) is not.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(0x65, true)]      // nal_ref_idc 3, type 5
    [InlineData(0x25, true)]      // nal_ref_idc 1, type 5
    [InlineData(0x41, false)]     // nal_ref_idc 2, type 1
    [InlineData(0x01, false)]     // nal_ref_idc 0, type 1
    [InlineData(0x42, false)]     // type 2, data partition A
    public void ALengthPrefixedSlice_IsTheKindItsTypeSays(int header, bool idr) =>
        Iso(Unit(header)).Should().Be(idr ? H264PictureKind.Idr : H264PictureKind.NonIdr);

    // serves-spec: SPEC-001#I70 - the units in front of the picture are skipped, as an x264 keyframe has them: AUD, SPS, PPS, SEI.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheUnitsBeforeThePicture_AreSkipped()
    {
        Iso(Join(Unit(0x09, 1), Unit(0x67, 10), Unit(0x68, 4), Unit(0x06, 30), Unit(0x65, 100))).Should().Be(H264PictureKind.Idr);
        Iso(Join(Unit(0x09, 1), Unit(0x06, 5), Unit(0x41, 100)))
            .Should().Be(H264PictureKind.NonIdr, "x264 open-gop=1 puts a recovery-point SEI, then a plain slice");
    }

    // serves-spec: SPEC-001#I70 - the length size is the stream's: 1, 2 and 3 byte lengths read as themselves.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheLengthSizeIsTheStreams()
    {
        var two = B(0, 6, 0x41, 0x9A, 0x65, 0x00, 0x11, 0x22); // one unit of 6 bytes: header 0x41, then bytes that look like an IDR header
        H264Packet.FirstPictureKind(two, 2, two.Length).Should().Be(H264PictureKind.NonIdr);

        var one = B(5, 0x65, 1, 2, 3, 4);
        H264Packet.FirstPictureKind(one, 1, one.Length).Should().Be(H264PictureKind.Idr);

        var three = B(0, 0, 4, 0x41, 1, 2, 3);
        H264Packet.FirstPictureKind(three, 3, three.Length).Should().Be(H264PictureKind.NonIdr);
    }

    // serves-spec: SPEC-001#I70 - review finding: a 2-byte-length packet read with the wrong length size must not become an IDR. The
    // same bytes as 4-byte lengths put the 5th byte (0x65) in a header position; its length (410010) does not fit the packet.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ALengthThatDoesNotFitThePacket_IsUnknown_NotAnIdr()
    {
        var packet = B(0, 6, 0x41, 0x9A, 0x65, 0x00, 0x11, 0x22);

        H264Packet.FirstPictureKind(packet, 4, packet.Length).Should().Be(H264PictureKind.Unknown);
    }

    // serves-spec: SPEC-001#I70 - review finding: a 4-byte length of 00 00 01 65 (a unit of 357 bytes) looks like a start code and an
    // IDR header. Read with the stream's framing it is a SEI, and a packet cut off inside the second, larger SEI is Unknown - never Idr.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(261)]
    [InlineData(293)]
    [InlineData(325)]
    [InlineData(357)]
    public void ALengthThatLooksLikeAStartCodeAndAnIdrHeader_IsNotMistakenForOne(int firstSeiLength)
    {
        var sei = B(0, 0, firstSeiLength >> 8 & 0xFF, firstSeiLength & 0xFF, 0x06).Concat(Enumerable.Repeat((byte)0x33, firstSeiLength - 1)).ToArray();
        var big = Unit(0x06, 20000);
        var whole = Join(sei, big, Unit(0x65, 100));
        var read = whole.Take(16 * 1024).ToArray();

        H264Packet.FirstPictureKind(read, 4, whole.Length).Should().Be(H264PictureKind.Unknown, "the larger SEI runs past what was read");
    }

    // serves-spec: SPEC-001#I70 - a first unit whose length looks like a start code (00 00 01 2C = 300 bytes) is a length.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ALengthThatLooksLikeAStartCode_IsReadAsALength()
    {
        var sei = B(0, 0, 1, 0x2C, 0x06).Concat(Enumerable.Repeat((byte)0x33, 299)).ToArray(); // length 300, type 6

        Iso(Join(sei, Unit(0x41, 50))).Should().Be(H264PictureKind.NonIdr);
    }

    // serves-spec: SPEC-001#I70 - the Annex B framing (a stream whose is_avc is false): start codes, with a three-byte one too.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AnAnnexBPacket_IsReadByItsStartCodes()
    {
        AnnexB(Join(AnnexUnit(0x09, 1), AnnexUnit(0x67, 6), AnnexUnit(0x68, 3), AnnexUnit(0x65, 20))).Should().Be(H264PictureKind.Idr);
        AnnexB(Join(AnnexUnit(0x09, 1), AnnexUnit(0x06, 8), AnnexUnit(0x41, 20))).Should().Be(H264PictureKind.NonIdr);
        AnnexB(Join(B(0, 0, 1, 0x67, 0x33, 0x33), B(0, 0, 1, 0x65, 0x44, 0x44))).Should().Be(H264PictureKind.Idr, "a three-byte start code");
    }

    // serves-spec: SPEC-001#I70 - review finding: an Annex B packet with a user-data SEI larger than the window (the 3-byte start code,
    // then a SEI whose payload type byte is 05) is Unknown. It used to read as an IDR through the other framing.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AnAnnexBPacket_WhoseSeiRunsPastTheWindow_IsUnknown()
    {
        var whole = Join(B(0, 0, 1, 0x06, 0x05), Enumerable.Repeat((byte)0x55, 20000).ToArray(), B(0, 0, 1, 0x41, 0x66));
        var read = whole.Take(16 * 1024).ToArray();

        H264Packet.FirstPictureKind(read, 0, whole.Length).Should().Be(H264PictureKind.Unknown);
    }

    // serves-spec: SPEC-001#I70 - what cannot be read is Unknown, never a guess: nothing, no picture in what was read, a unit cut
    // off before the picture, a forbidden bit, a zero length, noise, a size smaller than what was read, and a framing that is not one.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void WhatCannotBeRead_IsUnknown()
    {
        H264Packet.FirstPictureKind(ReadOnlySpan<byte>.Empty, 4, 0).Should().Be(H264PictureKind.Unknown);
        Iso(Join(Unit(0x09, 1), Unit(0x06, 5))).Should().Be(H264PictureKind.Unknown, "no picture in what was read");
        var cut = Unit(0x67, 400).Take(40).ToArray();
        H264Packet.FirstPictureKind(cut, 4, 405).Should().Be(H264PictureKind.Unknown, "the parameter set runs past what was read");
        Iso(Unit(0xE5)).Should().Be(H264PictureKind.Unknown, "forbidden_zero_bit set");
        Iso(B(0, 0, 0, 0, 0x65, 1, 2, 3)).Should().Be(H264PictureKind.Unknown, "a zero length");
        Iso(B(0xDE, 0xAD, 0xBE, 0xEF, 0x12, 0x34, 0x56, 0x78)).Should().Be(H264PictureKind.Unknown);
        Iso(Unit(0x12, 10)).Should().Be(H264PictureKind.Unknown, "a reserved type is not stepped over");

        var packet = Unit(0x65);
        H264Packet.FirstPictureKind(packet, 4, packet.Length - 1).Should().Be(H264PictureKind.Unknown, "the packet cannot be smaller than what was read");
        H264Packet.FirstPictureKind(packet, 5, packet.Length).Should().Be(H264PictureKind.Unknown, "a length size of 5 is no framing");
        H264Packet.FirstPictureKind(packet, -1, packet.Length).Should().Be(H264PictureKind.Unknown);
        AnnexB(Unit(0x65)).Should().Be(H264PictureKind.Unknown, "no start code at the start of an ISO packet");
    }

    // serves-spec: SPEC-001#I70 - a picture whose slice sits behind a long SEI is still found (x264 writes ~700 bytes of user data).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ALongSeiBeforeTheSlice_IsSkipped() =>
        Iso(Join(Unit(0x06, 700), Unit(0x65, 10))).Should().Be(H264PictureKind.Idr);

    // ---- The ffprobe hex dump -------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I70 - ffprobe -show_data prints 16 bytes a line behind an offset; the bytes come back in order, a
    // short last line is padded, and only the asked number of bytes is read.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AHexDump_IsReadBackAsItsBytes()
    {
        const string dump =
            "\n00000000: 0000 0005 0606 01c4 8000 0014 ec41 888c  .............A..\n"
            + "00000010: 85ff d457 6d62 78ad 3e89 b4b5 2ade ca87  ...Wmbx.>...*...\n"
            + "00000020: aa47 7d                                  .G}\n";

        var bytes = H264Packet.BytesFromHexDump(dump, 1000)!;

        bytes.Should().HaveCount(35);
        bytes.Take(8).Should().Equal(B(0, 0, 0, 5, 6, 6, 1, 0xC4));
        bytes.Skip(32).Should().Equal(B(0xAA, 0x47, 0x7D));
        H264Packet.BytesFromHexDump(dump, 20)!.Should().HaveCount(20, "the leading bytes are all a picture kind needs");
        H264Packet.FirstPictureKind(bytes, 4, 5369).Should().Be(H264PictureKind.NonIdr, "length 5 SEI, then type 1 (0x41)");
    }

    // serves-spec: SPEC-001#I70 - a dump that is not in ffprobe's shape is no bytes (and so Unknown), not a half read.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("hello world, not a dump")]
    [InlineData("00000000: zz00 0005 0606 01c4 8000 0014 ec41 888c  ................")]
    [InlineData("00000000: 0000 0005 0606 01c4 8000 0014 ec41 888  ................")]
    public void ADumpInAnotherShape_IsNoBytes(string? dump) =>
        H264Packet.BytesFromHexDump(dump, 1000).Should().BeNull();
}
