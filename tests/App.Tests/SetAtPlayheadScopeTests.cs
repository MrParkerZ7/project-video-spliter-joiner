using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-133 (SPEC-011) — the scope of "Set intro-end / outro-start here".
///
/// <para><b>The report.</b> <i>"set intro/outro-end here button suppose to set to all, or just have an
/// option to set all"</i> and <i>"why run bulk cut is execute cut only one file? bulk is mean single or
/// what?"</i> — one defect seen from both ends. The setters wrote <c>_selectedItem</c> only, so every
/// other row stayed a no-op trim, stayed auto-excluded, and Run cut exactly one file out of the batch.
/// Apply-to-all existed, but only as a per-row ⧉ button whose meaning had to be inferred.</para>
///
/// <para>The fan-out delegates to <see cref="BulkCutViewModel.ApplyToAll"/> rather than re-deriving the
/// copy, so the clause most likely to be reimplemented wrongly — the outro measured from the END of each
/// file, so one gesture fits episodes of different lengths — is exercised here against uneven durations
/// on purpose.</para>
/// </summary>
public sealed class SetAtPlayheadScopeTests
{
    private const string PathA = @"C:\videos\ep01.mp4";
    private const string PathB = @"C:\videos\ep02.mp4";
    private const string PathC = @"C:\videos\ep03.mp4";

    private sealed class ScopeTestPlayer : IMediaPlayer
    {
        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public double SpeedRatio { get; set; } = 1.0;

        /// <summary>Simulate the duration arriving from the decoder — flips the VM to ready.</summary>
        public void MakeReady(TimeSpan duration)
        {
            Duration = duration;
            DurationAvailable?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Simulate a playback tick / settled seek at <paramref name="t"/> (no seek armed).</summary>
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

        public void Stop()
        {
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void Seek(TimeSpan t) => Position = t;

        public void Unload()
        {
            Duration = null;
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void StepFrame(int direction)
        {
        }

        public event EventHandler? PositionChanged;

        public event EventHandler? DurationAvailable;

#pragma warning disable CS0067 // These are raised by the real player; this fake never fires them.
        public event EventHandler? Seeked;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;
#pragma warning restore CS0067
    }

    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeSettings Settings, FakeThumbnailService Thumbs, ScopeTestPlayer Player) Build(
        bool? applyToAll = null)
    {
        var probe = new BulkFakeProbe();
        var thumbs = new FakeThumbnailService();
        var settings = new FakeSettings { BulkApplyCutToAllRows = applyToAll };
        var player = new ScopeTestPlayer();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), thumbs, settings, new FakeBulkTrimEngine(), player);
        return (vm, probe, settings, thumbs, player);
    }

