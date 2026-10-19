using FluentAssertions;
using VideoSplitJoiner.Core.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-194 repro, written against the shipped code first: an MP4 is scanned by reading the whole file (<c>ffprobe -show_packets</c>
/// without <c>-read_intervals</c>), 100% of its bytes, before a cut can snap or a split can start. The fix reads the keyframes
/// from the file's own sample table and lets ffprobe check only the ends. Asserted on what ffprobe was asked for, never on time.
/// </summary>
[Collection(MediaFixturesCollection.Name)]
public class MediaProbeSampleTableReproTests
{
    private readonly SampleTableFixtures _samples;
    private readonly ITestOutputHelper _output;

    public MediaProbeSampleTableReproTests(SampleTableFixtures samples, ITestOutputHelper output)
    {
        _samples = samples;
        _output = output;
    }

    [SkippableFact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task Mp4_KeyframesComeFromTheSampleTable_WithoutAWholeFilePacketScan()
    {
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg");

        var path = _samples.H264Bframes; // H.264 with B-frames, moov at the end
        var runner = new ArgsRecordingFfprobeRunner();
        var probe = SampleTableTestSupport.NewProbe(runner);

        var keyframes = await probe.GetKeyframesAsync(path);

        foreach (var run in runner.Runs)
        {
            _output.WriteLine("ffprobe " + string.Join(' ', run));
        }

        runner.WholeFilePacketScans.Should().BeEmpty("an MP4's keyframes are in its sample table; no ffprobe run may read the whole file");
        runner.Runs.Should().HaveCount(2, "the only ffprobe runs are the two short windows that check the table at both ends");
        probe.LastScanPath.Should().Be(KeyframeScanPath.Index);

        // ...and the list is what the whole-file scan reports, element by element.
        var scanned = await probe.ScanKeyframesFromPacketsForTestAsync(path);
        keyframes.Should().Equal(scanned);
        keyframes.Count.Should().BeGreaterThan(5);
    }
}
