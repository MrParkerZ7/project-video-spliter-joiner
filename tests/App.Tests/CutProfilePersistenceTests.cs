using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Persistence tests for the cut-profile store on <see cref="AppSettings"/> (T-102): the profiles
/// round-trip through the JSON file (Save→Load returns the same list), upsert/delete/dedup by name are
/// case-insensitive, offsets persist as human-readable SECONDS, and — the migration guardrail — an
/// OLDER settings file that predates the feature loads cleanly to an empty list WITHOUT losing its
/// sibling fields or crashing. All I/O is redirected to a temp dir (the file path is injectable).
/// </summary>
public sealed class CutProfilePersistenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public CutProfilePersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-profiles-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SaveProfile_RoundTrips_ToDiskAndReloads()
    {
        var settings = new AppSettings(_file);
        var intro = new CutProfile("Intro only", TimeSpan.FromSeconds(8), null);
        var both = new CutProfile("Intro+Outro", TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20));

        settings.SaveProfile(intro);
        settings.SaveProfile(both);

        var reloaded = new AppSettings(_file);
        reloaded.CutProfiles.Should().Equal(new[] { intro, both },
            "the saved profiles round-trip in save order (records compare by value)");
        reloaded.CutProfiles[0].OutroFromEnd.Should().BeNull("a no-outro profile stays no-outro across the round-trip");
    }

    [Fact]
    public void SaveProfile_StoresOffsets_AsReadableSeconds()
    {
        var settings = new AppSettings(_file);
        settings.SaveProfile(new CutProfile("Series", TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20)));

        var json = File.ReadAllText(_file);
        json.Should().Contain("\"introSeconds\": 12").And.Contain("\"outroSeconds\": 20",
            "offsets persist as human-readable seconds (double), not TimeSpan ticks");
    }

    [Fact]
    public void SaveProfile_UpsertsByName_CaseInsensitive_InPlace()
    {
        var settings = new AppSettings(_file);
        settings.SaveProfile(new CutProfile("Series A", TimeSpan.FromSeconds(5), null));
        settings.SaveProfile(new CutProfile("Series B", TimeSpan.FromSeconds(6), null));

        // Re-save "series a" (different case) with new offsets — replaces in place, no duplicate.
        settings.SaveProfile(new CutProfile("series a", TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(3)));

        settings.CutProfiles.Should().HaveCount(2, "an upsert replaces, never appends a case-variant duplicate");
        var updated = settings.CutProfiles.Single(p => string.Equals(p.Name, "Series A", StringComparison.OrdinalIgnoreCase));
        updated.IntroFromStart.Should().Be(TimeSpan.FromSeconds(9), "the existing profile was updated in place");
        updated.OutroFromEnd.Should().Be(TimeSpan.FromSeconds(3));
        settings.CutProfiles[0].Name.Should().Be("series a", "position is preserved on upsert (Series A was first)");

        var reloaded = new AppSettings(_file);
        reloaded.CutProfiles.Should().HaveCount(2, "the upsert persisted");
    }

    [Fact]
    public void DeleteProfile_RemovesByName_CaseInsensitive_AndPersists()
    {
        var settings = new AppSettings(_file);
        settings.SaveProfile(new CutProfile("Keep", TimeSpan.FromSeconds(5), null));
        settings.SaveProfile(new CutProfile("Drop", TimeSpan.FromSeconds(6), null));

        settings.DeleteProfile("DROP"); // case-insensitive

        settings.CutProfiles.Select(p => p.Name).Should().ContainSingle().Which.Should().Be("Keep");

        var reloaded = new AppSettings(_file);
        reloaded.CutProfiles.Select(p => p.Name).Should().Equal(new[] { "Keep" }, "the delete persisted");
    }

    [Fact]
    public void DeleteProfile_UnknownOrBlankName_IsNoOp()
    {
        var settings = new AppSettings(_file);
        settings.SaveProfile(new CutProfile("Only", TimeSpan.FromSeconds(5), null));

        var act = () =>
        {
            settings.DeleteProfile("does-not-exist");
            settings.DeleteProfile("   ");
        };

        act.Should().NotThrow();
        settings.CutProfiles.Should().HaveCount(1, "deleting an unknown / blank name changes nothing");
    }

    [Fact]
    public void OlderFile_WithoutCutProfilesField_LoadsEmpty_OtherFieldsIntact()
    {
        // A legacy settings file that predates the feature: has the folder + layout keys, no cutProfiles.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file,
            "{ \"lastInputDir\": \"D:\\\\in\", \"lastOutputDir\": \"D:\\\\out\", \"layoutMode\": \"Vertical\", \"horizontalSplitRatio\": 0.7 }");

        var settings = new AppSettings(_file);

        settings.CutProfiles.Should().BeEmpty("a missing cutProfiles field ⇒ empty list, never a crash");
        settings.LastInputDir.Should().Be(@"D:\in", "the sibling fields survive the additive migration");
        settings.LastOutputDir.Should().Be(@"D:\out");
        settings.LayoutMode.Should().Be(LayoutMode.Vertical);
        settings.HorizontalSplitRatio.Should().Be(0.7);
    }

    [Fact]
    public void CorruptProfileEntry_IsSkipped_ValidOnesSurvive()
    {
        // Hand-crafted file: one blank-name entry + one negative-intro entry (both invalid) + one good one.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file,
            "{ \"cutProfiles\": [" +
            "{ \"name\": \"\", \"introSeconds\": 5 }," +
            "{ \"name\": \"Negative\", \"introSeconds\": -3 }," +
            "{ \"name\": \"Good\", \"introSeconds\": 10, \"outroSeconds\": 4 }" +
            "] }");

        var settings = new AppSettings(_file);

        settings.CutProfiles.Select(p => p.Name).Should().Equal(new[] { "Good" },
            "malformed entries are skipped, never crashing the load or losing the valid rows");
        settings.CutProfiles[0].OutroFromEnd.Should().Be(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void DuplicateNamesInFile_AreDedupedOnLoad()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file,
            "{ \"cutProfiles\": [" +
            "{ \"name\": \"Dup\", \"introSeconds\": 5 }," +
            "{ \"name\": \"dup\", \"introSeconds\": 9 }" +
            "] }");

        var settings = new AppSettings(_file);

        settings.CutProfiles.Should().ContainSingle("a case-insensitive duplicate name collapses to one entry")
            .Which.IntroFromStart.Should().Be(TimeSpan.FromSeconds(5), "first occurrence wins");
    }

    [Fact]
    public void NoProfiles_OmitsTheKey_KeepingOlderFilesByteClean()
    {
        var settings = new AppSettings(_file);
        settings.LastInputDir = @"D:\in"; // force a write with no profiles

        File.ReadAllText(_file).Should().NotContain("cutProfiles",
            "with no saved profiles the key is omitted entirely (empty list ≠ an empty array in the file)");
    }

    // ---- SPEC-007 cut-profiles gap (todo-automate) ------------------------------------------

    // SPEC-007#I11 — SaveProfile(null) throws ArgumentNullException (ArgumentNullException.ThrowIfNull
    // in AppSettings.SaveProfile). No existing persistence test passes null to SaveProfile.
    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void SaveProfile_Null_ThrowsArgumentNullException()
    {
        var settings = new AppSettings(_file);

        ((Action)(() => settings.SaveProfile(null!)))
            .Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("profile");
    }

    // ---- SPEC-007 — optional thumbnail path round-trip (T-106) --------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void SaveProfile_WithThumbnail_RoundTrips_PathPreserved()
    {
        var settings = new AppSettings(_file);
        var withThumb = new CutProfile("Series", TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12), @"C:\thumbs\series.png");
        var without = new CutProfile("Bare", TimeSpan.FromSeconds(5), null);

        settings.SaveProfile(withThumb);
        settings.SaveProfile(without);

        var reloaded = new AppSettings(_file);
        reloaded.CutProfiles.Should().Equal(new[] { withThumb, without },
            "the thumbnail path round-trips through the JSON (records compare by value, thumbnail included)");
        reloaded.CutProfiles[0].ThumbnailPath.Should().Be(@"C:\thumbs\series.png");
        reloaded.CutProfiles[1].ThumbnailPath.Should().BeNull("a no-thumbnail profile stays null across the round-trip");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void SaveProfile_PersistsThumbnail_AsPathString_NotBytes()
    {
        var settings = new AppSettings(_file);
        settings.SaveProfile(new CutProfile("Series", TimeSpan.FromSeconds(8), null, @"C:\thumbs\series.png"));

        var json = File.ReadAllText(_file);
        json.Should().Contain("\"thumbnailPath\"", "the thumbnail is persisted as a PATH key");
        json.Should().Contain("series.png", "the stored value is the path string, never image bytes");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void NoThumbnail_OmitsTheKey_KeepingProfilesByteClean()
    {
        var settings = new AppSettings(_file);
        settings.SaveProfile(new CutProfile("Bare", TimeSpan.FromSeconds(5), null));

        File.ReadAllText(_file).Should().NotContain("thumbnailPath",
            "a null thumbnail is omitted entirely (WhenWritingNull) so profiles without one stay byte-clean");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-007")]
    public void OlderProfileEntry_WithoutThumbnailPathField_LoadsNull_SiblingsIntact()
    {
        // A settings file whose cutProfiles entry predates the thumbnail field: name + offsets, no thumbnailPath.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file,
            "{ \"cutProfiles\": [" +
            "{ \"name\": \"Legacy\", \"introSeconds\": 10, \"outroSeconds\": 4 }" +
            "] }");

        var settings = new AppSettings(_file);

        settings.CutProfiles.Should().ContainSingle();
        var profile = settings.CutProfiles[0];
        profile.ThumbnailPath.Should().BeNull("an absent thumbnailPath field ⇒ null, never a crash");
        profile.Name.Should().Be("Legacy", "the sibling fields survive the additive migration");
        profile.IntroFromStart.Should().Be(TimeSpan.FromSeconds(10));
        profile.OutroFromEnd.Should().Be(TimeSpan.FromSeconds(4));
    }

    // ---- SPEC-009 — corrupt-row skip: the out-of-TimeSpan-range branch (I21) ------------------

    // SPEC-009#I21 — a persisted row whose outroSeconds is finite and non-negative, yet too large to
    // become a TimeSpan (1e300 seconds overflows TimeSpan.FromSeconds), is one more CORRUPT ROW: it must
    // be SKIPPED, leaving its valid sibling rows — and the sibling top-level fields — intact.
    // IsFiniteNonNegative admits 1e300 (it is neither NaN, infinite, nor negative), so the failure lands
    // on the TimeSpan conversion rather than on the record's own validation. CorruptProfileEntry_IsSkipped_
    // ValidOnesSurvive already covers the blank-name and negative-offset branches; this pins the
    // out-of-range one, where "skipped" must still mean "the load itself survives".
    [Fact]
    [Trait("serves-spec", "SPEC-009")]
    public void ProfileRow_WithOutOfRangeOutroSeconds_IsSkipped_SiblingsSurvive()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file,
            "{ \"lastInputDir\": \"D:\\\\keep\", \"cutProfiles\": [" +
            "{ \"name\": \"Overflow\", \"introSeconds\": 1, \"outroSeconds\": 1e300 }," +
            "{ \"name\": \"Good\", \"introSeconds\": 10, \"outroSeconds\": 4 }" +
            "] }");

        var settings = new AppSettings(_file);

        settings.CutProfiles.Select(p => p.Name).Should().Equal(new[] { "Good" },
            "an out-of-TimeSpan-range outroSeconds is a corrupt row — it is skipped, never taking the valid sibling rows down with it");
        settings.CutProfiles[0].IntroFromStart.Should().Be(TimeSpan.FromSeconds(10));
        settings.CutProfiles[0].OutroFromEnd.Should().Be(TimeSpan.FromSeconds(4));
        settings.LastInputDir.Should().Be(@"D:\keep",
            "skipping one bad profile row must never degrade into the whole-file corrupt-JSON fallback — the sibling top-level fields survive");
    }
}
