using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.Core.Errors;
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
}
