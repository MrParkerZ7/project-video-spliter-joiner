using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// View model for the Split screen (T-007). Loads a media file (probe + keyframes), lets the user
/// place cut markers — each showing its keyframe snap — then runs a lossless split through
/// <see cref="ISplitEngine"/>. All engine work is funnelled through a
/// composed <see cref="OperationViewModel"/> so progress / cancel / friendly-error handling is
/// shared. Deliberately WPF-free and constructor-injected so it is fully unit-testable with fakes.
/// </summary>
public sealed class SplitViewModel : ObservableObject
{
    // Warn the user when snapping will be coarse (mean GOP longer than this).
    private static readonly TimeSpan CoarseGopThreshold = TimeSpan.FromSeconds(4);

    private readonly IMediaProbe _probe;
    private readonly ISplitEngine _splitEngine;
    private readonly IAppSettings _settings;

    private string? _inputPath;
    private MediaInfo? _info;
    private IReadOnlyList<TimeSpan> _keyframes = Array.Empty<TimeSpan>();
    private string? _keyframeWarning;
    private bool _isIndexingKeyframes;
    private string? _statusText;

    // The in-flight background keyframe index for the current file (T-030). Swapped on each load;
    // AddCutAt awaits it when a cut is placed before the scan finishes so the cut still snaps.
    private Task<IReadOnlyList<TimeSpan>>? _keyframeIndexTask;

    // Cancels the previous file's background index when a new file is loaded, so a stale scan
    // result can never overwrite the newer file's keyframes.
    private CancellationTokenSource? _keyframeIndexCts;
    private TimeSpan _newMarkerPosition;
    private string _outputDir = string.Empty;
    private string _namingPattern = SplitRequest.DefaultNamingPattern;
    private bool _overwrite;
    private SplitResult? _lastResult;

