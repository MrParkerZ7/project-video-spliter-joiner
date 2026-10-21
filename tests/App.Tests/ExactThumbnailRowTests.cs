using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-196 (G-059, SPEC-011 I61 / I158, SPEC-007 I61 / I110) - a cut-point picture and a profile picture are EXACT-time grabs, at the time
/// the run cuts at, in ffmpeg's base. The fakes record which method each grab came through (<see cref="FakeThumbnailService.ExactRequests"/>
/// and <see cref="FakeThumbnailService.BucketedRequests"/>), so a chip that quietly went back to the bucketed request fails here
/// (the T-179 lesson: a fake must not hide the behaviour under test).
///
/// <para>The conversion: a snapped cut is a keyframe's pts and ffmpeg counts <c>-ss</c> from the format <c>start_time</c>, so a chip or
/// Save's picture at the snapped cut seeks to <c>max(0, Snapped - StartTime)</c>; a requested time (Exact on a row Exact cuts, the
/// player's position, a profile's offset) is already in ffmpeg's base and passes unchanged.</para>
/// </summary>
public sealed class ExactThumbnailRowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-t196-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private string MakeVideo(string name)
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "video bytes");
        return p;
    }

    private string MakeFrame()
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, "frame.jpg");
        File.WriteAllText(p, "frame-bytes");
        return p;
    }

    private static Task Immediate(TimeSpan _, CancellationToken ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

    private (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeThumbnailService Thumbs) Build(bool exact = false)
    {
        var probe = new BulkFakeProbe();
        var thumbs = new FakeThumbnailService { ThumbnailFactory = (_, _, _) => MakeFrame() };
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), thumbs, new FakeSettings(), new FakeBulkTrimEngine(),
            thumbnailStore: new ProfileThumbnailStore(Path.Combine(_dir, "store")),
            thumbnailDebounce: TimeSpan.FromMilliseconds(1), thumbnailDelay: Immediate);
        vm.LookupFileHolders = _ => Array.Empty<string>();
        vm.ExactCut = exact;
        return (vm, probe, thumbs);
    }

    private static async Task<BulkItemViewModel> AddAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, string? videoCodec = "h264", double? startSeconds = null, double step = 2)
    {
        probe.SetUniform(path, S(60), step);
        probe.SetCodecs(path, videoCodec, "aac");
        if (startSeconds is { } start)
        {
            probe.SetStartTime(path, S(start));
        }

        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask;
        return row;
    }

    private static void Settle(BulkItemViewModel row) =>
        row.InFlightGrabs.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the grab should finish promptly");

    private static IReadOnlyList<TimeSpan> ExactTimes(FakeThumbnailService thumbs, string path) =>
        thumbs.ExactRequests.Where(r => r.InputPath == path).Select(r => r.Time).ToList();

    // serves-spec: SPEC-011#I61 / SPEC-005#I28 - a Lossless chip is an EXACT-time grab at the snapped cut (the keyframe), never the
    // bucketed request that floored it a second early and decoded most of a GOP; the intro chip and the outro chip both.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task ALosslessChip_IsAnExactGrab_AtTheSnappedCut()
    {
        var (vm, probe, thumbs) = Build();
        var path = MakeVideo("clip.mp4");
        var row = await AddAsync(vm, probe, path);

        row.IntroEnd.Requested = S(11); // a 10/12 tie on the 2 s grid -> snaps to the earlier 10 s
        row.AddOutro(S(41));            // 40/42 tie -> 40 s
        Settle(row);

        ExactTimes(thumbs, path).Should().Contain(S(10), "the intro chip is the frame at the snapped intro");
        ExactTimes(thumbs, path).Should().Contain(S(40), "and the outro chip the frame at the snapped outro");
        thumbs.BucketedRequests.Should().BeEmpty("a cut-point chip is never a bucketed (floored) request");
    }

    // serves-spec: SPEC-011#I61 / SPEC-011#I158 - in Exact mode on a row Exact cuts, the chip is an exact grab at the REQUESTED cut.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnExactRowsChip_IsAnExactGrab_AtTheRequestedCut_Unconverted()
    {
        var (vm, probe, thumbs) = Build(exact: true);
        var path = MakeVideo("clip.mp4");
        var row = await AddAsync(vm, probe, path);

        row.IntroEnd.Requested = S(11.5);
        Settle(row);

        row.ExactCutApplies.Should().BeTrue("precondition: an H.264 MP4");
        ExactTimes(thumbs, path).Last().Should().Be(S(11.5), "a requested time is already in ffmpeg's base");
        thumbs.BucketedRequests.Should().BeEmpty();
    }

    // serves-spec: SPEC-011#I61 / SPEC-011#I164 - a row whose Exact cut falls back (HEVC), in Exact mode, grabs its chip exactly at the
    // SNAPPED cut: the run cuts at the keyframe, and so does the picture.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AFallBackRowInExact_GrabsItsChipExactly_AtTheSnappedCut()
    {
        var (vm, probe, thumbs) = Build(exact: true);
        var path = MakeVideo("phone.mp4");
        var row = await AddAsync(vm, probe, path, videoCodec: "hevc");

        row.IntroEnd.Requested = S(11);
        Settle(row);

        row.ExactCutApplies.Should().BeFalse("precondition: Exact cannot cut HEVC");
        ExactTimes(thumbs, path).Last().Should().Be(S(10));
        ExactTimes(thumbs, path).Should().NotContain(S(11));
        thumbs.BucketedRequests.Should().BeEmpty();
    }

    // serves-spec: SPEC-011#I61 / SPEC-004#I47 - on a row whose file starts at 1.445333 s (an MPEG-TS), in Lossless, the chip is grabbed at
    // Snapped - StartTime: ffmpeg counts -ss from the format start, so the keyframe's own time would land 1.4 s late. A snapped cut below
    // the start clamps to 0.
    [Trait("serves-spec", "SPEC-011")]
    [Theory]
    [InlineData(10.0, 8.554667)]
    [InlineData(1.0, 0.0)]
    public async Task OnARowThatDoesNotStartAtZero_TheChip_IsGrabbedAtTheCutMinusTheStart(double snappedSeconds, double expectedSeek)
    {
        var (vm, probe, thumbs) = Build();
        var path = MakeVideo("camera.mts");
        var row = await AddAsync(vm, probe, path, startSeconds: 1.445333, step: 1);

        row.IntroEnd.Requested = S(snappedSeconds);
        Settle(row);

        row.IntroEnd.Snapped.Should().Be(S(snappedSeconds), "precondition: on the 1 s grid");
        ExactTimes(thumbs, path).Last().TotalSeconds.Should().BeApproximately(expectedSeek, 1e-6);
        thumbs.BucketedRequests.Should().BeEmpty();
    }

    // serves-spec: SPEC-007#I61 - Save's automatic profile picture is an exact grab at the snapped intro minus the file's start.
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task SavesAutomaticPicture_IsAnExactGrab_AtTheSnappedIntroMinusTheStart()
    {
        var (vm, probe, thumbs) = Build();
        var path = MakeVideo("camera.mts");
        var row = await AddAsync(vm, probe, path, startSeconds: 1.445333);
        row.IntroEnd.Requested = S(10);
        Settle(row);
        vm.SelectedItem = row;
        var chipGrabs = thumbs.ExactRequests.Count;

        await vm.SaveProfileWithAutoThumbnailAsync("Camera OP");

        var grab = thumbs.ExactRequests.Skip(chipGrabs).Should().ContainSingle("one picture per save").Subject;
        grab.InputPath.Should().Be(path);
        grab.Time.TotalSeconds.Should().BeApproximately(10 - 1.445333, 1e-6);
        grab.Width.Should().Be(640);
        thumbs.BucketedRequests.Should().BeEmpty();
    }

    // serves-spec: SPEC-005#I28 / SPEC-011#I164 - before its probe lands a row has no start time to subtract: the cut time itself, so an
    // ordinary MP4 (start 0) is unchanged by T-196.
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task OnAnOrdinaryMp4_TheSeekOffset_IsTheCutItself()
    {
        var (vm, probe, thumbs) = Build();
        var path = MakeVideo("clip.mp4");
        var row = await AddAsync(vm, probe, path, startSeconds: 0.0);

        row.ToSeekOffset(S(12)).Should().Be(S(12));
        row.ToSeekOffset(S(0)).Should().Be(S(0));
        thumbs.BucketedRequests.Should().BeEmpty();
    }
}
