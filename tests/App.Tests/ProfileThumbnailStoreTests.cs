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
/// to the thumbnail file. All I/O is redirected to a temp root (the root is injectable).
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
}
