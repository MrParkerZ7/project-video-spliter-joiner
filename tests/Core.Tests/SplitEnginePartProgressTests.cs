using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Engine-level tests for the per-part progress channel (T-069): the fast single-pass segment-muxer
/// path DERIVES <see cref="PartProgress"/> from the overall ffmpeg fraction it already parses (no
/// extra passes), and the per-segment subset path reports it naturally from its loop. Uses fake
/// runners that materialize the expected temp outputs and (for the muxer test) emit progress
/// fractions, so routing + derivation are exercised without a real ffmpeg binary.
/// </summary>
public sealed class SplitEnginePartProgressTests
{
    [Fact]
    public async Task MuxerPath_IsSinglePass_AndDerivesPerPartProgressFromFraction()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-pp-mux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        try
        {
            // 30s file, cuts at 10 and 20 → 3 parts [0,10)[10,20)[20,30). Emit fractions 0.25, 0.5, 0.9.
            var runner = new ProgressEmittingRunner(new[] { 0.25, 0.5, 0.9 });
            var probe = new FakeProbe(
                TimeSpan.FromSeconds(30),
                Enumerable.Range(0, 31).Select(i => TimeSpan.FromSeconds(i)).ToList());
            var engine = new SplitEngine(runner, probe);

            var req = new SplitRequest(
                input,
                new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) },
                outDir); // null selection = full set → muxer path

            var samples = new List<PartProgress>();
            var partProgress = new CollectingProgress<PartProgress>(samples.Add);

            await engine.SplitAsync(req, progress: null, ct: default, status: null, partProgress: partProgress);

            // Single ffmpeg pass — the fast path is NOT split into per-segment runs.
            runner.RunCount.Should().Be(1, "the muxer path stays a single ffmpeg pass");

            // fraction 0.25 → time 7.5s → part 1 @ 0.75 ; 0.5 → 15s → part 2 @ 0.5 ; 0.9 → 27s → part 3 @ 0.7.
            samples.Should().Contain(p => p.PartIndex == 1 && p.PartCount == 3);
            samples.Should().Contain(p => p.PartIndex == 2 && p.PartCount == 3);
            samples.Should().Contain(p => p.PartIndex == 3 && p.PartCount == 3);

            var p1 = samples.First(p => p.PartIndex == 1);
            p1.PartFraction.Should().BeApproximately(0.75, 1e-6); // 7.5 of [0,10)
            var p2 = samples.First(p => p.PartIndex == 2);
            p2.PartFraction.Should().BeApproximately(0.5, 1e-6);  // 5 of [10,20)

            // Final sample marks the last part fully done.
            samples[^1].Should().Be(new PartProgress(3, 3, 1.0));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SubsetPath_ReportsPerPartByOriginalIndex_AndOnePassPerSelectedPart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-pp-sub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        try
        {
            // 3 parts; select parts 1 and 3 → per-segment path, one run per selected part.
            var runner = new ProgressEmittingRunner(new[] { 0.5 }); // each run emits a single mid fraction
            var probe = new FakeProbe(
                TimeSpan.FromSeconds(30),
                Enumerable.Range(0, 31).Select(i => TimeSpan.FromSeconds(i)).ToList());
            var engine = new SplitEngine(runner, probe);

            var req = new SplitRequest(
                input,
                new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) },
                outDir,
                SelectedSegmentIndices: new[] { 1, 3 });

            var samples = new List<PartProgress>();
            var partProgress = new CollectingProgress<PartProgress>(samples.Add);

            await engine.SplitAsync(req, progress: null, ct: default, status: null, partProgress: partProgress);

            runner.RunCount.Should().Be(2, "one ffmpeg run per selected part in the subset path");

            // Parts reported by their ORIGINAL 1-based index (1 and 3), never renumbered to 1 and 2.
            samples.Select(p => p.PartIndex).Distinct().Should().BeEquivalentTo(new[] { 1, 3 });
            samples.Should().OnlyContain(p => p.PartCount == 3);

            // Each selected part ends Done (fraction 1).
            samples.Should().Contain(new PartProgress(1, 3, 1.0));
            samples.Should().Contain(new PartProgress(3, 3, 1.0));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

/// <summary>
/// Fake runner that materializes the temp outputs (like RecordingFakeRunner) AND emits a scripted
/// list of overall progress fractions through the supplied <see cref="IProgress{T}"/> on each run,
/// so the engine's per-part derivation can be observed. Counts its own invocations.
/// </summary>
internal sealed class ProgressEmittingRunner : IFfmpegRunner
{
    private readonly IReadOnlyList<double> _fractions;

    public ProgressEmittingRunner(IReadOnlyList<double> fractions) => _fractions = fractions;

    public int RunCount { get; private set; }

    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        RunCount++;

        var tokens = args.ToList().ToList();
        var output = tokens[^1];
        if (output.Contains("%03d", StringComparison.Ordinal))
        {
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

        foreach (var f in _fractions)
        {
            progress?.Report(f);
        }

        return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
    }
}

/// <summary>Minimal synchronous <see cref="IProgress{T}"/> that forwards each report to a collector.</summary>
internal sealed class CollectingProgress<T> : IProgress<T>
{
    private readonly Action<T> _sink;

    public CollectingProgress(Action<T> sink) => _sink = sink;

    public void Report(T value) => _sink(value);
}
