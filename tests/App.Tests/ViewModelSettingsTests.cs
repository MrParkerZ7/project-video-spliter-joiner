using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Profiles;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Tests for the T-038 cross-session folder memory wiring in <see cref="SplitViewModel"/> and
/// <see cref="JoinViewModel"/>: a successful load remembers the input folder; a successful split/join
/// remembers the output folder. T-061 revised the Split output-dir default: it now re-anchors to the
/// LOADED FILE'S folder on every load (and is NO LONGER seeded from the remembered
/// <see cref="IAppSettings.LastOutputDir"/>) — see the re-anchor tests below. <see cref="IAppSettings.LastInputDir"/>
/// (the file-picker memory) is untouched. A fake <see cref="IAppSettings"/> keeps the tests off the
/// real APPDATA and lets us assert exact writes.
/// </summary>
public sealed class ViewModelSettingsTests : IDisposable
{
    // A real folder on disk so the "does the remembered dir still exist?" guard passes.
    private readonly string _existingDir;
    private readonly string _inputFile;

    public ViewModelSettingsTests()
    {
        _existingDir = Path.Combine(Path.GetTempPath(), "vsj-vm-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_existingDir);
        _inputFile = Path.Combine(_existingDir, "clip.mp4");
        File.WriteAllText(_inputFile, "not really a video");
    }

    public void Dispose()
    {
        try { Directory.Delete(_existingDir, recursive: true); } catch { /* best-effort */ }
    }

    // ---- Fakes ------------------------------------------------------------------------------

    private sealed class FakeSettings : IAppSettings
    {
        public string? LastInputDir { get; set; }
        public string? LastOutputDir { get; set; }
        public LayoutMode LayoutMode { get; set; } = LayoutMode.Horizontal;
        public double? HorizontalSplitRatio { get; set; }
        public double? VerticalSplitRatio { get; set; }
        public AppTab? LastTab { get; set; }

        public bool? BulkApplyCutToAllRows { get; set; }
        public bool? BulkAutoDeleteOriginals { get; set; }
        public bool? BulkAutoEmptyRecycleBin { get; set; }

        public bool? SplitAutoDeleteSource { get; set; }

        public bool? SplitAutoEmptyRecycleBin { get; set; }

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
        public ProbeResult ProbeResultToReturn { get; set; } = ProbeResult.Success(
            new MediaInfo(TimeSpan.FromSeconds(60), "mp4", Array.Empty<StreamInfo>(), Array.Empty<StreamInfo>()));

        public IReadOnlyList<TimeSpan> KeyframesToReturn { get; set; } =
            new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2) };

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(ProbeResultToReturn);

        public Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
            => Task.FromResult(KeyframesToReturn);

        public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
            => new(requested, TimeSpan.Zero);

