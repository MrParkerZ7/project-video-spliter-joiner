using FluentAssertions;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-194 — the sample-table keyframe reader on CRAFTED box trees (no ffmpeg): what it reads exactly, and every shape it must
/// decline. The reader is a binary-format parser, so each rule is pinned by the one smallest file that triggers it. The default
/// <see cref="Mp4Builder"/> track has 90 samples at 30 fps (time scale 30000) with sync samples 1, 31 and 61: keyframes at 0, 1
/// and 2 seconds.
/// </summary>
public class IsoBmffKeyframeIndexTests
{
    private const uint RateOne = 0x00010000;

    private static readonly TimeSpan[] ZeroOneTwo = { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };

    private static SampleTableKeyframes? Read(byte[] file, out string? reason) =>
        IsoBmffKeyframeIndex.TryRead(() => new MemoryStream(file), out reason);

    private static void AssertReads(Mp4Builder builder, params TimeSpan[] expected)
    {
        var read = Read(builder.Build(), out var reason);
        read.Should().NotBeNull($"the reader should read this file (declined: {reason})");
        read!.Keyframes.Should().Equal(expected);
    }

    private static void AssertDeclines(Mp4Builder builder, string reasonPart)
    {
        var read = Read(builder.Build(), out var reason);
        read.Should().BeNull($"the reader must decline this shape (read {read?.Keyframes.Count} keyframes)");
        reason.Should().Contain(reasonPart);
    }

    private static Mp4Track Video(Mp4Builder b) => b.Tracks[0];

