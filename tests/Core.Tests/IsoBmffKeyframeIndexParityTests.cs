using System.Buffers.Binary;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-194 — the sample-table keyframe reader against the real ffprobe, on files the real ffmpeg made. The fixtures are small and
/// synthetic; the SHAPES matter: B-frames and the edit list that hides their delay, a video that starts late, a stream copy that
/// starts mid-GOP, fragmented and foreign containers, several video tracks, a Join's 21 ms video start. For every shape the reader
/// accepts, the list is compared with <c>ffprobe -show_packets</c> element by element, TimeSpan-exact; for every shape it declines,
/// the probe must give today's packet-scan list.
/// </summary>
[Collection(MediaFixturesCollection.Name)]
public class IsoBmffKeyframeIndexParityTests
{
    private readonly SampleTableFixtures _samples;
    private readonly ITestOutputHelper _output;

    public IsoBmffKeyframeIndexParityTests(SampleTableFixtures samples, ITestOutputHelper output)
    {
        _samples = samples;
        _output = output;
    }

    /// <summary>Shapes the reader reads: each name is a property of <see cref="SampleTableFixtures"/>.</summary>
    public static TheoryData<string> AcceptedShapes => new()
    {
        nameof(SampleTableFixtures.H264Bframes),           // H.264 with B-frames, moov at the end
        nameof(SampleTableFixtures.H264BframesFaststart),  // the same remuxed +faststart (moov in front)
        nameof(SampleTableFixtures.H264NoBframes),
        nameof(SampleTableFixtures.Hvc1),
        nameof(SampleTableFixtures.Hev1),
        nameof(SampleTableFixtures.HevcMain10),
        nameof(SampleTableFixtures.LateVideoMov),          // a late video: a leading empty edit (media_time -1)
        nameof(SampleTableFixtures.LateVideoMp4),
        nameof(SampleTableFixtures.Ntsc),                  // 30000/1001: keyframes between whole seconds
        nameof(SampleTableFixtures.Fps60),
        nameof(SampleTableFixtures.LongGop),
        nameof(SampleTableFixtures.IntraOnly),             // no stss at all
        nameof(SampleTableFixtures.Mov),
        nameof(SampleTableFixtures.TrimmedStart),          // an edit list that starts mid-GOP: a pre-roll keyframe at a negative pts
        nameof(SampleTableFixtures.TrimmedEnd),            // an edit list shorter than the media
        nameof(SampleTableFixtures.TwoVideoTracks),        // v:0 is the first
        nameof(SampleTableFixtures.AudioTrackFirst),       // the first trak is audio
        nameof(SampleTableFixtures.JoinStyle),             // video starts 0.021 s after the format's start 0
        nameof(SampleTableFixtures.Mp4NamedMkv),           // an MP4 under another name: the reader goes by the bytes
    };

    /// <summary>Shapes the reader declines (and so the packet scan answers).</summary>
    public static TheoryData<string> DeclinedShapes => new()
    {
        nameof(SampleTableFixtures.Fragmented),
        nameof(SampleTableFixtures.Matroska),
        nameof(SampleTableFixtures.TransportStream),
        nameof(SampleTableFixtures.MatroskaNamedMp4),      // an MKV under an .mp4 name
        nameof(SampleTableFixtures.NegativeCtsOffsets),    // ctts version 1 with negative offsets
        nameof(SampleTableFixtures.FirstTrackMpeg4),       // v:0 is an mp4v track, an avc1 track follows
    };

    private string Path(string shape) =>
        (string)typeof(SampleTableFixtures).GetProperty(shape)!.GetValue(_samples)!;

    private bool Skip()
    {
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg");
        return false;
    }

    private static MediaProbe NewProbe() => SampleTableTestSupport.NewProbe();

