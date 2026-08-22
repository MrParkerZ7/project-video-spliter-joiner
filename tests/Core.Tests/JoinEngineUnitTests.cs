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
}
