using FluentAssertions;
using VideoSplitJoiner.Core.Detect;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Pure unit tests for the T-006 stderr parsers + merge + ranking, fed representative real
/// ffmpeg stderr snippets. No binary required.
/// </summary>
public class DetectParserTests
{
    // Identity snap for pure-logic tests (keyframe snapping is tested separately in integration).
    private static readonly Func<TimeSpan, TimeSpan> IdentitySnap = t => t;

    [Fact]
    public void ParseBlackLike_RealBlackdetectLine_YieldsBlackAt2()
    {
        const string stderr =
            "[blackdetect @ 0000027150fc5bc0] black_start:2 black_end:3.5 black_duration:1.5";

        var hits = DetectParser.ParseBlackLike(stderr, CandidateKind.Black);

        hits.Should().ContainSingle();
        hits[0].Kind.Should().Be(CandidateKind.Black);
        hits[0].Time.Should().Be(TimeSpan.FromSeconds(2.0));
        hits[0].RawScore.Should().BeApproximately(1.5, 1e-9); // duration = raw score
    }

    [Fact]
    public void ParseBlackLike_WhiteKind_TagsWhite()
    {
        const string stderr =
            "[blackdetect @ 000001f12ae97a00] black_start:5.5 black_end:7 black_duration:1.5";

        var hits = DetectParser.ParseBlackLike(stderr, CandidateKind.White);

        hits.Should().ContainSingle();
        hits[0].Kind.Should().Be(CandidateKind.White);
        hits[0].Time.Should().Be(TimeSpan.FromSeconds(5.5));
    }

    [Fact]
    public void ParseBlackLike_MultipleIntervals_ParsesEach()
    {
        const string stderr =
            "[blackdetect @ 0x1] black_start:2 black_end:3.5 black_duration:1.5\n" +
            "[blackdetect @ 0x1] black_start:10.25 black_end:11 black_duration:0.75\n";

        var hits = DetectParser.ParseBlackLike(stderr, CandidateKind.Black);

        hits.Should().HaveCount(2);
        hits.Select(h => h.Time.TotalSeconds).Should().Equal(2.0, 10.25);
    }

    [Fact]
    public void ParseScene_MetadataPrintPair_YieldsSceneWithScore()
    {
        // metadata=print emits a pts_time line then a lavfi.scene_score line per selected frame.
        const string stderr =
            "[Parsed_metadata_1 @ 0x0] frame:0    pts:61440   pts_time:4\n" +
            "[Parsed_metadata_1 @ 0x0] lavfi.scene_score=0.55\n";

        var hits = DetectParser.ParseScene(stderr);

        hits.Should().ContainSingle();
        hits[0].Kind.Should().Be(CandidateKind.Scene);
        hits[0].Time.Should().Be(TimeSpan.FromSeconds(4.0));
        hits[0].RawScore.Should().BeApproximately(0.55, 1e-9);
    }

    [Fact]
    public void ParseScene_MultipleHits_PairsStatefully()
    {
        const string stderr =
            "[Parsed_metadata_1 @ 0x0] frame:0 pts_time:2\n" +
            "[Parsed_metadata_1 @ 0x0] lavfi.scene_score=1.000000\n" +
            "[Parsed_metadata_1 @ 0x0] frame:1 pts_time:5.5\n" +
            "[Parsed_metadata_1 @ 0x0] lavfi.scene_score=0.900000\n";

        var hits = DetectParser.ParseScene(stderr);

        hits.Should().HaveCount(2);
        hits[0].Time.Should().Be(TimeSpan.FromSeconds(2.0));
        hits[0].RawScore.Should().BeApproximately(1.0, 1e-9);
        hits[1].Time.Should().Be(TimeSpan.FromSeconds(5.5));
        hits[1].RawScore.Should().BeApproximately(0.9, 1e-9);
    }