    // ---- What it reads --------------------------------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void DefaultTrack_KeyframesAtZeroOneTwoSeconds() => AssertReads(new Mp4Builder(), ZeroOneTwo);

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void MoovBeforeMdat_AndMoovAfterMdat_GiveTheSameList()
    {
        AssertReads(new Mp4Builder { MoovFirst = true }, ZeroOneTwo);
        AssertReads(new Mp4Builder { MoovFirst = false }, ZeroOneTwo);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void SixtyFourBitBoxSizes_AreRead()
    {
        // mdat and moov with size field 1 and an eight-byte size.
        AssertReads(new Mp4Builder { Use64BitTopLevelSizes = true }, ZeroOneTwo);
        AssertReads(new Mp4Builder { Use64BitTopLevelSizes = true, MoovFirst = true }, ZeroOneTwo);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void LastBoxWithSizeZero_RunsToTheEndOfTheFile()
    {
        var builder = new Mp4Builder { LastBoxRunsToEnd = true };
        AssertReads(builder, ZeroOneTwo);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Mdat64BitSize_ThenMoovWithSizeZero()
    {
        var builder = new Mp4Builder { Use64BitTopLevelSizes = true, LastBoxRunsToEnd = true };
        AssertReads(builder, ZeroOneTwo);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void UnknownTopLevelBoxes_AndSlackAfterTheLastBox_AreSkipped()
    {
        var file = Mp4Builder.Concat(
            Mp4Builder.Box("ftyp", Mp4Builder.Concat(Mp4Builder.Ascii("qt  "), Mp4Builder.U32(0), Mp4Builder.Ascii("qt  "))),
            Mp4Builder.Box("wide", Array.Empty<byte>()),
            Mp4Builder.Box("free", new byte[40]),
            BuildWithoutFtyp(new Mp4Builder()),
            new byte[5]); // fewer than a box header: slack, ignored
        var read = Read(file, out var reason);
        read.Should().NotBeNull(reason);
        read!.Keyframes.Should().Equal(ZeroOneTwo);
    }

    private static byte[] BuildWithoutFtyp(Mp4Builder b)
    {
        b.WithFtyp = false;
        return b.Build();
    }

    [Theory]
    [InlineData("hvc1")]
    [InlineData("hev1")]
    [InlineData("avc3")]
    [InlineData("avc1")]
    [Trait("serves-spec", "SPEC-004")]
    public void SupportedSampleEntries_AreRead(string entry)
    {
        var b = new Mp4Builder();
        Video(b).Entry = entry;
        AssertReads(b, ZeroOneTwo);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void ChunkOffsetsAs64Bit_Co64_AreNotInTheWay()
    {
        var b = new Mp4Builder();
        Video(b).UseCo64 = true;
        AssertReads(b, ZeroOneTwo);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void NoStss_EverySampleIsASyncSample()
    {
        var b = new Mp4Builder();
        Video(b).Stss = null;
        var read = Read(b.Build(), out var reason);
        read.Should().NotBeNull(reason);
        read!.Keyframes.Should().HaveCount(90);
        read.Keyframes[0].Should().Be(TimeSpan.Zero);
        read.Keyframes[1].Should().BeCloseTo(TimeSpan.FromSeconds(1.0 / 30), TimeSpan.FromTicks(10));
        read.Keyframes[89].Should().BeCloseTo(TimeSpan.FromSeconds(89.0 / 30), TimeSpan.FromTicks(10));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [Trait("serves-spec", "SPEC-004")]
    public void CompositionOffsets_V0AndNonNegativeV1_AreAddedToTheDecodeTimes_AndTheEditListSubtractsThem(byte version)
    {
        // Every sample composed 2000 ticks after its decode time; an edit list that starts at 2000 puts the first at pts 0.
        var b = new Mp4Builder();
        Video(b).Ctts = new[] { (90u, 2000) };
        Video(b).CttsVersion = version;
        Video(b).Edits = new[] { (3000UL, 2000L, RateOne) };
        AssertReads(b, ZeroOneTwo);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void CompositionOffsets_WithoutAnEditList_ShiftTheKeyframes()
    {
        // ffmpeg does not move the start without an edit list: the sync samples are at their composition times.
        var b = new Mp4Builder();
        Video(b).Ctts = new[] { (90u, 3000) };
        var shift = TimeSpan.FromSeconds(0.1);
        AssertReads(b, ZeroOneTwo.Select(t => t + shift).ToArray());
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void LeadingEmptyEdit_DelaysTheVideo()
    {
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (500UL, -1L, RateOne), (3000UL, 0L, RateOne) };
        AssertReads(b, ZeroOneTwo.Select(t => t + TimeSpan.FromSeconds(0.5)).ToArray());
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EmptyEditDuration_IsRescaledFromTheMovieToTheTrackTimeScale_RoundingToNearest()
    {
        // Movie time scale 7, track 30000: 3 movie ticks = 3/7 s = 12857.14 track ticks, rounded to 12857 = 0.428567 s.
        var b = new Mp4Builder { MovieTimescale = 7 };
        Video(b).Edits = new[] { (3UL, -1L, RateOne), (21UL, 0L, RateOne) };
        var shift = TimeSpan.FromSeconds(0.428567);
        AssertReads(b, ZeroOneTwo.Select(t => t + shift).ToArray());

        // 4 movie ticks = 17142.86 -> 17143 (up), 0.571433 s.
        var b2 = new Mp4Builder { MovieTimescale = 7 };
        Video(b2).Edits = new[] { (4UL, -1L, RateOne), (21UL, 0L, RateOne) };
        AssertReads(b2, ZeroOneTwo.Select(t => t + TimeSpan.FromSeconds(0.571433)).ToArray());
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EditListStartingMidGop_ListsOnlyTheLastKeyframeAtOrBeforeTheStart_AtANegativePts()
    {
        // Media time 45000 (1.5 s, the time of sample 45); sync samples at 0, 1 and 2 s. ffmpeg lists the keyframe at 1 s
        // (pre-roll, pts -0.5) and the one at 2 s (pts 0.5), not the one at 0.
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (1500UL, 45000L, RateOne) };
        AssertReads(b, TimeSpan.FromSeconds(-0.5), TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EditListStartingExactlyOnAKeyframe_HasNoPreRoll()
    {
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (2000UL, 30000L, RateOne) }; // 1 s: the keyframe at 1 s is the start
        AssertReads(b, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EditListMediaTimeBetweenTwoSamples_MovesTheStartToTheNextSample()
    {
        // Media time 15500: between sample 15 (15000) and 16 (16000). The start is sample 16, at pts 0; the keyframe before is
        // the one at 0 (pre-roll), at -16000/30000.
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (2800UL, 15500L, RateOne) };
        var read = Read(b.Build(), out var reason);
        read.Should().NotBeNull(reason);
        read!.Keyframes.Should().Equal(TimeSpan.FromSeconds(-0.533333), TimeSpan.FromSeconds(0.466667), TimeSpan.FromSeconds(1.466667));
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void SixtyFourBitFields_Mdhd1_Elst1_AreRead()
    {
        var b = new Mp4Builder();
        Video(b).MdhdVersion = 1;
        Video(b).ElstVersion = 1;
        Video(b).Edits = new[] { (500UL, -1L, RateOne), (3000UL, 0L, RateOne) };
        AssertReads(b, ZeroOneTwo.Select(t => t + TimeSpan.FromSeconds(0.5)).ToArray());
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void FirstVideoTrack_IsTheOneRead_AudioInFrontAndASecondVideoTrackBehind()
    {
        var b = new Mp4Builder();
        var audio = new Mp4Track { Handler = "soun", Entry = "mp4a", Timescale = 44100, Stss = null, Stts = new[] { (100u, 1024u) } };
        var video1 = Video(b);
        var video2 = new Mp4Track { Stss = new uint[] { 1, 10, 20, 30 } };
        b.Tracks.Clear();
        b.Tracks.Add(audio);
        b.Tracks.Add(video1);
        b.Tracks.Add(video2);
        AssertReads(b, ZeroOneTwo);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void SampleGroupsOfOtherTypes_AreIgnored()
    {
        var b = new Mp4Builder();
        Video(b).StblExtras.Add(Mp4Builder.FullBox("sgpd", 1, 0, Mp4Builder.Concat(Mp4Builder.Ascii("roll"), Mp4Builder.U32(2), Mp4Builder.U32(0))));
        Video(b).StblExtras.Add(Mp4Builder.FullBox("sbgp", 0, 0, Mp4Builder.Concat(Mp4Builder.Ascii("roll"), Mp4Builder.U32(0))));
        AssertReads(b, ZeroOneTwo);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void LongRecording_ManyRunsAndSamples_IsReadWithoutBuildingPerSampleTables()
    {
        // 3 hours at 60 fps: 648000 samples in a few runs, a keyframe every 2 s (stss every 120 samples).
        var b = new Mp4Builder();
        var video = Video(b);
        video.Stts = new[] { (300000u, 500u), (348000u, 500u) };
        video.Timescale = 30000;
        video.Stss = Enumerable.Range(0, 5400).Select(i => (uint)((i * 120) + 1)).ToArray();
        var read = Read(b.Build(), out var reason);
        read.Should().NotBeNull(reason);
        read!.Keyframes.Should().HaveCount(5400);
        read.Keyframes[1].Should().Be(TimeSpan.FromSeconds(2));
        read.Keyframes[^1].Should().Be(TimeSpan.FromSeconds(5399 * 2));
    }

    // ---- What it declines ------------------------------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void NoFtyp_Declines() => AssertDeclines(new Mp4Builder { WithFtyp = false }, "ftyp");

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Mvex_Declines()
    {
        var b = new Mp4Builder();
        b.MoovExtras.Add(Mp4Builder.Box("mvex", Mp4Builder.FullBox("trex", 0, 0, new byte[20])));
        AssertDeclines(b, "mvex");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Moof_Declines()
    {
        var b = new Mp4Builder();
        b.TopLevelAfterMoov.Add(Mp4Builder.Box("moof", Mp4Builder.FullBox("mfhd", 0, 0, new byte[4])));
        b.TopLevelAfterMoov.Add(Mp4Builder.Box("mdat", new byte[20]));
        AssertDeclines(b, "moof");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void MoofBeforeMoov_Declines()
    {
        var file = Mp4Builder.Concat(
            Mp4Builder.Box("ftyp", Mp4Builder.Ascii("isom")),
            Mp4Builder.Box("moof", Mp4Builder.FullBox("mfhd", 0, 0, new byte[4])),
            BuildWithoutFtyp(new Mp4Builder()));
        Read(file, out var reason).Should().BeNull();
        reason.Should().Contain("moof");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Stps_Declines()
    {
        var b = new Mp4Builder();
        Video(b).StblExtras.Add(Mp4Builder.FullBox("stps", 0, 0, Mp4Builder.Concat(Mp4Builder.U32(1), Mp4Builder.U32(1))));
        AssertDeclines(b, "stps");
    }

    [Theory]
    [InlineData("rap ")]
    [InlineData("sync")]
    [Trait("serves-spec", "SPEC-004")]
    public void RandomAccessSampleGroups_Decline(string grouping)
    {
        var b = new Mp4Builder();
        Video(b).StblExtras.Add(Mp4Builder.FullBox("sgpd", 1, 0, Mp4Builder.Concat(Mp4Builder.Ascii(grouping), Mp4Builder.U32(1), Mp4Builder.U32(1), new byte[1])));
        AssertDeclines(b, "random-access");

        var b2 = new Mp4Builder();
        Video(b2).StblExtras.Add(Mp4Builder.FullBox("sbgp", 0, 0, Mp4Builder.Concat(Mp4Builder.Ascii(grouping), Mp4Builder.U32(0))));
        AssertDeclines(b2, "random-access");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void TwoMediaEdits_Decline()
    {
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (1500UL, 0L, RateOne), (1500UL, 45000L, RateOne) };
        AssertDeclines(b, "more than one media edit");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EmptyEditThenTwoMediaEdits_Decline()
    {
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (500UL, -1L, RateOne), (1500UL, 0L, RateOne), (1500UL, 45000L, RateOne) };
        AssertDeclines(b, "edit list with 3 entries");
    }

    [Theory]
    [InlineData(0x8000u)]  // 0.5
    [InlineData(0u)]
    [InlineData(0x00020000u)]
    [Trait("serves-spec", "SPEC-004")]
    public void MediaRateOtherThanOne_Declines(uint rate)
    {
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (3000UL, 0L, rate) };
        AssertDeclines(b, "rate");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EmptyEditAlone_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (500UL, -1L, RateOne) };
        AssertDeclines(b, "empty edit");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void MediaEditWithNoDuration_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (0UL, 0L, RateOne) };
        AssertDeclines(b, "no duration");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void NegativeMediaTimeOtherThanMinusOne_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (3000UL, -2L, RateOne) };
        AssertDeclines(b, "media time");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EditListEndingBeforeALastKeyframe_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (1500UL, 0L, RateOne) }; // 1.5 s of a track whose last keyframe is at 2 s
        AssertDeclines(b, "end of the edit");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EditListStartingAfterTheLastSample_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Edits = new[] { (3000UL, 10_000_000L, RateOne) };
        AssertDeclines(b, "after the last sample");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void NegativeCompositionOffset_V1_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Ctts = new[] { (45u, 1000), (45u, -1000) };
        Video(b).CttsVersion = 1;
        AssertDeclines(b, "negative composition offset");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void CompositionOffsetWithTheHighBitSet_V0_Declines()
    {
        // ffmpeg reads a version 0 offset as signed: 0x80000000 is negative.
        var b = new Mp4Builder();
        Video(b).Ctts = new[] { (90u, int.MinValue) };
        Video(b).CttsVersion = 0;
        AssertDeclines(b, "negative composition offset");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void TwoSampleEntries_Decline()
    {
        var b = new Mp4Builder();
        Video(b).EntryCount = 2;
        AssertDeclines(b, "2 sample entries");
    }

    [Theory]
    [InlineData("mp4v")]
    [InlineData("vp09")]
    [InlineData("av01")]
    [InlineData("jpeg")]
    [InlineData("encv")]
    [Trait("serves-spec", "SPEC-004")]
    public void OtherCodecs_Decline(string entry)
    {
        var b = new Mp4Builder();
        Video(b).Entry = entry;
        AssertDeclines(b, "sample entry");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void FirstVideoTrackWithAnMp4vEntry_DeclinesAndNeverSkipsToTheAvc1TrackBehindIt()
    {
        var b = new Mp4Builder();
        Video(b).Entry = "mp4v";
        b.Tracks.Add(new Mp4Track()); // a perfectly good avc1 track
        AssertDeclines(b, "mp4v");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void CoverArt_Declines()
    {
        var b = new Mp4Builder();
        var covr = Mp4Builder.Box("covr", Mp4Builder.Box("data", new byte[16]));
        var ilst = Mp4Builder.Box("ilst", covr);
        var meta = Mp4Builder.FullBox("meta", 0, 0, ilst);
        b.MoovExtras.Add(Mp4Builder.Box("udta", meta));
        AssertDeclines(b, "covr");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void CoverArtInFrontOfTheTracks_Declines()
    {
        // The cover becomes stream 0 in ffmpeg when its box comes before the tracks: v:0 would be the picture.
        var b = new Mp4Builder();
        b.MoovPrefix.Add(Mp4Builder.Box("udta", Mp4Builder.Box("meta", Mp4Builder.Box("ilst", Mp4Builder.Box("covr", new byte[8])))));
        AssertDeclines(b, "covr");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void SingleKeyframe_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Stss = new uint[] { 1 };
        AssertDeclines(b, "fewer than two");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EmptyStss_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Stss = Array.Empty<uint>();
        AssertDeclines(b, "fewer than two");
    }

    [Theory]
    [InlineData(new uint[] { 1, 61, 31 })]       // not ascending
    [InlineData(new uint[] { 1, 31, 31 })]       // repeated
    [InlineData(new uint[] { 1, 31, 91 })]       // beyond the 90 samples
    [InlineData(new uint[] { 0, 31, 61 })]       // sample numbers count from 1
    [Trait("serves-spec", "SPEC-004")]
    public void StssThatIsNotStrictlyAscendingWithinTheSampleCount_Declines(uint[] stss)
    {
        var b = new Mp4Builder();
        Video(b).Stss = stss;
        AssertDeclines(b, "stss");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void SttsAndStszDisagreeingOnTheSampleCount_Declines()
    {
        var b = new Mp4Builder();
        Video(b).SampleCount = 91;
        AssertDeclines(b, "stts and stsz");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void CttsAndStszDisagreeingOnTheSampleCount_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Ctts = new[] { (89u, 0) };
        AssertDeclines(b, "ctts and stsz");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void SyncSamplesWhoseCompositionTimesDoNotIncrease_Decline()
    {
        var b = new Mp4Builder();
        Video(b).Stts = new[] { (90u, 0u) }; // every sample at time 0
        AssertDeclines(b, "do not increase");
    }

    [Theory]
    [InlineData("stsz")]
    [InlineData("stsc")]
    [InlineData("stco")]
    [InlineData("stsd")]
    [Trait("serves-spec", "SPEC-004")]
    public void IncompleteSampleTable_Declines(string missing)
    {
        var b = new Mp4Builder();
        var video = Video(b);
        switch (missing)
        {
            case "stsz":
                video.WithStsz = false;
                break;
            case "stsc":
                video.WithStsc = false;
                break;
            case "stco":
                video.WithStco = false;
                break;
            default:
                video.EntryCount = 0;
                break;
        }

        AssertDeclines(b, missing == "stsd" ? "stsd" : "incomplete");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void CompactSampleSizes_Stz2_Decline()
    {
        var b = new Mp4Builder();
        Video(b).WithStsz = false;
        Video(b).StblExtras.Add(Mp4Builder.FullBox("stz2", 0, 0, new byte[8]));
        AssertDeclines(b, "stz2");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void CompositionShift_Cslg_Declines()
    {
        var b = new Mp4Builder();
        Video(b).StblExtras.Add(Mp4Builder.FullBox("cslg", 0, 0, new byte[20]));
        AssertDeclines(b, "cslg");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void TrackTimeScaleZero_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Timescale = 0;
        AssertDeclines(b, "time scale");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void TwoStbl_OrTwoStts_Decline()
    {
        var b = new Mp4Builder();
        Video(b).StblExtras.Add(Mp4Builder.FullBox("stts", 0, 0, Mp4Builder.Concat(Mp4Builder.U32(0))));
        AssertDeclines(b, "more than one stts");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void NoVideoTrack_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Handler = "soun";
        Video(b).Entry = "mp4a";
        AssertDeclines(b, "no video track");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void NoMoov_Declines()
    {
        var file = Mp4Builder.Concat(Mp4Builder.Box("ftyp", Mp4Builder.Ascii("isom")), Mp4Builder.Box("mdat", new byte[100]));
        Read(file, out var reason).Should().BeNull();
        reason.Should().Contain("no moov");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void TwoMoovBoxes_Decline()
    {
        var builder = new Mp4Builder { MoovFirst = true };
        var file = Mp4Builder.Concat(builder.Build(), builder.BuildMoov());
        Read(file, out var reason).Should().BeNull();
        reason.Should().Contain("more than one moov");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("serves-spec", "SPEC-004")]
    public void ChunkOffsetBeyondTheEndOfTheFile_IsATruncatedFile_Declines(bool co64)
    {
        // The offsets say the media data starts 4 GB in, in a file of a few KB: whatever cut it, the table describes bytes
        // that are not there.
        var b = new Mp4Builder();
        Video(b).UseCo64 = co64;
        Video(b).ChunkOffsetOverride = 4_000_000_000UL;
        AssertDeclines(b, "beyond the end of the file");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Co64OffsetsBeyond4GB_AreReadWhenTheFileIsThatLong()
    {
        // A file of 4.5 GB, as the bench's 10-minute fixture: the last chunk at 4.4 GB, in 64-bit offsets.
        var b = new Mp4Builder { MdatBodyBytes = 0 };
        Video(b).UseCo64 = true;
        Video(b).ChunkOffsetOverride = 4_400_000_000UL;
        var moov = b.BuildMoov();
        var ftyp = Mp4Builder.Box("ftyp", Mp4Builder.Ascii("isom"));
        var stream = new VirtualFileStream();
        stream.Add(ftyp).Add(Mp4Builder.Concat(Mp4Builder.U32(1), Mp4Builder.Ascii("mdat"), Mp4Builder.U64(4_500_000_000UL))).AddZeros(4_500_000_000L - 16).Add(moov);
        var read = IsoBmffKeyframeIndex.TryRead(() => stream, out var reason);
        read.Should().NotBeNull(reason);
        read!.Keyframes.Should().Equal(ZeroOneTwo);
        stream.BytesRead.Should().BeLessThan(moov.Length + 200);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void BoxLargerThanItsParent_Declines()
    {
        var b = new Mp4Builder();
        // A trak that claims one byte more than the moov holds.
        var trak = Mp4Builder.Box("trak", new byte[16]);
        BitConverterWriteSize(trak, trak.Length + 1000);
        b.MoovExtras.Add(trak);
        AssertDeclines(b, "overruns");
    }

    private static void BitConverterWriteSize(byte[] box, int size) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void BoxSmallerThanItsOwnHeader_Declines()
    {
        var b = new Mp4Builder();
        var junk = Mp4Builder.Box("junk", new byte[16]);
        BitConverterWriteSize(junk, 4);
        b.MoovExtras.Add(junk);
        AssertDeclines(b, "overruns");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void TopLevelBoxRunningPastTheEndOfTheFile_IsATruncatedFile_Declines()
    {
        var file = new Mp4Builder().Build();
        var cut = file.AsSpan(0, file.Length - 20).ToArray(); // the moov is cut short
        Read(cut, out var reason).Should().BeNull();
        reason.Should().Contain("truncated");

        var mdatCut = new Mp4Builder { MoovFirst = true, MdatBodyBytes = 5000 }.Build();
        Read(mdatCut.AsSpan(0, mdatCut.Length - 100).ToArray(), out reason).Should().BeNull();
        reason.Should().Contain("truncated");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(40)]
    [Trait("serves-spec", "SPEC-004")]
    public void FileCutAnywhereInItsFirstBytes_DeclinesWithoutThrowing(int length)
    {
        var file = new Mp4Builder().Build();
        Read(file.AsSpan(0, length).ToArray(), out var reason).Should().BeNull();
        reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EveryTruncationOfASmallFile_DeclinesOrReadsTheSameList_NeverThrows()
    {
        // Cut the file at every length: whatever remains is either declined or still a whole file, never an exception.
        var file = new Mp4Builder { MoovFirst = true }.Build();
        for (var length = 0; length <= file.Length; length++)
        {
            var read = Read(file.AsSpan(0, length).ToArray(), out var reason);
            if (read is not null)
            {
                length.Should().Be(file.Length, "only the whole file reads");
            }
            else
            {
                reason.Should().NotBeNullOrEmpty();
            }
        }
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void EveryBitFlipInTheMoov_NeverThrows()
    {
        // A corrupt moov: flip each byte; the reader reads, declines, never throws. (A flip in a time value changes the list,
        // which is why a corrupt table is checked against ffprobe at both ends before it is used.)
        var file = new Mp4Builder { MoovFirst = true }.Build();
        for (var i = 0; i < file.Length; i++)
        {
            var copy = (byte[])file.Clone();
            copy[i] ^= 0xFF;
            var act = () => Read(copy, out _);
            act.Should().NotThrow($"flipping byte {i} must decline or read, never throw");
        }
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void MoovTooManyKeyframes_Declines()
    {
        var b = new Mp4Builder();
        Video(b).Stts = new[] { ((uint)IsoBmffKeyframeIndex.MaxKeyframes + 1, 1u) };
        Video(b).Stss = null;
        AssertDeclines(b, "sync samples");
    }

    // ---- Big files: bytes read ----------------------------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void MoovLargerThan256MB_Declines_WithoutReadingIt()
    {
        var stream = new VirtualFileStream();
        var ftyp = Mp4Builder.Box("ftyp", Mp4Builder.Ascii("isom"));
        const long moovBody = IsoBmffKeyframeIndex.MaxMoovBytes + 1;
        stream.Add(ftyp).Add(Mp4Builder.Concat(Mp4Builder.U32(1), Mp4Builder.Ascii("moov"), Mp4Builder.U64((ulong)(moovBody + 16)))).AddZeros(moovBody);

        var read = IsoBmffKeyframeIndex.TryRead(() => stream, out var reason);
        read.Should().BeNull();
        reason.Should().Contain("moov larger than");
        stream.BytesRead.Should().BeLessThan(64, "only the box headers were read, not the moov");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void FileOf60MB_WithMoovAtTheEnd_ReadsAtMostMoovPlus64KiB()
    {
        var movie = new Mp4Builder { MdatBodyBytes = 0 }.Build();
        var ftypLength = Mp4Builder.Box("ftyp", Mp4Builder.Concat(Mp4Builder.Ascii("isom"), Mp4Builder.U32(512), Mp4Builder.Ascii("isom"), Mp4Builder.Ascii("avc1"))).Length;
        var mdatLength = Mp4Builder.Box("mdat", Array.Empty<byte>()).Length;
        var moov = movie.AsSpan(ftypLength + mdatLength).ToArray();

        const long mdatBody = 60L * 1024 * 1024;
        var stream = new VirtualFileStream();
        stream.Add(movie.AsSpan(0, ftypLength).ToArray())
            .Add(Mp4Builder.Concat(Mp4Builder.U32(1), Mp4Builder.Ascii("mdat"), Mp4Builder.U64((ulong)mdatBody + 16)))
            .AddZeros(mdatBody)
            .Add(moov);
        stream.Length.Should().BeGreaterThan(50L * 1024 * 1024);

        var read = IsoBmffKeyframeIndex.TryRead(() => stream, out var reason);
        read.Should().NotBeNull(reason);
        read!.Keyframes.Should().Equal(ZeroOneTwo);
        stream.BytesRead.Should().BeLessThanOrEqualTo(moov.Length + (64 * 1024));
        read.BytesRead.Should().Be(stream.BytesRead);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void MoovOf5MB_IsReadWhole_AndNoMoreThanItsSizePlusHeaders()
    {
        // stsz carries 5 MB of per-sample sizes: a long recording's moov, far bigger than any first-bytes window.
        var b = new Mp4Builder { MdatBodyBytes = 0 };
        Video(b).StszPaddingBytes = 5 * 1024 * 1024;
        var file = b.Build();
        var stream = new VirtualFileStream().Add(file);
        var read = IsoBmffKeyframeIndex.TryRead(() => stream, out var reason);
        read.Should().NotBeNull(reason);
        read!.Keyframes.Should().Equal(ZeroOneTwo);
        stream.BytesRead.Should().BeLessThanOrEqualTo(file.Length);
        stream.BytesRead.Should().BeGreaterThan(5 * 1024 * 1024);
    }

    // ---- Never in the way of the file ---------------------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void OpenThatThrowsIOException_IsADecline()
    {
        var read = IsoBmffKeyframeIndex.TryRead(() => throw new IOException("sharing violation"), out var reason);
        read.Should().BeNull();
        reason.Should().Contain("IOException");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void OpenThatThrowsUnauthorizedAccess_IsADecline()
    {
        var read = IsoBmffKeyframeIndex.TryRead(() => throw new UnauthorizedAccessException("denied"), out var reason);
        read.Should().BeNull();
        reason.Should().Contain("UnauthorizedAccessException");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void ReadThatThrowsIOExceptionHalfWay_IsADecline()
    {
        var stream = new VirtualFileStream().Add(new Mp4Builder { MoovFirst = true }.Build());
        var reads = 0;
        stream.OnRead = (_, _) =>
        {
            if (++reads == 3)
            {
                throw new IOException("the disk went away");
            }
        };

        var read = IsoBmffKeyframeIndex.TryRead(() => stream, out var reason);
        read.Should().BeNull();
        reason.Should().Contain("IOException");
        stream.Disposed.Should().BeTrue("the stream is disposed even when the read fails");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void StreamThatCannotSeek_IsADecline()
    {
        var read = IsoBmffKeyframeIndex.TryRead(() => new NonSeekableStream(new Mp4Builder().Build()), out var reason);
        read.Should().BeNull();
        reason.Should().Contain("seekable");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void StreamIsDisposed_OnSuccess_AndOnDecline()
    {
        var ok = new VirtualFileStream().Add(new Mp4Builder().Build());
        IsoBmffKeyframeIndex.TryRead(() => ok, out _).Should().NotBeNull();
        ok.Disposed.Should().BeTrue();

        var bad = new VirtualFileStream().Add(new Mp4Builder { WithFtyp = false }.Build());
        IsoBmffKeyframeIndex.TryRead(() => bad, out _).Should().BeNull();
        bad.Disposed.Should().BeTrue();
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void CancellationDuringTheRead_Throws_NotADecline()
    {
        using var cts = new CancellationTokenSource();
        var stream = new VirtualFileStream().Add(new Mp4Builder { MoovFirst = true }.Build());
        var reads = 0;
        stream.OnRead = (_, _) =>
        {
            if (++reads == 2)
            {
                cts.Cancel();
            }
        };

        var act = () => IsoBmffKeyframeIndex.TryRead(() => stream, out _, cts.Token);
        act.Should().Throw<OperationCanceledException>();
        stream.Disposed.Should().BeTrue();
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void CancellationBeforeTheRead_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => IsoBmffKeyframeIndex.TryRead(() => new MemoryStream(new Mp4Builder().Build()), out _, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public NonSeekableStream(byte[] bytes)
            : base(bytes)
        {
        }

        public override bool CanSeek => false;
    }
}