    [SkippableTheory]
    [MemberData(nameof(AcceptedShapes))]
    [Trait("serves-spec", "SPEC-004")]
    public async Task AcceptedShape_ReaderAndProbe_EqualThePacketScan_TimeSpanExact(string shape)
    {
        Skip();
        var path = Path(shape);
        var probe = NewProbe();
        var scanned = await probe.ScanKeyframesFromPacketsForTestAsync(path);
        scanned.Count.Should().BeGreaterThan(1, "the fixture must have keyframes to compare");

        var read = IsoBmffKeyframeIndex.TryRead(() => File.OpenRead(path), out var reason);
        read.Should().NotBeNull($"{shape} is a shape the reader reads (declined: {reason})");
        read!.Keyframes.Should().Equal(scanned, "the sample table gives ffprobe's keyframe times");

        var viaProbe = await probe.GetKeyframesAsync(path);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Index, $"{shape}: both anchor windows agree with the table");
        viaProbe.Should().Equal(scanned);
    }

    [SkippableTheory]
    [MemberData(nameof(DeclinedShapes))]
    [Trait("serves-spec", "SPEC-004")]
    public async Task DeclinedShape_FallsBackToThePacketScan_WithTodaysList(string shape)
    {
        Skip();
        var path = Path(shape);
        var probe = NewProbe();
        var scanned = await probe.ScanKeyframesFromPacketsForTestAsync(path);
        scanned.Should().NotBeEmpty();

        IsoBmffKeyframeIndex.TryRead(() => File.OpenRead(path), out var reason).Should().BeNull($"{shape} is outside the subset");
        reason.Should().NotBeNullOrEmpty();
        _output.WriteLine($"{shape}: declined - {reason}");

        var viaProbe = await probe.GetKeyframesAsync(path);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
        viaProbe.Should().Equal(scanned);
    }

    [SkippableFact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task OpenGop_EitherReadAndEqualOrDeclined_NeverDifferent()
    {
        Skip();
        // An open GOP can make ffmpeg flag a keyframe the stss does not list (the rap sample group); whichever way the file is
        // written, the probe's answer is the packet scan's.
        var path = _samples.OpenGop;
        var probe = NewProbe();
        var scanned = await probe.ScanKeyframesFromPacketsForTestAsync(path);
        var viaProbe = await probe.GetKeyframesAsync(path);
        _output.WriteLine($"open GOP: path {probe.LastScanPath}");
        viaProbe.Should().Equal(scanned);
    }

    [SkippableFact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task JoinStyleFile_VideoStartsLateAgainstTheFormat_AndIsRead()
    {
        Skip();
        var path = _samples.JoinStyle;
        var read = IsoBmffKeyframeIndex.TryRead(() => File.OpenRead(path), out var reason);
        read.Should().NotBeNull(reason);
        read!.Keyframes[0].TotalSeconds.Should().BeInRange(0.015, 0.03, "a concat of AAC clips shifts the video 21 ms after the audio at 0");
        (await NewProbe().GetKeyframesAsync(path)).Should().Equal(read.Keyframes);
    }

    // A stream copy `ffmpeg -ss X -i source -c copy` writes an edit list whose media time is where the cut fell: on a frame, between
    // two frames, a frame after a keyframe. The reader must give ffprobe's list for each, whatever the frame rate or the codec.
    [SkippableTheory]
    [InlineData(nameof(SampleTableFixtures.H264Bframes), 0.034)]
    [InlineData(nameof(SampleTableFixtures.H264Bframes), 0.3)]
    [InlineData(nameof(SampleTableFixtures.H264Bframes), 1.2)]
    [InlineData(nameof(SampleTableFixtures.H264Bframes), 2.5)]
    [InlineData(nameof(SampleTableFixtures.H264Bframes), 3.1)]
    [InlineData(nameof(SampleTableFixtures.Ntsc), 0.3)]
    [InlineData(nameof(SampleTableFixtures.Ntsc), 1.2)]
    [InlineData(nameof(SampleTableFixtures.Ntsc), 3.1)]
    [InlineData(nameof(SampleTableFixtures.Hvc1), 0.3)]
    [InlineData(nameof(SampleTableFixtures.Hvc1), 2.5)]
    [InlineData(nameof(SampleTableFixtures.Fps60), 1.2)]
    [InlineData(nameof(SampleTableFixtures.H264NoBframes), 2.5)]
    [Trait("serves-spec", "SPEC-004")]
    public async Task TrimmedCopies_StartingBetweenFrames_AreReadLikeFfprobe(string shape, double start)
    {
        Skip();
        var path = _samples.TrimmedCopy(shape, start);
        var probe = NewProbe();
        var scanned = await probe.ScanKeyframesFromPacketsForTestAsync(path);
        var read = IsoBmffKeyframeIndex.TryRead(() => File.OpenRead(path), out var reason);
        read.Should().NotBeNull(reason);
        read!.Keyframes.Should().Equal(scanned);
        (await probe.GetKeyframesAsync(path)).Should().Equal(scanned);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Index);
    }

    [SkippableFact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task TrimmedStart_ListsThePreRollKeyframeAtANegativeTime()
    {
        Skip();
        var path = _samples.TrimmedStart;
        var read = IsoBmffKeyframeIndex.TryRead(() => File.OpenRead(path), out var reason);
        read.Should().NotBeNull(reason);
        read!.Keyframes[0].Should().BeNegative("the first GOP's keyframe lies before the edit list's start (ffprobe: flags KD, pts < 0)");
        (await NewProbe().GetKeyframesAsync(path)).Should().Equal(read.Keyframes);
    }

    // ---- Edit lists that ffmpeg reads in ways a formula can miss ---------------------------------------------------

    // Patches of the first edit of H264Bframes (movie time scale 1000, track time scale 15360, ctts offsets 1024 + 512k, edit
    // media_time 1024, duration 10000 ms). Measured against ffprobe when the reader was written: it lists only the last keyframe
    // at or before an edit's start, moves a media time that is not a sample's composition time forward to the next sample, and keeps
    // reading a while past a short edit. The reader accepts what it can prove and declines the rest; either way the probe's answer
    // is the packet scan's.
    [SkippableTheory]
    [InlineData(null, 7680L, true)]      // 0.5 s: on a sample, one pre-roll keyframe
    [InlineData(null, 38400L, true)]     // 2.5 s: two keyframes before the start, only the last is listed
    [InlineData(null, 46080L, true)]
    [InlineData(null, 47104L, true)]     // exactly the composition time of a keyframe: it is the first, at pts 0
    [InlineData(null, 47616L, true)]
    [InlineData(null, 69120L, true)]
    [InlineData(null, 153600L, false)]   // 10 s: one keyframe left, fewer than two
    [InlineData(null, 0L, true)]         // not the time of any sample (the first is at 1024): ffmpeg moves the start forward to it
    [InlineData(null, 512L, true)]
    [InlineData(null, 1025L, true)]      // between two samples: the start moves to the next one (1536), the pre-roll is the keyframe at 1024
    [InlineData(3000L, null, false)]     // a keyframe at or after the end of the edit: ffmpeg keeps some, drops some
    [InlineData(5500L, null, false)]
    [InlineData(9999L, null, true)]
    [InlineData(10001L, null, true)]
    [InlineData(20000L, null, true)]
    [Trait("serves-spec", "SPEC-004")]
    public async Task PatchedEditList_ReaderAcceptsOnlyWhatItCanProve(long? duration, long? mediaTime, bool accepted)
    {
        Skip();
        var bytes = File.ReadAllBytes(_samples.H264Bframes);
        var elst = FindBox(bytes, "elst") + 8; // version/flags and the entry count
        if (duration is { } d)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(elst), (uint)d);
        }

        if (mediaTime is { } m)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(elst + 4), (int)m);
        }

        var path = _samples.PathFor($"elst_{duration}_{mediaTime}.mp4");
        File.WriteAllBytes(path, bytes);

        var probe = NewProbe();
        var scanned = await probe.ScanKeyframesFromPacketsForTestAsync(path);
        var read = IsoBmffKeyframeIndex.TryRead(() => File.OpenRead(path), out var reason);
        _output.WriteLine($"duration {duration} media_time {mediaTime}: {(read is null ? "declined - " + reason : "read " + read.Keyframes.Count + " keyframes")}; ffprobe lists {scanned.Count}");
        (read is not null).Should().Be(accepted, reason);
        if (read is not null)
        {
            read.Keyframes.Should().Equal(scanned);
        }

        (await probe.GetKeyframesAsync(path)).Should().Equal(scanned);
        probe.LastScanPath.Should().Be(accepted ? KeyframeScanPath.Index : KeyframeScanPath.Packets);
    }

    [SkippableTheory]
    [InlineData(0x8000u)]  // media rate 0.5
    [InlineData(0u)]       // media rate 0
    [Trait("serves-spec", "SPEC-004")]
    public async Task PatchedEditListRate_IsDeclined(uint rate)
    {
        Skip();
        var bytes = File.ReadAllBytes(_samples.H264Bframes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(FindBox(bytes, "elst") + 8 + 8), rate);
        var path = _samples.PathFor($"elst_rate_{rate}.mp4");
        File.WriteAllBytes(path, bytes);

        IsoBmffKeyframeIndex.TryRead(() => File.OpenRead(path), out var reason).Should().BeNull();
        reason.Should().Contain("rate");
        var probe = NewProbe();
        (await probe.GetKeyframesAsync(path)).Should().Equal(await probe.ScanKeyframesFromPacketsForTestAsync(path));
    }

    // ---- A truncated or corrupt file ------------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData(nameof(SampleTableFixtures.H264Bframes), 0.97)]          // moov at the end: the moov is cut
    [InlineData(nameof(SampleTableFixtures.H264Bframes), 0.5)]
    [InlineData(nameof(SampleTableFixtures.H264BframesFaststart), 0.6)]  // moov in front: the media data is cut
    [InlineData(nameof(SampleTableFixtures.H264BframesFaststart), 0.05)]
    [Trait("serves-spec", "SPEC-004")]
    public async Task TruncatedCopy_DeclinesWithoutThrowing_AndTheProbeAnswersAsTheScanDoes(string shape, double fraction)
    {
        Skip();
        var source = Path(shape);
        var bytes = File.ReadAllBytes(source);
        var cut = _samples.PathFor($"truncated_{shape}_{fraction:0.00}.mp4");
        File.WriteAllBytes(cut, bytes.AsSpan(0, (int)(bytes.Length * fraction)).ToArray());

        var read = IsoBmffKeyframeIndex.TryRead(() => File.OpenRead(cut), out var reason);
        read.Should().BeNull("a truncated file is not read: " + reason);

        // What the probe does is what it did: the packet scan lists what ffprobe can read, or fails the same way.
        var probe = NewProbe();
        IReadOnlyList<TimeSpan>? scanned = null;
        Exception? scanFailure = null;
        try
        {
            scanned = await probe.ScanKeyframesFromPacketsForTestAsync(cut);
        }
        catch (FfprobeException ex)
        {
            scanFailure = ex;
        }

        var probe2 = NewProbe();
        if (scanFailure is not null)
        {
            var act = async () => await probe2.GetKeyframesAsync(cut);
            await act.Should().ThrowAsync<FfprobeException>();
        }
        else
        {
            (await probe2.GetKeyframesAsync(cut)).Should().Equal(scanned!);
            probe2.LastScanPath.Should().NotBe(KeyframeScanPath.Index);
        }
    }

    // ---- The table and ffprobe disagree ----------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData(1)]   // the second sync sample is moved by one sample: the head window sees a different second keyframe
    [InlineData(-1)]  // the last sync sample is moved by one sample: the tail window sees a different last keyframe
    [Trait("serves-spec", "SPEC-004")]
    public async Task TableThatDisagreesWithFfprobe_IsNotTrusted_ThePacketListIsUsedAndCached(int which)
    {
        Skip();
        var patched = _samples.PathFor($"disagree_{which}.mp4");
        var bytes = File.ReadAllBytes(_samples.H264Bframes);
        var stss = FindBox(bytes, "stss");
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stss + 4));
        var entry = which > 0 ? stss + 8 + 4 : stss + 8 + (4 * ((int)count - 1));
        var number = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry), number + 1); // a sample that is not a keyframe
        File.WriteAllBytes(patched, bytes);

        var probe = NewProbe();
        var scanned = await probe.ScanKeyframesFromPacketsForTestAsync(patched);

        // The reader alone trusts its table; the anchor windows are what catch it.
        var read = IsoBmffKeyframeIndex.TryRead(() => File.OpenRead(patched));
        read.Should().NotBeNull();
        read!.Keyframes.Should().NotEqual(scanned);

        var viaProbe = await probe.GetKeyframesAsync(patched);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets, "ffprobe disagrees with the table at one end");
        viaProbe.Should().Equal(scanned);
        (await probe.GetKeyframesAsync(patched)).Should().Equal(scanned, "the packet-scan list is what got cached");
    }

    [SkippableFact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task TableWithAShiftedTimeScale_ListsWhatFfprobeListsForTheSameFile()
    {
        // ffprobe reads the same patched mdhd, so the table and the demuxer shift together: the anchor has nothing to
        // disagree about, and the contract here is only that the list equals the packet scan of the very same bytes.
        Skip();
        var patched = _samples.PathFor("disagree_timescale.mp4");
        var bytes = File.ReadAllBytes(_samples.H264Bframes);
        var mdhd = FindBox(bytes, "mdhd");
        var timescaleAt = mdhd + 12; // version/flags, creation time, modification time, then the time scale
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(timescaleAt));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(timescaleAt), timescale + 100);
        File.WriteAllBytes(patched, bytes);

        var probe = NewProbe();
        var scanned = await probe.ScanKeyframesFromPacketsForTestAsync(patched);
        var viaProbe = await probe.GetKeyframesAsync(patched);
        viaProbe.Should().Equal(scanned);
    }

    /// <summary>Offset of the body of the first box of the type, found by a byte search (the fixtures hold each type once).</summary>
    private static int FindBox(byte[] bytes, string type)
    {
        var tag = System.Text.Encoding.ASCII.GetBytes(type);
        var at = bytes.AsSpan().IndexOf(tag);
        at.Should().BeGreaterThan(3, $"the fixture holds a {type} box");
        return at + 4;
    }

    // ---- Bytes: the reader reads the moov and some headers, never the media data -------------------------------------

    [SkippableFact]
    [Trait("serves-spec", "SPEC-004")]
    public void Large_ReaderReadsAtMostMoovPlus64KiB()
    {
        Skip();
        var path = _samples.Large;
        var length = new FileInfo(path).Length;
        length.Should().BeGreaterThanOrEqualTo(50L * 1024 * 1024, "the fixture is 50 MB or more");

        var moovSize = TopLevelBoxSize(path, "moov");
        var counting = new CountingStream(File.OpenRead(path));
        var read = IsoBmffKeyframeIndex.TryRead(() => counting, out var reason);
        read.Should().NotBeNull(reason);
        _output.WriteLine($"file {length:N0} bytes, moov {moovSize:N0} bytes, read {counting.BytesRead:N0} bytes, {read!.Keyframes.Count} keyframes");

        counting.BytesRead.Should().BeLessThanOrEqualTo(moovSize + (64 * 1024));
        read.BytesRead.Should().Be(counting.BytesRead, "the reader's own count is the stream's");
        read.BytesRead.Should().BeLessThan(length / 100);
    }

    private static long TopLevelBoxSize(string path, string type)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[16];
        long position = 0;
        while (position + 8 <= stream.Length)
        {
            stream.Position = position;
            stream.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var name = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            if (size == 1)
            {
                stream.ReadExactly(header, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
            }

            if (name == type)
            {
                return size;
            }

            position += size;
        }

        throw new InvalidOperationException("no " + type + " box");
    }
}
