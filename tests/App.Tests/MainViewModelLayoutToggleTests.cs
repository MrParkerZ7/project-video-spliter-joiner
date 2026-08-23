using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Profiles;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-081 / D-001 — the vertical-monitor layout toggle on <see cref="MainViewModel"/>. The command
/// flips <see cref="MainViewModel.IsVertical"/> and writes the new axis through to
/// <see cref="IAppSettings.LayoutMode"/> (so the choice persists across launches); the initial
/// <c>IsVertical</c> is restored FROM the persisted setting on construction. Exercised through the
/// test ctor with fake screen VMs + a fake settings store, so no ffmpeg / render is needed.
/// </summary>
public sealed class MainViewModelLayoutToggleTests
{
    // ---- Minimal fakes (mirroring ViewModelSettingsTests) -----------------------------------

    private sealed class FakeSettings : IAppSettings
    {
        public string? LastInputDir { get; set; }
        public string? LastOutputDir { get; set; }
        public LayoutMode LayoutMode { get; set; } = LayoutMode.Horizontal;
        public double? HorizontalSplitRatio { get; set; }
        public double? VerticalSplitRatio { get; set; }
        public double? BulkHorizontalSplitRatio { get; set; }
        public double? BulkVerticalSplitRatio { get; set; }

        private readonly List<CutProfile> _cutProfiles = new();
        public IReadOnlyList<CutProfile> CutProfiles => _cutProfiles;
        public void SaveProfile(CutProfile profile)
        {
            var i = _cutProfiles.FindIndex(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { _cutProfiles[i] = profile; } else { _cutProfiles.Add(profile); }
        }
        public void DeleteProfile(string name) =>
            _cutProfiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeProbe : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResult.Success(
                new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>())));

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimeSpan>>(new[] { TimeSpan.Zero });

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
            => new(requested, TimeSpan.Zero);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.FromSeconds(2);
    }

    private sealed class NoOpSplitEngine : ISplitEngine
    {
        public Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<OperationStatus>? status = null, IProgress<PartProgress>? partProgress = null)
            => Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
    }

    private sealed class NoOpJoinEngine : IJoinEngine
    {
        public Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => Task.FromResult(CompatReport.Ok());

        public Task<JoinResult> JoinAsync(JoinRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<OperationStatus>? status = null)
            => Task.FromResult(JoinResult.Ok(req.OutputPath));
    }

    private static MainViewModel BuildViewModel(IAppSettings settings)
    {
        var split = new SplitViewModel(new FakeProbe(), new NoOpSplitEngine(), player: null, settings);
        var join = new JoinViewModel(new NoOpJoinEngine(), new FakeProbe(), settings);
        return new MainViewModel(split, join, settings);
    }

    // ---- Tests ------------------------------------------------------------------------------

    [Fact]
    public void DefaultsToHorizontal_WhenSettingIsHorizontal()
    {
        var vm = BuildViewModel(new FakeSettings { LayoutMode = LayoutMode.Horizontal });

        vm.IsVertical.Should().BeFalse("Horizontal is the default axis");
        vm.LayoutToggleTooltip.Should().Be("Switch to vertical layout",
            "the icon/tooltip name the mode the click switches TO (D5)");
    }

    [Fact]
    public void RestoresVertical_FromPersistedSetting_OnStartup()
    {
        var vm = BuildViewModel(new FakeSettings { LayoutMode = LayoutMode.Vertical });

        vm.IsVertical.Should().BeTrue("the app reopens in the last-used mode");
        vm.LayoutToggleTooltip.Should().Be("Switch to horizontal layout");
    }

    [Fact]
    public void ToggleCommand_FlipsIsVertical_AndWritesSettingThrough()
    {
        var settings = new FakeSettings { LayoutMode = LayoutMode.Horizontal };
        var vm = BuildViewModel(settings);

        vm.ToggleLayoutCommand.Execute(null);

        vm.IsVertical.Should().BeTrue("the toggle flips the axis");
        settings.LayoutMode.Should().Be(LayoutMode.Vertical, "the flip writes through to settings (persistence)");
        vm.LayoutToggleTooltip.Should().Be("Switch to horizontal layout", "the tooltip now targets horizontal");

        // Flipping back returns to Horizontal and writes it through again.
        vm.ToggleLayoutCommand.Execute(null);

        vm.IsVertical.Should().BeFalse();
        settings.LayoutMode.Should().Be(LayoutMode.Horizontal);
    }

    [Fact]
    public void SettingIsVertical_RaisesPropertyChanged_ForIsVerticalAndTooltip()
    {
        var vm = BuildViewModel(new FakeSettings());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsVertical = true;

        changed.Should().Contain(nameof(MainViewModel.IsVertical));
        changed.Should().Contain(nameof(MainViewModel.LayoutToggleTooltip),
            "the target-mode tooltip must refresh when the axis flips");
    }

    [Fact]
    public void PerAxisRatios_WriteThroughIndependently()
    {
        var settings = new FakeSettings();
        var vm = BuildViewModel(settings);

        vm.HorizontalSplitRatio = 0.55;
        vm.VerticalSplitRatio = 0.4;

        settings.HorizontalSplitRatio.Should().Be(0.55, "horizontal ratio persists on its own key");
        settings.VerticalSplitRatio.Should().Be(0.4, "vertical ratio persists independently (D6)");
    }

    [Fact]
    public void SplitRatios_AreClampedToASaneBand()
    {
        var vm = BuildViewModel(new FakeSettings());

        vm.HorizontalSplitRatio = 5.0;   // absurd → clamped to the upper bound
        vm.VerticalSplitRatio = -1.0;    // absurd → clamped to the lower bound

        vm.HorizontalSplitRatio.Should().BeLessThanOrEqualTo(0.95);
        vm.VerticalSplitRatio.Should().BeGreaterThanOrEqualTo(0.05);
    }
}