    /// <summary>
    /// Create the split VM over the two Core services (real or fake). <paramref name="player"/> is
    /// the in-app preview player (T-012): the composition root passes a <see cref="FfmeMediaPlayer"/>,
    /// tests pass a fake; when omitted it defaults to a no-op <see cref="NullMediaPlayer"/> so existing
    /// constructions keep working. On a successful <see cref="LoadAsync"/> the loaded file is also
    /// opened in the preview. <paramref name="settings"/> is the cross-session folder memory (T-038):
    /// the composition root shares the real <see cref="AppSettings"/>, tests pass a fake; when omitted
    /// a real file-backed store is used so existing constructions keep working.
    /// </summary>
    public SplitViewModel(
        IMediaProbe probe,
        ISplitEngine splitEngine,
        IMediaPlayer? player = null,
        IAppSettings? settings = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _splitEngine = splitEngine ?? throw new ArgumentNullException(nameof(splitEngine));
        _settings = settings ?? new AppSettings();

        // Seed the output dir from the remembered last-output folder (T-038) — but only if it still
        // exists on disk. A stale/missing remembered folder falls back to the per-load default
        // (the input file's folder) applied in LoadAsync.
        if (DirectoryExists(_settings.LastOutputDir))
        {
            _outputDir = _settings.LastOutputDir!;
        }

        Player = new PlayerViewModel(player ?? NullMediaPlayer.Instance);

        Operation = new OperationViewModel();

        Markers = new ObservableCollection<CutMarkerViewModel>();

        // The timeline overlay (T-014) projects Player/Markers onto a normalized strip.
        // Constructed last so it can subscribe to the fully-built collections + player.
        Timeline = new TimelineViewModel(this);

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
        RemoveMarkerCommand = new RelayCommand(RemoveMarker);
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
                OnPropertyChanged(nameof(KeyframesReady));
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

    /// <summary>
    /// True while the background keyframe scan for the loaded file is still running (T-030). The
    /// preview + <see cref="Info"/> appear as soon as the probe succeeds; keyframes are indexed in
    /// the background, and this flag lets the view show a non-blocking "indexing…" hint. Flipped
    /// false when the scan completes, fails, or is cancelled by a newer load.
    /// </summary>
    public bool IsIndexingKeyframes
    {
        get => _isIndexingKeyframes;
        private set
        {
            if (SetProperty(ref _isIndexingKeyframes, value))
            {
                OnPropertyChanged(nameof(KeyframesReady));
            }
        }
    }

    /// <summary>True once a file is loaded AND its background keyframe scan has finished.</summary>
    public bool KeyframesReady => HasFile && !IsIndexingKeyframes;

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

    /// <summary>
    /// The cross-session folder memory (T-038). Exposed so the view's file-picker code-behind can seed
    /// <c>OpenFileDialog.InitialDirectory</c> from <see cref="IAppSettings.LastInputDir"/> and share the
    /// same instance the VM writes to.
    /// </summary>
    public IAppSettings Settings => _settings;

    /// <summary>The in-app video preview player (T-012). Fed the loaded file on a successful load.</summary>
    public PlayerViewModel Player { get; }

    /// <summary>The cut markers, in add order (sorted at run time).</summary>
    public ObservableCollection<CutMarkerViewModel> Markers { get; }

    /// <summary>
    /// The timeline overlay projection (T-014): markers + playhead on a normalized strip under the
    /// player, with click-to-cut / click-to-seek routed back through this VM's existing
    /// <see cref="AddCutAt"/> / seek commands.
    /// </summary>
    public TimelineViewModel Timeline { get; }

    /// <summary>True once a file is loaded (gates marker actions).</summary>
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

    /// <summary>Remove a marker. Parameter is the <see cref="CutMarkerViewModel"/>.</summary>
    public RelayCommand RemoveMarkerCommand { get; }

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
    /// unloaded — never throws. Clears any existing markers on a successful load.
    /// </summary>
    public async Task LoadAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        StatusText = null;
        MediaInfo? loadedInfo = null;

        // Route the load through Operation so a bad file yields a friendly error + details expander
        // exactly like a run does. T-030: the PROBE is now the only gating async step — the
        // keyframe scan no longer blocks the load; it runs in the background after this returns.
        // ProbeFailed is a typed failure (not an exception) → surface it via the failureSelector;
        // a thrown probe error is mapped by RunWithResultAsync.
        await Operation.RunWithResultAsync(
            work: async (_, ct) =>
            {
                var probeResult = await _probe.ProbeAsync(path, ct).ConfigureAwait(true);
                if (probeResult is ProbeResult.ProbeSucceeded ok)
                {
                    loadedInfo = ok.Info;
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

        if (Operation.State != OperationState.Completed || loadedInfo is null)
        {
            // Failure/cancel already reflected in Operation.Error; also mirror a short status line.
            if (Operation.State == OperationState.Failed)
            {
                StatusText = Operation.Error?.Message ?? $"Could not read '{Path.GetFileName(path)}'.";
            }

            return;
        }

        // Success — the probe returned, so the preview + info can appear AT ONCE, before any
        // keyframe scan. Commit the loaded state and clear prior markers immediately.
        Info = loadedInfo;
        Keyframes = Array.Empty<TimeSpan>();
        InputPath = path;
        // Feed the freshly-loaded file to the in-app preview player (T-012). No-op under the
        // NullMediaPlayer default; the fake records the Open in tests.
        Player.Open(path);
        Markers.Clear();
        LastResult = null;
        // No keyframes yet → clear any stale warning until the background scan reports.
        KeyframeWarning = null;

        // Remember the folder the input file was chosen from (T-038) so next session's picker opens
        // there. Best-effort — a persistence failure is swallowed inside the settings store.
        var inputDir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(inputDir))
        {
            _settings.LastInputDir = inputDir;
        }

        // Default the output dir when none is set yet: prefer the remembered last-output folder
        // (T-038, when it still exists on disk), else fall back to the input file's folder.
        if (string.IsNullOrWhiteSpace(OutputDir))
        {
            if (DirectoryExists(_settings.LastOutputDir))
            {
                OutputDir = _settings.LastOutputDir!;
            }
            else if (!string.IsNullOrEmpty(inputDir))
            {
                OutputDir = inputDir;
            }
        }

        StatusText = $"Loaded {Path.GetFileName(path)} — {FormatDuration(loadedInfo.Duration)}.";

        // Now index keyframes IN THE BACKGROUND (T-030). Cancel any previous file's index so a
        // stale result can't overwrite this file's keyframes, then start a fresh one.
        StartKeyframeIndex(path);
    }

    /// <summary>
    /// Cancel any in-flight background keyframe index, then start a new one for <paramref name="path"/>
    /// (T-030). The load has already committed the preview + info; this runs the keyframe scan
    /// without blocking. On completion (back on the captured sync context) it commits
    /// <see cref="Keyframes"/> + the coarse-GOP warning and clears the indexing flag — but ONLY if
    /// this scan is still the current one (a newer load cancels it). Failure/cancel just clears the
    /// flag and leaves <see cref="Keyframes"/> empty (snap then awaits/falls back — see
    /// <see cref="EnsureKeyframesAsync"/>).
    /// </summary>
    private void StartKeyframeIndex(string path)
    {
        // Cancel + dispose the previous file's index (stale-result guard).
        _keyframeIndexCts?.Cancel();
        _keyframeIndexCts?.Dispose();

        var cts = new CancellationTokenSource();
        _keyframeIndexCts = cts;

        IsIndexingKeyframes = true;

        // The task AddCutAt awaits when a cut is placed before the scan finishes. Kept as a field
        // so the same in-flight scan is reused (never a second ffprobe pass).
        var indexTask = _probe.GetKeyframesAsync(path, cts.Token);
        _keyframeIndexTask = indexTask;

        // Fast path: a probe whose keyframe scan already completed synchronously (cached result, or
        // a fake that returns Task.FromResult) commits inline — no posted continuation, no thread
        // hop — so callers that load-then-read keyframes on the same call see them immediately.
        if (indexTask.IsCompleted)
        {
            if (ReferenceEquals(_keyframeIndexCts, cts))
            {
                if (indexTask.Status == TaskStatus.RanToCompletion)
                {
                    Keyframes = indexTask.Result;
                    UpdateKeyframeWarning();
                }

                IsIndexingKeyframes = false;
            }

            return;
        }

        // Observe completion back on the captured context (the WPF dispatcher in the app). When
        // there is no synchronization context (e.g. the xUnit default), fall back to the default
        // scheduler — the continuation body only touches VM state, which those tests read after
        // awaiting anyway.
        var completionScheduler = SynchronizationContext.Current is not null
            ? TaskScheduler.FromCurrentSynchronizationContext()
            : TaskScheduler.Default;

        _ = indexTask.ContinueWith(
            t =>
            {
                // A newer load already superseded this scan → drop the result silently.
                if (!ReferenceEquals(_keyframeIndexCts, cts))
                {
                    return;
                }

                if (t.Status == TaskStatus.RanToCompletion)
                {
                    Keyframes = t.Result;
                    UpdateKeyframeWarning();
                }
                // Faulted/cancelled → leave Keyframes empty; snap falls back (EnsureKeyframesAsync).

                IsIndexingKeyframes = false;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            completionScheduler);
    }

    /// <summary>
    /// Return the loaded file's keyframes, awaiting the in-flight background index if it is still
    /// running (T-030 snap-before-ready). Used by <see cref="AddCutAt"/> so a cut placed while
    /// <see cref="IsIndexingKeyframes"/> is true still snaps correctly against the SAME scan already
    /// running — never against an empty list. If the index failed / was cancelled (or never ran),
    /// returns whatever <see cref="Keyframes"/> holds (possibly empty), and the caller falls back to
    /// an identity snap rather than crashing.
    /// </summary>
    private async Task<IReadOnlyList<TimeSpan>> EnsureKeyframesAsync()
    {
        if (Keyframes.Count > 0)
        {
            return Keyframes;
        }

        var task = _keyframeIndexTask;
        if (task is not null)
        {
            try
            {
                return await task.ConfigureAwait(true);
            }
            catch
            {
                // Index failed / cancelled → fall through to whatever Keyframes holds (empty).
            }
        }

        return Keyframes;
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

        // Snap-before-ready (T-030, D3): if a cut is placed while the background keyframe scan is
        // still running, Keyframes is empty and a synchronous snap would snap to nothing. Await the
        // SAME in-flight scan for just this action so the cut snaps correctly, then add it back on
        // the captured context. When keyframes are already present, add synchronously as before so
        // the existing contract / tests are unchanged.
        if (IsIndexingKeyframes && Keyframes.Count == 0)
        {
            _ = AddCutAtWhenIndexedAsync(position);
            return;
        }

        AddSnappedMarker(position);
    }

    /// <summary>
    /// Await the in-flight keyframe index (T-030), then add the marker so it snaps against the
    /// arrived keyframes. If the index failed / was cancelled, the marker still adds — it snaps
    /// against whatever <see cref="Keyframes"/> holds (an identity snap on empty, delta 0), never
    /// crashing on an empty list (<see cref="CutMarkerViewModel"/> already guards that).
    /// </summary>
    private async Task AddCutAtWhenIndexedAsync(TimeSpan position)
    {
        await EnsureKeyframesAsync().ConfigureAwait(true);

        // The file may have changed while awaiting (a newer load) — only add if still loaded.
        if (!HasFile)
        {
            return;
        }

        AddSnappedMarker(position);
    }

    /// <summary>Build a snapping marker at <paramref name="position"/> and add it (dedup on snapped time).</summary>
    private void AddSnappedMarker(TimeSpan position)
    {
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

    private void RemoveMarker(object? parameter)
    {
        if (parameter is CutMarkerViewModel marker)
        {
            Markers.Remove(marker);
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

            // Remember the output folder we just wrote to (T-038) so it becomes next load's default.
            if (!string.IsNullOrWhiteSpace(OutputDir))
            {
                _settings.LastOutputDir = OutputDir;
            }
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
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    /// <summary>True when <paramref name="dir"/> is a non-blank path that exists on disk (guards a stale
    /// remembered folder so we never set a bad default / picker directory). Never throws.</summary>
    private static bool DirectoryExists(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }

        try
        {
            return Directory.Exists(dir);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1
            ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : d.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
