using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using VideoSplitJoiner.Core.Io;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// SPEC-002 I42–I44 — <see cref="OriginalReplacer"/> is the ONE implementation of "put this produced file
/// where the user's original lives, and never let the bytes exist nowhere". It was <c>private</c> inside
/// <c>SplitEngine</c> until T-130; extracting it is what let the frame-exact route stop refusing
/// Exact + ReplaceOriginal (I54) and reuse the same guarantee instead of its own delete-then-move.
///
/// <para>Because it had only ever been exercised THROUGH <c>SplitEngine</c>, its own contract was never
/// pinned. These tests drive it directly on real files: the swap, the stale-backup sweep, the rename-aside
/// fallback, and — the reason the type exists at all — the restore branch that puts the user's original
/// back when the fallback's second move fails.</para>
///
/// <para><b>Inducing the fallback honestly.</b> The exFAT / SMB
/// <see cref="PlatformNotSupportedException"/> cannot be produced on the NTFS temp volume these tests run
/// on. The catch arm names three exception types, though, and a <b>read-only destination</b> genuinely
/// raises one of them (<see cref="UnauthorizedAccessException"/>) out of
/// <see cref="File.Replace(string,string,string,bool)"/> while leaving both fallback renames free to
/// succeed — a real user-facing case (a master the user marked read-only), not a stub. Nothing here fakes
/// a failure or reaches past the public surface.</para>
///
/// <para><b>Windows / NTFS.</b> Like <c>ReplaceOriginalSafetyTests</c>, these lean on Windows file
/// semantics: <see cref="FileShare.None"/> as a hard lock, and the NTFS file index as a file's identity —
/// the only managed-level way to tell a RENAME from a COPY, since Win32 <c>CopyFile</c> faithfully
/// reproduces size, attributes, timestamps and alternate streams. The move-vs-copy assertion ships with a
/// real copy as its control, so the discriminator is demonstrated rather than assumed.</para>
/// </summary>
public sealed class OriginalReplacerTests
{
    private const string OriginalContent = "THE-USER-MASTER-FILE";

