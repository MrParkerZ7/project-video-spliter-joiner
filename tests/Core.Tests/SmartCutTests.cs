using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
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
        // T-191 (recorded contract change; this assertion used to demand an OUTPUT seek, -ss AFTER -i): the INPUT seek (-ss
        // BEFORE -i, ffmpeg's accurate_seek) starts the decode at the keyframe before the start and drops frames until it, so the
        // cut is just as frame-exact and the decode no longer runs from the start of the file.
        var list = args.ToList();
        list.FindIndex(t => t == "-ss").Should().BeLessThan(list.FindIndex(t => t.EndsWith("clip.mp4")),
            "the seek must come BEFORE the input: an input seek decodes from the keyframe before the start, not from the file's start");
    }

    // SPEC-001#I44 (T-191) - the head's argument shape: ONE -ss, before -i, and -t (an output duration) after it, no
    // -noaccurate_seek; every other token is the one the output-seek head had, in the same order.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void HeadReencode_SeeksBeforeItsInput_AndOtherwiseKeepsEveryToken()
    {
        var tokens = SmartCutArgsBuilder.HeadReencode(
            @"C:\v\clip.mp4", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8),
            H264Aac(), "libx264", "aac", @"C:\v\head.mp4").ToList().ToList();

        tokens.Count(t => t == "-ss").Should().Be(1, "one seek, and it is on the input");
        tokens.Should().NotContain("-noaccurate_seek", "accurate_seek (ffmpeg's default) is what makes the start frame-exact");
        var ss = tokens.IndexOf("-ss");
        var input = tokens.IndexOf("-i");
        tokens[ss + 1].Should().Be("5");
        ss.Should().Be(input - 2, "-ss <start> immediately precedes -i <input>");
        tokens[input + 1].Should().Be(@"C:\v\clip.mp4");
        tokens.IndexOf("-t").Should().BeGreaterThan(input + 1, "-t stays an OUTPUT duration, after the input");
        tokens[tokens.IndexOf("-t") + 1].Should().Be("3");

        // The shipped output-seek head, token for token: -i <in> -ss <start> -t <dur> ... Moving the pair is the whole change.
        var before = tokens.ToList();
        before.RemoveRange(ss, 2);
        before.Insert(before.IndexOf("-i") + 2, "-ss");
        before.Insert(before.IndexOf("-ss") + 1, "5");
        before.Skip(before.IndexOf("-y")).Should().Equal(new[]
        {
            "-y", "-i", @"C:\v\clip.mp4", "-ss", "5", "-t", "3", "-map", "0",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-s", "1920x1080",
            "-c:a", "aac", "-ar", "48000", "-ac", "2", @"C:\v\head.mp4",
        }, "the codec, pix_fmt, size, sample-rate and channel tokens are unchanged");
    }

    // SPEC-001#I44 (T-190) - the kept part is no longer copied into a tail file: the concat reads the SOURCE itself from the
    // boundary keyframe. So the concat is the only copy run, it is a pure stream copy with no encoder token, and the second
    // `file` entry of its list is the source. (This replaces TailCopy_IsStillAPureStreamCopy_NoEncoderLeaksIn: TailCopy is gone.)
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TheJointConcat_IsAPureStreamCopy_AndItsSecondFileIsTheSource()
    {
        var concat = JoinArgsBuilder.ConcatCopy(@"C:\v\concat.txt", @"C:\v\final.mp4").ToList();

        concat.Should().Contain("copy");
        JoinArgsBuilder.SatisfiesCopyInvariant(concat).Should().BeTrue(
            "the concat that joins the head to the source carries no encoder token");

        var list = SmartCutArgsBuilder.RenderJointList(
            @"C:\v\head.mp4", TimeSpan.FromSeconds(3), @"C:\v\clip.mp4", TimeSpan.FromSeconds(8), null);
        var files = list.Split('\n').Where(l => l.StartsWith("file ", StringComparison.Ordinal)).ToList();

        files.Should().HaveCount(2);
        files[1].Should().Be("file '" + Path.GetFullPath(@"C:\v\clip.mp4") + "'", "the kept part is the source itself, from the keyframe");
        list.Should().NotContain("tail", "there is no tail file any more");
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

    // SPEC-001#I48 (T-190: two runs, was three) - a HeadReencode is EXACTLY two ffmpeg invocations (head encode, then the
    // concat that reads the SOURCE from the boundary keyframe), never one per GOP and never a tail copy. Each run is pinned
    // by identity so a re-ordering or an extra pass is caught, not just the count.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_HeadReencode_RunsExactlyTwoFfmpegInvocations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsj-smarthead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "placeholder");
        var outputPath = Path.Combine(dir, "cut.mp4");

        try
        {
            var runner = new ListCapturingRunner();
            var progress = new RecordedProgress();
            var engine = new SmartCutEngine(runner, new StreamedFakeProbe(H264Aac(), Grid4s()));

            var result = await engine.CutAsync(input, TimeSpan.FromSeconds(5), null, outputPath, progress);

            runner.Commands.Should().HaveCount(2,
                "a head re-encode is the head encode and the concat and NOTHING else - no tail copy, never one pass per GOP");

            // 1. The head fragment: re-encoded, starting at EXACTLY the requested 5s, bounded to 3s.
            var head = runner.Commands[0].ToList();
            head.Should().Contain("libx264");
            var headSs = head.IndexOf("-ss");
            headSs.Should().BeGreaterThanOrEqualTo(0);
            head[headSs + 1].Should().Be("5", "the head starts at exactly the requested time");
            var headT = head.IndexOf("-t");
            headT.Should().BeGreaterThanOrEqualTo(0);
            head[headT + 1].Should().Be("3", "the re-encode is bounded by one GOP (5s to the 8s keyframe)");

            // 2. The concat: a stream copy (never a second encode) of [head, the source from the boundary keyframe].
            var concat = runner.Commands[1].ToList();
            concat.Should().Contain("concat");
            JoinArgsBuilder.SatisfiesCopyInvariant(concat).Should().BeTrue();
            runner.Commands.SelectMany(c => c).Should().NotContain(t => t.Contains("tail.", StringComparison.Ordinal),
                "no run names a tail output: the kept part is written once, into the final");

            runner.Lists.Should().ContainSingle();
            var list = runner.Lists[0].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            list.Should().HaveCount(4, "head, its duration, the source, its inpoint - and no outpoint for an open-ended cut");
            list[0].Should().StartWith("file '").And.EndWith("head.mp4'");
            list[1].Should().Be("duration 3", "HeadEnd - Start fixes where the source leg starts");
            list[2].Should().Be("file '" + Path.GetFullPath(input) + "'", "the second entry is the source");
            list[3].Should().Be("inpoint 8", "the kept part begins at the boundary keyframe");

            // T-191 (recorded contract change; this used to demand the interim steps 0.5 then 1.0): the fake runner prints no time=, so
            // each run still ends at the end of its slice - the head at w = h·k / (h·k + c) from SmartCutCost (3 s re-encoded, 52 s
            // copied), the concat just below 1 - and 1.0 follows the move into place. The fixed 0.5 step is gone.
            var w = SmartCutCost.Of(SmartCutPlanner.Plan(TimeSpan.FromSeconds(5), null, Grid4s()), TimeSpan.FromSeconds(60)).ReencodeShare;
            progress.Values.Should().HaveCount(3).And.BeInAscendingOrder();
            progress.Values[0].Should().BeApproximately(w, 1e-12, "the head's slice ends at its share of the cost");
            progress.Values[1].Should().Be(SmartCutEngine.MaxRunProgress, "the concat's slice ends just below 1");
            progress.Values[2].Should().Be(1.0, "1.0 follows, once, after the output is in place");
            progress.Values.Should().NotContain(0.5);

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

    /// <summary>Records each reported fraction synchronously, in order (<see cref="Progress{T}"/> would post to a context).</summary>
    private sealed class RecordedProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
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
