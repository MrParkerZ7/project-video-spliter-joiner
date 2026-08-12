using System.Globalization;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Waveform;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Pure-unit tests for <see cref="FfmpegWaveformService"/> — no real ffmpeg binary. A fake
/// <see cref="IFfmpegRunner"/> records the args + call count and (when told to "succeed") writes a supplied
/// raw Int16-LE PCM blob to the requested temp file so the service's read/bucket path runs on known samples.
/// The cache root is redirected to a temp dir so nothing touches the real per-user folder.
/// </summary>
public class FfmpegWaveformServiceTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        Path.GetTempPath(), "vsj-wave-" + Guid.NewGuid().ToString("N"));

    private readonly string _inputDir = Path.Combine(
        Path.GetTempPath(), "vsj-wavein-" + Guid.NewGuid().ToString("N"));

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

    private FfmpegWaveformService NewService(IFfmpegRunner runner, int sampleRate = 4000, int maxEntries = 16) =>
        new(runner, _cacheRoot, sampleRate, maxEntries);

    /// <summary>Little-endian byte blob for a sequence of Int16 samples (the PCM the runner "produces").</summary>
    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var u = unchecked((ushort)samples[i]);
            bytes[i * 2] = (byte)(u & 0xFF);
            bytes[(i * 2) + 1] = (byte)((u >> 8) & 0xFF);
        }

        return bytes;
    }

    // ---- Args ----

    [Fact]
    public async Task GetPeaksAsync_BuildsMonoLowRateS16leTempArgs()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(1000, 2000, 3000, 4000));
        var svc = NewService(runner, sampleRate: 4000);

        var peaks = await svc.GetPeaksAsync(input, buckets: 4, CancellationToken.None);

        peaks.Should().NotBeNull();
        runner.Commands.Should().ContainSingle();
        var tokens = runner.Commands[0];

        // Input precedes the audio-extraction flags.
        var iIndex = tokens.IndexOf("-i");
        iIndex.Should().BeGreaterThanOrEqualTo(0);
        tokens[iIndex + 1].Should().Be(input);

        // Mono + low sample rate + raw signed 16-bit LE PCM + overwrite.
        tokens.Should().ContainInConsecutiveOrder("-ac", "1");
        tokens.Should().ContainInConsecutiveOrder("-ar", "4000");
        tokens.Should().ContainInConsecutiveOrder("-f", "s16le");
        tokens.Should().Contain("-y");
        tokens.Should().Contain("-vn");

        // The output token (last) is the temp .pcm the service read back.
        tokens[^1].Should().EndWith(".pcm");
    }

    [Fact]
    public async Task GetPeaksAsync_SampleRateFlowsIntoArgs()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(100, 200));
        var svc = NewService(runner, sampleRate: 8000);

        await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);

        var tokens = runner.Commands[0];
        tokens[tokens.IndexOf("-ar") + 1].Should().Be("8000");
    }

    // ---- Bucketing + normalization ----

    [Fact]
    public async Task GetPeaksAsync_ReturnsNormalizedMaxAbsPerBucket()
    {
        var input = MakeInput();

        // 4 samples, 2 buckets → window 0 = {16384, -8192}, window 1 = {32767, 0}.
        // max-abs bucket 0 = 16384 → 16384/32768 = 0.5; bucket 1 = 32767 → ~0.99997.
        var runner = new WritingPcmRunner(Pcm(16384, -8192, 32767, 0));
        var svc = NewService(runner);

        var peaks = await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);

        peaks.Should().NotBeNull();
        peaks!.Should().HaveCount(2);
        peaks![0].Should().BeApproximately(0.5f, 1e-4f);
        peaks![1].Should().BeApproximately(32767f / 32768f, 1e-4f);
    }

    [Fact]
    public async Task GetPeaksAsync_MaxNegativeSample_NormalizesToOne()
    {
        var input = MakeInput();

        // short.MinValue (-32768): |sample| = 32768 → 32768/32768 = 1.0 exactly (the clamp boundary).
        var runner = new WritingPcmRunner(Pcm(short.MinValue, short.MinValue));
        var svc = NewService(runner);

        var peaks = await svc.GetPeaksAsync(input, buckets: 1, CancellationToken.None);

        peaks.Should().NotBeNull();
        peaks![0].Should().BeApproximately(1.0f, 1e-6f);
    }

    [Fact]
    public async Task GetPeaksAsync_AllValuesWithinZeroToOne()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(-30000, 5, 12345, -1, 9999, -20000, 32767, -32768));
        var svc = NewService(runner);

        var peaks = await svc.GetPeaksAsync(input, buckets: 3, CancellationToken.None);

        peaks.Should().NotBeNull();
        peaks!.Should().OnlyContain(v => v >= 0f && v <= 1f);
    }

    [Fact]
    public async Task GetPeaksAsync_ReturnsExactRequestedLength_EvenWhenMoreBucketsThanSamples()
    {
        var input = MakeInput();

        // 3 samples but 10 buckets → array length must be 10; empty windows are 0.
        var runner = new WritingPcmRunner(Pcm(10000, 20000, 30000));
        var svc = NewService(runner);

        var peaks = await svc.GetPeaksAsync(input, buckets: 10, CancellationToken.None);

        peaks.Should().NotBeNull();
        peaks!.Should().HaveCount(10);
        // At least the populated windows are non-zero; empty windows are exactly 0.
        peaks.Should().Contain(v => v > 0f);
        peaks.Should().Contain(v => v == 0f);
    }

    [Fact]
    public async Task GetPeaksAsync_SilentPcm_ReturnsAllZeroPeaks_NotNull()
    {
        var input = MakeInput();

        // Non-empty but all-zero PCM (a silent-but-present audio track) → a valid all-zero waveform, NOT null.
        var runner = new WritingPcmRunner(Pcm(0, 0, 0, 0, 0, 0));
        var svc = NewService(runner);

        var peaks = await svc.GetPeaksAsync(input, buckets: 3, CancellationToken.None);

        peaks.Should().NotBeNull();
        peaks!.Should().HaveCount(3);
        peaks.Should().OnlyContain(v => v == 0f);
    }

    // ---- Cache ----

    [Fact]
    public async Task GetPeaksAsync_SameKeyTwice_SecondCallHitsCache_RunnerCalledOnce()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(1000, 2000, 3000, 4000));
        var svc = NewService(runner);

        var first = await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);
        var second = await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().Equal(first);
        runner.CallCount.Should().Be(1, "the second same-key request must hit the cache");
    }

    [Fact]
    public async Task GetPeaksAsync_CacheHit_ReturnsIndependentCopy()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(16384, 16384));
        var svc = NewService(runner);

        var first = await svc.GetPeaksAsync(input, buckets: 1, CancellationToken.None);
        first![0] = 999f; // mutate the caller's copy

        var second = await svc.GetPeaksAsync(input, buckets: 1, CancellationToken.None);

        // Mutating the first result must not corrupt the cached array handed to the second call.
        second![0].Should().BeApproximately(0.5f, 1e-4f);
        runner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetPeaksAsync_DifferentBucketCounts_RunTwice()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(1000, 2000, 3000, 4000));
        var svc = NewService(runner);

        await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);
        await svc.GetPeaksAsync(input, buckets: 4, CancellationToken.None);

        runner.CallCount.Should().Be(2, "a different bucket count is a distinct cache key");
    }

    [Fact]
    public async Task GetPeaksAsync_FileChanged_ReExtracts()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(1000, 2000));
        var svc = NewService(runner);

        await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);

        // Rewrite the input so its length + last-write-time change → new cache key → re-extract.
        await Task.Delay(20);
        File.WriteAllText(input, "placeholder-with-more-bytes-now");

        await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);

        runner.CallCount.Should().Be(2, "a changed file (mtime/length) invalidates the cache key");
    }

    [Fact]
    public async Task GetPeaksAsync_CacheBounded_EvictsOldest()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(1000, 2000, 3000, 4000));
        // Cap of 2 entries; 3 distinct bucket counts → the oldest key is evicted from memory.
        var svc = NewService(runner, maxEntries: 2);

        await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None); // oldest
        await svc.GetPeaksAsync(input, buckets: 3, CancellationToken.None);
        await svc.GetPeaksAsync(input, buckets: 4, CancellationToken.None);

        var before = runner.CallCount; // 3 so far
        // Re-request the evicted key (buckets:2) → must re-run ffmpeg.
        await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);
        runner.CallCount.Should().Be(before + 1, "an evicted cache entry must be re-extracted");
    }

    // ---- No audio / empty PCM / failure ----

    [Fact]
    public async Task GetPeaksAsync_EmptyPcm_NoAudio_ReturnsNull()
    {
        var input = MakeInput();
        // Exit 0 but writes an EMPTY pcm file (no audio track) → null, and NOT cached.
        var runner = new WritingPcmRunner(Array.Empty<byte>());
        var svc = NewService(runner);

        var result = await svc.GetPeaksAsync(input, buckets: 8, CancellationToken.None);

        result.Should().BeNull();
        // A no-audio result is not cached → a retry re-runs the runner.
        await svc.GetPeaksAsync(input, buckets: 8, CancellationToken.None);
        runner.CallCount.Should().Be(2, "an empty-PCM (no-audio) result must not be cached");
    }

    [Fact]
    public async Task GetPeaksAsync_RunnerSuccessButNoFile_ReturnsNull()
    {
        var input = MakeInput();
        // Exit 0 but writes NO temp file at all → the service's existence check fails → null.
        var runner = new NoFilePcmRunner();
        var svc = NewService(runner);

        var result = await svc.GetPeaksAsync(input, buckets: 8, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPeaksAsync_FfmpegFailure_ReturnsNull_NoThrow_NotCached()
    {
        var input = MakeInput();
        var runner = new FailingPcmRunner();
        var svc = NewService(runner);

        var result = await svc.GetPeaksAsync(input, buckets: 8, CancellationToken.None);

        result.Should().BeNull();
        await svc.GetPeaksAsync(input, buckets: 8, CancellationToken.None);
        runner.CallCount.Should().Be(2, "a failed extraction must not be cached");
    }

    [Fact]
    public async Task GetPeaksAsync_RunnerThrows_ReturnsNull_NoThrow()
    {
        var input = MakeInput();
        var runner = new ThrowingPcmRunner();
        var svc = NewService(runner);

        var act = async () => await svc.GetPeaksAsync(input, buckets: 8, CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Which.Should().BeNull("any runner exception resolves to best-effort null");
    }

    [Fact]
    public async Task GetPeaksAsync_MissingInput_ReturnsNull_LetsFfmpegModelIt()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(1, 2));
        var svc = NewService(runner);

        // Empty/whitespace path short-circuits before any run.
        var empty = await svc.GetPeaksAsync("   ", buckets: 8, CancellationToken.None);
        empty.Should().BeNull();
        runner.CallCount.Should().Be(0, "an empty input path never launches ffmpeg");
        _ = input;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task GetPeaksAsync_NonPositiveBuckets_ReturnsNull_NoRun(int buckets)
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(1, 2, 3, 4));
        var svc = NewService(runner);

        var result = await svc.GetPeaksAsync(input, buckets, CancellationToken.None);

        result.Should().BeNull();
        runner.CallCount.Should().Be(0, "a non-positive bucket count never launches ffmpeg");
    }

    // ---- Cancellation ----

    [Fact]
    public async Task GetPeaksAsync_Cancelled_ReturnsNull_NoThrow()
    {
        var input = MakeInput();
        var runner = new CancellingPcmRunner();
        var svc = NewService(runner);

        using var cts = new CancellationTokenSource();
        var result = await svc.GetPeaksAsync(input, buckets: 8, cts.Token);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPeaksAsync_PreCancelledToken_ReturnsNull_RunnerNotCalled()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(1, 2, 3, 4));
        var svc = NewService(runner);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await svc.GetPeaksAsync(input, buckets: 8, cts.Token);

        result.Should().BeNull();
        runner.CallCount.Should().Be(0, "an already-cancelled request must not launch ffmpeg");
    }

    // ---- Clear / ClearAll ----

    [Fact]
    public async Task Clear_RemovesInputCacheDir_AndDropsMemoryEntry_BestEffort()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(1000, 2000, 3000, 4000));
        var svc = NewService(runner);

        await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);
        var tempDir = svc.InputCacheDir(input);
        Directory.Exists(tempDir).Should().BeTrue();

        svc.Clear(input);
        Directory.Exists(tempDir).Should().BeFalse("Clear deletes the input's cache dir");

        // After Clear the in-memory entry is dropped too → same key re-runs ffmpeg.
        var before = runner.CallCount;
        await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);
        runner.CallCount.Should().Be(before + 1);
    }

    [Fact]
    public async Task ClearAll_RemovesWholeCacheRoot_AndMemory()
    {
        var input = MakeInput();
        var runner = new WritingPcmRunner(Pcm(1000, 2000, 3000, 4000));
        var svc = NewService(runner);

        await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);
        Directory.Exists(_cacheRoot).Should().BeTrue();

        svc.ClearAll();
        Directory.Exists(_cacheRoot).Should().BeFalse("ClearAll deletes the whole cache root");

        // Memory cleared → re-runs.
        var before = runner.CallCount;
        await svc.GetPeaksAsync(input, buckets: 2, CancellationToken.None);
        runner.CallCount.Should().Be(before + 1);
    }

    [Fact]
    public void Clear_MissingDir_IsNoOp_NoThrow()
    {
        var runner = new WritingPcmRunner(Pcm(1, 2));
        var svc = NewService(runner);

        var act1 = () => svc.Clear(Path.Combine(_inputDir, "never.mp4"));
        var act2 = () => svc.ClearAll();
        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }

    [Fact]
    public void Clear_EmptyPath_IsNoOp_NoThrow()
    {
        var runner = new WritingPcmRunner(Pcm(1, 2));
        var svc = NewService(runner);
        var act = () => svc.Clear("   ");
        act.Should().NotThrow();
    }

    // ---- ComputePeaks direct (pure helper) ----

    [Fact]
    public void ComputePeaks_NullOrEmpty_ReturnsNull()
    {
        FfmpegWaveformService.ComputePeaks(Array.Empty<byte>(), 4).Should().BeNull();
        FfmpegWaveformService.ComputePeaks(new byte[] { 0x01 }, 4).Should().BeNull("< 1 whole sample");
    }

    [Fact]
    public void ComputePeaks_NonPositiveBuckets_ReturnsNull()
    {
        var pcm = Pcm(1000, 2000);
        FfmpegWaveformService.ComputePeaks(pcm, 0).Should().BeNull();
        FfmpegWaveformService.ComputePeaks(pcm, -1).Should().BeNull();
    }

    [Fact]
    public void ComputePeaks_KnownSamples_ExactValues()
    {
        // 6 samples, 3 buckets → windows {8192,-8192}, {16384,-16384}, {24576,-24576}.
        var pcm = Pcm(8192, -8192, 16384, -16384, 24576, -24576);
        var peaks = FfmpegWaveformService.ComputePeaks(pcm, 3);

        peaks.Should().NotBeNull();
        peaks!.Should().HaveCount(3);
        peaks![0].Should().BeApproximately(8192f / 32768f, 1e-4f);  // 0.25
        peaks![1].Should().BeApproximately(16384f / 32768f, 1e-4f); // 0.5
        peaks![2].Should().BeApproximately(24576f / 32768f, 1e-4f); // 0.75
    }

    [Fact]
    public void BuildArgs_TokenOrder_InputBeforeExtractionFlags_TempLast()
    {
        var args = FfmpegWaveformService.BuildArgs("/videos/in.mp4", 4000, "/tmp/out.pcm");
        var tokens = args.ToList().ToList();

        tokens.IndexOf("-i").Should().BeLessThan(tokens.IndexOf("-f"));
        tokens[^1].Should().Be("/tmp/out.pcm");
        tokens[tokens.IndexOf("-ar") + 1].Should().Be(4000.ToString(CultureInfo.InvariantCulture));
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

/// <summary>
/// Success runner that materializes the requested output file (last token) with a caller-supplied raw PCM blob
/// so the service reads back known samples. Records every command's token list + a call count.
/// </summary>
internal sealed class WritingPcmRunner : IFfmpegRunner
{
    private readonly byte[] _pcm;

    public WritingPcmRunner(byte[] pcm) => _pcm = pcm;

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

        File.WriteAllBytes(output, _pcm);
        return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
    }
}

/// <summary>Runner that honors the token by cancelling — models a superseded request.</summary>
internal sealed class CancellingPcmRunner : IFfmpegRunner
{
    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        throw new OperationCanceledException();
}

/// <summary>Runner that returns a non-zero exit (ffmpeg failure) and writes no file.</summary>
internal sealed class FailingPcmRunner : IFfmpegRunner
{
    public int CallCount { get; private set; }

    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(new FfmpegResult(1, new[] { "no audio stream" }.ToList().AsReadOnly()));
    }
}

/// <summary>Runner that exits 0 but writes NO output file — the service must still return null.</summary>
internal sealed class NoFilePcmRunner : IFfmpegRunner
{
    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
}

/// <summary>Runner that throws a non-cancellation exception — the service must swallow it → null.</summary>
internal sealed class ThrowingPcmRunner : IFfmpegRunner
{
    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        throw new InvalidOperationException("boom");
}
