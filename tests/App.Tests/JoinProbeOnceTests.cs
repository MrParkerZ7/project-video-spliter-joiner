using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-193 on the Join screen: the view model, the real <see cref="JoinEngine"/> and the real <see cref="MediaProbe"/>
/// share ONE probe, as <c>MainViewModel</c> wires them. Adding clips probes each clip once (the chip probe; the
/// compatibility check's probes are cache hits), a remove or a reorder runs no metadata probe at all, and a Run starts
/// copying without probing again. The banner and <see cref="JoinViewModel.IsCompatible"/> still follow the list.
/// Binary-free: a counting <see cref="IFfprobeRunner"/> answers the probes and a fake ffmpeg writes the output.
/// </summary>
public sealed class JoinProbeOnceTests : IDisposable
{
    private readonly string _dir;

    public JoinProbeOnceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-join-probe-once-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Clip(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, name);
        return path;
    }

    private (JoinViewModel Vm, CountingFfprobeRunner Runner) Build()
    {
        var runner = new CountingFfprobeRunner();
        var probe = new MediaProbe(runner);
        var engine = new JoinEngine(new OutputWritingFfmpegRunner(), probe, new ErrorLogWriter(Path.Combine(_dir, "logs")));
        return (new JoinViewModel(engine, probe, new FakeSettings()), runner);
    }

    // serves-spec: SPEC-004#I33 / SPEC-012#I4 — adding 3 clips makes 3 metadata probes (6 before T-193: 3 chip probes
    // and the compatibility check's 3); the chips and the banner are filled as before.
    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public async Task AddingThreeClips_ProbesEachClipOnce_AndFillsChipsAndBanner()
    {
        var (vm, runner) = Build();

        await vm.AddFilesAsync(new[] { Clip("a.mp4"), Clip("b.mp4"), Clip("c.mp4") });

        runner.MetadataCalls.Should().Be(3, "the compatibility check re-reads the chip probes from the cache");
        vm.IsCompatible.Should().BeTrue();
        vm.CompatSummary.Should().Be("3 clips ready to join.");
        vm.Items.Should().OnlyContain(i => i.InfoText == "h264 · 320x240" && i.Duration == TimeSpan.FromSeconds(10));
    }

    // serves-spec: SPEC-004#I33 / SPEC-012 — a reorder and a remove run the compatibility check over the new list with
    // zero metadata probes, and the verdict follows the list: the incompatible clip moved to the front becomes the
    // reference the others are compared against; removing it makes the rest joinable.
    [Trait("serves-spec", "SPEC-012")]
    [Fact]
    public async Task ReorderAndRemove_RunNoMetadataProbe_AndTheBannerFollowsTheNewList()
    {
        var (vm, runner) = Build();
        var hevc = Clip("c-hevc.mp4");
        runner.VideoCodecFor[hevc] = "hevc";
        await vm.AddFilesAsync(new[] { Clip("a.mp4"), Clip("b.mp4"), hevc });
        vm.IsCompatible.Should().BeFalse();
        vm.CompatSummary.Should().Contain("clip 3 video codec is 'hevc'");
        var afterAdd = runner.MetadataCalls;

        await vm.MoveAsync(2, 0);

        (runner.MetadataCalls - afterAdd).Should().Be(0, "a reorder re-checks from the cache");
        vm.IsCompatible.Should().BeFalse();
        vm.CompatSummary.Should().Contain("reference (clip 1) is 'hevc'", "the check ran over the reordered list");

        var afterMove = runner.MetadataCalls;
        vm.RemoveCommand.Execute(vm.Items[0]);

        (runner.MetadataCalls - afterMove).Should().Be(0, "a remove re-checks from the cache");
        vm.IsCompatible.Should().BeTrue();
        vm.CompatSummary.Should().Be("2 clips ready to join.");
    }

    // serves-spec: SPEC-004#I33 / SPEC-003 — a Run after the adds makes no metadata probe (6 before T-193: the
    // pre-flight's 3 and SumDurationsAsync's 3) and still completes through the engine's stages.
    [Trait("serves-spec", "SPEC-003")]
    [Fact]
    public async Task RunAfterAdding_MakesNoMetadataProbe_AndCompletes()
    {
        var (vm, runner) = Build();
        await vm.AddFilesAsync(new[] { Clip("a.mp4"), Clip("b.mp4"), Clip("c.mp4") });
        vm.OutputPath = Path.Combine(_dir, "joined.mp4");
        var afterAdd = runner.MetadataCalls;

        await vm.RunJoinAsync();

        vm.Operation.State.Should().Be(OperationState.Completed);
        vm.LastResult!.Success.Should().BeTrue();
        File.Exists(vm.OutputPath).Should().BeTrue();
        (runner.MetadataCalls - afterAdd).Should().Be(0, "the run's pre-flight and duration sum are cache hits");
    }

    /// <summary>Answers metadata probes with an H.264 (or a per-path codec) 10 s payload and counts them.</summary>
    private sealed class CountingFfprobeRunner : IFfprobeRunner
    {
        private int _metadataCalls;

        public Dictionary<string, string> VideoCodecFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int MetadataCalls => Volatile.Read(ref _metadataCalls);

        public Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
        {
            var list = args.ToList();
            if (!list.Contains("-show_format"))
            {
                return Task.FromResult("""{"packets":[{"pts_time":"0.0","dts_time":"0.0","flags":"K__"}]}""");
            }

            Interlocked.Increment(ref _metadataCalls);
            var input = list[list.ToList().IndexOf("-i") + 1];
            var codec = VideoCodecFor.TryGetValue(input, out var c) ? c : "h264";
            return Task.FromResult(
                "{\"streams\":[{\"index\":0,\"codec_name\":\"" + codec + "\",\"codec_type\":\"video\",\"width\":320,"
                + "\"height\":240,\"pix_fmt\":\"yuv420p\",\"time_base\":\"1/15360\"}],"
                + "\"format\":{\"format_name\":\"mov,mp4,m4a,3gp,3g2,mj2\",\"duration\":\"10.000000\"}}");
        }
    }

    /// <summary>"Succeeds" by writing the output path (the last argument).</summary>
    private sealed class OutputWritingFfmpegRunner : IFfmpegRunner
    {
        public Task<FfmpegResult> RunAsync(
            FfmpegArgs args,
            TimeSpan? totalDuration = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            File.WriteAllText(args.ToList()[^1], "joined");
            return Task.FromResult(new FfmpegResult(0, Array.Empty<string>()));
        }
    }
}
