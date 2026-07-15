using FluentAssertions;
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
}
