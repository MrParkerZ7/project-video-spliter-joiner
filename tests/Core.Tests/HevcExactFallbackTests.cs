using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Io;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using Xunit;
using Xunit.Abstractions;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-189 — an Exact cut on an HEVC source falls back to the lossless cut and says why. HEVC is no longer something
/// Exact can reproduce: the concat demuxer converts parameter sets in-band for H.264 only, so an HEVC joint decoded
/// against the head's configuration and every frame after it was wrong (<see cref="SmartCutJointIntegrityTests"/>).
///
/// <para>The fallback path itself is not new — <see cref="SmartCutEngine"/> already returns <c>FellBack</c> with no
/// ffmpeg run when <see cref="SmartCutArgsBuilder.TryResolveEncoders"/> refuses, and <see cref="BulkTrimEngine"/> already
/// runs the lossless cut with a row warning. What is new: HEVC is refused, in words a user can act on, before the
/// generic "no known encoder" branch; and a fall-back row whose snapped cut removes nothing keeps its warning on the
/// <c>Skipped</c> result.</para>
/// </summary>
public sealed class HevcExactFallbackTests : IDisposable
{
    private const string HevcReason = "frame-exact cutting is not supported for HEVC/H.265 video yet";

    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public HevcExactFallbackTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "vsj-hevc-exact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static MediaInfo Info(string videoCodec, string? audioCodec = "aac") => new(
        TimeSpan.FromSeconds(30),
        "mov,mp4,m4a,3gp,3g2,mj2",
        new[] { new StreamInfo(0, videoCodec, "video", 1920, 1080, "yuv420p", null, null, "1/30") },
        audioCodec is null
            ? Array.Empty<StreamInfo>()
            : new[] { new StreamInfo(1, audioCodec, "audio", null, null, null, 48000, 2, "1/48000") });

    private static IReadOnlyList<TimeSpan> Grid2s() =>
        Enumerable.Range(0, 16).Select(i => TimeSpan.FromSeconds(i * 2)).ToList();

    /// <summary>A probe that reports a fixed streamed <see cref="MediaInfo"/> and keyframe grid; it never snaps.</summary>
    private sealed class FixedProbe : IMediaProbe
    {
        private readonly MediaInfo _info;
        private readonly IReadOnlyList<TimeSpan> _keyframes;

        public FixedProbe(MediaInfo info, IReadOnlyList<TimeSpan> keyframes)
        {
            _info = info;
            _keyframes = keyframes;
        }

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(ProbeResult.Success(_info));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(_keyframes);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested) =>
            throw new NotSupportedException("the smart cutter plans against the raw keyframes and never snaps");

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.Zero;
    }

    private string Placeholder(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "src");
        return p;
    }

    // ---- The gate: TryResolveEncoders ---------------------------------------------------------------------

    // serves-spec: SPEC-001#I45 / SPEC-001#I46 — HEVC is not in the encoder map; it is refused with a reason a user can
    // act on, checked before the generic "no known encoder" branch.
    [Trait("serves-spec", "SPEC-001")]
    [Theory]
    [InlineData("hevc")]
    [InlineData("h265")]
    [InlineData("HEVC")]
    public void TryResolveEncoders_RefusesHevc_WithAReasonAUserCanRead(string codec)
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info(codec), out var video, out var audio, out var why)
            .Should().BeFalse("an HEVC joint decodes against the head's parameter sets, so HEVC cannot be exact-cut yet");

        video.Should().BeNull();
        audio.Should().BeNull();
        why.Should().Be(HevcReason);
        why.Should().Contain("HEVC");
        why.Should().NotContain("no known encoder", "the HEVC reason is checked before the generic branch");
        why.Should().NotContain("joint", "no internal terms in a sentence the row shows");
        why.Should().NotContainAny(new[] { "(", ")" }, "the row warning wraps the reason in parentheses already");
    }

    // serves-spec: SPEC-001#I46 — the video gate wins over an unmappable audio codec, so the row names HEVC.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TryResolveEncoders_NamesHevc_EvenWhenTheAudioIsUnmappableToo()
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info("hevc", "pcm_s24le"), out _, out _, out var why).Should().BeFalse();
        why.Should().Be(HevcReason);
    }

    // serves-spec: SPEC-001#I45 — H.264 + AAC still resolve: the map keeps every other entry.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void TryResolveEncoders_StillResolvesH264AndAac()
    {
        SmartCutArgsBuilder.TryResolveEncoders(Info("h264"), out var video, out var audio, out var why).Should().BeTrue();
        video.Should().Be("libx264");
        audio.Should().Be("aac");
        why.Should().BeNull();
    }

    // serves-spec: SPEC-001#I46 — an audio-only source has no video stream to refuse: unchanged.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public void AnAudioOnlySource_IsUnchanged()
    {
        var audioOnly = new MediaInfo(
            TimeSpan.FromSeconds(30), "mp4", Array.Empty<StreamInfo>(),
            new[] { new StreamInfo(0, "aac", "audio", null, null, null, 48000, 2, "1/48000") });

        SmartCutArgsBuilder.TryResolveEncoders(audioOnly, out var video, out var audio, out var why).Should().BeTrue();
        video.Should().BeNull();
        audio.Should().Be("aac");
        why.Should().BeNull();
    }

    // ---- The engine: falls back before any ffmpeg run -----------------------------------------------------

    // serves-spec: SPEC-001#I46 — an HEVC source falls back with the stated reason and runs ZERO ffmpeg invocations.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_OnHevc_FallsBackWithTheReason_AndRunsNoFfmpeg()
    {
        var input = Placeholder("phone.mp4");
        var output = Path.Combine(_dir, "phone_trimmed.mp4");
        var runner = new RecordingFakeRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info("hevc"), Grid2s()));

        var result = await engine.CutAsync(input, TimeSpan.FromSeconds(3.5), null, output);

        runner.Commands.Should().BeEmpty("nothing is re-encoded, copied or joined for a source Exact cannot reproduce");
        result.FellBack.Should().BeTrue();
        result.FallbackReason.Should().Be(HevcReason);
        result.OutputPath.Should().BeNull();
        result.ReencodedDuration.Should().Be(TimeSpan.Zero, "nothing was re-encoded");
        File.Exists(output).Should().BeFalse();
        Directory.GetDirectories(_dir, ".vsj-smartcut-*").Should().BeEmpty("no temp dir is even created");
    }

    // serves-spec: SPEC-001#I40 — a start already on a keyframe is PureCopy before the encoder gate: zero runs.
    [Trait("serves-spec", "SPEC-001")]
    [Fact]
    public async Task SmartCutEngine_OnHevc_StartOnAKeyframe_IsPureCopy_WithNoFfmpegRun()
    {
        var input = Placeholder("phone.mp4");
        var runner = new RecordingFakeRunner();
        var engine = new SmartCutEngine(runner, new FixedProbe(Info("hevc"), Grid2s()));

        var result = await engine.CutAsync(input, TimeSpan.FromSeconds(4), null, Path.Combine(_dir, "out.mp4"));

        runner.Commands.Should().BeEmpty();
        result.Strategy.Should().Be(SmartCutStrategy.PureCopy);
        result.FellBack.Should().BeTrue();
    }

    // ---- The batch: lossless cut, row warning, Skipped keeps it --------------------------------------------

    private static BulkTrimEngine Batch(
        FakeSplitEngine split, FakeRequestBuilder builder, ISmartCutEngine smart, IOriginalDisposer? disposer = null) =>
        new(split, builder, new FakeDiskSpaceProbe(long.MaxValue), smart, disposer);

    private SmartCutEngine HevcSmartCut() =>
        new(new RecordingFakeRunner(), new FixedProbe(Info("hevc"), Grid2s()));

    // serves-spec: SPEC-002#I50 / SPEC-002#I51 — an HEVC Exact row runs the lossless cut, and its warning names the reason.
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task BulkTrimEngine_HevcExactRow_RunsTheLosslessCut_WithTheReasonInItsWarning()
    {
        var input = Placeholder("phone.mp4");
        var split = new FakeSplitEngine();
        var item = new BulkTrimItem(input, TimeSpan.FromSeconds(3.5), null, Path.Combine(_dir, "phone_trimmed.mp4"));

        var result = await Batch(split, new FakeRequestBuilder(), HevcSmartCut())
            .RunAsync(new[] { item }, new BulkTrimOptions(Precision: CutPrecision.Exact));

        split.CallCount.Should().Be(1, "the fall-back row takes the ordinary lossless path");
        var row = result.Items.Single();
        row.Outcome.Should().Be(ItemOutcome.Done);
        row.Warnings.Should().ContainSingle()
            .Which.Should().Be($"{BulkTrimEngine.ExactFallbackPrefix} ({HevcReason}) - cut snapped to the nearest keyframe");
    }

    // serves-spec: SPEC-002#I18 / SPEC-002#I51 — a fall-back row whose snapped cut removes nothing is Skipped WITH its
    // fallback warning, so a caller that reaches the engine without the view model still learns why nothing was cut.
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task BulkTrimEngine_HevcExactRow_WhoseSnappedCutRemovesNothing_IsSkippedWithTheFallbackWarning()
    {
        var input = Placeholder("phone.mp4");
        var split = new FakeSplitEngine();
        var builder = new FakeRequestBuilder();
        builder.NoOpInputs.Add("phone.mp4"); // the snapped intro is 0 and there is no outro
        var item = new BulkTrimItem(input, TimeSpan.FromSeconds(0.8), null, Path.Combine(_dir, "phone_trimmed.mp4"));

        var result = await Batch(split, builder, HevcSmartCut())
            .RunAsync(new[] { item }, new BulkTrimOptions(Precision: CutPrecision.Exact));

        var row = result.Items.Single();
        row.Outcome.Should().Be(ItemOutcome.Skipped);
        row.Warnings.Should().ContainSingle()
            .Which.Should().StartWith(BulkTrimEngine.ExactFallbackPrefix).And.Contain(HevcReason);
        split.CallCount.Should().Be(0, "nothing was cut");
    }

    // serves-spec: SPEC-002#I18 / SPEC-002#I55 — the same holds on the refused route: Exact + Replace originals with no
    // disposer is announced as "(replacing originals)" (I55), and when that row's lossless pass is a no-op the Skipped
    // result keeps the announcement.
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task BulkTrimEngine_RefusedReplaceOriginalExactRow_WhoseLosslessPassIsANoOp_IsSkippedWithTheRefusalWarning()
    {
        var input = Placeholder("phone.mp4");
        var split = new FakeSplitEngine();
        var builder = new FakeRequestBuilder();
        builder.NoOpInputs.Add("phone.mp4");
        var item = new BulkTrimItem(input, TimeSpan.FromSeconds(0.8), null, Path.Combine(_dir, "unused.mp4"));

        var result = await Batch(split, builder, HevcSmartCut(), disposer: null)
            .RunAsync(new[] { item }, new BulkTrimOptions(Precision: CutPrecision.Exact, Output: OutputMode.ReplaceOriginal));

        var row = result.Items.Single();
        row.Outcome.Should().Be(ItemOutcome.Skipped);
        row.Warnings.Should().ContainSingle()
            .Which.Should().Be($"{BulkTrimEngine.ExactFallbackPrefix} (replacing originals) - cut snapped to the nearest keyframe");
        split.CallCount.Should().Be(0, "nothing was cut");
    }

    // serves-spec: SPEC-002#I18 — a Lossless no-op row still skips with no warning (unchanged).
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task BulkTrimEngine_LosslessNoOpRow_IsStillSkippedWithNoWarning()
    {
        var input = Placeholder("phone.mp4");
        var builder = new FakeRequestBuilder();
        builder.NoOpInputs.Add("phone.mp4");
        var item = new BulkTrimItem(input, TimeSpan.FromSeconds(0.8), null, Path.Combine(_dir, "phone_trimmed.mp4"));

        var result = await Batch(new FakeSplitEngine(), builder, HevcSmartCut())
            .RunAsync(new[] { item }, new BulkTrimOptions());

        result.Items.Single().Outcome.Should().Be(ItemOutcome.Skipped);
        result.Items.Single().Warnings.Should().BeEmpty();
    }

    // serves-spec: SPEC-002#I51 — an HEVC Exact row whose start is on a keyframe is PureCopy: no warning (the exemption).
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task BulkTrimEngine_HevcExactRow_StartOnAKeyframe_AddsNoWarning()
    {
        var input = Placeholder("phone.mp4");
        var split = new FakeSplitEngine();
        var item = new BulkTrimItem(input, TimeSpan.FromSeconds(4), null, Path.Combine(_dir, "phone_trimmed.mp4"));

        var result = await Batch(split, new FakeRequestBuilder(), HevcSmartCut())
            .RunAsync(new[] { item }, new BulkTrimOptions(Precision: CutPrecision.Exact));

        split.CallCount.Should().Be(1);
        result.Items.Single().Outcome.Should().Be(ItemOutcome.Done);
        result.Items.Single().Warnings.Should().BeEmpty("a cut on a keyframe is exact on the lossless path too");
    }

    // ---- Real media: the lossless cut it writes decodes clean -----------------------------------------------

    private sealed class DeletingDisposer : IOriginalDisposer
    {
        public List<string> Disposed { get; } = new();

        public void DisposeOriginalBackup(string backupPath)
        {
            Disposed.Add(backupPath);
            try { File.Delete(backupPath); } catch { /* best-effort, like the real disposer */ }
        }
    }

    private BulkTrimEngine RealBatch(IOriginalDisposer disposer)
    {
        var locator = new FfmpegBinaryLocator(
            ffmpegOverride: FfmpegTestBinaries.Ffmpeg, ffprobeOverride: FfmpegTestBinaries.Ffprobe);
        var runner = new FfmpegRunner(locator);
        var probe = new MediaProbe(new FfprobeRunner(locator));
        var logs = new ErrorLogWriter(Path.Combine(_dir, "logs")); // never %LOCALAPPDATA%
        var split = new SplitEngine(runner, probe, logs, new FakeDiskSpaceProbe(long.MaxValue), disposer);
        return new BulkTrimEngine(
            split, new KeptMiddleRequestBuilder(probe), new FakeDiskSpaceProbe(long.MaxValue),
            new SmartCutEngine(runner, probe), disposer);
    }

    private async Task<string> HevcSourceAsync(string name)
    {
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfmpegExists, "ffmpeg");
        FfmpegTestBinaries.Require(_output, FfmpegTestBinaries.FfprobeExists, "ffprobe");
        Skip.IfNot(await SmartCutJointIntegrityTests.HasEncoderAsync("libx265"), "no libx265 in the bundled ffmpeg");
        return await SmartCutJointIntegrityTests.MakeHevcFixtureAsync(_dir, name);
    }

    private static double DurationOf(string path) =>
        new MediaProbe(new FfprobeRunner(new FfmpegBinaryLocator(
                ffmpegOverride: FfmpegTestBinaries.Ffmpeg, ffprobeOverride: FfmpegTestBinaries.Ffprobe)))
            .ProbeAsync(path).GetAwaiter().GetResult() is ProbeResult.ProbeSucceeded ok
            ? ok.Info.Duration.TotalSeconds
            : -1;

    // serves-spec: SPEC-002#I50 / SPEC-002#I51 / SPEC-001#I46 — an HEVC Exact row writes the lossless cut (snapped to the
    // 4 s keyframe), says why, and the output decodes with zero errors.
    [Trait("serves-spec", "SPEC-002")]
    [SkippableFact]
    public async Task AnHevcExactRow_WritesTheLosslessCut_ThatDecodesClean()
    {
        var src = await HevcSourceAsync("phone.mp4");
        var dest = Path.Combine(_dir, "phone_trimmed.mp4");
        var item = new BulkTrimItem(src, TimeSpan.FromSeconds(3.5), null, dest);

        var result = await RealBatch(new DeletingDisposer())
            .RunAsync(new[] { item }, new BulkTrimOptions(Precision: CutPrecision.Exact));

        var row = result.Items.Single();
        row.Outcome.Should().Be(ItemOutcome.Done, row.Error?.Message);
        row.Warnings.Should().Contain(w => w.StartsWith(BulkTrimEngine.ExactFallbackPrefix) && w.Contains(HevcReason));
        File.Exists(row.OutputPath).Should().BeTrue();

        var (errors, lines) = await SmartCutJointIntegrity.DecodeAsync(row.OutputPath!);
        _output.WriteLine($"HEVC Exact row, fell back: output decodes with {errors} decoder/demuxer error line(s), {lines.Count} line(s) in all");
        lines.ToList().ForEach(_output.WriteLine);
        errors.Should().Be(0, "the lossless cut is the source's own packets from a keyframe on");
        DurationOf(row.OutputPath!).Should().BeApproximately(8.0, 0.35, "3.5 s snaps to the 4 s keyframe on the lossless path");
    }

    // serves-spec: SPEC-002#I58 — under Replace originals with a disposer, the fall-back leaves no .vsj-exact sibling beside
    // the source, and the original now holds the lossless cut.
    [Trait("serves-spec", "SPEC-002")]
    [SkippableFact]
    public async Task UnderReplaceOriginals_AnHevcExactRow_LeavesNoSiblingTemp()
    {
        var src = await HevcSourceAsync("phone-replace.mp4");
        var item = new BulkTrimItem(src, TimeSpan.FromSeconds(3.5), null, Path.Combine(_dir, "unused.mp4"));
        var disposer = new DeletingDisposer();

        var result = await RealBatch(disposer)
            .RunAsync(new[] { item }, new BulkTrimOptions(Output: OutputMode.ReplaceOriginal, Precision: CutPrecision.Exact));

        var row = result.Items.Single();
        row.Outcome.Should().Be(ItemOutcome.Done, row.Error?.Message);
        row.Warnings.Should().Contain(w => w.StartsWith(BulkTrimEngine.ExactFallbackPrefix) && w.Contains(HevcReason));
        Directory.GetFiles(_dir, "*.vsj-exact*").Should().BeEmpty("the sibling temp is swept; the lossless pass owns the destination");
        DurationOf(src).Should().BeApproximately(8.0, 0.35, "the original now holds the lossless cut");
        var (errors, _) = await SmartCutJointIntegrity.DecodeAsync(src);
        errors.Should().Be(0);
    }
}
