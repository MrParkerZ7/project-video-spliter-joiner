using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Pure-unit tests for the split engine that need no real binary: filename-pattern rendering,
/// and the cancel-safety guarantee (a cancelled split leaves no half-written FINAL output).
/// The runner and probe are faked; the fake runner honors the cancellation token.
/// </summary>
public class SplitEngineUnitTests
{
    [Theory]
    [InlineData("{name}_part{index:00}{ext}", "clip", ".mp4", 1, "clip_part01.mp4")]
    [InlineData("{name}_part{index:00}{ext}", "clip", ".mp4", 12, "clip_part12.mp4")]
    [InlineData("{name}-{index:000}{ext}", "movie", ".mkv", 3, "movie-003.mkv")]
    [InlineData("{name}_{index}{ext}", "v", ".mp4", 7, "v_7.mp4")]
    [InlineData("seg{index:00}{ext}", "ignored", ".mov", 5, "seg05.mov")]
    public void ApplyNamingPattern_RendersTokens(string pattern, string name, string ext, int index, string expected)
    {
        SplitEngine.ApplyNamingPattern(pattern, name, ext, index).Should().Be(expected);
    }

    [Fact]
    public void ApplyNamingPattern_EmptyPattern_FallsBackToDefault()
    {
        SplitEngine.ApplyNamingPattern(string.Empty, "clip", ".mp4", 2).Should().Be("clip_part02.mp4");
    }

    [Fact]
    public async Task SplitAsync_Cancelled_LeavesNoFinalOutputFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // A real (empty) input file so the shape validation passes.
        var input = Path.Combine(dir, "in.mp4");
        await File.WriteAllTextAsync(input, "placeholder");

        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        try
        {
            var cts = new CancellationTokenSource();

            // Runner that cancels partway (as ffmpeg-with-ct would) and never writes finals.
            var runner = new CancellingFakeRunner(cts);
            var probe = new FakeProbe(
                duration: TimeSpan.FromSeconds(10),
                keyframes: Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList());

            var engine = new SplitEngine(runner, probe);
            var req = new SplitRequest(
                input,
                new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) },
                outDir);

            Func<Task> act = () => engine.SplitAsync(req, progress: null, ct: cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();

            // No FINAL named segment should exist, and no leftover temp dir.
            Directory.GetFiles(outDir, "*.mp4").Should().BeEmpty("a cancelled split must leave no final output");
            Directory.GetDirectories(outDir, ".vsj-split-*").Should().BeEmpty("the temp dir must be cleaned up");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SplitAsync_RefusesToOverwrite_WhenOverwriteFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-clobber-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        // Pre-create the file the first segment would write to.
        await File.WriteAllTextAsync(Path.Combine(outDir, "clip_part01.mp4"), "existing");

        try
        {
            var runner = new NoopFakeRunner();
            var probe = new FakeProbe(
                TimeSpan.FromSeconds(10),
                Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList());
            var engine = new SplitEngine(runner, probe);

            var req = new SplitRequest(input, new[] { TimeSpan.FromSeconds(5) }, outDir, Overwrite: false);

            Func<Task> act = () => engine.SplitAsync(req);
            await act.Should().ThrowAsync<SplitException>().WithMessage("*already exists*");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

/// <summary>Fake probe returning a fixed duration + keyframe list, no binary.</summary>
internal sealed class FakeProbe : IMediaProbe
{
    private readonly TimeSpan _duration;
    private readonly IReadOnlyList<TimeSpan> _keyframes;
    private readonly MediaProbe _real = new(new FakeFfprobeRunner("{}"));

    public FakeProbe(TimeSpan duration, IReadOnlyList<TimeSpan> keyframes)
    {
        _duration = duration;
        _keyframes = keyframes;
    }

    public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(ProbeResult.Success(new MediaInfo(
            _duration,
            "mp4",
            new List<StreamInfo>().AsReadOnly(),
            new List<StreamInfo>().AsReadOnly())));

    public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(_keyframes);

    // Delegate snapping/GOP to the real (binary-free) implementation.
    public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
        _real.SnapToNearestKeyframe(keyframes, requested);

    public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => _real.AverageGop(keyframes);
}

/// <summary>Runner that cancels the token and throws — simulates a mid-run cancel.</summary>
internal sealed class CancellingFakeRunner : IFfmpegRunner
{
    private readonly CancellationTokenSource _cts;

    public CancellingFakeRunner(CancellationTokenSource cts) => _cts = cts;

    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // Assert the invariant even in the fake path — the engine must only hand us copy args.
        SplitArgsBuilder.SatisfiesCopyInvariant(args.ToList()).Should().BeTrue();

        _cts.Cancel();
        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }
}

/// <summary>Runner that would succeed but is never expected to be reached in these tests.</summary>
internal sealed class NoopFakeRunner : IFfmpegRunner
{
    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
}
