using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Tests for <see cref="ProfileThumbnailStore"/> (G-038 / T-106): copy-in returns a stored PATH and
/// overwrites a profile's prior thumbnail; delete-by-name and delete-by-path are best-effort (never throw
/// on a missing/locked file); odd profile names sanitize to a safe, collision-resistant filename; the
/// default root mirrors the thumb-cache composition; and <see cref="AppSettings.DeleteProfile"/> cascades
/// to the thumbnail file. The final section covers the G-044 copy-then-swap fix — a <c>Save</c> that FAILS
/// leaves the profile's existing thumbnail byte-for-byte intact and sweeps its own <c>.incoming</c> staging
/// file — plus the follow-up fix for the residual window inside that swap: the prior thumbnail is renamed
/// ASIDE to a <c>.vsj-aside</c> sibling instead of deleted, so a move that fails once the prior is out of
/// the way restores it, and a move that commits deletes the aside. All I/O is redirected to a temp root
/// (the root is injectable).
/// </summary>
public sealed class ProfileThumbnailStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _srcDir;

    public ProfileThumbnailStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vsj-profile-thumbs-" + Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(Path.GetTempPath(), "vsj-thumb-src-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_srcDir);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private string MakeSource(string fileName = "frame.jpg", string content = "img-bytes")
    {
        Directory.CreateDirectory(_srcDir);
        var path = Path.Combine(_srcDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- Save -------------------------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_CopiesSourceIntoRoot_ReturnsStoredPath()
    {
        var store = new ProfileThumbnailStore(_root);
        var source = MakeSource("frame.jpg", "hello-thumb");

        var stored = store.Save("Series A", source);

        stored.Should().StartWith(_root, "the thumbnail lands under the store's root");
        File.Exists(stored).Should().BeTrue("the source was copied in");
        File.ReadAllText(stored).Should().Be("hello-thumb", "the bytes are copied verbatim");
        stored.Should().EndWith(".jpg", "a recognized source extension is preserved");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_UnknownExtension_DefaultsToPng()
    {
        var store = new ProfileThumbnailStore(_root);
        var source = MakeSource("capture.dat");

        var stored = store.Save("Odd ext", source);

        stored.Should().EndWith(".png", "an unrecognized source extension falls back to .png");
    }

    // SPEC-007#I43 (the "lowercased" clause) — Save_CopiesSourceIntoRoot_ReturnsStoredPath sources a
    // "frame.jpg" whose extension is ALREADY lowercase, so NormalizeExtension's ext.ToLowerInvariant()
    // is never observably exercised. KnownImageExtensions is an OrdinalIgnoreCase set, so an uppercase
    // ".JPG" source IS recognized (it must NOT fall through to the .png default) — and the stored file
    // must carry the case-FOLDED extension, so one profile resolves to one stable filename however the
    // chosen source happened to be cased.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_UppercaseRecognizedExtension_IsPreservedLowercased()
    {
        var store = new ProfileThumbnailStore(_root);

        var stored = store.Save("Upper", MakeSource("FRAME.JPG"));

        Path.GetExtension(stored).Should().Be(".jpg",
            "a recognized extension is preserved LOWERCASED — neither left uppercase nor defaulted to .png");
        File.Exists(stored).Should().BeTrue("the copy still lands under the store root");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_OverwritesPriorThumbnail_IncludingAcrossExtensionChange()
    {
        var store = new ProfileThumbnailStore(_root);

        var first = store.Save("Series", MakeSource("a.jpg", "first"));
        var second = store.Save("Series", MakeSource("b.png", "second"));

        File.Exists(first).Should().BeFalse("the prior .jpg thumbnail is removed when the profile's thumbnail changes");
        File.Exists(second).Should().BeTrue();
        File.ReadAllText(second).Should().Be("second");
        Directory.EnumerateFiles(_root).Should().ContainSingle("exactly one thumbnail exists per profile");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_SanitizesOddProfileName_ToSafeFilename()
    {
        var store = new ProfileThumbnailStore(_root);
        var source = MakeSource();

        // Invalid filename characters are sanitized — the copy succeeds instead of throwing an I/O error.
        var stored = store.Save("A/B:C*?<illegal>", source);

        File.Exists(stored).Should().BeTrue();
        Path.GetFileName(stored).Should().NotContainAny("/", "\\", ":", "*", "?", "<", ">");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_BlankName_Throws()
    {
        var store = new ProfileThumbnailStore(_root);
        var source = MakeSource();

        ((Action)(() => store.Save("  ", source)))
            .Should().Throw<ArgumentException>().Which.ParamName.Should().Be("profileName");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_BlankSource_Throws()
    {
        var store = new ProfileThumbnailStore(_root);

        ((Action)(() => store.Save("Series", "  ")))
            .Should().Throw<ArgumentException>().Which.ParamName.Should().Be("sourceImageOrFramePath");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_MissingSource_ThrowsFileNotFound()
    {
        var store = new ProfileThumbnailStore(_root);
        var missing = Path.Combine(_srcDir, "nope.png");

        ((Action)(() => store.Save("Series", missing)))
            .Should().Throw<FileNotFoundException>("a nonexistent source is a genuine caller error, distinct from best-effort delete");
    }

    // ---- Delete / DeleteByPath (best-effort) ------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Delete_RemovesThumbnail_ForProfile()
    {
        var store = new ProfileThumbnailStore(_root);
        var stored = store.Save("Series", MakeSource());

        store.Delete("Series");

        File.Exists(stored).Should().BeFalse("delete removes the profile's thumbnail file");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Delete_IsCaseInsensitive_MatchingTheUpsertKey()
    {
        var store = new ProfileThumbnailStore(_root);
        var stored = store.Save("Series", MakeSource());

        store.Delete("series"); // different case, same profile key

        File.Exists(stored).Should().BeFalse("delete resolves the same file case-insensitively");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Delete_MissingProfileOrBlank_DoesNotThrow()
    {
        var store = new ProfileThumbnailStore(_root);

        ((Action)(() =>
        {
            store.Delete("never-saved");
            store.Delete("   ");
            store.Delete(null!);
        })).Should().NotThrow("delete is best-effort — a missing thumbnail is a no-op");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void DeleteByPath_RemovesFile()
    {
        var store = new ProfileThumbnailStore(_root);
        var stored = store.Save("Series", MakeSource());

        store.DeleteByPath(stored);

        File.Exists(stored).Should().BeFalse();
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void DeleteByPath_MissingOrBlankPath_DoesNotThrow()
    {
        var store = new ProfileThumbnailStore(_root);

        ((Action)(() =>
        {
            store.DeleteByPath(Path.Combine(_root, "does-not-exist.png"));
            store.DeleteByPath("   ");
            store.DeleteByPath(null!);
        })).Should().NotThrow("delete-by-path is best-effort on a missing/blank path");
    }

    // ---- Root + safe-name --------------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void DefaultRoot_MirrorsThumbCacheComposition_ProfileThumbsFolder()
    {
        var root = ProfileThumbnailStore.DefaultRoot();

        root.Should().EndWith(Path.Combine("VideoSplitJoiner", "profile-thumbs"),
            "the root mirrors the thumb-cache composition under LocalAppData");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void SafeFileName_DistinctNamesThatSanitizeAlike_DoNotCollide()
    {
        // "A/B" and "A?B" both sanitize their readable part to "A_B" — the hash suffix keeps them distinct.
        var a = ProfileThumbnailStore.SafeFileName("A/B");
        var b = ProfileThumbnailStore.SafeFileName("A?B");

        a.Should().NotBe(b, "distinct names never map to the same file even when the readable stem collides");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void SafeFileName_IsCaseInsensitivelyStable()
    {
        ProfileThumbnailStore.SafeFileName("Series")
            .Should().BeEquivalentTo(ProfileThumbnailStore.SafeFileName("series"),
                "the safe name is case-insensitive, matching the profile upsert key (same file for Foo/foo)");
    }

    // ---- DeleteProfile cascade (AppSettings ⇒ store) ----------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void DeleteProfile_CascadesToThumbnailFile()
    {
        var settingsDir = Path.Combine(Path.GetTempPath(), "vsj-cascade-" + Guid.NewGuid().ToString("N"));
        var settingsFile = Path.Combine(settingsDir, "settings.json");
        try
        {
            var store = new ProfileThumbnailStore(_root);
            var settings = new AppSettings(settingsFile, store);

            var thumb = store.Save("Series", MakeSource());
            settings.SaveProfile(new CutProfile("Series", TimeSpan.FromSeconds(8), null, thumb));
            File.Exists(thumb).Should().BeTrue("precondition: the thumbnail exists before delete");

            settings.DeleteProfile("Series");

            File.Exists(thumb).Should().BeFalse("deleting the profile cascades to remove its thumbnail file");
            settings.CutProfiles.Should().BeEmpty("the profile itself is also removed");
        }
        finally
        {
            TryDelete(settingsDir);
        }
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void DeleteProfile_WithoutThumbnailStore_DoesNotThrow()
    {
        // The store is optional — AppSettings(file) with no store simply skips the cascade.
        var settingsDir = Path.Combine(Path.GetTempPath(), "vsj-nocascade-" + Guid.NewGuid().ToString("N"));
        var settingsFile = Path.Combine(settingsDir, "settings.json");
        try
        {
            var settings = new AppSettings(settingsFile);
            settings.SaveProfile(new CutProfile("Series", TimeSpan.FromSeconds(8), null, @"C:\thumbs\series.png"));

            ((Action)(() => settings.DeleteProfile("Series"))).Should().NotThrow();
            settings.CutProfiles.Should().BeEmpty();
        }
        finally
        {
            TryDelete(settingsDir);
        }
    }

    // SPEC-007#I59 — the delete-cascade fires BOTH halves: store.Delete(name) removes whatever sits at
    // the recomputed safe-name path, AND store.DeleteByPath(removedProfile.ThumbnailPath) removes the
    // exact recorded path — which is the only half that can reach a directly-set thumbnail whose name
    // diverges from the safe name. DeleteProfile_CascadesToThumbnailFile only exercises the case where
    // both halves happen to address the SAME file (the path came from Save), so the divergent-path
    // branch is untested there.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void DeleteProfile_CascadesToADirectlySetThumbnailPath_ThatDivergesFromTheSafeName()
    {
        var settingsDir = Path.Combine(Path.GetTempPath(), "vsj-cascade-divergent-" + Guid.NewGuid().ToString("N"));
        var settingsFile = Path.Combine(settingsDir, "settings.json");
        try
        {
            var store = new ProfileThumbnailStore(_root);
            var settings = new AppSettings(settingsFile, store);

            // (a) the safe-name file the store itself would resolve for the profile...
            var safeNameThumb = store.Save("Series", MakeSource());

            // (b) ...and a hand-written thumbnail whose filename deliberately does NOT match the safe name,
            // recorded directly on the profile (a path the user/UI set rather than one Save produced).
            var handWritten = Path.Combine(_root, "hand-written-thumb.png");
            File.WriteAllText(handWritten, "bytes");
            Path.GetFileNameWithoutExtension(handWritten).Should().NotBe(
                ProfileThumbnailStore.SafeFileName("Series"),
                "precondition: the recorded path diverges from the recomputed safe-name path, so only the DeleteByPath half can reach it");

            settings.SaveProfile(new CutProfile("Series", TimeSpan.FromSeconds(8), null, handWritten));
            File.Exists(safeNameThumb).Should().BeTrue("precondition: both files exist before the delete");
            File.Exists(handWritten).Should().BeTrue();

            settings.DeleteProfile("Series");

            File.Exists(handWritten).Should().BeFalse(
                "the cascade also deletes by the EXACT recorded path, covering a directly-set thumbnail the safe-name half would miss");
            File.Exists(safeNameThumb).Should().BeFalse(
                "the safe-name half of the cascade still runs — both files go, not one or the other");
            Directory.EnumerateFiles(_root).Should().BeEmpty("nothing is left behind for the deleted profile");
            settings.CutProfiles.Should().BeEmpty("the profile itself is also removed");
        }
        finally
        {
            TryDelete(settingsDir);
        }
    }

    // SPEC-007#I58 (second half) — construction is SIDE-EFFECT-FREE: the injected root is only resolved
    // to a string, so no directory is created until the first Save. The best-effort deletes are equally
    // read-only about the root — they must not materialize it either. (The first half, "the root is
    // injectable", is implicit in every other test here; this pins the no-I/O-on-construction clause.)
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Construction_AndBestEffortDeletes_CreateNoDirectory_UntilFirstSave()
    {
        var store = new ProfileThumbnailStore(_root);

        Directory.Exists(_root).Should().BeFalse("constructing the store only resolves a root string — no I/O");
        store.Root.Should().Be(_root, "the root is injectable, so no test ever touches the real per-user folder");

        store.Delete("never-saved");
        store.DeleteByPath(Path.Combine(_root, "never-written.png"));

        Directory.Exists(_root).Should().BeFalse(
            "the best-effort deletes short-circuit on a missing root — they never create the folder as a side effect");

        var stored = store.Save("Series", MakeSource());

        Directory.Exists(_root).Should().BeTrue("the root folder is created lazily, on the first Save");
        File.Exists(stored).Should().BeTrue("that first Save is what materializes both the folder and the thumbnail");
    }

    // ---- Save FAILURE: copy-then-swap keeps the prior thumbnail (G-044) --------------------------

    // SPEC-007#I45 (the FAILURE half) — Save_OverwritesPriorThumbnail_IncludingAcrossExtensionChange
    // above covers only the SUCCESS path of the overwrite. The failure half is the data-loss shape
    // G-044 fixed: Save used to DeleteExistingFor BEFORE File.Copy, so a copy that failed part-way (a
    // source locked by another program, a read-only or full volume) destroyed the picture the profile
    // already had — the caller correctly reported the failure and left CutProfile.ThumbnailPath alone,
    // but that path then pointed at a file that no longer existed and the profile silently reverted to
    // the placeholder. Save now copies into <safeName>.incoming<ext> first and only swaps once the new
    // bytes are safely on disk. (SPEC-007 has since been re-worded to match: I45 now states the
    // displace order and I73 owns this durability guarantee, replacing the old I70 "known store-side
    // gap" paragraph.)
    //
    // The failure is induced with an EXCLUSIVE FileShare.None handle, the idiom
    // AppSettingsTests.Save_FailingReplace_LeavesGoodFileIntact_AndCleansStrayTmp uses. Here it is held
    // on the SOURCE: File.Copy opens its source with FileShare.Read, so the copy into the staging file
    // fails with a sharing violation — literally "a source locked by another program". File.Exists
    // still sees a locked file, so Save clears its guards and fails INSIDE the copy rather than at the
    // I49 missing-source check, and the lock touches nothing in the store root, so the prior
    // thumbnail surviving is proof of the ordering rather than an artifact of the lock.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_FailingCopy_LeavesPriorThumbnailIntact_ByteForByte()
    {
        var store = new ProfileThumbnailStore(_root);
        var stored = store.Save("Series", MakeSource("a.jpg", "the-picture-the-profile-already-has"));
        var priorBytes = File.ReadAllBytes(stored);
        var priorWrittenUtc = File.GetLastWriteTimeUtc(stored);

        var replacement = MakeSource("b.jpg", "never-lands");
        using (new FileStream(replacement, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var thrown = ((Action)(() => store.Save("Series", replacement)))
                .Should().Throw<IOException>(
                    "a copy that cannot read its source is a real failure the caller must see — the store does not swallow it the way the deletes do")
                .Which;

            thrown.Should().NotBeOfType<FileNotFoundException>(
                "the source DOES exist, it is merely locked — so this exercises the copy step itself, not the I49 missing-source guard");
        }

        File.Exists(stored).Should().BeTrue(
            "a failed Save must leave the existing thumbnail exactly as it was — the recorded ThumbnailPath still points at a real file instead of reverting to the placeholder");
        File.ReadAllBytes(stored).Should().Equal(priorBytes,
            "the prior thumbnail survives byte-for-byte: the delete happens AFTER the copy succeeds, so a copy that never succeeded deletes nothing");
        File.GetLastWriteTimeUtc(stored).Should().Be(priorWrittenUtc,
            "it survives untouched — the failure path never rewrote or restored it, it simply never removed it");

        Directory.EnumerateFiles(_root).Should()
            .ContainSingle("the failed Save added nothing to the root")
            .Which.Should().Be(stored);
        StagingFiles().Should().BeEmpty("no stray .incoming is left behind by a failed Save");
    }

    // SPEC-007#I45 — the sharpest form of the same regression, and the exact counterpart of
    // Save_OverwritesPriorThumbnail_IncludingAcrossExtensionChange (which keeps passing: that one owns
    // the success path, this one the failure path). A stem-matched pass over the root exists because a
    // replacement may carry a DIFFERENT extension, so the destination path alone cannot displace the old
    // file. That pass — DeleteExistingFor when this regression was found, RenameExistingAside now — is
    // exactly what used to eat the prior .jpg while the incoming .png was still being copied. Staging the
    // bytes first means a failed copy never reaches it at all, whichever form it takes.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_FailingCopy_AcrossAnExtensionChange_NeverReachesTheSweep()
    {
        var store = new ProfileThumbnailStore(_root);
        var stored = store.Save("Series", MakeSource("a.jpg", "first"));
        Path.GetExtension(stored).Should().Be(".jpg", "precondition: the prior thumbnail is a .jpg");

        // A .png replacement resolves to a DIFFERENT destination path, so only the stem-matched pass can
        // displace the prior .jpg — and it must not run when the copy failed.
        var replacement = MakeSource("b.png", "never-lands");
        using (new FileStream(replacement, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ((Action)(() => store.Save("Series", replacement)))
                .Should().Throw<IOException>("the locked source fails the copy");
        }

        File.Exists(stored).Should().BeTrue(
            "the cross-extension sweep must not have run — the prior .jpg only goes once the new bytes are staged");
        File.ReadAllText(stored).Should().Be("first", "and it still holds the original picture");
        Directory.EnumerateFiles(_root).Should()
            .ContainSingle("no .png destination was created either — the failed Save left the root exactly as it found it")
            .Which.Should().Be(stored);
    }

    // SPEC-007#I45 — the STAGING-CLEANUP half. The two tests above fail before the copy, so no .incoming
    // file is ever created and their "no stray staging" assertions cost nothing. This one fails AFTER a
    // SUCCESSFUL copy, so the staging file genuinely exists at throw time and the catch is what removes
    // it: an exclusive handle on the DESTINATION (a plausible real case — the picker is displaying that
    // very file) lets the copy into staging succeed, then fails File.Move. Note that the locked file here
    // IS the prior thumbnail (same extension), so RenameExistingAside cannot move it either and silently
    // leaves it in place — this test therefore exercises the staging sweep with an EMPTY aside set; the
    // restore path itself is pinned separately below, where the aside rename genuinely succeeds.
    //
    // To prove the copy step really ran (nothing observable survives a swept staging file), a stale
    // .incoming from an imagined earlier crash is planted at the staging path first: it can only
    // disappear if Save overwrote it and then cleaned it up.
    //
    // NOTE on the exception type: a locked DESTINATION surfaces as UnauthorizedAccessException (the
    // replacing move needs delete access on it), not the IOException a locked SOURCE gives — hence the
    // either/or. SPEC-007#I73 now records the same caveat; the Save XML doc still says "IOException" for
    // a locked target — see findings.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_FailingMove_CleansUpItsStagingFile_AndKeepsThePriorThumbnail()
    {
        var store = new ProfileThumbnailStore(_root);
        var stored = store.Save("Series", MakeSource("a.jpg", "prior-picture"));

        var staging = Path.Combine(_root, ProfileThumbnailStore.SafeFileName("Series") + ".incoming.jpg");
        File.WriteAllText(staging, "left over from an earlier crashed save");

        var replacement = MakeSource("b.jpg", "new-picture");
        using (new FileStream(stored, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var thrown = ((Action)(() => store.Save("Series", replacement)))
                .Should().Throw<Exception>("a destination that cannot be replaced is a real failure the caller must see")
                .Which;

            (thrown is IOException or UnauthorizedAccessException).Should().BeTrue(
                "a locked target surfaces as an I/O or access failure, never as a silent success (got {0})",
                thrown.GetType().Name);
        }

        File.Exists(staging).Should().BeFalse(
            "the planted staging file was overwritten by the copy and then swept by the catch (TryDeleteFile) — proof the cleanup ran, not merely that the copy never started");
        StagingFiles().Should().BeEmpty("a failed Save never leaves a stray .incoming behind");

        File.Exists(stored).Should().BeTrue("the prior thumbnail is still there");
        File.ReadAllText(stored).Should().Be("prior-picture",
            "and still holds the prior picture — the half-swapped state is never observable to the caller");
        Directory.EnumerateFiles(_root).Should()
            .ContainSingle("exactly one thumbnail per profile survives the failure")
            .Which.Should().Be(stored);
    }

    // SPEC-007#I45 — the same data loss without any lock at all, so it holds on every filesystem:
    // re-saving the profile's OWN stored thumbnail (the user re-picks the currently displayed file in
    // the upload dialog). Under delete-then-copy the sweep removed the file that was ALSO the copy's
    // source, so the copy failed with FileNotFoundException and the only surviving picture was already
    // gone. Copy-then-swap stages the bytes before anything is deleted, so the round trip is lossless.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_OfTheProfilesOwnStoredThumbnail_KeepsIt()
    {
        var store = new ProfileThumbnailStore(_root);
        var stored = store.Save("Series", MakeSource("a.jpg", "the-only-copy"));

        var again = store.Save("Series", stored); // the source IS the file the sweep is about to delete

        again.Should().Be(stored, "the same profile and extension resolve to the same stored path");
        File.Exists(again).Should().BeTrue("re-storing a profile's own thumbnail must not consume it");
        File.ReadAllText(again).Should().Be("the-only-copy", "the bytes round-trip through the staging file intact");
        Directory.EnumerateFiles(_root).Should().ContainSingle("still exactly one thumbnail for the profile");
        StagingFiles().Should().BeEmpty("the successful swap consumed the staging file — a move, not a second copy");
    }

    // PERFORMANCE (structural, not timed) — SPEC-007#I45 plus the I70 "no extra work" discipline.
    // Recovery from a failed Save is O(1) in files touched: it deletes the ONE staging file it created
    // and stops. It does not copy the prior thumbnail aside as a backup, does not restore it by
    // rewriting it, and does not re-touch the other profiles' thumbnails — so the failure cost does not
    // grow with how many thumbnails the root holds. A name|length|last-write inventory of the whole
    // root pins that: every one of those extra operations would show as a diff, and it must not.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_ThatFails_TouchesNoFileInTheRootBeyondItsOwnStaging()
    {
        var store = new ProfileThumbnailStore(_root);
        store.Save("Series A", MakeSource("a.jpg", "picture-a"));
        store.Save("Series B", MakeSource("b.png", "picture-b"));
        store.Save("Series C", MakeSource("c.jpg", "picture-c"));

        var before = SnapshotRoot();
        before.Should().HaveCount(3, "precondition: three unrelated thumbnails share the root");

        var replacement = MakeSource("locked.png", "never-lands");
        using (new FileStream(replacement, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ((Action)(() => store.Save("Series B", replacement)))
                .Should().Throw<IOException>("the locked source fails the copy");
        }

        SnapshotRoot().Should().Equal(before,
            "a failed Save writes nothing but its own staging file: no file is added, removed, rewritten, or backed up, so the recovery cost stays one delete however many thumbnails the root holds");
        StagingFiles().Should().BeEmpty("and that one staging file is gone");
    }

    // ---- Save FAILURE, part 2: the prior is renamed ASIDE, not deleted (residual window) ----------

    // SPEC-007#I73 (the MOVE half) via I45 steps (2) and (4) — the RESIDUAL window the fix above left
    // open. The tests above cover I73's COPY half, the easy one: nothing has been touched yet when a copy
    // fails. The swap itself, though, still ran as "delete the prior, THEN move staging into place", and a
    // move that failed AFTER that delete left the profile with no picture at all — the catch then swept
    // the staging file too, so BOTH the old and the new picture were gone. Strictly worse than the bug
    // that was fixed. Save now RENAMES the prior aside to "<file>.vsj-aside" and puts it back when the
    // move fails, so no ordering of failures can leave the profile empty-handed.
    //
    // Inducing that exact ordering needs a failure that lets the aside rename SUCCEED and only then
    // blocks the move — so the replacement carries a DIFFERENT extension (.png over the prior .jpg) and
    // an exclusive FileShare.None handle is held on the .png DESTINATION: the prior .jpg is renamed
    // aside for real (nothing holds it), the move onto the held .png is refused, and RestoreAsides is
    // the only thing that can put the .jpg back. The blocked .png shares the safe-name stem, so
    // RenameExistingAside tries to move it aside too and is refused — it stays put, which is its
    // documented best-effort outcome and leaves the destination genuinely unreplaceable.
    //
    // NOTE on the exception type: replacing a locked DESTINATION surfaces as UnauthorizedAccessException
    // on .NET 8 (the replacing move needs delete access on the target), not the IOException a locked
    // SOURCE gives, and UnauthorizedAccessException does NOT derive from IOException — hence the
    // either/or rather than a narrow pin. SPEC-007#I73 records the same caveat for callers.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_FailingMove_RestoresThePriorThumbnailFromItsAside_LeavingNoAsideOrStaging()
    {
        var store = new ProfileThumbnailStore(_root);
        var stored = store.Save("Series", MakeSource("a.jpg", "the-picture-the-profile-already-has"));
        var priorBytes = File.ReadAllBytes(stored);
        var priorWrittenUtc = File.GetLastWriteTimeUtc(stored);

        // The destination a .png replacement resolves to, held open exclusively below.
        var blockedDestination = Path.Combine(_root, ProfileThumbnailStore.SafeFileName("Series") + ".png");
        File.WriteAllText(blockedDestination, "held open by another program");
        blockedDestination.Should().NotBe(stored,
            "precondition: the replacement lands on a DIFFERENT path than the prior thumbnail, so the prior is displaced by the aside rename rather than by the move itself");

        var replacement = MakeSource("b.png", "never-lands");
        using (new FileStream(blockedDestination, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var thrown = ((Action)(() => store.Save("Series", replacement)))
                .Should().Throw<Exception>("a destination that cannot be replaced is a real failure the caller must see")
                .Which;

            (thrown is IOException or UnauthorizedAccessException).Should().BeTrue(
                "a locked destination surfaces as an I/O or access failure, never as a silent success (got {0})",
                thrown.GetType().Name);
        }

        File.Exists(stored).Should().BeTrue(
            "the prior thumbnail is PUT BACK: the move failed with the prior already moved out of the way, and that is exactly the window in which delete-then-move lost both pictures at once");
        File.ReadAllBytes(stored).Should().Equal(priorBytes,
            "and it comes back byte-for-byte — the recorded ThumbnailPath still points at the picture the profile already had");
        File.GetLastWriteTimeUtc(stored).Should().Be(priorWrittenUtc,
            "restored by renaming the aside back, not by rewriting it from a backup copy — a copy-based restore would move the timestamp");

        AsideFiles().Should().BeEmpty(
            "the restore consumed the aside: a failed Save leaves no .vsj-aside orphan beside the thumbnail it rescued");
        StagingFiles().Should().BeEmpty(
            "and no .incoming either — the staging file is swept AFTER the asides are restored, so the cleanup never races the rescue");

        File.ReadAllText(blockedDestination).Should().Be("held open by another program",
            "the blocked destination was never written — the move genuinely did not land, so the failure being observed is the swap itself");
        Directory.EnumerateFiles(_root).Should().BeEquivalentTo(new[] { stored, blockedDestination },
            "the failed Save left the root holding exactly the prior thumbnail and the file it could not replace — nothing added, nothing lost");
    }

    // PERFORMANCE (structural, not timed) — SPEC-007#I73 plus the I70 "no extra work" discipline, for the
    // failure that reaches the aside step. Recovery from a failed Save stays O(1) in files WRITTEN even
    // now that recovery does real work: it renames its own aside back and deletes its own staging file,
    // and that is all. It does not copy the prior thumbnail aside as a backup (a copy would move the
    // restored file's timestamp), does not rewrite it, and does not touch any OTHER profile's thumbnail
    // — the aside pass is stem-matched, so the number of files written stays constant however many
    // thumbnails share the root. One name|length|last-write inventory pins all of that at once: a
    // rename-out-and-back is invisible to it, while every extra operation named above would show as a
    // diff.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_ThatFailsAfterTheAsideRename_TouchesNoOtherFileInTheRoot()
    {
        var store = new ProfileThumbnailStore(_root);
        store.Save("Series A", MakeSource("a.jpg", "picture-a"));
        store.Save("Series B", MakeSource("b.jpg", "picture-b"));
        store.Save("Series C", MakeSource("c.jpg", "picture-c"));

        var blockedDestination = Path.Combine(_root, ProfileThumbnailStore.SafeFileName("Series B") + ".png");
        File.WriteAllText(blockedDestination, "held open by another program");

        var before = SnapshotRoot();
        before.Should().HaveCount(4,
            "precondition: three unrelated thumbnails share the root with the destination that will refuse the move");

        var replacement = MakeSource("replacement.png", "never-lands");
        using (new FileStream(blockedDestination, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ((Action)(() => store.Save("Series B", replacement)))
                .Should().Throw<Exception>("the locked destination fails the move, after the prior thumbnail has been renamed aside");
        }

        SnapshotRoot().Should().Equal(before,
            "the failure restored its own aside in place and swept its own staging file — nothing else in the root is renamed, rewritten, or backed up, so recovery stays O(1) however many thumbnails it holds");
        AsideFiles().Should().BeEmpty("no .vsj-aside survives the recovery");
        StagingFiles().Should().BeEmpty("and no .incoming either");
    }

    // ---- Save SUCCESS: the aside is cleaned up on commit -------------------------------------------

    // SPEC-007#I45 (the COMMIT side of the aside mechanism) — the aside is recovery state, not a backup:
    // once the move commits, the prior thumbnail is genuinely superseded and its .vsj-aside must go.
    // Keeping it would quietly double the store's disk use and leave a stale picture sitting beside every
    // thumbnail the user ever replaced.
    //
    // A stale aside from an imagined earlier interrupted save is planted first, for the same reason the
    // staging test plants one: nothing observable survives a correctly-cleaned aside, so a test that only
    // asserted "no .vsj-aside exists" would pass just as happily if the aside were never created at all.
    // The planted file can only disappear if Save's aside handling ran over it.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_SuccessfulSwap_DeletesTheAside_LeavingOnlyTheNewThumbnail()
    {
        var store = new ProfileThumbnailStore(_root);
        var stored = store.Save("Series", MakeSource("a.jpg", "prior-picture"));

        var staleAside = stored + ".vsj-aside";
        File.WriteAllText(staleAside, "left over from an earlier interrupted save");

        var again = store.Save("Series", MakeSource("b.jpg", "the-new-picture"));

        again.Should().Be(stored, "the same profile and extension resolve to the same stored path");
        File.ReadAllText(again).Should().Be("the-new-picture", "the swap committed — the new bytes are in place");
        File.Exists(staleAside).Should().BeFalse(
            "the planted aside was cleared before the prior thumbnail was renamed onto it and deleted again at commit — proof the aside handling actually ran, not merely that no aside was ever made");
        AsideFiles().Should().BeEmpty(
            "a committed swap leaves no .vsj-aside behind: the rescue copy is kept only for as long as the swap can still fail");
        StagingFiles().Should().BeEmpty("and the staging file was consumed by the move, not left beside it");
        Directory.EnumerateFiles(_root).Should()
            .ContainSingle("exactly one file per profile survives a successful save")
            .Which.Should().Be(stored);
    }

    // SPEC-007#I45 (the cross-extension COMMIT) — Save_OverwritesPriorThumbnail_IncludingAcrossExtensionChange
    // owns the overwrite contract itself and keeps passing; this pins the one way the NEW mechanism could
    // break it. A .png replacing a .jpg resolves to a different destination, so the prior .jpg is displaced
    // ONLY by being renamed aside — and if the commit restored that aside instead of deleting it, or simply
    // forgot it, the replaced picture would come back (under its own name, or as a .vsj-aside sibling) and
    // the profile would again own two files. Displacing across an extension change is exactly what the
    // aside pass inherited from the old sweep, so it is where a resurrection would show up.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void Save_AcrossAnExtensionChange_DeletesTheAside_WithoutResurrectingThePriorThumbnail()
    {
        var store = new ProfileThumbnailStore(_root);
        var first = store.Save("Series", MakeSource("a.jpg", "first"));

        var second = store.Save("Series", MakeSource("b.png", "second"));

        second.Should().EndWith(".png")
            .And.NotBe(first, "precondition: the replacement resolves to a different destination than the prior .jpg");
        File.ReadAllText(second).Should().Be("second");
        File.Exists(first).Should().BeFalse("the prior .jpg is displaced by the aside rename");
        File.Exists(first + ".vsj-aside").Should().BeFalse(
            "and its aside is DELETED at commit, never restored — a superseded picture must not come back under a sibling name");
        AsideFiles().Should().BeEmpty("no aside of any name survives a committed swap");
        Directory.EnumerateFiles(_root).Should()
            .ContainSingle("still exactly one thumbnail per profile across an extension change")
            .Which.Should().Be(second);
    }

    // ---- helpers for the failure tests -------------------------------------------------------------

    /// <summary>
    /// Every staging artifact sitting in the root — <c>&lt;safeName&gt;.incoming&lt;ext&gt;</c>, the file
    /// <see cref="ProfileThumbnailStore.Save"/> copies into before it swaps. Matched by substring rather
    /// than a search pattern so no legacy 8.3 wildcard quirk can hide one.
    /// </summary>
    private string[] StagingFiles() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root)
                .Where(f => Path.GetFileName(f).Contains(".incoming", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// Every aside artifact sitting in the root — <c>&lt;file&gt;.vsj-aside</c>, what
    /// <see cref="ProfileThumbnailStore.Save"/> renames a prior thumbnail to while it swaps, so a
    /// failed swap can put it back. Matched by name suffix rather than a search pattern, for the same
    /// reason <see cref="StagingFiles"/> is.
    /// </summary>
    private string[] AsideFiles() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root)
                .Where(f => Path.GetFileName(f).EndsWith(".vsj-aside", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// Ordered <c>name|length|last-write</c> inventory of the store root. Any write to any file — a
    /// rewrite, a backup copy, a sweep — shows up as a diff between two snapshots, which is how a
    /// failure path's I/O gets bounded without timing anything.
    /// </summary>
    private string[] SnapshotRoot() =>
        new DirectoryInfo(_root).EnumerateFiles()
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}")
            .ToArray();
}
