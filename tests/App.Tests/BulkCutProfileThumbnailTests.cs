using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for the T-107 profile-thumbnail glue on <see cref="BulkCutViewModel"/> — the thin VM layer over
/// the T-106 <see cref="ProfileThumbnailStore"/>: "Save current as…" auto-captures the selected row's
/// intro-end frame as the profile's default thumbnail (best-effort — a null / throwing grab still saves the
/// profile, just with no thumbnail); Upload overrides it with a chosen image; Clear deletes the stored file
/// and nulls the path. Rows use the real-snap <see cref="BulkFakeProbe"/>; the store is redirected to a temp
/// root and the frame grab is scripted via <see cref="FakeThumbnailService.ThumbnailFactory"/> (no ffmpeg).
/// </summary>
public sealed class BulkCutProfileThumbnailTests : IDisposable
{
    private readonly string _storeRoot;
    private readonly string _srcDir;

    public BulkCutProfileThumbnailTests()
    {
        _storeRoot = Path.Combine(Path.GetTempPath(), "vsj-t107-thumbs-" + Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(Path.GetTempPath(), "vsj-t107-src-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        TryDelete(_storeRoot);
        TryDelete(_srcDir);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeSettings Settings, FakeThumbnailService Thumbs, ProfileThumbnailStore Store) Build()
    {
        var probe = new BulkFakeProbe();
        var settings = new FakeSettings();
        var thumbs = new FakeThumbnailService();
        var store = new ProfileThumbnailStore(_storeRoot);
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), thumbs, settings, new FakeBulkTrimEngine(), thumbnailStore: store);
        return (vm, probe, settings, thumbs, store);
    }

    private string MakeImage(string fileName = "frame.jpg", string content = "img-bytes")
    {
        Directory.CreateDirectory(_srcDir);
        var path = Path.Combine(_srcDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<BulkItemViewModel> AddRowAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double durationSeconds, double stepSeconds,
        double introSeconds, double? outroSeconds = null)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(durationSeconds), stepSeconds);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        if (outroSeconds is double o)
        {
            row.AddOutro(TimeSpan.FromSeconds(o));
        }

        return row;
    }

    // ---- Auto-default on save ---------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task SaveWithAutoDefault_CapturesIntroEndFrame_StoresIt_AndPersistsThePath()
    {
        var (vm, probe, settings, thumbs, store) = Build();
        var frame = MakeImage("frame.jpg", "intro-end-frame");
        TimeSpan? grabbedAt = null;
        thumbs.ThumbnailFactory = (_, time, _) => { grabbedAt = time; return frame; };

        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10, outroSeconds: 90);
        vm.SelectedItem = row;

        await vm.SaveProfileWithAutoThumbnailAsync("Anime OP");

        thumbs.GetThumbnailCallCount.Should().Be(1, "the auto-default grabbed exactly one frame");
        grabbedAt.Should().Be(row.IntroEnd.Snapped, "the default thumbnail is the row's snapped intro-end frame");

        var saved = settings.CutProfiles.Single();
        saved.Name.Should().Be("Anime OP");
        saved.ThumbnailPath.Should().NotBeNull("the captured frame becomes the profile's default thumbnail");
        saved.ThumbnailPath!.Should().StartWith(store.Root, "the frame is copied into the T-106 store root");
        File.Exists(saved.ThumbnailPath).Should().BeTrue("the stored thumbnail file exists on disk");

        vm.SelectedProfile!.ThumbnailPath.Should().Be(saved.ThumbnailPath, "the bar's selection re-points at the thumbnailed instance");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task SaveWithAutoDefault_GrabReturnsNull_StillSavesProfile_WithPlaceholder()
    {
        var (vm, probe, settings, thumbs, _) = Build();
        thumbs.ThumbnailFactory = null; // the grab yields null (e.g. probe pending / ffmpeg unavailable)

        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;

        await vm.SaveProfileWithAutoThumbnailAsync("NoThumb");

        thumbs.GetThumbnailCallCount.Should().Be(1, "the grab was attempted");
        settings.CutProfiles.Should().ContainSingle(p => p.Name == "NoThumb", "the profile still saves — never blocked on the grab");
        settings.CutProfiles.Single().ThumbnailPath.Should().BeNull("a null grab leaves no thumbnail (the picker shows a placeholder)");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task SaveWithAutoDefault_GrabThrows_StillSavesProfile_WithPlaceholder()
    {
        var (vm, probe, settings, thumbs, _) = Build();
        thumbs.ThumbnailFactory = (_, _, _) => throw new InvalidOperationException("scripted grab failure");

        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;

        await vm.SaveProfileWithAutoThumbnailAsync("Resilient");

        settings.CutProfiles.Should().ContainSingle(p => p.Name == "Resilient", "a throwing grab never blocks or fails the save");
        settings.CutProfiles.Single().ThumbnailPath.Should().BeNull("a failed grab leaves the placeholder");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task SaveWithAutoDefault_NoSelectedRow_IsNoOp()
    {
        var (vm, _, settings, thumbs, _) = Build();

        // Nothing selected → nothing saved, no grab attempted.
        await vm.SaveProfileWithAutoThumbnailAsync("X");

        settings.CutProfiles.Should().BeEmpty();
        thumbs.GetThumbnailCallCount.Should().Be(0);
    }

    // ---- Upload override --------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task Upload_OverridesThumbnail_CopiesImage_AndPersistsStoredPath()
    {
        var (vm, probe, settings, _, store) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;
        vm.SaveProfile("Series"); // saved with no thumbnail

        var image = MakeImage("cover.png", "png-bytes");
        vm.UploadThumbnail(vm.SelectedProfile, image);

        var saved = settings.CutProfiles.Single();
        saved.ThumbnailPath.Should().NotBeNull("upload attaches the chosen image as the thumbnail");
        saved.ThumbnailPath!.Should().StartWith(store.Root, "the uploaded image is copied into the store");
        File.Exists(saved.ThumbnailPath).Should().BeTrue();
        Path.GetExtension(saved.ThumbnailPath).Should().Be(".png", "the store preserves the uploaded image's extension");
        vm.SelectedProfile!.ThumbnailPath.Should().Be(saved.ThumbnailPath);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task Upload_MissingImage_IsNoOp_KeepsCurrentThumbnail()
    {
        var (vm, probe, settings, _, _) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;
        vm.SaveProfile("Series");

        // A path to a file that does not exist → the store throws FileNotFound → best-effort no-op.
        vm.UploadThumbnail(vm.SelectedProfile, Path.Combine(_srcDir, "does-not-exist.png"));

        settings.CutProfiles.Single().ThumbnailPath.Should().BeNull("a missing source leaves the profile unchanged (never throws)");
    }

    // ---- Clear ------------------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task Clear_DeletesStoredFile_AndNullsThePath()
    {
        var (vm, probe, settings, _, _) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;
        vm.SaveProfile("Series");
        vm.UploadThumbnail(vm.SelectedProfile, MakeImage("cover.png"));

        var storedPath = settings.CutProfiles.Single().ThumbnailPath;
        storedPath.Should().NotBeNull();
        File.Exists(storedPath).Should().BeTrue();

        vm.ClearThumbnail(vm.SelectedProfile);

        settings.CutProfiles.Single().ThumbnailPath.Should().BeNull("clear nulls the profile's thumbnail path");
        File.Exists(storedPath!).Should().BeFalse("clear best-effort deletes the stored thumbnail file");
        vm.SelectedProfile!.ThumbnailPath.Should().BeNull("the bar's selection reflects the cleared thumbnail");
    }
}