    private const string ProducedContent = "THE-TRIMMED-RESULT";

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-replacer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// Clears attributes before the sweep — the fallback tests deliberately mark files read-only, and a
    /// read-only leftover would otherwise defeat <see cref="Directory.Delete(string,bool)"/>.
    /// </summary>
    private static void Cleanup(string dir)
    {
        try
        {
            foreach (var leftover in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(leftover, FileAttributes.Normal); } catch { /* best-effort */ }
            }

            Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// The two real files every swap needs: the user's master, and the sibling temp the frame-exact route
    /// writes beside it (the <c>.vsj-exact</c> shape <c>BulkTrimEngine</c> builds under ReplaceOriginal).
    /// </summary>
    private static (string Original, string Produced) Fixture(string dir, string name = "clip.mp4")
    {
        var original = Path.Combine(dir, name);
        var produced = original + ".vsj-exact" + Path.GetExtension(name);
        File.WriteAllText(original, OriginalContent);
        File.WriteAllText(produced, ProducedContent);
        return (original, produced);
    }

    private static string Backup(string original) => original + OriginalReplacer.BackupSuffix;

    private static byte[] Payload(int bytes, byte fill)
    {
        var buffer = new byte[bytes];
        Array.Fill(buffer, fill);
        return buffer;
    }

    /// <summary>Records every backup handed over, and keeps the file so recoverability stays assertable.</summary>
    private sealed class RecordingDisposer : IOriginalDisposer
    {
        public List<string> Disposed { get; } = new();

        public void DisposeOriginalBackup(string backupPath) => Disposed.Add(backupPath);
    }

    /// <summary>
    /// Records the state of the world AT THE MOMENT the backup is handed over — the only way to pin the
    /// ordering half of I44 from outside: the disposer is reached only after the swap has already
    /// committed, never while the outcome is still in doubt.
    /// </summary>
    private sealed class InspectingDisposer : IOriginalDisposer
    {
        private readonly string _originalPath;
        private readonly string _producedPath;

        public InspectingDisposer(string originalPath, string producedPath)
        {
            _originalPath = originalPath;
            _producedPath = producedPath;
        }

        public int Calls { get; private set; }

        public string? OriginalContentAtCallTime { get; private set; }

        public string? BackupContentAtCallTime { get; private set; }

        public bool ProducedTempExistedAtCallTime { get; private set; }

        public void DisposeOriginalBackup(string backupPath)
        {
            Calls++;
            OriginalContentAtCallTime = File.Exists(_originalPath) ? File.ReadAllText(_originalPath) : null;
            BackupContentAtCallTime = File.Exists(backupPath) ? File.ReadAllText(backupPath) : null;
            ProducedTempExistedAtCallTime = File.Exists(_producedPath);
        }
    }

    // ---- The swap: produced bytes in, original kept as a backup (I42) --------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void Replace_PutsTheProducedBytesAtTheOriginalsPath_AndOffersExactlyOneBackup()
    {
        var dir = NewDir();
        try
        {
            var (original, produced) = Fixture(dir);
            var disposer = new RecordingDisposer();

            new OriginalReplacer(disposer).Replace(produced, original);

            File.ReadAllText(original).Should().Be(
                ProducedContent, "the produced bytes now live at the path the user knows their file by");
            File.ReadAllText(Backup(original)).Should().Be(
                OriginalContent,
                "the pre-replacement original is kept under the backup name — the bytes are never nowhere");
            File.Exists(produced).Should().BeFalse(
                "the sibling temp was consumed by the swap, not left beside the user's file");
            disposer.Disposed.Should().ContainSingle("one swap produces exactly one backup")
                .Which.Should().Be(
                    original + ".vsj-original", "the disposer is handed that backup's path, verbatim");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void TheBackupSuffix_IsTheOneAnInterruptedRunLeavesBehind()
    {
        OriginalReplacer.BackupSuffix.Should().Be(
            ".vsj-original",
            "the suffix is a cross-version contract, not an implementation detail: it is what the user sees "
            + "beside their file and what the next run sweeps as a stale backup, so changing it silently "
            + "orphans every backup an earlier version wrote");
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void TheBackupIsHandedOver_OnlyAfterTheSwapHasCommitted()
    {
        var dir = NewDir();
        try
        {
            var (original, produced) = Fixture(dir);
            var disposer = new InspectingDisposer(original, produced);

            new OriginalReplacer(disposer).Replace(produced, original);

            disposer.Calls.Should().Be(1, "one replaced original offers exactly one backup for disposal");
            disposer.OriginalContentAtCallTime.Should().Be(
                ProducedContent, "the produced output had ALREADY taken the original's place");
            disposer.BackupContentAtCallTime.Should().Be(
                OriginalContent, "the backup handed over is the user's original, byte-identical");
            disposer.ProducedTempExistedAtCallTime.Should().BeFalse(
                "the swap was fully committed before the destructive step was ever offered");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void AStaleBackupFromAnInterruptedRun_IsClearedFirst_AndNeverBreaksTheSwap()
    {
        var dir = NewDir();
        try
        {
            var (original, produced) = Fixture(dir);
            File.WriteAllText(Backup(original), "stale-from-a-crashed-run");
            var disposer = new RecordingDisposer();

            Action act = () => new OriginalReplacer(disposer).Replace(produced, original);
            act.Should().NotThrow(
                "a leftover .vsj-original from an earlier interrupted run is swept, never a reason to fail");

            File.ReadAllText(original).Should().Be(
                ProducedContent, "the trimmed output took the original's place");
            File.ReadAllText(Backup(original)).Should().Be(
                OriginalContent,
                "the backup holds THIS run's original, not the stale bytes of the interrupted one");
            disposer.Disposed.Should().ContainSingle(
                "one swap, one backup — the stale file is swept, not handed over as a second one");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void TheBackupsFate_IsTheInjectedDisposersDecision()
    {
        var dir = NewDir();
        try
        {
            var (kept, keptProduced) = Fixture(dir, "kept.mp4");
            var (gone, goneProduced) = Fixture(dir, "gone.mp4");

            new OriginalReplacer(new KeepOriginalBackupDisposer()).Replace(keptProduced, kept);
            new OriginalReplacer(new DeleteOriginalBackupDisposer()).Replace(goneProduced, gone);

            File.ReadAllText(kept).Should().Be(ProducedContent, "the output lands whatever the backup's fate");
            File.ReadAllText(gone).Should().Be(ProducedContent, "the output lands whatever the backup's fate");
            File.Exists(Backup(kept)).Should().BeTrue(
                "KeepOriginalBackupDisposer is Core's safest-possible default — nothing is destroyed headlessly");
            File.ReadAllText(Backup(kept)).Should().Be(
                OriginalContent, "the kept backup is the user's original, byte-identical and recoverable");
            File.Exists(Backup(gone)).Should().BeFalse(
                "DeleteOriginalBackupDisposer removes it — the opted-in, no-undo behaviour");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void AReplacerWithoutADisposer_IsRejectedAtConstruction()
    {
        Action act = () => new OriginalReplacer(null!);

        act.Should().Throw<ArgumentNullException>(
            "the backup's fate must always have an owner — a null disposer would strand every backup "
            + "silently, so the dependency is required, never optional");
    }

    // ---- The fallback when File.Replace cannot be used (I43) -----------------------------------

    /// <summary>
    /// The rename-aside route, driven by a genuine <see cref="UnauthorizedAccessException"/> out of
    /// <see cref="File.Replace(string,string,string,bool)"/> — one of the three exception types the catch
    /// arm names, raised here by a read-only destination. That the swap nonetheless completes is itself
    /// the proof the fallback ran: <c>File.Replace</c> demonstrably cannot land this one.
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void WhenFileReplaceCannotBeUsed_TheOriginalIsMovedAside_AndTheProducedFileMovedIn()
    {
        var dir = NewDir();
        try
        {
            var (original, produced) = Fixture(dir);
            var producedIdentity = FileIdentity(produced);
            var originalIdentity = FileIdentity(original);
            File.SetAttributes(original, FileAttributes.ReadOnly);
            var disposer = new RecordingDisposer();

            new OriginalReplacer(disposer).Replace(produced, original);

            File.ReadAllText(original).Should().Be(
                ProducedContent, "the fallback lands the produced bytes just as the atomic route would");
            File.ReadAllText(Backup(original)).Should().Be(
                OriginalContent, "the original was renamed ASIDE first, so its bytes were never at risk");
            File.Exists(produced).Should().BeFalse("the sibling temp was moved, not left behind");
            disposer.Disposed.Should().ContainSingle(
                    "the fallback owes the same single backup as the fast path")
                .Which.Should().Be(Backup(original));

            FileIdentity(original).Should().Be(
                producedIdentity,
                "the file at the original's path IS the produced temp — renamed in, not copied in");
            FileIdentity(Backup(original)).Should().Be(
                originalIdentity, "and the backup IS the user's original — renamed aside, not duplicated");
            new FileInfo(original).IsReadOnly.Should().BeFalse(
                "the produced file arrived with its OWN attributes, the signature of a rename-aside — "
                + "File.Replace would have preserved the replaced file's read-only flag instead");
            new FileInfo(Backup(original)).IsReadOnly.Should().BeTrue(
                "the original kept its read-only flag through the move aside");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The branch the whole type exists for. Two independent, real inductions stack: the destination is
    /// read-only, so <c>File.Replace</c> raises <see cref="UnauthorizedAccessException"/> and the fallback
    /// takes over; and the produced temp is held with <see cref="FileShare.None"/> — the sharing shape an
    /// exFAT / SMB volume fails with — so the fallback's SECOND move fails and the restore must run.
    ///
    /// <para>The surfaced exception is what proves the route: an <see cref="IOException"/> reaches the
    /// caller, NOT the <see cref="UnauthorizedAccessException"/> <c>File.Replace</c> raised, so this run
    /// demonstrably got past <c>File.Replace</c>, through the rename-aside, and into the restore. That the
    /// move-aside itself succeeds under exactly this precondition is pinned by
    /// <see cref="WhenFileReplaceCannotBeUsed_TheOriginalIsMovedAside_AndTheProducedFileMovedIn"/>, whose
    /// only difference is the absence of the lock.</para>
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void WhenTheFallbacksSecondMoveFails_TheOriginalIsPutBack_AndTheFailureSurfaces()
    {
        var dir = NewDir();
        try
        {
            var (original, produced) = Fixture(dir);
            var originalIdentity = FileIdentity(original);
            File.SetAttributes(original, FileAttributes.ReadOnly);
            var disposer = new RecordingDisposer();

            using var lockedTemp = new FileStream(produced, FileMode.Open, FileAccess.Read, FileShare.None);

            Action act = () => new OriginalReplacer(disposer).Replace(produced, original);
            act.Should().Throw<IOException>(
                "File.Replace's UnauthorizedAccessException was absorbed by the fallback, so what reaches "
                + "the caller is the SECOND move's own failure — the run went through the rename-aside and "
                + "into the restore branch rather than stopping at File.Replace, and a swap that cannot "
                + "complete must surface, never half-write");

            File.Exists(original).Should().BeTrue("the user's file is present after a failed swap");
            File.ReadAllText(original).Should().Be(
                OriginalContent, "the backup was moved BACK over the original, byte-identical");
            FileIdentity(original).Should().Be(
                originalIdentity,
                "and it is the SAME file on disk — moved aside and moved back, never re-created from a copy");
            new FileInfo(original).IsReadOnly.Should().BeTrue(
                "attributes survived the round trip too — the restore is a rename, not a rewrite");
            File.Exists(Backup(original)).Should().BeFalse(
                "the restore leaves no stray .vsj-original for the next run to mistake for a stale one");
            disposer.Disposed.Should().BeEmpty(
                "the disposer is only ever reached after a swap has committed — a throw disposes nothing");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The same restore branch reached by the other real way the second move dies: a produced file that
    /// never materialised. Here the end state discriminates on its own — without the restore, the original
    /// would be sitting at the <c>.vsj-original</c> path with NOTHING at the path the user knows.
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void AProducedFileThatIsNotThere_CannotCostTheUserTheirOriginal()
    {
        var dir = NewDir();
        try
        {
            var original = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(original, OriginalContent);
            var neverProduced = original + ".vsj-exact.mp4";
            var originalIdentity = FileIdentity(original);
            var disposer = new RecordingDisposer();

            Action act = () => new OriginalReplacer(disposer).Replace(neverProduced, original);
            act.Should().Throw<FileNotFoundException>(
                "a swap with nothing to swap in must surface, not quietly consume the original");

            File.ReadAllText(original).Should().Be(
                OriginalContent, "the original is back at its own path, byte-identical");
            FileIdentity(original).Should().Be(originalIdentity, "and it is the same file on disk");
            File.Exists(Backup(original)).Should().BeFalse(
                "the move-aside was undone — no half-swapped state is left behind");
            disposer.Disposed.Should().BeEmpty("nothing was replaced, so nothing may be disposed");
        }
        finally { Cleanup(dir); }
    }

    // ---- Performance: O(1) file operations, whatever the payload weighs ------------------------

    /// <summary>
    /// Structural, not wall-clock: a video master is gigabytes, so the swap must be a constant number of
    /// directory operations — renames — and never a byte copy. The NTFS file index proves it: a rename
    /// keeps a file's identity, a copy creates a new one. The control at the end copies a file for real,
    /// so the discriminator is demonstrated rather than assumed.
    /// </summary>
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void TheSwap_MovesThePayload_AndNeverCopiesIt()
    {
        const int TwoMegabytes = 2 * 1024 * 1024;
        var dir = NewDir();
        try
        {
            var original = Path.Combine(dir, "clip.mp4");
            var produced = original + ".vsj-exact.mp4";
            File.WriteAllBytes(original, Payload(TwoMegabytes, 0x11));
            File.WriteAllBytes(produced, Payload(TwoMegabytes, 0x22));

            var producedIdentity = FileIdentity(produced);
            var originalIdentity = FileIdentity(original);
            var producedWrittenAt = File.GetLastWriteTimeUtc(produced);
            var disposer = new RecordingDisposer();

            new OriginalReplacer(disposer).Replace(produced, original);

            FileIdentity(original).Should().Be(
                producedIdentity,
                "the produced file was RENAMED onto the original's path — an O(1) directory operation; a "
                + "copy would have created a new file record and cost O(payload)");
            FileIdentity(Backup(original)).Should().Be(
                originalIdentity, "and the original was renamed aside, never duplicated");
            File.GetLastWriteTimeUtc(original).Should().Be(
                producedWrittenAt, "the bytes were re-pointed, not re-authored");
            disposer.Disposed.Should().ContainSingle(
                "one swap, one backup — the operation count is fixed, not a function of the payload");

            var namesAfterSwap = Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).ToArray();
            namesAfterSwap.Should().BeEquivalentTo(
                new[] { "clip.mp4", "clip.mp4" + OriginalReplacer.BackupSuffix },
                "exactly the two paths the swap names — the 2 MB payload exists once on disk, never twice");
            new FileInfo(original).Length.Should().Be(TwoMegabytes, "the whole payload arrived intact");

            // Control: copying the very same bytes in the very same folder yields a DIFFERENT identity,
            // so the assertions above genuinely discriminate a move from a copy.
            var control = Path.Combine(dir, "control.mp4");
            File.Copy(original, control);
            FileIdentity(control).Should().NotBe(
                producedIdentity, "a copy is a new file on disk — precisely what the swap must never do");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public void TheSwap_TouchesNothingElseInTheFolder()
    {
        var dir = NewDir();
        try
        {
            var (original, produced) = Fixture(dir);
            var neighbour = Path.Combine(dir, "someone-elses-clip.mp4");
            File.WriteAllText(neighbour, "NOT-PART-OF-THIS-ROW");
            var neighbourIdentity = FileIdentity(neighbour);
            var neighbourWrittenAt = File.GetLastWriteTimeUtc(neighbour);

            new OriginalReplacer(new RecordingDisposer()).Replace(produced, original);

            File.ReadAllText(neighbour).Should().Be(
                "NOT-PART-OF-THIS-ROW", "the swap works on the two paths it is given and no others");
            FileIdentity(neighbour).Should().Be(neighbourIdentity, "the neighbour was never re-created");
            File.GetLastWriteTimeUtc(neighbour).Should().Be(neighbourWrittenAt, "nor rewritten");

            var names = Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).ToArray();
            names.Should().BeEquivalentTo(
                new[] { "clip.mp4", "clip.mp4" + OriginalReplacer.BackupSuffix, "someone-elses-clip.mp4" },
                "no scratch file, no second copy, no collateral — a bounded, three-path footprint");
        }
        finally { Cleanup(dir); }
    }

    // ---- NTFS file identity: the move-vs-copy discriminator ------------------------------------

    /// <summary>
    /// A file's identity on disk — the NTFS (volume serial, file index) pair. It survives a RENAME and
    /// changes on a COPY, which makes it the only managed-level way to tell the two apart here: Win32
    /// <c>CopyFile</c> faithfully reproduces size, attributes, timestamps and alternate data streams, so
    /// none of those discriminate. Opened share-everything so a locked or read-only file can still be
    /// identified.
    /// </summary>
    private static string FileIdentity(string path)
    {
        using var handle = File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new IOException(
                $"GetFileInformationByHandle failed for '{path}' (win32 {Marshal.GetLastWin32Error()}).");
        }

        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return $"{info.VolumeSerialNumber:X8}:{index:X}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle, out ByHandleFileInformation information);

#pragma warning disable CS0649 // Interop layout: every field is written by the OS, never from C#.
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
#pragma warning restore CS0649
}