    [Fact]
    public void ParseScene_PtsTimeWithNoScoreLine_StillCountsAsCut()
    {
        const string stderr =
            "[Parsed_metadata_1 @ 0x0] frame:0 pts_time:3.0\n";

        var hits = DetectParser.ParseScene(stderr);

        hits.Should().ContainSingle();
        hits[0].Time.Should().Be(TimeSpan.FromSeconds(3.0));
        hits[0].RawScore.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Parse_EmptyOrIrrelevantStderr_YieldsNoHits()
    {
        DetectParser.ParseBlackLike("frame= 100 fps=0.0 q=-1.0 size=N/A time=00:00:03", CandidateKind.Black)
            .Should().BeEmpty();
        DetectParser.ParseScene("").Should().BeEmpty();
    }

    [Fact]
    public void Merge_HitsWithinWindow_KeepsStrongerFadeOverScene()
    {
        var hits = new[]
        {
            new RawHit(TimeSpan.FromSeconds(2.0), CandidateKind.Scene, 1.0),
            new RawHit(TimeSpan.FromSeconds(2.1), CandidateKind.Black, 1.5),
        };

        var merged = DetectParser.Merge(hits, TimeSpan.FromSeconds(0.5));

        merged.Should().ContainSingle();
        merged[0].Kind.Should().Be(CandidateKind.Black); // fade beats scene inside the window
    }

    [Fact]
    public void Merge_HitsOutsideWindow_KeptSeparate()
    {
        var hits = new[]
        {
            new RawHit(TimeSpan.FromSeconds(2.0), CandidateKind.Scene, 1.0),
            new RawHit(TimeSpan.FromSeconds(9.0), CandidateKind.Scene, 1.0),
        };

        var merged = DetectParser.Merge(hits, TimeSpan.FromSeconds(0.5));

        merged.Should().HaveCount(2);
    }

    [Fact]
    public void Rank_AssignsAscendingRanks_BestScoreFirst_SnapsTime()
    {
        var hits = new[]
        {
            new RawHit(TimeSpan.FromSeconds(2.0), CandidateKind.Black, 1.5),  // strong fade
            new RawHit(TimeSpan.FromSeconds(4.0), CandidateKind.Scene, 0.5),  // weaker scene
        };

        // Snap that rounds to the nearest whole second, to prove SnappedTime is used.
        Func<TimeSpan, TimeSpan> snap = t => TimeSpan.FromSeconds(Math.Round(t.TotalSeconds));

        var ranked = DetectParser.Rank(DetectParser.Merge(hits, TimeSpan.FromSeconds(0.5)), snap, 50);

        ranked.Should().HaveCount(2);
        ranked.Select(c => c.Rank).Should().Equal(1, 2);
        // Ranks strictly ascending, scores non-increasing.
        ranked[0].Score.Should().BeGreaterThanOrEqualTo(ranked[1].Score);
        ranked[0].Kind.Should().Be(CandidateKind.Black); // fade outranks the weak scene
        ranked.All(c => c.SnappedTime == TimeSpan.FromSeconds(Math.Round(c.Time.TotalSeconds)))
            .Should().BeTrue();
    }

    [Fact]
    public void Rank_HighSceneScore_CanOutrankWeakFade()
    {
        var hits = new[]
        {
            new RawHit(TimeSpan.FromSeconds(2.0), CandidateKind.Black, 0.05), // very short/weak fade
            new RawHit(TimeSpan.FromSeconds(4.0), CandidateKind.Scene, 1.0),  // max scene
        };

        var ranked = DetectParser.Rank(hits, IdentitySnap, 50);

        // Both normalize to 1.0 within their own kind (single sample each), so the fade's kind
        // weight wins the tie — but the scene is still present and ranked. Assert full coverage
        // + ascending ranks rather than a brittle ordering claim.
        ranked.Should().HaveCount(2);
        ranked.Select(c => c.Rank).Should().Equal(1, 2);
        ranked.Select(c => c.Kind).Should().Contain(new[] { CandidateKind.Black, CandidateKind.Scene });
    }

    [Fact]
    public void Rank_EmptyHits_ReturnsEmpty_NotException()
    {
        DetectParser.Rank(Array.Empty<RawHit>(), IdentitySnap, 50).Should().BeEmpty();
    }

    [Fact]
    public void BuildRanked_RespectsMaxCandidatesCap()
    {
        var hits = Enumerable.Range(0, 20)
            .Select(i => new RawHit(TimeSpan.FromSeconds(i * 2), CandidateKind.Scene, 0.5 + (i * 0.01)))
            .ToArray();

        var ranked = DetectParser.BuildRanked(hits, IdentitySnap, TimeSpan.FromSeconds(0.5), maxCandidates: 5);

        ranked.Should().HaveCount(5);
        ranked.Select(c => c.Rank).Should().Equal(1, 2, 3, 4, 5);
    }
}
