using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-124 (epic G-042) — frame-exact "smart" cutting. The user set the intro at 5s on a ~4s keyframe
/// grid and got 4s, because a stream-copied segment must START on a keyframe. Smart cutting honours the
/// requested time by re-encoding only the fragment between the request and the next keyframe and
/// stream-copying the rest, so the cost is bounded by one GOP rather than the whole file.
/// </summary>
public sealed class SmartCutTests
{
    /// <summary>The grid from the bug report: keyframes every 4s.</summary>
    private static List<TimeSpan> Grid4s(double totalSeconds = 60) =>
        Enumerable.Range(0, (int)(totalSeconds / 4) + 1).Select(i => TimeSpan.FromSeconds(i * 4)).ToList();

    private static MediaInfo H264Aac() => new(
        TimeSpan.FromSeconds(60),
        "mov,mp4,m4a,3gp,3g2,mj2",
        new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/12800") },
        new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

    // ---- The planner: the reported case now lands exactly ------------------------------------

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RequestAt5s_OnA4sGrid_ReencodesOnlyUpToTheNextKeyframe()
    {
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), null, Grid4s());

        plan.Strategy.Should().Be(SmartCutStrategy.HeadReencode);
        plan.Start.Should().Be(TimeSpan.FromSeconds(5), "the cut honours EXACTLY what the user asked for");
        plan.HeadEnd.Should().Be(TimeSpan.FromSeconds(8), "the copyable tail begins at the next keyframe");
        plan.ReencodedDuration.Should().Be(TimeSpan.FromSeconds(3), "only the head fragment is re-encoded");
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RequestAt6s_ProducesADifferentPlanThan5s_TheReportedSymptomIsGone()
    {
        var at5 = SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), null, Grid4s());
        var at6 = SmartCutPlanner.Plan(TimeSpan.FromSeconds(6), null, Grid4s());

        at5.Start.Should().Be(TimeSpan.FromSeconds(5));
        at6.Start.Should().Be(TimeSpan.FromSeconds(6));
        at6.Should().NotBe(at5, "moving the playhead now genuinely changes the result — the whole complaint");
        at6.ReencodedDuration.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void RequestAlreadyOnAKeyframe_TakesThePureCopyPath_NoReencode()
    {
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(8), null, Grid4s());

        plan.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        plan.HasReencode.Should().BeFalse("nothing is re-encoded when the lossless cut is already exact");
        plan.ReencodedDuration.Should().Be(TimeSpan.Zero);
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ARequestWithinToleranceOfAKeyframe_CountsAsOnIt()
    {
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(8).Add(TimeSpan.FromMilliseconds(4)), null, Grid4s());

        plan.Strategy.Should().Be(SmartCutStrategy.PureCopy, "float/UI rounding must not force a pointless re-encode");
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ARequestInsideTheFinalGop_HasNoCopyableTail_SoTheRangeIsFullyReencoded()
    {
        // Keyframes end at 8s; a request at 9s with end 10s has no keyframe ahead of it.
        var keyframes = new List<TimeSpan> { TimeSpan.Zero, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) };

        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10), keyframes);

        plan.Strategy.Should().Be(SmartCutStrategy.FullReencode);
        plan.HeadEnd.Should().BeNull();
        plan.ReencodedDuration.Should().Be(TimeSpan.FromSeconds(1), "still bounded — it is a sub-GOP range");
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AKeyframeAtOrAfterTheRequestedEnd_IsNotAUsableTailBoundary()
    {
        // Next keyframe (8s) is at/after the requested end (8s) → nothing left to copy.
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), Grid4s());

        plan.Strategy.Should().Be(SmartCutStrategy.FullReencode);
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void EmptyKeyframes_FallBackToFullReencode_NeverACrash()
    {
        var plan = SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), Array.Empty<TimeSpan>());

        plan.Strategy.Should().Be(SmartCutStrategy.FullReencode);
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void InvalidRanges_AreRejected()
    {
        var act1 = () => SmartCutPlanner.Plan(TimeSpan.FromSeconds(-1), null, Grid4s());
        act1.Should().Throw<ArgumentOutOfRangeException>();

        var act2 = () => SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), Grid4s());
        act2.Should().Throw<ArgumentException>("an empty or inverted range is not a cut");
    }

    // ---- The args: the head matches the source, the tail stays a pure copy ---------------------

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void HeadReencode_MatchesTheSourceStreams_SoTheConcatWillAccept()
    {
        var args = SmartCutArgsBuilder.HeadReencode(
            @"C:\v\clip.mp4", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8),
            H264Aac(), "libx264", "aac", @"C:\v\head.mp4").ToList();

        args.Should().Contain("libx264").And.Contain("yuv420p").And.Contain("1920x1080");
        args.Should().Contain("aac").And.Contain("48000").And.Contain("2");
        // Output seek (-ss AFTER -i) is what makes the cut frame-exact.
        var list = args.ToList();
        list.FindIndex(t => t == "-ss").Should().BeGreaterThan(list.FindIndex(t => t.EndsWith("clip.mp4")),
            "the seek must come after the input for a frame-exact start");
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TailCopy_IsStillAPureStreamCopy_NoEncoderLeaksIn()
    {
        var args = SmartCutArgsBuilder.TailCopy(
            @"C:\v\clip.mp4", TimeSpan.FromSeconds(8), null, @"C:\v\tail.mp4").ToList();

        args.Should().Contain("copy");
        SplitArgsBuilder.SatisfiesCopyInvariant(args).Should().BeTrue(
            "the tail is exactly what the lossless path would have produced");
    }

    // ---- The fallback: never guess an encoder --------------------------------------------------

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void KnownCodecs_ResolveToEncoders()
    {
        SmartCutArgsBuilder.TryResolveEncoders(H264Aac(), out var v, out var a, out var why)
            .Should().BeTrue();
        v.Should().Be("libx264");
        a.Should().Be("aac");
        why.Should().BeNull();
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AnUnmappableCodec_ReportsAFallback_RatherThanGuessing()
    {
        var exotic = new MediaInfo(
            TimeSpan.FromSeconds(10), "matroska",
            new[] { new StreamInfo(0, "prores_raw_hq", "video", 1920, 1080, "yuv422p10le", null, null, null) },
            Array.Empty<StreamInfo>());

        SmartCutArgsBuilder.TryResolveEncoders(exotic, out _, out _, out var why).Should().BeFalse();
        why.Should().Contain("prores_raw_hq",
            "the caller must be told WHY, so it can fall back to the lossless cut instead of shipping a corrupt file");
    }

    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void ASourceWithNoStreams_CannotBeSmartCut()
    {
        var empty = new MediaInfo(
            TimeSpan.FromSeconds(10), "matroska", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>());

        SmartCutArgsBuilder.TryResolveEncoders(empty, out _, out _, out var why).Should().BeFalse();
        why.Should().NotBeNull();
    }

    // ---- The ENGINE: temp-dir discipline, cancel safety, and the invocation budget -------------
    //
    // Everything above exercises the pure planner / args-builder. These drive the CONCRETE
    // SmartCutEngine. Note the shared FakeProbe (SplitEngineUnitTests.cs) reports EMPTY stream lists,
    // which short-circuits TryResolveEncoders into the fallback branch BEFORE a temp dir is ever
    // created — so the engine tests need a probe that reports real streams.

    /// <summary>
    /// A probe that reports a real (streamed) <see cref="MediaInfo"/> plus a fixed keyframe grid, so
    /// <see cref="SmartCutArgsBuilder.TryResolveEncoders"/> resolves and the engine actually runs its
    /// head → tail → concat pipeline. Snapping is deliberately unsupported: the smart-cut path plans
    /// against the RAW keyframe list and must never snap (that is the whole point of the feature).
    /// </summary>
    private sealed class StreamedFakeProbe : IMediaProbe
    {
        private readonly MediaInfo _info;
        private readonly IReadOnlyList<TimeSpan> _keyframes;

        public StreamedFakeProbe(MediaInfo info, IReadOnlyList<TimeSpan> keyframes)
        {
            _info = info;
            _keyframes = keyframes;
        }

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(ProbeResult.Success(_info));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(_keyframes);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
            throw new NotSupportedException("Smart cutting honours the requested time EXACTLY — it never snaps.");

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    /// <summary>
    /// Cancels the token and throws on its FIRST invocation — a mid-run cancel. (The split-side
    /// CancellingFakeRunner cannot be reused here: it asserts the pure-copy invariant, which the head
    /// RE-ENCODE command legitimately violates.)
    /// </summary>
    private sealed class CancellingSmartCutRunner : IFfmpegRunner
    {
        private readonly CancellationTokenSource _cts;

        public CancellingSmartCutRunner(CancellationTokenSource cts) => _cts = cts;

        public int Calls { get; private set; }

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            Calls++;
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(ct);
        }
    }

    // SPEC-001#I47 — every intermediate lives in a `.vsj-smartcut-<guid>` temp dir swept in a
    // `finally`, and the final file is moved into place only AFTER it exists. A cancel must therefore
    // leave neither a half-written output nor a stray temp dir behind.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_Cancelled_LeavesNoFinalOutput_AndSweepsTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-smartcancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outputPath = Path.Combine(dir, "cut.mp4");

        try
        {
            var cts = new CancellationTokenSource();
            var runner = new CancellingSmartCutRunner(cts);
            var engine = new SmartCutEngine(runner, new StreamedFakeProbe(H264Aac(), Grid4s()));

            // 5s on a 4s grid → HeadReencode, so the engine gets far enough to create its temp dir.
            Func<Task> act = () => engine.CutAsync(
                input, TimeSpan.FromSeconds(5), null, outputPath, progress: null, ct: cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();

            runner.Calls.Should().Be(1, "the cancel lands on the very first (head re-encode) invocation");
            File.Exists(outputPath).Should().BeFalse("a cancelled cut must leave NO final output");
            Directory.GetDirectories(dir, ".vsj-smartcut-*").Should()
                .BeEmpty("the temp dir holding every intermediate is swept in a finally");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // SPEC-001#I48 — a HeadReencode is EXACTLY three ffmpeg invocations (head encode, tail copy,
    // concat), never one per GOP. Each pass is pinned by identity so a re-ordering or an extra pass is
    // caught, not just the count.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_HeadReencode_RunsExactlyThreeFfmpegInvocations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-smarthead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outputPath = Path.Combine(dir, "cut.mp4");

        try
        {
            var runner = new RecordingFakeRunner();
            var engine = new SmartCutEngine(runner, new StreamedFakeProbe(H264Aac(), Grid4s()));

            var result = await engine.CutAsync(input, TimeSpan.FromSeconds(5), null, outputPath);

            runner.Commands.Should().HaveCount(3,
                "a head re-encode is head + tail + concat and NOTHING else — never one pass per GOP");

            // 1. The head fragment: re-encoded, starting at EXACTLY the requested 5s, bounded to 3s.
            var head = runner.Commands[0].ToList();
            head.Should().Contain("libx264");
            var headSs = head.IndexOf("-ss");
            headSs.Should().BeGreaterThanOrEqualTo(0);
            head[headSs + 1].Should().Be("5", "the head starts at exactly the requested time");
            var headT = head.IndexOf("-t");
            headT.Should().BeGreaterThanOrEqualTo(0);
            head[headT + 1].Should().Be("3", "the re-encode is bounded by one GOP (5s to the 8s keyframe)");

            // 2. The tail: a pure stream copy from the boundary keyframe to EOF.
            var tail = runner.Commands[1].ToList();
            SplitArgsBuilder.SatisfiesCopyInvariant(tail).Should().BeTrue();
            var tailSs = tail.IndexOf("-ss");
            tailSs.Should().BeGreaterThanOrEqualTo(0);
            tail[tailSs + 1].Should().Be("8", "the copyable tail begins at the next keyframe");
            tail.Should().NotContain("-to");

            // 3. The concat: also a stream copy, never a second encode.
            var concat = runner.Commands[2].ToList();
            concat.Should().Contain("concat");
            SplitArgsBuilder.SatisfiesCopyInvariant(concat).Should().BeTrue();

            result.Strategy.Should().Be(SmartCutStrategy.HeadReencode);
            result.FellBack.Should().BeFalse("h264/aac resolve to encoders, so the exact route is available");
            result.OutputPath.Should().Be(outputPath);
            result.ReencodedDuration.Should().Be(TimeSpan.FromSeconds(3), "only the 5s-to-8s fragment is re-encoded");

            File.Exists(outputPath).Should().BeTrue("the final file is moved into place once it exists");
            Directory.GetDirectories(dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // SPEC-001#I48 (the other half) — a FullReencode is EXACTLY ONE invocation: with no copyable tail
    // there is nothing to copy and nothing to concat, and the head IS the output.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_FullReencode_RunsExactlyOneFfmpegInvocation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-smartfull-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outputPath = Path.Combine(dir, "cut.mp4");

        try
        {
            // Keyframes stop at 8s, so a 9s-to-10s request has no keyframe ahead of it.
            var keyframes = new List<TimeSpan> { TimeSpan.Zero, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) };
            var runner = new RecordingFakeRunner();
            var engine = new SmartCutEngine(runner, new StreamedFakeProbe(H264Aac(), keyframes));

            var result = await engine.CutAsync(
                input, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10), outputPath);

            runner.Commands.Should().ContainSingle("a full re-encode is ONE pass — no tail copy, no concat");
            runner.Commands[0].ToList().Should().Contain("libx264");

            result.Strategy.Should().Be(SmartCutStrategy.FullReencode);
            result.FellBack.Should().BeFalse();
            result.ReencodedDuration.Should().Be(TimeSpan.FromSeconds(1), "the range is still bounded by one GOP");

            File.Exists(outputPath).Should().BeTrue("the re-encoded head IS the output here");
            Directory.GetDirectories(dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // SPEC-002#I53 — on the Exact route the produced file is put in place by SmartCutEngine's OWN
    // delete-then-move (MoveIntoPlace), NOT by SplitEngine.ReplaceOriginalInPlace. So even when the
    // destination IS the input, the I42 atomic-with-a-backup contract does not apply here and no
    // `.vsj-original` sidecar is left behind.
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task CutAsync_PutsTheFileInPlace_WithNoBackup_EvenWhenTheDestinationIsTheInput()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-smartinplace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "the-original-bytes");

        try
        {
            var runner = new RecordingFakeRunner();
            var engine = new SmartCutEngine(runner, new StreamedFakeProbe(H264Aac(), Grid4s()));

            // Destination == input: the cut file takes the original's place.
            var result = await engine.CutAsync(input, TimeSpan.FromSeconds(5), null, outputPath: input);

            result.FellBack.Should().BeFalse();
            result.OutputPath.Should().Be(input);
            File.Exists(input).Should().BeTrue("the produced file took the original's place");
            (await File.ReadAllTextAsync(input)).Should().Be("seg", "the ORIGINAL bytes were replaced by the cut");

            File.Exists(input + ".vsj-original").Should().BeFalse(
                "MoveIntoPlace is a delete-then-move — the SplitEngine backup sidecar is never written here");
            Directory.GetFiles(dir).Select(Path.GetFileName)
                .Should().BeEquivalentTo(new[] { "clip.mp4" }, "no backup, no stray intermediate");
            Directory.GetDirectories(dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
