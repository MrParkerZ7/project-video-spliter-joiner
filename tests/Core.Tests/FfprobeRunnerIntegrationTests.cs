using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Integration tests for <see cref="FfprobeRunner"/> against the real ffprobe binary,
/// guarded so a machine without ffprobe stays green.
/// </summary>
public class FfprobeRunnerIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public FfprobeRunnerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static FfprobeRunner MakeRunner() =>
        new(new FfmpegBinaryLocator(ffprobeOverride: FfmpegTestBinaries.Ffprobe));

    [SkippableFact]
    public async Task RunJsonAsync_ShowVersions_ReturnsParseableJson()
    {
        if (FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe"))
        {
            return;
        }

        var runner = MakeRunner();
        var args = FfmpegArgs.ForFfprobe()
            .Raw("-print_format", "json", "-show_program_version");

        var json = await runner.RunJsonAsync(args);

        json.Should().NotBeNullOrWhiteSpace();
        json.Should().Contain("{", "ffprobe should emit JSON");
    }

    [SkippableFact]
    public async Task RunJsonAsync_BadArg_ThrowsFfprobeException()
    {
        if (FfmpegTestBinaries.SkipIfMissing(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe"))
        {
            return;
        }

        var runner = MakeRunner();
        var args = FfmpegArgs.ForFfprobe().Raw("-this_is_not_a_real_flag_zzz");

        Func<Task> act = async () => await runner.RunJsonAsync(args);

        (await act.Should().ThrowAsync<FfprobeException>())
            .Which.StdErrTail.Should().NotBeEmpty();
    }
}
