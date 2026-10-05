using System.Collections.Concurrent;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-193 — <see cref="MediaProbe.ProbeAsync"/> caches a successful metadata probe per file version, keyed exactly
/// like the keyframe cache (<c>full path | LastWriteTimeUtc ticks | length</c>), so Join, Split and Bulk Cut stop
/// re-running ffprobe for a file they already probed. Only success is cached; the empty-path and missing-file guards
/// run before the lookup. Binary-free: a <see cref="ProbeCountingFfprobeRunner"/> stands in for ffprobe and counts
/// the metadata queries apart from the keyframe queries.
/// </summary>
public sealed class MediaProbeProbeCacheTests : IDisposable
{
    private readonly string _dir;

    public MediaProbeProbeCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-probecache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string NewFile(string name, string content = "placeholder")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static MediaInfo InfoOf(ProbeResult result)
    {
        result.Should().BeOfType<ProbeResult.ProbeSucceeded>();
        return ((ProbeResult.ProbeSucceeded)result).Info;
    }

    // ---- Required-Success -------------------------------------------------------------------

    // SPEC-004#I33 — a repeat probe of the unchanged file is a cache hit: ffprobe runs once.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_SameUnchangedFile_RepeatedCalls_RunFfprobeOnce()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var probe = new MediaProbe(runner);
        var file = NewFile("a.mp4");

        var first = await probe.ProbeAsync(file);
        var second = await probe.ProbeAsync(file);
        var third = await probe.ProbeAsync(file);

