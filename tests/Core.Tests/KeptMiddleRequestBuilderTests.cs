using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Correctness tests for the production <see cref="KeptMiddleRequestBuilder"/> (the T-094 seam):
/// it probes for duration + keyframes, delegates the kept-middle index to
/// <see cref="KeptSegmentSelector.ResolveKeptIndex"/>, and assembles a single-kept-segment
/// <see cref="SplitRequest"/> that writes to the runner's collision-resolved path — and runs
/// through the REAL <see cref="SplitEngine"/> as exactly one <c>-c copy</c> per-segment command.
/// Reuses the binary-free <see cref="FakeProbe"/> / <see cref="RecordingFakeRunner"/> from
/// <see cref="SplitEngineUnitTests"/>. Fixture: a 10s file with a keyframe at every integer second.
/// </summary>
public class KeptMiddleRequestBuilderTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    private static List<TimeSpan> Keyframes() =>
        Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList();

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-kmrb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Build_WithOutro_TwoCutPoints_LiteralNamingPattern_Index2()
    {
        var dir = NewDir();
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "src");
        try
        {
            var builder = new KeptMiddleRequestBuilder(new FakeProbe(Duration, Keyframes()));
            var effective = Path.Combine(dir, "clip_trimmed.mp4");
            var item = new BulkTrimItem(input, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), effective);

            var req = await builder.BuildAsync(item, effective, overwrite: false, CancellationToken.None);

            req.CutPoints.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));
            req.OutputDir.Should().Be(Path.GetDirectoryName(Path.GetFullPath(effective)));
            req.NamingPattern.Should().Be("clip_trimmed.mp4", "the literal file name (no {index}) lands the segment verbatim");
            req.SelectedSegmentIndices.Should().Equal(2);
            req.Overwrite.Should().BeFalse();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Build_NoOutro_SingleCutPoint_KeepsToEof()
    {
        var dir = NewDir();
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "src");
        try
        {
            var builder = new KeptMiddleRequestBuilder(new FakeProbe(Duration, Keyframes()));
            var effective = Path.Combine(dir, "clip_trimmed.mp4");
            var item = new BulkTrimItem(input, TimeSpan.FromSeconds(2), null, effective);

            var req = await builder.BuildAsync(item, effective, overwrite: false, CancellationToken.None);

            req.CutPoints.Should().Equal(TimeSpan.FromSeconds(2));
            req.SelectedSegmentIndices.Should().Equal(2);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Build_IntroSnapsToZero_WithOutro_ResolvesIndex1()
    {
        var dir = NewDir();
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "src");
        try
        {
            var builder = new KeptMiddleRequestBuilder(new FakeProbe(Duration, Keyframes()));
            var effective = Path.Combine(dir, "clip_trimmed.mp4");
            // Intro 0.3s snaps to keyframe 0 → planner drops it → kept part begins at file start → index 1.
            var item = new BulkTrimItem(input, TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(8), effective);

            var req = await builder.BuildAsync(item, effective, overwrite: false, CancellationToken.None);

            req.SelectedSegmentIndices.Should().Equal(1); // the index-not-always-2 case is delegated to T-094.
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Build_Overwrite_PassedThrough()
    {
        var dir = NewDir();
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "src");
        try
        {
            var builder = new KeptMiddleRequestBuilder(new FakeProbe(Duration, Keyframes()));
            var effective = Path.Combine(dir, "clip_trimmed_2.mp4");
            var item = new BulkTrimItem(input, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), effective);

            var req = await builder.BuildAsync(item, effective, overwrite: true, CancellationToken.None);

            req.Overwrite.Should().BeTrue();
            req.NamingPattern.Should().Be("clip_trimmed_2.mp4", "the collision-resolved name is honored verbatim");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Build_ThenRunThroughRealEngine_SinglePerSegmentCopy_SatisfiesInvariant()
    {
        var dir = NewDir();
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "src");
        try
        {
            var probe = new FakeProbe(Duration, Keyframes());
            var builder = new KeptMiddleRequestBuilder(probe);
            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(runner, probe);
            var effective = Path.Combine(dir, "clip_trimmed.mp4");
            var item = new BulkTrimItem(input, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), effective);

            var req = await builder.BuildAsync(item, effective, overwrite: false, CancellationToken.None);
            var result = await engine.SplitAsync(req);

            runner.Commands.Should().ContainSingle("a kept-middle trim is exactly one per-segment run");
            var tokens = runner.Commands[0];
            tokens.Should().Contain("-ss").And.Contain("-to");
            tokens.Should().NotContain("segment", "it must not use the segment muxer");
            SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();
            foreach (var forbidden in SplitArgsBuilder.ForbiddenEncoderTokens)
            {
                tokens.Should().NotContain(forbidden);
            }

            Path.GetFileName(result.Segments[0].Path).Should().Be("clip_trimmed.mp4");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Build_NoOpTrim_ThrowsNoOpTrimException()
    {
        var dir = NewDir();
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "src");
        try
        {
            var builder = new KeptMiddleRequestBuilder(new FakeProbe(Duration, Keyframes()));
            var effective = Path.Combine(dir, "clip_trimmed.mp4");
            // Intro 0.3s snaps to 0 AND no outro → no cut survives → ResolveKeptIndex throws
            // SplitException → the builder translates it into the runner's Skipped signal.
            var item = new BulkTrimItem(input, TimeSpan.FromSeconds(0.3), null, effective);

            var act = () => builder.BuildAsync(item, effective, overwrite: false, CancellationToken.None);

            await act.Should().ThrowAsync<NoOpTrimException>();
        }
        finally { Cleanup(dir); }
    }
}
