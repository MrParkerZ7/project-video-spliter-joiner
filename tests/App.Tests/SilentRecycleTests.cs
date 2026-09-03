using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Io;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-155 (SPEC-011) — binning an original must never put a Windows dialog on screen.
///
/// <para>Reported as: <i>"currently delete original files is work, but everytime windows will popup
/// unable to remove file able"</i>. The cause was findable rather than mysterious —
/// <c>FileSystem.DeleteFile</c> takes a <c>UIOption</c> whose only values are <c>AllDialogs</c> and
/// <c>OnlyErrorDialogs</c>. There is no silent option, so every locked file raised shell UI and no
/// configuration could stop it.</para>
///
/// <para>These tests cannot see a dialog. What they CAN pin is the property that makes one impossible:
/// a locked file comes back as a reported refusal with a result the app owns, rather than being handed
/// to the shell to complain about. The visual half is the reporter's to confirm, and the ticket says
/// so.</para>
/// </summary>
public sealed class SilentRecycleTests : IDisposable
{
    private readonly string _dir;

    public SilentRecycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t155-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Make(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "content");
        return p;
    }

    // ---- The happy path: it must actually recycle, not merely not crash -----------------------------

    /// <summary>
    /// T-155 — the file really goes, and it goes to the RECYCLE BIN.
    ///
    /// <para>The flag that decides recycle-vs-permanent is a single bit (<c>FOF_ALLOWUNDO</c>). Losing it
    /// would delete the user's originals irrecoverably while every other test here still passed, because
    /// they only check that the file is gone. Recoverability is the entire reason this feature is safe to
    /// offer, so it is asserted directly.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void AnUnlockedFileIsActuallyRecycled()
    {
        var path = Make("goes-to-the-bin.mp4");

        ShellRecycleBin.TryRecycle(path).Should().BeTrue();
        File.Exists(path).Should().BeFalse("it was not locked, so it should be gone from where it was");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void RecyclingIsNotPermanentDeletion()
    {
        // FOF_ALLOWUNDO is what routes the file to the bin. If it is ever dropped, the file still
        // disappears and a "did it vanish" assertion still passes - so check the flag is present in the
        // source rather than only its visible effect, which is identical either way.
        var src = FindSource("ShellRecycleBin.cs");
        src.Should().NotBeNull();

        var text = File.ReadAllText(src!);
        text.Should().Contain("FOF_ALLOWUNDO", "without it this silently becomes permanent deletion");
        text.Should().Contain(
            "FOF_ALLOWUNDO | FOF_SILENT",
            "and it must actually be passed in the flags, not merely declared");
    }

    private static string? FindSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "App", "Io", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    // ---- The refusal path, which is where the dialog used to appear ----------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void ALockedFileIsRefused_WithAResultRatherThanADialog()
    {
        var path = Make("locked.mp4");

        // A real exclusive lock — the exact condition that raised the shell error dialog.
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var recycled = ShellRecycleBin.TryRecycle(path, attempts: 1, delayMs: 0);

        recycled.Should().BeFalse("the file is genuinely held — the honest answer is 'no'");
        File.Exists(path).Should().BeTrue("and it must still be there, not half-removed");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheDisposerSwallowsALockedFileWithoutThrowing()
    {
        var path = Make("locked2.mp4");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var act = () => new RecycleBinOriginalDisposer().DisposeOriginalBackup(path);

        act.Should().NotThrow(
            "best-effort by contract — a stray backup must never fail an otherwise-successful run");
        File.Exists(path).Should().BeTrue();
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void AnAbsentFileIsAlreadyInTheDesiredState()
    {
        ShellRecycleBin.TryRecycle(Path.Combine(_dir, "never-existed.mp4"))
            .Should().BeTrue("gone is gone — reporting that as a failure would name an innocent file");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GarbagePathsAreSurvivable(string? path)
    {
        var act = () => ShellRecycleBin.TryRecycle(path!);
        act.Should().NotThrow();
    }

    // ---- The retry, which covers the handle that is about to close -----------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheRetryIsBounded_AndDoesNotHangOnAPermanentlyHeldFile()
    {
        var path = Make("held.mp4");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        // Structural, not wall-clock: 3 attempts at 10ms cannot become an unbounded wait. A long retry
        // would trade a dialog for a frozen app, which is not an improvement.
        var recycled = ShellRecycleBin.TryRecycle(path, attempts: 3, delayMs: 10);

        recycled.Should().BeFalse();
        File.Exists(path).Should().BeTrue();
    }

    // ---- Naming the holder — the reporter's actual question ------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheHolderOfALockedFileIsIdentifiedByName()
    {
        var path = Make("who-has-it.mp4");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var holders = FileLockOwner.WhoIsHolding(path);

        // THIS test process is holding it, so Restart Manager must name us. If it cannot manage that,
        // it will not manage ffmpeg or an antivirus scanner either, and the feature is decoration.
        holders.Should().NotBeEmpty("the whole point is to answer 'what is holding this file?'");
        string.Join(" ", holders).Should().Contain(
            "testhost", "the holder here is the test runner — a real name, not a placeholder");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void AFileNobodyHoldsNamesNobody()
    {
        FileLockOwner.WhoIsHolding(Make("free.mp4")).Should().BeEmpty(
            "an unheld file must not produce a spurious culprit");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Z:\does\not\exist.mp4")]
    public void TheHolderLookupNeverThrows(string path)
    {
        var act = () => FileLockOwner.WhoIsHolding(path);
        act.Should().NotThrow("a diagnostic that throws is worse than one that says nothing");
    }
}
