using FluentAssertions;
using FluentAssertions.Execution;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-191 (G-059, SPEC-001 I44 / I69) - the two repro tests of the ticket, written to FAIL on the shipped engine and to pass on the
/// fixed one. They name only what the shipped code already has (no <c>SmartCutCost</c>, no slice constants), so the very same file
/// compiles against the code before the change: that is how the failure was seen (T-191 Build log).
///
/// <para><b>The defect.</b> The head put <c>-ss</c> AFTER <c>-i</c> (an output seek): ffmpeg decoded every frame from the start of the
/// file up to the cut and dropped it, so a 1.5 s head cost 4.8 s at an intro of 10.5 s, 22.8 s at 104.5 s and 130.7 s at 540.5 s of a
/// 10-minute 4K file (SmartCutArgsBuilder's own comment, ADR 0018 and SPEC-001 I44 all said "bounded by one GOP"). And every run was
/// started with null progress, so the row bar showed nothing during a 4K Exact cut.</para>
/// </summary>
public sealed class SmartCutHeadSeekAndProgressReproTests : IDisposable
{
    private readonly string _dir;

    public SmartCutHeadSeekAndProgressReproTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t191r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static MediaInfo H264Aac() => new(
        S(60),
        "mov,mp4,m4a,3gp,3g2,mj2",
        new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
        new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

    private sealed class GridProbe : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(ProbeResult.Success(H264Aac()));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TimeSpan>>(Enumerable.Range(0, 31).Select(i => S(i * 2)).ToList());

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
            throw new NotSupportedException("the smart cutter plans against the raw keyframes and never snaps");

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    /// <summary>
    /// A runner that acts like <see cref="FfmpegRunner"/> for progress: given a total duration AND a progress sink it reports the
    /// fractions of <c>time=</c> lines (quarter steps of the run), and it writes the output file the engine expects.
    /// </summary>
    private sealed class TimeReportingRunner : IFfmpegRunner
    {
        private readonly RecordedProgress? _row;

        public TimeReportingRunner(RecordedProgress? row = null) => _row = row;

        public List<(IReadOnlyList<string> Tokens, TimeSpan? Total)> Runs { get; } = new();

        /// <summary>How many values the row's progress held when each run started.</summary>
        public List<int> ValuesAtRunStart { get; } = new();

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var tokens = args.ToList().ToList();
            Runs.Add((tokens, totalDuration));
            ValuesAtRunStart.Add(_row?.Values.Count ?? 0);
            if (totalDuration is { Ticks: > 0 } && progress is not null)
            {
                foreach (var f in new[] { 0.25, 0.5, 0.75, 1.0 })
                {
                    progress.Report(f);
                }
            }

            File.WriteAllText(tokens[^1], "seg");
            return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
        }
    }

    private sealed class RecordedProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }

    // SPEC-001#I44 (T-191) - REPRO 1: the head's -ss comes BEFORE -i <input>. The shipped head put it after, an output seek that
    // decodes from the start of the file, so this fails there.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void HeadReencode_PutsTheSeekBeforeTheInput()
    {
        var tokens = SmartCutArgsBuilder.HeadReencode(
            @"C:\v\clip.mp4", S(104.5), S(106), H264Aac(), "libx264", "aac", @"C:\v\head.mp4").ToList().ToList();

        tokens.IndexOf("-ss").Should().BeLessThan(tokens.IndexOf("-i"),
            "-ss <start> before -i <input> is an input seek: the decode starts at the keyframe before the start, not at 0");
        tokens[tokens.IndexOf("-ss") + 1].Should().Be("104.5");
    }

    // SPEC-001#I69 (T-191) - REPRO 2: during the head run the row's progress gets at least three strictly increasing values. The
    // shipped engine started every run with null progress, so the row got a lone 0.5 after the head.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task TheRowsProgress_GetsAtLeastThreeStrictlyIncreasingValues_DuringTheHeadRun()
    {
        var input = Path.Combine(_dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var progress = new RecordedProgress();
        var runner = new TimeReportingRunner(progress);

        // 5 s on a 2 s grid: the head is [5, 6), the concat copies [6, end of file).
        var result = await new SmartCutEngine(runner, new GridProbe()).CutAsync(input, S(5), null, Path.Combine(_dir, "cut.mp4"), progress);

        result.FellBack.Should().BeFalse();
        runner.Runs.Should().HaveCount(2);
        using var scope = new AssertionScope("the row's progress during the head");
        runner.Runs[0].Total.Should().Be(S(1), "the head run is told its length (HeadEnd - Start), so ffmpeg's time= becomes a fraction");

        // Everything the row was told before the concat run started: the values of the head run.
        var duringHead = progress.Values.Take(runner.ValuesAtRunStart[1]).ToList();
        duringHead.Count.Should().BeGreaterThanOrEqualTo(3, "the row bar moves while the head encodes: " + string.Join(", ", progress.Values));
        duringHead.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        progress.Values.Should().BeInAscendingOrder("the fraction never goes backwards");
    }
}
