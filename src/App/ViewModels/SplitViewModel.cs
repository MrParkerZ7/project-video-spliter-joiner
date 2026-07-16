using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.Core.Detect;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// View model for the Split screen (T-007). Loads a media file (probe + keyframes), lets the user
/// place cut markers — each showing its keyframe snap — or auto-detect candidate boundaries, then
/// runs a lossless split through <see cref="ISplitEngine"/>. All engine work is funnelled through a
/// composed <see cref="OperationViewModel"/> so progress / cancel / friendly-error handling is
/// shared. Deliberately WPF-free and constructor-injected so it is fully unit-testable with fakes.
/// </summary>
public sealed class SplitViewModel : ObservableObject
{
    // Warn the user when snapping will be coarse (mean GOP longer than this).
    private static readonly TimeSpan CoarseGopThreshold = TimeSpan.FromSeconds(4);

    private readonly IMediaProbe _probe;
    private readonly ISplitEngine _splitEngine;
    private readonly ISplitPointDetector _detector;

    private string? _inputPath;
    private MediaInfo? _info;
    private IReadOnlyList<TimeSpan> _keyframes = Array.Empty<TimeSpan>();
    private string? _keyframeWarning;
    private string? _statusText;
    private TimeSpan _newMarkerPosition;
    private string _outputDir = string.Empty;
    private string _namingPattern = SplitRequest.DefaultNamingPattern;
    private bool _overwrite;
    private SplitResult? _lastResult;

