using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using System.IO;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-135 (SPEC-007) — "use the frame I am looking at as this profile's picture".
///
/// <para>Reported as <i>"I want button snapt-shot current play video into thumnail of profile thumnail"</i>.
/// Choosing a profile picture previously meant either accepting the automatic intro-end grab or
/// screenshotting with another tool and uploading the file — for a frame the app already had decoded on
/// screen.</para>
///
/// <para>This is a THIRD entry point onto the existing store-and-attach path, so the tests below care
/// most about the things a third entry point gets wrong: capturing the wrong TIME, using the wrong WIDTH
/// (so the stored picture does not match the auto path's), and inheriting the auto path's silence instead
/// of the upload path's reporting. It is a gesture the user deliberately pressed, so it must report.</para>
/// </summary>
public sealed class SnapshotProfileThumbnailTests : IDisposable
{
    private const string PathA = @"C:\videos\ep01.mp4";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-snap-" + Guid.NewGuid().ToString("N"));

    public SnapshotProfileThumbnailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>A real file standing in for a grabbed frame — the store copies it, so it must exist.</summary>
    private string MakeFrame(string name = "frame.jpg", string content = "frame-bytes")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class SnapPlayer : IMediaPlayer
    {
        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        public void MakeReady(TimeSpan duration)
        {
            Duration = duration;
            DurationAvailable?.Invoke(this, EventArgs.Empty);
        }

        public void MovePlayheadTo(TimeSpan t)
        {
            Position = t;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Open(string path)
        {
            IsPlaying = false;
            Duration = null;
            Position = TimeSpan.Zero;
        }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;

        public void Stop() { IsPlaying = false; Position = TimeSpan.Zero; }

        public void Seek(TimeSpan t) => Position = t;

        public void Unload() { Duration = null; IsPlaying = false; Position = TimeSpan.Zero; }

        public void StepFrame(int direction) { }

        public event EventHandler? PositionChanged;

        public event EventHandler? DurationAvailable;

#pragma warning disable CS0067
        public event EventHandler? Seeked;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    private (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeThumbnailService Thumbs, FakeSettings Settings, SnapPlayer Player) Build()
    {
        var probe = new BulkFakeProbe();
        var thumbs = new FakeThumbnailService();
        var settings = new FakeSettings();
        var player = new SnapPlayer();
        var store = new ProfileThumbnailStore(Path.Combine(_dir, "store"));

        // The default fake grab returns null; script a REAL file so the store has something to copy.
        thumbs.ThumbnailFactory = (_, _, _) => MakeFrame();

        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), thumbs, settings, new FakeBulkTrimEngine(), player,
            thumbnailStore: store);
        return (vm, probe, thumbs, settings, player);
    }

    private async Task<BulkItemViewModel> AddReadyRowAsync(BulkCutViewModel vm, BulkFakeProbe probe)
    {
        probe.SetUniform(PathA, TimeSpan.FromSeconds(600), 1);
        await vm.AddFilesAsync(new[] { PathA });
        var row = vm.Items.Single();
        await row.CurrentScanTask;
        return row;
    }