        runner.MetadataCallCount.Should().Be(1, "the second and third calls on the unchanged file are cache hits");
        InfoOf(second).Should().BeSameAs(InfoOf(first), "the cached immutable MediaInfo is shared, not re-parsed");
        InfoOf(third).Should().BeSameAs(InfoOf(first));
        InfoOf(first).Duration.Should().Be(TimeSpan.FromSeconds(10));
    }

    // SPEC-004#I33 — the key is the FULL path, so one file named two ways is one cache entry.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_TheSameFileNamedTwoWays_IsOneCacheEntry()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var probe = new MediaProbe(runner);
        var file = NewFile("b.mp4");
        var sameFileOtherSpelling = Path.Combine(_dir, ".", "b.mp4");

        await probe.ProbeAsync(file);
        var again = await probe.ProbeAsync(sameFileOtherSpelling);

        again.IsSuccess.Should().BeTrue();
        runner.MetadataCallCount.Should().Be(1, "BuildCacheKey resolves the full path, so '.\\b.mp4' is the same entry");
    }

    // SPEC-004#I33 — the cache is per file: two files are probed once each and never served each other's result.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_TwoFiles_EachProbedOnce_EachGetsItsOwnResult()
    {
        var runner = new ProbeCountingFfprobeRunner();
        runner.ThenMetadata(ProbeCountingFfprobeRunner.MetadataJson(10));
        runner.ThenMetadata(ProbeCountingFfprobeRunner.MetadataJson(20));
        var probe = new MediaProbe(runner);
        var a = NewFile("a.mp4");
        var b = NewFile("b.mp4");

        InfoOf(await probe.ProbeAsync(a)).Duration.Should().Be(TimeSpan.FromSeconds(10));
        InfoOf(await probe.ProbeAsync(b)).Duration.Should().Be(TimeSpan.FromSeconds(20));
        InfoOf(await probe.ProbeAsync(a)).Duration.Should().Be(TimeSpan.FromSeconds(10), "a's own cached result");
        InfoOf(await probe.ProbeAsync(b)).Duration.Should().Be(TimeSpan.FromSeconds(20), "b's own cached result");

        runner.MetadataCallCount.Should().Be(2, "one ffprobe per distinct file");
        runner.MetadataInputs.Select(Path.GetFileName).Should().Equal("a.mp4", "b.mp4");
    }

    // SPEC-004#I33 — the metadata cache and the keyframe cache share a key but not a store: neither answers for the other.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_AndGetKeyframesAsync_KeepSeparateCaches()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var probe = new MediaProbe(runner);
        var file = NewFile("c.mp4");

        var keyframes = await probe.GetKeyframesAsync(file);
        var probed = await probe.ProbeAsync(file);
        var probedAgain = await probe.ProbeAsync(file);
        var keyframesAgain = await probe.GetKeyframesAsync(file);

        runner.MetadataCallCount.Should().Be(1, "a cached keyframe list is not a metadata probe");
        runner.KeyframeCallCount.Should().Be(1, "a cached metadata probe is not a keyframe list");
        InfoOf(probed).Should().BeSameAs(InfoOf(probedAgain));
        keyframesAgain.Should().Equal(keyframes);
    }

    // ---- Required-Fail: a modified file is never served stale --------------------------------

    // SPEC-004#I34 — same path, same mtime, a NEW LENGTH → a new key and a fresh probe (boundary cell).
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_LengthChanged_SameMtime_ReProbes()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var probe = new MediaProbe(runner);
        var file = NewFile("d.mp4", "short");
        var mtime = File.GetLastWriteTimeUtc(file);

        await probe.ProbeAsync(file);
        File.WriteAllText(file, "a-much-longer-body-so-the-length-changes");
        File.SetLastWriteTimeUtc(file, mtime);
        File.GetLastWriteTimeUtc(file).Should().Be(mtime, "the test isolates the length half of the key");

        await probe.ProbeAsync(file);

        runner.MetadataCallCount.Should().Be(2, "a changed length is a different file version");
    }

    // SPEC-004#I34 — same path, same length, a NEW MTIME → a new key and a fresh probe.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_MtimeChanged_SameLength_ReProbes()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var probe = new MediaProbe(runner);
        var file = NewFile("e.mp4", "same-length-body");
        var length = new FileInfo(file).Length;

        await probe.ProbeAsync(file);
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(5));
        new FileInfo(file).Length.Should().Be(length, "the test isolates the mtime half of the key");

        await probe.ProbeAsync(file);

        runner.MetadataCallCount.Should().Be(2, "a changed last-write time is a different file version");
    }

    // SPEC-004#I34 — the re-probe's result is the one returned (and cached), never the stale one.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_ModifiedFile_ReturnsTheFreshResult_AndCachesIt()
    {
        var runner = new ProbeCountingFfprobeRunner();
        runner.ThenMetadata(ProbeCountingFfprobeRunner.MetadataJson(10));
        runner.ThenMetadata(ProbeCountingFfprobeRunner.MetadataJson(25));
        var probe = new MediaProbe(runner);
        var file = NewFile("f.mp4", "v1");

        InfoOf(await probe.ProbeAsync(file)).Duration.Should().Be(TimeSpan.FromSeconds(10));
        File.WriteAllText(file, "version-two-is-longer");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));

        InfoOf(await probe.ProbeAsync(file)).Duration.Should().Be(TimeSpan.FromSeconds(25), "the edited file's own probe");
        InfoOf(await probe.ProbeAsync(file)).Duration.Should().Be(TimeSpan.FromSeconds(25), "the fresh result is now the cached one");
        runner.MetadataCallCount.Should().Be(2);
    }

    // ---- Required-Fail / Optional-Fail: failures and cancellations are never cached ----------

    // SPEC-004#I35 — a failed probe (non-zero exit, unparsable JSON, zero streams) is not cached; the next call
    // runs ffprobe again and its success IS cached.
    [Trait("serves-spec", "SPEC-004")]
    [Theory]
    [InlineData("exit")]
    [InlineData("bad-json")]
    [InlineData("no-streams")]
    public async Task ProbeAsync_FailedProbe_IsNotCached_NextCallRunsFfprobeAgain(string failure)
    {
        var runner = new ProbeCountingFfprobeRunner();
        switch (failure)
        {
            case "exit":
                runner.ThenMetadata(_ => throw new FfprobeException(1, new[] { "moov atom not found" }));
                break;
            case "bad-json":
                runner.ThenMetadata("this is not json");
                break;
            default:
                runner.ThenMetadata("""{"streams":[]}""");
                break;
        }

        var probe = new MediaProbe(runner);
        var file = NewFile("g.mp4");

        var failed = await probe.ProbeAsync(file);
        var retried = await probe.ProbeAsync(file);
        var cachedSuccess = await probe.ProbeAsync(file);

        failed.Should().BeOfType<ProbeResult.ProbeFailed>();
        retried.IsSuccess.Should().BeTrue("the retry ran ffprobe again instead of replaying the failure");
        InfoOf(cachedSuccess).Should().BeSameAs(InfoOf(retried), "the success after the failure is what gets cached");
        runner.MetadataCallCount.Should().Be(2, "failure (not cached) + success (cached) — the third call is a hit");
    }

    // SPEC-004#I35 — a probe cancelled through its token throws OperationCanceledException and caches nothing.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_CancelledWhileRunning_Throws_CachesNothing_NextCallProbes()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.ThenMetadata(async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ProbeCountingFfprobeRunner.MetadataJson(10);
        });
        var probe = new MediaProbe(runner);
        var file = NewFile("h.mp4");
        using var cts = new CancellationTokenSource();

        var running = probe.ProbeAsync(file, cts.Token);
        await entered.Task;
        cts.Cancel();

        var act = async () => await running;
        await act.Should().ThrowAsync<OperationCanceledException>("cancellation propagates, it is never a ProbeFailed (I8)");

        var next = await probe.ProbeAsync(file);
        next.IsSuccess.Should().BeTrue();
        runner.MetadataCallCount.Should().Be(2, "the cancelled probe left nothing cached, so the next call ran ffprobe");
    }

    // SPEC-004#I35 — a runner that throws OperationCanceledException outright (no token involved) caches nothing either.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_RunnerThrowsCancellation_CachesNothing()
    {
        var runner = new ProbeCountingFfprobeRunner();
        runner.ThenMetadata(_ => throw new OperationCanceledException());
        var probe = new MediaProbe(runner);
        var file = NewFile("i.mp4");

        var act = () => probe.ProbeAsync(file);
        await act.Should().ThrowAsync<OperationCanceledException>();

        (await probe.ProbeAsync(file)).IsSuccess.Should().BeTrue();
        (await probe.ProbeAsync(file)).IsSuccess.Should().BeTrue();
        runner.MetadataCallCount.Should().Be(2, "cancelled (not cached) + success (cached) — the third call is a hit");
    }

    // SPEC-004#I35 / I8 — a cache hit still honours a cancelled token: the call throws, as it did when every call
    // reached ffprobe, so the cache changes nothing but the process it saves.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_CachedFile_CancelledToken_StillThrows_AndKeepsTheEntry()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var probe = new MediaProbe(runner);
        var file = NewFile("l.mp4");
        var first = await probe.ProbeAsync(file);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => probe.ProbeAsync(file, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        InfoOf(await probe.ProbeAsync(file)).Should().BeSameAs(InfoOf(first), "the entry is untouched by the cancelled call");
        runner.MetadataCallCount.Should().Be(1);
    }

    // ---- Boundaries: the guards run before any cache lookup ----------------------------------

    // SPEC-004#I36 — an empty/whitespace path returns Failure with zero runner calls (I1 before the cache).
    [Trait("serves-spec", "SPEC-004")]
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProbeAsync_EmptyPath_ReturnsFailure_WithNoRunnerCall(string path)
    {
        var runner = new ProbeCountingFfprobeRunner();
        var probe = new MediaProbe(runner);

        var result = await probe.ProbeAsync(path);
        var again = await probe.ProbeAsync(path);

        result.Should().BeOfType<ProbeResult.ProbeFailed>();
        again.Should().BeOfType<ProbeResult.ProbeFailed>();
        runner.MetadataCallCount.Should().Be(0);
    }

    // SPEC-004#I36 — a missing file returns Failure with zero runner calls and caches nothing: once the file exists,
    // it is probed.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_MissingFile_NoRunnerCall_NothingCached()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var probe = new MediaProbe(runner);
        var path = Path.Combine(_dir, "not-yet.mp4");

        var missing = await probe.ProbeAsync(path);
        missing.Should().BeOfType<ProbeResult.ProbeFailed>();
        ((ProbeResult.ProbeFailed)missing).Reason.Should().Contain("File does not exist");
        runner.MetadataCallCount.Should().Be(0, "the missing-file guard answers without ffprobe");

        File.WriteAllText(path, "now it exists");
        var present = await probe.ProbeAsync(path);

        present.IsSuccess.Should().BeTrue("no failure was cached for the missing state");
        runner.MetadataCallCount.Should().Be(1);
    }

    // SPEC-004#I36 — a cached entry never masks a file that has since been deleted: the missing-file guard runs first.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public async Task ProbeAsync_FileDeletedAfterACachedProbe_ReturnsFileDoesNotExist()
    {
        var runner = new ProbeCountingFfprobeRunner();
        var probe = new MediaProbe(runner);
        var file = NewFile("j.mp4");

        (await probe.ProbeAsync(file)).IsSuccess.Should().BeTrue();
        File.Delete(file);

        var gone = await probe.ProbeAsync(file);

        gone.Should().BeOfType<ProbeResult.ProbeFailed>();
        ((ProbeResult.ProbeFailed)gone).Reason.Should().Contain("File does not exist");
        runner.MetadataCallCount.Should().Be(1, "the guard answered; neither the cache nor ffprobe was asked");
    }

    // SPEC-004#I36 — the key builder never throws for a file that vanished between the guard and the lookup; the
    // probe then runs uncached, exactly as it did before the cache existed.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public void TryBuildCacheKey_FileGone_ReturnsFalse_DoesNotThrow()
    {
        var path = Path.Combine(_dir, "vanished.mp4");

        var ok = MediaProbe.TryBuildCacheKey(path, out var key);

        ok.Should().BeFalse();
        key.Should().BeEmpty();
    }

    // SPEC-004#I33 / I34 — the key is the keyframe cache's: full path | LastWriteTimeUtc ticks | length.
    [Trait("serves-spec", "SPEC-004")]
    [Fact]
    public void TryBuildCacheKey_IsFullPath_MtimeTicks_Length()
    {
        var file = NewFile("k.mp4", "12345");
        var info = new FileInfo(file);

        MediaProbe.TryBuildCacheKey(Path.Combine(_dir, ".", "k.mp4"), out var key).Should().BeTrue();

        key.Should().Be($"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|5");
    }
}