        public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes) => TimeSpan.FromSeconds(2);
    }

    private sealed class NoOpSplitEngine : ISplitEngine
    {
        public Task<SplitResult> SplitAsync(SplitRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null, IProgress<VideoSplitJoiner.Core.Split.PartProgress>? partProgress = null)
            => Task.FromResult(new SplitResult(Array.Empty<SplitSegment>(), Array.Empty<string>()));
    }

    private sealed class NoOpJoinEngine : IJoinEngine
    {
        public Task<CompatReport> CheckCompatibilityAsync(IReadOnlyList<string> inputPaths, CancellationToken ct = default)
            => Task.FromResult(CompatReport.Ok());

        public Task<JoinResult> JoinAsync(JoinRequest req, IProgress<double>? progress = null, CancellationToken ct = default, IProgress<VideoSplitJoiner.Core.Ffmpeg.OperationStatus>? status = null)
            => Task.FromResult(JoinResult.Ok(req.OutputPath));
    }

    // ---- Split ------------------------------------------------------------------------------

    [Fact]
    public async Task Split_Load_RemembersInputFolder()
    {
        var settings = new FakeSettings();
        var vm = new SplitViewModel(new FakeProbe(), new NoOpSplitEngine(), player: null, settings);

        await vm.LoadAsync(_inputFile);

        settings.LastInputDir.Should().Be(_existingDir, "a successful load remembers the input file's folder");
    }

    [Fact]
    public async Task Split_Load_OutputDirDefault_IsLoadedFileFolder_IgnoringRememberedLastOutputDir()
    {
        // T-061: a remembered output folder is IGNORED as the default — the loaded file's folder wins.
        var otherDir = Path.Combine(_existingDir, "prev-out");
        Directory.CreateDirectory(otherDir);
        var settings = new FakeSettings { LastOutputDir = otherDir };

        var vm = new SplitViewModel(new FakeProbe(), new NoOpSplitEngine(), player: null, settings);
        vm.OutputDir.Should().BeEmpty("T-061: the ctor no longer seeds OutputDir from LastOutputDir");

        await vm.LoadAsync(_inputFile);

        vm.OutputDir.Should().Be(_existingDir, "T-061: the default is the loaded file's folder, not the remembered output dir");
    }

    [Fact]
    public async Task Split_Load_OutputDirDefault_IsInputFolder_WhenNoRememberedOutput()
    {
        var settings = new FakeSettings(); // no LastOutputDir
        var vm = new SplitViewModel(new FakeProbe(), new NoOpSplitEngine(), player: null, settings);

        await vm.LoadAsync(_inputFile);

        vm.OutputDir.Should().Be(_existingDir, "the default is the loaded file's folder");
    }

    [Fact]
    public async Task Split_OutputDir_StaysEditable_ThenReAnchorsOnNextLoad()
    {
        // T-061: OutputDir defaults to file X's folder, stays editable (set to Y), then RE-ANCHORS
        // to a new file Z's folder on the next load — the manual Y value is discarded.
        var folderX = Path.Combine(_existingDir, "x");
        Directory.CreateDirectory(folderX);
        var fileX = Path.Combine(folderX, "clipX.mp4");
        File.WriteAllText(fileX, "x");

        var folderZ = Path.Combine(_existingDir, "z");
        Directory.CreateDirectory(folderZ);
        var fileZ = Path.Combine(folderZ, "clipZ.mp4");
        File.WriteAllText(fileZ, "z");

        var editedDir = Path.Combine(_existingDir, "y-manual");
        Directory.CreateDirectory(editedDir);

        var vm = new SplitViewModel(new FakeProbe(), new NoOpSplitEngine(), player: null, new FakeSettings());

        // Load file X → OutputDir == X's folder.
        await vm.LoadAsync(fileX);
        vm.OutputDir.Should().Be(folderX, "load re-anchors OutputDir to the loaded file's folder");

        // Edit it → stays editable.
        vm.OutputDir = editedDir;
        vm.OutputDir.Should().Be(editedDir, "OutputDir stays a normal editable property");

        // Load a NEW file Z → OutputDir re-anchors to Z's folder; the manual Y value is discarded.
        await vm.LoadAsync(fileZ);
        vm.OutputDir.Should().Be(folderZ, "T-061: every new load resets OutputDir to the new file's folder");
    }

    [Fact]
    public async Task Split_RunSplit_RemembersOutputFolder()
    {
        var settings = new FakeSettings();
        var vm = new SplitViewModel(new FakeProbe(), new NoOpSplitEngine(), player: null, settings);

        await vm.LoadAsync(_inputFile);
        vm.AddMarker(TimeSpan.FromSeconds(2));   // one marker → CanRunSplit
        vm.OutputDir = _existingDir;

        await vm.RunSplitAsync();

        vm.LastResult.Should().NotBeNull("the fake engine returns a success result");
        settings.LastOutputDir.Should().Be(_existingDir, "a successful split remembers the output folder");
    }

    // ---- Join -------------------------------------------------------------------------------

    [Fact]
    public async Task Join_AddFiles_RemembersInputFolder()
    {
        var settings = new FakeSettings();
        var vm = new JoinViewModel(new NoOpJoinEngine(), new FakeProbe(), settings);

        await vm.AddFilesAsync(new[] { _inputFile });

        settings.LastInputDir.Should().Be(_existingDir, "a successful add remembers the added file's folder");
    }

    [Fact]
    public async Task Join_RunJoin_RemembersOutputFolder()
    {
        var settings = new FakeSettings();
        var vm = new JoinViewModel(new NoOpJoinEngine(), new FakeProbe(), settings);

        var a = Path.Combine(_existingDir, "a.mp4");
        var b = Path.Combine(_existingDir, "b.mp4");
        File.WriteAllText(a, "x");
        File.WriteAllText(b, "y");

        await vm.AddFilesAsync(new[] { a, b });
        vm.IsCompatible.Should().BeTrue("the fake engine reports compatible");

        vm.OutputPath = Path.Combine(_existingDir, "joined.mp4");
        await vm.RunJoinAsync();

        vm.LastResult.Should().NotBeNull();
        settings.LastOutputDir.Should().Be(_existingDir, "a successful join remembers the output file's folder");
    }
}
