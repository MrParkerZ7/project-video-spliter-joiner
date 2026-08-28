using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Io;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-122 (epic G-041) — writing OVER the user's original is the one genuinely destructive thing this app
/// does, so the load-bearing property is not the happy path but the failure paths: <b>every</b> way a run
/// can go wrong must leave the original byte-identical. The engine therefore verifies every produced part
/// BEFORE touching a destination, and replaces in place via a backup so the bytes never exist nowhere.
/// </summary>
public sealed class ReplaceOriginalSafetyTests
{
    private static readonly IReadOnlyList<TimeSpan> Keyframes =
        Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList();

    private const string OriginalContent = "THE-USER-MASTER-FILE";

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Records every backup handed to it (and keeps the file, so tests can assert recoverability).</summary>
    private sealed class RecordingDisposer : IOriginalDisposer
    {
        public List<string> Disposed { get; } = new();

        public void DisposeOriginalBackup(string backupPath) => Disposed.Add(backupPath);
    }

    /// <summary>
    /// Records the state of the world AT THE MOMENT the backup is handed over, so a test can pin the
    /// ordering half of SPEC-002 I44: the disposer is reached only AFTER a verified output has already
    /// taken the original's place.
    /// </summary>
    private sealed class InspectingDisposer : IOriginalDisposer
    {
        private readonly string _originalPath;

        public InspectingDisposer(string originalPath) => _originalPath = originalPath;

        public int Calls { get; private set; }

        public string? OriginalContentAtCallTime { get; private set; }

        public bool BackupExistedAtCallTime { get; private set; }

        public string? BackupContentAtCallTime { get; private set; }

        public void DisposeOriginalBackup(string backupPath)
        {
            Calls++;
            OriginalContentAtCallTime = File.Exists(_originalPath) ? File.ReadAllText(_originalPath) : null;
            BackupExistedAtCallTime = File.Exists(backupPath);
            BackupContentAtCallTime = BackupExistedAtCallTime ? File.ReadAllText(backupPath) : null;
        }
    }

    /// <summary>
    /// Materialises the planned part like <c>RecordingFakeRunner</c> does, then HOLDS IT OPEN with
    /// <see cref="FileShare.None"/>. <see cref="File.Replace(string,string,string,bool)"/> on such a
    /// replacement fails with a sharing violation exactly the way an exFAT / SMB volume fails it, which
    /// drives the engine into its SPEC-002 I43 rename-aside fallback; the same lock then fails the
    /// fallback's second move, forcing the restore-the-original branch. Dispose releases the handles so
    /// the temp dir can be swept.
    /// </summary>
    private sealed class LockingFakeRunner : IFfmpegRunner, IDisposable
    {
        private readonly List<FileStream> _held = new();

        public List<IReadOnlyList<string>> Commands { get; } = new();

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            var tokens = args.ToList().ToList();
            Commands.Add(tokens);

            // A strict subset selection always takes the per-segment path, whose output token is a
            // concrete file (never the muxer's part%03d pattern).
            var output = tokens[^1];
            File.WriteAllText(output, "seg");
            _held.Add(new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.None));

