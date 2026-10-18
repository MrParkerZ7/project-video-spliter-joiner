using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Thumbnails;
using VideoSplitJoiner.Core.Waveform;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-195 / G-059 — on an MP4/MOV input (<c>.mp4</c>, <c>.m4v</c>, <c>.mov</c>, any case) the waveform skips ffmpeg's
/// stream-info decode (<c>-nofind_stream_info</c> immediately before <c>-i</c>). A flagged run that fails, or exits 0
/// with no file or an empty PCM, is retried once without the flag and that result is used; a cancelled run is never
/// retried, and nothing is remembered. Every other input keeps exactly today's tokens. The frame grab never carries
/// the flag, on any input (SPEC-005 I27: skipping the pass also drops the format start time an input <c>-ss</c> counts
/// from), and neither do the probe and the <c>-c copy</c> runs. Pure-unit: a scripted fake runner, no real ffmpeg (the
/// real-ffmpeg checks are in <c>StreamInfoSkipIntegrationTests</c>).
/// </summary>
public sealed class StreamInfoSkipTests : IDisposable
{
    private const string Flag = "-nofind_stream_info";

    private static readonly byte[] JpegBytes = "jpg-bytes"u8.ToArray();

    // Four Int16-LE samples: 8192, 16384, 24576, 32767 → one bucket peaks at 32767/32768.
    private static readonly byte[] PcmBytes = { 0x00, 0x20, 0x00, 0x40, 0x00, 0x60, 0xFF, 0x7F };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-nofsi-" + Guid.NewGuid().ToString("N"));

    public StreamInfoSkipTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string MakeInput(string name)
    {
        var input = Path.Combine(_dir, "in", name);
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        File.WriteAllText(input, "placeholder");
        return input;
    }

    private FfmpegThumbnailService Thumbs(IFfmpegRunner runner) =>
        new(runner, Path.Combine(_dir, "thumb-cache"), TimeSpan.FromSeconds(1));

    private FfmpegWaveformService Waves(IFfmpegRunner runner) =>
        new(runner, Path.Combine(_dir, "waveform-cache"));

    private static List<string> WithoutFlag(IEnumerable<string> tokens) => tokens.Where(t => t != Flag).ToList();

    // ================================================================================================
    // The extension gate (SPEC-006 I3)
    // ================================================================================================

    // SPEC-006#I3 — .mp4/.m4v/.mov in any case are the ISO-BMFF family; the last extension is the one that counts.
    [Trait("serves-spec", "SPEC-006")]
    [Theory]
    [InlineData(@"C:\videos\clip.mp4")]
    [InlineData(@"C:\videos\clip.m4v")]
    [InlineData(@"C:\videos\clip.mov")]
    [InlineData(@"C:\videos\CLIP.MP4")]
    [InlineData(@"C:\videos\clip.M4V")]
    [InlineData(@"C:\videos\clip.MoV")]
    [InlineData(@"C:\videos\my.film.v2.mp4")]
    [InlineData("clip.mp4")]
    [InlineData(@"C:\video folder.mkv\clip.mov")]
    public void IsIsoBmffExtension_Mp4M4vMov_AnyCase_True(string path) =>
        StreamInfoSkip.IsIsoBmffExtension(path).Should().BeTrue();

