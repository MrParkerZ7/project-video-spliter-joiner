using System.Diagnostics;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Integration tests that exercise the real ffmpeg binary via a configured override.
/// Each test guards with <see cref="FfmpegTestBinaries.SkipIfMissing"/> so a machine
/// without ffmpeg stays green (the test no-ops and logs a skip line).
/// </summary>
public class FfmpegRunnerIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public FfmpegRunnerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static FfmpegRunner MakeRunner() =>
        new(new FfmpegBinaryLocator(ffmpegOverride: FfmpegTestBinaries.Ffmpeg));

    [SkippableFact]
    public async Task Version_Succeeds()
    {
        if (FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg"))
        {
            return;
        }

        var runner = MakeRunner();
        var args = FfmpegArgs.ForFfmpeg().Raw("-version");

        var result = await runner.RunAsync(args);

        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);
    }

    [SkippableFact]
    public async Task BadArg_ReturnsFailureResult_NoException()
    {
        if (FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg"))
        {
            return;
        }

        var runner = MakeRunner();
        var args = FfmpegArgs.ForFfmpeg().Raw("-this_is_not_a_real_flag_zzz");

        var result = await runner.RunAsync(args);

        result.Success.Should().BeFalse();
        result.ExitCode.Should().NotBe(0);
        result.StdErrTail.Should().NotBeEmpty();
    }

    [SkippableFact]
    public async Task Cancellation_KillsProcessTree_WithinTimeout()
    {
        if (FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg"))
        {
            return;
        }

        var runner = MakeRunner();
        // A long no-op that runs ~30s of WALL-CLOCK time if left alone. -re forces ffmpeg to
        // consume the lavfi source at native frame rate; without it the null muxer would burn
        // through 30 virtual seconds in milliseconds and finish before cancellation fires.
        var args = FfmpegArgs.ForFfmpeg()
            .Raw("-re", "-f", "lavfi", "-i", "testsrc=duration=30:size=64x64:rate=10", "-f", "null", "-");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var sw = Stopwatch.StartNew();
        Func<Task> act = async () => await runner.RunAsync(args, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "cancellation should return promptly, not run the full 30s job");
    }
}
