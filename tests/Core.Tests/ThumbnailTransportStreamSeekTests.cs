using System.Globalization;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-227 (G-059, SPEC-005 I34) - the bucketed hover on an MPEG-TS (.ts, .m2ts, .mts) whose probed format start time is known seeks early and
/// trims by absolute time: <c>-copyts -ss (t - lead) -i in -ss (start + t)</c>. Against a fake runner and a fake start-time source (no ffmpeg): the
/// gate, the exact tokens, the clamp at the start of the file, that the exact request and every other container keep today's args, the retry
/// with today's args, and that a doubt is never a throw. The real-media half is <see cref="FfmpegThumbnailServiceTransportStreamIntegrationTests"/>.
///
/// <para>Why: the mpegts demuxer's seek lands on the first keyframe AFTER the target, so an input <c>-ss</c> alone showed the next keyframe, up to one
/// GOP late. Why <c>-copyts</c>: without it the output <c>-ss</c> is counted from the landing point, and the frame is another one.</para>
/// </summary>
public sealed class ThumbnailTransportStreamSeekTests : IDisposable
{
    private const double Start = 1.443444;

    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "vsj-t227-" + Guid.NewGuid().ToString("N"));
    private readonly string _inputDir = Path.Combine(Path.GetTempPath(), "vsj-t227in-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var dir in new[] { _cacheRoot, _inputDir })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private string MakeInput(string name)
    {
        Directory.CreateDirectory(_inputDir);
        var input = Path.Combine(_inputDir, name);
        File.WriteAllText(input, "placeholder");
        return input;
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private FfmpegThumbnailService NewService(IFfmpegRunner runner, Func<string, CancellationToken, Task<TimeSpan?>>? startTimeOf) =>
        new(runner, _cacheRoot, TimeSpan.FromSeconds(1), 128, startTimeOf);

    private static Func<string, CancellationToken, Task<TimeSpan?>> StartOf(double? seconds) =>
        (_, _) => Task.FromResult(seconds is { } s ? S(s) : (TimeSpan?)null);

    /// <summary>A runner scripted per call number (1-based) → exit code and what it writes (null = nothing, "" = an empty file).</summary>
    private sealed class ScriptedRunner : IFfmpegRunner
    {
        private readonly Func<int, (int Exit, string? Content)> _script;

        public ScriptedRunner(Func<int, (int Exit, string? Content)> script) => _script = script;

        public List<List<string>> Commands { get; } = new();

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var tokens = args.ToList().ToList();
            Commands.Add(tokens);
            var (exit, content) = _script(Commands.Count);
            if (content is not null)
            {
                var dir = Path.GetDirectoryName(tokens[^1]);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(tokens[^1], content);
            }

            return Task.FromResult(new FfmpegResult(exit, new List<string>().AsReadOnly()));
        }
    }

    private static ScriptedRunner Always() => new(_ => (0, "jpg-bytes"));

    // serves-spec: SPEC-005#I34 - the tokens: -copyts first, the input seek one lead (3 s) early, the input, the output seek at start + time,
    // then the single frame, the scale and the output. The ffmpeg root tokens (-y -loglevel ...) come from FfmpegArgs.ForFfmpeg and are not asserted.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData("clip.ts")]
    [InlineData("CLIP.TS")]
    [InlineData("clip.m2ts")]
    [InlineData("clip.mts")]
    public async Task ATransportStreamHover_IsOneRunWithTheEarlySeekAndTheAbsoluteTrim(string name)
    {
        var input = MakeInput(name);
        var runner = Always();

        var path = await NewService(runner, StartOf(Start)).GetThumbnailAsync(input, S(5.4), 160, CancellationToken.None);

        path.Should().NotBeNull();
        var tokens = runner.Commands.Should().ContainSingle().Which;
        tokens.Should().ContainInConsecutiveOrder("-copyts", "-ss", "2", "-i", input, "-ss", "6.443444", "-frames:v", "1");
        tokens.Should().ContainInConsecutiveOrder("-vf", "scale=160:-1");
        tokens.Where(t => t == "-ss").Should().HaveCount(2, "one input seek and one output seek");
        tokens.Should().NotContain("-nofind_stream_info", "a TS never skips the stream-info pass: it computes the start the seek counts from");
    }

    // serves-spec: SPEC-005#I34 - the boundary: a hover inside the first lead (t < 3 s) seeks the input to 0 (never a negative time) and still
    // trims at start + t; a hover at 0 is start itself.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(0.0, "0", "1.443444")]
    [InlineData(1.0, "0", "2.443444")]
    [InlineData(3.0, "0", "4.443444")]
    [InlineData(4.0, "1", "5.443444")]
    public async Task TheInputSeek_IsClampedAtZero_AndTheTrimStaysAtStartPlusTime(double hover, string inputSeek, string trim)
    {
        var input = MakeInput("clip.ts");
        var runner = Always();

        await NewService(runner, StartOf(Start)).GetThumbnailAsync(input, S(hover), 160, CancellationToken.None);

        var tokens = runner.Commands.Should().ContainSingle().Which;
        tokens.Should().ContainInConsecutiveOrder("-copyts", "-ss", inputSeek, "-i", input, "-ss", trim);
    }

    // serves-spec: SPEC-005#I34 - the hover is bucketed first (1 s): 5.9 s hovers the 5 s bucket, so the trim is start + 5.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task TheTrim_IsTheBucketedTime_NotTheRawHover()
    {
        var input = MakeInput("clip.ts");
        var runner = Always();

        await NewService(runner, StartOf(Start)).GetThumbnailAsync(input, S(5.9), 160, CancellationToken.None);

        runner.Commands.Should().ContainSingle().Which.Should().ContainInConsecutiveOrder("-ss", "2", "-i", input, "-ss", "6.443444");
    }

    // serves-spec: SPEC-005#I34 - the exact request (T-196) is a keyframe's own time minus the start, where the plain seek already lands on
    // the frame: it keeps today's args on a TS, with no early seek and no -copyts.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task AnExactGrab_OnATransportStream_KeepsTodaysArgs()
    {
        var input = MakeInput("clip.ts");
        var runner = Always();

        await NewService(runner, StartOf(Start)).GetExactThumbnailAsync(input, S(3.466667), 640, CancellationToken.None);

        var tokens = runner.Commands.Should().ContainSingle().Which;
        tokens.Should().NotContain("-copyts");
        tokens.Should().ContainInConsecutiveOrder("-ss", "3.466667", "-i", input);
        tokens.Where(t => t == "-ss").Should().ContainSingle();
    }

    // serves-spec: SPEC-005#I2 / SPEC-005#I34 - only the MPEG-TS family: an MKV, a WebM, an AVI, an MPG, a name like clip.ts.mkv and an
    // extension-less file keep today's args, whatever their probed start.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData("clip.mkv")]
    [InlineData("clip.webm")]
    [InlineData("clip.avi")]
    [InlineData("clip.mpg")]
    [InlineData("clip.ts.mkv")]
    [InlineData("clip")]
    public async Task EveryOtherContainer_KeepsTodaysArgs(string name)
    {
        var input = MakeInput(name);
        var runner = Always();

        await NewService(runner, StartOf(Start)).GetThumbnailAsync(input, S(5), 160, CancellationToken.None);

        var tokens = runner.Commands.Should().ContainSingle().Which;
        tokens.Should().NotContain("-copyts");
        tokens.Should().ContainInConsecutiveOrder("-ss", "5", "-i", input);
        tokens.Where(t => t == "-ss").Should().ContainSingle();
    }

    // serves-spec: SPEC-005#I2 - an MP4's args are untouched by the TS path: a zero-start MP4 is the T-226 flagged grab, a non-zero one today's.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.043, false)]
    public async Task AnMp4_KeepsItsTokens(double start, bool flagged)
    {
        var input = MakeInput("clip.mp4");
        var runner = Always();

        await NewService(runner, StartOf(start)).GetThumbnailAsync(input, S(5), 160, CancellationToken.None);

        var tokens = runner.Commands.Should().ContainSingle().Which;
        tokens.Should().NotContain("-copyts");
        tokens.Contains("-nofind_stream_info").Should().Be(flagged);
        tokens.Should().ContainInConsecutiveOrder("-ss", "5");
    }

    // serves-spec: SPEC-005#I34 - a doubt is today's args and never a throw: no start-time source at all (the older constructors), a source
    // that does not know the start, and one that throws.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task WithoutAKnownStart_ATransportStreamHover_IsTodaysGrab()
    {
        var sources = new Func<string, CancellationToken, Task<TimeSpan?>>?[]
        {
            null,
            StartOf(null),
            (_, _) => throw new InvalidOperationException("probe failed"),
        };

        foreach (var source in sources)
        {
            var input = MakeInput("clip" + Array.IndexOf(sources, source) + ".ts");
            var runner = Always();

            var path = await NewService(runner, source).GetThumbnailAsync(input, S(5), 160, CancellationToken.None);

            path.Should().NotBeNull();
            var tokens = runner.Commands.Should().ContainSingle().Which;
            tokens.Should().NotContain("-copyts");
            tokens.Should().ContainInConsecutiveOrder("-ss", "5", "-i", input);
        }
    }

    // serves-spec: SPEC-005#I34 - a negative or zero start still takes the path (it is a known start); the trim is start + time.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(0.0, "5")]
    [InlineData(-0.5, "4.5")]
    public async Task AKnownStartOfZeroOrBelow_TakesThePathToo(double start, string trim)
    {
        var input = MakeInput("clip.ts");
        var runner = Always();

        await NewService(runner, StartOf(start)).GetThumbnailAsync(input, S(5), 160, CancellationToken.None);

        runner.Commands.Should().ContainSingle().Which.Should().ContainInConsecutiveOrder("-copyts", "-ss", "2", "-i", input, "-ss", trim);
    }

    // serves-spec: SPEC-005#I34 - a wrong hint costs a second run, never a missing picture: an early-seek run that fails, writes nothing or
    // writes an empty file is run once more with today's args, and its picture is the result.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(1, null)]
    [InlineData(0, null)]
    [InlineData(0, "")]
    public async Task AnEarlySeekRunWithoutAPicture_IsRunOnceMoreWithTodaysArgs(int firstExit, string? firstContent)
    {
        var input = MakeInput("clip.ts");
        var runner = new ScriptedRunner(call => call == 1 ? (firstExit, firstContent) : (0, "jpg-bytes"));

        var path = await NewService(runner, StartOf(Start)).GetThumbnailAsync(input, S(5), 160, CancellationToken.None);

        path.Should().NotBeNull();
        File.ReadAllText(path!).Should().Be("jpg-bytes", "the retry's picture, never the failed run's leftover");
        runner.Commands.Should().HaveCount(2);
        runner.Commands[0].Should().Contain("-copyts");
        runner.Commands[1].Should().NotContain("-copyts");
        runner.Commands[1].Should().ContainInConsecutiveOrder("-ss", "5", "-i", input);
    }

    // serves-spec: SPEC-005#I34 - the retry is for the early-seek run only: a run with today's args that fails is not run again.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task ATodaysArgsRunThatFails_IsNotRunAgain()
    {
        var input = MakeInput("clip.mkv");
        var runner = new ScriptedRunner(_ => (1, null));

        var path = await NewService(runner, StartOf(Start)).GetThumbnailAsync(input, S(5), 160, CancellationToken.None);

        path.Should().BeNull();
        runner.Commands.Should().ContainSingle();
    }

    // serves-spec: SPEC-005#I34 - a cancelled grab is never retried and resolves to no picture.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task ACancelledEarlySeekGrab_IsNotRetried()
    {
        var input = MakeInput("clip.ts");
        var runner = Always();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var path = await NewService(runner, StartOf(Start)).GetThumbnailAsync(input, S(5), 160, cts.Token);

        path.Should().BeNull();
        runner.Commands.Should().BeEmpty();
    }

    // serves-spec: SPEC-005#I34 - the cache is untouched: the same bucket again is served from it with no second run, and the picture sits where
    // the cache expects it (the per-input folder, <bucketMs>_w<width>.jpg).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task TheSameBucketAgain_IsServedFromTheCache()
    {
        var input = MakeInput("clip.ts");
        var runner = Always();
        var service = NewService(runner, StartOf(Start));

        var first = await service.GetThumbnailAsync(input, S(5), 160, CancellationToken.None);
        var second = await service.GetThumbnailAsync(input, S(5.5), 160, CancellationToken.None);

        second.Should().Be(first);
        runner.Commands.Should().ContainSingle();
        Path.GetFileName(first!).Should().Be("5000_w160.jpg");
    }

    // serves-spec: SPEC-005#I34 - the extension table.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData("a.ts", true)]
    [InlineData("a.TS", true)]
    [InlineData("a.m2ts", true)]
    [InlineData("a.M2TS", true)]
    [InlineData("a.mts", true)]
    [InlineData(@"C:\v\a.b.ts", true)]
    [InlineData("a.mp4", false)]
    [InlineData("a.mkv", false)]
    [InlineData("a.ts.mp4", false)]
    [InlineData("ts", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsTransportStreamExtension_OnlyTheLastExtension_OfTheFamily(string? path, bool expected) =>
        FfmpegThumbnailService.IsTransportStreamExtension(path).Should().Be(expected);

    // serves-spec: SPEC-005#I34 - BuildArgs: no start keeps today's token list exactly; a start gives the early-seek list.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void BuildArgs_WithoutAStart_IsTodaysList_AndWithOneIsTheEarlySeekList()
    {
        var today = FfmpegThumbnailService.BuildArgs("in.ts", S(5), 160, "out.jpg").ToList().ToList();
        var skipping = FfmpegThumbnailService.BuildArgs("in.mp4", S(5), 160, "out.jpg", skipStreamInfo: true).ToList().ToList();
        var early = FfmpegThumbnailService.BuildArgs("in.ts", S(5), 160, "out.jpg", transportStreamStart: S(Start)).ToList().ToList();

        today.Skip(today.IndexOf("-ss")).Should().Equal(
            "-ss", "5", "-i", "in.ts", "-frames:v", "1", "-vf", "scale=160:-1", "-y", "out.jpg");
        skipping.Skip(skipping.IndexOf("-ss")).Should().Equal(
            "-ss", "5", "-nofind_stream_info", "-i", "in.mp4", "-frames:v", "1", "-vf", "scale=160:-1", "-y", "out.jpg");
        early.Skip(early.IndexOf("-copyts")).Should().Equal(
            "-copyts", "-ss", "2", "-i", "in.ts", "-ss", "6.443444", "-frames:v", "1", "-vf", "scale=160:-1", "-y", "out.jpg");
    }

    // serves-spec: SPEC-005#I34 - the seconds on the new tokens are culture-invariant: a comma-decimal machine writes 1.443444, never 1,443444
    // (found by the independent review: no test covered it, and the formatter's culture could be swapped without a failure on an en-US machine).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void TheSeconds_AreWrittenWithAPoint_OnACommaDecimalMachine()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            ((1.5).ToString(CultureInfo.CurrentCulture)).Should().Be("1,5", "precondition: this culture writes a comma");

            var tokens = FfmpegThumbnailService.BuildArgs("in.ts", S(5), 160, "out.jpg", transportStreamStart: S(Start)).ToList().ToList();

            tokens.Should().ContainInConsecutiveOrder("-copyts", "-ss", "2", "-i", "in.ts", "-ss", "6.443444");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // serves-spec: SPEC-005#I34 - the lead is 3 s: it covers a 2 s GOP (a GOP longer than the lead keeps the late frame, as before the fix).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void TheSeekLead_IsThreeSeconds() =>
        FfmpegThumbnailService.TransportStreamSeekLead.Should().Be(S(3));
}
