using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.App.Views;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-181 (G-058, SPEC-007) — applying a profile whose picture is narrower than the card's minimum box (320px), or
/// whose picture file is missing, re-takes the picture at full size from a video it was just applied to, at the
/// profile's own intro time, and keeps the old file in <c>profile-thumbs\replaced</c>.
///
/// <para><b>Harness.</b> Every test awaits <see cref="BulkCutViewModel.PendingPictureRefresh"/> before asserting —
/// the negative ones included, so "no grab" can never pass just because the background work had not run yet. The
/// rows' own chip grabs are parked (<see cref="NeverSettles"/>), so every grab the fake records is the refresh's.
/// The fake grab writes a REAL PNG at the requested width, and the 64px precondition is a real 64px PNG put in
/// place through <see cref="BulkCutViewModel.UploadThumbnail"/> (the store keeps small uploads as they are).</para>
/// </summary>
public sealed class ProfilePictureRefreshTests : IDisposable
{
    private const string PathA = @"C:\v\ep01.mp4";
    private const string PathB = @"C:\v\ep02.mp4";
    private const string PathC = @"C:\v\ep03.mp4";
    private const string Series = "Series";

    // Every wait is bounded: a refresh that never grabs must FAIL the test, not hang the run.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly TaskCompletionSource ParkedForever = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), "vsj-t181-store-" + Guid.NewGuid().ToString("N"));
    private readonly string _srcDir = Path.Combine(Path.GetTempPath(), "vsj-t181-src-" + Guid.NewGuid().ToString("N"));

    public ProfilePictureRefreshTests() => Directory.CreateDirectory(_srcDir);

