using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-239 — a failed keyframe scan is never served to a later call. The scan used to rely on a
/// <c>Task.Yield</c> to order "GetOrAdd stores the Task" before the scan's own cleanup; when the scan failed
/// fast, the cleanup could run FIRST (removing nothing) and the failed Task was then stored and stayed
/// registered, so a retry for the same file got the old failure back. The ordering is forced here, not hoped
/// for: the <c>AfterScanStartedForTest</c> seam holds the registering call until the scan task has completed.
/// </summary>
public sealed class MediaProbeFailedScanRetryTests
{
    private const string PacketsJson = """
        {"packets":[
          {"pts_time":"0.000000","dts_time":"0.000000","flags":"K__"},
          {"pts_time":"1.000000","dts_time":"1.000000","flags":"K__"}
        ]}
        """;

    private sealed class SwitchableRunner : IFfprobeRunner
    {
        public bool Fail { get; set; } = true;

        public int Calls;

        public Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Fail
                ? Task.FromException<string>(new FfprobeException(1, new[] { "simulated failure" }))
                : Task.FromResult(args.ToList().Contains("-show_packets") ? PacketsJson : "{\"frames\":[]}");
        }
    }

    private static string NewTempFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "vsj-t239-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(tmp, "placeholder");
        return tmp;
    }

    private static async Task AssertRetryScansAgainAsync()
    {
        var runner = new SwitchableRunner();
        var seamCalls = 0;
        var probe = new MediaProbe(runner)
        {
            // Hold the registering call until the scan has finished (its cleanup included): the worst ordering.
            // Bounded, so a scan that never settles fails this assertion instead of hanging the test run.
            AfterScanStartedForTest = scan =>
            {
                Interlocked.Increment(ref seamCalls);
                ((IAsyncResult)scan).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10))
                    .Should().BeTrue("the shared scan settles");
            },
        };
        var tmp = NewTempFile();
        try
        {
            var failed = async () => await probe.GetKeyframesAsync(tmp);
            await failed.Should().ThrowAsync<FfprobeException>();

            runner.Fail = false;
            var callsAfterFailure = runner.Calls;

            var result = await probe.GetKeyframesAsync(tmp);

            result.Should().Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1) }, "the retry starts a new scan");
            runner.Calls.Should().BeGreaterThan(callsAfterFailure, "a failed scan is never served to a later call");
            seamCalls.Should().Be(2, "the seam ran once per call, so the forced ordering held for both the failure and the retry");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public Task GetKeyframesAsync_ScanFailsBeforeRegistrationCompletes_RetryScansAgain() => AssertRetryScansAgainAsync();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task GetKeyframesAsync_ScanFailsBeforeRegistrationCompletes_RetryScansAgain_100Repeats()
    {
        for (var i = 0; i < 100; i++)
        {
            await AssertRetryScansAgainAsync();
        }
    }

    // T-239 case coverage, Boundaries: "a caller cancelled while the scan fails".
    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task GetKeyframesAsync_OneCallerCancelsWhileTheScanFails_TheOtherGetsTheFailure_AndTheRetryScansAgain()
    {
        var fake = new GatedFfprobeRunner(packetsPayload: null, framesPayload: null);
        var probe = new MediaProbe(fake);
        var tmp = NewTempFile();
        try
        {
            using var ctsA = new CancellationTokenSource();
            var callA = probe.GetKeyframesAsync(tmp, ctsA.Token);
            var callB = probe.GetKeyframesAsync(tmp);

            await fake.WaitUntilEntered();
            ctsA.Cancel();

            var actA = async () => await callA;
            await actA.Should().ThrowAsync<OperationCanceledException>("the cancelling caller's own await is cancelled");

            // The scan, which nobody cancelled, now fails: the caller that stayed gets the failure, not a cancellation.
            fake.ReleaseAll();
            var actB = async () => await callB;
            await actB.Should().ThrowAsync<FfprobeException>("the scan's failure reaches the caller that stayed");

            // Nothing of the failed scan stays registered or cached: a retry starts a new scan and succeeds.
            fake.SetPayloads(PacketsJson, null);
            fake.Reset();
            var retry = probe.GetKeyframesAsync(tmp);
            await fake.WaitUntilEntered();
            fake.ReleaseAll();

            (await retry).Should().Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1) }, "the retry starts a new scan");
            fake.PacketCallCount.Should().Be(1, "the retry ran one fresh scan");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // T-239 case coverage, Boundaries: "the same file path in different case". The cache key is built from the
    // full path as typed, so the two spellings are two keys (on Windows they are one file, on a case-sensitive file
    // system two files). Either way a failed scan under one spelling is never served to a call under the other,
    // and the other spelling scans for itself.
    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task GetKeyframesAsync_FailureUnderOneSpellingOfAPath_IsNeverServedToTheOtherSpelling()
    {
        var runner = new SwitchableRunner();
        var probe = new MediaProbe(runner);
        var dir = Path.Combine(Path.GetTempPath(), "vsj-t239-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var upper = Path.Combine(dir, "CLIP.mp4");
        var lower = Path.Combine(dir, "clip.mp4");
        File.WriteAllText(upper, "placeholder");
        File.WriteAllText(lower, "placeholder");
        try
        {
            var failed = async () => await probe.GetKeyframesAsync(upper);
            await failed.Should().ThrowAsync<FfprobeException>();

            runner.Fail = false;
            var callsAfterFailure = runner.Calls;

            var result = await probe.GetKeyframesAsync(lower);

            result.Should().Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1) }, "the other spelling scans for itself");
            runner.Calls.Should().BeGreaterThan(callsAfterFailure, "the failure under one spelling is not served to the other");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
