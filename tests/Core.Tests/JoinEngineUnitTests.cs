using FluentAssertions;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Binary-free JoinEngine tests: an empty input list is rejected before any ffmpeg runs, and a
/// probe-failure on an input surfaces as a refusal (no crash, no output). Uses throwing stubs so
/// that a spurious ffmpeg launch would fail the test loudly.
/// </summary>
public class JoinEngineUnitTests
{
    private sealed class ThrowingRunner : IFfmpegRunner
    {
        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("ffmpeg must NOT run for a refused join.");
    }

    private sealed class StubProbe : IMediaProbe
    {
        private readonly Func<string, ProbeResult> _probe;

        public StubProbe(Func<string, ProbeResult> probe) => _probe = probe;

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(_probe(path));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
            throw new NotSupportedException();

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => throw new NotSupportedException();
    }

    [Fact]
    public async Task JoinAsync_EmptyInputList_RefusesWithoutRunningFfmpeg()
    {
        var engine = new JoinEngine(
            new ThrowingRunner(),
            new StubProbe(_ => throw new InvalidOperationException("probe must not run for empty input")));

        var result = await engine.JoinAsync(new JoinRequest(Array.Empty<string>(), @"C:\out\joined.mp4"));

        result.Success.Should().BeFalse();
        result.OutputPath.Should().BeNull();
        result.Refusal.Should().NotBeNull();
        result.Refusal!.Mismatches.Should().Contain(m => m.Field == "input_count");
    }

    [Fact]
    public async Task JoinAsync_ProbeFailureOnInput_RefusesNoOutput()
    {
        var engine = new JoinEngine(
            new ThrowingRunner(),
            new StubProbe(_ => ProbeResult.Failure("not a media file")));

        var result = await engine.JoinAsync(new JoinRequest(
            new[] { @"C:\a.mp4", @"C:\b.mp4" }, @"C:\out\joined.mp4"));

        result.Success.Should().BeFalse();
        result.Refusal.Should().NotBeNull();
        result.Refusal!.Mismatches.Should().Contain(m => m.Field == "probe");
    }

