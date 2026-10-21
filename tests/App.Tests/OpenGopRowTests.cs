using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-223 (G-059, SPEC-011 I164) - a Bulk Cut row in Exact mode whose intro lands behind a keyframe that is not an IDR (an open-GOP
/// H.264 file) is honest BEFORE Run, like the HEVC row of T-189: it asks the SAME question the engine falls back on
/// (<see cref="IVideoFrameProbe.IsIdrKeyframeAsync"/>, about the keyframe the head would end on), and while the answer is "not an IDR"
/// it behaves exactly as in Lossless - snap note, snapped cut, snapped chip - and states the reason in the run's own words. The
/// question follows the intro (asked once per keyframe), is not asked where there is no joint, nor for a source that is not H.264,
/// and a row whose probe cannot answer is unchanged.
/// </summary>
public sealed class OpenGopRowTests : IDisposable
{
    private const string Reason = SmartCutEngine.NotAnIdrKeyframeReason;

    private static readonly string Note = $"{BulkTrimEngine.ExactFallbackPrefix} ({Reason}) - cut snapped to the nearest keyframe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-t223-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string MakeVideo(string name)
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "video bytes");
        return p;
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static Task Immediate(TimeSpan _, CancellationToken ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

    /// <summary>The Bulk fake probe plus the frame question, scripted: the answer per keyframe time (true when not listed), and an optional gate.</summary>
    private sealed class IdrProbe : IMediaProbe, IVideoFrameProbe
    {
        public BulkFakeProbe Inner { get; } = new();

        /// <summary>The answer for a keyframe time; a time not listed is an IDR (true).</summary>
        public Dictionary<TimeSpan, bool?> Answers { get; } = new();

        /// <summary>When set, the next answers wait for it - the row must read as before until it is released.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public List<TimeSpan> Asked { get; } = new();

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) => Inner.ProbeAsync(path, ct);

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) => Inner.GetKeyframesAsync(path, ct);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) => Inner.SnapToNearestKeyframe(keyframes, requested);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => Inner.AverageGop(keyframes);

        public Task<TimeSpan?> FirstVideoFrameAtOrAfterAsync(string path, TimeSpan time, CancellationToken ct = default) => Task.FromResult<TimeSpan?>(time);

        public Task<IReadOnlyList<TimeSpan>?> GetVideoFrameTimesAsync(string path, TimeSpan from, TimeSpan to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TimeSpan>?>(null);

        public async Task<bool?> IsIdrKeyframeAsync(string path, TimeSpan keyframeTime, CancellationToken ct = default)
        {
            Asked.Add(keyframeTime);
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            return Answers.TryGetValue(keyframeTime, out var answer) ? answer : true;
        }
    }

    private static (BulkCutViewModel Vm, IdrProbe Probe, FakeThumbnailService Thumbs) Build()
    {
        var probe = new IdrProbe();
        var thumbs = new FakeThumbnailService { ThumbnailFactory = (_, time, _) => $"frame-{time.TotalSeconds}.jpg" };
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), thumbs, new FakeSettings(), new FakeBulkTrimEngine(),
            thumbnailDebounce: TimeSpan.FromMilliseconds(1), thumbnailDelay: Immediate);
        vm.LookupFileHolders = _ => Array.Empty<string>();
        return (vm, probe, thumbs);
    }

    private static async Task<BulkItemViewModel> AddAsync(BulkCutViewModel vm, IdrProbe probe, string path, string videoCodec = "h264")
    {
        probe.Inner.SetUniform(path, S(60), 2);
        probe.Inner.SetCodecs(path, videoCodec);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask;
        return row;
    }

    private static void Settle(BulkItemViewModel row) =>
        row.InFlightGrabs.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the grab should finish promptly");

    // serves-spec: SPEC-011#I164 / SPEC-001#I70 - the row of an open-GOP file in Exact: the keyframe its head ends on (4 s) is not an
    // IDR, so it shows the snapped cut, is judged on it, and states the reason before Run in the run's own words.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnOpenGopRowInExact_ShowsTheSnappedCut_AndTheReason_BeforeRun()
    {
        var (vm, probe, thumbs) = Build();
        vm.ExactCut = true;
        probe.Answers[S(4)] = false;
        var row = await AddAsync(vm, probe, MakeVideo("camera.mp4"));

        row.IntroEnd.Requested = S(3.4);
        Settle(row);

        probe.Asked.Should().Equal(new[] { S(4) }, "asked about the keyframe the head ends on, once");
        row.KeyframeKindReason.Should().Be(Reason);
        row.ExactCutApplies.Should().BeFalse();
        row.IntroEnd.Snapped.Should().Be(S(4), "3.4 snaps to the 4 s keyframe on the 2 s grid");
        row.IntroEnd.HasSnapNote.Should().BeTrue("the run cuts at the keyframe, so the row says so");
        row.KeptDuration.Should().Be(S(56), "eligibility and the kept length come from Snapped: 60 - 4");
        row.Warning.Should().Contain(Note, "the reason is stated before Run, in the run's own words");
        thumbs.Requests.Where(r => r.InputPath == row.Path).Select(r => r.Time).Last()
            .Should().Be(S(4), "the chip shows the frame the run cuts at");
    }

    // serves-spec: SPEC-011#I164 - an IDR keyframe: the row is the Exact row it always was.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AClosedGopRowInExact_IsUnchanged()
    {
        var (vm, probe, _) = Build();
        vm.ExactCut = true;
        var row = await AddAsync(vm, probe, MakeVideo("camera.mp4"));

        row.IntroEnd.Requested = S(3.4);
        Settle(row);

        probe.Asked.Should().Equal(new[] { S(4) });
        row.KeyframeKindReason.Should().BeNull();
        row.ExactCutApplies.Should().BeTrue();
        row.KeptDuration.Should().Be(S(60) - S(3.4), "an Exact row keeps the requested cut");
        row.Warning.Should().NotContain(Reason);
    }

    // serves-spec: SPEC-011#I164 - the reason follows the intro: it goes when the intro moves to a GOP whose keyframe is an IDR, and
    // comes back with it; each keyframe is asked once (the answer is kept).
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheReason_FollowsTheIntro_AndEachKeyframeIsAskedOnce()
    {
        var (vm, probe, _) = Build();
        vm.ExactCut = true;
        probe.Answers[S(4)] = false;          // behind this one the file is open-GOP
        probe.Answers[S(8)] = true;           // a forced IDR
        var row = await AddAsync(vm, probe, MakeVideo("mixed.mp4"));

        row.IntroEnd.Requested = S(3.4);
        row.KeyframeKindReason.Should().Be(Reason);
        row.IntroEnd.Requested = S(7.4);
        row.KeyframeKindReason.Should().BeNull("the head of this intro ends on the IDR at 8 s");
        row.ExactCutApplies.Should().BeTrue();
        row.IntroEnd.Requested = S(3.2);
        row.KeyframeKindReason.Should().Be(Reason);

        probe.Asked.Should().Equal(new[] { S(4), S(8) }, "the answer for 4 s was kept when the intro came back to it");
    }

    // serves-spec: SPEC-011#I164 - no joint, no question: an intro on a keyframe is the lossless cut; the question is not asked
    // for it, and not while Exact is off (switching Exact on asks).
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task NoQuestionIsAsked_OnAKeyframe_OrWhileExactIsOff_ButExactSwitchedOnAsks()
    {
        var (vm, probe, _) = Build();
        probe.Answers[S(4)] = false;
        var row = await AddAsync(vm, probe, MakeVideo("camera.mp4"));

        row.IntroEnd.Requested = S(3.4);                 // Lossless: the row is the snapped row already
        row.KeyframeKindReason.Should().BeNull();
        probe.Asked.Should().BeEmpty("Exact is off");

        vm.ExactCut = true;
        probe.Asked.Should().Equal(new[] { S(4) }, "switching Exact on asks about the current intro");
        row.KeyframeKindReason.Should().Be(Reason);

        row.IntroEnd.Requested = S(6);                   // on a keyframe: PureCopy, no joint
        row.KeyframeKindReason.Should().BeNull();
        probe.Asked.Should().Equal(new[] { S(4) }, "nothing to ask where there is no joint");

        vm.ExactCut = false;
        row.IntroEnd.Requested = S(3.4);
        row.KeyframeKindReason.Should().BeNull("Exact is off again: the row is a Lossless row");
    }

    // serves-spec: SPEC-011#I164 - the question is about H.264: a row of another codec is never asked.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ARowThatIsNotH264_IsNeverAsked()
    {
        var (vm, probe, _) = Build();
        vm.ExactCut = true;
        probe.Answers[S(4)] = false;
        var row = await AddAsync(vm, probe, MakeVideo("old.mp4"), videoCodec: "mpeg4");

        row.IntroEnd.Requested = S(3.4);

        probe.Asked.Should().BeEmpty();
        row.KeyframeKindReason.Should().BeNull();
        row.ExactCutApplies.Should().BeTrue();
    }

    // serves-spec: SPEC-011#I164 - a probe that cannot say (null) or that throws is "not provably an IDR", the engine's rule: the
    // row states the reason; and until the answer comes the row reads exactly as before.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ARowWhoseProbeCannotSay_StatesTheReason_AndUntilTheAnswerComesReadsAsBefore()
    {
        var (vm, probe, _) = Build();
        vm.ExactCut = true;
        probe.Answers[S(4)] = null;
        probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = await AddAsync(vm, probe, MakeVideo("camera.mp4"));

        row.IntroEnd.Requested = S(3.4);

        row.KeyframeKindReason.Should().BeNull("the answer has not come yet");
        row.ExactCutApplies.Should().BeTrue();

        probe.Gate.SetResult();
        await WaitForAsync(() => row.KeyframeKindReason is not null);
        row.KeyframeKindReason.Should().Be(Reason);
        row.ExactCutApplies.Should().BeFalse();
    }

    // serves-spec: SPEC-011#I164 - a removed row drops the answer that was on its way (no change after Remove / Clear).
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnAnswerThatArrivesAfterTheRowWasCancelled_ChangesNothing()
    {
        var (vm, probe, _) = Build();
        vm.ExactCut = true;
        probe.Answers[S(4)] = false;
        probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = await AddAsync(vm, probe, MakeVideo("camera.mp4"));
        row.IntroEnd.Requested = S(3.4);

        row.CancelScan();
        probe.Gate.TrySetResult();
        await Task.Delay(50);

        row.KeyframeKindReason.Should().BeNull();
        row.ExactCutApplies.Should().BeTrue();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }
    }
}
