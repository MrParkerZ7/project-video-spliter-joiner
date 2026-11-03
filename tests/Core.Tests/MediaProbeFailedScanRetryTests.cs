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

    private static async Task AssertRetryScansAgainAsync()
    {
        var runner = new SwitchableRunner();
        var probe = new MediaProbe(runner)
        {
            // Hold the registering call until the scan has finished (its cleanup included): the worst ordering.
            AfterScanStartedForTest = scan => ((IAsyncResult)scan).AsyncWaitHandle.WaitOne(),
        };
        var tmp = Path.Combine(Path.GetTempPath(), "vsj-t239-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(tmp, "placeholder");
        try
        {
            var failed = async () => await probe.GetKeyframesAsync(tmp);
            await failed.Should().ThrowAsync<FfprobeException>();

            runner.Fail = false;
            var callsAfterFailure = runner.Calls;

            var result = await probe.GetKeyframesAsync(tmp);

            result.Should().Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1) }, "the retry starts a new scan");
            runner.Calls.Should().BeGreaterThan(callsAfterFailure, "a failed scan is never served to a later call");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public Task GetKeyframesAsync_ScanFailsBeforeRegistrationCompletes_RetryScansAgain() => AssertRetryScansAgainAsync();

    [Fact]
    public async Task GetKeyframesAsync_ScanFailsBeforeRegistrationCompletes_RetryScansAgain_100Repeats()
    {
        for (var i = 0; i < 100; i++)
        {
            await AssertRetryScansAgainAsync();
        }
    }
}
