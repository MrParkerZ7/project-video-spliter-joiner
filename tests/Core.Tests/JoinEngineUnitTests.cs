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
}
