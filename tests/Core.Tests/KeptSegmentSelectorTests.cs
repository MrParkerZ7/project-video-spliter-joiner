using FluentAssertions;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Tests for the D-004 Bulk Cut Core foundation: <see cref="KeptSegmentSelector.ResolveKeptIndex"/>
/// (correct 1-based kept-middle index across every planner outcome) and
/// <see cref="KeptSegmentSelector.BuildKeptMiddleRequest"/> (the single-kept-segment request runs
/// through the REAL <see cref="SplitEngine"/> as exactly ONE <c>-c copy</c> per-segment command,
/// with the EOF omit-<c>-to</c> path). Reuses the binary-free fakes from
/// <see cref="SplitEngineUnitTests"/> (<see cref="FakeProbe"/> / <see cref="RecordingFakeRunner"/>).
///
/// <para>Fixture: a 10s file with a keyframe at every integer second (0,1,…,10).</para>
/// </summary>
public class KeptSegmentSelectorTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    private static List<TimeSpan> Keyframes() =>
        Enumerable.Range(0, 11).Select(i => TimeSpan.FromSeconds(i)).ToList();

    private static int ResolveKeptIndex(TimeSpan introEnd, TimeSpan? outroStart)
    {
        var keyframes = Keyframes();
        var probe = new FakeProbe(Duration, keyframes);
        return KeptSegmentSelector.ResolveKeptIndex(
            Duration,
            keyframes,
            probe.SnapToNearestKeyframe,
            probe.AverageGop(keyframes),
            introEnd,
            outroStart);
    }

    // ---- ResolveKeptIndex: index correctness across planner outcomes ------------------------

    [Fact]
    public void ResolveKeptIndex_IntroAndOutro_ReturnsIndex2()
    {
        // Intro 2s + outro 8s both survive → parts [0..2],[2..8],[8..10]; kept middle is index 2.
        ResolveKeptIndex(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)).Should().Be(2);
    }

    [Fact]
    public void ResolveKeptIndex_NoOutro_ReturnsFinalIndex2()
    {
        // Intro 2s, no outro → parts [0..2],[2..10]; kept [introEnd..EOF] is the final part, index 2.
        ResolveKeptIndex(TimeSpan.FromSeconds(2), null).Should().Be(2);
    }

    [Fact]
    public void ResolveKeptIndex_IntroSnapsToZero_WithOutro_ReturnsIndex1()
    {
        // Intro 0.3s snaps to keyframe 0 → planner DROPS that cut → parts [0..8],[8..10];
        // the kept part now begins at file start, so the kept index is 1.
        ResolveKeptIndex(TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(8)).Should().Be(1);
    }

    [Fact]
    public void ResolveKeptIndex_OutroSnapsToEof_ReturnsIndex2()
    {
        // Outro 9.8s snaps to keyframe 10 (== duration) → planner DROPS it → parts [0..2],[2..10];
        // kept stays index 2 but runs to EOF.
        ResolveKeptIndex(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(9.8)).Should().Be(2);
    }

    [Fact]
    public void ResolveKeptIndex_EmptyKeyframes_ResolvesOnRawTimes()
    {
        // No keyframes probed → raw (unsnapped) cut times → parts [0..2],[2..8],[8..10]; index 2.
        var empty = new List<TimeSpan>();
        var probe = new FakeProbe(Duration, empty);
        KeptSegmentSelector.ResolveKeptIndex(
                Duration,
                empty,
                probe.SnapToNearestKeyframe,
                probe.AverageGop(empty),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(8))
            .Should().Be(2);
    }

    [Fact]
    public void ResolveKeptIndex_BothCollapse_Throws()
    {
        // Intro 0.3s snaps to 0 AND no outro → no cut survives → planner throws SplitException,
        // which must propagate (no bogus index).
        var act = () => ResolveKeptIndex(TimeSpan.FromSeconds(0.3), null);
        act.Should().Throw<SplitException>();
    }

    // ---- BuildKeptMiddleRequest: request shape ---------------------------------------------

    [Fact]
    public void BuildKeptMiddleRequest_SetsSameDir_TrimmedName_SingleSelectedIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-buildreq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");

        try
        {
            var req = KeptSegmentSelector.BuildKeptMiddleRequest(
                input, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), keptIndex: 2);

            req.InputPath.Should().Be(input);
            req.OutputDir.Should().Be(Path.GetDirectoryName(Path.GetFullPath(input)));
            req.NamingPattern.Should().Be("{name}_trimmed{ext}");
            req.NamingPattern.Should().Be(KeptSegmentSelector.TrimmedNamingPattern);
            req.SelectedSegmentIndices.Should().Equal(2);
            req.CutPoints.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));
            req.Overwrite.Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void BuildKeptMiddleRequest_NoOutro_SingleCutPoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-buildreq-noout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");

        try
        {
            var req = KeptSegmentSelector.BuildKeptMiddleRequest(
                input, TimeSpan.FromSeconds(2), outroStart: null, keptIndex: 2);

            req.CutPoints.Should().Equal(TimeSpan.FromSeconds(2));
            req.SelectedSegmentIndices.Should().Equal(2);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ---- End-to-end: build → run through the REAL SplitEngine (fake runner) ----------------

    [Fact]
    public async Task Build_ThenSplit_Interior_RunsSinglePerSegmentCopy_WithTo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-trim-interior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");

        try
        {
            var keyframes = Keyframes();
            var probe = new FakeProbe(Duration, keyframes);
            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(runner, probe);

            var introEnd = TimeSpan.FromSeconds(2);
            var outroStart = TimeSpan.FromSeconds(8);
            var keptIndex = KeptSegmentSelector.ResolveKeptIndex(
                Duration, keyframes, probe.SnapToNearestKeyframe, probe.AverageGop(keyframes), introEnd, outroStart);
            keptIndex.Should().Be(2);

            var req = KeptSegmentSelector.BuildKeptMiddleRequest(input, introEnd, outroStart, keptIndex);
            var result = await engine.SplitAsync(req);

            // Exactly ONE ffmpeg run, on the per-segment path — interior kept part carries -to.
            runner.Commands.Should().ContainSingle();
            var tokens = runner.Commands[0];
            tokens.Should().Contain("-ss").And.Contain("-to");
            tokens.Should().NotContain("segment", "a kept-middle trim must use the per-segment copy path, not the muxer");
            SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();

            // Output is the single _trimmed file.
            result.Segments.Should().ContainSingle();
            Path.GetFileName(result.Segments[0].Path).Should().Be("clip_trimmed.mp4");
            Directory.GetFiles(dir, "*_trimmed.mp4").Select(Path.GetFileName)
                .Should().BeEquivalentTo(new[] { "clip_trimmed.mp4" });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Build_ThenSplit_NoOutro_EofPath_OmitsTo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-trim-eof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");

        try
        {
            var keyframes = Keyframes();
            var probe = new FakeProbe(Duration, keyframes);
            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(runner, probe);

            var introEnd = TimeSpan.FromSeconds(2);
            var keptIndex = KeptSegmentSelector.ResolveKeptIndex(
                Duration, keyframes, probe.SnapToNearestKeyframe, probe.AverageGop(keyframes), introEnd, outroStart: null);
            keptIndex.Should().Be(2);

            var req = KeptSegmentSelector.BuildKeptMiddleRequest(input, introEnd, outroStart: null, keptIndex);
            var result = await engine.SplitAsync(req);

            // Exactly ONE per-segment run; kept-to-EOF (final part) OMITS -to.
            runner.Commands.Should().ContainSingle();
            var tokens = runner.Commands[0];
            tokens.Should().Contain("-ss");
            tokens.Should().NotContain("-to", "a kept-to-EOF trim copies to end of file and must omit -to");
            tokens.Should().NotContain("segment");
            SplitArgsBuilder.SatisfiesCopyInvariant(tokens).Should().BeTrue();

            result.Segments.Should().ContainSingle();
            Path.GetFileName(result.Segments[0].Path).Should().Be("clip_trimmed.mp4");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Build_ThenSplit_NoEncoderTokens_SourceUntouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-trim-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        const string original = "the-original-bytes";
        await File.WriteAllTextAsync(input, original);

        try
        {
            var keyframes = Keyframes();
            var probe = new FakeProbe(Duration, keyframes);
            var runner = new RecordingFakeRunner();
            var engine = new SplitEngine(runner, probe);

            var introEnd = TimeSpan.FromSeconds(2);
            var outroStart = TimeSpan.FromSeconds(8);
            var keptIndex = KeptSegmentSelector.ResolveKeptIndex(
                Duration, keyframes, probe.SnapToNearestKeyframe, probe.AverageGop(keyframes), introEnd, outroStart);

            var req = KeptSegmentSelector.BuildKeptMiddleRequest(input, introEnd, outroStart, keptIndex);
            await engine.SplitAsync(req);

            // No encoder token leaks into the single built command.
            var tokens = runner.Commands.Should().ContainSingle().Subject;
            foreach (var forbidden in SplitArgsBuilder.ForbiddenEncoderTokens)
            {
                tokens.Should().NotContain(forbidden);
            }

            // The SOURCE file is present and byte-for-byte unmodified; a distinct _trimmed file exists.
            File.Exists(input).Should().BeTrue("the original must never be touched");
            (await File.ReadAllTextAsync(input)).Should().Be(original);
            File.Exists(Path.Combine(dir, "clip_trimmed.mp4")).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