    public enum Change
    {
        Snapshot,
        UploadAnother64,
        Upload640,
        ClearPicture,
        ReSave,
        Delete,
        DeleteAndRecreate,
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _storeRoot, _srcDir })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    // ---- The re-take --------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ApplyToSelected_A64pxPicture_IsReTakenAt640_AtTheProfilesOwnIntro_AndTheOldOneIsKept()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        var old = GivePicture(rig, Series, 64);
        var oldBytes = File.ReadAllBytes(old);
        Select(rig, row, Series);

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        var grab = rig.Thumbs.Requests.Should().ContainSingle("one refresh, one grab").Subject;
        grab.Should().Be((PathA, TimeSpan.FromSeconds(11), 640),
            "the row's file, at the PROFILE's own intro (11 s), at ProfileThumbnailWidth");
        grab.Time.Should().NotBe(row.IntroEnd.Snapped,
            "the requested time, not the row's snapped cut (11 s snaps to 10 on a 2-second grid)");

        var stored = Persisted(rig).ThumbnailPath!;
        PixelWidth(stored).Should().Be(640, "the stored file itself is 640 wide");
        rig.Vm.Profiles.Single().ThumbnailPath.Should().Be(stored, "the bar shows the refreshed profile");

        var kept = Kept(rig).Should().ContainSingle("the old picture is kept aside, not deleted").Subject;
        File.ReadAllBytes(kept).Should().Equal(oldBytes, "byte-identical");

        rig.Vm.ProfilePictureRefreshNote.Should().Be(
            "Picture for \"Series\" re-taken at full size from ep01.mp4 — the old one is kept.");
        rig.Vm.ProfilePictureRefreshKeptPath.Should().Be(kept, "Show old picture opens Explorer on the kept copy");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ApplyToAll_TakesTheSelectedRow_WhenItIsATarget()
    {
        var rig = Build(Profile(Series, intro: 11));
        await AddRowAsync(rig, PathA);
        var b = await AddRowAsync(rig, PathB);
        await AddRowAsync(rig, PathC);
        GivePicture(rig, Series, 64);
        Select(rig, b, Series);

        rig.Vm.ApplyProfileToAll();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.Requests.Should().ContainSingle().Which.InputPath.Should().Be(PathB);
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ApplyToAll_TakesTheFirstTargetInListOrder_WhenTheSelectedRowIsNotOne()
    {
        var rig = Build(Profile(Series, intro: 11));
        var a = await AddRowAsync(rig, PathA);
        await AddRowAsync(rig, PathB);
        await AddRowAsync(rig, PathC);
        GivePicture(rig, Series, 64);
        Select(rig, a, Series);
        a.IsCheckedByUser = false;

        rig.Vm.ApplyProfileToAll();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.Requests.Should().ContainSingle().Which.InputPath.Should().Be(PathB);
    }

    /// <summary>
    /// A target that cannot hold the intro is passed over: no duration (the probe failed), or a duration that is
    /// not longer than the profile's intro (the boundary — 11 s long, intro 11 s).
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ApplyToAll_PassesOverTargetsThatCannotHoldTheIntro()
    {
        var rig = Build(Profile(Series, intro: 11));
        rig.Probe.FailProbePaths.Add(PathA);
        await rig.Vm.AddFilesAsync(new[] { PathA });
        var a = rig.Vm.Items.Single(i => i.Path == PathA);
        await AddRowAsync(rig, PathB, durationSeconds: 11, introSeconds: 4);
        await AddRowAsync(rig, PathC);
        GivePicture(rig, Series, 64);
        Select(rig, a, Series);

        a.Duration.Should().BeNull("precondition: the failed row has no duration");
        a.IsCheckedByUser.Should().BeTrue("precondition: it is still a target");

        rig.Vm.ApplyProfileToAll();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.Requests.Should().ContainSingle().Which.InputPath.Should().Be(PathC);
    }

    /// <summary>
    /// The rule, stated (T-181 review): the selected row first, then the OTHER targets from the top of the list. A
    /// selected row in the middle that cannot hold the intro hands over to the first qualifying target from the top.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ApplyToAll_ASelectedRowThatCannotHoldTheIntro_HandsOverToTheFirstTargetFromTheTop()
    {
        var rig = Build(Profile(Series, intro: 11));
        await AddRowAsync(rig, PathA);
        var b = await AddRowAsync(rig, PathB, durationSeconds: 11, introSeconds: 4);
        await AddRowAsync(rig, PathC);
        GivePicture(rig, Series, 64);
        Select(rig, b, Series);

        rig.Vm.ApplyProfileToAll();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.Requests.Should().ContainSingle().Which.InputPath.Should().Be(PathA);
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task NoTargetCanHoldTheIntro_NothingIsGrabbed()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA, durationSeconds: 11, introSeconds: 4);
        var old = GivePicture(rig, Series, 64);
        var oldBytes = File.ReadAllBytes(old);
        Select(rig, row, Series);

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.GetThumbnailCallCount.Should().Be(0);
        File.ReadAllBytes(old).Should().Equal(oldBytes);
        rig.Vm.ProfilePictureRefreshNote.Should().BeNull();
    }

    /// <summary>An outro-only profile (intro 0) grabs the first frame, as the automatic default would.</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task AnIntroOf0_GrabsAt0()
    {
        var rig = Build(Profile(Series, intro: 0, outro: 30));
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        Select(rig, row, Series);

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.Requests.Should().ContainSingle().Which.Time.Should().Be(TimeSpan.Zero);
    }

    // ---- The gate -----------------------------------------------------------------------------------

    /// <summary>Below 320px it is re-taken; at 320 or wider it never is (every capture since T-169, any sharp upload).</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(64, true)]
    [InlineData(319, true)]
    [InlineData(320, false)]
    [InlineData(640, false)]
    public async Task OnlyAPictureNarrowerThan320_IsReTaken(int width, bool reTaken)
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        var old = GivePicture(rig, Series, width);
        var oldBytes = File.ReadAllBytes(old);
        Select(rig, row, Series);

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.GetThumbnailCallCount.Should().Be(reTaken ? 1 : 0);
        if (!reTaken)
        {
            File.ReadAllBytes(old).Should().Equal(oldBytes, "a picture the card can already show sharply is never touched");
            rig.Vm.ProfilePictureRefreshNote.Should().BeNull();
            Kept(rig).Should().BeEmpty();
        }
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void BelowTheCardsMinimum_IsOneNamedNumber()
    {
        BulkCutViewModel.LowResolutionPictureWidth.Should().Be(320);
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task AProfileWithNoPicture_IsNeverGivenOne()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        Select(rig, row, Series);

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.GetThumbnailCallCount.Should().Be(0, "removing a picture is deliberate (I78)");
        Persisted(rig).ThumbnailPath.Should().BeNull();
    }

    /// <summary>A file that exists but cannot be measured is never replaced — we cannot know it is small.</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task APictureThatCannotBeRead_IsNeverReplaced()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        var unreadable = Path.Combine(_srcDir, "broken.png");
        File.WriteAllBytes(unreadable, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 });
        rig.Vm.UploadThumbnail(rig.Vm.Profiles.Single(), unreadable).Should().BeTrue("precondition: a PNG signature is accepted");
        var old = Persisted(rig).ThumbnailPath!;
        var oldBytes = File.ReadAllBytes(old);
        Select(rig, row, Series);

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.GetThumbnailCallCount.Should().Be(0);
        File.ReadAllBytes(old).Should().Equal(oldBytes);
        rig.Vm.ProfilePictureRefreshNote.Should().BeNull();
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task AMissingPictureFile_IsReTaken_WithNothingToKeepAside()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        File.Delete(GivePicture(rig, Series, 64));
        Select(rig, row, Series);

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.GetThumbnailCallCount.Should().Be(1);
        PixelWidth(Persisted(rig).ThumbnailPath!).Should().Be(640);
        Kept(rig).Should().BeEmpty("there was no file to keep");
        rig.Vm.ProfilePictureRefreshNote.Should().Be(
            "Picture for \"Series\" re-taken at full size from ep01.mp4 — its old picture file was missing.");
        rig.Vm.ProfilePictureRefreshKeptPath.Should().BeNull("there is no old picture to show");
    }

    // ---- Failures are silent and lose nothing -------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGrabThatReturnsNothingOrThrows_ChangesNothing_AndSaysNothing(bool throws)
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        var old = GivePicture(rig, Series, 64);
        var oldBytes = File.ReadAllBytes(old);
        Select(rig, row, Series);
        rig.Thumbs.ThumbnailFactory = throws ? (_, _, _) => throw new IOException("ffmpeg died") : (_, _, _) => null;

        var report = rig.Vm.ApplyProfileToSelected()!;
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.GetThumbnailCallCount.Should().Be(1, "precondition: the refresh did try");
        Persisted(rig).ThumbnailPath.Should().Be(old);
        File.ReadAllBytes(old).Should().Equal(oldBytes);
        rig.Vm.Operation.Error.Should().BeNull("silent, like the automatic default (I66)");
        rig.Vm.BatchState.Should().Be(BulkBatchState.Idle);
        rig.Vm.ProfilePictureRefreshNote.Should().BeNull();
        rig.Vm.ApplyReportSummary.Should().Be(BulkCutViewModel.FormatApplySummary(report), "the apply note is untouched");
        Kept(rig).Should().BeEmpty("nothing is kept aside for a frame that never came");
    }

    /// <summary>Fixture: a FILE named <c>replaced</c> in a usable store root, so the keep-aside copy cannot be made.</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task WhenTheOldPictureCannotBeKeptAside_ItIsNotReplaced()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        var old = GivePicture(rig, Series, 64);
        var oldBytes = File.ReadAllBytes(old);
        File.WriteAllText(Path.Combine(rig.Store.Root, ProfileThumbnailStore.ReplacedFolderName), "occupied");
        Select(rig, row, Series);

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.GetThumbnailCallCount.Should().Be(1, "precondition: the frame arrived");
        Persisted(rig).ThumbnailPath.Should().Be(old);
        File.ReadAllBytes(old).Should().Equal(oldBytes);
        rig.Vm.ProfilePictureRefreshNote.Should().BeNull();
        rig.Vm.Operation.Error.Should().BeNull();
    }

    /// <summary>
    /// Fixture: a DIRECTORY occupying the store's staging path <c>&lt;safe&gt;.incoming.png</c> — <c>.png</c> being the
    /// GRABBED file's extension — so the store's Save throws after the keep-aside copy succeeded. The store's own
    /// staging clean-up only deletes files, so the directory survives and the failure is deterministic.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task WhenTheStoreFailsAfterTheKeepAside_TheOldPictureStays_AndTheStrayCopyIsRemoved()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        var old = GivePicture(rig, Series, 64);
        var oldBytes = File.ReadAllBytes(old);
        Directory.CreateDirectory(Path.Combine(rig.Store.Root, ProfileThumbnailStore.SafeFileName(Series) + ".incoming.png"));
        Select(rig, row, Series);

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.GetThumbnailCallCount.Should().Be(1, "precondition: the frame arrived");
        Persisted(rig).ThumbnailPath.Should().Be(old);
        File.ReadAllBytes(old).Should().Equal(oldBytes, "still at its original path, byte-identical");
        Kept(rig).Should().BeEmpty("the redundant copy in replaced/ is deleted");
        rig.Vm.ProfilePictureRefreshNote.Should().BeNull();
        rig.Vm.Operation.Error.Should().BeNull();
    }

    // ---- In flight ----------------------------------------------------------------------------------

    /// <summary>
    /// Anything that changes the profile's picture, or the profile itself, while the grab is in flight discards
    /// the refresh: nothing is attached and no note appears. The same-extension 64px upload keeps the SAME path,
    /// so only the picture generation can catch it.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData(Change.Snapshot)]
    [InlineData(Change.UploadAnother64)]
    [InlineData(Change.Upload640)]
    [InlineData(Change.ClearPicture)]
    [InlineData(Change.ReSave)]
    [InlineData(Change.Delete)]
    [InlineData(Change.DeleteAndRecreate)]
    public async Task AChangeWhileTheGrabIsInFlight_DiscardsTheRefresh(Change change)
    {
        var player = new SnapPlayer();
        var rig = Build(player, Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        Select(rig, row, Series);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Thumbs.Gate = gate;
        rig.Vm.ApplyProfileToSelected();
        await rig.Thumbs.WhenCallCountReaches(1).WaitAsync(Bound);
        rig.Thumbs.Gate = null; // the change's own grab (the snapshot) must not park

        var generation = rig.Vm.PictureGeneration(Series);
        string? expected = await MakeChangeAsync(rig, player, row, change);
        rig.Vm.PictureGeneration(Series).Should().BeGreaterThan(generation, "every change to a picture or a profile bumps its generation");

        var pending = rig.Vm.PendingPictureRefresh; // taken before the release: the refresh lands on another thread
        gate.SetResult();
        await pending.WaitAsync(Bound);

        var now = rig.Settings.CutProfiles.SingleOrDefault(p => string.Equals(p.Name, Series, StringComparison.OrdinalIgnoreCase));
        if (expected is null)
        {
            (now?.ThumbnailPath).Should().BeNull("the refresh must not bring back a picture that was removed");
        }
        else
        {
            File.ReadAllBytes(now!.ThumbnailPath!).Should().Equal(File.ReadAllBytes(expected), "the change stands");
        }

        rig.Vm.ProfilePictureRefreshNote.Should().BeNull();
        Kept(rig).Should().BeEmpty("a discarded refresh keeps nothing aside");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task TheGenerationOnlyEverIncreases_EvenAcrossADelete()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        Select(rig, row, Series);
        var before = rig.Vm.PictureGeneration(Series);

        rig.Vm.DeleteSelectedProfile();
        var afterDelete = rig.Vm.PictureGeneration(Series);
        rig.Vm.SaveProfile(Series);
        var afterRecreate = rig.Vm.PictureGeneration(Series);

        afterDelete.Should().BeGreaterThan(before);
        afterRecreate.Should().BeGreaterThan(afterDelete, "a re-created profile never lands back on an earlier value");
        rig.Vm.PictureGeneration("SERIES").Should().Be(afterRecreate, "keyed by name, case-insensitive");
    }

    /// <summary>
    /// One refresh in flight per profile name, case-insensitive. The key is exercised by re-saving the in-flight
    /// profile as "SERIES" and giving it a 64px picture again, then applying it: still one grab. The re-save bumps
    /// the generation, so the first refresh is discarded when it lands, which frees the slot — and an apply after
    /// that retries.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task OneRefreshInFlightPerName_CaseInsensitive_AndADiscardFreesTheSlot()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        Select(rig, row, Series);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Thumbs.Gate = gate;
        rig.Vm.ApplyProfileToSelected();
        await rig.Thumbs.WhenCallCountReaches(1).WaitAsync(Bound);

        rig.Vm.ApplyProfileToSelected();
        rig.Thumbs.GetThumbnailCallCount.Should().Be(1, "an apply of the same profile while one is in flight starts nothing");

        row.IntroEnd.Requested = TimeSpan.FromSeconds(11);
        rig.Vm.SaveProfile("SERIES");
        GivePicture(rig, "SERIES", 64);
        Select(rig, row, "SERIES");
        rig.Vm.ApplyProfileToSelected();
        rig.Thumbs.GetThumbnailCallCount.Should().Be(1, "the slot is keyed case-insensitively");

        rig.Thumbs.Gate = null;
        var pending = rig.Vm.PendingPictureRefresh; // taken before the release: the refresh lands on another thread
        gate.SetResult();
        await pending.WaitAsync(Bound);
        PixelWidth(Persisted(rig).ThumbnailPath!).Should().Be(64, "the first refresh was discarded: the re-save changed its profile");

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);
        rig.Thumbs.GetThumbnailCallCount.Should().Be(2, "the discard freed the slot, so this apply retries");
        PixelWidth(Persisted(rig).ThumbnailPath!).Should().Be(640);
    }

    // ---- The note -----------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task TheNote_IsClearedByTheNextApply_AndByClear()
    {
        var rig = Build(Profile(Series, intro: 11), Profile("Sharp", intro: 20));
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        GivePicture(rig, "Sharp", 640);
        Select(rig, row, Series);

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);
        rig.Vm.ProfilePictureRefreshNote.Should().NotBeNull("precondition");

        Select(rig, row, "Sharp");
        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);
        rig.Vm.ProfilePictureRefreshNote.Should().BeNull("a new apply's report replaces the note's report");

        File.Delete(Persisted(rig).ThumbnailPath!); // missing → re-taken again, so the note comes back
        Select(rig, row, Series);
        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);
        rig.Vm.ProfilePictureRefreshNote.Should().NotBeNull("precondition");

        rig.Vm.Clear();
        rig.Vm.ProfilePictureRefreshNote.Should().BeNull("clearing the list clears the report and the note with it");
    }

    /// <summary>
    /// T-181 review — applying the SAME profile again while its grab is in flight must not leave the picture replaced
    /// with no word: the in-flight re-take adopts the newer apply's report and its note shows there.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ReApplyingTheSameProfileWhileItsGrabIsInFlight_TheNoteShowsUnderTheLatestApply()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        Select(rig, row, Series);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Thumbs.Gate = gate;
        rig.Vm.ApplyProfileToSelected();
        await rig.Thumbs.WhenCallCountReaches(1).WaitAsync(Bound);
        var second = rig.Vm.ApplyProfileToSelected();

        rig.Thumbs.GetThumbnailCallCount.Should().Be(1, "still one re-take in flight");
        var pending = rig.Vm.PendingPictureRefresh; // taken before the release: the refresh lands on another thread
        gate.SetResult();
        await pending.WaitAsync(Bound);

        rig.Vm.ApplyToAllReport.Should().BeSameAs(second);
        rig.Vm.ProfilePictureRefreshNote.Should().NotBeNull("the change is named under the latest apply of the profile");
        PixelWidth(Persisted(rig).ThumbnailPath!).Should().Be(640);
    }

    /// <summary>
    /// T-181 review — the note's line is held from the click while the re-take is in flight, so the note arriving a
    /// grab later does not push the list down under the pointer; a discard gives the line back.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task TheNotesLine_IsReservedWhileTheReTakeIsInFlight_AndGivenBackOnADiscard()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        Select(rig, row, Series);
        rig.Vm.IsPictureRefreshLineReserved.Should().BeFalse("precondition");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Thumbs.Gate = gate;
        rig.Vm.ApplyProfileToSelected();
        await rig.Thumbs.WhenCallCountReaches(1).WaitAsync(Bound);
        rig.Vm.IsPictureRefreshLineReserved.Should().BeTrue("reserved from the click, before the note exists");
        rig.Vm.ProfilePictureRefreshNote.Should().BeNull();

        rig.Vm.ClearThumbnail(rig.Vm.Profiles.Single()); // discards the re-take when it lands
        var pending = rig.Vm.PendingPictureRefresh;
        gate.SetResult();
        await pending.WaitAsync(Bound);

        rig.Vm.IsPictureRefreshLineReserved.Should().BeFalse("a discard gives the line back");
    }

    /// <summary>
    /// A refresh that lands after its report was replaced (here by the row-level Apply to all) still attaches the
    /// picture, but its note is dropped, so it never appears under another action's summary.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ARefreshLandingAfterItsReportWasReplaced_AttachesThePicture_ButSaysNothing()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        await AddRowAsync(rig, PathB);
        GivePicture(rig, Series, 64);
        Select(rig, row, Series);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Thumbs.Gate = gate;
        rig.Vm.ApplyProfileToSelected();
        await rig.Thumbs.WhenCallCountReaches(1).WaitAsync(Bound);

        rig.Vm.ApplyToAll(row).Should().NotBeNull("precondition: the row-level apply writes a new report");
        var pending = rig.Vm.PendingPictureRefresh; // taken before the release: the refresh lands on another thread
        gate.SetResult();
        await pending.WaitAsync(Bound);

        PixelWidth(Persisted(rig).ThumbnailPath!).Should().Be(640, "the picture is still attached");
        rig.Vm.ProfilePictureRefreshNote.Should().BeNull("its report is gone, so its note is dropped");
    }

    // ---- Selection ----------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task MovingTheSelectionWhileTheGrabIsInFlight_IsNotUndoneByTheRefresh()
    {
        var rig = Build(Profile(Series, intro: 11), Profile("Other", intro: 20));
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        Select(rig, row, Series);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Thumbs.Gate = gate;
        rig.Vm.ApplyProfileToSelected();
        await rig.Thumbs.WhenCallCountReaches(1).WaitAsync(Bound);

        rig.Vm.SelectedProfile = rig.Vm.Profiles.Single(p => p.Name == "Other");
        var pending = rig.Vm.PendingPictureRefresh; // taken before the release: the refresh lands on another thread
        gate.SetResult();
        await pending.WaitAsync(Bound);

        rig.Vm.SelectedProfile!.Name.Should().Be("Other", "the refresh attaches by name and leaves the selection alone");
        rig.Vm.Profiles.Should().Contain(rig.Vm.SelectedProfile, "the selection is the refreshed bar's instance");
        PixelWidth(Persisted(rig).ThumbnailPath!).Should().Be(640, "the applied profile was still refreshed");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ANoProfileSelection_StaysEmpty_WhenTheReTakeLands()
    {
        var rig = Build(Profile(Series, intro: 11));
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        Select(rig, row, Series);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Thumbs.Gate = gate;
        rig.Vm.ApplyProfileToSelected();
        await rig.Thumbs.WhenCallCountReaches(1).WaitAsync(Bound);

        rig.Vm.SelectedProfile = null;
        var pending = rig.Vm.PendingPictureRefresh;
        gate.SetResult();
        await pending.WaitAsync(Bound);

        rig.Vm.SelectedProfile.Should().BeNull("the re-take never selects the profile it refreshed");
        PixelWidth(Persisted(rig).ThumbnailPath!).Should().Be(640);
    }

    /// <summary>
    /// The same with the real view: the bar's ListBox binds SelectedItem two-way, and re-projecting the bar clears
    /// it, so the binding itself must not drag the selection anywhere.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void MovingTheSelectionWhileTheGrabIsInFlight_WithTheRealProfileBar()
    {
        StaViewHarness.OnSta(() =>
        {
            var previous = SynchronizationContext.Current;
            using var pump = new PumpContext();
            SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                var rig = Build(Profile(Series, intro: 11), Profile("Other", intro: 20));
                var view = new BulkCutView { DataContext = rig.Vm };
                StaViewHarness.LayOut(view, 1280, 800);

                rig.Probe.SetUniform(PathA, TimeSpan.FromSeconds(100), 2);
                pump.RunUntil(rig.Vm.AddFilesAsync(new[] { PathA }));
                var row = rig.Vm.Items.Single();
                GivePicture(rig, Series, 64);
                Select(rig, row, Series);
                StaViewHarness.LayOut(view, 1280, 800);

                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                rig.Thumbs.Gate = gate;
                rig.Vm.ApplyProfileToSelected();
                pump.RunUntil(rig.Thumbs.WhenCallCountReaches(1));

                var bar = StaViewHarness.Find<ListBox>(view, "ProfileBar")!;
                bar.SelectedItem = rig.Vm.Profiles.Single(p => p.Name == "Other");
                rig.Vm.SelectedProfile!.Name.Should().Be("Other", "precondition: the bar drives the selection");

                gate.SetResult();
                pump.RunUntil(rig.Vm.PendingPictureRefresh);
                StaViewHarness.LayOut(view, 1280, 800);

                rig.Vm.SelectedProfile!.Name.Should().Be("Other");
                bar.SelectedItem.Should().BeSameAs(rig.Vm.SelectedProfile, "the bar shows the same selection");
                PixelWidth(Persisted(rig).ThumbnailPath!).Should().Be(640);

                var note = StaViewHarness.Find<TextBlock>(view, "ProfilePictureRefreshNote")!;
                rig.Vm.ProfilePictureRefreshNote.Should().NotBeNull("precondition: the re-take happened");
                Shown(note, view).Should().BeTrue("the note shows in the apply-note area");
                note.Inlines.OfType<System.Windows.Documents.Run>().First().Text.Should().Be(rig.Vm.ProfilePictureRefreshNote);
                Shown(StaViewHarness.Find<Button>(view, "ShowOldPictureButton")!, view).Should().BeTrue(
                    "an old picture was kept, so the button that reveals it shows");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
    }

    // ---- Never on other gestures, and the apply is unchanged ----------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task SelectingSavingAndRestoring_NeverReTakeAPicture()
    {
        var rig = Build(Profile(Series, intro: 11), Profile("Other", intro: 20));
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        GivePicture(rig, "Other", 64);

        Select(rig, row, Series);
        rig.Vm.SelectedProfile = rig.Vm.Profiles.Single(p => p.Name == "Other");
        rig.Vm.SaveProfile("Third");

        var backup = Path.Combine(_srcDir, "profiles.vsjprofiles");
        ProfileBackup.Export(rig.Settings.CutProfiles, backup);
        rig.Vm.ChooseProfileImportPath = () => backup;
        rig.Vm.ConfirmProfileOverwrite = _ => true;
        rig.Vm.ImportProfiles();

        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);
        rig.Thumbs.GetThumbnailCallCount.Should().Be(0, "only an apply re-takes a picture");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ARestore_BumpsEveryRestoredName_OverwrittenAndNew()
    {
        var rig = Build(Profile(Series, intro: 11), Profile("Other", intro: 20));
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        GivePicture(rig, "Other", 64);

        var backup = Path.Combine(_srcDir, "profiles.vsjprofiles");
        ProfileBackup.Export(rig.Settings.CutProfiles, backup);

        Select(rig, row, "Other");
        rig.Vm.DeleteSelectedProfile(); // "Other" comes back as a NEW name; "Series" is overwritten
        var series = rig.Vm.PictureGeneration(Series);
        var other = rig.Vm.PictureGeneration("Other");

        rig.Vm.ChooseProfileImportPath = () => backup;
        rig.Vm.ConfirmProfileOverwrite = _ => true;
        rig.Vm.ImportProfiles();

        rig.Vm.PictureGeneration(Series).Should().BeGreaterThan(series, "an overwritten name is bumped");
        rig.Vm.PictureGeneration("Other").Should().BeGreaterThan(other, "and so is a name the restore brings back");
    }

    /// <summary>The apply's own effect is identical whether or not it starts a refresh (SPEC-011 I26 untouched).</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task TheApplyItself_IsIdentical_WithOrWithoutARefresh()
    {
        async Task<(TimeSpan Cut, ApplyToAllReport Report, string? Summary, int Grabs)> Run(int pictureWidth)
        {
            var rig = Build(Profile(Series, intro: 11, outro: 30));
            var row = await AddRowAsync(rig, PathA);
            await AddRowAsync(rig, PathB);
            GivePicture(rig, Series, pictureWidth);
            Select(rig, row, Series);
            var report = rig.Vm.ApplyProfileToAll()!;
            await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);
            return (row.IntroEnd.Snapped, report, rig.Vm.ApplyReportSummary, rig.Thumbs.GetThumbnailCallCount);
        }

        var refreshed = await Run(64);
        var plain = await Run(640);

        refreshed.Grabs.Should().Be(1, "precondition");
        plain.Grabs.Should().Be(0, "precondition");
        refreshed.Cut.Should().Be(plain.Cut);
        refreshed.Report.AppliedCount.Should().Be(plain.Report.AppliedCount);
        refreshed.Report.InvalidatedRows.Count.Should().Be(plain.Report.InvalidatedRows.Count);
        refreshed.Report.PendingSnapRows.Count.Should().Be(plain.Report.PendingSnapRows.Count);
        refreshed.Report.SkippedNotLoadedCount.Should().Be(plain.Report.SkippedNotLoadedCount);
        refreshed.Summary.Should().Be(plain.Summary, "byte-identical apply note");
    }

    /// <summary>A row whose keyframe scan is still running is used as is: the grab needs no snapped cut.</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ARowStillScanning_IsGrabbedAtTheRequestedTime()
    {
        var rig = Build(Profile(Series, intro: 11));
        rig.Probe.GatedPaths.Add(PathA);
        var row = await AddRowAsync(rig, PathA);
        GivePicture(rig, Series, 64);
        Select(rig, row, Series);

        row.Duration.Should().NotBeNull("precondition: probed");

        rig.Vm.ApplyProfileToSelected();
        await rig.Vm.PendingPictureRefresh.WaitAsync(Bound);

        rig.Thumbs.Requests.Should().ContainSingle().Which.Time.Should().Be(TimeSpan.FromSeconds(11));
        rig.Probe.ReleaseScans();
    }

    // ---- plumbing -----------------------------------------------------------------------------------

    private sealed record Rig(
        BulkCutViewModel Vm, BulkFakeProbe Probe, FakeSettings Settings, FakeThumbnailService Thumbs, ProfileThumbnailStore Store);

    private static Task NeverSettles(TimeSpan _, CancellationToken ct) => ParkedForever.Task.WaitAsync(ct);

    private static CutProfile Profile(string name, double intro, double? outro = null) =>
        new(name, TimeSpan.FromSeconds(intro), outro is { } o ? TimeSpan.FromSeconds(o) : null);

    private Rig Build(params CutProfile[] profiles) => Build(null, profiles);

    private Rig Build(SnapPlayer? player, params CutProfile[] profiles)
    {
        var probe = new BulkFakeProbe();
        var settings = new FakeSettings();
        foreach (var profile in profiles)
        {
            settings.SaveProfile(profile);
        }

        // A REAL picture at whatever width is asked for, as ffmpeg's scale=W:-1 produces.
        var thumbs = new FakeThumbnailService
        {
            ThumbnailFactory = (_, _, width) => Png($"grab-{Guid.NewGuid():N}.png", width, width * 9 / 16),
        };
        var store = new ProfileThumbnailStore(_storeRoot);
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), thumbs, settings, new FakeBulkTrimEngine(), player,
            thumbnailStore: store, thumbnailDelay: NeverSettles,
            selectionOpenDelay: (_, _) => Task.CompletedTask); // the player opens at once, so a snapshot can be ready
        return new Rig(vm, probe, settings, thumbs, store);
    }

    private static async Task<BulkItemViewModel> AddRowAsync(
        Rig rig, string path, double durationSeconds = 100, double introSeconds = 30)
    {
        rig.Probe.SetUniform(path, TimeSpan.FromSeconds(durationSeconds), 2);
        await rig.Vm.AddFilesAsync(new[] { path });
        var row = rig.Vm.Items.Single(i => i.Path == path);
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        return row;
    }

    /// <summary>Gives the profile a real PNG of <paramref name="width"/> pixels through an upload and returns its stored path.</summary>
    private string GivePicture(Rig rig, string profile, int width)
    {
        var target = rig.Vm.Profiles.Single(p => string.Equals(p.Name, profile, StringComparison.OrdinalIgnoreCase));
        rig.Vm.UploadThumbnail(target, Png($"{profile}-{width}-{Guid.NewGuid():N}.png", width, Math.Max(1, width * 9 / 16)))
            .Should().BeTrue("precondition: the picture is attached");
        var stored = rig.Settings.CutProfiles.Single(p => string.Equals(p.Name, profile, StringComparison.OrdinalIgnoreCase)).ThumbnailPath!;
        PixelWidth(stored).Should().Be(width, "precondition: an upload narrower than 640 is stored as it is");
        return stored;
    }

    private static void Select(Rig rig, BulkItemViewModel row, string profile)
    {
        rig.Vm.SelectedItem = row;
        rig.Vm.SelectedProfile = rig.Vm.Profiles.Single(p => string.Equals(p.Name, profile, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="element"/> and every ancestor up to <paramref name="view"/> are Visible. IsVisible cannot
    /// say it here: it is false for anything not hosted in a window, which the harness never creates.
    /// </summary>
    private static bool Shown(DependencyObject element, DependencyObject view)
    {
        for (var node = element; node is not null && !ReferenceEquals(node, view); node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private static CutProfile Persisted(Rig rig, string name = Series) =>
        rig.Settings.CutProfiles.Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string[] Kept(Rig rig)
    {
        var folder = Path.Combine(rig.Store.Root, ProfileThumbnailStore.ReplacedFolderName);
        return Directory.Exists(folder) ? Directory.GetFiles(folder) : Array.Empty<string>();
    }

    /// <summary>Performs <paramref name="change"/> and returns the file whose bytes the picture must now hold, or null for none.</summary>
    private async Task<string?> MakeChangeAsync(Rig rig, SnapPlayer player, BulkItemViewModel row, Change change)
    {
        var profile = rig.Vm.Profiles.Single(p => p.Name == Series);
        switch (change)
        {
            case Change.Snapshot:
                player.MakeReady(TimeSpan.FromSeconds(100));
                player.MovePlayheadTo(TimeSpan.FromSeconds(42));
                (await rig.Vm.SnapshotProfileThumbnailAsync()).Should().BeTrue("precondition");
                return Persisted(rig).ThumbnailPath;
            case Change.UploadAnother64:
                var another = Png($"another-{Guid.NewGuid():N}.png", 64, 36);
                rig.Vm.UploadThumbnail(profile, another).Should().BeTrue("precondition");
                return another;
            case Change.Upload640:
                var sharp = Png($"sharp-{Guid.NewGuid():N}.png", 640, 360);
                rig.Vm.UploadThumbnail(profile, sharp).Should().BeTrue("precondition");
                return sharp;
            case Change.ClearPicture:
                rig.Vm.ClearThumbnail(profile);
                return null;
            case Change.ReSave:
                rig.Vm.SaveProfile(Series);
                return Persisted(rig).ThumbnailPath;
            case Change.Delete:
                rig.Vm.DeleteSelectedProfile();
                return null;
            default:
                rig.Vm.DeleteSelectedProfile();
                rig.Vm.SelectedItem = row;
                rig.Vm.SaveProfile(Series);
                return null;
        }
    }

    private string Png(string name, int width, int height)
    {
        var path = Path.Combine(_srcDir, name);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 251);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static int PixelWidth(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        return image.PixelWidth;
    }

    /// <summary>A single-threaded context the STA test drains itself, so a continuation can be run on the UI thread.</summary>
    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback D, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>Runs posted work until <paramref name="task"/> completes (bounded, so a bug fails rather than hangs).</summary>
        public void RunUntil(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted)
            {
                if (!_queue.TryDequeue(out var item))
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        throw new TimeoutException("the awaited work never completed");
                    }

                    Thread.Sleep(1);
                    continue;
                }

                item.D(item.State);
            }

            task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
        }
    }
}
