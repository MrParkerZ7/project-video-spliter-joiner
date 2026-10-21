using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-191 (G-059, SPEC-001 I69) - the row's fraction while an Exact cut runs, against fakes. Each ffmpeg run reports into its own
/// slice of the row, below 1.0: the head encode into <c>[0, w)</c>, the concat into <c>[w, 1)</c>, with <c>w = h·k / (h·k + c)</c>
/// from <see cref="SmartCutCost"/>; a <c>FullReencode</c> reports its one run across <c>[0, 1)</c>. The fraction never goes
/// backwards, a run that reports nothing still ends at its slice's end, and 1.0 is reported once, after the output is in place -
/// never by a cut that fails, is cancelled or falls back. The two ffprobe reads of T-190 (the frame snap before the head, the
/// head's packet count after it) have no slice of their own: they sit on the slice boundaries, and these tests pin where.
///
/// <para>The shipped engine started every run with null progress; the two tests that fail on it are
/// <see cref="SmartCutHeadSeekAndProgressReproTests"/>. The wall clock is never asserted (T-137).</para>
/// </summary>
public sealed class SmartCutProgressTests : IDisposable
{
    private readonly string _dir;

    public SmartCutProgressTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-t191p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static MediaInfo Info() => new(
        S(60),
        "mov,mp4,m4a,3gp,3g2,mj2",
        new[] { new StreamInfo(0, "h264", "video", 1920, 1080, "yuv420p", null, null, "1/30") },
        new[] { new StreamInfo(1, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

    /// <summary>A keyframe every 2 s over the 60 s source.</summary>
    private static IReadOnlyList<TimeSpan> Grid2s() => Enumerable.Range(0, 31).Select(i => S(i * 2)).ToList();

    /// <summary>Keyframes only up to 8 s of the 60 s source: a request after them has nothing to copy.</summary>
    private static IReadOnlyList<TimeSpan> ShortGrid() => new[] { S(0), S(4), S(8) };

    private string Placeholder(string name = "clip.mp4")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "the-original-bytes");
        return p;
    }

    private string Dest => Path.Combine(_dir, "cut.mp4");

    private static double W(double start, double? end, IReadOnlyList<TimeSpan> keyframes) =>
        SmartCutCost.Of(SmartCutPlanner.Plan(S(start), end is { } e ? S(e) : null, keyframes), S(60)).ReencodeShare;

    /// <summary>A probe without the frame questions (the engine then cuts at the request and checks no head).</summary>
    private sealed class GridProbe : IMediaProbe
    {
        private readonly IReadOnlyList<TimeSpan> _keyframes;

        public GridProbe(IReadOnlyList<TimeSpan> keyframes) => _keyframes = keyframes;

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(ProbeResult.Success(Info()));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(_keyframes);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
            throw new NotSupportedException("the smart cutter plans against the raw keyframes and never snaps");

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    /// <summary>A probe WITH the frame questions that logs when each is asked (into the same list the runner and the spy log into).</summary>
    private sealed class LoggingProbe : IMediaProbe, IVideoFrameProbe
    {
        private readonly List<string> _events;
        private readonly int? _headPackets;
        private readonly int? _sourceFrames;

        public LoggingProbe(List<string> events, int? headPackets = 45, int? sourceFrames = -1)
        {
            _events = events;
            _headPackets = headPackets;
            _sourceFrames = sourceFrames == -1 ? headPackets : sourceFrames; // by default the source holds as many frames as the head
        }

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(ProbeResult.Success(Info()));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(Grid2s());

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
            throw new NotSupportedException("the smart cutter plans against the raw keyframes and never snaps");

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;

        public Task<TimeSpan?> FirstVideoFrameAtOrAfterAsync(string path, TimeSpan time, CancellationToken ct = default)
        {
            _events.Add("snap");
            return Task.FromResult<TimeSpan?>(time); // the request is on a frame
        }

        public Task<IReadOnlyList<TimeSpan>?> GetVideoFrameTimesAsync(string path, TimeSpan from, TimeSpan to, CancellationToken ct = default)
        {
            // The engine reads its head ("head.<ext>") for its frames, then the source over the span the head was cut from.
            var isHead = Path.GetFileName(path).StartsWith("head.", StringComparison.Ordinal);
            _events.Add(isHead ? "count" : "source");
            IReadOnlyList<TimeSpan>? times = (isHead ? _headPackets : _sourceFrames) is { } n
                ? Enumerable.Range(0, n).Select(i => (isHead ? TimeSpan.Zero : from) + S(i / 30.0)).ToList()
                : null;
            return Task.FromResult(times);
        }

        // T-223: every keyframe of the scripted source is an IDR (a closed GOP), so the question never changes the events logged.
        public Task<bool?> IsIdrKeyframeAsync(string path, TimeSpan keyframeTime, CancellationToken ct = default) =>
            Task.FromResult<bool?>(true);
    }

    private enum RunEnd
    {
        Succeeds,
        ExitsNonZero,
        Cancels,
    }

    /// <summary>What one scripted run reports through its progress (the fractions of <c>time=</c> lines) and how it ends.</summary>
    private sealed record RunScript(double[] Fractions, RunEnd End = RunEnd.Succeeds);

    /// <summary>
    /// Acts like <see cref="FfmpegRunner"/> for progress: it reports the scripted fractions through the progress it is handed (when it
    /// is handed one), then ends as scripted, writing the output file a successful run leaves.
    /// </summary>
    private sealed class ScriptedRunner : IFfmpegRunner
    {
        private readonly RunScript[] _scripts;
        private readonly List<string>? _events;

        public ScriptedRunner(List<string>? events = null, params RunScript[] scripts)
        {
            _events = events;
            _scripts = scripts;
        }

        public List<IReadOnlyList<string>> Commands { get; } = new();

        public List<TimeSpan?> Totals { get; } = new();

        public List<bool> HandedProgress { get; } = new();

        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args, TimeSpan? totalDuration = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var index = Commands.Count;
            var tokens = args.ToList().ToList();
            Commands.Add(tokens);
            Totals.Add(totalDuration);
            HandedProgress.Add(progress is not null);
            _events?.Add(tokens.Contains("concat") ? "run:concat" : "run:head");

            var script = index < _scripts.Length ? _scripts[index] : new RunScript(Array.Empty<double>());
            foreach (var fraction in script.Fractions)
            {
                progress?.Report(fraction);
            }

            switch (script.End)
            {
                case RunEnd.Cancels:
                    throw new OperationCanceledException("cancelled mid-run");
                case RunEnd.ExitsNonZero:
                    return Task.FromResult(new FfmpegResult(1, new List<string> { "boom" }.AsReadOnly()));
                default:
                    File.WriteAllText(tokens[^1], "seg");
                    return Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));
            }
        }
    }

    /// <summary>Records each reported fraction in order, and what was on disk at that moment.</summary>
    private sealed class ProgressSpy : IProgress<double>
    {
        private readonly List<string>? _events;
        private readonly Action<double>? _onReport;

        public ProgressSpy(List<string>? events = null, Action<double>? onReport = null)
        {
            _events = events;
            _onReport = onReport;
        }

        public List<double> Values { get; } = new();

        public void Report(double value)
        {
            Values.Add(value);
            _events?.Add("p:" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            _onReport?.Invoke(value);
        }
    }

    private Task<SmartCutResult> CutAsync(
        ScriptedRunner runner, double start, double? end, IProgress<double>? progress, IMediaProbe? probe = null,
        IReadOnlyList<TimeSpan>? keyframes = null, CancellationToken ct = default) =>
        new SmartCutEngine(runner, probe ?? new GridProbe(keyframes ?? Grid2s()))
            .CutAsync(Placeholder(), S(start), end is { } e ? S(e) : null, Dest, progress, ct);

    // ---- What each run is told ---------------------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I69 - the head run is told HeadEnd - Start (ffmpeg's time= counts the head's output), the concat run the
    // kept duration (End - Start, or Duration - Start for an open-ended cut), so FfmpegRunner turns time= into a fraction.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(null, 1.0, 55.0)]   // 5 s on a 2 s grid: head [5, 6), kept [5, 60)
    [InlineData(22.0, 1.0, 17.0)]   // kept middle [5, 22)
    public async Task TheRuns_AreToldTheirLength(double? end, double headSeconds, double keptSeconds)
    {
        var runner = new ScriptedRunner();

        await CutAsync(runner, 5, end, new ProgressSpy());

        runner.Totals.Should().HaveCount(2);
        runner.Totals[0].Should().Be(S(headSeconds), "the head run's total is HeadEnd - Start");
        runner.Totals[1].Should().Be(S(keptSeconds), "the concat run's total is the kept duration");
    }

    // ---- The slices ----------------------------------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I69 - the head's fractions land in [0, w], the concat's in [w, 1) beginning where the head's own part of
    // the concat's time ends, nothing reaches 1.0 until the output is in place, and the whole sequence is strictly increasing.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task EachRunReportsIntoItsOwnSlice_StrictlyIncreasing_AndTheRowReachesOneOnce()
    {
        var w = W(5, null, Grid2s());
        var from = 1.0 / 55.0; // the head's 1 s of the concat's 55 s
        double M(double f) => w + ((1 - w) * (f - from) / (1 - from));
        var runner = new ScriptedRunner(null,
            new RunScript(new[] { 0.1, 0.3, 0.6, 1.0 }),
            new RunScript(new[] { 0.2, 0.5, 0.9, 1.0 }));
        var spy = new ProgressSpy();

        var result = await CutAsync(runner, 5, null, spy);

        result.FellBack.Should().BeFalse();
        spy.Values.Should().HaveCount(9).And.BeInAscendingOrder().And.OnlyHaveUniqueItems("strictly increasing");
        var expected = new[]
        {
            0.1 * w, 0.3 * w, 0.6 * w, w,           // the head: three values below w, then w itself
            M(0.2), M(0.5), M(0.9),                  // the concat: from w toward 1
            SmartCutEngine.MaxRunProgress,           // the concat's last time= is capped below 1
            1.0,                                     // the output is in place
        };
        for (var i = 0; i < expected.Length; i++)
        {
            spy.Values[i].Should().BeApproximately(expected[i], 1e-9, $"value #{i}");
        }

        spy.Values.Count(v => v < w).Should().BeGreaterThanOrEqualTo(3, "at least three values strictly below w during the head");
        spy.Values.Count(v => v >= 1.0).Should().Be(1, "1.0 is reported exactly once");
        spy.Values.Take(spy.Values.Count - 1).Should().OnlyContain(v => v < 1.0, "no run reports 1.0");
        spy.Values.Should().NotContain(0.5, "the fixed 0.5 / 0.8 steps are gone").And.NotContain(0.8);
    }

    // serves-spec: SPEC-001#I69 - a FullReencode (no keyframe to copy from) reports its one run across [0, 1), then 1.0. Both ends:
    // a range with an end, and one to the end of the file (the head's length is then Duration - Start).
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(9.0, 10.0)]
    [InlineData(59.0, null)]
    public async Task AFullReencode_ReportsAcrossTheWholeRow_ThenOne(double start, double? end)
    {
        var runner = new ScriptedRunner(null, new RunScript(new[] { 0.25, 0.5, 0.75, 1.0 }));
        var spy = new ProgressSpy();

        // Keyframes at 0, 4 and 8 s only: nothing after 8 s can be copied from, so the whole range is re-encoded.
        var result = await CutAsync(runner, start, end, spy, keyframes: ShortGrid());

        result.Strategy.Should().Be(SmartCutStrategy.FullReencode);
        runner.Commands.Should().ContainSingle("a full re-encode is one run");
        runner.Totals[0].Should().Be(S(1), "the range is one second: [9, 10) or [59, 60)");
        spy.Values.Should().Equal(new[] { 0.25, 0.5, 0.75, SmartCutEngine.MaxRunProgress, 1.0 },
            "the run covers the whole row, its last time= is capped below 1, and 1.0 follows the move into place");
    }

    // serves-spec: SPEC-001#I69 - a run that reports nothing (very short, or ffmpeg printed no time=) still ends at its slice's end.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task ARunThatReportsNothing_StillEndsAtItsSlicesEnd()
    {
        var w = W(5, null, Grid2s());
        var spy = new ProgressSpy();

        await CutAsync(new ScriptedRunner(), 5, null, spy);

        spy.Values.Should().HaveCount(3);
        spy.Values[0].Should().BeApproximately(w, 1e-12, "the head's slice ends at w");
        spy.Values[1].Should().Be(SmartCutEngine.MaxRunProgress, "the concat's slice ends just below 1");
        spy.Values[2].Should().Be(1.0);

        var full = new ProgressSpy();
        await CutAsync(new ScriptedRunner(), 9, 10, full, keyframes: ShortGrid());
        full.Values.Should().Equal(new[] { SmartCutEngine.MaxRunProgress, 1.0 }, "a full re-encode's one slice is the whole row");
    }

    // serves-spec: SPEC-001#I69 - the head's slice ends at the cost function's share: the value the head reports at its last time=
    // IS SmartCutCost.Of(plan).ReencodeShare, so the row bar and the batch bar weigh the same cut the same way.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(5.0, null)]
    [InlineData(5.0, 22.0)]
    [InlineData(5.5, 7.0)]
    [InlineData(3.001, 59.0)]
    public async Task TheHeadSlice_EndsAtTheCostFunctionsShare(double start, double? end)
    {
        var plan = SmartCutPlanner.Plan(S(start), end is { } e ? S(e) : null, Grid2s());
        plan.Strategy.Should().Be(SmartCutStrategy.HeadReencode, "precondition");
        var spy = new ProgressSpy();

        await CutAsync(new ScriptedRunner(null, new RunScript(new[] { 1.0 })), start, end, spy);

        var cost = SmartCutCost.Of(plan, S(60));
        spy.Values[0].Should().BeApproximately(cost.ReencodedSeconds * SmartCutEngine.EncodeCostFactor / cost.Total, 1e-12);
        spy.Values[0].Should().BeApproximately(cost.ReencodeShare, 1e-12);
        spy.Values[0].Should().BeInRange(0.0, 1.0).And.BeLessThan(SmartCutEngine.MaxRunProgress);
    }

    // serves-spec: SPEC-001#I69 - the concat's time= starts at the head's length (the concat output begins with the head), which the
    // head's slice already reported: the concat fraction that is exactly the head's own part moves nothing.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task TheConcatsSlice_BeginsWhereTheHeadsOwnPartOfItsTimeEnds()
    {
        var w = W(5, null, Grid2s());
        var from = 1.0 / 55.0;
        var spy = new ProgressSpy();

        await CutAsync(
            new ScriptedRunner(null, new RunScript(Array.Empty<double>()), new RunScript(new[] { from, from + ((1 - from) / 2) })),
            5, null, spy);

        spy.Values.Should().HaveCount(4, "w (the head's end), the midpoint, the capped end, 1.0 - the fraction `from` moved nothing");
        spy.Values[0].Should().BeApproximately(w, 1e-12);
        spy.Values[1].Should().BeApproximately(w + ((1 - w) / 2), 1e-9, "halfway through the concat's own time is halfway through its slice");
    }

    // serves-spec: SPEC-001#I69 - the fraction never goes backwards whatever a run reports (ffmpeg's time= can rewind on a stream
    // with a timestamp jump), and a repeated value is not reported again.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task ARunThatRewinds_NeverMakesTheFractionGoBack()
    {
        var w = W(5, null, Grid2s());
        var spy = new ProgressSpy();

        await CutAsync(new ScriptedRunner(null, new RunScript(new[] { 0.5, 0.3, 0.7, 0.7, 0.6, 1.0 })), 5, null, spy);

        spy.Values.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        var expected = new[] { 0.5 * w, 0.7 * w, w, SmartCutEngine.MaxRunProgress, 1.0 };
        spy.Values.Should().HaveCount(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            spy.Values[i].Should().BeApproximately(expected[i], 1e-9, $"value #{i}: the 0.3, the repeated 0.7 and the 0.6 are not reported");
        }
    }

    // serves-spec: SPEC-001#I69 - nobody listens: the runner is handed no progress at all (the totals are still passed), and nothing throws.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task WithoutAProgressSink_TheRunnerIsHandedNoProgress()
    {
        var runner = new ScriptedRunner(null, new RunScript(new[] { 0.5, 1.0 }), new RunScript(new[] { 0.5, 1.0 }));

        var result = await CutAsync(runner, 5, null, progress: null);

        result.FellBack.Should().BeFalse();
        runner.HandedProgress.Should().Equal(false, false);
    }

    // ---- 1.0 only with the output in place -----------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I69 - at the first 1.0 the output is already at its destination, and at no earlier value it is.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(5.0, null)]   // a head and a concat
    [InlineData(9.0, 10.0)]   // a full re-encode: the head IS the output
    public async Task OneIsReportedOnlyWhenTheOutputIsInPlace(double start, double? end)
    {
        var existedAtOne = new List<bool>();
        var existedBefore = false;
        var spy = new ProgressSpy(null, v =>
        {
            if (v >= 1.0)
            {
                existedAtOne.Add(File.Exists(Dest));
            }
            else
            {
                existedBefore |= File.Exists(Dest);
            }
        });

        await CutAsync(new ScriptedRunner(), start, end, spy, keyframes: start > 8 ? ShortGrid() : Grid2s());

        existedAtOne.Should().Equal(true);
        existedBefore.Should().BeFalse("no value below 1.0 is reported with a file at the destination");
    }

    public enum Failing
    {
        TheHead,
        TheConcat,
    }

    // serves-spec: SPEC-001#I69 / SPEC-001#I47 - a run that fails: the cut throws, no 1.0 was ever reported, nothing is left.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(Failing.TheHead, "re-encoding the leading fragment")]
    [InlineData(Failing.TheConcat, "joining the fragments")]
    public async Task AFailedRun_ThrowsAndNeverReportsOne(Failing which, string message)
    {
        var scripts = which == Failing.TheHead
            ? new[] { new RunScript(new[] { 0.4, 0.8 }, RunEnd.ExitsNonZero) }
            : new[] { new RunScript(new[] { 1.0 }), new RunScript(new[] { 0.5, 1.0 }, RunEnd.ExitsNonZero) };
        var spy = new ProgressSpy();

        var act = () => CutAsync(new ScriptedRunner(null, scripts), 5, null, spy);

        (await act.Should().ThrowAsync<SplitException>()).Which.Message.Should().Contain(message);
        spy.Values.Should().NotBeEmpty("the runs that ran did move the bar").And.OnlyContain(v => v < 1.0);
        File.Exists(Dest).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
    }

    // serves-spec: SPEC-001#I69 / SPEC-001#I47 - a cancel lands mid-run, in the head or in the concat: it propagates, the temp dir is
    // swept, no destination file exists and 1.0 was never reported.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData(Failing.TheHead)]
    [InlineData(Failing.TheConcat)]
    public async Task ACancelledRun_SweepsTheTempDir_AndReportsNoOne(Failing which)
    {
        var w = W(5, null, Grid2s());
        var scripts = which == Failing.TheHead
            ? new[] { new RunScript(new[] { 0.2, 0.5 }, RunEnd.Cancels) }
            : new[] { new RunScript(new[] { 1.0 }), new RunScript(new[] { 0.5 }, RunEnd.Cancels) };
        var spy = new ProgressSpy();

        var act = () => CutAsync(new ScriptedRunner(null, scripts), 5, null, spy);

        await act.Should().ThrowAsync<OperationCanceledException>();
        spy.Values.Should().OnlyContain(v => v < 1.0);
        if (which == Failing.TheHead)
        {
            spy.Values.Should().HaveCount(2).And.OnlyContain(v => v <= w, "only the head's slice moved");
        }

        File.Exists(Dest).Should().BeFalse("a cancelled cut leaves no destination file");
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty("the temp dir is swept in a finally");
    }

    // ---- The ffprobe reads around the head -------------------------------------------------------------------------------------

    // serves-spec: SPEC-001#I69 / SPEC-001#I43 / SPEC-001#I46 - the frame snap is read BEFORE the head runs and the row has reported
    // nothing yet; the head's packet count is read AFTER it, before the concat starts, with the row inside the head's slice (at most w);
    // the first value above w is the concat's. The reads carry no slice and no weight of their own.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task TheTwoFfprobeReads_SitOnTheSliceBoundaries()
    {
        var w = W(5, null, Grid2s());
        var events = new List<string>();
        var runner = new ScriptedRunner(events, new RunScript(new[] { 0.5, 1.0 }), new RunScript(new[] { 0.5, 1.0 }));
        var spy = new ProgressSpy(events);

        await CutAsync(runner, 5, null, spy, new LoggingProbe(events));

        var snap = events.IndexOf("snap");
        var head = events.IndexOf("run:head");
        var count = events.IndexOf("count");
        var concat = events.IndexOf("run:concat");
        snap.Should().BeGreaterThanOrEqualTo(0);
        snap.Should().BeLessThan(head, "the frame snap is read before the head is built");
        events.Take(head).Should().NotContain(e => e.StartsWith("p:", StringComparison.Ordinal), "the row has reported nothing while the snap is read");
        count.Should().BeGreaterThan(head).And.BeLessThan(concat, "the head's frames are read after the head and before the concat");
        events.IndexOf("source").Should().BeGreaterThan(count).And.BeLessThan(concat, "then the source's frames over the span the head was cut from, still before the concat");

        double Value(string e) => double.Parse(e[2..], System.Globalization.CultureInfo.InvariantCulture);
        events.Take(count).Where(e => e.StartsWith("p:", StringComparison.Ordinal)).Select(Value)
            .Should().OnlyContain(v => v <= w + 1e-12, "until the head is checked the row is inside the head's slice");
        events.Skip(concat).Where(e => e.StartsWith("p:", StringComparison.Ordinal)).Select(Value)
            .Should().OnlyContain(v => v >= w - 1e-12, "the concat's values begin at w");
        events.Skip(count).Where(e => e.StartsWith("p:", StringComparison.Ordinal)).Select(Value)
            .Where(v => v > w + 1e-12).Should().NotBeEmpty();
        events.IndexOf("p:" + SmartCutEngine.MaxRunProgress.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
            .Should().BeGreaterThan(concat);
    }

    // serves-spec: SPEC-001#I69 / SPEC-001#I46 - a head that holds no picture is a fall back AFTER the head ran: the bar moved inside
    // the head's slice, then the cut hands back - it never reports 1.0, never starts the concat and leaves nothing behind.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task AHeadWithNoPicture_FallsBackAfterTheHeadsProgress_AndReportsNoOne()
    {
        var w = W(5, null, Grid2s());
        var events = new List<string>();
        var runner = new ScriptedRunner(events, new RunScript(new[] { 0.5, 1.0 }));
        var spy = new ProgressSpy(events);

        var result = await CutAsync(runner, 5, null, spy, new LoggingProbe(events, headPackets: 0));

        result.FellBack.Should().BeTrue();
        runner.Commands.Should().ContainSingle("no concat after a head without a picture");
        spy.Values.Should().NotBeEmpty().And.OnlyContain(v => v <= w + 1e-12, "the bar only moved inside the head's slice");
        spy.Values.Should().NotContain(1.0);
        File.Exists(Dest).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty();
    }

    // serves-spec: SPEC-001#I69 / SPEC-001#I46 - the same for a head that is not the source's frames (T-191): the bar moved inside the
    // head's slice, then the cut hands back - no 1.0, no concat, nothing left behind.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task AHeadThatIsNotTheSourcesFrames_FallsBackAfterTheHeadsProgress_AndReportsNoOne()
    {
        var w = W(5, null, Grid2s());
        var events = new List<string>();
        var runner = new ScriptedRunner(events, new RunScript(new[] { 0.5, 1.0 }));
        var spy = new ProgressSpy(events);

        var result = await CutAsync(runner, 5, null, spy, new LoggingProbe(events, headPackets: 29, sourceFrames: 30));

        result.FellBack.Should().BeTrue();
        runner.Commands.Should().ContainSingle("no concat after a head that is not the source's frames");
        spy.Values.Should().NotBeEmpty().And.OnlyContain(v => v <= w + 1e-12, "the bar only moved inside the head's slice");
        File.Exists(Dest).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty();
    }

    // ---- The Bulk row ----------------------------------------------------------------------------------------------------------

    // serves-spec: SPEC-002#I9 / SPEC-001#I69 - the row's fraction in a Bulk batch never goes backwards even when the Exact cut falls
    // back AFTER its head ran: the lossless pass that takes the row over reports from 0.5, and the row stays where the head left it.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task ABulkRow_WhoseExactCutFallsBackAfterItsHead_NeverShowsAFractionGoingBack()
    {
        var events = new List<string>();
        var smart = new SmartCutEngine(
            new ScriptedRunner(events, new RunScript(new[] { 0.5, 1.0 })), new LoggingProbe(events, headPackets: 0));
        var engine = new BulkTrimEngine(
            new FakeSplitEngine(), new FakeRequestBuilder(), new FakeDiskSpaceProbe(long.MaxValue), smart, null);
        var item = new BulkTrimItem(Placeholder("a.mp4"), S(5), null, Path.Combine(_dir, "a_trimmed.mp4"));
        var rows = new List<BulkTrimProgress>();

        var result = await engine.RunAsync(
            new[] { item }, new BulkTrimOptions(Precision: CutPrecision.Exact), new SyncProgress<BulkTrimProgress>(rows.Add));

        result.Items.Single().Outcome.Should().Be(ItemOutcome.Done, "the lossless cut took the row over");
        var fractions = rows.Where(r => r.Phase == BulkTrimPhase.Item && r.ItemIndex == 0).Select(r => r.ItemFraction).ToList();
        fractions.Should().BeInAscendingOrder("the row's fraction never goes backwards: the head's slice, then the lossless pass");
        fractions[^1].Should().Be(1.0);
        fractions.Count(f => f > 0 && f < 1).Should().BeGreaterThan(0, "the head's slice moved the row before the fall back");
    }

    // serves-spec: SPEC-001#I69 - a Bulk row cut exactly: the row's fraction rises through the head's slice and the concat's to 1.0.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task ABulkRow_CutExactly_RisesThroughBothSlices_ToOne()
    {
        var smart = new SmartCutEngine(
            new ScriptedRunner(null, new RunScript(new[] { 0.25, 0.5, 0.75, 1.0 }), new RunScript(new[] { 0.25, 0.5, 0.75, 1.0 })),
            new GridProbe(Grid2s()));
        var engine = new BulkTrimEngine(
            new FakeSplitEngine(), new FakeRequestBuilder(), new FakeDiskSpaceProbe(long.MaxValue), smart, null);
        var item = new BulkTrimItem(Placeholder("a.mp4"), S(5), null, Path.Combine(_dir, "a_trimmed.mp4"));
        var rows = new List<BulkTrimProgress>();

        var result = await engine.RunAsync(
            new[] { item }, new BulkTrimOptions(Precision: CutPrecision.Exact), new SyncProgress<BulkTrimProgress>(rows.Add));

        result.Items.Single().Outcome.Should().Be(ItemOutcome.Done);
        var fractions = rows.Where(r => r.Phase == BulkTrimPhase.Item && r.ItemIndex == 0).Select(r => r.ItemFraction).ToList();
        fractions.Should().BeInAscendingOrder();
        fractions.Count(f => f is > 0 and < 1).Should().BeGreaterThanOrEqualTo(7, "four values in the head's slice, then the concat's");
        fractions[^1].Should().Be(1.0);
    }

    /// <summary>A lossless engine that reports a scripted row fraction sequence (a NaN among it) and writes the output.</summary>
    private sealed class ScriptedFractionSplitEngine : ISplitEngine
    {
        private readonly double[] _fractions;

        public ScriptedFractionSplitEngine(params double[] fractions) => _fractions = fractions;

        public async Task<SplitResult> SplitAsync(
            SplitRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<OperationStatus>? status = null,
            IProgress<PartProgress>? partProgress = null)
        {
            foreach (var f in _fractions)
            {
                progress?.Report(f);
            }

            var outputPath = Path.Combine(req.OutputDir, req.NamingPattern);
            Directory.CreateDirectory(req.OutputDir);
            await File.WriteAllTextAsync(outputPath, "trimmed", ct).ConfigureAwait(false);
            return new SplitResult(
                new[] { new SplitSegment(outputPath, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) }, Array.Empty<string>());
        }
    }

    // serves-spec: SPEC-002#I31 - a NaN fraction says nothing new: the row stays where it was. (Clamp01(NaN) is NaN and
    // Math.Max(NaN, x) is NaN, so without the guard one NaN froze the row's high-water mark at NaN for the rest of the row.)
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ABulkRow_IgnoresANaNFraction_AndKeepsItsHighWaterMark()
    {
        var engine = new BulkTrimEngine(
            new ScriptedFractionSplitEngine(0.4, double.NaN, 0.7, double.NaN, 1.0),
            new FakeRequestBuilder(), new FakeDiskSpaceProbe(long.MaxValue));
        var item = new BulkTrimItem(Placeholder("a.mp4"), S(5), null, Path.Combine(_dir, "a_trimmed.mp4"));
        var rows = new List<BulkTrimProgress>();

        await engine.RunAsync(new[] { item }, new BulkTrimOptions(), new SyncProgress<BulkTrimProgress>(rows.Add));

        var fractions = rows.Where(r => r.Phase == BulkTrimPhase.Item && r.ItemIndex == 0).Select(r => r.ItemFraction).ToList();
        fractions.Should().OnlyContain(f => !double.IsNaN(f), "no NaN reaches the row");
        fractions.Should().BeInAscendingOrder();
        fractions.Should().Contain(new[] { 0.4, 0.7, 1.0 }, "the real values still move the row");
        rows.Where(r => r.Phase == BulkTrimPhase.Item).Select(r => r.OverallFraction).Should().OnlyContain(f => !double.IsNaN(f));
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