            return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
        }

        public void Dispose()
        {
            foreach (var stream in _held)
            {
                try { stream.Dispose(); } catch { /* best-effort */ }
            }

            _held.Clear();
        }
    }

    /// <summary>
    /// A request shaped exactly as <c>KeptMiddleRequestBuilder</c> builds one for
    /// <see cref="VideoSplitJoiner.Core.Bulk.OutputMode.ReplaceOriginal"/>: OutputDir = the effective
    /// path's folder and NamingPattern = its literal file name, so the single selected segment lands
    /// ON the original.
    /// </summary>
    private static SplitRequest ReplaceRequest(string input) =>
        new(input,
            new[] { TimeSpan.FromSeconds(3) },
            Path.GetDirectoryName(input)!,
            NamingPattern: Path.GetFileName(input),
            Overwrite: true,
            SelectedSegmentIndices: new[] { 1 });

    // ---- Happy path: replaced, and recoverable -------------------------------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ReplacingTheOriginal_PutsTheNewBytesInPlace_AndOffersTheBackupForDisposal()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, OriginalContent);

            var disposer = new RecordingDisposer();
            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(
                runner, new FakeProbe(TimeSpan.FromSeconds(10), Keyframes), new ErrorLogWriter(),
                new FakeDiskSpaceProbe(long.MaxValue), disposer);

            // The fake runner materialises the planned part; its destination IS the source path here.
            await engine.SplitAsync(ReplaceRequest(input));

            File.Exists(input).Should().BeTrue("the original path now holds the trimmed result");
            File.ReadAllText(input).Should().NotBe(OriginalContent, "it was genuinely replaced");
            disposer.Disposed.Should().ContainSingle("exactly one backup was produced and handed over")
                .Which.Should().EndWith(".vsj-original");
        }
        finally { Cleanup(dir); }
    }

    // ---- The load-bearing failure paths --------------------------------------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task WhenFfmpegProducesNothing_TheOriginalIsUntouched_AndNoBackupIsDisposed()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, OriginalContent);

            var disposer = new RecordingDisposer();
            // NoopFakeRunner exits 0 but writes NO temp parts -> the verify step must fire first.
            var engine = new SplitEngine(
                new NoopFakeRunner(), new FakeProbe(TimeSpan.FromSeconds(10), Keyframes), new ErrorLogWriter(),
                new FakeDiskSpaceProbe(long.MaxValue), disposer);

            Func<Task> act = () => engine.SplitAsync(ReplaceRequest(input));
            await act.Should().ThrowAsync<SplitException>().WithMessage("*was not produced by ffmpeg*");

            File.ReadAllText(input).Should().Be(
                OriginalContent, "a run that produced nothing must leave the user's master byte-identical");
            disposer.Disposed.Should().BeEmpty("no destructive step may run when the output was never produced");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task WhenTheDiskPreflightBlocks_TheOriginalIsUntouched_AndFfmpegNeverRuns()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, OriginalContent);

            var disposer = new RecordingDisposer();
            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(
                runner, new FakeProbe(TimeSpan.FromSeconds(10), Keyframes), new ErrorLogWriter(),
                new FakeDiskSpaceProbe(1000), disposer); // shortfall

            Func<Task> act = () => engine.SplitAsync(ReplaceRequest(input));
            await act.Should().ThrowAsync<SplitException>().WithMessage("*Not enough space*");

            File.ReadAllText(input).Should().Be(OriginalContent, "a blocked run never touches the master");
            runner.Commands.Should().BeEmpty("no ffmpeg run on a blocked pre-flight");
            disposer.Disposed.Should().BeEmpty("no destructive step on a blocked pre-flight");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task AnInvalidRequest_IsRejectedBeforeAnythingIsTouched()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, OriginalContent);

            var disposer = new RecordingDisposer();
            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(
                runner, new FakeProbe(TimeSpan.FromSeconds(10), Keyframes), new ErrorLogWriter(),
                new FakeDiskSpaceProbe(long.MaxValue), disposer);

            // No cut points at all -> rejected by request-shape validation.
            var bad = new SplitRequest(input, Array.Empty<TimeSpan>(), dir, Overwrite: true);

            Func<Task> act = () => engine.SplitAsync(bad);
            await act.Should().ThrowAsync<SplitException>();

            File.ReadAllText(input).Should().Be(OriginalContent);
            runner.Commands.Should().BeEmpty();
            disposer.Disposed.Should().BeEmpty("a rejected request performs ZERO destructive calls");
        }
        finally { Cleanup(dir); }
    }

    // ---- The replace mechanics: atomic-with-a-backup (I42), the fallback (I43) -----------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task AStaleBackupFromAnInterruptedRun_IsReplaced_NotTrippedOver()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            var backup = input + ".vsj-original";
            File.WriteAllText(input, OriginalContent);
            File.WriteAllText(backup, "stale-from-a-crashed-run");

            var disposer = new RecordingDisposer();
            var engine = new SplitEngine(
                new RecordingFakeRunner(), new FakeProbe(TimeSpan.FromSeconds(10), Keyframes), new ErrorLogWriter(),
                new FakeDiskSpaceProbe(long.MaxValue), disposer);

            Func<Task> act = () => engine.SplitAsync(ReplaceRequest(input));
            await act.Should().NotThrowAsync(
                "a leftover .vsj-original from an earlier interrupted run is swept, never a reason to fail");

            File.ReadAllText(input).Should().NotBe(OriginalContent, "the trimmed output took the original's place");
            File.Exists(backup).Should().BeTrue(
                "File.Replace writes the pre-replacement original to the backup path — the bytes are never nowhere");
            File.ReadAllText(backup).Should().Be(
                OriginalContent,
                "the backup holds THIS run's original, not the stale bytes of the interrupted one");
            disposer.Disposed.Should().ContainSingle("one replace produced exactly one backup")
                .Which.Should().EndWith(".vsj-original");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task AFailedFallbackStillLeavesTheUsersFileInPlace()
    {
        var dir = NewDir();
        var runner = new LockingFakeRunner();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            var backup = input + ".vsj-original";
            File.WriteAllText(input, OriginalContent);

            var disposer = new RecordingDisposer();
            var engine = new SplitEngine(
                runner, new FakeProbe(TimeSpan.FromSeconds(10), Keyframes), new ErrorLogWriter(),
                new FakeDiskSpaceProbe(long.MaxValue), disposer);

            // File.Replace hits a sharing violation (the exFAT/SMB shape) -> rename-aside fallback ->
            // whose move-into-place ALSO fails on the same lock -> the original must be put back.
            Func<Task> act = () => engine.SplitAsync(ReplaceRequest(input));
            await act.Should().ThrowAsync<IOException>(
                "a replace that cannot complete must surface, never half-write");

            runner.Commands.Should().ContainSingle(
                "precondition: ffmpeg DID produce the part, so the failure is the replace step itself");
            File.Exists(input).Should().BeTrue("the user's file is present after a failed fallback");
            File.ReadAllText(input).Should().Be(
                OriginalContent, "the rename-aside backup is moved BACK over the original, byte-identical");
            File.Exists(backup).Should().BeFalse("the restore leaves no stray .vsj-original behind");
            disposer.Disposed.Should().BeEmpty(
                "the disposer is only ever reached after a verified output took the original's place");
        }
        finally
        {
            runner.Dispose();
            Cleanup(dir);
        }
    }

    // ---- The backup's fate is the injected disposer's decision (I44) ---------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task TheCoreDefault_KeepsTheBackup()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            var backup = input + ".vsj-original";
            File.WriteAllText(input, OriginalContent);

            // No disposer argument at all — Core's default must be the safest possible behaviour.
            var engine = new SplitEngine(
                new RecordingFakeRunner(), new FakeProbe(TimeSpan.FromSeconds(10), Keyframes));

            await engine.SplitAsync(ReplaceRequest(input));

            File.ReadAllText(input).Should().NotBe(OriginalContent, "the original was genuinely replaced");
            File.Exists(backup).Should().BeTrue(
                "Core defaults to KeepOriginalBackupDisposer — nothing is ever destroyed headlessly");
            File.ReadAllText(backup).Should().Be(
                OriginalContent, "the kept backup is the user's original, byte-identical and recoverable");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task TheDeleteDisposer_RemovesTheBackup_AfterTheOutputIsInPlace()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            var backup = input + ".vsj-original";
            File.WriteAllText(input, OriginalContent);

            var engine = new SplitEngine(
                new RecordingFakeRunner(), new FakeProbe(TimeSpan.FromSeconds(10), Keyframes),
                new DeleteOriginalBackupDisposer());

            await engine.SplitAsync(ReplaceRequest(input));

            File.Exists(input).Should().BeTrue("the trimmed output is in place whatever the backup's fate");
            File.ReadAllText(input).Should().NotBe(OriginalContent);
            File.Exists(backup).Should().BeFalse(
                "DeleteOriginalBackupDisposer removes the backup — the opted-in no-undo behaviour");
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task TheBackupIsHandedOver_OnlyAfterTheOutputHasTakenTheOriginalsPlace()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, OriginalContent);

            var disposer = new InspectingDisposer(input);
            var engine = new SplitEngine(
                new RecordingFakeRunner(), new FakeProbe(TimeSpan.FromSeconds(10), Keyframes), new ErrorLogWriter(),
                new FakeDiskSpaceProbe(long.MaxValue), disposer);

            await engine.SplitAsync(ReplaceRequest(input));

            disposer.Calls.Should().Be(1, "one replaced original offers exactly one backup for disposal");
            disposer.OriginalContentAtCallTime.Should().NotBeNull(
                "the original's path already holds a file when the disposer is called");
            disposer.OriginalContentAtCallTime.Should().NotBe(
                OriginalContent, "the verified output had ALREADY taken the original's place");
            disposer.BackupExistedAtCallTime.Should().BeTrue("the backup exists when its fate is decided");
            disposer.BackupContentAtCallTime.Should().Be(
                OriginalContent, "the backup handed over is the user's original, byte-identical");
        }
        finally { Cleanup(dir); }
    }
}