    // SPEC-006#I3 — anything else, including no extension, a name that only contains "mp4", and blank input.
    [Trait("serves-spec", "SPEC-006")]
    [Theory]
    [InlineData(@"C:\videos\clip.mkv")]
    [InlineData(@"C:\videos\clip.ts")]
    [InlineData(@"C:\videos\clip.m2ts")]
    [InlineData(@"C:\videos\clip.avi")]
    [InlineData(@"C:\videos\clip.webm")]
    [InlineData(@"C:\videos\clip.mp4.mkv")]
    [InlineData(@"C:\videos\clip.mp4v")]
    [InlineData(@"C:\videos\clipmp4")]
    [InlineData(@"C:\videos.mp4\clip")]
    [InlineData(@"C:\videos\clip")]
    [InlineData(@"C:\videos\clip.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsIsoBmffExtension_OtherPaths_False(string? path) =>
        StreamInfoSkip.IsIsoBmffExtension(path).Should().BeFalse();

    // SPEC-006#I3 — the caller-chosen overload: skip=false is exactly today's tokens even for an MP4 (that is the
    // retry), skip=true adds only the flag, immediately before -i.
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public void WaveformBuildArgs_ExplicitSkip_AddsOnlyTheFlag_ExplicitNoSkip_IsTodaysTokens()
    {
        const string mp4 = @"C:\videos\clip.mp4";
        const string mkv = @"C:\videos\clip.mkv";

        var waveNo = FfmpegWaveformService.BuildArgs(mp4, 4000, "a.pcm", skipStreamInfo: false).ToList();
        var waveYes = FfmpegWaveformService.BuildArgs(mkv, 4000, "a.pcm", skipStreamInfo: true).ToList();
        waveNo.Should().Equal("-hide_banner", "-nostdin", "-i", mp4, "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y", "a.pcm");
        waveYes.Should().Equal("-hide_banner", "-nostdin", Flag, "-i", mkv, "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y", "a.pcm");
    }

    // ================================================================================================
    // Tokens (SPEC-005 I2 + I27 · SPEC-006 I3)
    // ================================================================================================

    // SPEC-005#I2 + I27 — every input, MP4/MOV included, gets exactly today's grab tokens: no flag anywhere.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.M4V")]
    [InlineData("clip.mov")]
    [InlineData("CLIP.MP4")]
    [InlineData("clip.MoV")]
    [InlineData("clip.mkv")]
    [InlineData("clip.ts")]
    [InlineData("clip.avi")]
    [InlineData("clip.m2ts")]
    [InlineData("clip")]
    [InlineData("clip.mp4.mkv")]
    [InlineData("clip.mp4v")]
    public void ThumbnailArgs_AnyInput_ExactlyTodaysTokens_NeverTheFlag(string name)
    {
        var input = Path.Combine(@"C:\videos", name);
        const string temp = @"C:\cache\5000_w160.jpg";

        var tokens = FfmpegThumbnailService.BuildArgs(input, TimeSpan.FromSeconds(5), 160, temp).ToList();

        tokens.Should().Equal(
            "-hide_banner", "-nostdin", "-ss", "5", "-i", input,
            "-frames:v", "1", "-vf", "scale=160:-1", "-y", temp);
    }

    // SPEC-006#I3 — the waveform's MP4/MOV args carry the flag immediately before -i.
    [Trait("serves-spec", "SPEC-006")]
    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.M4V")]
    [InlineData("clip.mov")]
    [InlineData("CLIP.MP4")]
    public void WaveformArgs_IsoBmffInput_FlagImmediatelyBeforeInput(string name)
    {
        var input = Path.Combine(@"C:\videos", name);
        const string temp = @"C:\cache\audio.pcm";

        var tokens = FfmpegWaveformService.BuildArgs(input, 4000, temp).ToList();

        tokens.Should().Equal(
            "-hide_banner", "-nostdin", Flag, "-i", input,
            "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y", temp);
    }

    // SPEC-006#I3 — every other input keeps exactly today's waveform tokens.
    [Trait("serves-spec", "SPEC-006")]
    [Theory]
    [InlineData("clip.mkv")]
    [InlineData("clip.ts")]
    [InlineData("clip.avi")]
    [InlineData("clip")]
    public void WaveformArgs_OtherInput_ExactlyTodaysTokens(string name)
    {
        var input = Path.Combine(@"C:\videos", name);
        const string temp = @"C:\cache\audio.pcm";

        var tokens = FfmpegWaveformService.BuildArgs(input, 4000, temp).ToList();

        tokens.Should().Equal(
            "-hide_banner", "-nostdin", "-i", input,
            "-vn", "-ac", "1", "-ar", "4000", "-f", "s16le", "-y", temp);
    }

    // SPEC-005#I27 — the service itself runs today's args for an MP4 and a MOV: one run, no flag.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.MOV")]
    public async Task Thumbnail_IsoBmffInput_OneUnflaggedRun(string name)
    {
        var input = MakeInput(name);
        var runner = new ScriptedRunner(JpegBytes);

        var path = await Thumbs(runner).GetThumbnailAsync(input, TimeSpan.FromSeconds(5), 160, CancellationToken.None);

        path.Should().NotBeNull();
        runner.CallCount.Should().Be(1);
        runner.Commands[0].Should().NotContain(Flag);
    }

    // SPEC-006#I3 — the waveform service runs the flagged args for a MOV: one run, flag before -i.
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public async Task Waveform_MovInput_OneFlaggedRun()
    {
        var input = MakeInput("clip.mov");
        var runner = new ScriptedRunner(PcmBytes);

        var peaks = await Waves(runner).GetPeaksAsync(input, 1, CancellationToken.None);

        peaks.Should().NotBeNull();
        runner.CallCount.Should().Be(1);
        var tokens = runner.Commands[0];
        tokens[tokens.IndexOf("-i") - 1].Should().Be(Flag);
    }

    // ================================================================================================
    // The flag stays where it belongs: never on the probe or a -c copy run (SPEC-006 I3)
    // ================================================================================================

    // SPEC-006#I3 — the metadata probe never skips stream info (it would lose pix_fmt and the format duration).
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public async Task ProbeAsync_Mp4Input_CarriesNoFlag()
    {
        var input = MakeInput("clip.mp4");
        var ffprobe = new RecordingFfprobeRunner();

        var result = await new MediaProbe(ffprobe).ProbeAsync(input);

        result.Should().BeOfType<ProbeResult.ProbeSucceeded>();
        ffprobe.Commands.Should().ContainSingle();
        ffprobe.Commands[0].Should().NotContain(Flag);
    }

    // SPEC-006#I3 — the split, join and Exact-tail/concat copy runs never carry the flag, for an MP4 input too.
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public void CopyArgs_Mp4Input_CarryNoFlag()
    {
        const string input = @"C:\videos\clip.mp4";

        var all = new[]
        {
            SplitArgsBuilder.SegmentMuxer(input, new[] { TimeSpan.FromSeconds(5) }, @"C:\out\part%03d.mp4", TimeSpan.FromSeconds(1.0 / 60)).ToList(),
            SplitArgsBuilder.PerSegment(input, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.Zero, @"C:\out\p2.mp4").ToList(),
            SplitArgsBuilder.PerSegment(input, TimeSpan.FromSeconds(2), null, TimeSpan.Zero, @"C:\out\p3.mp4").ToList(),
            SmartCutArgsBuilder.TailCopy(input, TimeSpan.FromSeconds(4), null, @"C:\out\tail.mp4").ToList(),
            JoinArgsBuilder.ConcatCopy(@"C:\tmp\concat.txt", @"C:\out\joined.mp4").ToList(),
        };

        all.Should().AllSatisfy(tokens =>
        {
            tokens.Should().Contain("copy", "precondition: these are the -c copy runs");
            tokens.Should().NotContain(Flag);
        });
    }

    // ================================================================================================
    // The frame grab keeps the stream-info pass (SPEC-005 I27)
    // ================================================================================================

    // SPEC-005#I27 — a failed MP4 grab is one run, as today: no retry without a flag it never had, nothing remembered.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(RunStep.Fail)]
    [InlineData(RunStep.NoFile)]
    public async Task Thumbnail_Mp4GrabFails_OneRun_Null_NoRetry(RunStep failure)
    {
        var input = MakeInput("clip.mp4");
        var runner = new ScriptedRunner(JpegBytes, failure);
        var svc = Thumbs(runner);

        var path = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(3), 160, CancellationToken.None);

        path.Should().BeNull();
        runner.CallCount.Should().Be(1);
        runner.Commands[0].Should().NotContain(Flag);

        var next = await svc.GetThumbnailAsync(input, TimeSpan.FromSeconds(7), 160, CancellationToken.None);
        next.Should().NotBeNull();
        runner.CallCount.Should().Be(2);
        runner.Commands[1].Should().NotContain(Flag, "the next grab on that input is today's command too");
    }

    // SPEC-005#I27 + I14 — a cancelled MP4 grab is one run and null, as today.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public async Task Thumbnail_Mp4GrabCancelled_OneRun_Null()
    {
        var input = MakeInput("clip.mp4");
        var runner = new ScriptedRunner(JpegBytes, RunStep.Cancel);

        var path = await Thumbs(runner).GetThumbnailAsync(input, TimeSpan.FromSeconds(3), 160, CancellationToken.None);

        path.Should().BeNull();
        runner.CallCount.Should().Be(1);
    }

    // ================================================================================================
    // Waveform retry (SPEC-006 I24) — the same rule, no memory
    // ================================================================================================

    // SPEC-006#I24 — a flagged run that exits non-zero is followed by exactly one unflagged run, whose peaks are returned.
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public async Task Waveform_FlaggedRunFails_RetriedOnceWithoutFlag_ItsPeaksReturned()
    {
        var input = MakeInput("clip.mp4");
        var runner = new ScriptedRunner(PcmBytes, RunStep.Fail);

        var peaks = await Waves(runner).GetPeaksAsync(input, 1, CancellationToken.None);

        peaks.Should().NotBeNull();
        peaks![0].Should().BeApproximately(32767f / 32768f, 1e-6f);
        runner.CallCount.Should().Be(2);
        runner.Commands[0].Should().Contain(Flag);
        runner.Commands[1].Should().Equal(WithoutFlag(runner.Commands[0]), "the retry is today's command exactly");
    }

    // SPEC-006#I24 — the same when the flagged run exits 0 with no file.
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public async Task Waveform_FlaggedRunExitsZeroWithNoFile_Retried()
    {
        var input = MakeInput("clip.mp4");
        var runner = new ScriptedRunner(PcmBytes, RunStep.NoFile);

        var peaks = await Waves(runner).GetPeaksAsync(input, 1, CancellationToken.None);

        peaks.Should().NotBeNull();
        runner.CallCount.Should().Be(2);
        runner.Commands[1].Should().NotContain(Flag);
    }

    // SPEC-006#I24 — the same when the flagged run exits 0 with an empty PCM file.
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public async Task Waveform_FlaggedRunWritesEmptyPcm_Retried()
    {
        var input = MakeInput("clip.mp4");
        var runner = new ScriptedRunner(PcmBytes, RunStep.WriteEmpty);

        var peaks = await Waves(runner).GetPeaksAsync(input, 1, CancellationToken.None);

        peaks.Should().NotBeNull();
        runner.CallCount.Should().Be(2);
        runner.Commands[1].Should().NotContain(Flag);
    }

    // SPEC-006#I24 — a failed flagged run's leftover PCM is never read as the retry's output.
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public async Task Waveform_FlaggedRunFailsLeavingPcm_RetryIsJudgedOnItsOwnOutput()
    {
        var input = MakeInput("clip.mp4");
        var runner = new ScriptedRunner(PcmBytes, RunStep.FailLeavingFile, RunStep.NoFile);

        var peaks = await Waves(runner).GetPeaksAsync(input, 1, CancellationToken.None);

        peaks.Should().BeNull("the leftover of the failed flagged run is not the retry's PCM");
        runner.CallCount.Should().Be(2);
    }

    // SPEC-006#I24 + I19 — both runs fail (a no-audio MP4): exactly two runs, null, nothing cached, no throw.
    [Trait("serves-spec", "SPEC-006")]
    [Theory]
    [InlineData(RunStep.Fail)]
    [InlineData(RunStep.WriteEmpty)]
    public async Task Waveform_BothRunsFail_TwoRuns_Null_NotCached(RunStep failure)
    {
        var input = MakeInput("no-audio.mp4");
        var runner = new ScriptedRunner(PcmBytes, failure, failure, failure, failure);
        var svc = Waves(runner);

        var act = async () => await svc.GetPeaksAsync(input, 8, CancellationToken.None);
        var result = await act.Should().NotThrowAsync();

        result.Which.Should().BeNull();
        runner.CallCount.Should().Be(2, "one flagged run and exactly one retry");

        await svc.GetPeaksAsync(input, 8, CancellationToken.None);
        runner.CallCount.Should().Be(4, "nothing was cached, and the waveform keeps no failure memory");
        runner.Commands[2].Should().Contain(Flag);
    }

    // SPEC-006#I24 + I13 — a cancelled flagged run is not retried.
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public async Task Waveform_FlaggedRunCancelled_NotRetried_Null()
    {
        var input = MakeInput("clip.mp4");
        var runner = new ScriptedRunner(PcmBytes, RunStep.Cancel);

        var peaks = await Waves(runner).GetPeaksAsync(input, 1, CancellationToken.None);

        peaks.Should().BeNull();
        runner.CallCount.Should().Be(1);
    }

    // SPEC-006#I24 — the token is cancelled while the flagged run fails: no retry.
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public async Task Waveform_TokenCancelledDuringAFailedFlaggedRun_NotRetried()
    {
        var input = MakeInput("clip.mp4");
        using var cts = new CancellationTokenSource();
        var runner = new ScriptedRunner(PcmBytes, RunStep.FailAndCancel) { ToCancel = cts };

        var peaks = await Waves(runner).GetPeaksAsync(input, 1, cts.Token);

        peaks.Should().BeNull();
        runner.CallCount.Should().Be(1);
    }

    // SPEC-006#I24 — an input outside MP4/MOV is never retried.
    [Trait("serves-spec", "SPEC-006")]
    [Theory]
    [InlineData("clip.mkv", RunStep.Fail)]
    [InlineData("clip.ts", RunStep.WriteEmpty)]
    public async Task Waveform_NonIsoInputFailure_NeverRetried_OneRun(string name, RunStep failure)
    {
        var input = MakeInput(name);
        var runner = new ScriptedRunner(PcmBytes, failure);

        var peaks = await Waves(runner).GetPeaksAsync(input, 1, CancellationToken.None);

        peaks.Should().BeNull();
        runner.CallCount.Should().Be(1);
        runner.Commands[0].Should().NotContain(Flag);
    }

    // SPEC-006#I24 — the waveform keeps no memory: after a failed flagged run and a good retry, the next
    // extraction of that input (another bucket count, so not a cache hit) is flagged again.
    [Trait("serves-spec", "SPEC-006")]
    [Fact]
    public async Task Waveform_KeepsNoFailureMemory_TheNextExtractionIsFlaggedAgain()
    {
        var input = MakeInput("clip.mp4");
        var runner = new ScriptedRunner(PcmBytes, RunStep.Fail);
        var svc = Waves(runner);
        (await svc.GetPeaksAsync(input, 1, CancellationToken.None)).Should().NotBeNull();

        (await svc.GetPeaksAsync(input, 2, CancellationToken.None)).Should().NotBeNull();

        runner.CallCount.Should().Be(3);
        runner.Commands[2].Should().Contain(Flag);
    }
}

/// <summary>What <see cref="ScriptedRunner"/> does on one call.</summary>
public enum RunStep
{
    /// <summary>Exit 0 and write the runner's content to the output path.</summary>
    Write,