    /// <summary>
    /// Create the split VM over the three Core services (real or fake). <paramref name="player"/> is
    /// the in-app preview player (T-012): the composition root passes a <see cref="MediaElementPlayer"/>,
    /// tests pass a fake; when omitted it defaults to a no-op <see cref="NullMediaPlayer"/> so existing
    /// constructions keep working. On a successful <see cref="LoadAsync"/> the loaded file is also
    /// opened in the preview.
    /// </summary>
    public SplitViewModel(
        IMediaProbe probe,
        ISplitEngine splitEngine,
        ISplitPointDetector detector,
        IMediaPlayer? player = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _splitEngine = splitEngine ?? throw new ArgumentNullException(nameof(splitEngine));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));

        Player = new PlayerViewModel(player ?? NullMediaPlayer.Instance);

        Operation = new OperationViewModel();

        Markers = new ObservableCollection<CutMarkerViewModel>();
        Candidates = new ObservableCollection<CandidateViewModel>();

        // CanRunSplit depends on the marker count → recompute when the collection changes.
        Markers.CollectionChanged += OnMarkersChanged;

        // "Set cut at playhead" enables once the player reports IsReady (DurationAvailable) →
        // recompute the command guard whenever the preview player's readiness changes.
        Player.PropertyChanged += OnPlayerChanged;

        LoadCommand = new RelayCommand(p => _ = LoadAsync(p as string));
        AddMarkerCommand = new RelayCommand(_ => AddMarker(NewMarkerPosition), _ => CanAddMarker);
        AddCutAtCommand = new RelayCommand(p => { if (p is TimeSpan t) AddCutAt(t); }, _ => CanAddMarker);
        SetCutAtPlayheadCommand = new RelayCommand(_ => SetCutAtPlayhead(), _ => CanSetCutAtPlayhead);
        SeekToMarkerCommand = new RelayCommand(SeekToMarker);
        PreviewCandidateCommand = new RelayCommand(PreviewCandidate);
        RemoveMarkerCommand = new RelayCommand(RemoveMarker);
        AutoDetectCommand = new RelayCommand(_ => _ = AutoDetectAsync(), _ => HasFile);
        AddSelectedCandidatesCommand = new RelayCommand(_ => AddSelectedCandidates(), _ => Candidates.Count > 0);
        RunSplitCommand = new RelayCommand(_ => _ = RunSplitAsync(), _ => CanRunSplit);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => !string.IsNullOrWhiteSpace(OutputDir));
        CancelCommand = Operation.CancelCommand;
    }

    // ---- State ------------------------------------------------------------------------------

    /// <summary>Path of the loaded media file, or null if none loaded.</summary>
    public string? InputPath
    {
        get => _inputPath;
        private set
        {
            if (SetProperty(ref _inputPath, value))
            {
                OnPropertyChanged(nameof(HasFile));
                OnPropertyChanged(nameof(CanRunSplit));
                OnPropertyChanged(nameof(CanSetCutAtPlayhead));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>Probed media info (duration, streams) for the loaded file, or null.</summary>
    public MediaInfo? Info
    {
        get => _info;
        private set => SetProperty(ref _info, value);
    }

    /// <summary>Video keyframe timestamps of the loaded file (drives snapping).</summary>
    public IReadOnlyList<TimeSpan> Keyframes
    {
        get => _keyframes;
        private set => SetProperty(ref _keyframes, value);
    }

    /// <summary>Set when the mean GOP is coarse — warns the user cuts may move noticeably.</summary>
    public string? KeyframeWarning
    {
        get => _keyframeWarning;
        private set => SetProperty(ref _keyframeWarning, value);
    }

    /// <summary>Free-form status line (load failures, "no candidates", etc.) — distinct from Operation.Error.</summary>
    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>The position (from a numeric input) a new marker is added at.</summary>
    public TimeSpan NewMarkerPosition
    {
        get => _newMarkerPosition;
        set => SetProperty(ref _newMarkerPosition, value);
    }

    /// <summary>Directory the segments are written into.</summary>
    public string OutputDir
    {
        get => _outputDir;
        set
        {
            if (SetProperty(ref _outputDir, value))
            {
                OnPropertyChanged(nameof(CanRunSplit));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>Segment filename template (default <c>{name}_part{index:00}{ext}</c>).</summary>
    public string NamingPattern
    {
        get => _namingPattern;
        set => SetProperty(ref _namingPattern, value);
    }

    /// <summary>When true, existing output files are replaced instead of blocking the split.</summary>
    public bool Overwrite
    {
        get => _overwrite;
        set => SetProperty(ref _overwrite, value);
    }

    /// <summary>The most recent successful split result (segments + snap deltas), for the results panel.</summary>
    public SplitResult? LastResult
    {
        get => _lastResult;
        private set => SetProperty(ref _lastResult, value);
    }

    /// <summary>The shared progress / cancel / error operation state for the run.</summary>
    public OperationViewModel Operation { get; }

    /// <summary>The in-app video preview player (T-012). Fed the loaded file on a successful load.</summary>
    public PlayerViewModel Player { get; }

    /// <summary>The cut markers, in add order (sorted at run time).</summary>
    public ObservableCollection<CutMarkerViewModel> Markers { get; }

    /// <summary>The auto-detected candidates, ranked.</summary>
    public ObservableCollection<CandidateViewModel> Candidates { get; }

    /// <summary>True once a file is loaded (gates marker/detect actions).</summary>
    public bool HasFile => InputPath is not null;

    private bool CanAddMarker => HasFile;

    /// <summary>
    /// "Set cut at playhead" is enabled only when a file is loaded AND the preview player is ready
    /// (its duration is known) — i.e. there is a real playhead position to capture.
    /// </summary>
    public bool CanSetCutAtPlayhead => HasFile && Player.IsReady;

    /// <summary>Run is enabled only with a file, at least one marker, and an output dir set.</summary>
    public bool CanRunSplit =>
        !string.IsNullOrWhiteSpace(InputPath)
        && Markers.Count >= 1
        && !string.IsNullOrWhiteSpace(OutputDir);

    // ---- Commands ---------------------------------------------------------------------------

    /// <summary>Load a file: probe it + read keyframes. Parameter is the path (string).</summary>
    public RelayCommand LoadCommand { get; }

    /// <summary>Add a cut marker at <see cref="NewMarkerPosition"/>. Blocked with no file.</summary>
    public RelayCommand AddMarkerCommand { get; }

    /// <summary>
    /// Add a cut marker at an explicit time (parameter is a <see cref="TimeSpan"/>). The single
    /// entry point both the playhead-capture (T-013) and the timeline-click (T-014) route through,
    /// so every added cut snaps + dedupes identically. Blocked with no file.
    /// </summary>
    public RelayCommand AddCutAtCommand { get; }

    /// <summary>
    /// Drop a cut marker at the preview player's current playhead position, via
    /// <see cref="AddCutAt"/>. Enabled only when a file is loaded AND the player is ready.
    /// </summary>
    public RelayCommand SetCutAtPlayheadCommand { get; }

    /// <summary>Seek the preview player to a marker's snapped time. Parameter is the <see cref="CutMarkerViewModel"/>.</summary>
    public RelayCommand SeekToMarkerCommand { get; }

    /// <summary>Preview a candidate: seek the player to its raw detected time. Parameter is the <see cref="CandidateViewModel"/>.</summary>
    public RelayCommand PreviewCandidateCommand { get; }

    /// <summary>Remove a marker. Parameter is the <see cref="CutMarkerViewModel"/>.</summary>
    public RelayCommand RemoveMarkerCommand { get; }

    /// <summary>Auto-detect candidate split points and populate <see cref="Candidates"/>.</summary>
    public RelayCommand AutoDetectCommand { get; }

    /// <summary>Turn every ticked candidate into a marker.</summary>
    public RelayCommand AddSelectedCandidatesCommand { get; }

    /// <summary>Run the split through the engine (guarded by <see cref="CanRunSplit"/>).</summary>
    public RelayCommand RunSplitCommand { get; }

    /// <summary>Open the output directory in Explorer.</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Cancel the in-flight run — delegates to <see cref="OperationViewModel.CancelCommand"/>.</summary>
    public RelayCommand CancelCommand { get; }

    // ---- Load -------------------------------------------------------------------------------

    /// <summary>
    /// Probe <paramref name="path"/> and read its keyframes. On a <see cref="ProbeResult.ProbeFailed"/>
    /// (bad/non-media file), surfaces a friendly error via <see cref="Operation"/> and leaves the VM
    /// unloaded — never throws. Clears any existing markers/candidates on a successful load.
    /// </summary>
    public async Task LoadAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        StatusText = null;
        MediaInfo? loadedInfo = null;
        IReadOnlyList<TimeSpan>? loadedKeyframes = null;

        // Route the load through Operation so a bad file yields a friendly error + details expander
        // exactly like a run does. ProbeFailed is a typed failure (not an exception) → surface it
        // via the failureSelector; a thrown probe/keyframe error is mapped by RunWithResultAsync.
        await Operation.RunWithResultAsync(
            work: async (_, ct) =>
            {
                var probeResult = await _probe.ProbeAsync(path, ct).ConfigureAwait(true);
                if (probeResult is ProbeResult.ProbeSucceeded ok)
                {
                    loadedInfo = ok.Info;
                    loadedKeyframes = await _probe.GetKeyframesAsync(path, ct).ConfigureAwait(true);
                }

                return probeResult;
            },
            failureSelector: r => r is ProbeResult.ProbeFailed f
                ? new UserFacingError(
                    ErrorCategory.CorruptInput,
                    $"Could not read '{Path.GetFileName(path)}'.",
                    f.Reason,
                    "Choose a different file, or check that it is a valid media file.")
                : null,
            runningStatus: "Loading…").ConfigureAwait(true);

        if (Operation.State != OperationState.Completed || loadedInfo is null || loadedKeyframes is null)
        {
            // Failure/cancel already reflected in Operation.Error; also mirror a short status line.
            if (Operation.State == OperationState.Failed)
            {
                StatusText = Operation.Error?.Message ?? $"Could not read '{Path.GetFileName(path)}'.";
            }

            return;
        }

        // Success — commit the loaded state and clear prior markers/candidates.
        Info = loadedInfo;
        Keyframes = loadedKeyframes;
        InputPath = path;
        // Feed the freshly-loaded file to the in-app preview player (T-012). No-op under the
        // NullMediaPlayer default; the fake records the Open in tests.
        Player.Open(path);
        Markers.Clear();
        Candidates.Clear();
        LastResult = null;
        UpdateKeyframeWarning();

        // Default the output dir to the input file's folder when none is set yet.
        if (string.IsNullOrWhiteSpace(OutputDir))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                OutputDir = dir;
            }
        }

        StatusText = $"Loaded {Path.GetFileName(path)} — {FormatDuration(loadedInfo.Duration)}.";
    }

    private void UpdateKeyframeWarning()
    {
        var gop = _probe.AverageGop(Keyframes);
        if (gop > CoarseGopThreshold)
        {
            var seconds = gop.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            KeyframeWarning =
                $"Keyframes are ~{seconds}s apart — cuts may move up to ~{seconds}s to the nearest keyframe.";
        }
        else
        {
            KeyframeWarning = null;
        }
    }

    // ---- Markers ----------------------------------------------------------------------------

    /// <summary>Add a cut marker at <paramref name="position"/> (snap computed). No-op without a file.</summary>
    public void AddMarker(TimeSpan position) => AddCutAt(position);

    /// <summary>
    /// THE single entry point for adding a cut marker at an explicit time — used by manual add,
    /// playhead-capture (T-013), and the timeline click (T-014). Builds a snapping
    /// <see cref="CutMarkerViewModel"/> and dedupes on the snapped keyframe: if a marker already
    /// lands on the same keyframe, the add is skipped (two playhead captures on the same GOP → one
    /// cut). No-op without a file.
    /// </summary>
    public void AddCutAt(TimeSpan position)
    {
        if (!HasFile)
        {
            return;
        }

        var marker = new CutMarkerViewModel(_probe, () => Keyframes, position);

        // Dedupe on the snapped time — the cut actually lands on the keyframe, so two requests that
        // snap to the same keyframe are one cut. Guards double-capture of the same playhead.
        if (Markers.Any(m => m.Snapped == marker.Snapped))
        {
            return;
        }

        Markers.Add(marker);
    }

    /// <summary>Capture a cut at the preview player's current playhead position (T-013).</summary>
    public void SetCutAtPlayhead()
    {
        if (!CanSetCutAtPlayhead)
        {
            return;
        }

        AddCutAt(Player.Position);
    }

    /// <summary>Seek the preview player to <paramref name="marker"/>'s SNAPPED time so the user sees where the cut lands.</summary>
    public void SeekToMarker(object? parameter)
    {
        if (parameter is CutMarkerViewModel marker)
        {
            Player.Scrub(marker.Snapped);
        }
    }

    /// <summary>Seek the preview player to <paramref name="candidate"/>'s RAW detected time for a preview before selecting.</summary>
    public void PreviewCandidate(object? parameter)
    {
        if (parameter is CandidateViewModel candidate)
        {
            Player.Scrub(candidate.Candidate.Time);
        }
    }

    private void RemoveMarker(object? parameter)
    {
        if (parameter is CutMarkerViewModel marker)
        {
            Markers.Remove(marker);
        }
    }

    // ---- Auto-detect ------------------------------------------------------------------------

    /// <summary>
    /// Run the detector over the loaded file and populate <see cref="Candidates"/> (ranked). An
    /// empty result is a friendly "no candidates found" status — NOT an error. No file → no-op.
    /// </summary>
    public async Task AutoDetectAsync()
    {
        if (InputPath is null)
        {
            return;
        }

        var path = InputPath;
        StatusText = null;
        IReadOnlyList<Candidate>? detected = null;

        await Operation.RunAsync(
            async (progress, ct) =>
            {
                detected = await _detector.DetectAsync(path, new DetectOptions(), progress, ct).ConfigureAwait(true);
            },
            "Detecting split points…").ConfigureAwait(true);

        if (Operation.State != OperationState.Completed || detected is null)
        {
            // Failure/cancel already reflected in Operation; leave candidates as-is.
            return;
        }

        Candidates.Clear();
        foreach (var candidate in detected.OrderBy(c => c.Rank))
        {
            Candidates.Add(new CandidateViewModel(candidate));
        }

        RaiseCommandStates();
        StatusText = Candidates.Count == 0
            ? "No candidates found — try adding cut markers manually."
            : $"Found {Candidates.Count} candidate split point(s).";
    }

    /// <summary>For each ticked candidate, add a marker at its detected time (snaps to its keyframe).</summary>
    public void AddSelectedCandidates()
    {
        if (!HasFile)
        {
            return;
        }

        foreach (var candidate in Candidates.Where(c => c.IsSelected).ToList())
        {
            // Add at the detected Time; the marker re-snaps against Keyframes (lands on SnappedTime).
            Markers.Add(new CutMarkerViewModel(_probe, () => Keyframes, candidate.Candidate.Time));
            candidate.IsSelected = false;
        }
    }

    // ---- Run split --------------------------------------------------------------------------

    /// <summary>
    /// Build a <see cref="SplitRequest"/> from the current markers (cut points sorted ascending) and
    /// run it through the engine via <see cref="OperationViewModel.RunWithResultAsync{T}"/>. On
    /// success, <see cref="LastResult"/> is set; a failure result surfaces via <see cref="Operation"/>.
    /// </summary>
    public async Task RunSplitAsync()
    {
        if (!CanRunSplit || InputPath is null)
        {
            return;
        }

        var cutPoints = Markers
            .Select(m => m.Requested)
            .OrderBy(t => t)
            .ToList()
            .AsReadOnly();

        var request = new SplitRequest(
            InputPath: InputPath,
            CutPoints: cutPoints,
            OutputDir: OutputDir,
            NamingPattern: string.IsNullOrWhiteSpace(NamingPattern) ? SplitRequest.DefaultNamingPattern : NamingPattern,
            Overwrite: Overwrite);

        LastResult = null;
        SplitResult? result = null;

        await Operation.RunWithResultAsync(
            work: async (progress, ct) =>
            {
                result = await _splitEngine.SplitAsync(request, progress, ct).ConfigureAwait(true);
                return result;
            },
            // The engine reports genuine failures as SplitException (mapped by OperationViewModel);
            // a returned SplitResult is a success, so there is no per-result failure to select.
            failureSelector: _ => null,
            runningStatus: "Splitting…").ConfigureAwait(true);

        if (Operation.State == OperationState.Completed && result is not null)
        {
            LastResult = result;
            StatusText = result.Warnings.Count > 0
                ? $"Split complete with {result.Warnings.Count} warning(s)."
                : "Split complete.";
        }
    }

    // ---- Open folder ------------------------------------------------------------------------

    private void OpenFolder()
    {
        if (string.IsNullOrWhiteSpace(OutputDir) || !Directory.Exists(OutputDir))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputDir}\"") { UseShellExecute = true });
        }
        catch
        {
            // Best-effort; opening Explorer is non-critical and never runs under tests.
        }
    }

    // ---- Plumbing ---------------------------------------------------------------------------

    private void OnMarkersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanRunSplit));
        RaiseCommandStates();
    }

    private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The player's readiness (or a load) gates SetCutAtPlayhead; refresh its guard.
        if (e.PropertyName is nameof(PlayerViewModel.IsReady) or nameof(PlayerViewModel.Duration))
        {
            OnPropertyChanged(nameof(CanSetCutAtPlayhead));
            SetCutAtPlayheadCommand.RaiseCanExecuteChanged();
        }
    }

    private void RaiseCommandStates()
    {
        RunSplitCommand.RaiseCanExecuteChanged();
        AddMarkerCommand.RaiseCanExecuteChanged();
        AddCutAtCommand.RaiseCanExecuteChanged();
        SetCutAtPlayheadCommand.RaiseCanExecuteChanged();
        AutoDetectCommand.RaiseCanExecuteChanged();
        AddSelectedCandidatesCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1
            ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : d.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
