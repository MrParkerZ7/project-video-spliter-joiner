using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-226 (G-059, SPEC-005 I27 / I33) - a frame grab of an MP4/MOV whose probed format start time is EXACTLY 0 skips the stream-info decode
/// (<c>-nofind_stream_info</c>, 0.05-0.09 s per 4K H.264 grab); every other grab keeps today's args. Against a fake runner and a fake
/// start-time source (no ffmpeg): the gate, the position of the flag, the unflagged retry, and that a doubt is never a throw. The
/// real-media half is <see cref="ThumbnailStreamInfoSkipIntegrationTests"/>.
///
/// <para>Why only at start 0: skipping the pass also leaves ffmpeg without the format start time an input <c>-ss</c> counts from, so on an
/// MP4/MOV that starts later (the first part of the app's own lossless split, 0.043 s; an <c>-output_ts_offset</c> MOV; MPEG-TS content
/// named <c>.mp4</c>) a skipping grab exits 0 with another frame and no retry can tell.</para>
/// </summary>
public sealed class ThumbnailStreamInfoSkipTests : IDisposable
{
    private const string Flag = "-nofind_stream_info";

    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "vsj-t226-" + Guid.NewGuid().ToString("N"));
    private readonly string _inputDir = Path.Combine(Path.GetTempPath(), "vsj-t226in-" + Guid.NewGuid().ToString("N"));

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

    private FfmpegThumbnailService NewService(IFfmpegRunner runner, Func<string, CancellationToken, Task<TimeSpan?>>? startTimeOf, int maxEntries = 128) =>
        new(runner, _cacheRoot, TimeSpan.FromSeconds(1), maxEntries, startTimeOf);

    private static Func<string, CancellationToken, Task<TimeSpan?>> StartOf(double? seconds) =>
        (_, _) => Task.FromResult(seconds is { } s ? S(s) : (TimeSpan?)null);

    /// <summary>A runner scripted per call: <c>(flagged, call number)</c> → exit code and what it writes (null = nothing, "" = an empty file).</summary>
    private sealed class ScriptedRunner : IFfmpegRunner
    {
        private readonly Func<bool, int, (int Exit, string? Content)> _script;

        public ScriptedRunner(Func<bool, int, (int Exit, string? Content)> script) => _script = script;

        public List<List<string>> Commands { get; } = new();

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var tokens = args.ToList().ToList();
            Commands.Add(tokens);
            var (exit, content) = _script(tokens.Contains(Flag), Commands.Count);
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

    private static ScriptedRunner Always() => new((_, _) => (0, "jpg-bytes"));

    // serves-spec: SPEC-005#I27 - the repro of the task, written first: a zero-start MP4's grab is ONE run carrying -nofind_stream_info.
    // T-195's grab was unflagged on every input, so this fails on it. The flag is an input option: immediately before -i, after -ss.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("CLIP.MP4")]
    [InlineData("clip.mov")]
    [InlineData("clip.m4v")]
    public async Task AZeroStartIsoBmffGrab_IsOneRunWithTheFlag_RightBeforeTheInput(string name)
    {
        var input = MakeInput(name);
        var runner = Always();

        var path = await NewService(runner, StartOf(0)).GetThumbnailAsync(input, S(5), 160, CancellationToken.None);

        path.Should().NotBeNull();
        var tokens = runner.Commands.Should().ContainSingle().Which;
        var i = tokens.IndexOf("-i");
        tokens[i - 1].Should().Be(Flag, "an input option goes immediately before -i");
        tokens[i - 3].Should().Be("-ss", "after the seek: -ss <t> -nofind_stream_info -i <input>");
        tokens.Should().ContainInConsecutiveOrder("-frames:v", "1");
        tokens.Should().ContainInConsecutiveOrder("-vf", "scale=160:-1");
    }

    // serves-spec: SPEC-005#I27 - the exact request (T-196) is gated the same way.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task AnExactGrab_OfAZeroStartMp4_IsFlaggedToo()
    {
        var input = MakeInput("clip.mp4");
        var runner = Always();

        await NewService(runner, StartOf(0)).GetExactThumbnailAsync(input, S(8.333333), 640, CancellationToken.None);

        runner.Commands.Should().ContainSingle().Which.Should().Contain(Flag);
    }

    // serves-spec: SPEC-005#I27 - every other start is today's args: one frame (0.033 s), the first part of a lossless split (0.043 s), an
    // -output_ts_offset MOV (9.976 s), a negative start, a TS named .mp4 (1.44 s), and a start of one microsecond - "exactly 0" means 0.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(0.033)]
    [InlineData(0.042993)]
    [InlineData(9.976)]
    [InlineData(-0.021)]
    [InlineData(1.44)]
    [InlineData(0.000001)]
    public async Task ANonZeroStart_IsTodaysUnflaggedGrab(double start)
    {
        var input = MakeInput("clip.mp4");
        var runner = Always();

        await NewService(runner, StartOf(start)).GetThumbnailAsync(input, S(3), 160, CancellationToken.None);

        runner.Commands.Should().ContainSingle().Which.Should().NotContain(Flag);
    }

    // serves-spec: SPEC-005#I27 - only MP4/M4V/MOV are gated: a zero-start MKV, TS or extension-less file is unflagged (T-217 owns them).
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData("clip.mkv")]
    [InlineData("clip.ts")]
    [InlineData("clip.webm")]
    [InlineData("clip")]
    [InlineData("clip.mp4.mkv")]
    public async Task ANonIsoBmffExtension_IsNeverFlagged_EvenAtStartZero(string name)
    {
        var input = MakeInput(name);
        var runner = Always();

        await NewService(runner, StartOf(0)).GetThumbnailAsync(input, S(3), 160, CancellationToken.None);

        runner.Commands.Should().ContainSingle().Which.Should().NotContain(Flag);
    }

    // serves-spec: SPEC-005#I27 - no start-time source (the older constructors, a test), a source that does not know, and one that throws
    // are all today's unflagged args and never a throw.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task WithoutAStartTime_TheGrabIsTodaysUnflaggedOne_AndNeverThrows()
    {
        var input = MakeInput("clip.mp4");

        foreach (var source in new Func<string, CancellationToken, Task<TimeSpan?>>?[]
                 {
                     null,
                     StartOf(null),
                     (_, _) => throw new InvalidOperationException("the probe failed"),
                     (_, _) => Task.FromException<TimeSpan?>(new IOException("the file vanished")),
                 })
        {
            var runner = Always();
            var path = await NewService(runner, source).GetThumbnailAsync(input, S(3), 160, CancellationToken.None);

            path.Should().NotBeNull("a doubt costs the saving, never the picture");
            runner.Commands.Should().ContainSingle().Which.Should().NotContain(Flag);
            Directory.Delete(_cacheRoot, recursive: true);
        }
    }

    // serves-spec: SPEC-005#I27 - a cancelled probe is a cancelled grab: null, no run.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task ACancelledProbe_IsACancelledGrab_WithNoRun()
    {
        var input = MakeInput("clip.mp4");
        var runner = Always();
        using var cts = new CancellationTokenSource();

        var path = await NewService(runner, (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<TimeSpan?>(TimeSpan.Zero);
        }).GetThumbnailAsync(input, S(3), 160, cts.Token);

        path.Should().BeNull();
        runner.Commands.Should().BeEmpty();
    }

    // serves-spec: SPEC-005#I33 - a flagged run that exits non-zero, writes no file, or writes an empty one is run once more WITHOUT the flag;
    // the picture comes from the retry and is cached, so the next call runs nothing.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(1, "")]      // exits non-zero (and leaves an empty file behind: the retry is judged on its own output)
    [InlineData(0, null)]    // exits 0, writes nothing
    [InlineData(0, "")]      // exits 0, writes an empty file
    public async Task AFlaggedRunThatYieldsNoPicture_IsRunOnceMoreWithoutTheFlag(int exit, string? content)
    {
        var input = MakeInput("clip.mp4");
        var runner = new ScriptedRunner((flagged, _) => flagged ? (exit, content) : (0, "jpg-bytes"));
        var svc = NewService(runner, StartOf(0));

        var path = await svc.GetThumbnailAsync(input, S(3), 160, CancellationToken.None);
        var again = await svc.GetThumbnailAsync(input, S(3), 160, CancellationToken.None);

        runner.Commands.Should().HaveCount(2);
        runner.Commands[0].Should().Contain(Flag);
        runner.Commands[1].Should().NotContain(Flag);
        File.ReadAllText(path!).Should().Be("jpg-bytes");
        again.Should().Be(path, "the retry's picture was cached");
    }

    // serves-spec: SPEC-005#I33 - a retry that fails too is null, nothing is cached, and the next call tries again (flagged first, as
    // there is no failure memory).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task WhenTheRetryFailsToo_TheGrabIsNull_AndNotCached()
    {
        var input = MakeInput("clip.mp4");
        var runner = new ScriptedRunner((_, _) => (1, null));
        var svc = NewService(runner, StartOf(0));

        (await svc.GetThumbnailAsync(input, S(3), 160, CancellationToken.None)).Should().BeNull();
        (await svc.GetThumbnailAsync(input, S(3), 160, CancellationToken.None)).Should().BeNull();

        runner.Commands.Should().HaveCount(4, "flagged then unflagged, twice: nothing was cached");
    }

    // serves-spec: SPEC-005#I33 - a grab cancelled while its flagged run is in flight is never retried.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task ACancelledFlaggedGrab_IsNeverRetried()
    {
        var input = MakeInput("clip.mp4");
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var runner = new CancellingOnFirstRunRunner(cts, () => calls++);

        var path = await NewService(runner, StartOf(0)).GetThumbnailAsync(input, S(3), 160, cts.Token);

        path.Should().BeNull();
        calls.Should().Be(1, "the cancelled run is not run again without the flag");
    }

    private sealed class CancellingOnFirstRunRunner : IFfmpegRunner
    {
        private readonly CancellationTokenSource _cts;
        private readonly Action _count;

        public CancellingOnFirstRunRunner(CancellationTokenSource cts, Action count)
        {
            _cts = cts;
            _count = count;
        }

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            _count();
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
        }
    }

    // serves-spec: SPEC-005#I27 - the gate costs no process: the start time is asked once per grab that runs ffmpeg, and a cache hit asks
    // nothing (the shared probe caches it, SPEC-004 I33, so a repeated hover does not even reach it).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task TheStartTimeIsAskedOncePerRunningGrab_AndNotOnACacheHit()
    {
        var input = MakeInput("clip.mp4");
        var runner = Always();
        var asked = 0;
        var svc = NewService(runner, (_, _) =>
        {
            asked++;
            return Task.FromResult<TimeSpan?>(TimeSpan.Zero);
        });

        await svc.GetThumbnailAsync(input, S(3), 160, CancellationToken.None);
        await svc.GetThumbnailAsync(input, S(3), 160, CancellationToken.None);   // a cache hit
        await svc.GetThumbnailAsync(input, S(5), 160, CancellationToken.None);   // another bucket

        asked.Should().Be(2);
        runner.Commands.Should().HaveCount(2);
    }

    // serves-spec: SPEC-005#I27 - BuildArgs without the argument is today's token list (T-195 kept it at four arguments for the older callers).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void BuildArgs_WithoutTheArgument_IsTodaysTokenList()
    {
        var plain = FfmpegThumbnailService.BuildArgs(@"C:\v\clip.mp4", S(5), 160, @"C:\t\5000_w160.jpg").ToList().ToList();
        var flagged = FfmpegThumbnailService.BuildArgs(@"C:\v\clip.mp4", S(5), 160, @"C:\t\5000_w160.jpg", skipStreamInfo: true).ToList().ToList();

        plain.Should().NotContain(Flag);
        flagged.Where(t => t != Flag).Should().Equal(plain, "the flag is the only difference");
        flagged.IndexOf(Flag).Should().Be(flagged.IndexOf("-i") - 1);
    }
}
