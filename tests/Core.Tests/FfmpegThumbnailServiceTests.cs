using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Pure-unit tests for <see cref="FfmpegThumbnailService"/> — no real ffmpeg binary. A fake
/// <see cref="IFfmpegRunner"/> records the args + call count and (when told to "succeed") writes the
/// requested output file so the service's post-run existence check passes. The cache root is redirected
/// to a temp dir so nothing touches the real per-user folder.
/// </summary>
public class FfmpegThumbnailServiceTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        Path.GetTempPath(), "vsj-thumb-" + Guid.NewGuid().ToString("N"));

    private readonly string _inputDir = Path.Combine(
        Path.GetTempPath(), "vsj-thumbin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TryDelete(_cacheRoot);
        TryDelete(_inputDir);
    }

    private string MakeInput()
    {
        Directory.CreateDirectory(_inputDir);
        var input = Path.Combine(_inputDir, "clip.mp4");
        File.WriteAllText(input, "placeholder");
        return input;
    }

    private FfmpegThumbnailService NewService(IFfmpegRunner runner, TimeSpan? bucket = null, int maxEntries = 128) =>
        new(runner, _cacheRoot, bucket ?? TimeSpan.FromSeconds(1), maxEntries);

    // ---- Args ----

    [Fact]
    public async Task GetThumbnailAsync_BuildsFastSeekArgs_SsBeforeInput_OneFrame_Scale()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(5), width: 160, CancellationToken.None);

        path.Should().NotBeNull();
        runner.Commands.Should().ContainSingle();
        var tokens = runner.Commands[0];

        // -ss must come BEFORE -i (fast input seek).
        var ssIndex = tokens.IndexOf("-ss");
        var iIndex = tokens.IndexOf("-i");
        ssIndex.Should().BeGreaterThanOrEqualTo(0);
        iIndex.Should().BeGreaterThan(ssIndex, "-ss must precede -i for fast seek");

        // Single frame + scale filter + overwrite.
        tokens.Should().ContainInConsecutiveOrder("-frames:v", "1");
        tokens.Should().ContainInConsecutiveOrder("-vf", "scale=160:-1");
        tokens.Should().Contain("-y");

        // The seek value is the bucketed timestamp in invariant seconds (5s → "5").
        tokens[ssIndex + 1].Should().Be("5");

        // The output token (last) is the temp jpg path the service returned.
        tokens[^1].Should().Be(path);
        path!.Should().EndWith(".jpg");
    }

    [Fact]
    public async Task GetThumbnailAsync_FractionalTime_FlooredToBucket_ForSeekAndPath()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner, bucket: TimeSpan.FromSeconds(1));

        // 7.85s with a 1s bucket → floored to 7s.
        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(7.85), 160, CancellationToken.None);

        var tokens = runner.Commands[0];
        tokens[tokens.IndexOf("-ss") + 1].Should().Be("7");
        Path.GetFileName(path!).Should().Be("7000.jpg", "the bucket file is named by its floored-ms value");
    }

    // ---- Cache ----

    [Fact]
    public async Task GetThumbnailAsync_SameBucketTwice_SecondCallHitsCache_RunnerCalledOnce()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner, bucket: TimeSpan.FromSeconds(1));

        var first = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(3.2), 160, CancellationToken.None);
        // Same 1s bucket (3.x) → must reuse the file, NOT run ffmpeg again.
        var second = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(3.9), 160, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().Be(first);
        runner.CallCount.Should().Be(1, "the second same-bucket request must hit the cache");
    }

    [Fact]
    public async Task GetThumbnailAsync_DifferentBuckets_RunTwice()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner, bucket: TimeSpan.FromSeconds(1));

        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(3), 160, CancellationToken.None);
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(4), 160, CancellationToken.None);

        runner.CallCount.Should().Be(2, "distinct buckets each need their own extraction");
    }

    [Fact]
    public async Task GetThumbnailAsync_CacheBounded_EvictsOldest()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        // Cap of 2 entries; 3 distinct buckets → the oldest is evicted (file deleted).
        var svc = NewService(runner, bucket: TimeSpan.FromSeconds(1), maxEntries: 2);

        var p0 = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(0), 160, CancellationToken.None);
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, CancellationToken.None);
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        // The oldest (bucket 0) file was evicted from disk.
        File.Exists(p0!).Should().BeFalse("the LRU cap evicts the oldest entry's file");

        // Re-requesting bucket 0 now re-runs ffmpeg (it was evicted from the in-memory cache).
        var before = runner.CallCount;
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(0), 160, CancellationToken.None);
        runner.CallCount.Should().Be(before + 1, "an evicted bucket must be re-extracted");
    }

    // ---- Cancellation ----

    [Fact]
    public async Task GetThumbnailAsync_Cancelled_ReturnsNull_NoThrow()
    {
        var input = MakeInput();
        var runner = new CancellingFakeThumbRunner();
        var svc = NewService(runner);

        using var cts = new CancellationTokenSource();
        var result = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(5), 160, cts.Token);

        // A superseded/cancelled request resolves to null and never throws or clobbers.
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetThumbnailAsync_PreCancelledToken_ReturnsNull_RunnerNotCalled()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(5), 160, cts.Token);

        result.Should().BeNull();
        runner.CallCount.Should().Be(0, "an already-cancelled request must not launch ffmpeg");
    }

    // ---- Best-effort failure ----

    [Fact]
    public async Task GetThumbnailAsync_FfmpegFailure_ReturnsNull_NoThrow_NotCached()
    {
        var input = MakeInput();
        var runner = new FailingThumbRunner();
        var svc = NewService(runner);

        var result = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(5), 160, CancellationToken.None);

        result.Should().BeNull();
        // A failure is not cached → a retry re-runs the runner.
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(5), 160, CancellationToken.None);
        runner.CallCount.Should().Be(2, "a failed grab must not be cached");
    }

    [Fact]
    public async Task GetThumbnailAsync_RunnerReportsSuccessButNoFile_ReturnsNull()
    {
        var input = MakeInput();
        // Exit 0 but writes nothing → the service's file-existence check fails → null.
        var runner = new NoFileSuccessRunner();
        var svc = NewService(runner);

        var result = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(5), 160, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetThumbnailAsync_MissingInput_ReturnsNull_NoRun()
    {
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        var missing = Path.Combine(_inputDir, "does-not-exist.mp4");
        // The service does not itself stat the input (ffmpeg would fail); the fake models that by failing.
        // But an empty/whitespace path short-circuits before any run.
        var empty = await svc.GetThumbnailAsync("   ", TimeSpan.FromSeconds(1), 160, CancellationToken.None);
        empty.Should().BeNull();
        runner.CallCount.Should().Be(0, "an empty input path never launches ffmpeg");
        _ = missing;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task GetThumbnailAsync_NonPositiveWidth_ReturnsNull_NoRun(int width)
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        var result = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), width, CancellationToken.None);
        result.Should().BeNull();
        runner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetThumbnailAsync_TimeZero_And_LargeTime_Bucketed()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner, bucket: TimeSpan.FromSeconds(1));

        var atZero = await svc.GetThumbnailAsync(input, TimeSpan.Zero, 160, CancellationToken.None);
        Path.GetFileName(atZero!).Should().Be("0.jpg");
        runner.Commands[0][runner.Commands[0].IndexOf("-ss") + 1].Should().Be("0");
    }

    // ---- Clear / ClearAll ----

    [Fact]
    public async Task Clear_RemovesInputCacheDir_BestEffort()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);
        File.Exists(path!).Should().BeTrue();

        svc.Clear(input);
        File.Exists(path!).Should().BeFalse("Clear deletes the input's cache dir");

        // After Clear, the same bucket re-runs ffmpeg (the in-memory entry was dropped too).
        var before = runner.CallCount;
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);
        runner.CallCount.Should().Be(before + 1);
    }

    [Fact]
    public async Task ClearAll_RemovesWholeCacheRoot()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);
        File.Exists(path!).Should().BeTrue();
        Directory.Exists(_cacheRoot).Should().BeTrue();

        svc.ClearAll();
        Directory.Exists(_cacheRoot).Should().BeFalse("ClearAll deletes the whole cache root");
    }

    [Fact]
    public void Clear_MissingDir_IsNoOp_NoThrow()
    {
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);

        // Never populated → Clear/ClearAll must not throw.
        var act1 = () => svc.Clear(Path.Combine(_inputDir, "never.mp4"));
        var act2 = () => svc.ClearAll();
        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }

    [Fact]
    public void Clear_EmptyPath_IsNoOp_NoThrow()
    {
        var runner = new WritingFakeRunner();
        var svc = NewService(runner);
        var act = () => svc.Clear("   ");
        act.Should().NotThrow();
    }

    // ---- todo-automate gap coverage (SPEC-005) ----

    // SPEC-005#I11 — a cache HIT requires the tracked file to still exist; a tracked path whose file
    // was deleted externally falls through to re-extraction.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task GetThumbnailAsync_TrackedFileDeletedExternally_ReExtracts()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner, bucket: TimeSpan.FromSeconds(1));

        var first = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(3), 160, CancellationToken.None);
        first.Should().NotBeNull();
        runner.CallCount.Should().Be(1);

        // Delete the tracked temp file out from under the service.
        File.Delete(first!);

        var second = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(3), 160, CancellationToken.None);
        second.Should().NotBeNull();
        File.Exists(second!).Should().BeTrue();
        runner.CallCount.Should().Be(2, "a tracked-but-missing file must be re-extracted, not returned stale");
    }

    // SPEC-005#I12 — a temp file left on disk by a prior process (on disk, untracked in memory) is
    // reused WITHOUT running ffmpeg and re-tracked in the cache.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task GetThumbnailAsync_PreexistingOnDiskFile_ReusedWithoutFfmpeg_ThenCached()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner, bucket: TimeSpan.FromSeconds(1));

        // Seed the exact temp path for (input, bucket 2s) on disk, on a service with nothing tracked yet.
        var tempPath = svc.ResolveTempPath(input, TimeSpan.FromSeconds(2));
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        await File.WriteAllTextAsync(tempPath, "left-by-prior-process");

        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2.4), 160, CancellationToken.None);

        path.Should().Be(tempPath, "the on-disk file for the bucket is reused verbatim");
        runner.CallCount.Should().Be(0, "a pre-existing on-disk temp file bypasses ffmpeg");

        // It is now tracked → a second same-bucket call is an in-memory cache hit (still no ffmpeg).
        var again = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2.9), 160, CancellationToken.None);
        again.Should().Be(tempPath);
        runner.CallCount.Should().Be(0);
    }

    // SPEC-005#I23 — the constructor rejects a null runner or null cacheRoot with ArgumentNullException.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void Ctor_NullRunnerOrCacheRoot_Throws()
    {
        var runner = new WritingFakeRunner();
        var act1 = () => new FfmpegThumbnailService(null!, _cacheRoot);
        var act2 = () => new FfmpegThumbnailService(runner, null!);
        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }

    // SPEC-005#I24 — a non-positive bucketGranularity falls back to the 1s default (so 1.5s and 1.9s
    // share bucket 1, while 0.5s is the distinct bucket 0).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task Ctor_NonPositiveBucketGranularity_FallsBackToOneSecond()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner, bucket: TimeSpan.Zero); // non-positive → 1s default

        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1.5), 160, CancellationToken.None);
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1.9), 160, CancellationToken.None);
        runner.CallCount.Should().Be(1, "Zero granularity fell back to 1s: 1.5s and 1.9s share bucket 1");

        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(0.5), 160, CancellationToken.None);
        runner.CallCount.Should().Be(2, "0.5s falls into the distinct bucket 0 (1s bucketing, not pass-through)");
    }

    // SPEC-005#I24 — a non-positive maxEntries falls back to the 128 default (no premature eviction).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task Ctor_NonPositiveMaxEntries_FallsBackTo128()
    {
        var input = MakeInput();
        var runner = new WritingFakeRunner();
        var svc = NewService(runner, bucket: TimeSpan.FromSeconds(1), maxEntries: 0); // non-positive → 128

        var p0 = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(0), 160, CancellationToken.None);
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, CancellationToken.None);
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        // With the 128 default (not 0), bucket 0 is NOT evicted: its file survives and re-request is a hit.
        File.Exists(p0!).Should().BeTrue("maxEntries 0 fell back to 128 — no eviction after 3 entries");
        var before = runner.CallCount;
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(0), 160, CancellationToken.None);
        runner.CallCount.Should().Be(before, "the still-cached bucket 0 is served from memory (no re-extract)");
    }

    // SPEC-005#I25 — DefaultCacheRoot() composes %LOCALAPPDATA%/VideoSplitJoiner/thumb-cache (OS-temp fallback).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void DefaultCacheRoot_ComposesAppDataThumbCachePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }

        var expected = Path.Combine(root, FfmpegThumbnailService.AppFolderName, FfmpegThumbnailService.CacheFolderName);
        var actual = FfmpegThumbnailService.DefaultCacheRoot();

        actual.Should().Be(expected);
        actual.Should().EndWith(Path.Combine(FfmpegThumbnailService.AppFolderName, FfmpegThumbnailService.CacheFolderName));
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

/// <summary>
/// Success runner that materializes the requested output file (last token) so the service's post-run
/// existence check passes. Records every command's token list + a call count.
/// </summary>
internal sealed class WritingFakeRunner : IFfmpegRunner
{
    public List<List<string>> Commands { get; } = new();

    public int CallCount { get; private set; }

    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var tokens = args.ToList().ToList();
        Commands.Add(tokens);
        CallCount++;

        var output = tokens[^1];
        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(output, "jpg-bytes");
        return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
    }
}

/// <summary>Runner that honors the token by cancelling — models a superseded request.</summary>
internal sealed class CancellingFakeThumbRunner : IFfmpegRunner
{
    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        throw new OperationCanceledException();
    }
}

/// <summary>Runner that returns a non-zero exit (ffmpeg failure) and writes no file.</summary>
internal sealed class FailingThumbRunner : IFfmpegRunner
{
    public int CallCount { get; private set; }

    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(new FfmpegResult(1, new[] { "no such file" }.ToList().AsReadOnly()));
    }
}

/// <summary>Runner that exits 0 but writes NO output file — the service must still return null.</summary>
internal sealed class NoFileSuccessRunner : IFfmpegRunner
{
    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
}
