using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-147 (SPEC-007) — profiles you can actually keep: export to one file, import anywhere.
///
/// <para><b>Why one file with images inline.</b> A profile lives across two roots — the profile in Roaming
/// <c>%APPDATA%</c>, its picture in Local <c>%LOCALAPPDATA%</c>. Back up "your settings" and the profiles
/// survive with a <c>ThumbnailPath</c> pointing at a folder that does not exist on the new machine, so
/// every picture is silently gone. The backup carries the images inline so one file is the whole story
/// (ADR-0021).</para>
///
/// <para>The load-bearing tests here are the destructive ones: a corrupt file must change NOTHING, and an
/// import must never be able to cost someone the profiles they already had. "Restore" being the most
/// dangerous button in the app would be an unusually cruel bug.</para>
/// </summary>
public sealed class ProfileBackupTests : IDisposable
{
    private readonly string _dir;

    public ProfileBackupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private string MakeImage(string name, string content = "picture-bytes")
    {
        var p = Path_(name);
        File.WriteAllText(p, content);
        return p;
    }

    private ProfileThumbnailStore NewStore() =>
        new(Path.Combine(_dir, "store-" + Guid.NewGuid().ToString("N")));

    private static CutProfile Profile(string name, double intro = 5, double? outro = null, string? thumb = null) =>
        new(name, TimeSpan.FromSeconds(intro), outro is { } o ? TimeSpan.FromSeconds(o) : null, thumb);

