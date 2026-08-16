using System.Collections.Concurrent;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-093 — in-flight dedup on <see cref="MediaProbe.GetKeyframesAsync"/>. Two concurrent callers for
/// the SAME file must share ONE underlying ffprobe scan (no doubled "Preparing" wait when the split
/// starts before the load-time background scan finishes), and a caller that cancels its own await must
/// NOT tear down the shared scan for the other awaiter. Driven by a GATED fake <see cref="IFfprobeRunner"/>
/// so the in-flight window is held open deterministically and the underlying call count is asserted.
/// </summary>
public sealed class MediaProbeInFlightDedupTests
{
    // A packet payload with two keyframes (whole-second) + one non-key packet.
    private const string PacketsJson = """
        {"packets":[
          {"pts_time":"0.000000","dts_time":"0.000000","flags":"K__"},
          {"pts_time":"1.000000","dts_time":"1.000000","flags":"K__"},
          {"pts_time":"0.500000","dts_time":"0.500000","flags":"___"}
        ]}
        """;

    private static string NewTempFile(string tag)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"vsj-{tag}-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(tmp, "placeholder");
        return tmp;
    }

    // ---- (a) two concurrent GetKeyframesAsync for one path → the scan runs ONCE ----------------

    [Fact]
    public async Task GetKeyframesAsync_TwoConcurrentCallers_SamePath_ScanRunsOnce()
    {
        var fake = new GatedFfprobeRunner(PacketsJson);
        var probe = new MediaProbe(fake);
        var tmp = NewTempFile("dedup-once");

        try
        {
            // Start two concurrent scans for the SAME path. Neither can complete until the gate opens,
            // so the second caller must attach to the FIRST's in-flight task rather than starting its own.
            var callA = probe.GetKeyframesAsync(tmp);
            var callB = probe.GetKeyframesAsync(tmp);

            // Wait until the runner has actually been entered (the single shared scan is in flight),
            // then release it so both awaiters complete from the one underlying call.
            await fake.WaitUntilEntered();
            fake.ReleaseAll();

            var resultA = await callA;
            var resultB = await callB;

            fake.PacketCallCount.Should().Be(1, "both concurrent callers must share ONE underlying ffprobe scan");
            resultA.Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(1));
            resultB.Should().Equal(resultA, "both awaiters observe the same shared result");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task GetKeyframesAsync_SecondCallAfterFirstCompletes_ServedFromCache_NoRescan()
    {
        var fake = new GatedFfprobeRunner(PacketsJson);
        var probe = new MediaProbe(fake);
        var tmp = NewTempFile("dedup-cache");

        try
        {
            var first = probe.GetKeyframesAsync(tmp);
            await fake.WaitUntilEntered();
            fake.ReleaseAll();
            await first;

            // A later call for the same unchanged file hits the durable cache (in-flight entry gone).
            var second = await probe.GetKeyframesAsync(tmp);

            fake.PacketCallCount.Should().Be(1, "the completed result is cached; a later call must not re-scan");
            second.Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ---- (b) a cancelled caller doesn't break the shared scan for another awaiter --------------

    [Fact]
    public async Task GetKeyframesAsync_OneCallerCancels_SharedScanStillCompletesForOther()
    {
        var fake = new GatedFfprobeRunner(PacketsJson);
        var probe = new MediaProbe(fake);
        var tmp = NewTempFile("dedup-cancel");

        try
        {
            using var ctsA = new CancellationTokenSource();

            // Caller A rides the shared scan with a cancellable token; caller B rides it with no token.
            var callA = probe.GetKeyframesAsync(tmp, ctsA.Token);
            var callB = probe.GetKeyframesAsync(tmp);

            // The shared scan is in flight. Cancel A's OWN await — this must not cancel the shared scan.
            await fake.WaitUntilEntered();
            ctsA.Cancel();

            // A observes cancellation for itself.
            var actA = async () => await callA;
            await actA.Should().ThrowAsync<OperationCanceledException>("the cancelling caller's own await is cancelled");

            // Now release the shared scan — B must still get a valid result from the ONE scan.
            fake.ReleaseAll();
            var resultB = await callB;

            resultB.Should().Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1) },
                "a cancelled caller must not tear down the shared scan for the other awaiter");
            fake.PacketCallCount.Should().Be(1, "still exactly one underlying scan despite one caller cancelling");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ---- Success-only caching: a failed scan leaves nothing cached; a retry re-scans -----------

    [Fact]
    public async Task GetKeyframesAsync_ScanFails_NotCached_RetryReScans()
    {
        // First scan throws on BOTH the packet query and the frame fallback → GetKeyframesAsync faults.
        var fake = new GatedFfprobeRunner(packetsPayload: null, framesPayload: null);
        var probe = new MediaProbe(fake);
        var tmp = NewTempFile("dedup-fail");

        try
        {
            var callFail = probe.GetKeyframesAsync(tmp);
            await fake.WaitUntilEntered();
            fake.ReleaseAll();

            var actFail = async () => await callFail;
            await actFail.Should().ThrowAsync<FfprobeException>("both queries failed → the scan faults");

            // The failed scan cached nothing and dropped its in-flight entry → a retry starts fresh.
            fake.SetPayloads(PacketsJson, null);
            fake.Reset();

            var retry = probe.GetKeyframesAsync(tmp);
            await fake.WaitUntilEntered();
            fake.ReleaseAll();
            var result = await retry;

            result.Should().Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1) }, "the retry re-scans cleanly");
            fake.PacketCallCount.Should().Be(1, "the retry runs a fresh scan (nothing was cached from the failure)");
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

/// <summary>
/// Gated in-memory <see cref="IFfprobeRunner"/> for the T-093 dedup tests. Every
/// <see cref="RunJsonAsync"/> signals it has been ENTERED, then blocks on a shared release gate until
/// <see cref="ReleaseAll"/> is called — so a test can prove that a SECOND concurrent
/// <see cref="MediaProbe.GetKeyframesAsync"/> attaches to the first's in-flight scan (the runner is
/// entered exactly once). Packet vs frame queries are distinguished by <c>-show_packets</c> and each
/// is counted. A null packets payload makes the packet query THROW; a null frames payload makes the
/// frame query THROW (so a scan can be forced to fault for the success-only-caching test).
/// </summary>
internal sealed class GatedFfprobeRunner : IFfprobeRunner
{
    private readonly object _lock = new();
    private string? _packetsPayload;
    private string? _framesPayload;

    private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GatedFfprobeRunner(string? packetsPayload, string? framesPayload = null)
    {
        _packetsPayload = packetsPayload;
        _framesPayload = framesPayload;
    }

    private int _packetCallCount;
    private int _frameCallCount;

    public int PacketCallCount => Volatile.Read(ref _packetCallCount);

    public int FrameCallCount => Volatile.Read(ref _frameCallCount);

    /// <summary>Awaits until the runner body has been entered at least once (a scan is in flight).</summary>
    public Task WaitUntilEntered() => _entered.Task;

    /// <summary>Opens the gate so every blocked (and future) call proceeds.</summary>
    public void ReleaseAll() => _release.TrySetResult();

    /// <summary>Swap the payloads for a subsequent scan (used by the retry test).</summary>
    public void SetPayloads(string? packetsPayload, string? framesPayload)
    {
        lock (_lock)
        {
            _packetsPayload = packetsPayload;
            _framesPayload = framesPayload;
        }
    }

    /// <summary>
    /// Re-arm the entered/release gates AND zero the call counters for a fresh in-flight window (used by
    /// the retry test) — so the count asserted after the retry reflects ONLY the retry's scan.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Volatile.Write(ref _packetCallCount, 0);
        Volatile.Write(ref _frameCallCount, 0);
    }

    public async Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
    {
        var isPacketQuery = args.ToList().Contains("-show_packets");

        // Signal entry + capture the release gate under the lock so Reset() swaps are seen coherently.
        Task release;
        lock (_lock)
        {
            _entered.TrySetResult();
            release = _release.Task;
        }

        // Block until the test opens the gate — holds the in-flight window open. The shared scan runs
        // on CancellationToken.None inside MediaProbe, so a caller's cancellation never reaches here.
        await release.ConfigureAwait(false);

        string? packets;
        string? frames;
        lock (_lock)
        {
            packets = _packetsPayload;
            frames = _framesPayload;
        }

        if (isPacketQuery)
        {
            Interlocked.Increment(ref _packetCallCount);
            if (packets is null)
            {
                throw new FfprobeException(1, new[] { "simulated packet-query failure" });
            }

            return packets;
        }

        Interlocked.Increment(ref _frameCallCount);
        if (frames is null)
        {
            throw new FfprobeException(1, new[] { "simulated frame-query failure" });
        }

        return frames;
    }
}
