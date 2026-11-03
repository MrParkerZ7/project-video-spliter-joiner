using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-243 (SPEC-005): the thumbnail cache is versioned. A picture an older build wrote is never served by a build that changed
/// what a grab returns, and the older version folders (plus truncated pictures and work files a hard kill left) are swept once,
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

        var runner = new JpegWritingRunner();
        var svc = NewVersionedService(runner);
        var picture = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(1), 160, CancellationToken.None);

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
        Directory.CreateDirectory(Path.Combine(v1, "abc"));
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
    public async Task TheSweepFinishesBeforeTheFirstGrab_SoItCannotRemoveAFreshPicture()
    {
        var input = MakeInput();
        var runner = new JpegWritingRunner();
        var svc = NewVersionedService(runner);

        var tasks = Enumerable.Range(0, 8)
            .Select(i => svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(i), 160, CancellationToken.None))
            .ToArray();
        var paths = await Task.WhenAll(tasks);

        paths.Should().OnlyContain(p => p != null && File.Exists(p));
    }

    [Fact]
    public void Sweep_OfAMissingBase_DoesNothingAndNeverThrows()
    {
        var act = () => FfmpegThumbnailService.SweepStaleCache(_cacheBase);

        act.Should().NotThrow();
        FfmpegThumbnailService.SweepStaleCache(_cacheBase).Should().Be(0);
        Directory.Exists(_cacheBase).Should().BeFalse("the sweep creates nothing");
    }

    // ---- (a) a truncated picture under a final name ----

    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void Sweep_RemovesATruncatedOrEmptyPicture_KeepsAWholeOne()
    {
        var dir = Path.Combine(CurrentRoot, "0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(dir);
        var whole = Path.Combine(dir, "1000_w160.jpg");
        var wholePadded = Path.Combine(dir, "2000_w160.jpg");
        var truncated = Path.Combine(dir, "3000_w160.jpg");
        var empty = Path.Combine(dir, "4000_w160.jpg");
        var tiny = Path.Combine(dir, "5000_w160.jpg");
        File.WriteAllBytes(whole, WholeJpeg);
        File.WriteAllBytes(wholePadded, WholeJpeg.Concat(new byte[] { 0, 0, 0 }).ToArray());
        File.WriteAllBytes(truncated, TruncatedJpeg);
        File.WriteAllBytes(empty, Array.Empty<byte>());
        File.WriteAllBytes(tiny, new byte[] { 0xFF });

        FfmpegThumbnailService.SweepStaleCache(_cacheBase).Should().Be(3);

        File.Exists(whole).Should().BeTrue();
        File.Exists(wholePadded).Should().BeTrue("zero padding after the end marker is still a whole JPEG");
        File.Exists(truncated).Should().BeFalse("no FF D9 end marker: cut short");
        File.Exists(empty).Should().BeFalse();
        File.Exists(tiny).Should().BeFalse();
    }

    // ---- (b) work files a hard kill left ----

    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void Sweep_RemovesAnOldTmpJpgWorkFile_KeepsAFreshOneAndTheFinalPicture()
    {
        var dir = Path.Combine(CurrentRoot, "0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(dir);
        var finalPicture = Path.Combine(dir, "1000_w160.jpg");
        var oldWork = Path.Combine(dir, "1000_w160.aaaa.tmp.jpg");
        var freshWork = Path.Combine(dir, "2000_w160.bbbb.tmp.jpg");
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
    public void ExplicitCacheRoot_IsUsedAsGivenAndNeverSwept()
    {
        // The explicit-root constructor (tests, callers that own the folder) keeps its layout: no version folder, no sweep.
        var root = Path.Combine(_cacheBase, "mine");
        var stray = Path.Combine(_cacheBase, "v1");
        Directory.CreateDirectory(stray);

        var svc = new FfmpegThumbnailService(new JpegWritingRunner(), root);

        svc.CacheRoot.Should().Be(root);
        Directory.Exists(stray).Should().BeTrue();
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
