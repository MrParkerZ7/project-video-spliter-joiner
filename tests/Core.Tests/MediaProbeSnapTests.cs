using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Pure-unit tests for snapping, average-GOP, and cache behaviour — no binary required, always run.
/// </summary>
public class MediaProbeSnapTests
{
    private static MediaProbe MakeProbe(IFfprobeRunner? runner = null) =>
        new(runner ?? new FakeFfprobeRunner("{}"));

    private static IReadOnlyList<TimeSpan> KeyframesEverySecond(int count) =>
        Enumerable.Range(0, count).Select(i => TimeSpan.FromSeconds(i)).ToList();

    [Fact]
    public void Snap_Request1Point4_SnapsTo1_WithNegativeDelta()
    {
        var probe = MakeProbe();
        var kf = KeyframesEverySecond(11);

        var snap = probe.SnapToNearestKeyframe(kf, TimeSpan.FromSeconds(1.4));

        snap.Snapped.Should().Be(TimeSpan.FromSeconds(1.0));
        snap.Delta.Should().BeCloseTo(TimeSpan.FromSeconds(-0.4), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Snap_Request1Point6_SnapsTo2_WithPositiveDelta()
    {
        var probe = MakeProbe();
        var kf = KeyframesEverySecond(11);

        var snap = probe.SnapToNearestKeyframe(kf, TimeSpan.FromSeconds(1.6));

        snap.Snapped.Should().Be(TimeSpan.FromSeconds(2.0));
        snap.Delta.Should().BeCloseTo(TimeSpan.FromSeconds(0.4), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Snap_ExactMidpointTie_ResolvesToEarlierKeyframe()
    {
        var probe = MakeProbe();
        var kf = KeyframesEverySecond(11);

        // 1.5s is exactly between 1.0 and 2.0 — tie must resolve to the earlier (1.0).
        var snap = probe.SnapToNearestKeyframe(kf, TimeSpan.FromSeconds(1.5));

        snap.Snapped.Should().Be(TimeSpan.FromSeconds(1.0));
        snap.Delta.Should().Be(TimeSpan.FromSeconds(-0.5));
    }

    [Fact]
    public void Snap_RequestPastLast_ClampsToLastKeyframe()
    {
        var probe = MakeProbe();
        var kf = KeyframesEverySecond(11); // 0..10

        var snap = probe.SnapToNearestKeyframe(kf, TimeSpan.FromSeconds(999));

        snap.Snapped.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Snap_RequestBeforeFirst_ClampsToFirstKeyframe()
    {
        var probe = MakeProbe();
        var kf = KeyframesEverySecond(11);

        var snap = probe.SnapToNearestKeyframe(kf, TimeSpan.FromSeconds(-5));

        snap.Snapped.Should().Be(TimeSpan.Zero);
        snap.Delta.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Snap_UnsortedInput_StillPicksNearestAndPrefersEarlierOnTie()
    {
        var probe = MakeProbe();
        var kf = new List<TimeSpan>
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(1),
        };

        var snap = probe.SnapToNearestKeyframe(kf, TimeSpan.FromSeconds(1.5));

        snap.Snapped.Should().Be(TimeSpan.FromSeconds(1.0));
    }

    [Fact]
    public void Snap_EmptyKeyframes_Throws()
    {
        var probe = MakeProbe();
        var act = () => probe.SnapToNearestKeyframe(new List<TimeSpan>(), TimeSpan.Zero);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AverageGop_EverySecond_IsAboutOneSecond()
    {
        var probe = MakeProbe();
        var kf = KeyframesEverySecond(11); // 0..10 → 10 gaps of 1s each

        var gop = probe.AverageGop(kf);

        gop.Should().BeCloseTo(TimeSpan.FromSeconds(1.0), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void AverageGop_FewerThanTwo_ReturnsZero()
    {
        var probe = MakeProbe();

        probe.AverageGop(new List<TimeSpan>()).Should().Be(TimeSpan.Zero);
        probe.AverageGop(new[] { TimeSpan.FromSeconds(3) }).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task GetKeyframes_TwiceOnSameFile_UsesCache_OnlyOneFfprobeCall()
    {
        // A query-aware fake returns a packet payload for the T-031 packet query and counts
        // invocations, proving the second GetKeyframesAsync call is served from cache (no binary).
        const string packetsJson = """
            {"packets":[
              {"pts_time":"0.000000","dts_time":"0.000000","flags":"K__"},
              {"pts_time":"1.000000","dts_time":"1.000000","flags":"K__"},
              {"pts_time":"2.000000","dts_time":"2.000000","flags":"K__"}
            ]}
            """;
        var fake = new QueryAwareFfprobeRunner(packetsPayload: packetsJson, framesPayload: "{}");
        var probe = MakeProbe(fake);

        var tmp = Path.Combine(Path.GetTempPath(), "vsj-cache-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllTextAsync(tmp, "placeholder"); // real file → cache key resolves
        try
        {
            var first = await probe.GetKeyframesAsync(tmp);
            var second = await probe.GetKeyframesAsync(tmp);

            first.Should().Equal(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2));
            second.Should().Equal(first);
            fake.PacketCallCount.Should().Be(1, "the second call must be served from cache");
            fake.FrameCallCount.Should().Be(0, "the packet path succeeds, so no frame fallback runs");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // SPEC-004#I32 — the mean-spacing average is computed on DEFENSIVELY-SORTED input, so an
    // unsorted keyframe list still yields (max - min) / (count - 1) rather than a negative span
    // derived from the raw first/last entries.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public void AverageGop_UnsortedInput_UsesSortedSpan()
    {
        var probe = MakeProbe();

        // Raw order is 4s, 0s, 2s — last-minus-first would be -4s without the defensive sort.
        var gop = probe.AverageGop(new[]
        {
            TimeSpan.FromSeconds(4),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
        });

        gop.Should().Be(TimeSpan.FromSeconds(2), "(max - min) / (count - 1) = 4s / 2 regardless of input order");
        gop.Should().BeGreaterThan(TimeSpan.Zero, "an unsorted list must never produce a negative average GOP");
    }
}

/// <summary>
/// Minimal in-memory <see cref="IFfprobeRunner"/> that returns a canned payload and counts calls.
/// Used for pure-unit cache/parse tests that must run with no real binary present.
/// </summary>
internal sealed class FakeFfprobeRunner : IFfprobeRunner
{
    private readonly string _payload;

    public FakeFfprobeRunner(string payload)
    {
        _payload = payload;
    }

    public int CallCount { get; private set; }

    public Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(_payload);
    }
}
