using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-228 (SPEC-005 I35, I12, I15, I16): a picture file that a cancelled, killed or failed ffmpeg run left half-written is never handed
/// back and never cached. A scripted runner models each way a run can end (a kill in the middle of the write, a non-zero exit that left
/// a file, an exit 0 with an empty file); the service must run ffmpeg again for the next request, and the final file name must only
/// ever hold a whole picture (the grab writes under a work name beside it and publishes it when the run is done).
/// No real ffmpeg: <see cref="ThumbnailPartialFileIntegrationTests"/> covers the real binary.
/// </summary>
public class ThumbnailPartialFileTests : IDisposable
{
    private const string Whole = "whole-jpeg";
    private const string Half = "half-jpe";

    private readonly string _cacheRoot = Path.Combine(
        Path.GetTempPath(), "vsj-thumb-partial-" + Guid.NewGuid().ToString("N"));

    private readonly string _inputDir = Path.Combine(
        Path.GetTempPath(), "vsj-thumb-partial-in-" + Guid.NewGuid().ToString("N"));

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

    private FfmpegThumbnailService NewService(IFfmpegRunner runner) =>
        new(runner, _cacheRoot, TimeSpan.FromSeconds(1), 128);

    private static FfmpegResult Ok() => new(0, new List<string>().AsReadOnly());

    private static FfmpegResult Failed() => new(1, new[] { "boom" }.ToList().AsReadOnly());

    /// <summary>The files under the cache root, relative to it (the per-input hash folder is one level down).</summary>
    private string[] FilesUnderCache() =>
        Directory.Exists(_cacheRoot)
            ? Directory.GetFiles(_cacheRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_cacheRoot, f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    // ---- A run that ended without a whole picture leaves nothing the next request serves ----

    // SPEC-005#I35 + I14 — a run killed in the middle of the write (a latest-wins hover cancels the grab and FfmpegRunner kills the
    // process tree) leaves bytes on disk; the next request for that bucket and width runs ffmpeg again and gets the whole picture.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task Cancelled_MidWrite_TheNextRequestRunsFfmpegAgainAndGetsTheWholePicture()
    {
        var input = MakeInput();
        var runner = new ScriptedRunner((output, _) =>
        {
            File.WriteAllText(output, Half);
            throw new OperationCanceledException();
        });
        var svc = NewService(runner);

        var first = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);
        var second = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        first.Should().BeNull("a cancelled grab resolves to no thumbnail");
        runner.CallCount.Should().Be(2, "the half-written file is not a cache hit: the next request runs ffmpeg");
        second.Should().NotBeNull();
        File.ReadAllText(second!).Should().Be(Whole, "the next request is served a whole picture, never the leftover");
    }