/// <summary>
/// An <see cref="IFfprobeRunner"/> that tells the metadata probe (<c>-show_format</c>) apart from the keyframe queries
/// and counts each. On its own it answers from a script (then a default valid payload); built with
/// <see cref="Over"/> it forwards every call to a real runner and only counts. Thread-safe counters.
/// </summary>
internal sealed class ProbeCountingFfprobeRunner : IFfprobeRunner
{
    private const string PacketsJson = """
        {"packets":[
          {"pts_time":"0.000000","dts_time":"0.000000","flags":"K__"},
          {"pts_time":"1.000000","dts_time":"1.000000","flags":"K__"}
        ]}
        """;

    private readonly IFfprobeRunner? _inner;
    private readonly ConcurrentQueue<Func<CancellationToken, Task<string>>> _script = new();
    private readonly ConcurrentQueue<string> _metadataInputs = new();
    private int _metadataCalls;
    private int _keyframeCalls;

    public ProbeCountingFfprobeRunner()
    {
    }

    private ProbeCountingFfprobeRunner(IFfprobeRunner inner)
    {
        _inner = inner;
    }

    /// <summary>Count the calls a real runner makes, forwarding each one unchanged.</summary>
    public static ProbeCountingFfprobeRunner Over(IFfprobeRunner inner) => new(inner ?? throw new ArgumentNullException(nameof(inner)));