    /// <summary>Exit 0 and write an empty output file.</summary>
    WriteEmpty,

    /// <summary>Exit 0 and write nothing.</summary>
    NoFile,

    /// <summary>Exit 1 and write nothing.</summary>
    Fail,

    /// <summary>Exit 1 but leave the content behind at the output path (a partial write).</summary>
    FailLeavingFile,

    /// <summary>Throw <see cref="OperationCanceledException"/>, as the real runner does when cancelled.</summary>
    Cancel,

    /// <summary>Cancel <see cref="ScriptedRunner.ToCancel"/>, then exit 1 (the token fired while the run failed).</summary>
    FailAndCancel,

    /// <summary>Throw a plain <see cref="IOException"/> (not a cancellation).</summary>
    Throw,
}

/// <summary>
/// An <see cref="IFfmpegRunner"/> that plays a script of <see cref="RunStep"/>s, one per call (then <see cref="RunStep.Write"/>
/// once the script runs out). Every call is recorded before anything else happens, so a call made with a cancelled token
/// still counts.
/// </summary>
internal sealed class ScriptedRunner : IFfmpegRunner
{
    private readonly Queue<RunStep> _script;
    private readonly byte[] _content;

    public ScriptedRunner(byte[] content, params RunStep[] script)
    {
        _content = content;
        _script = new Queue<RunStep>(script);
    }

