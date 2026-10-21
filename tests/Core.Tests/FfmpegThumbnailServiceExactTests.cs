using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-196 (G-059, SPEC-005 I28-I32 / SPEC-004 I47) - the EXACT-time frame grab for a cut-point or profile picture, against a fake
/// runner (no ffmpeg): the tokens, the name and cache key, the separation from the bucketed request, eviction, Clear, the failure
/// contract, and <see cref="MediaInfo.ToSeekOffset"/>. The real-media half is <see cref="FfmpegThumbnailServiceExactIntegrationTests"/>.
///
/// <para>The bucketed request floors a time to the second and seeks there: for a cut-point picture at a keyframe of 8.333333 s it seeks
/// to 8, before the keyframe, decodes most of a GOP (3.5-4.4 s CPU against 0.39 s, measured) and shows a frame up to a second before the
/// cut. The exact request seeks to the time itself.</para>
/// </summary>
public sealed class FfmpegThumbnailServiceExactTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "vsj-thumbx-" + Guid.NewGuid().ToString("N"));
    private readonly string _inputDir = Path.Combine(Path.GetTempPath(), "vsj-thumbxin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TryDelete(_cacheRoot);
        TryDelete(_inputDir);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private string MakeInput(string name = "clip.mp4")
    {
        Directory.CreateDirectory(_inputDir);
        var input = Path.Combine(_inputDir, name);
        File.WriteAllText(input, "placeholder");
        return input;
    }

    private FfmpegThumbnailService NewService(IFfmpegRunner runner, int maxEntries = 128) =>
        new(runner, _cacheRoot, TimeSpan.FromSeconds(1), maxEntries);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // serves-spec: SPEC-005#I28 - the repro, written first. A grab at a keyframe of 8.333333 s: the bucketed request seeks to 8 (before
    // the keyframe), the exact request to 8.333333. Shipped code had only the first.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task AnExactRequest_SeeksToTheTimeItself_WhereTheBucketedOneSeeksToTheFlooredSecond()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);
        var keyframe = TimeSpan.FromTicks(83333330); // 8.333333 s

        var bucketed = await svc.GetThumbnailAsync(input, keyframe, 640, CancellationToken.None);
        var exact = await svc.GetExactThumbnailAsync(input, keyframe, 640, CancellationToken.None);

        bucketed.Should().NotBeNull();
        exact.Should().NotBeNull();
        runner.Commands[0][runner.Commands[0].IndexOf("-ss") + 1].Should().Be("8", "the bucketed request floors to the second");
        runner.Commands[1][runner.Commands[1].IndexOf("-ss") + 1].Should().Be("8.333333", "the exact request seeks to the keyframe itself");
    }

    // serves-spec: SPEC-005#I28 - the exact grab's command: -ss BEFORE -i, one frame, scale to the width, overwrite; the stream-info
    // pass stays (SPEC-005 I27); the output is the temp jpg the service returned.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task AnExactRequest_BuildsTheSameGrabArgs_AsTheBucketedOne_ExceptTheSeek()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        var path = await svc.GetExactThumbnailAsync(input, S(8.333333), 640, CancellationToken.None);

        var tokens = runner.Commands.Should().ContainSingle().Which;
        tokens.IndexOf("-ss").Should().BeLessThan(tokens.IndexOf("-i"), "an input seek");
        tokens.Should().NotContain("-nofind_stream_info");
        tokens.Should().ContainInConsecutiveOrder("-frames:v", "1");
        tokens.Should().ContainInConsecutiveOrder("-vf", "scale=640:-1");
        tokens.Should().Contain("-y");
        tokens[^1].Should().Be(path);
    }

    // serves-spec: SPEC-005#I28 - an exact time is never rounded to milliseconds: six decimals reach -ss unchanged. A keyframe at
    // 8.333333 s rounded to 8.333 would land just before the keyframe, decode the whole previous GOP and show the previous frame.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(83333330L, "8.333333")]   // 8.333333 s
    [InlineData(75075000L, "7.5075")]     // 7.5075 s (a keyframe every 2.5025 s at 30000/1001 fps)
    [InlineData(10010000L, "1.001")]
    [InlineData(10000001L, "1")]          // below a microsecond: "0.######" prints six decimals
    [InlineData(10000010L, "1.000001")]
    public async Task AnExactTime_ReachesSsAtSixDecimals_NeverRoundedToMilliseconds(long ticks, string expected)
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();

        await NewService(runner).GetExactThumbnailAsync(input, TimeSpan.FromTicks(ticks), 160, CancellationToken.None);

        var tokens = runner.Commands.Should().ContainSingle().Which;
        tokens[tokens.IndexOf("-ss") + 1].Should().Be(expected);
    }

    // serves-spec: SPEC-005#I30 - the file name is x<microseconds>_w<width>.jpg in the input's cache folder, which no bucket's name
    // (digits only) can equal.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(83333330L, 640, "x8333333_w640.jpg")]
    [InlineData(80000000L, 160, "x8000000_w160.jpg")]
    public async Task AnExactRequest_IsNamedByItsMicroseconds_AndItsWidth(long ticks, int width, string name)
    {
        var input = MakeInput();
        var svc = NewService(new WritingFakeRunner());

        var path = await svc.GetExactThumbnailAsync(input, TimeSpan.FromTicks(ticks), width, CancellationToken.None);

        Path.GetFileName(path).Should().Be(name);
        Path.GetDirectoryName(path).Should().Be(svc.InputCacheDir(input), "the same per-input folder as the bucketed files");
    }

    // serves-spec: SPEC-005#I28 - a time of zero or less is zero: -ss 0 and x0_w<width>.jpg, as the bucketed path clamps (I4).
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(0L)]
    [InlineData(-50000000L)]
    public async Task AnExactTime_OfZeroOrLess_IsZero(long ticks)
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();

        var path = await NewService(runner).GetExactThumbnailAsync(input, TimeSpan.FromTicks(ticks), 640, CancellationToken.None);

        var tokens = runner.Commands.Should().ContainSingle().Which;
        tokens[tokens.IndexOf("-ss") + 1].Should().Be("0");
        Path.GetFileName(path).Should().Be("x0_w640.jpg");
    }

    // serves-spec: SPEC-005#I29 - an exact request and a bucketed request in the same second at the same width are two files, in either
    // order, and each runs ffmpeg once: they never serve each other.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnExactAndABucketedRequest_NeverServeEachOther(bool exactFirst)
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);
        var second = S(8); // a whole second: the exact name is x8000000, the bucket's 8000

        string? exact;
        string? bucketed;
        if (exactFirst)
        {
            exact = await svc.GetExactThumbnailAsync(input, second, 640, CancellationToken.None);
            bucketed = await svc.GetThumbnailAsync(input, second, 640, CancellationToken.None);
        }
        else
        {
            bucketed = await svc.GetThumbnailAsync(input, second, 640, CancellationToken.None);
            exact = await svc.GetExactThumbnailAsync(input, second, 640, CancellationToken.None);
        }

        exact.Should().NotBe(bucketed);
        Path.GetFileName(exact).Should().Be("x8000000_w640.jpg");
        Path.GetFileName(bucketed).Should().Be("8000_w640.jpg");
        runner.CallCount.Should().Be(2, "each kind ran ffmpeg for itself");
    }

    // serves-spec: SPEC-005#I29 - a repeat exact request reuses its file without running ffmpeg; the same exact time at another width is
    // another file (as I26).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task AnExactRequest_IsCachedPerWidth()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        var first = await svc.GetExactThumbnailAsync(input, S(8.333333), 640, CancellationToken.None);
        var again = await svc.GetExactThumbnailAsync(input, S(8.333333), 640, CancellationToken.None);
        var narrow = await svc.GetExactThumbnailAsync(input, S(8.333333), 64, CancellationToken.None);

        again.Should().Be(first);
        runner.CallCount.Should().Be(2, "the repeat hit the cache; the other width ran again");
        narrow.Should().NotBe(first);
        Path.GetFileName(narrow).Should().Be("x8333333_w64.jpg");
    }

    // serves-spec: SPEC-005#I31 - a file a PRIOR run left on disk is reused only under its own name: an exact file for an exact request,
    // never a bucket file for an exact request (or the reverse).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task APriorRunsFile_IsReusedOnlyUnderItsOwnName()
    {
        var input = MakeInput();
        var probeSvc = NewService(new WritingFakeRunner());
        var dir = probeSvc.InputCacheDir(input);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "8000_w640.jpg"), "a bucket file of a prior run");
        File.WriteAllText(Path.Combine(dir, "x8333333_w640.jpg"), "an exact file of a prior run");
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        var reused = await svc.GetExactThumbnailAsync(input, S(8.333333), 640, CancellationToken.None);
        var notReused = await svc.GetExactThumbnailAsync(input, S(8), 640, CancellationToken.None);

        runner.CallCount.Should().Be(1, "only the second needed ffmpeg");
        File.ReadAllText(reused!).Should().Be("an exact file of a prior run");
        Path.GetFileName(notReused).Should().Be("x8000000_w640.jpg", "the bucket file 8000_w640.jpg is not an exact 8 s grab");
    }

    // serves-spec: SPEC-005#I31 - the LRU is shared: past the cap the least-recently-used entry is evicted AND its file deleted, an exact
    // file included.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task EvictingAnExactEntry_DeletesItsFile()
    {
        var input = MakeInput();
        var svc = NewService(new WritingFakeRunner(), maxEntries: 2);

        var a = await svc.GetExactThumbnailAsync(input, S(1.25), 640, CancellationToken.None);
        var b = await svc.GetExactThumbnailAsync(input, S(2.25), 640, CancellationToken.None);
        File.Exists(a).Should().BeTrue();
        var c = await svc.GetThumbnailAsync(input, S(3), 640, CancellationToken.None);

        File.Exists(a).Should().BeFalse("the oldest entry was evicted with its file");
        File.Exists(b).Should().BeTrue();
        File.Exists(c).Should().BeTrue();
    }

    // serves-spec: SPEC-005#I31 - Clear(input) and ClearAll() remove exact files with the rest.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task ClearAndClearAll_RemoveExactFiles()
    {
        var input = MakeInput();
        var other = MakeInput("other.mp4");
        var svc = NewService(new WritingFakeRunner());
        var a = await svc.GetExactThumbnailAsync(input, S(1.25), 640, CancellationToken.None);
        var o = await svc.GetExactThumbnailAsync(other, S(1.25), 640, CancellationToken.None);

        svc.Clear(input);

        File.Exists(a).Should().BeFalse("the input's own folder is gone");
        File.Exists(o).Should().BeTrue("another input's folder stays");
        svc.ClearAll();
        File.Exists(o).Should().BeFalse();
    }

    // serves-spec: SPEC-005#I28 - a failing exact grab is null, caches nothing, and the next call runs ffmpeg again; so is a grab that
    // exits 0 with no file, a cancelled one, and one that throws. Never throws.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task AFailingExactGrab_IsNull_IsNotCached_AndIsRetried()
    {
        var input = MakeInput();
        var failing = new FailingThumbRunner();
        var svc = NewService(failing);

        (await svc.GetExactThumbnailAsync(input, S(8.25), 640, CancellationToken.None)).Should().BeNull();
        (await svc.GetExactThumbnailAsync(input, S(8.25), 640, CancellationToken.None)).Should().BeNull();

        failing.CallCount.Should().Be(2, "nothing was cached, so the second call asked ffmpeg again");
        (await NewService(new NoFileSuccessRunner()).GetExactThumbnailAsync(input, S(8.25), 640, CancellationToken.None)).Should().BeNull();
        (await NewService(new ThrowingThumbRunner()).GetExactThumbnailAsync(input, S(8.25), 640, CancellationToken.None)).Should().BeNull();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        (await NewService(new CancellingFakeThumbRunner()).GetExactThumbnailAsync(input, S(8.25), 640, cts.Token)).Should().BeNull();
    }

    // serves-spec: SPEC-005#I28 - a blank input and a width of zero or less are null without running ffmpeg, as the bucketed request.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task AnExactRequest_WithABlankInputOrNoWidth_IsNull_WithoutRunningFfmpeg()
    {
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        (await svc.GetExactThumbnailAsync(" ", S(1), 640, CancellationToken.None)).Should().BeNull();
        (await svc.GetExactThumbnailAsync(MakeInput(), S(1), 0, CancellationToken.None)).Should().BeNull();

        runner.CallCount.Should().Be(0);
    }

    // ---- MediaInfo.ToSeekOffset (SPEC-004 I47) ---------------------------------------------------------------------------------

    private static MediaInfo InfoWithStart(double startSeconds) => new(
        S(30), "mpegts", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>(), S(startSeconds));

    // serves-spec: SPEC-004#I47 - ToSeekOffset(t) = max(0, t - StartTime): the time itself when the file starts at 0; T-219's measured
    // pair on the synthetic TS (5.466667 s -> 4.021334 s at a start of 1.445333 s); zero for a time below the start.
    [Trait("serves-spec", "SPEC-004")]
    [Theory]
    [InlineData(0.0, 5.466667, 5.466667)]
    [InlineData(1.445333, 5.466667, 4.021334)]
    [InlineData(1.445333, 1.445333, 0.0)]
    [InlineData(1.445333, 0.5, 0.0)]
    [InlineData(-0.021, 3.0, 3.021)]
    public void ToSeekOffset_SubtractsTheFormatStart_AndNeverGoesBelowZero(double start, double time, double expected)
    {
        InfoWithStart(start).ToSeekOffset(S(time)).TotalSeconds.Should().BeApproximately(expected, 1e-6);
    }
}
