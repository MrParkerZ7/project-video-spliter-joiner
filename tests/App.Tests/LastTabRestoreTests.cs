using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-143 (SPEC-009) — the app reopens on the screen you left it on.
///
/// <para>The layout axis already persisted (<c>LayoutMode</c>, D-001/T-081); the tab did not, so every
/// launch started on Split. These tests cover the tab AND assert the axis still restores, so adding one
/// cannot quietly regress the other.</para>
///
/// <para>The value is stored as a NAMED <see cref="AppTab"/>, not a raw index: a stored int silently
/// points at the wrong screen the moment tabs are reordered.</para>
/// </summary>
public sealed class LastTabRestoreTests : IDisposable
{
    private readonly string _dir;

    public LastTabRestoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-tab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string NewFile() => Path.Combine(_dir, "settings-" + Guid.NewGuid().ToString("N") + ".json");

    // ---- The store ---------------------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-009")]
    [Theory]
    [InlineData(AppTab.Split)]
    [InlineData(AppTab.Join)]
    [InlineData(AppTab.BulkCut)]
    public void TheTabRoundTrips(AppTab tab)
    {
        var path = NewFile();
        _ = new AppSettings(path) { LastTab = tab };

        new AppSettings(path).LastTab.Should().Be(tab);
    }

    [Trait("serves-spec", "SPEC-009")]
    [Fact]
    public void AnOlderFileWithNoTabKey_LoadsAndMeansSplit()
    {
        var path = NewFile();
        File.WriteAllText(path, "{ \"lastInputDir\": \"D:\\\\v\", \"layoutMode\": \"Vertical\" }");

        var settings = new AppSettings(path);

        settings.LastTab.Should().BeNull("absent is not a value — it reads as the default");
        settings.LayoutMode.Should().Be(LayoutMode.Vertical, "the sibling keys still load");
    }

    /// <summary>
    /// A stored value that no longer maps to a tab — a hand-edited file, or a future build with fewer
    /// screens — must not throw or leave the window on nothing.
    /// </summary>
    [Trait("serves-spec", "SPEC-009")]
    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    public void AnUnrecognisedStoredTab_FallsBackInsteadOfThrowing(string raw)
    {
        var path = NewFile();
        File.WriteAllText(path, "{ \"lastTab\": " + raw + " }");

        AppSettings? settings = null;
        Action load = () => settings = new AppSettings(path);

        load.Should().NotThrow("a corrupt preference must never stop the app starting");
        settings!.LastTab.Should().BeNull("unrecognised reads as 'no preference', i.e. Split");
    }

    [Trait("serves-spec", "SPEC-009")]
    [Fact]
    public void AddingTheTabKey_DoesNotDisturbTheExistingKeys()
    {
        // T-133 hazard: a new DTO field inserted between a [JsonPropertyName] and its property silently
        // renamed a persisted key and would have orphaned every user's saved value.
        var path = NewFile();
        _ = new AppSettings(path)
        {
            LastTab = AppTab.BulkCut,
            LayoutMode = LayoutMode.Vertical,
            BulkHorizontalSplitRatio = 0.42,
            LastInputDir = @"D:\in",
        };

        var json = File.ReadAllText(path);
        json.Should().Contain("lastTab")
            .And.Contain("layoutMode")
            .And.Contain("bulkHorizontalSplitRatio")
            .And.Contain("lastInputDir");

        var reloaded = new AppSettings(path);
        reloaded.LastTab.Should().Be(AppTab.BulkCut);
        reloaded.LayoutMode.Should().Be(LayoutMode.Vertical);
        reloaded.BulkHorizontalSplitRatio.Should().Be(0.42);
        reloaded.LastInputDir.Should().Be(@"D:\in");
    }

    // ---- The view model ----------------------------------------------------------------------------

    private sealed class TabFakeSettings : IAppSettings
    {
        public string? LastInputDir { get; set; }

        public string? LastOutputDir { get; set; }

        public LayoutMode LayoutMode { get; set; }

        public AppTab? LastTab { get; set; }

        public bool? BulkApplyCutToAllRows { get; set; }

        public double? HorizontalSplitRatio { get; set; }

        public double? VerticalSplitRatio { get; set; }

        public double? BulkHorizontalSplitRatio { get; set; }

        public double? BulkVerticalSplitRatio { get; set; }

        public System.Collections.Generic.IReadOnlyList<VideoSplitJoiner.Core.Profiles.CutProfile> CutProfiles
            => Array.Empty<VideoSplitJoiner.Core.Profiles.CutProfile>();

        public void SaveProfile(VideoSplitJoiner.Core.Profiles.CutProfile profile) { }

        public void DeleteProfile(string name) { }
    }

