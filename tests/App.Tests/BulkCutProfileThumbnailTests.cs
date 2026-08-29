using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
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
///
/// <para>T-129 additions: the two paths now have DIFFERENT contracts, and both halves are asserted here.
/// The explicit <see cref="BulkCutViewModel.UploadThumbnail"/> gesture REPORTS every failure on the
/// screen's existing error surface (<c>Operation.Error</c> — headline + hint + copyable detail) while
/// still leaving the profile's current thumbnail untouched; the auto capture on save stays silent. Store
/// failures are induced deterministically by pointing the store's root at an existing FILE
/// (<c>MakeUnusableStoreRoot</c>) — no file locks, no permission games.</para>
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
        var (vm, probe, settings, thumbs, store, _) = BuildWith(_storeRoot);
        return (vm, probe, settings, thumbs, store);
    }

    /// <summary>
    /// T-129 seam over <see cref="Build"/>: lets a test redirect the thumbnails root (to script a store
    /// failure) and hands back the batch engine (to script a BATCH failure onto the same error surface the
    /// upload reports on). <see cref="Build"/> is this with the default root and the engine dropped.
    /// </summary>
    private (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeSettings Settings, FakeThumbnailService Thumbs, ProfileThumbnailStore Store, FakeBulkTrimEngine Engine) BuildWith(string storeRoot)
    {
        var probe = new BulkFakeProbe();
        var settings = new FakeSettings();
        var thumbs = new FakeThumbnailService();
        var store = new ProfileThumbnailStore(storeRoot);
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe,
            new ThrowingFakeSplitEngine(),
            thumbs,
            settings,
            engine,
            thumbnailStore: store,
            thumbnailDelay: NeverSettles);
        return (vm, probe, settings, thumbs, store, engine);
    }

    // Every test here is about the PROFILE thumbnail, never a row's cut-point frame. Park the per-row grab
    // in its debounce window so a background row grab can never reach the fake service and inflate (or
    // race) the exact GetThumbnailCallCount numbers these tests assert. The AUTO profile capture calls
    // IThumbnailService directly and is unaffected, so it still registers as exactly one grab.
    private static readonly TaskCompletionSource ParkedForever = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task NeverSettles(TimeSpan _, CancellationToken ct) => ParkedForever.Task.WaitAsync(ct);

    /// <summary>
    /// A store root the store can NEVER create: an existing FILE occupying the root path, so the
    /// <c>Directory.CreateDirectory</c> inside <see cref="ProfileThumbnailStore.Save"/> throws BEFORE the
    /// copy (and before the overwrite-delete). The deterministic stand-in for "that image could not be
    /// stored" — no file locks, no permission games, no platform-specific behaviour.
    /// </summary>
    private string MakeUnusableStoreRoot()
    {
        Directory.CreateDirectory(_srcDir);
        var path = Path.Combine(_srcDir, "root-is-a-file");
        File.WriteAllText(path, "occupied");
        return path;
    }

    /// <summary>
    /// The sorted file names currently in the thumbnails root (empty when the root does not exist) — the
    /// on-disk snapshot a FAILED upload must leave exactly as it found it (the "no extra I/O" assertion).
    /// </summary>
    private static IReadOnlyList<string> StoreFiles(ProfileThumbnailStore store)
    {
        if (!Directory.Exists(store.Root))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(store.Root)
            .Select(f => Path.GetFileName(f)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
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
        vm.UploadThumbnail(vm.SelectedProfile, image)
            .Should().BeTrue("T-129: the gesture reports its outcome — true means the image really was attached");

        var saved = settings.CutProfiles.Single();
        saved.ThumbnailPath.Should().NotBeNull("upload attaches the chosen image as the thumbnail");
        saved.ThumbnailPath!.Should().StartWith(store.Root, "the uploaded image is copied into the store");
        File.Exists(saved.ThumbnailPath).Should().BeTrue();
        Path.GetExtension(saved.ThumbnailPath).Should().Be(".png", "the store preserves the uploaded image's extension");
        vm.SelectedProfile!.ThumbnailPath.Should().Be(saved.ThumbnailPath);
        vm.Operation.Error.Should().BeNull("T-129: a SUCCESSFUL upload says nothing — only failures reach the error surface");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task Upload_MissingImage_IsNoOp_KeepsCurrentThumbnail()
    {
        var (vm, probe, settings, _, _) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;
        vm.SaveProfile("Series");

        // A path to a file that does not exist → the store throws FileNotFound. The profile is still left
        // untouched (SPEC-007 I68); T-129 additionally REPORTS it — asserted in the reporting region below.
        vm.UploadThumbnail(vm.SelectedProfile, Path.Combine(_srcDir, "does-not-exist.png"));

        settings.CutProfiles.Single().ThumbnailPath.Should().BeNull("a missing source leaves the profile unchanged (never throws)");
    }

    // SPEC-007#I68 (the null-profile / blank-path guard; this was I66's territory before T-129 re-scoped
    // I66 to the AUTO path) — Upload_MissingImage_IsNoOp covers exactly one
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
        })).Should().NotThrow("upload never throws — a null profile / blank image is refused, not raised (SPEC-007 I68)");

        settings.CutProfiles.Single().ThumbnailPath.Should().Be(stored,
            "a null profile / blank image leaves the EXISTING thumbnail untouched");
        File.Exists(stored!).Should().BeTrue("the previously stored file is neither replaced nor deleted");
        vm.SelectedProfile!.ThumbnailPath.Should().Be(stored, "and the bar's selection still points at it");
    }

    // ---- T-129: the explicit upload REPORTS its failures (SPEC-007 I68-I72) -----------------
    //
    // The gesture used to be best-effort AND silent, which made "I picked this file and nothing happened"
    // indistinguishable from a broken button (G-044). The no-op half is unchanged and still asserted above;
    // these tests pin the half that changed: every failure lands on the SAME error surface a failed batch
    // uses (Operation.Error - headline + actionable Hint + copyable RawTail), and the failure path stays
    // cheap: no frame grab, no profile upsert, no RefreshProfiles re-projection, no file added or removed.

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task Upload_MissingImage_ReportsToTheUser_AndLeavesTheStoredThumbnailIntact()
    {
        var (vm, probe, settings, thumbs, store) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;
        vm.SaveProfile("Series");
        vm.UploadThumbnail(vm.SelectedProfile, MakeImage("cover.png")).Should().BeTrue("precondition: the profile carries a real thumbnail");

        var stored = settings.CutProfiles.Single().ThumbnailPath;
        var selectedBefore = vm.SelectedProfile;
        var filesBefore = StoreFiles(store);
        var missing = Path.Combine(_srcDir, "does-not-exist.png");

        vm.UploadThumbnail(vm.SelectedProfile, missing).Should().BeFalse("the image could not be attached");

        // --- correctness: the user is TOLD, on the surface this screen already uses for failures ---
        var error = vm.Operation.Error;
        error.Should().NotBeNull("a deliberate gesture that fails must say so, not vanish (SPEC-007 I69)");
        error!.Category.Should().Be(ErrorCategory.CorruptInput, "an unusable source image is an input problem, not a store problem");
        error.Message.Should().Contain("could not be read", "the headline names what went wrong in the user's terms");
        error.Hint.Should().NotBeNullOrWhiteSpace("every reported failure carries the action that fixes it");
        error.RawTail.Should().Contain(missing, "the copyable detail names the file the user actually picked");
        vm.Operation.State.Should().Be(OperationState.Failed, "the reported failure owns the error surface");

        // --- correctness: the previous thumbnail is untouched (the old best-effort promise, kept) ---
        settings.CutProfiles.Single().ThumbnailPath.Should().Be(stored, "a failed upload never rewrites the profile");
        File.Exists(stored!).Should().BeTrue("nor deletes the thumbnail file it was going to replace");

        // --- performance (structural): the failure path does no extra work ---
        thumbs.GetThumbnailCallCount.Should().Be(0, "the explicit upload never grabs a frame - that is the AUTO path's job");
        StoreFiles(store).Should().Equal(filesBefore, "a refused upload performs no file I/O beyond the store call that refused");
        vm.SelectedProfile.Should().BeSameAs(selectedBefore,
            "no SaveProfile upsert and no RefreshProfiles re-projection ran - the bar still holds the very same record instance");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Upload_NoProfileSelected_Reports_InsteadOfVanishing()
    {
        var (vm, _, settings, thumbs, store) = Build();

        vm.UploadThumbnail(null, MakeImage("cover.png")).Should().BeFalse("there is no profile to hang the image off");

        var error = vm.Operation.Error;
        error.Should().NotBeNull("SPEC-007 I69: the no-profile branch reports too - it used to be the quietest of all");
        error!.Category.Should().Be(ErrorCategory.InvalidArgument);
        error.Message.Should().Contain("No cut profile is selected");
        error.Hint.Should().Contain("Pick a profile", "the hint names the one action that unblocks the user");

        settings.CutProfiles.Should().BeEmpty("a refused upload never materializes a phantom profile");

        // Performance (structural): the guard fires before ANY store call, so nothing is created on disk.
        Directory.Exists(store.Root).Should().BeFalse("the guard returns before the store is ever touched - zero I/O");
        thumbs.GetThumbnailCallCount.Should().Be(0, "and no frame grab is attempted");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task Upload_BlankOrNullImagePath_Reports_AndKeepsTheExistingThumbnail()
    {
        var (vm, probe, settings, thumbs, store) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;
        vm.SaveProfile("Series");
        vm.UploadThumbnail(vm.SelectedProfile, MakeImage("cover.png")).Should().BeTrue("precondition: a real thumbnail is in place");

        var stored = settings.CutProfiles.Single().ThumbnailPath;
        var selectedBefore = vm.SelectedProfile;
        var filesBefore = StoreFiles(store);

        vm.UploadThumbnail(vm.SelectedProfile, "   ").Should().BeFalse("a blank path is not an image");
        vm.Operation.Error!.Message.Should().Contain("No image was chosen", "SPEC-007 I69: the blank-path branch reports");
        vm.Operation.Error!.Category.Should().Be(ErrorCategory.InvalidArgument);

        vm.UploadThumbnail(vm.SelectedProfile, null).Should().BeFalse("neither is a null path");
        vm.Operation.Error!.Message.Should().Contain("No image was chosen", "and reports the same way");

        settings.CutProfiles.Single().ThumbnailPath.Should().Be(stored, "the EXISTING thumbnail is untouched (SPEC-007 I68)");
        File.Exists(stored!).Should().BeTrue();

        // Performance (structural): both refusals are pure guards - no store call, no grab, no re-projection.
        StoreFiles(store).Should().Equal(filesBefore, "two refused uploads performed no file I/O at all");
        thumbs.GetThumbnailCallCount.Should().Be(0);
        vm.SelectedProfile.Should().BeSameAs(selectedBefore, "and never re-projected the profile bar");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Upload_ProfileNotSavedYet_Reports_AndWritesNothing()
    {
        var (vm, _, settings, thumbs, store) = Build();
        var ghost = new CutProfile("never-saved", TimeSpan.FromSeconds(1), null, null);

        vm.UploadThumbnail(ghost, MakeImage("cover.png")).Should().BeFalse("a thumbnail cannot hang off an unsaved profile");

        var error = vm.Operation.Error;
        error.Should().NotBeNull("SPEC-007 I69: 'save the profile first' is a reportable failure, not silence");
        error!.Message.Should().Contain("never-saved", "the headline names the profile the user was working on");
        error.Hint.Should().Contain("Save the profile first", "the hint names the action that unblocks the user");

        settings.CutProfiles.Should().BeEmpty("a refused upload never upserts the unsaved profile on its way out");
        vm.Profiles.Should().BeEmpty("and never materializes a phantom entry in the bar");

        // Performance (structural): the persisted-profile guard runs before the store, so nothing is created.
        Directory.Exists(store.Root).Should().BeFalse("the guard returns before any store I/O");
        thumbs.GetThumbnailCallCount.Should().Be(0);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Upload_StoreCannotStoreTheImage_Reports_AndKeepsTheExistingThumbnailFile()
    {
        // The store's root is occupied by a FILE, so Save throws at Directory.CreateDirectory - a real
        // copy failure, deterministically, without locking anything.
        var blockedRoot = MakeUnusableStoreRoot();
        var (vm, _, settings, thumbs, store, _) = BuildWith(blockedRoot);

        // A profile that already carries a thumbnail stored OUTSIDE the blocked root, so it stays reachable.
        var existingThumb = MakeImage("existing-thumb.png", "the picture the user already had");
        settings.SaveProfile(new CutProfile("Series", TimeSpan.FromSeconds(8), null, existingThumb));

        var replacement = MakeImage("replacement.png", "the picture the user just picked");
        vm.UploadThumbnail(settings.CutProfiles.Single(), replacement).Should().BeFalse("the store could not take the copy");

        var error = vm.Operation.Error;
        error.Should().NotBeNull("SPEC-007 I69: a store copy-failure is exactly the case that used to be invisible");
        error!.Category.Should().Be(ErrorCategory.PermissionDenied, "a store that cannot take the copy is an access/IO problem, not a bad pick");
        error.Message.Should().Contain("could not be stored");
        error.Hint.Should().NotBeNullOrWhiteSpace();
        error.RawTail.Should().Contain(replacement, "the copyable detail names the picked file...");
        error.RawTail.Should().NotBe(replacement, "...alongside the refusing exception's own message");

        settings.CutProfiles.Single().ThumbnailPath.Should().Be(existingThumb, "the profile still points at the thumbnail it had (SPEC-007 I68)");
        File.Exists(existingThumb).Should().BeTrue("and that file is still on disk");

        // Performance (structural): the refusal happened BEFORE the store wrote or deleted anything.
        Directory.Exists(store.Root).Should().BeFalse("no thumbnails tree was built at the blocked root");
        File.ReadAllText(blockedRoot).Should().Be("occupied", "and the blocking file itself was neither replaced nor appended to");
        thumbs.GetThumbnailCallCount.Should().Be(0);
        vm.Profiles.Should().BeEmpty("no RefreshProfiles re-projection ran on the failure path");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task Upload_SucceedingAfterAFailure_RetractsTheMessageItReported()
    {
        var (vm, probe, _, thumbs, store) = Build();
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;
        vm.SaveProfile("Series");

        vm.UploadThumbnail(vm.SelectedProfile, Path.Combine(_srcDir, "gone.png")).Should().BeFalse();
        vm.Operation.Error.Should().NotBeNull("precondition: the failed pick lit the error surface");
        vm.Operation.State.Should().Be(OperationState.Failed);

        vm.UploadThumbnail(vm.SelectedProfile, MakeImage("cover.png")).Should().BeTrue("the retry with a real image succeeds");

        vm.Operation.Error.Should().BeNull("SPEC-007 I71: a successful upload retracts the message it reported");
        vm.Operation.State.Should().Be(OperationState.Idle, "and drops the surface back out of Failed - no red taskbar with nothing to explain it");

        // Performance (structural): the retry stored exactly ONE file - the failure left no half-written residue.
        StoreFiles(store).Should().ContainSingle("one thumbnail per profile: the refused attempt wrote nothing to clean up");
        thumbs.GetThumbnailCallCount.Should().Be(0, "and neither upload ever grabbed a frame");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task Upload_Success_DoesNotRetractAnUnrelatedBatchFailure()
    {
        var (vm, probe, _, thumbs, _, engine) = BuildWith(_storeRoot);
        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 60, 2, introSeconds: 10);
        engine.ResultFactory = (items, _) => new BatchResult(
            BatchOutcome.Blocked,
            items.Select(i => new BulkTrimItemResult(i, ItemOutcome.NotStarted, null, null, Array.Empty<string>())).ToList());

        await vm.RunBatchAsync();

        var batchFailure = vm.Operation.Error;
        batchFailure.Should().NotBeNull("precondition: a BLOCKED batch owns the shared error surface");
        batchFailure!.Category.Should().Be(ErrorCategory.DiskFull);

        vm.SelectedItem = row;
        vm.SaveProfile("Series");
        vm.UploadThumbnail(vm.SelectedProfile, MakeImage("cover.png")).Should().BeTrue("the upload itself succeeds");

        vm.Operation.Error.Should().BeSameAs(batchFailure,
            "SPEC-007 I71: the retraction is reference-scoped - a successful thumbnail upload must never erase someone else's failure");
        vm.Operation.State.Should().Be(OperationState.Failed, "the batch failure still owns the surface");

        // Performance (structural): the successful upload is O(1) work on the error surface - one reference
        // check, no re-report, no second batch, no frame grab.
        engine.CallCount.Should().Be(1, "the upload never re-ran the batch");
        thumbs.GetThumbnailCallCount.Should().Be(0, "and never grabbed a frame");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public async Task SaveWithAutoDefault_StoreFailure_StaysSILENT_AndStillSavesTheProfile()
    {
        // The same store failure the explicit upload now reports - proving the re-scoped I66: the AUTO
        // path must NOT have inherited the new contract, because it is a side effect of "Save".
        var blockedRoot = MakeUnusableStoreRoot();
        var (vm, probe, settings, thumbs, _, _) = BuildWith(blockedRoot);
        var frame = MakeImage("frame.jpg", "intro-end-frame");
        thumbs.ThumbnailFactory = (_, _, _) => frame;

        var row = await AddRowAsync(vm, probe, @"C:\v\ep01.mp4", 100, 2, introSeconds: 10);
        vm.SelectedItem = row;

        await vm.SaveProfileWithAutoThumbnailAsync("Auto");

        settings.CutProfiles.Should().ContainSingle(p => p.Name == "Auto", "the save is never blocked by the thumbnail");
        settings.CutProfiles.Single().ThumbnailPath.Should().BeNull("a store failure leaves the placeholder");
        vm.Operation.Error.Should().BeNull("SPEC-007 I66/I72: the AUTO path stays silent - only the deliberate gesture reports");
        vm.Operation.State.Should().Be(OperationState.Idle, "and never lights the error surface");

        // Performance (structural): exactly one bounded frame grab, and the save is not retried on failure.
        thumbs.GetThumbnailCallCount.Should().Be(1, "the auto-default grabs exactly one frame, failure or not");
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