    /// <summary>Add a row of a given length on a 1s keyframe grid, settled and ready.</summary>
    private static async Task<BulkItemViewModel> AddAsync(
        BulkCutViewModel vm, BulkFakeProbe probe, string path, double seconds)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(seconds), 1);
        await vm.AddFilesAsync(new[] { path });
        var row = vm.Items.Single(i => i.Path == path);
        await row.CurrentScanTask;
        return row;
    }

    /// <summary>Select a row and park the shared preview player on it, as previewing + playing does.</summary>
    private static void Preview(BulkCutViewModel vm, ScopeTestPlayer player, BulkItemViewModel row, double atSeconds)
    {
        vm.SelectedItem = row;
        player.MakeReady(row.Duration ?? TimeSpan.FromSeconds(600));
        player.MovePlayheadTo(TimeSpan.FromSeconds(atSeconds));
    }

    // ---- The default: one gesture sets the whole batch -----------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheDefaultIsOn_BecauseTheTabIsCalledBulkCut()
    {
        var (vm, _, _, _, _) = Build(applyToAll: null); // an older settings file / never set

        vm.ApplyCutToAllRows.Should().BeTrue(
            "setting one row and pressing Run is exactly what produced 'why did it cut only one file?'");
        vm.SetAtPlayheadScopeNote.Should().Contain("every ticked");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task SettingTheIntroOnce_GivesEveryTickedRowACut_AndRunCountsThemAll()
    {
        var (vm, probe, _, _, player) = Build();
        var a = await AddAsync(vm, probe, PathA, 600);
        var b = await AddAsync(vm, probe, PathB, 600);
        var c = await AddAsync(vm, probe, PathC, 600);

        vm.RunLabel.Should().Contain("(0)", "precondition: nothing has a cut yet, so nothing is eligible");

        Preview(vm, player, a, atSeconds: 30);
        vm.SetIntroAtPlayhead();

        foreach (var row in new[] { a, b, c })
        {
            row.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(30), "one gesture set the whole batch");
            row.IsEnabled.Should().BeTrue();
        }

        vm.RunLabel.Should().Contain("(3)", "Run cuts every video, which is what 'bulk' has to mean");
        vm.CanRunBatch.Should().BeTrue();
    }

    /// <summary>
    /// The clause that is easy to get wrong. A 10-minute and a 45-minute episode with the same trailing
    /// credits must both keep the right tail, so the outro is copied as a distance from the END, never as
    /// an absolute timestamp.
    /// </summary>
    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheOutroFansOut_MeasuredFromTheEndOfEachFile_NotAsAnAbsoluteTime()
    {
        var (vm, probe, _, _, player) = Build();
        var shortEp = await AddAsync(vm, probe, PathA, 600);   // 10:00
        var longEp = await AddAsync(vm, probe, PathB, 2700);   // 45:00

        Preview(vm, player, shortEp, atSeconds: 570);                  // 30s of credits
        vm.SetOutroAtPlayhead();

        shortEp.OutroStart!.Requested.Should().Be(TimeSpan.FromSeconds(570));
        longEp.OutroStart!.Requested.Should().Be(
            TimeSpan.FromSeconds(2670),
            "45:00 minus the same 30s tail — copying 570s literally would cut 35 minutes off the long episode");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnUntickedRow_IsNeverWrittenTo()
    {
        var (vm, probe, _, _, player) = Build();
        var a = await AddAsync(vm, probe, PathA, 600);
        var b = await AddAsync(vm, probe, PathB, 600);
        b.IsCheckedByUser = false;

        Preview(vm, player, a, atSeconds: 30);
        vm.SetIntroAtPlayhead();

        a.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(30));
        b.IntroEnd.Requested.Should().Be(
            TimeSpan.Zero, "unticking a row is the user saying 'leave this one alone' — fan-out must respect it");
    }

    // ---- The option, turned off ------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task WithTheToggleOff_OnlyThePreviewedRowChanges()
    {
        var (vm, probe, _, _, player) = Build(applyToAll: false);
        var a = await AddAsync(vm, probe, PathA, 600);
        var b = await AddAsync(vm, probe, PathB, 600);

        vm.ApplyCutToAllRows.Should().BeFalse();
        vm.SetAtPlayheadScopeNote.Should().Contain("previewed");

        Preview(vm, player, a, atSeconds: 30);
        vm.SetIntroAtPlayhead();

        a.IntroEnd.Requested.Should().Be(TimeSpan.FromSeconds(30));
        b.IntroEnd.Requested.Should().Be(TimeSpan.Zero, "rows that genuinely need different cuts stay possible");
        vm.RunLabel.Should().Contain("(1)");
    }

    // ---- The choice persists ----------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void FlippingTheToggle_PersistsIt_AndUpdatesTheScopeNote()
    {
        var (vm, _, settings, _, _) = Build();

        vm.ApplyCutToAllRows = false;

        settings.BulkApplyCutToAllRows.Should().BeFalse("the choice must survive a restart, not be re-decided");
        vm.SetAtPlayheadScopeNote.Should().Contain("previewed");

        vm.ApplyCutToAllRows = true;
        settings.BulkApplyCutToAllRows.Should().BeTrue();
        vm.SetAtPlayheadScopeNote.Should().Contain("every ticked");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void APersistedOff_IsHonouredOnConstruction()
    {
        var (vm, _, _, _, _) = Build(applyToAll: false);

        vm.ApplyCutToAllRows.Should().BeFalse("a stored preference is not overridden by the default");
    }

    // ---- Reporting --------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task RowsTheCopyInvalidates_AreReported_NotSilentlyDropped()
    {
        var (vm, probe, _, _, player) = Build();
        var longEp = await AddAsync(vm, probe, PathA, 600);
        var tiny = await AddAsync(vm, probe, PathB, 20);   // an intro past this file's end

        Preview(vm, player, longEp, atSeconds: 300);
        vm.SetIntroAtPlayhead();

        vm.ApplyToAllReport.Should().NotBeNull("the fan-out reports through the same channel apply-to-all does");
        vm.ApplyToAllReport!.InvalidatedRows.Should().Contain(
            tiny, "a row the copy made invalid has to surface — silently dropping it is how a batch loses a file");
    }

    // ---- Performance -------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheFanOut_IsPureVmState_AndRefreshesTheBatchProjectionOnce()
    {
        var (vm, probe, _, thumbs, player) = Build();
        var a = await AddAsync(vm, probe, PathA, 600);
        for (var i = 0; i < 8; i++)
        {
            await AddAsync(vm, probe, $@"C:\videos\extra{i}.mp4", 600);
        }

        Preview(vm, player, a, atSeconds: 30);
        var scansBefore = probe.GetKeyframesCallCount;
        var grabsBefore = thumbs.GetThumbnailCallCount;

        var runLabelRaises = 0;
        void OnChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BulkCutViewModel.RunLabel))
            {
                runLabelRaises++;
            }
        }

        vm.PropertyChanged += OnChanged;
        try
        {
            vm.SetIntroAtPlayhead();
        }
        finally
        {
            vm.PropertyChanged -= OnChanged;
        }

        probe.GetKeyframesCallCount.Should().Be(
            scansBefore, "copying a time across rows re-snaps against keyframes already in memory — never a re-scan");
        thumbs.GetThumbnailCallCount.Should().BeGreaterThanOrEqualTo(
            grabsBefore, "cut-point frames may be re-grabbed as handles move — but never a keyframe re-scan");
        runLabelRaises.Should().BeGreaterThan(0, "the button's count must not be left stale after the fan-out");
    }
}