    private static MainViewModel Build(IAppSettings settings) =>
        new(new SplitViewModel(new BulkFakeProbe(), new ThrowingFakeSplitEngine(), player: null, settings),
            joinViewModel: null,
            settings: settings);

    [Trait("serves-spec", "SPEC-009")]
    [Theory]
    [InlineData(AppTab.Split, 0)]
    [InlineData(AppTab.Join, 1)]
    [InlineData(AppTab.BulkCut, 2)]
    public void TheStoredTab_IsRestoredOnStartup(AppTab stored, int expectedIndex)
    {
        var vm = Build(new TabFakeSettings { LastTab = stored });

        vm.SelectedTabIndex.Should().Be(expectedIndex);
    }

    [Trait("serves-spec", "SPEC-009")]
    [Fact]
    public void WithNoStoredTab_ItStartsOnSplit()
    {
        Build(new TabFakeSettings { LastTab = null }).SelectedTabIndex.Should().Be(0);
    }

    [Trait("serves-spec", "SPEC-009")]
    [Fact]
    public void SwitchingTabs_PersistsTheChoice()
    {
        var settings = new TabFakeSettings();
        var vm = Build(settings);

        vm.SelectedTabIndex = 2;

        settings.LastTab.Should().Be(AppTab.BulkCut, "it has to survive a restart, not just the session");
    }

    /// <summary>Adding the tab must not regress the axis, which already worked.</summary>
    [Trait("serves-spec", "SPEC-009")]
    [Theory]
    [InlineData(LayoutMode.Vertical, true)]
    [InlineData(LayoutMode.Horizontal, false)]
    public void TheLayoutAxisStillRestores(LayoutMode mode, bool expectVertical)
    {
        var vm = Build(new TabFakeSettings { LayoutMode = mode, LastTab = AppTab.Join });

        vm.IsVertical.Should().Be(expectVertical);
        vm.SelectedTabIndex.Should().Be(1, "both are restored, not one at the cost of the other");
    }

    /// <summary>
    /// Restoring a tab is startup STATE, not a tab SWITCH: it must not write the same value straight back
    /// to disk, and it must not run the switching side effects.
    /// </summary>
    [Trait("serves-spec", "SPEC-009")]
    [Fact]
    public void RestoringATab_DoesNotWriteBack()
    {
        var settings = new TabFakeSettings { LastTab = AppTab.BulkCut };
        var writes = 0;
        var tracking = new WriteCountingSettings(settings, () => writes++);

        var vm = Build(tracking);

        vm.SelectedTabIndex.Should().Be(2);
        writes.Should().Be(0, "startup restore is not a user gesture — it has nothing new to persist");
    }

    private sealed class WriteCountingSettings : IAppSettings
    {
        private readonly IAppSettings _inner;
        private readonly Action _onWrite;

        public WriteCountingSettings(IAppSettings inner, Action onWrite)
        {
            _inner = inner;
            _onWrite = onWrite;
        }

        public string? LastInputDir { get => _inner.LastInputDir; set { _inner.LastInputDir = value; _onWrite(); } }

        public string? LastOutputDir { get => _inner.LastOutputDir; set { _inner.LastOutputDir = value; _onWrite(); } }

        public LayoutMode LayoutMode { get => _inner.LayoutMode; set { _inner.LayoutMode = value; _onWrite(); } }

        public AppTab? LastTab { get => _inner.LastTab; set { _inner.LastTab = value; _onWrite(); } }

        public bool? BulkApplyCutToAllRows { get => _inner.BulkApplyCutToAllRows; set { _inner.BulkApplyCutToAllRows = value; _onWrite(); } }

        public double? HorizontalSplitRatio { get => _inner.HorizontalSplitRatio; set { _inner.HorizontalSplitRatio = value; _onWrite(); } }

        public double? VerticalSplitRatio { get => _inner.VerticalSplitRatio; set { _inner.VerticalSplitRatio = value; _onWrite(); } }

        public double? BulkHorizontalSplitRatio { get => _inner.BulkHorizontalSplitRatio; set { _inner.BulkHorizontalSplitRatio = value; _onWrite(); } }

        public double? BulkVerticalSplitRatio { get => _inner.BulkVerticalSplitRatio; set { _inner.BulkVerticalSplitRatio = value; _onWrite(); } }

        public System.Collections.Generic.IReadOnlyList<VideoSplitJoiner.Core.Profiles.CutProfile> CutProfiles => _inner.CutProfiles;

        public void SaveProfile(VideoSplitJoiner.Core.Profiles.CutProfile profile) => _inner.SaveProfile(profile);

        public void DeleteProfile(string name) => _inner.DeleteProfile(name);
    }
}
