using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-132 (SPEC-013) — the AVIO adapter that lets the preview open a file whose path cannot be a
/// <see cref="Uri"/> (a network share whose server name contains a space; see ADR-0020).
///
/// <para>These tests drive the adapter over ORDINARY local files, because what they pin is the ffmpeg
/// callback contract — read semantics, seek semantics, EOF signalling, handle release — none of which
/// depends on the path being exotic. What they deliberately do NOT prove is that playback works from a
/// real share with a space in its name; that needs a real share and is called out as unverified in
/// ADR-0020 and T-132 rather than implied by a green suite here.</para>
/// </summary>
public sealed class FileMediaInputStreamTests : IDisposable
{
    private const int AvErrorEof = -541478725;   // -MKTAG('E','O','F',' ')
    private const int AvseekSize = 0x10000;

    private readonly string _dir;

    public FileMediaInputStreamTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-mis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static byte[] Pattern(int length)
        => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    private static unsafe int Read(FileMediaInputStream s, byte[] into, int count)
    {
        fixed (byte* p = into)
        {
            return s.Read(null, p, count);
        }
    }

    // ---- Reading ------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void Read_ReturnsTheBytes_InOrder_ThenSignalsEof()
    {
        var data = Pattern(1000);
        using var s = new FileMediaInputStream(WriteFile("a.bin", data));

        var buf = new byte[400];
        Read(s, buf, 400).Should().Be(400);
        buf.Should().Equal(data.Take(400), "the first read yields the head of the file");

        Read(s, buf, 400).Should().Be(400);
        buf.Should().Equal(data.Skip(400).Take(400), "reads advance the position");

        var tail = new byte[400];
        Read(s, tail, 400).Should().Be(200, "a short final read returns only what is left");
        tail.Take(200).Should().Equal(data.Skip(800));

        Read(s, buf, 400).Should().Be(
            AvErrorEof,
            "ffmpeg treats a plain 0 as 'no data yet' and can spin on it — end of file must say AVERROR_EOF");
    }

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void Read_OfAnEmptyFile_IsEofImmediately()
    {
        using var s = new FileMediaInputStream(WriteFile("empty.bin", Array.Empty<byte>()));

        Read(s, new byte[16], 16).Should().Be(AvErrorEof);
    }

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void Read_OfZeroOrNegativeLength_IsEof_NotACrash()
    {
        using var s = new FileMediaInputStream(WriteFile("a.bin", Pattern(10)));

        Read(s, new byte[1], 0).Should().Be(AvErrorEof);
        Read(s, new byte[1], -5).Should().Be(AvErrorEof, "a nonsensical length must never index the buffer");
    }

    // ---- Seeking ------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public unsafe void Seek_HonoursAllThreeOrigins()
    {
        var data = Pattern(1000);
        using var s = new FileMediaInputStream(WriteFile("a.bin", data));

        s.Seek(null, 100, 0).Should().Be(100, "SEEK_SET is absolute");
        var buf = new byte[1];
        Read(s, buf, 1).Should().Be(1);
        buf[0].Should().Be(data[100]);

        s.Seek(null, 10, 1).Should().Be(111, "SEEK_CUR is relative to the position after that read");
        s.Seek(null, -1, 2).Should().Be(999, "SEEK_END counts back from the length");
    }

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public unsafe void Seek_WithAvseekSize_ReportsTheLength_AndDoesNotMove()
    {
        var data = Pattern(777);
        using var s = new FileMediaInputStream(WriteFile("a.bin", data));

        s.Seek(null, 0, 0).Should().Be(0);
        s.Seek(null, 0, AvseekSize).Should().Be(
            777, "AVSEEK_SIZE asks for the total length rather than a move — ffmpeg needs it to know the duration");

        var buf = new byte[1];
        Read(s, buf, 1).Should().Be(1);
        buf[0].Should().Be(data[0], "reporting the size must not have moved the read position");
    }

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public unsafe void Seek_WithAnUnknownWhence_IsRefused_NotGuessed()
    {
        using var s = new FileMediaInputStream(WriteFile("a.bin", Pattern(10)));

        s.Seek(null, 0, 99).Should().BeNegative("an unrecognised whence must fail, never silently seek to 0");
    }

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void CanSeek_IsTrue_ForARegularFile()
    {
        using var s = new FileMediaInputStream(WriteFile("a.bin", Pattern(10)));

        s.CanSeek.Should().BeTrue("without this ffmpeg cannot scrub, which is the whole point of the preview");
        s.ReadBufferLength.Should().BeGreaterThan(0);
    }

