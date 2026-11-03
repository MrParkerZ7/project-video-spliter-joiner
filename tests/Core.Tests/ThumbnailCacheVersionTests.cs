using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-243 (SPEC-005): the thumbnail cache is versioned. A picture an older build wrote is never served by a build that changed
/// what a grab returns, and the older version folders (plus work files a hard kill left) are swept once,
/// best-effort. A fake runner stands in for ffmpeg; the cache base is a temp folder, so nothing touches the per-user folder.
/// </summary>
public class ThumbnailCacheVersionTests : IDisposable
{
    private static readonly byte[] WholeJpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0xFF, 0xD9 };
    private static readonly byte[] FreshJpeg = { 0xFF, 0xD8, 0xFF, 0xDB, 0x09, 0x09, 0xFF, 0xD9 };
    private static readonly byte[] TruncatedJpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03, 0x04 };

    private readonly string _cacheBase = Path.Combine(
        Path.GetTempPath(), "vsj-thumb-ver-" + Guid.NewGuid().ToString("N"), "thumb-cache");

    private readonly string _inputDir = Path.Combine(
        Path.GetTempPath(), "vsj-thumb-ver-in-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TryDelete(Path.GetDirectoryName(_cacheBase)!);
        TryDelete(_inputDir);
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
            // Test cleanup only.
        }
    }

    private string MakeInput()
    {
        Directory.CreateDirectory(_inputDir);
        var input = Path.Combine(_inputDir, "clip.ts");
        File.WriteAllText(input, "placeholder");
        return input;
    }

    private string CurrentRoot => Path.Combine(_cacheBase, FfmpegThumbnailService.CacheVersionFolderName);

    /// <summary>The per-input folder the PREVIOUS layout used: directly under the cache base, no version folder.</summary>
    private string PreviousLayoutDir(string input) =>
        new FfmpegThumbnailService(new JpegWritingRunner(), _cacheBase).InputCacheDir(input);

    private FfmpegThumbnailService NewVersionedService(JpegWritingRunner runner) =>
        FfmpegThumbnailService.ForCacheBase(runner, _cacheBase);

    // ---- Repro: a picture from the previous layout is not served ----

    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task APictureWrittenUnderThePreviousLayout_IsNotReturned_TheGrabRunsAndTheNewFileIsReturned()
    {
        var input = MakeInput();
        var oldDir = PreviousLayoutDir(input);
        Directory.CreateDirectory(oldDir);
        var oldPicture = Path.Combine(oldDir, "1000_w160.jpg");
        await File.WriteAllBytesAsync(oldPicture, WholeJpeg);

        // Held open, so the sweep cannot delete it: only the version folder keeps it from being served.
        using var hold = new FileStream(oldPicture, FileMode.Open, FileAccess.Read, FileShare.None);
        var runner = new JpegWritingRunner();
        var svc = NewVersionedService(runner);
        var picture = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, CancellationToken.None);

        File.Exists(oldPicture).Should().BeTrue("the sweep could not remove it, and it was still not served");
        runner.CallCount.Should().Be(1, "the old build's picture is not this build's picture: ffmpeg runs");
        picture.Should().NotBeNull();
        picture.Should().NotBe(oldPicture);
        picture!.Should().StartWith(CurrentRoot + Path.DirectorySeparatorChar, "new pictures live under the version folder");
        (await File.ReadAllBytesAsync(picture!)).Should().Equal(FreshJpeg, "the file returned is the fresh grab, not the old bytes");
    }

    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void DefaultCacheRoot_IsTheVersionFolderUnderTheCacheBase()
    {
        FfmpegThumbnailService.DefaultCacheRoot().Should().Be(
            Path.Combine(FfmpegThumbnailService.DefaultCacheBase(), FfmpegThumbnailService.CacheVersionFolderName));
        FfmpegThumbnailService.CacheVersionFolderName.Should().Be("v" + FfmpegThumbnailService.CacheVersion);
        FfmpegThumbnailService.ForCacheBase(new JpegWritingRunner(), _cacheBase).CacheRoot.Should().Be(CurrentRoot);
    }

    // ---- The older versions are removed once ----

    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task OlderVersionFolders_AreRemovedOnce_AndASecondStartFindsNothingToRemove()
    {
        var input = MakeInput();
        var legacyDir = PreviousLayoutDir(input); // <base>/<hash>: what every build before T-243 wrote
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllBytesAsync(Path.Combine(legacyDir, "1000_w160.jpg"), WholeJpeg);
        var v1 = Path.Combine(_cacheBase, "v1");
        Directory.CreateDirectory(Path.Combine(v1, HashA));
        File.WriteAllBytes(Path.Combine(v1, HashA, "1000_w160.jpg"), WholeJpeg);
        var newer = Path.Combine(_cacheBase, "v" + (FfmpegThumbnailService.CacheVersion + 1));
        Directory.CreateDirectory(newer);
        var unrelated = Path.Combine(_cacheBase, "not-ours");
        Directory.CreateDirectory(unrelated);

        var runner = new JpegWritingRunner();
        var svc = NewVersionedService(runner);
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, CancellationToken.None);

        Directory.Exists(legacyDir).Should().BeFalse("the pre-version layout is swept");
        Directory.Exists(v1).Should().BeFalse("an older version folder is swept");
        Directory.Exists(newer).Should().BeTrue("a newer build's folder is not ours to delete");
        Directory.Exists(unrelated).Should().BeTrue("only thumbnail layouts are touched");
        Directory.Exists(CurrentRoot).Should().BeTrue("the current version's pictures stay");

        // A second start: nothing left to remove, the still-valid picture is kept and served without ffmpeg.
        FfmpegThumbnailService.SweepStaleCache(_cacheBase).Should().Be(0);
        var second = NewVersionedService(runner);
        var again = await second.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, CancellationToken.None);
        again.Should().NotBeNull();
        runner.CallCount.Should().Be(1, "the current version's picture survived the second start");
    }

    [Fact]
    public async Task TheFirstGrab_StaysPendingUntilTheSweepIsDone_AndEveryGrabWaitsForTheSameOneSweep()
    {
        var input = MakeInput();
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var sweeps = 0;
        var runner = new JpegWritingRunner();
        var svc = FfmpegThumbnailService.ForCacheBase(runner, _cacheBase, null, _ =>
        {
            Interlocked.Increment(ref sweeps);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return 0;
        });

        var first = svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, CancellationToken.None);
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the first grab starts the sweep");
        var second = svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);
        await Task.Delay(200);

        first.IsCompleted.Should().BeFalse("a grab never runs beside the sweep");
        second.IsCompleted.Should().BeFalse("a later grab waits for the same sweep");
        runner.CallCount.Should().Be(0);

        release.Set();
        (await first).Should().NotBeNull();
        (await second).Should().NotBeNull();
        runner.CallCount.Should().Be(2);
        sweeps.Should().Be(1, "one sweep for the process, however many grabs waited for it");
    }

    [Fact]
    public async Task WhenTheFirstGrabReturns_NothingStaleIsLeft()
    {
        var input = MakeInput();
        var legacyDir = PreviousLayoutDir(input);
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllBytesAsync(Path.Combine(legacyDir, "1000_w160.jpg"), WholeJpeg);
        var staleWork = Path.Combine(
            CurrentRoot, new DirectoryInfo(legacyDir).Name, "2000_w160.0123456789abcdef0123456789abcdef.tmp.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(staleWork)!);
        await File.WriteAllBytesAsync(staleWork, TruncatedJpeg);
        File.SetLastWriteTimeUtc(staleWork, DateTime.UtcNow - FfmpegThumbnailService.StaleWorkFileAge - TimeSpan.FromMinutes(1));

        var svc = NewVersionedService(new JpegWritingRunner());
        await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, CancellationToken.None);

        Directory.Exists(legacyDir).Should().BeFalse("the sweep was finished when the grab returned");
        File.Exists(staleWork).Should().BeFalse();
    }

    [Fact]
    public async Task ACancelledGrab_ReturnsAtOnce_WhileTheSweepKeepsRunning()
    {
        var input = MakeInput();
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var sweeps = 0;
        var runner = new JpegWritingRunner();
        var svc = FfmpegThumbnailService.ForCacheBase(runner, _cacheBase, null, _ =>
        {
            Interlocked.Increment(ref sweeps);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return 0;
        });

        using var cts = new CancellationTokenSource();
        var grab = svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, cts.Token);
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        cts.Cancel();

        var finished = await Task.WhenAny(grab, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().BeSameAs(grab, "a cancelled hover must not wait for the sweep");
        (await grab).Should().BeNull();
        runner.CallCount.Should().Be(0);
        release.IsSet.Should().BeFalse("the sweep is still pending");

        release.Set();
        var later = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(2), 160, CancellationToken.None);
        later.Should().NotBeNull("the sweep was not lost; the next grab goes on");
        sweeps.Should().Be(1, "the cancelled grab did not restart the sweep and the next grab did not start another");
    }

    [Fact]
    public void Sweep_OfAMissingBase_DoesNothingAndNeverThrows()
    {
        var act = () => FfmpegThumbnailService.SweepStaleCache(_cacheBase);

        act.Should().NotThrow();
        FfmpegThumbnailService.SweepStaleCache(_cacheBase).Should().Be(0);
        Directory.Exists(_cacheBase).Should().BeFalse("the sweep creates nothing");
    }

    // ---- (a) a picture under a final name in the current version is left alone ----

    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void Sweep_NeverOpensOrDeletesAPictureOfTheCurrentVersion()
    {
        // A final name in the current version is published whole by a rename (T-228), so it is never cut short; the pictures of the older
        // layouts, where a cut-short one could be, are removed wholesale. Reading every cached picture at every start was measured at about
        // 100 times the cost of deleting it, for a guard nothing can trigger.
        var dir = Path.Combine(CurrentRoot, HashA);
        Directory.CreateDirectory(dir);
        var files = new[]
        {
            (Name: "1000_w160.jpg", Bytes: WholeJpeg),
            (Name: "2000_w160.jpg", Bytes: TruncatedJpeg),
            (Name: "3000_w160.jpg", Bytes: Array.Empty<byte>()),
            (Name: "4000_w160.jpg", Bytes: new byte[] { 0xFF }),
        };
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
        }

        FfmpegThumbnailService.SweepStaleCache(_cacheBase).Should().Be(0);

        foreach (var (name, _) in files)
        {
            File.Exists(Path.Combine(dir, name)).Should().BeTrue(name + " is left as it is");
        }
    }

    // ---- (b) work files a hard kill left ----

    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void Sweep_RemovesAnOldTmpJpgWorkFile_KeepsAFreshOneAndTheFinalPicture()
    {
        var dir = Path.Combine(CurrentRoot, "0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(dir);
        var finalPicture = Path.Combine(dir, "1000_w160.jpg");
        var oldWork = Path.Combine(dir, "1000_w160.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tmp.jpg");
        var freshWork = Path.Combine(dir, "2000_w160.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.tmp.jpg");
        File.WriteAllBytes(finalPicture, WholeJpeg);
        File.WriteAllBytes(oldWork, TruncatedJpeg);
        File.WriteAllBytes(freshWork, TruncatedJpeg);
        File.SetLastWriteTimeUtc(oldWork, DateTime.UtcNow - FfmpegThumbnailService.StaleWorkFileAge - TimeSpan.FromMinutes(1));

        FfmpegThumbnailService.SweepStaleCache(_cacheBase).Should().Be(1);

        File.Exists(oldWork).Should().BeFalse("a work file a hard kill left");
        File.Exists(freshWork).Should().BeTrue("a recent work file may be another running grab's (another instance)");
        File.Exists(finalPicture).Should().BeTrue();
    }

    [Fact]
    public async Task ExplicitCacheRoot_IsUsedAsGivenAndNeverSwept_EvenByAGrab()
    {
        // The explicit-root constructor (tests, callers that own the folder) keeps its layout: no version folder, no sweep.
        var input = MakeInput();
        var root = Path.Combine(_cacheBase, "mine");
        var stray = Path.Combine(_cacheBase, "v1");
        Directory.CreateDirectory(Path.Combine(stray, "0123456789abcdef0123456789abcdef"));
        File.WriteAllBytes(Path.Combine(stray, "0123456789abcdef0123456789abcdef", "1000_w160.jpg"), WholeJpeg);

        var svc = new FfmpegThumbnailService(new JpegWritingRunner(), root);
        var picture = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, CancellationToken.None);

        svc.CacheRoot.Should().Be(root);
        picture.Should().NotBeNull();
        Path.GetRelativePath(root, picture!).Split(Path.DirectorySeparatorChar).Should().HaveCount(2, "<hash>/<file>, no v2 folder");
        File.Exists(Path.Combine(stray, "0123456789abcdef0123456789abcdef", "1000_w160.jpg")).Should().BeTrue("an explicit root never sweeps");
    }

    // ---- narrow deletion ----

    private const string HashA = "0123456789abcdef0123456789abcdef";

    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void Sweep_InALegacyFolder_DeletesOnlyWhatTheServiceWrites_AndKeepsAFolderWithAForeignFile()
    {
        var legacy = Path.Combine(_cacheBase, HashA);
        var oldVersion = Path.Combine(_cacheBase, "v1", HashA);
        var cleanLegacy = Path.Combine(_cacheBase, "fedcba9876543210fedcba9876543210");
        foreach (var dir in new[] { legacy, oldVersion, cleanLegacy })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "1000_w160.jpg"), WholeJpeg);
            File.WriteAllBytes(Path.Combine(dir, "x2500000_w640.jpg"), WholeJpeg);
        }

        File.WriteAllText(Path.Combine(legacy, "thesis.docx"), "mine");
        File.WriteAllText(Path.Combine(oldVersion, "holiday.jpg"), "mine");

        FfmpegThumbnailService.SweepStaleCache(_cacheBase);

        File.Exists(Path.Combine(legacy, "thesis.docx")).Should().BeTrue("a foreign file survives");
        File.Exists(Path.Combine(oldVersion, "holiday.jpg")).Should().BeTrue("a foreign .jpg is not a thumbnail name");
        File.Exists(Path.Combine(legacy, "1000_w160.jpg")).Should().BeFalse();
        File.Exists(Path.Combine(oldVersion, "x2500000_w640.jpg")).Should().BeFalse();
        Directory.Exists(cleanLegacy).Should().BeFalse("a folder left empty is removed");
        Directory.Exists(legacy).Should().BeTrue("a folder that still holds a foreign file stays");
        FfmpegThumbnailService.SweepStaleCache(_cacheBase).Should().Be(0);
    }

    [Fact]
    public void Sweep_InTheCurrentVersion_TouchesOnlyStaleWorkFilesInInputFolders()
    {
        var old = DateTime.UtcNow - FfmpegThumbnailService.StaleWorkFileAge - TimeSpan.FromMinutes(1);
        var notInput = Path.Combine(CurrentRoot, "my-photos");
        var input = Path.Combine(CurrentRoot, HashA);
        Directory.CreateDirectory(notInput);
        Directory.CreateDirectory(input);
        var workInForeignFolder = Path.Combine(notInput, "1000_w160.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tmp.jpg");
        var foreignName = Path.Combine(input, "cover.tmp.jpg");
        var finalPicture = Path.Combine(input, "1000_w160.jpg");
        var staleWork = Path.Combine(input, "1000_w160.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.tmp.jpg");
        foreach (var file in new[] { workInForeignFolder, foreignName, finalPicture, staleWork })
        {
            File.WriteAllBytes(file, TruncatedJpeg);
            File.SetLastWriteTimeUtc(file, old);
        }

        FfmpegThumbnailService.SweepStaleCache(_cacheBase).Should().Be(1);

        File.Exists(workInForeignFolder).Should().BeTrue("not a 32-hex input folder");
        File.Exists(foreignName).Should().BeTrue("not a name the service writes");
        File.Exists(finalPicture).Should().BeTrue("a final-name picture is never touched in the current version");
        File.Exists(staleWork).Should().BeFalse();
    }

    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void Sweep_RemovesAStaleWorkFile_UnderTheNamesWorkPathForReallyGives()
    {
        // The sweep knows a work file by its name and WorkPathFor makes the name: a change to one that is not made to the other would turn
        // the stale-work-file sweep into a silent no-op, so the names are taken from the generator here, not typed.
        var dir = Path.Combine(CurrentRoot, HashA);
        Directory.CreateDirectory(dir);
        var works = new[]
        {
            FfmpegThumbnailService.WorkPathFor(Path.Combine(dir, "1000_w160.jpg")),
            FfmpegThumbnailService.WorkPathFor(Path.Combine(dir, "x1234567_w1920.jpg")),
        };
        foreach (var work in works)
        {
            File.WriteAllBytes(work, TruncatedJpeg);
            File.SetLastWriteTimeUtc(work, DateTime.UtcNow - FfmpegThumbnailService.StaleWorkFileAge - TimeSpan.FromMinutes(1));
        }

        FfmpegThumbnailService.SweepStaleCache(_cacheBase).Should().Be(2);

        foreach (var work in works)
        {
            File.Exists(work).Should().BeFalse(Path.GetFileName(work));
        }
    }

    [Fact]
    public void TheDefaultConstructorsAndForCacheBase_SweepOnTheFirstGrab_AnExplicitRootDoesNot()
    {
        // Nothing is swept here (the sweep starts on the first grab); the wiring is what is checked.
        var runner = new JpegWritingRunner();
        new FfmpegThumbnailService(runner).SweepsOnFirstGrab.Should().BeTrue("the one-argument constructor is the app's");
        new FfmpegThumbnailService(runner, (Func<string, CancellationToken, Task<TimeSpan?>>?)null).SweepsOnFirstGrab.Should().BeTrue();
        FfmpegThumbnailService.ForCacheBase(runner, _cacheBase).SweepsOnFirstGrab.Should().BeTrue();
        new FfmpegThumbnailService(runner, Path.Combine(_cacheBase, "mine")).SweepsOnFirstGrab.Should().BeFalse("an explicit root is the caller's");
    }

    [Fact]
    public void Sweep_NeverFollowsALinkedFolder()
    {
        var outside = Path.Combine(_inputDir, "outside");
        var photo = Path.Combine(outside, HashA, "1000_w160.dddddddddddddddddddddddddddddddd.tmp.jpg");
        var legacyTarget = Path.Combine(_inputDir, "outside-legacy");
        var legacyPhoto = Path.Combine(legacyTarget, "1000_w160.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(photo)!);
        Directory.CreateDirectory(legacyTarget);
        // A stale work file: the one thing the sweep deletes in a current-version input folder, so it would go if the link were followed.
        File.WriteAllBytes(photo, TruncatedJpeg);
        File.SetLastWriteTimeUtc(photo, DateTime.UtcNow - FfmpegThumbnailService.StaleWorkFileAge - TimeSpan.FromMinutes(1));
        File.WriteAllBytes(legacyPhoto, WholeJpeg);
        Directory.CreateDirectory(_cacheBase);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_cacheBase, FfmpegThumbnailService.CacheVersionFolderName), outside);
            Directory.CreateSymbolicLink(Path.Combine(_cacheBase, "fedcba9876543210fedcba9876543210"), legacyTarget);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // links cannot be made here (Windows without the privilege)
        }

        FfmpegThumbnailService.SweepStaleCache(_cacheBase);

        File.Exists(photo).Should().BeTrue("a linked v2 folder is not entered");
        File.Exists(legacyPhoto).Should().BeTrue("a linked legacy folder is not entered");
    }

    // ---- files held open ----

    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task FilesHeldOpen_AreNotDeleted_AndTheGrabStillReturnsAPicture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the hold is a Windows sharing mode; on Linux a delete ignores a lock
        }

        var input = MakeInput();
        var legacyDir = PreviousLayoutDir(input);
        Directory.CreateDirectory(legacyDir);
        var legacyFile = Path.Combine(legacyDir, "1000_w160.jpg");
        await File.WriteAllBytesAsync(legacyFile, WholeJpeg);

        var currentDir = Path.Combine(CurrentRoot, new DirectoryInfo(legacyDir).Name);
        Directory.CreateDirectory(currentDir);
        var staleWork = Path.Combine(currentDir, "9000_w160.cccccccccccccccccccccccccccccccc.tmp.jpg");
        await File.WriteAllBytesAsync(staleWork, TruncatedJpeg);
        File.SetLastWriteTimeUtc(staleWork, DateTime.UtcNow - FfmpegThumbnailService.StaleWorkFileAge - TimeSpan.FromMinutes(1));

        using var holdLegacy = new FileStream(legacyFile, FileMode.Open, FileAccess.Read, FileShare.None);
        using var holdWork = new FileStream(staleWork, FileMode.Open, FileAccess.Read, FileShare.None);

        var svc = NewVersionedService(new JpegWritingRunner());
        var picture = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, CancellationToken.None);

        picture.Should().NotBeNull("a held file must not stop the grab");
        File.Exists(legacyFile).Should().BeTrue("a file held open is left alone");
        File.Exists(staleWork).Should().BeTrue("a file held open is left alone");
        Directory.Exists(legacyDir).Should().BeTrue("a folder that still holds a file stays");
    }
}

/// <summary>Fake ffmpeg that writes a whole (FF D8 .. FF D9) picture to the output, so the sweep keeps what it wrote.</summary>
internal sealed class JpegWritingRunner : IFfmpegRunner
{
    public int CallCount { get; private set; }

    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CallCount++;
        var output = args.ToList().ToList()[^1];
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x09, 0x09, 0xFF, 0xD9 });
        return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
    }
}
