using System.Diagnostics;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-031 — faster keyframe scan via demux-level packet flags, with a frame-scan fallback.
/// Covers: pure K-flag parsing (always runs), packet-path correctness + packet/frame parity +
/// old-vs-new timing on a real fixture (guard-skips without ffmpeg), and the empty-packet fallback
/// (pure unit via a query-aware fake runner).
/// </summary>
[Collection(MediaFixturesCollection.Name)]
public class MediaProbeKeyframePacketTests
{
    private readonly MediaFixtures _fixtures;
    private readonly ITestOutputHelper _output;

    public MediaProbeKeyframePacketTests(MediaFixtures fixtures, ITestOutputHelper output)
    {
        _fixtures = fixtures;
        _output = output;
    }

    private static MediaProbe MakeProbe() =>
        new(new FfprobeRunner(new FfmpegBinaryLocator(ffprobeOverride: FfmpegTestBinaries.Ffprobe)));

    private bool ShouldSkip()
    {
        if (FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe"))
        {
            return true;
        }

        return FfmpegTestBinaries.SkipIfMissing(_output, MediaFixtures.FfmpegAvailable, "ffmpeg");
    }

    // ---- Flag parsing (pure — always runs) ---------------------------------------------------

    [Theory]
    [InlineData("K__", true)]   // keyframe, ffprobe 3-char form
    [InlineData("K_", true)]    // keyframe, ffprobe 2-char form
    [InlineData("K", true)]
    [InlineData("KD_", true)]   // keyframe + discard marker
    [InlineData("___", false)]  // non-keyframe
    [InlineData("__", false)]
    [InlineData("_D_", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKeyframeFlag_DetectsKMarker(string? flags, bool expected)
    {
        MediaProbe.IsKeyframeFlag(flags).Should().Be(expected);
    }

    // ---- Correctness on a known-GOP fixture (packet path) ------------------------------------

    [Fact]
    public async Task GetKeyframesAsync_PacketPath_KnownGopFixture_ReturnsWholeSecondKeyframes()
    {
        if (ShouldSkip())
        {
            return;
        }

        var probe = MakeProbe();
        var kf = await probe.GetKeyframesAsync(_fixtures.VideoOnlyPath);

        // Fixture is 10s @ 30fps, GOP 30 (1s) → keyframes at ~0,1,2,…,10.
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets, "the fast demux path must be used when packets carry keyframe flags");
        kf.Should().NotBeEmpty();
        kf.Should().BeInAscendingOrder();
        kf.Count.Should().BeInRange(10, 12);
        kf[0].Should().BeCloseTo(TimeSpan.Zero, TimeSpan.FromMilliseconds(50));

        // Each keyframe should land on (or very near) a whole second.
        foreach (var t in kf)
        {
            var nearestWholeSecond = Math.Round(t.TotalSeconds);
            Math.Abs(t.TotalSeconds - nearestWholeSecond).Should().BeLessThan(0.05,
                "GOP-30 keyframes land on whole seconds");
        }
    }

    // ---- Parity: packet path == frame path on the same fixture -------------------------------

    [Fact]
    public async Task PacketScan_MatchesFrameScan_OnFixture()
    {
        if (ShouldSkip())
        {
            return;
        }

        var probe = MakeProbe();
        var packet = await probe.ScanKeyframesFromPacketsForTestAsync(_fixtures.VideoOnlyPath);
        var frame = await probe.ScanKeyframesFromFramesForTestAsync(_fixtures.VideoOnlyPath);

        packet.Should().NotBeEmpty();
        frame.Should().NotBeEmpty();
        packet.Count.Should().Be(frame.Count, "both queries must find the same keyframe count");

        // Same sorted timestamps within a small tolerance (pts vs best-effort ts rounding).
        for (var i = 0; i < packet.Count; i++)
        {
            packet[i].Should().BeCloseTo(frame[i], TimeSpan.FromMilliseconds(5));
        }
    }

    // ---- Measurement: old frame scan vs new packet scan on a 4K clip -------------------------

    [Fact]
    public async Task PacketScan_FasterThanFrameScan_On4K_WithMatchingCounts()
    {
        if (ShouldSkip())
        {
            return;
        }

        var probe = MakeProbe();
        var path = _fixtures.ScanCost4kPath;

        // Warm ffprobe/OS file cache once so we time the scan itself, not first-touch disk I/O.
        _ = await probe.ScanKeyframesFromPacketsForTestAsync(path);

        var swPacket = Stopwatch.StartNew();
        var packet = await probe.ScanKeyframesFromPacketsForTestAsync(path);
        swPacket.Stop();

        var swFrame = Stopwatch.StartNew();
        var frame = await probe.ScanKeyframesFromFramesForTestAsync(path);
        swFrame.Stop();

        packet.Count.Should().Be(frame.Count, "keyframe-count parity between the two paths");
        packet.Should().NotBeEmpty();

        _output.WriteLine($"[T-031] fixture: 20s 4K (3840x2160), GOP {MediaFixtures.ScanCostGopFrames}");
        _output.WriteLine($"[T-031] OLD frame-scan:  {swFrame.ElapsedMilliseconds} ms, {frame.Count} keyframes");
        _output.WriteLine($"[T-031] NEW packet-scan: {swPacket.ElapsedMilliseconds} ms, {packet.Count} keyframes");
        var ratio = swPacket.ElapsedMilliseconds == 0 ? double.NaN : (double)swFrame.ElapsedMilliseconds / swPacket.ElapsedMilliseconds;
        _output.WriteLine($"[T-031] speedup (frame/packet): {ratio:F2}x");

        // Not asserted as a hard threshold (CI-box variance), but the demux scan should not be slower
        // than the decode scan on a 4K clip — a generous guard that still catches a regression.
        swPacket.ElapsedMilliseconds.Should().BeLessThanOrEqualTo(
            swFrame.ElapsedMilliseconds + 50,
            "the demux packet scan must not be slower than the decode frame scan on 4K");
    }

    // ---- Fallback: empty packets → frame path (pure unit, query-aware fake) ------------------

    [Fact]
    public async Task GetKeyframesAsync_EmptyPackets_FallsBackToFrameScan()
    {
        const string emptyPackets = """{"packets":[]}""";
        const string framesJson = """
            {"frames":[
              {"media_type":"video","key_frame":1,"pts_time":"0.000000"},
              {"media_type":"video","key_frame":1,"pts_time":"1.000000"},
              {"media_type":"video","key_frame":1,"pts_time":"2.000000"}
            ]}
            """;
        var fake = new QueryAwareFfprobeRunner(packetsPayload: emptyPackets, framesPayload: framesJson);
        var probe = new MediaProbe(fake);

        var tmp = Path.Combine(Path.GetTempPath(), "vsj-fallback-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllTextAsync(tmp, "placeholder");
        try
        {
            var kf = await probe.GetKeyframesAsync(tmp);

            probe.LastScanPath.Should().Be(KeyframeScanPath.Frames, "an empty packet result must trigger the frame fallback");
            kf.Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            fake.PacketCallCount.Should().Be(1, "the packet query is tried first");
            fake.FrameCallCount.Should().Be(1, "then the frame fallback runs");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task GetKeyframesAsync_PacketQueryThrows_FallsBackToFrameScan()
    {
        const string framesJson = """
            {"frames":[
              {"media_type":"video","key_frame":1,"pts_time":"0.000000"},
              {"media_type":"video","key_frame":1,"pts_time":"1.000000"}
            ]}
            """;
        var fake = new QueryAwareFfprobeRunner(packetsPayload: null, framesPayload: framesJson);
        var probe = new MediaProbe(fake);

        var tmp = Path.Combine(Path.GetTempPath(), "vsj-fallback2-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllTextAsync(tmp, "placeholder");
        try
        {
            var kf = await probe.GetKeyframesAsync(tmp);

            probe.LastScanPath.Should().Be(KeyframeScanPath.Frames, "a throwing packet query must trigger the frame fallback");
            kf.Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ---- Cache preserved on the packet path (query-aware fake, no binary) ---------------------

    [Fact]
    public async Task GetKeyframesAsync_PacketPath_SecondCall_FromCache()
    {
        const string packetsJson = """
            {"packets":[
              {"pts_time":"0.000000","dts_time":"0.000000","flags":"K__"},
              {"pts_time":"1.000000","dts_time":"1.000000","flags":"K__"},
              {"pts_time":"0.500000","dts_time":"0.500000","flags":"___"}
            ]}
            """;
        var fake = new QueryAwareFfprobeRunner(packetsPayload: packetsJson, framesPayload: "{}");
        var probe = new MediaProbe(fake);

        var tmp = Path.Combine(Path.GetTempPath(), "vsj-pcache-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllTextAsync(tmp, "placeholder");
        try
        {
            var first = await probe.GetKeyframesAsync(tmp);
            var second = await probe.GetKeyframesAsync(tmp);

            first.Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(1));
            second.Should().Equal(first);
            probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
            fake.PacketCallCount.Should().Be(1, "the second call must be served from cache");
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

/// <summary>
/// In-memory <see cref="IFfprobeRunner"/> that returns a different payload for the packet query vs
/// the frame query (distinguished by the presence of <c>-show_packets</c> in the args), and counts
/// each. A null packets payload makes the packet query THROW (to exercise the throw-fallback path).
/// </summary>
internal sealed class QueryAwareFfprobeRunner : IFfprobeRunner
{
    private readonly string? _packetsPayload;
    private readonly string _framesPayload;

    public QueryAwareFfprobeRunner(string? packetsPayload, string framesPayload)
    {
        _packetsPayload = packetsPayload;
        _framesPayload = framesPayload;
    }

    public int PacketCallCount { get; private set; }

    public int FrameCallCount { get; private set; }

    public Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
    {
        var isPacketQuery = args.ToList().Contains("-show_packets");
        if (isPacketQuery)
        {
            PacketCallCount++;
            if (_packetsPayload is null)
            {
                throw new FfprobeException(1, new[] { "simulated packet-query failure" });
            }

            return Task.FromResult(_packetsPayload);
        }

        FrameCallCount++;
        return Task.FromResult(_framesPayload);
    }
}