    // ---- Identity ------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void StreamUri_IsASyntheticIdentifier_NotTheRealPath()
    {
        var path = WriteFile("a.bin", Pattern(10));
        using var s = new FileMediaInputStream(path);

        s.StreamUri.Should().NotBeNull();
        s.StreamUri.IsAbsoluteUri.Should().BeTrue("FFME labels the stream with it, so it has to be a legal Uri");
        s.StreamUri.OriginalString.Should().NotContain(
            Path.GetFileName(path),
            "deriving it from the path would reintroduce exactly the parsing this adapter exists to avoid");
    }

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void TwoStreams_GetDistinctIdentities()
    {
        using var a = new FileMediaInputStream(WriteFile("a.bin", Pattern(4)));
        using var b = new FileMediaInputStream(WriteFile("b.bin", Pattern(4)));

        a.StreamUri.Should().NotBe(b.StreamUri);
    }

    // ---- Handles -------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void TheFileIsOpenedShared_SoACutOverTheSameFileIsNotBlocked()
    {
        var path = WriteFile("a.bin", Pattern(64));
        using var s = new FileMediaInputStream(path);

        // The engine may read or even rewrite this file while the preview holds it. If the adapter took
        // an exclusive handle, previewing a row would make cutting that row fail.
        Action alsoOpen = () =>
        {
            using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            other.ReadByte();
        };

        alsoOpen.Should().NotThrow();
    }

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void Dispose_ReleasesTheHandle_AndIsIdempotent()
    {
        var path = WriteFile("a.bin", Pattern(64));
        var s = new FileMediaInputStream(path);

        s.Dispose();
        s.Dispose(); // Unload can run more than once; a second release must not throw.

        Action deleteIt = () => File.Delete(path);
        deleteIt.Should().NotThrow("a handle left open would also block a later cut over this file");
    }

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void AfterDispose_ReadAndSeek_FailQuietly_RatherThanThrowingIntoNativeCode()
    {
        var s = new FileMediaInputStream(WriteFile("a.bin", Pattern(64)));
        s.Dispose();

        // These are ffmpeg callbacks: an exception crossing back into native code is a process-level
        // hazard, so a disposed stream must answer with error codes instead.
        Read(s, new byte[8], 8).Should().Be(AvErrorEof);
        Seek(s).Should().BeNegative();

        static unsafe long Seek(FileMediaInputStream st) => st.Seek(null, 0, 0);
    }

    // ---- Construction --------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void AMissingFile_ThrowsAtConstruction_SoTheCallerCanFallBack()
    {
        Action act = () => new FileMediaInputStream(Path.Combine(_dir, "nope.bin"));

        act.Should().Throw<Exception>(
            "FfmeMediaPlayer catches this and reports the ordinary refusal instead of opening nothing");
    }

    [Trait("serves-spec", "SPEC-013")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankPath_IsRejected(string? path)
    {
        Action act = () => new FileMediaInputStream(path!);

        act.Should().Throw<ArgumentException>();
    }

    // ---- Performance ----------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-013")]
    [Fact]
    public void ReadingTheWholeFile_CostsOnePassAndNoBuffering_OfTheWholePayload()
    {
        // Structural, not timed: the adapter must stream. A read of N bytes advances by exactly N and the
        // file is consumed in ceil(size / bufferLength) reads — no accumulation of the payload in memory,
        // which for a multi-GB video over a share would be fatal.
        var data = Pattern(10_000);
        using var s = new FileMediaInputStream(WriteFile("a.bin", data));

        var chunk = new byte[1024];
        var total = 0;
        var reads = 0;
        int n;
        while ((n = Read(s, chunk, chunk.Length)) > 0)
        {
            total += n;
            reads++;
            reads.Should().BeLessThan(50, "a stalled read would loop forever here rather than fail a timer");
        }

        total.Should().Be(data.Length, "every byte is delivered exactly once");
        reads.Should().Be(10, "10_000 bytes in 1024-byte chunks is 10 reads — no re-reading, no padding");
    }
}
