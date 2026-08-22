using FluentAssertions;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// todo-automate gap coverage for <see cref="SplitEngine"/> (SPEC-001): out-of-range segment
/// selection, request-shape validation, probe-failure mapping, and the "fewer parts than planned"
/// move-time guard. All binary-free — reuses the shared <see cref="FakeProbe"/> /
/// <see cref="RecordingFakeRunner"/> / <see cref="NoopFakeRunner"/> from <see cref="SplitEngineUnitTests"/>.
/// </summary>
public class SplitEngineSpecGapTests
{
    private static readonly IReadOnlyList<TimeSpan> Keyframes =
        Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList();

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-splitgap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private static string NewInput(string dir)
    {
        var input = Path.Combine(dir, "clip.mp4");
        File.WriteAllText(input, "placeholder");
        return input;
    }

    // SPEC-001#I27 — a non-null selection none of whose indices fall in-range → the distinct
    // "None of the selected segment indices" SplitException (vs I26's empty-selection message).
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SplitAsync_AllIndicesOutOfRange_Rejected()
    {
        var dir = NewDir();
        try
        {
            var input = NewInput(dir);
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);

            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(runner, new FakeProbe(TimeSpan.FromSeconds(10), Keyframes));
            // Cuts at 3 & 6 → 3 parts (1..3). {99} is non-empty but wholly out of range → clamps to nothing.
            var req = new SplitRequest(
                input,
                new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) },
                outDir,
                SelectedSegmentIndices: new[] { 99 });

            Func<Task> act = () => engine.SplitAsync(req);
            await act.Should().ThrowAsync<SplitException>().WithMessage("*None of the selected segment indices*");
            runner.Commands.Should().BeEmpty("no ffmpeg runs when the selection clamps to nothing");
        }
        finally { Cleanup(dir); }
    }

    // SPEC-001#I32 — ValidateRequestShape rejects an empty InputPath before probing.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SplitAsync_EmptyInputPath_Rejected_NoProbeNoRun()
    {
        var runner = new RecordingFakeRunner();
        var engine = new SplitEngine(runner, new RejectingMediaProbe());
        var req = new SplitRequest("", new[] { TimeSpan.FromSeconds(5) }, @"C:\out");

        Func<Task> act = () => engine.SplitAsync(req);
        await act.Should().ThrowAsync<SplitException>().WithMessage("*Input path is empty*");
        runner.Commands.Should().BeEmpty();
    }

    // SPEC-001#I32 — a missing input file is rejected before probing.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SplitAsync_MissingInputFile_Rejected_NoProbeNoRun()
    {
        var dir = NewDir();
        try
        {
            var missing = Path.Combine(dir, "does-not-exist.mp4");
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);

            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(runner, new RejectingMediaProbe());
            var req = new SplitRequest(missing, new[] { TimeSpan.FromSeconds(5) }, outDir);

            Func<Task> act = () => engine.SplitAsync(req);
            await act.Should().ThrowAsync<SplitException>().WithMessage("*does not exist*");
            runner.Commands.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    // SPEC-001#I32 — an empty OutputDir is rejected before probing.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SplitAsync_EmptyOutputDir_Rejected_NoProbeNoRun()
    {
        var dir = NewDir();
        try
        {
            var input = NewInput(dir);
            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(runner, new RejectingMediaProbe());
            var req = new SplitRequest(input, new[] { TimeSpan.FromSeconds(5) }, string.Empty);

            Func<Task> act = () => engine.SplitAsync(req);
            await act.Should().ThrowAsync<SplitException>().WithMessage("*Output directory is empty*");
            runner.Commands.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    // SPEC-001#I32 — an empty CutPoints list is rejected before probing.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SplitAsync_EmptyCutPoints_Rejected_NoProbeNoRun()
    {
        var dir = NewDir();
        try
        {
            var input = NewInput(dir);
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);

            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(runner, new RejectingMediaProbe());
            var req = new SplitRequest(input, Array.Empty<TimeSpan>(), outDir);

            Func<Task> act = () => engine.SplitAsync(req);
            await act.Should().ThrowAsync<SplitException>().WithMessage("*No cut points supplied*");
            runner.Commands.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    // SPEC-001#I32 — an unwritable OutputDir (the write-probe fails) is rejected before probing.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SplitAsync_UnwritableOutputDir_Rejected_NoProbeNoRun()
    {
        var dir = NewDir();
        try
        {
            var input = NewInput(dir);
            // A FILE named "blocker"; using it as a parent directory component makes CreateDirectory
            // (inside the write-probe) throw IOException → the "is not writable" rejection.
            var blocker = Path.Combine(dir, "blocker");
            File.WriteAllText(blocker, "not a directory");
            var outDir = Path.Combine(blocker, "sub");

            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(runner, new RejectingMediaProbe());
            var req = new SplitRequest(input, new[] { TimeSpan.FromSeconds(5) }, outDir);

            Func<Task> act = () => engine.SplitAsync(req);
            await act.Should().ThrowAsync<SplitException>().WithMessage("*not writable*");
            runner.Commands.Should().BeEmpty();
        }
        finally { Cleanup(dir); }
    }

    // SPEC-001#I33 — a failed probe surfaces as SplitException "Cannot split '<input>': <reason>".
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SplitAsync_ProbeFails_ThrowsCannotSplit_WithReason()
    {
        var dir = NewDir();
        try
        {
            var input = NewInput(dir);
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);

            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(runner, new FailingMediaProbe("not a media file"));
            var req = new SplitRequest(input, new[] { TimeSpan.FromSeconds(5) }, outDir);

            Func<Task> act = () => engine.SplitAsync(req);
            (await act.Should().ThrowAsync<SplitException>()).Which.Message
                .Should().Contain("Cannot split").And.Contain("not a media file");
            runner.Commands.Should().BeEmpty("a probe failure aborts before ffmpeg");
        }
        finally { Cleanup(dir); }
    }

    // SPEC-001#I35 — ffmpeg "succeeding" without writing the planned temp parts → SplitException at
    // move time ("was not produced by ffmpeg (got fewer segments than planned)").
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SplitAsync_FewerPartsThanPlanned_Throws()
    {
        var dir = NewDir();
        try
        {
            var input = NewInput(dir);
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);

            // NoopFakeRunner returns exit 0 but writes NO temp parts → the move step finds them missing.
            var engine = new SplitEngine(new NoopFakeRunner(), new FakeProbe(TimeSpan.FromSeconds(10), Keyframes));
            var req = new SplitRequest(input, new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) }, outDir);

            Func<Task> act = () => engine.SplitAsync(req);
            await act.Should().ThrowAsync<SplitException>().WithMessage("*was not produced by ffmpeg*");
        }
        finally { Cleanup(dir); }
    }
}

/// <summary>Probe that always reports a typed failure (never a success) — drives the probe-failure branch.</summary>
internal sealed class FailingMediaProbe : IMediaProbe
{
    private readonly string _reason;

    public FailingMediaProbe(string reason) => _reason = reason;

    public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(ProbeResult.Failure(_reason));

    public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException("keyframes must not be scanned after a probe failure");

    public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
        throw new NotSupportedException();

    public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => throw new NotSupportedException();
}

/// <summary>Probe that throws on EVERY call — proves request-shape validation runs before any probing.</summary>
internal sealed class RejectingMediaProbe : IMediaProbe
{
    public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException("ProbeAsync must not be reached — validation should reject first");

    public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
        throw new NotSupportedException();

    public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => throw new NotSupportedException();
}
