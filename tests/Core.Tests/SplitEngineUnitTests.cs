using FluentAssertions;
using VideoSplitJoiner.Core.Errors;
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
    public async Task SplitAsync_FfmpegFailure_WritesFullLog_AndThreadsPathAndFullText_ToException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-splitfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);
        var logDir = Path.Combine(dir, "logs");

        try
        {
            var fullStdErr = string.Join(Environment.NewLine,
                Enumerable.Range(0, 500).Select(i => $"stderr line {i}"))
                + Environment.NewLine + "Conversion failed! disk error";

            var runner = new FailingFakeRunner(exitCode: -22, stderr: fullStdErr);
            var probe = new FakeProbe(
                TimeSpan.FromSeconds(10),
                Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList());

            var engine = new SplitEngine(runner, probe, new ErrorLogWriter(logDir));
            var req = new SplitRequest(input, new[] { TimeSpan.FromSeconds(5) }, outDir);

            var ex = await Assert.ThrowsAsync<SplitException>(() => engine.SplitAsync(req));

            // Full text is threaded through (not a truncated tail) + a log file was written.
            ex.FullStdErr.Should().NotBeNullOrEmpty();
            ex.FullStdErr!.Should().Contain("stderr line 0").And.Contain("stderr line 499")
                .And.Contain("Conversion failed! disk error");
            ex.LogFilePath.Should().NotBeNull();
            File.Exists(ex.LogFilePath!).Should().BeTrue();

            var logContent = await File.ReadAllTextAsync(ex.LogFilePath!);
            logContent.Should().Contain("stderr line 499", "the FULL stderr is persisted, not just the tail");
            logContent.Should().Contain("-22", "the exit code is persisted");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SplitAsync_Subset_UsesPerSegmentCopyPath_NoEncoderTokens_WritesOnlySelected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-subset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        try
        {
            // 10s file, 1s GOP. Cuts at 3 & 6 → 3 parts. Select only the middle part (index 2).
            var runner = new RecordingFakeRunner();
            var probe = new FakeProbe(
                TimeSpan.FromSeconds(10),
                Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList());
            var engine = new SplitEngine(runner, probe);

            var req = new SplitRequest(
                input,
                new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) },
                outDir,
                SelectedSegmentIndices: new[] { 2 });

            var result = await engine.SplitAsync(req);

            // ONE ffmpeg run (one selected part), and it is the PER-SEGMENT path (-ss/-to), not the muxer.
            runner.Commands.Should().ContainSingle();
            var tokens = runner.Commands[0];
            tokens.Should().Contain("-ss").And.Contain("-to");
            tokens.Should().NotContain("segment", "the subset path must NOT use the segment muxer");

            // The copy invariant holds and NO encoder token leaks in.
            SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
            foreach (var forbidden in SplitArgsBuilder.ForbiddenEncoderTokens)
            {
                tokens.Should().NotContain(forbidden);
            }

            // Only the selected part is produced, and it keeps its ORIGINAL index (_part02).
            result.Segments.Should().ContainSingle();
            Path.GetFileName(result.Segments[0].Path).Should().Be("clip_part02.mp4");
            Directory.GetFiles(outDir, "*.mp4").Select(Path.GetFileName)
                .Should().BeEquivalentTo(new[] { "clip_part02.mp4" });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SplitAsync_FullSelection_UsesSegmentMuxerPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-fullsel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        try
        {
            var runner = new RecordingFakeRunner();
            var probe = new FakeProbe(
                TimeSpan.FromSeconds(10),
                Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList());
            var engine = new SplitEngine(runner, probe);

            // Selecting all 3 parts explicitly == the full contiguous set → single muxer pass.
            var req = new SplitRequest(
                input,
                new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) },
                outDir,
                SelectedSegmentIndices: new[] { 1, 2, 3 });

            await engine.SplitAsync(req);

            runner.Commands.Should().ContainSingle("the muxer path is a single ffmpeg pass");
            runner.Commands[0].Should().Contain("segment").And.Contain("-segment_times");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SplitAsync_EmptySelection_Rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-emptysel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        try
        {
            var runner = new RecordingFakeRunner();
            var probe = new FakeProbe(
                TimeSpan.FromSeconds(10),
                Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList());
            var engine = new SplitEngine(runner, probe);

            var req = new SplitRequest(
                input,
                new[] { TimeSpan.FromSeconds(5) },
                outDir,
                SelectedSegmentIndices: Array.Empty<int>());

            Func<Task> act = () => engine.SplitAsync(req);
            await act.Should().ThrowAsync<SplitException>().WithMessage("*No segments selected*");
            runner.Commands.Should().BeEmpty("no ffmpeg should run for an empty selection");
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

/// <summary>Runner that returns a non-zero exit + a supplied (large) stderr, simulating an ffmpeg failure.</summary>
internal sealed class FailingFakeRunner : IFfmpegRunner
{
    private readonly int _exitCode;
    private readonly IReadOnlyList<string> _stderr;

    public FailingFakeRunner(int exitCode, string stderr)
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

/// <summary>
/// Records every command's token list AND materializes the temp files the engine expects to move
/// into place — so routing/invariant assertions run without a real binary while the split still
/// "succeeds". For the per-segment path the output token is a concrete file (created verbatim); for
/// the segment-muxer path the output token is a <c>part%03d.ext</c> pattern, expanded to
/// <c>part000…part00{N-1}</c> where N = (comma count in <c>-segment_times</c>) + 1.
/// </summary>
internal sealed class RecordingFakeRunner : IFfmpegRunner
{
    public List<IReadOnlyList<string>> Commands { get; } = new();

    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tokens = args.ToList().ToList();
        Commands.Add(tokens);

        var output = tokens[^1];
        if (output.Contains("%03d", StringComparison.Ordinal))
        {
            // Muxer pattern → expand from the -segment_times comma count.
            var idx = tokens.IndexOf("-segment_times");
            var count = 1;
            if (idx >= 0 && idx + 1 < tokens.Count)
            {
                count = tokens[idx + 1].Split(',').Length + 1;
            }

            for (var i = 0; i < count; i++)
            {
                File.WriteAllText(output.Replace("%03d", i.ToString("000")), "seg");
            }
        }
        else
        {
            File.WriteAllText(output, "seg");
        }

        return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
    }
}