    public CancellationTokenSource? ToCancel { get; init; }

    public List<List<string>> Commands { get; } = new();

    public int CallCount => Commands.Count;

    public Task<FfmpegResult> RunAsync(
        FfmpegArgs args,
        TimeSpan? totalDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tokens = args.ToList().ToList();
        Commands.Add(tokens);
        ct.ThrowIfCancellationRequested();

        var output = tokens[^1];
        var step = _script.Count > 0 ? _script.Dequeue() : RunStep.Write;
        switch (step)
        {
            case RunStep.Write:
                Write(output, _content);
                return Ok();
            case RunStep.WriteEmpty:
                Write(output, Array.Empty<byte>());
                return Ok();
            case RunStep.NoFile:
                return Ok();
            case RunStep.Fail:
                return Failed();
            case RunStep.FailLeavingFile:
                Write(output, _content);
                return Failed();
            case RunStep.Cancel:
                throw new OperationCanceledException();
            case RunStep.FailAndCancel:
                ToCancel!.Cancel();
                return Failed();
            case RunStep.Throw:
                throw new IOException("the temp volume went away");
            default:
                throw new ArgumentOutOfRangeException(nameof(step), step, null);
        }
    }

    private static void Write(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, bytes);
    }

    private static Task<FfmpegResult> Ok() => Task.FromResult(new FfmpegResult(0, new List<string>().AsReadOnly()));

    private static Task<FfmpegResult> Failed() =>
        Task.FromResult(new FfmpegResult(1, new List<string> { "scripted failure" }.AsReadOnly()));
}

/// <summary>An <see cref="IFfprobeRunner"/> that records every command and answers a valid metadata payload.</summary>
internal sealed class RecordingFfprobeRunner : IFfprobeRunner
{
    public List<List<string>> Commands { get; } = new();

    public Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
    {
        Commands.Add(args.ToList().ToList());
        return Task.FromResult(ProbeCountingFfprobeRunner.MetadataJson(10));
    }
}