    /// <summary>A valid ffprobe <c>-show_streams -show_format</c> payload: 320x240 video (H.264 unless named) + AAC.</summary>
    public static string MetadataJson(double seconds, string videoCodec = "h264")
    {
        var d = seconds.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        return "{\"streams\":["
            + "{\"index\":0,\"codec_name\":\"" + videoCodec + "\",\"codec_type\":\"video\",\"width\":320,\"height\":240,"
            + "\"pix_fmt\":\"yuv420p\",\"time_base\":\"1/15360\",\"duration\":\"" + d + "\"},"
            + "{\"index\":1,\"codec_name\":\"aac\",\"codec_type\":\"audio\",\"sample_rate\":\"48000\",\"channels\":2,"
            + "\"time_base\":\"1/48000\",\"duration\":\"" + d + "\"}],"
            + "\"format\":{\"format_name\":\"mov,mp4,m4a,3gp,3g2,mj2\",\"duration\":\"" + d + "\"}}";
    }

    /// <summary>Metadata probes run so far (ffprobe <c>-show_format</c> invocations).</summary>
    public int MetadataCallCount => Volatile.Read(ref _metadataCalls);

    /// <summary>Keyframe queries run so far (packet or frame scans) — never counted as metadata probes.</summary>
    public int KeyframeCallCount => Volatile.Read(ref _keyframeCalls);

    /// <summary>The input path of each metadata probe, in call order.</summary>
    public IReadOnlyList<string> MetadataInputs => _metadataInputs.ToArray();

    /// <summary>Script the next unscripted metadata probe's answer.</summary>
    public void ThenMetadata(string payload) => _script.Enqueue(_ => Task.FromResult(payload));

    /// <summary>Script the next unscripted metadata probe's behaviour (throw, wait on the token, …).</summary>
    public void ThenMetadata(Func<CancellationToken, Task<string>> step) => _script.Enqueue(step);

    public Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
    {
        var list = args.ToList();
        if (!list.Contains("-show_format"))
        {
            Interlocked.Increment(ref _keyframeCalls);
            return _inner is not null ? _inner.RunJsonAsync(args, ct) : Task.FromResult(PacketsJson);
        }

        Interlocked.Increment(ref _metadataCalls);
        var i = IndexOf(list, "-i");
        _metadataInputs.Enqueue(i >= 0 && i + 1 < list.Count ? list[i + 1] : string.Empty);

        if (_inner is not null)
        {
            return _inner.RunJsonAsync(args, ct);
        }

        return _script.TryDequeue(out var step) ? step(ct) : Task.FromResult(MetadataJson(10));
    }

    private static int IndexOf(IReadOnlyList<string> list, string token)
    {
        for (var k = 0; k < list.Count; k++)
        {
            if (list[k] == token)
            {
                return k;
            }
        }

        return -1;
    }
}