    [Fact]
    public async Task CheckCompatibilityAsync_EmptyList_ReportsInputCount()
    {
        var engine = new JoinEngine(
            new ThrowingRunner(),
            new StubProbe(_ => ProbeResult.Failure("unused")));

        var report = await engine.CheckCompatibilityAsync(Array.Empty<string>());

        report.Compatible.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Field == "input_count");
    }

    private sealed class FailingRunner : IFfmpegRunner
    {
        private readonly int _exitCode;
        private readonly IReadOnlyList<string> _stderr;

        public FailingRunner(int exitCode, string stderr)
        {
            _exitCode = exitCode;
            _stderr = stderr.Split('\n').Select(s => s.TrimEnd('\r')).ToList().AsReadOnly();
        }

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(new FfmpegResult(_exitCode, _stderr));
    }

    private static MediaInfo CompatibleClip() =>
        new(TimeSpan.FromSeconds(5), "mp4",
            new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
            new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

    [Fact]
    public async Task JoinAsync_FfmpegFailure_WritesFullLog_AndThreadsPathAndFullText_OnRefusal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-joinfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.mp4");
        var b = Path.Combine(dir, "b.mp4");
        await File.WriteAllTextAsync(a, "placeholder");
        await File.WriteAllTextAsync(b, "placeholder");
        var outPath = Path.Combine(dir, "joined.mp4");
        var logDir = Path.Combine(dir, "logs");

        try
        {
            var fullStdErr = string.Join(Environment.NewLine,
                Enumerable.Range(0, 300).Select(i => $"concat stderr line {i}"))
                + Environment.NewLine + "Impossible to open list.txt";

            // Compatible inputs → the engine reaches the ffmpeg run, which we fail.
            var engine = new JoinEngine(
                new FailingRunner(exitCode: 1, stderr: fullStdErr),
                new StubProbe(_ => ProbeResult.Success(CompatibleClip())),
                new ErrorLogWriter(logDir));

            var result = await engine.JoinAsync(new JoinRequest(new[] { a, b }, outPath));

            result.Success.Should().BeFalse();
            File.Exists(outPath).Should().BeFalse("a failed join leaves no output");

            result.FullStdErr.Should().NotBeNullOrEmpty();
            result.FullStdErr!.Should().Contain("concat stderr line 0")
                .And.Contain("concat stderr line 299")
                .And.Contain("Impossible to open list.txt");
            result.LogFilePath.Should().NotBeNull();
            File.Exists(result.LogFilePath!).Should().BeTrue();

            var logContent = await File.ReadAllTextAsync(result.LogFilePath!);
            logContent.Should().Contain("concat stderr line 299", "the FULL stderr is persisted, not just the tail");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ---- todo-automate gap coverage (SPEC-003) ----

    /// <summary>Runner that records the built command, cancels the token, and throws — models a mid-run cancel.</summary>
    private sealed class CancellingJoinRunner : IFfmpegRunner
    {
        private readonly CancellationTokenSource _cts;

        public CancellingJoinRunner(CancellationTokenSource cts) => _cts = cts;

        public List<string> LastTokens { get; } = new();

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            LastTokens.Clear();
            LastTokens.AddRange(args.ToList());
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(ct);
        }
    }

    /// <summary>Runner that writes the output (last token), reports an intermediate progress sample, and succeeds.</summary>
    private sealed class WritingProgressJoinRunner : IFfmpegRunner
    {
        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var output = args.ToList()[^1];
            var d = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(d))
            {
                Directory.CreateDirectory(d);
            }

            File.WriteAllText(output, "joined-bytes");
            progress?.Report(0.5);
            return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
        }
    }

    // SPEC-003#I4 — an empty/whitespace OutputPath is refused with field "output" before any ffmpeg.
    [Trait("serves-spec", "SPEC-003")]
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task JoinAsync_EmptyOutputPath_Refused_FieldOutput_NoFfmpeg(string outputPath)
    {
        var engine = new JoinEngine(
            new ThrowingRunner(),
            new StubProbe(_ => throw new InvalidOperationException("probe must not run for an empty output")));

        var result = await engine.JoinAsync(new JoinRequest(new[] { @"C:\a.mp4", @"C:\b.mp4" }, outputPath));

        result.Success.Should().BeFalse();
        result.OutputPath.Should().BeNull();
        result.Refusal!.Mismatches.Should().Contain(m => m.Field == "output");
    }

    // SPEC-003#I18 — Overwrite=false + an existing output → refusal field "output_exists" before ffmpeg.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task JoinAsync_OutputExists_OverwriteFalse_Refused_NoFfmpeg()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-joinexists-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.mp4");
            var b = Path.Combine(dir, "b.mp4");
            await File.WriteAllTextAsync(a, "x");
            await File.WriteAllTextAsync(b, "x");
            var outPath = Path.Combine(dir, "joined.mp4");
            await File.WriteAllTextAsync(outPath, "existing");

            // ThrowingRunner fails loudly if ffmpeg is ever launched.
            var engine = new JoinEngine(new ThrowingRunner(), new StubProbe(_ => ProbeResult.Success(CompatibleClip())));

            var result = await engine.JoinAsync(new JoinRequest(new[] { a, b }, outPath, Overwrite: false));

            result.Success.Should().BeFalse();
            result.Refusal!.Mismatches.Should().Contain(m => m.Field == "output_exists");
            File.ReadAllText(outPath).Should().Be("existing", "the existing output must be untouched");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // SPEC-003#I19 — Overwrite=true replaces an existing output.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task JoinAsync_OutputExists_OverwriteTrue_Replaces()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-joinreplace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.mp4");
            var b = Path.Combine(dir, "b.mp4");
            await File.WriteAllTextAsync(a, "x");
            await File.WriteAllTextAsync(b, "x");
            var outPath = Path.Combine(dir, "joined.mp4");
            await File.WriteAllTextAsync(outPath, "old-output");

            // WritingFakeRunner writes the temp output (last token) + succeeds → the engine moves it into place.
            var engine = new JoinEngine(new WritingFakeRunner(), new StubProbe(_ => ProbeResult.Success(CompatibleClip())));

            var result = await engine.JoinAsync(new JoinRequest(new[] { a, b }, outPath, Overwrite: true));

            result.Success.Should().BeTrue();
            result.OutputPath.Should().Be(Path.GetFullPath(outPath));
            File.Exists(outPath).Should().BeTrue();
            File.ReadAllText(outPath).Should().NotBe("old-output", "the existing output was replaced");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // SPEC-003#I27 — cancellation deletes the partial temp output + rethrows; the temp concat list file
    // is always cleaned up in the finally block.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task JoinAsync_Cancelled_DeletesTempOutput_CleansListFile_Rethrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-joincancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.mp4");
            var b = Path.Combine(dir, "b.mp4");
            await File.WriteAllTextAsync(a, "x");
            await File.WriteAllTextAsync(b, "x");
            var outPath = Path.Combine(dir, "joined.mp4");

            var cts = new CancellationTokenSource();
            var runner = new CancellingJoinRunner(cts);
            var engine = new JoinEngine(runner, new StubProbe(_ => ProbeResult.Success(CompatibleClip())));

            Func<Task> act = () => engine.JoinAsync(new JoinRequest(new[] { a, b }, outPath), progress: null, ct: cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();

            File.Exists(Path.GetFullPath(outPath)).Should().BeFalse("a cancelled join leaves no output");

            // The runner captured the built command: the concat list file (-i arg) + the temp output
            // (last token) must both be cleaned up.
            var tokens = runner.LastTokens;
            tokens.Should().NotBeEmpty();
            var listFile = tokens[tokens.IndexOf("-i") + 1];
            var tempOut = tokens[^1];
            File.Exists(listFile).Should().BeFalse("the temp concat list file is always cleaned up in finally");
            File.Exists(tempOut).Should().BeFalse("the partial temp output is deleted on cancel");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // SPEC-003#I30 — the "Joining" stage detail is "1 clip" for a single input.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task JoinAsync_SingleInput_JoiningDetail_IsOneClip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-joinone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.mp4");
            await File.WriteAllTextAsync(a, "x");
            var outPath = Path.Combine(dir, "joined.mp4");

            var status = new RecordingProgress<OperationStatus>();
            var engine = new JoinEngine(new WritingFakeRunner(), new StubProbe(_ => ProbeResult.Success(CompatibleClip())));

            var result = await engine.JoinAsync(new JoinRequest(new[] { a }, outPath), progress: null, ct: default, status: status);

            result.Success.Should().BeTrue();
            status.Reports.Should().Contain(s => s.Stage == "Joining" && s.Detail == "1 clip");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // SPEC-003#I31 — numeric progress reports within 0..1 and reaches 1.0 on success.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task JoinAsync_Success_Progress_ReachesOne_WithinRange()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-joinprog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.mp4");
            var b = Path.Combine(dir, "b.mp4");
            await File.WriteAllTextAsync(a, "x");
            await File.WriteAllTextAsync(b, "x");
            var outPath = Path.Combine(dir, "joined.mp4");

            var progress = new RecordingProgress<double>();
            var engine = new JoinEngine(new WritingProgressJoinRunner(), new StubProbe(_ => ProbeResult.Success(CompatibleClip())));

            var result = await engine.JoinAsync(new JoinRequest(new[] { a, b }, outPath), progress);

            result.Success.Should().BeTrue();
            progress.Reports.Should().NotBeEmpty();
            progress.Reports.Should().OnlyContain(v => v >= 0.0 && v <= 1.0);
            progress.Reports[^1].Should().Be(1.0, "progress reaches 1.0 on success");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // SPEC-003#I1/I20/I16 — a compatible MULTI-clip join is ONE concat-demuxer stream-copy pass
    // (O(N)->1), never N-1 pairwise joins: the runner is launched EXACTLY once with a single
    // command carrying both "concat" and "copy".
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task JoinAsync_MultiClipCompatible_LaunchesFfmpegExactlyOnce_SingleConcatPass()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-joinbatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.mp4");
            var b = Path.Combine(dir, "b.mp4");
            var c = Path.Combine(dir, "c.mp4");
            await File.WriteAllTextAsync(a, "x");
            await File.WriteAllTextAsync(b, "x");
            await File.WriteAllTextAsync(c, "x");
            var outPath = Path.Combine(dir, "joined.mp4");

            // WritingFakeRunner writes the temp output (last token) + succeeds, and records
            // CallCount + every command's token list.
            var runner = new WritingFakeRunner();
            var engine = new JoinEngine(runner, new StubProbe(_ => ProbeResult.Success(CompatibleClip())));

            var result = await engine.JoinAsync(new JoinRequest(new[] { a, b, c }, outPath));

            // CORRECTNESS: three concat-compatible clips join successfully into the output.
            result.Success.Should().BeTrue();
            result.OutputPath.Should().Be(Path.GetFullPath(outPath));

            // PERF (batch, O(N)->1 pass): ONE ffmpeg launch — a single concat-demuxer stream-copy
            // command — NOT N-1 pairwise joins.
            runner.CallCount.Should().Be(1, "a compatible multi-clip join is a single concat pass, not N-1 pairwise joins");
            runner.Commands.Should().ContainSingle();
            runner.Commands[0].Should().Contain("concat").And.Contain("copy");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // SPEC-003#I2/I6-I15 — two clips that BOTH probe successfully but differ in resolution are
    // refused via the FULL probe-loop + CompatChecker path (distinct from the early input/output/
    // probe-fail/output-exists guards): the refusal names the mismatched field and ffmpeg never runs.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task JoinAsync_StreamIncompatibleSet_Refuses_WithoutLaunchingFfmpeg()
    {
        var a = @"C:\clip-a.mp4";
        var b = @"C:\clip-b.mp4";

        // Reference clip (1920x1080) vs a second clip that probes fine but at a different resolution
        // (1280x720) — built the CompatibleClip() way, only the video dimensions differ.
        var reference = CompatibleClip();
        var mismatched = new MediaInfo(
            TimeSpan.FromSeconds(5), "mp4",
            new[] { new StreamInfo(0, "h264", "video", 1280, 720, "yuv420p", null, null, "1/30") },
            new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

        // Recording runner: its CallCount stays 0 unless the engine actually launches ffmpeg.
        var runner = new WritingFakeRunner();
        var engine = new JoinEngine(
            runner,
            new StubProbe(p => p == a ? ProbeResult.Success(reference) : ProbeResult.Success(mismatched)));

        var result = await engine.JoinAsync(new JoinRequest(new[] { a, b }, @"C:\out\joined.mp4"));

        // CORRECTNESS: refused, and the refusal names the mismatched field (resolution).
        result.Success.Should().BeFalse();
        result.Refusal.Should().NotBeNull();
        result.Refusal!.Mismatches.Should().Contain(m => m.Field == "resolution");

        // PERF (no-I/O-on-reject): a stream-incompatible set is turned away after the probe loop +
        // CompatChecker, before any ffmpeg launch.
        runner.CallCount.Should().Be(0, "an incompatible set is refused before ffmpeg — no I/O on reject");
    }

    /// <summary>
    /// Runner that materializes the requested output (last token) and THEN reports a non-zero exit —
    /// models ffmpeg dying part-way through the concat, leaving a partial file behind that the engine
    /// must sweep. Records the built token list so the test can name the exact temp path.
    /// </summary>
    private sealed class WritingFailingJoinRunner : IFfmpegRunner
    {
        private readonly int _exitCode;
        private readonly IReadOnlyList<string> _stderr;

        public WritingFailingJoinRunner(int exitCode, string stderr)
        {
            _exitCode = exitCode;
            _stderr = stderr.Split('\n').Select(s => s.TrimEnd('\r')).ToList().AsReadOnly();
        }

        public List<string> LastTokens { get; } = new();

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            LastTokens.Clear();
            LastTokens.AddRange(args.ToList());

            // ffmpeg got far enough to create the temp output before it died.
            File.WriteAllText(LastTokens[^1], "partial-bytes");

            return Task.FromResult(new FfmpegResult(_exitCode, _stderr));
        }
    }

    /// <summary>
    /// STRICT success runner: writes the requested output (last token) WITHOUT creating its parent
    /// directory. It therefore throws <see cref="DirectoryNotFoundException"/> unless the ENGINE
    /// created the destination folder first — which is exactly the invariant under test.
    /// </summary>
    private sealed class StrictWritingJoinRunner : IFfmpegRunner
    {
        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Deliberately NO Directory.CreateDirectory — the parent must already exist.
            File.WriteAllText(args.ToList()[^1], "joined-bytes");
            return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
        }
    }

    // SPEC-003#I28 — an ffmpeg concat failure refuses under the "ffmpeg" field AND deletes the
    // partial temp output, so a failed run leaves NOTHING behind next to the destination (the
    // log-path / full-stderr threading is asserted alongside).
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task JoinAsync_FfmpegFailure_DeletesTempOutput_AndNamesFfmpegField()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-jointemp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.mp4");
            var b = Path.Combine(dir, "b.mp4");
            await File.WriteAllTextAsync(a, "x");
            await File.WriteAllTextAsync(b, "x");
            var outPath = Path.Combine(dir, "joined.mp4");
            var logDir = Path.Combine(dir, "logs");

            // The runner writes the temp output and THEN fails: a partial file genuinely exists at
            // the moment ffmpeg reports the non-zero exit.
            var runner = new WritingFailingJoinRunner(exitCode: 1, stderr: "Invalid data found when processing input");
            var engine = new JoinEngine(
                runner,
                new StubProbe(_ => ProbeResult.Success(CompatibleClip())),
                new ErrorLogWriter(logDir));

            var result = await engine.JoinAsync(new JoinRequest(new[] { a, b }, outPath));

            result.Success.Should().BeFalse();
            result.OutputPath.Should().BeNull();
            result.Refusal.Should().NotBeNull();
            result.Refusal!.Mismatches.Should().Contain(
                m => m.Field == "ffmpeg",
                "a failed concat run is reported under the 'ffmpeg' field");

            // The temp output the runner actually created is swept — no partial file left behind.
            runner.LastTokens.Should().NotBeEmpty();
            var tempOut = runner.LastTokens[^1];
            tempOut.Should().NotBe(Path.GetFullPath(outPath), "the engine writes to a temp file, then moves it into place");
            File.Exists(tempOut).Should().BeFalse("the partial temp output is deleted when ffmpeg fails");
            File.Exists(Path.GetFullPath(outPath)).Should().BeFalse("a failed join leaves no output");
            Directory.EnumerateFiles(dir)
                .Where(f => Path.GetFileName(f).Contains(".vsj-join-", StringComparison.Ordinal))
                .Should().BeEmpty("no join temp artefact survives a failure");

            // RefusedWithLog threads the saved log path + the complete stderr onto the result.
            result.FullStdErr.Should().NotBeNullOrEmpty();
            result.FullStdErr!.Should().Contain("Invalid data found when processing input");
            result.LogFilePath.Should().NotBeNull();
            File.Exists(result.LogFilePath!).Should().BeTrue();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // SPEC-003#I30 — the multi-input half of the Joining stage detail: "{N} clips" (the
    // single-input "1 clip" half is pinned by JoinAsync_SingleInput_JoiningDetail_IsOneClip).
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task JoinAsync_MultiInput_JoiningDetail_IsNClips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-joinmany-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.mp4");
            var b = Path.Combine(dir, "b.mp4");
            var c = Path.Combine(dir, "c.mp4");
            await File.WriteAllTextAsync(a, "x");
            await File.WriteAllTextAsync(b, "x");
            await File.WriteAllTextAsync(c, "x");
            var outPath = Path.Combine(dir, "joined.mp4");

            var status = new RecordingProgress<OperationStatus>();
            var engine = new JoinEngine(new WritingFakeRunner(), new StubProbe(_ => ProbeResult.Success(CompatibleClip())));

            var result = await engine.JoinAsync(
                new JoinRequest(new[] { a, b, c }, outPath), progress: null, ct: default, status: status);

            result.Success.Should().BeTrue();
            status.Reports.Should().Contain(
                s => s.Stage == "Joining" && s.Detail == "3 clips",
                "the Joining detail pluralizes to '<N> clips' for a multi-clip join");
            status.Reports.Should().NotContain(
                s => s.Detail == "1 clip",
                "three inputs are never described as one clip");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // SPEC-003#I26 — the "parent directory is created if absent" clause: with a destination two
    // levels deep that does not exist yet, the ENGINE creates the tree before launching ffmpeg.
    // The strict runner writes its output without creating any directory, so the join can only
    // succeed if the engine did it.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task JoinAsync_MissingParentDirectory_IsCreated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-joinmkdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.mp4");
            var b = Path.Combine(dir, "b.mp4");
            await File.WriteAllTextAsync(a, "x");
            await File.WriteAllTextAsync(b, "x");

            var outPath = Path.Combine(dir, "nested", "deep", "joined.mp4");
            Directory.Exists(Path.Combine(dir, "nested")).Should().BeFalse("precondition: the destination tree is absent");

            var engine = new JoinEngine(
                new StrictWritingJoinRunner(),
                new StubProbe(_ => ProbeResult.Success(CompatibleClip())));

            var result = await engine.JoinAsync(new JoinRequest(new[] { a, b }, outPath));

            result.Success.Should().BeTrue("the engine creates the missing parent directory itself");
            result.OutputPath.Should().Be(Path.GetFullPath(outPath));
            Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(outPath))!)
                .Should().BeTrue("the parent directory is created if absent");
            File.Exists(outPath).Should().BeTrue();
            File.ReadAllText(outPath).Should().Be("joined-bytes", "the temp output is moved into the freshly created folder");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }
}