    // ---- Export -----------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ExportWritesEveryProfile_WithItsImageInline()
    {
        var img = MakeImage("a.png", "AAA");
        var dest = Path_("backup.json");

        var (profiles, images) = ProfileBackup.Export(
            new[] { Profile("Anime OP", 90, 30, img), Profile("No picture", 12) }, dest);

        profiles.Should().Be(2);
        images.Should().Be(1);

        var json = File.ReadAllText(dest);
        json.Should().Contain("Anime OP").And.Contain("No picture");
        json.Should().Contain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("AAA")),
            "the picture travels IN the file — a path would not survive the trip to another machine");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AProfileWhoseImageIsMissing_IsStillExported_JustWithoutIt()
    {
        var dest = Path_("backup.json");

        var (profiles, images) = ProfileBackup.Export(
            new[] { Profile("Ghost", 5, null, Path_("does-not-exist.png")) }, dest);

        profiles.Should().Be(1, "losing a profile because its picture went missing would be a poor trade");
        images.Should().Be(0);
    }

    // ---- Round trip -------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AProfileSurvivesAFullRoundTrip_PictureIncluded()
    {
        var dest = Path_("backup.json");
        ProfileBackup.Export(new[] { Profile("Anime OP", 90, 30, MakeImage("a.png", "PIC")) }, dest);

        // A machine where neither the profile nor its picture exists.
        var settings = new FakeSettings();
        var store = NewStore();

        var plan = ProfileBackup.Plan(dest, settings.CutProfiles);
        plan.Failed.Should().BeFalse();
        plan.New.Should().ContainSingle().Which.Name.Should().Be("Anime OP");

        var (written, restored) = ProfileBackup.Apply(plan, settings, store, includeColliding: false);

        written.Should().Be(1);
        restored.Should().Be(1);

        var landed = settings.CutProfiles.Single();
        landed.IntroFromStart.Should().Be(TimeSpan.FromSeconds(90));
        landed.OutroFromEnd.Should().Be(TimeSpan.FromSeconds(30));
        landed.ThumbnailPath.Should().NotBeNullOrWhiteSpace("the picture is restored, not just remembered");
        File.Exists(landed.ThumbnailPath!).Should().BeTrue();
        File.ReadAllText(landed.ThumbnailPath!).Should().Be("PIC", "and it is the SAME picture, byte for byte");
    }

    // ---- The destructive cases --------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"version\": 1, \"profiles\":")]           // truncated
    [InlineData("{ \"version\": 1 }")]                        // no profiles key
    public void ACorruptOrTruncatedFile_ChangesNothing(string content)
    {
        var path = Path_("bad.json");
        File.WriteAllText(path, content);

        var settings = new FakeSettings();
        settings.SaveProfile(Profile("Keep me", 7));

        var plan = ProfileBackup.Plan(path, settings.CutProfiles);

        plan.Failed.Should().BeTrue("a half-applied restore is worse than a refused one");
        plan.Error.Should().NotBeNullOrWhiteSpace("and the user is told why, not left guessing");

        // The invariant the safety actually rests on. Apply() also refuses a failed plan outright, but
        // that guard is defence in depth - it is THIS that makes a failed import a no-op, and if Plan
        // ever started returning partially-filled lists beside an Error, the guard would silently become
        // the only thing standing between a corrupt file and someone's profiles.
        plan.New.Should().BeEmpty("a failed plan proposes nothing");
        plan.Colliding.Should().BeEmpty();
        plan.Images.Should().BeEmpty();

        ProfileBackup.Apply(plan, settings, NewStore(), includeColliding: true);

        settings.CutProfiles.Should().ContainSingle().Which.Name.Should().Be(
            "Keep me", "the profiles already there are untouched by a failed import");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ABackupFromANewerVersion_IsRefused_NotGuessedAt()
    {
        var path = Path_("future.json");
        File.WriteAllText(path, "{ \"version\": 999, \"profiles\": [] }");

        var plan = ProfileBackup.Plan(path, Array.Empty<CutProfile>());

        plan.Failed.Should().BeTrue();
        plan.Error.Should().Contain("newer", "guessing at a future format is how data gets mangled");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ACorruptImage_CostsThePicture_NeverTheProfile()
    {
        var path = Path_("badimg.json");
        File.WriteAllText(path,
            "{ \"version\": 1, \"profiles\": [ { \"name\": \"A\", \"introSeconds\": 5, " +
            "\"imageBase64\": \"!!!not-base64!!!\", \"imageExtension\": \".png\" } ] }");

        var plan = ProfileBackup.Plan(path, Array.Empty<CutProfile>());

        plan.Failed.Should().BeFalse();
        plan.New.Should().ContainSingle().Which.Name.Should().Be("A");
        plan.Images.Should().NotContainKey("A", "the picture is dropped; the profile is not");
    }

    // ---- Collisions are the user's call ------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ANameCollision_IsReportedSeparately_NotAppliedSilently()
    {
        var dest = Path_("backup.json");
        ProfileBackup.Export(new[] { Profile("Anime OP", 99), Profile("Brand new", 12) }, dest);

        var settings = new FakeSettings();
        settings.SaveProfile(Profile("Anime OP", 5));   // theirs, different value

        var plan = ProfileBackup.Plan(dest, settings.CutProfiles);

        plan.New.Should().ContainSingle().Which.Name.Should().Be("Brand new");
        plan.Colliding.Should().ContainSingle().Which.Name.Should().Be("Anime OP");

        // Declining collisions leaves the existing one exactly as it was.
        ProfileBackup.Apply(plan, settings, NewStore(), includeColliding: false);

        settings.CutProfiles.Single(p => p.Name == "Anime OP").IntroFromStart.Should().Be(
            TimeSpan.FromSeconds(5), "an import must never overwrite without being told to");
        settings.CutProfiles.Should().HaveCount(2, "the non-colliding one still arrives");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AcceptingCollisions_Overwrites_BecauseTheUserSaidSo()
    {
        var dest = Path_("backup.json");
        ProfileBackup.Export(new[] { Profile("Anime OP", 99) }, dest);

        var settings = new FakeSettings();
        settings.SaveProfile(Profile("Anime OP", 5));

        var plan = ProfileBackup.Plan(dest, settings.CutProfiles);
        ProfileBackup.Apply(plan, settings, NewStore(), includeColliding: true);

        settings.CutProfiles.Single().IntroFromStart.Should().Be(TimeSpan.FromSeconds(99));
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ANamelessOrInvalidRow_IsSkipped_NotForcedIn()
    {
        var path = Path_("odd.json");
        File.WriteAllText(path,
            "{ \"version\": 1, \"profiles\": [ { \"name\": \"  \", \"introSeconds\": 5 }, " +
            "{ \"name\": \"Fine\", \"introSeconds\": 5 } ] }");

        var plan = ProfileBackup.Plan(path, Array.Empty<CutProfile>());

        plan.Failed.Should().BeFalse("one bad row does not condemn the file");
        plan.New.Should().ContainSingle().Which.Name.Should().Be("Fine");
    }

    // ---- Performance --------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void PlanningReadsTheFileOnce_AndChangesNothingOnDisk()
    {
        var dest = Path_("backup.json");
        ProfileBackup.Export(new[] { Profile("A", 5, null, MakeImage("a.png")) }, dest);
        var stamp = File.GetLastWriteTimeUtc(dest);
        var settings = new FakeSettings();

        for (var i = 0; i < 5; i++)
        {
            ProfileBackup.Plan(dest, settings.CutProfiles).Failed.Should().BeFalse();
        }

        File.GetLastWriteTimeUtc(dest).Should().Be(stamp, "planning is a read — it must not touch the file");
        settings.CutProfiles.Should().BeEmpty("and it must not apply anything either");
    }
}