    /// <summary>Get to the state the gesture is meant for: a saved profile selected, a frame on screen.</summary>
    private async Task<CutProfile> ReadyToSnapAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, SnapPlayer player, double atSeconds)
    {
        await AddReadyRowAsync(vm, probe);
        vm.SaveProfile("Anime OP");
        player.MakeReady(TimeSpan.FromSeconds(600));
        player.MovePlayheadTo(TimeSpan.FromSeconds(atSeconds));
        return vm.SelectedProfile!;
    }

    // ---- The gesture -----------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ItCapturesTheFrameAtThePlayhead_AtTheProfileThumbnailWidth()
    {
        var (vm, probe, thumbs, _, player) = Build();
        await ReadyToSnapAsync(vm, probe, player, atSeconds: 91);
        var grabsBefore = thumbs.Requests.Count;

        (await vm.SnapshotProfileThumbnailAsync()).Should().BeTrue();

        var grab = thumbs.Requests.Skip(grabsBefore).Should().ContainSingle(
            "exactly one frame is captured per press").Subject;

        grab.InputPath.Should().Be(PathA);
        grab.Time.Should().Be(
            TimeSpan.FromSeconds(91),
            "the point of the gesture is THIS frame — not the intro-end the automatic capture uses");
        grab.Width.Should().Be(96, "it must match the auto path's width or the stored pictures differ in size");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task TheCapturedFrame_BecomesTheProfilesStoredThumbnail()
    {
        var (vm, probe, _, settings, player) = Build();
        var profile = await ReadyToSnapAsync(vm, probe, player, atSeconds: 42);

        (await vm.SnapshotProfileThumbnailAsync()).Should().BeTrue();

        var persisted = settings.CutProfiles.Single(p =>
            string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        persisted.ThumbnailPath.Should().NotBeNullOrWhiteSpace(
            "the picture is folded onto the PERSISTED profile, so it survives a restart");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task MovingThePlayhead_AndSnappingAgain_CapturesTheNewFrame()
    {
        var (vm, probe, thumbs, _, player) = Build();
        await ReadyToSnapAsync(vm, probe, player, atSeconds: 10);

        await vm.SnapshotProfileThumbnailAsync();
        player.MovePlayheadTo(TimeSpan.FromSeconds(300));
        await vm.SnapshotProfileThumbnailAsync();

        thumbs.Requests.Select(r => r.Time).Should().ContainInOrder(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(300));
    }

    // ---- The gate --------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task WithoutASelectedProfile_ItIsDisabled_AndSaysWhy()
    {
        var (vm, probe, thumbs, _, player) = Build();
        await AddReadyRowAsync(vm, probe);
        player.MakeReady(TimeSpan.FromSeconds(600));
        player.MovePlayheadTo(TimeSpan.FromSeconds(5));

        vm.CanSnapshotProfileThumbnail.Should().BeFalse();
        vm.SnapshotProfileThumbnailCommand.CanExecute(null).Should().BeFalse();
        vm.SnapshotUnavailableReason.Should().Contain(
            "profile", "an inert button with no explanation is what made the upload unreachable in G-044");

        var before = thumbs.Requests.Count;
        (await vm.SnapshotProfileThumbnailAsync()).Should().BeFalse();
        thumbs.Requests.Count.Should().Be(before, "a refused gesture must not pay for a frame grab");
        vm.Operation.Error.Should().NotBeNull("and it reports, because the user pressed it deliberately");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task WithoutAVideoOnScreen_ItIsDisabled_AndSaysWhy()
    {
        var (vm, probe, thumbs, _, _) = Build();
        await AddReadyRowAsync(vm, probe);
        vm.SaveProfile("Anime OP");   // a profile exists, but the player was never made ready

        vm.CanSnapshotProfileThumbnail.Should().BeFalse();
        vm.SnapshotUnavailableReason.Should().Contain("preview");

        var before = thumbs.Requests.Count;
        (await vm.SnapshotProfileThumbnailAsync()).Should().BeFalse();
        thumbs.Requests.Count.Should().Be(before, "there is no frame to capture, so nothing is grabbed");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task WhenEverythingIsReady_TheGateOpens_AndTheReasonGoesAway()
    {
        var (vm, probe, _, _, player) = Build();
        await ReadyToSnapAsync(vm, probe, player, atSeconds: 12);

        vm.CanSnapshotProfileThumbnail.Should().BeTrue();
        vm.SnapshotProfileThumbnailCommand.CanExecute(null).Should().BeTrue();
        vm.SnapshotUnavailableReason.Should().BeNull("nothing is blocking it, so there is nothing to explain");
    }

    // ---- Failure reports, because this is a deliberate gesture -------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task AFailedGrab_IsReported_AndLeavesTheExistingThumbnailAlone()
    {
        var (vm, probe, thumbs, settings, player) = Build();
        var profile = await ReadyToSnapAsync(vm, probe, player, atSeconds: 20);

        await vm.SnapshotProfileThumbnailAsync();          // a good one first
        var good = settings.CutProfiles.Single(p => p.Name == profile.Name).ThumbnailPath;
        good.Should().NotBeNullOrWhiteSpace();

        thumbs.ThumbnailFactory = (_, _, _) => null;        // the grab now fails
        vm.Operation.Reset();

        (await vm.SnapshotProfileThumbnailAsync()).Should().BeFalse();

        vm.Operation.Error.Should().NotBeNull(
            "the auto capture is deliberately silent (SPEC-007 I66); a button the user pressed is not");
        settings.CutProfiles.Single(p => p.Name == profile.Name).ThumbnailPath.Should().Be(
            good, "a failed capture must never cost the user the picture they already had");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ASuccessfulSnapshot_RetractsAnEarlierReport()
    {
        var (vm, probe, thumbs, _, player) = Build();
        await ReadyToSnapAsync(vm, probe, player, atSeconds: 20);

        thumbs.ThumbnailFactory = (_, _, _) => null;
        await vm.SnapshotProfileThumbnailAsync();
        vm.Operation.Error.Should().NotBeNull("precondition: something was reported");

        thumbs.ThumbnailFactory = (_, _, _) => MakeFrame(); // the grab works again
        (await vm.SnapshotProfileThumbnailAsync()).Should().BeTrue();

        vm.Operation.Error.Should().BeNull("a stale error must not outlive the problem it described");
    }

    // ---- Performance -------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public async Task ASnapshot_CostsOneGrab_AndNoReProbe()
    {
        var (vm, probe, thumbs, _, player) = Build();
        await ReadyToSnapAsync(vm, probe, player, atSeconds: 33);
        var scansBefore = probe.GetKeyframesCallCount;
        var grabsBefore = thumbs.Requests.Count;

        await vm.SnapshotProfileThumbnailAsync();

        thumbs.Requests.Count.Should().Be(grabsBefore + 1, "exactly one frame per press");
        probe.GetKeyframesCallCount.Should().Be(
            scansBefore, "capturing a frame is not a reason to re-scan keyframes");
    }
}