    // SPEC-005#I35 + I15 — a failed run (non-zero exit) that left a file behind: the next request runs ffmpeg again.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task FailedRun_ThatLeftAFile_TheNextRequestRunsFfmpegAgain()
    {
        var input = MakeInput();
        var runner = new ScriptedRunner((output, _) =>
        {
            File.WriteAllText(output, Half);
            return Failed();
        });
        var svc = NewService(runner);

        var first = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);
        var second = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        first.Should().BeNull();
        runner.CallCount.Should().Be(2);
        File.ReadAllText(second!).Should().Be(Whole);
    }

    // SPEC-005#I35 + I16 — an exit 0 that wrote an EMPTY file is not a picture: null, not cached, run again next time.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task ExitZero_WithAnEmptyFile_IsNotAPicture_AndIsNotCached()
    {
        var input = MakeInput();
        var runner = new ScriptedRunner((output, _) =>
        {
            File.WriteAllBytes(output, Array.Empty<byte>());
            return Ok();
        });
        var svc = NewService(runner);

        var first = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);
        var second = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        first.Should().BeNull("a 0-byte file is not a picture");
        runner.CallCount.Should().Be(2, "the empty result was not cached");
        File.ReadAllText(second!).Should().Be(Whole);
    }

    // SPEC-005#I12 + I35 — a 0-byte file a PRIOR process left under the final name (an older build wrote in place) is not reused.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task EmptyFile_LeftByAPriorProcess_IsNotReused()
    {
        var input = MakeInput();
        var runner = new ScriptedRunner();
        var svc = NewService(runner);
        var final = svc.ResolveTempPath(input, TimeSpan.FromSeconds(2), 160);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        File.WriteAllBytes(final, Array.Empty<byte>());

        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2.4), 160, CancellationToken.None);

        runner.CallCount.Should().Be(1, "an empty file is not a picture, so ffmpeg runs");
        path.Should().Be(final);
        File.ReadAllText(path!).Should().Be(Whole);
    }

    // The exact request shares the grab: a cut-point or profile picture killed in the middle of the write is run again too.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task ExactRequest_Cancelled_MidWrite_TheNextRequestRunsFfmpegAgain()
    {
        var input = MakeInput();
        var runner = new ScriptedRunner((output, _) =>
        {
            File.WriteAllText(output, Half);
            throw new OperationCanceledException();
        });
        var svc = NewService(runner);

        var first = await svc.GetExactThumbnailAsync(input, TimeSpan.FromSeconds(8.5), 640, CancellationToken.None);
        var second = await svc.GetExactThumbnailAsync(input, TimeSpan.FromSeconds(8.5), 640, CancellationToken.None);

        first.Should().BeNull();
        runner.CallCount.Should().Be(2);
        File.ReadAllText(second!).Should().Be(Whole);
        Path.GetFileName(second).Should().Be("x8500000_w640.jpg", "the picture ends under the exact request's own name");
    }

    // ---- The final name only ever holds a whole picture ----

    // SPEC-005#I35 — the run writes under a work name beside the final one (same folder, so the publish is a rename), the final name does
    // not exist while the run is in flight, and after it the final name holds the whole picture and nothing else is left.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task TheRunWritesUnderAWorkName_AndThePictureIsPublishedWhole()
    {
        var input = MakeInput();
        string? workOutput = null;
        var finalExistedDuringTheRun = true;
        var svc = default(FfmpegThumbnailService)!;
        var runner = new ScriptedRunner((output, _) =>
        {
            workOutput = output;
            finalExistedDuringTheRun = File.Exists(svc.ResolveTempPath(input, TimeSpan.FromSeconds(2), 160));
            File.WriteAllText(output, Whole);
            return Ok();
        });
        svc = NewService(runner);
        var final = svc.ResolveTempPath(input, TimeSpan.FromSeconds(2), 160);

        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2.2), 160, CancellationToken.None);

        path.Should().Be(final);
        workOutput.Should().NotBeNull();
        workOutput.Should().NotBe(final, "ffmpeg writes beside the final name, never to it");
        Path.GetDirectoryName(workOutput).Should().Be(Path.GetDirectoryName(final), "the same folder: publishing is one rename");
        Path.GetFileName(workOutput).Should().StartWith("2000_w160.").And.EndWith(".tmp.jpg");
        finalExistedDuringTheRun.Should().BeFalse("the final name is absent until the run is done");
        File.ReadAllText(final).Should().Be(Whole);
        FilesUnderCache().Should().ContainSingle().Which.Should().EndWith("2000_w160.jpg", "no work file is left behind");
    }

    // SPEC-005#I35 — every way a run can end leaves no work file; only a whole picture is left, and only on success.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData("cancel", 0)]
    [InlineData("fail", 0)]
    [InlineData("throw", 0)]
    [InlineData("empty", 0)]
    [InlineData("ok", 1)]
    public async Task NoWorkFileSurvives_AnyOutcome(string outcome, int expectedFiles)
    {
        var input = MakeInput();
        var runner = new ScriptedRunner((output, _) =>
        {
            if (outcome == "empty")
            {
                File.WriteAllBytes(output, Array.Empty<byte>());
                return Ok();
            }

            File.WriteAllText(output, outcome == "ok" ? Whole : Half);
            return outcome switch
            {
                "cancel" => throw new OperationCanceledException(),
                "throw" => throw new IOException("the volume went away"),
                "fail" => Failed(),
                _ => Ok(),
            };
        });
        var svc = NewService(runner);

        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(3), 160, CancellationToken.None);

        FilesUnderCache().Should().HaveCount(expectedFiles);
        FilesUnderCache().Where(f => f.EndsWith(".tmp.jpg", StringComparison.Ordinal)).Should().BeEmpty();
    }

    // SPEC-005#I35 — a flagged run (the MP4 stream-info skip, T-226) that fails after writing bytes is followed by the one retry,
    // judged on its own output: the retry's picture is whole, and the failed run's bytes are gone.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task FlaggedRun_ThatLeftBytesAndFailed_IsRetried_ToAWholePicture_WithNoLeftover()
    {
        var input = MakeInput(); // clip.mp4
        var runner = new ScriptedRunner((output, _) =>
        {
            File.WriteAllText(output, Half);
            return Failed();
        });
        var svc = new FfmpegThumbnailService(
            runner, _cacheRoot, TimeSpan.FromSeconds(1), 128, (_, _) => Task.FromResult<TimeSpan?>(TimeSpan.Zero));

        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        runner.CallCount.Should().Be(2, "the flagged run, then the retry with today's tokens");
        path.Should().NotBeNull();
        File.ReadAllText(path!).Should().Be(Whole);
        FilesUnderCache().Should().ContainSingle();
    }

    // Boundaries — two grabs of one (input, bucket, width) in flight at once: each writes under its own work name, the first to finish
    // publishes, the other finds the picture there and drops its own. Both get the one path, whole, and nothing is left over.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task TwoGrabsOfOneKey_InFlightTogether_BothGetTheOneWholePicture()
    {
        var input = MakeInput();
        var gate = new GatedRunner();
        var svc = NewService(gate);

        var a = svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(4), 160, CancellationToken.None);
        var b = svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(4), 160, CancellationToken.None);
        await gate.WaitUntilStartedAsync(2);
        gate.Outputs.Distinct().Should().HaveCount(2, "each in-flight grab has its own work file");
        gate.Release();

        var results = await Task.WhenAll(a, b);

        results[0].Should().NotBeNull();
        results[1].Should().Be(results[0], "both resolve to the one published picture");
        File.ReadAllText(results[0]!).Should().Be(Whole);
        FilesUnderCache().Should().ContainSingle();
    }

    // SPEC-005#I35 — the first grab to finish wins: a picture a racing grab already published under the final name may be open in the
    // app, so it is kept and the late grab's own picture is dropped (never renamed over it).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task APictureAlreadyPublished_WhileTheRunWasInFlight_IsKept_NotReplaced()
    {
        const string Racer = "published-by-the-racing-grab";
        var input = MakeInput();
        var svc = default(FfmpegThumbnailService)!;
        var runner = new ScriptedRunner((output, _) =>
        {
            File.WriteAllText(svc.ResolveTempPath(input, TimeSpan.FromSeconds(2), 160), Racer);
            File.WriteAllText(output, Whole);
            return Ok();
        });
        svc = NewService(runner);

        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        File.ReadAllText(path!).Should().Be(Racer, "the picture that was published first stays");
        FilesUnderCache().Should().ContainSingle("the late grab's own file is removed");
    }

    // SPEC-005#I35 — a killed ffmpeg may still hold its output for a moment after the cancel (the runner does not wait for the process);
    // the work file is removed once the hold is gone, not left in the folder.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task AWorkFile_StillHeldForAMomentAfterTheKill_IsRemovedOnceItIsReleased()
    {
        var input = MakeInput();
        var runner = new ScriptedRunner((output, token) =>
        {
            var held = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            held.Write(new byte[] { 0xFF, 0xD8 });
            _ = Task.Delay(100).ContinueWith(done => held.Dispose(), TaskScheduler.Default);
            throw new OperationCanceledException(token);
        });
        var svc = NewService(runner);

        var result = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        result.Should().BeNull();
        FilesUnderCache().Should().BeEmpty("the work file is deleted as soon as the killed process lets go of it");
    }

    // SPEC-005#I35 — a picture that cannot be put under its final name (the move is refused) is null and is not remembered: the
    // service never hands back a path that has no file behind it. The next request, once the refusal is gone, runs and publishes.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task APublishThatIsRefused_ResolvesToNull_AndTheNextRequestRunsAgain()
    {
        var input = MakeInput();
        FileStream? held = null;
        var runner = new ScriptedRunner((output, _) =>
        {
            // ffmpeg's output is still open without sharing when the run reports success, so renaming it is refused.
            held = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            held.Write(System.Text.Encoding.ASCII.GetBytes(Whole));
            held.Flush();
            return Ok();
        });
        var svc = NewService(runner);
        var final = svc.ResolveTempPath(input, TimeSpan.FromSeconds(2), 160);

        string? during;
        try
        {
            during = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);
            File.Exists(final).Should().BeFalse("nothing was published");
        }
        finally
        {
            held?.Dispose();
        }

        var after = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        during.Should().BeNull("the picture could not be put in place");
        runner.CallCount.Should().Be(2, "the refused grab was not cached");
        File.ReadAllText(after!).Should().Be(Whole);
    }

    // SPEC-005#I35 — a scanner or indexer can hold a freshly written file for a moment; the rename that is refused then is tried again,
    // and the picture is published once the hold is gone (the old code had no rename, so it could not miss a picture this way).
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task APublishRefusedForAMoment_IsRetried_AndThePictureIsPublished()
    {
        var input = MakeInput();
        var runner = new ScriptedRunner((output, token) =>
        {
            var held = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            held.Write(System.Text.Encoding.ASCII.GetBytes(Whole));
            held.Flush();
            _ = Task.Delay(60).ContinueWith(done => held.Dispose(), TaskScheduler.Default);
            return Ok();
        });
        var svc = NewService(runner);

        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        path.Should().NotBeNull("the rename was refused only for a moment");
        runner.CallCount.Should().Be(1, "the picture was published, not asked for again");
        File.ReadAllText(path!).Should().Be(Whole);
        FilesUnderCache().Should().ContainSingle();
    }

    // Boundaries — Clear during a grab removes the folder, work file included: the grab resolves to no thumbnail (never throws), is
    // not remembered, and the next request runs ffmpeg again.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task ClearDuringAGrab_ResolvesToNull_NeverThrows_AndTheNextRequestRunsAgain()
    {
        var input = MakeInput();
        var svc = default(FfmpegThumbnailService)!;
        var runner = new ScriptedRunner((output, _) =>
        {
            File.WriteAllText(output, Whole);
            svc.Clear(input); // the Split screen loaded another file mid-grab
            return Ok();
        });
        svc = NewService(runner);

        var during = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);
        var after = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);

        during.Should().BeNull("the work file went with the folder, so there is nothing to publish");
        runner.CallCount.Should().Be(2);
        after.Should().NotBeNull();
        File.ReadAllText(after!).Should().Be(Whole);
        FilesUnderCache().Should().ContainSingle();
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Runs one scripted behaviour for the first call (given the output token it was asked to write and the cancellation token);
    /// every later call, and every call when none is given, writes <see cref="Whole"/> and exits 0.
    /// </summary>
    private sealed class ScriptedRunner : IFfmpegRunner
    {
        private readonly Func<string, CancellationToken, FfmpegResult>? _first;

        public ScriptedRunner(Func<string, CancellationToken, FfmpegResult>? first = null) => _first = first;

        public int CallCount { get; private set; }

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            var output = args.ToList().Last();
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            if (CallCount == 1 && _first is not null)
            {
                return Task.FromResult(_first(output, ct));
            }

            File.WriteAllText(output, Whole);
            return Task.FromResult(Ok());
        }
    }

    /// <summary>Holds every run until released, so a test can have several grabs in flight at once.</summary>
    private sealed class GatedRunner : IFfmpegRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();

        public List<string> Outputs { get; } = new();

        public void Release() => _release.SetResult();

        public async Task WaitUntilStartedAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    if (Outputs.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("the runs did not start");
        }

        public async Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            var output = args.ToList().Last();
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            lock (_lock)
            {
                Outputs.Add(output);
            }

            await _release.Task.ConfigureAwait(false);
            File.WriteAllText(output, Whole);
            return Ok();
        }
    }
}
