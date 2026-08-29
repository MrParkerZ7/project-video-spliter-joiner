using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Profiles;
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
        thumbs.Requests[0].Width.Should().Be(96,
            "the auto-default grabs at the NAMED ProfileThumbnailWidth (96), not an arbitrary size");
        thumbs.Requests[0].InputPath.Should().Be(row.Path, "and from the row's own source file");

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

    // SPEC-007#I66 (the null-profile / blank-path guard) — Upload_MissingImage_IsNoOp covers exactly one
    // of the three named branches: a path to a nonexistent file, which reaches AttachThumbnail's try/catch
    // around the store copy. The EARLIER guard (BulkCutViewModel.cs:969 — `profile is null ||
    // string.IsNullOrWhiteSpace(imagePath)`) is never entered, and neither branch is checked against a
    // profile that ALREADY has a thumbnail — "leaves the profile's current thumbnail untouched" is
    // strictly stronger than "leaves it null".
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task Upload_NullProfileOrBlankPath_IsNoOp_KeepsExistingThumbnail()
    {
        var (vm, probe, settings, _, _) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;
        vm.SaveProfile("Series");
        vm.UploadThumbnail(vm.SelectedProfile, MakeImage("cover.png"));

        var stored = settings.CutProfiles.Single().ThumbnailPath;
        stored.Should().NotBeNull("precondition: the profile carries a real thumbnail before the no-op calls");

        ((Action)(() =>
        {
            vm.UploadThumbnail(null, MakeImage("other.png")); // no profile to hang the image off
            vm.UploadThumbnail(vm.SelectedProfile, "   ");    // blank image path
            vm.UploadThumbnail(vm.SelectedProfile, null);     // no image path at all
        })).Should().NotThrow("upload is best-effort — a null profile / blank image is a silent no-op");

        settings.CutProfiles.Single().ThumbnailPath.Should().Be(stored,
            "a null profile / blank image leaves the EXISTING thumbnail untouched");
        File.Exists(stored!).Should().BeTrue("the previously stored file is neither replaced nor deleted");
        vm.SelectedProfile!.ThumbnailPath.Should().Be(stored, "and the bar's selection still points at it");
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

    // SPEC-007#I67 (the no-op guard) — Clear_DeletesStoredFile_AndNullsThePath covers the happy path only.
    // The guard at BulkCutViewModel.cs:985 (`profile is null || FindPersistedProfile(...) is not {} existing`)
    // has no assertion anywhere: clearing an unset profile, or one that was never persisted, must write
    // nothing — in particular it must NOT upsert the unpersisted profile into settings on its way out —
    // and must never throw.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Clear_NullOrUnpersistedProfile_IsNoOp_NeverThrows()
    {
        var (vm, _, settings, _, _) = Build();

        ((Action)(() =>
        {
            vm.ClearThumbnail(null);
            vm.ClearThumbnail(new CutProfile("never-saved", TimeSpan.FromSeconds(1), null, @"C:\x.png"));
        })).Should().NotThrow("clear is best-effort on an unset / unpersisted profile");

        settings.CutProfiles.Should().BeEmpty("clearing an unset / unpersisted profile writes nothing");
        vm.Profiles.Should().BeEmpty("and never materializes a phantom entry in the bar");
        vm.SelectedProfile.Should().BeNull("the bar's selection is untouched by a no-op clear");
    }

    // SPEC-007#I67 (the "by the exact recorded path" half) — Clear_DeletesStoredFile_AndNullsThePath only
    // exercises the case where BOTH delete halves address the SAME file (its path came from the store's own
    // Save, so it already sits at the safe name). A directly-set ThumbnailPath whose filename diverges from
    // the safe name is reachable ONLY by the DeleteByPath half (BulkCutViewModel.cs:993) — delete that line
    // and the happy-path test still passes.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Clear_AlsoDeletesADirectlySetPath_ThatDivergesFromTheSafeName()
    {
        var (vm, _, settings, _, store) = Build();

        // (a) the safe-name file the store itself resolves for the profile...
        var safeNameThumb = store.Save("Series", MakeImage("frame.jpg"));

        // (b) ...and a hand-written thumbnail recorded DIRECTLY on the profile, whose filename deliberately
        //     does not match the safe name (a path the UI/user set rather than one Save produced).
        var handWritten = Path.Combine(store.Root, "hand-written-thumb.png");
        File.WriteAllText(handWritten, "bytes");
        Path.GetFileNameWithoutExtension(handWritten).Should().NotBe(
            ProfileThumbnailStore.SafeFileName("Series"),
            "precondition: the recorded path diverges from the safe-name path, so only the by-path half can reach it");

        settings.SaveProfile(new CutProfile("Series", TimeSpan.FromSeconds(8), null, handWritten));

        vm.ClearThumbnail(settings.CutProfiles.Single());

        File.Exists(handWritten).Should().BeFalse(
            "clear also deletes by the EXACT recorded path, covering a directly-set thumbnail the by-name half would miss");
        File.Exists(safeNameThumb).Should().BeFalse(
            "the by-name half still runs — both files go, not one or the other");
        settings.CutProfiles.Single().ThumbnailPath.Should().BeNull("the profile's path is nulled either way");
        vm.SelectedProfile!.Name.Should().Be("Series", "and the bar's selection is re-pointed at the refreshed instance");
    }
}
