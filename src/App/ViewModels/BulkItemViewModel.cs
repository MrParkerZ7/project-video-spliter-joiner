using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// The lifecycle state of one Bulk Cut row (D-004 / T-096). The pre-batch states
/// (<see cref="Loading"/>/<see cref="Ready"/>/<see cref="Invalid"/>/<see cref="NoOpTrim"/>/<see cref="LoadFailed"/>)
/// are computed from the probe + keyframe scan + the two handles; the batch states
/// (<see cref="Queued"/>→<see cref="Running"/>→<see cref="Done"/>/<see cref="Failed"/>/<see cref="Skipped"/>/<see cref="Cancelled"/>)
/// are driven from the T-095 progress + ledger fan-out.
/// </summary>
public enum RowState
{
    /// <summary>Probe done but the background keyframe scan has not finished yet.</summary>
    Loading,

    /// <summary>Keyframes ready, the cut is valid and non-trivial — eligible for the batch.</summary>
    Ready,

    /// <summary>Keyframes ready but the cut is degenerate (intro ≥ (outro ?? EOF) − MinKeptSpan / out of range).</summary>
    Invalid,

    /// <summary>The net result keeps the whole file (intro ≈ 0 and no outro / outro ≈ EOF) — auto-excluded.</summary>
    NoOpTrim,

    /// <summary>The source could not be probed — excluded from the batch.</summary>
    LoadFailed,

    /// <summary>Selected into a batch that is preparing to run.</summary>
    Queued,

    /// <summary>This row's trim is in flight.</summary>
    Running,

    /// <summary>Trimmed successfully.</summary>
    Done,

    /// <summary>The trim raised an error (the batch continued).</summary>
    Failed,

    /// <summary>Skipped without running (collision Skip, or a no-op trim).</summary>
    Skipped,

    /// <summary>This row was in flight when the batch was cancelled.</summary>
    Cancelled,
}

/// <summary>
/// One Bulk Cut row (D-004 / T-096): a single source video trimmed by KEEPING exactly the middle
/// segment <c>[intro-end → outro-start | EOF]</c>. Holds the Join-item shape (path / duration / size),
/// two <see cref="CutMarkerViewModel"/> handles (intro-end required, outro-start optional/from-end),
/// a per-file background keyframe scan (throttled through the tab VM's shared gate), the computed
/// validity/row-state, its own per-row <see cref="OperationViewModel"/>, and the two T-094 request
/// builders (<see cref="BuildRequest"/> / <see cref="BuildBulkTrimItem"/>).
///
/// <para>Deliberately WPF-free — only <see cref="ObservableObject"/> + Core/BCL types (the discipline
/// is kept by hand; there is no App-layer UI-free guard test).</para>
/// </summary>
public sealed class BulkItemViewModel : ObservableObject
{
    // Snap this close to 0 / EOF counts as "at the boundary" for the no-op / tail-warning checks.
    private static readonly TimeSpan BoundaryEpsilon = TimeSpan.FromMilliseconds(500);

    // A mean GOP longer than this is "coarse" — cuts can move noticeably (mirrors the Split screen).
    private static readonly TimeSpan CoarseGopThreshold = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private readonly IMediaProbe _probe;
    private readonly SemaphoreSlim _scanGate;

    private IReadOnlyList<TimeSpan> _keyframes = Array.Empty<TimeSpan>();
    private bool _isIndexingKeyframes = true;
    private bool _loadFailed;
    private TimeSpan? _duration;
    private long _sizeBefore;
    private long? _sizeAfter;
    private CutMarkerViewModel? _outroStart;
    private bool _isEnabledByUser = true;

    // Batch phase, when active, overrides the computed base row-state. null ⇒ show the computed state.
    private RowState? _batchState;
    private double _progress;
    private UserFacingError? _error;
    private string? _ledgerOutputPath;
    private IReadOnlyList<string> _ledgerWarnings = Array.Empty<string>();

    private CancellationTokenSource? _scanCts;
    private Task? _currentScanTask;

    /// <summary>
    /// Create a row over a source path, sharing the tab VM's media probe + bounded scan gate.
    /// <paramref name="defaultIntro"/> (unwired in v1 — post-v1 <c>DefaultIntroSeconds</c> pre-seed)
    /// seeds the intro handle; both handles are created optimistically (<c>snapPending: true</c>) and
    /// resolved once the background scan lands.
    /// </summary>
    public BulkItemViewModel(string path, IMediaProbe probe, SemaphoreSlim scanGate, TimeSpan? defaultIntro = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _scanGate = scanGate ?? throw new ArgumentNullException(nameof(scanGate));

        FileName = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(FileName))
        {
            FileName = path;
        }

        Operation = new OperationViewModel();

        IntroEnd = new CutMarkerViewModel(_probe, () => Keyframes, defaultIntro ?? TimeSpan.Zero, snapPending: true);
        IntroEnd.PropertyChanged += OnHandleChanged;
    }

    // ---- Identity / shape -------------------------------------------------------------------

    /// <summary>Full path of the source file on disk (the dedup key is its normalized <see cref="System.IO.Path.GetFullPath"/>).</summary>
    public string Path { get; }

    /// <summary>Display filename shown in the row.</summary>
    public string FileName { get; }

    /// <summary>Probed duration (upper bound for both handles / outro-EOF fallback); null until probed.</summary>
    public TimeSpan? Duration
    {
        get => _duration;
        set
        {
            if (SetProperty(ref _duration, value))
            {
                RecomputeAll();
            }
        }
    }

    /// <summary>On-disk size before the trim (feeds the shrink estimate + progress-weight fallback).</summary>
    public long SizeBefore
    {
        get => _sizeBefore;
        set => SetProperty(ref _sizeBefore, value);
    }

    /// <summary>On-disk size of the written output, filled from the ledger; null until a Done result lands.</summary>
    public long? SizeAfter
    {
        get => _sizeAfter;
        private set => SetProperty(ref _sizeAfter, value);
    }

    // ---- Handles ----------------------------------------------------------------------------

    /// <summary>The required intro-end handle (start of the kept middle).</summary>
    public CutMarkerViewModel IntroEnd { get; }

    /// <summary>The optional outro-start handle (end of the kept middle); null ⇒ keep runs to EOF.</summary>
    public CutMarkerViewModel? OutroStart
    {
        get => _outroStart;
        private set
        {
            if (ReferenceEquals(_outroStart, value))
            {
                return;
            }

            if (_outroStart is not null)
            {
                _outroStart.PropertyChanged -= OnHandleChanged;
            }

            _outroStart = value;

            if (_outroStart is not null)
            {
                _outroStart.PropertyChanged += OnHandleChanged;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasOutro));
        }
    }

    /// <summary>True when the optional outro-start handle exists.</summary>
    public bool HasOutro => OutroStart is not null;

    /// <summary>Add (or replace) the outro-start handle at <paramref name="requested"/> (time-from-start), optimistically snapped.</summary>
    public void AddOutro(TimeSpan requested)
    {
        var handle = new CutMarkerViewModel(_probe, () => Keyframes, requested, snapPending: true);
        if (KeyframesReady)
        {
            handle.ResolveSnap();
        }

        OutroStart = handle;
        RecomputeAll();
    }

    /// <summary>Drop the outro-start handle (keep now runs to EOF).</summary>
    public void ClearOutro()
    {
        OutroStart = null;
        RecomputeAll();
    }

    // ---- Keyframes --------------------------------------------------------------------------

    /// <summary>Per-file keyframe times (sorted, distinct); empty until the background scan lands.</summary>
    public IReadOnlyList<TimeSpan> Keyframes
    {
        get => _keyframes;
        private set => SetProperty(ref _keyframes, value);
    }

    /// <summary>True while the background keyframe scan is queued or running.</summary>
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

    /// <summary>True once the file is probed AND its background keyframe scan has finished.</summary>
    public bool KeyframesReady => Duration is not null && !IsIndexingKeyframes;

    // ---- Computed cut state -----------------------------------------------------------------

    private TimeSpan IntroEndSnapped => IntroEnd.Snapped;

    private TimeSpan? OutroStartSnapped => HasOutro ? OutroStart!.Snapped : (TimeSpan?)null;

    /// <summary>Kept-middle length <c>(OutroStartSnapped ?? Duration) − IntroEndSnapped</c>; null until keyframes ready.</summary>
    public TimeSpan? KeptDuration =>
        KeyframesReady && Duration is { } d ? (OutroStartSnapped ?? d) - IntroEndSnapped : null;

    /// <summary>Minimum meaningful kept span — <c>max(1s, 1 GOP)</c> (D-004 open-decision 4).</summary>
    public TimeSpan MinKeptSpan
    {
        get
        {
            var gop = _probe.AverageGop(Keyframes);
            return gop > OneSecond ? gop : OneSecond;
        }
    }

    /// <summary>True when the cut is valid + in-range and the kept span exceeds <see cref="MinKeptSpan"/> — gates the run.</summary>
    public bool IsValidCut
    {
        get
        {
            if (!KeyframesReady || Duration is not { } d)
            {
                return false;
            }

            var upper = OutroStartSnapped ?? d;
            return IntroEndSnapped >= TimeSpan.Zero
                && upper <= d
                && IntroEndSnapped < upper - MinKeptSpan;
        }
    }

    /// <summary>True when the net result keeps the whole file (intro ≈ 0 AND no outro / outro ≈ EOF) — auto-excluded.</summary>
    public bool IsNoOpTrim
    {
        get
        {
            if (!KeyframesReady || Duration is not { } d)
            {
                return false;
            }

            var introAtStart = IntroEndSnapped <= BoundaryEpsilon;
            var outroAtEof = HasOutro && d - OutroStartSnapped!.Value <= BoundaryEpsilon;
            return introAtStart && (!HasOutro || outroAtEof);
        }
    }

    /// <summary>The row's lifecycle state (batch phase when running/finished, else the computed pre-batch state).</summary>
    public RowState RowState => _batchState ?? ComputeBaseRowState();

    private RowState ComputeBaseRowState()
    {
        if (_loadFailed)
        {
            return RowState.LoadFailed;
        }

        if (!KeyframesReady)
        {
            return RowState.Loading;
        }

        if (IsNoOpTrim)
        {
            return RowState.NoOpTrim;
        }

        return IsValidCut ? RowState.Ready : RowState.Invalid;
    }

    /// <summary>Non-fatal notes: coarse-GOP, "nothing trimmed from the tail", "very short keep", folded with ledger warnings.</summary>
    public string? Warning
    {
        get
        {
            var notes = new List<string>();

            if (KeyframesReady && Duration is { } d)
            {
                var gop = _probe.AverageGop(Keyframes);
                if (gop > CoarseGopThreshold)
                {
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"coarse keyframes — cuts may move ~{gop.TotalSeconds:0.0}s"));
                }

                if (HasOutro && d - OutroStartSnapped!.Value <= BoundaryEpsilon && IntroEndSnapped > BoundaryEpsilon)
                {
                    notes.Add("nothing trimmed from the tail");
                }

                if (KeptDuration is { } kept && kept > BoundaryEpsilon && kept < MinKeptSpan)
                {
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"very short keep (~{kept.TotalSeconds:0.0}s)"));
                }
            }

            notes.AddRange(_ledgerWarnings);

            return notes.Count == 0 ? null : string.Join("; ", notes);
        }
    }

    /// <summary>Row-in-batch checkbox; auto-forced false when Invalid / NoOpTrim / LoadFailed.</summary>
    public bool IsEnabled
    {
        get => _isEnabledByUser && !IsAutoDisabled;
        set
        {
            if (_isEnabledByUser != value)
            {
                _isEnabledByUser = value;
                OnPropertyChanged();
            }
        }
    }

    // Auto-disabled once the row is known-ineligible (Loading is NOT auto-disabled — CanRunBatch waits on it).
    private bool IsAutoDisabled => _loadFailed || (KeyframesReady && (IsNoOpTrim || !IsValidCut));

    /// <summary>The user's raw checkbox intent, independent of auto-disable — used by apply-to-all targeting.</summary>
    internal bool IsCheckedByUser => _isEnabledByUser;

    /// <summary>Per-row progress fraction 0..1 (forwarded from the batch progress fan-out).</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, Math.Clamp(value, 0d, 1d));
    }

    /// <summary>The per-row friendly error for a Failed row (from the ledger); null otherwise.</summary>
    public UserFacingError? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Deterministic base output path <c>&lt;dir&gt;/&lt;name&gt;_trimmed&lt;ext&gt;</c>, or the collision-resolved written path once a Done result lands.</summary>
    public string OutputPath => _ledgerOutputPath ?? ComputeBaseOutputPath();

    /// <summary>Its own per-row progress/state/error operation (the aggregate op owns the real batch cancel).</summary>
    public OperationViewModel Operation { get; }

    // ---- Requests (T-094) -------------------------------------------------------------------

    /// <summary>
    /// Build the single-kept-segment <see cref="SplitRequest"/> for this row via T-094
    /// (<see cref="KeptSegmentSelector.ResolveKeptIndex"/> + <see cref="KeptSegmentSelector.BuildKeptMiddleRequest"/>) —
    /// the same path the engine's request builder funnels through. Powers the preview + validity cross-check.
    /// </summary>
    public SplitRequest BuildRequest(bool overwrite = false)
    {
        if (Duration is not { } d)
        {
            throw new InvalidOperationException("Cannot build a request before the file is probed.");
        }

        var outro = HasOutro ? OutroStart!.Requested : (TimeSpan?)null;
        var idx = KeptSegmentSelector.ResolveKeptIndex(
            d, Keyframes, _probe.SnapToNearestKeyframe, _probe.AverageGop(Keyframes), IntroEnd.Requested, outro);
        return KeptSegmentSelector.BuildKeptMiddleRequest(Path, IntroEnd.Requested, outro, idx, overwrite);
    }

    /// <summary>Build the T-095 batch input for this row (correlates back through <c>Tag = this</c>).</summary>
    public BulkTrimItem BuildBulkTrimItem() =>
        new(Path, IntroEnd.Requested, HasOutro ? OutroStart!.Requested : null, ComputeBaseOutputPath(), Tag: this);

    // ---- Background keyframe scan (throttled — §3) ------------------------------------------

    /// <summary>
    /// Start (or restart) this row's background keyframe scan under the shared bounded gate, modelling
    /// <c>SplitViewModel.StartKeyframeIndex</c>: swap a per-row CTS (cancelling any prior scan), flip the
    /// indexing flag, then a captured-context continuation with a stale-CTS guard commits the keyframes,
    /// resolves both handle snaps, and recomputes validity. Returns the scan task (awaitable for tests).
    /// </summary>
    public Task StartKeyframeScanAsync()
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _scanCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        IsIndexingKeyframes = true;
        OnPropertyChanged(nameof(RowState));

        var task = ScanBodyAsync(cts);
        _currentScanTask = task;
        return task;
    }

    private async Task ScanBodyAsync(CancellationTokenSource cts)
    {
        IReadOnlyList<TimeSpan> scanned;
        try
        {
            await _scanGate.WaitAsync(cts.Token).ConfigureAwait(true);
            try
            {
                scanned = await _probe.GetKeyframesAsync(Path, cts.Token).ConfigureAwait(true);
            }
            finally
            {
                _scanGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded or removed — if still current, just clear the flag (keyframes stay empty).
            if (ReferenceEquals(_scanCts, cts))
            {
                IsIndexingKeyframes = false;
                RecomputeAll();
            }

            return;
        }
        catch
        {
            // Scan failed — leave keyframes empty (handles fall back to identity snaps) and clear the flag.
            if (ReferenceEquals(_scanCts, cts))
            {
                IsIndexingKeyframes = false;
                IntroEnd.ResolveSnap();
                OutroStart?.ResolveSnap();
                RecomputeAll();
            }

            return;
        }

        // A newer scan superseded this one → drop the result silently.
        if (!ReferenceEquals(_scanCts, cts))
        {
            return;
        }

        Keyframes = scanned;
        IsIndexingKeyframes = false;
        IntroEnd.ResolveSnap();
        OutroStart?.ResolveSnap();
        RecomputeAll();
    }

    /// <summary>Cancel this row's in-flight/queued scan (on Remove / Clear).</summary>
    internal void CancelScan() => _scanCts?.Cancel();

    /// <summary>The row's current scan task (for tests to await the throttled scans deterministically).</summary>
    internal Task CurrentScanTask => _currentScanTask ?? Task.CompletedTask;

    // ---- Batch fan-out hooks (driven by BulkCutViewModel §4) --------------------------------

    /// <summary>Mark this row queued for a starting batch (resets progress + error).</summary>
    internal void MarkQueued()
    {
        _batchState = RowState.Queued;
        Error = null;
        _ledgerOutputPath = null;
        _ledgerWarnings = Array.Empty<string>();
        Progress = 0;
        OnPropertyChanged(nameof(RowState));
        OnPropertyChanged(nameof(OutputPath));
        OnPropertyChanged(nameof(Warning));
    }

    /// <summary>Advance a queued/running row to Running (never overrides a terminal state — guards late progress posts).</summary>
    internal void MarkRunning()
    {
        if (_batchState is RowState.Queued or RowState.Running)
        {
            if (_batchState != RowState.Running)
            {
                _batchState = RowState.Running;
                OnPropertyChanged(nameof(RowState));
            }
        }
    }

    /// <summary>Forward a per-row progress fraction (ignored once the row reached a terminal batch state).</summary>
    internal void SetProgress(double fraction)
    {
        if (IsTerminalBatchState)
        {
            return;
        }

        Progress = fraction;
    }

    private bool IsTerminalBatchState =>
        _batchState is RowState.Done or RowState.Failed or RowState.Skipped or RowState.Cancelled;

    /// <summary>Fold a T-095 ledger entry back onto the row: terminal state, warnings, output path, size, error.</summary>
    internal void ApplyResult(BulkTrimItemResult result)
    {
        _ledgerWarnings = result.Warnings ?? Array.Empty<string>();
        if (result.OutputPath is not null)
        {
            _ledgerOutputPath = result.OutputPath;
        }

        Error = result.Error;

        switch (result.Outcome)
        {
            case ItemOutcome.Done:
                _batchState = RowState.Done;
                Progress = 1d;
                SizeAfter = SafeFileSize(OutputPath);
                break;
            case ItemOutcome.Failed:
                _batchState = RowState.Failed;
                break;
            case ItemOutcome.Skipped:
                _batchState = RowState.Skipped;
                break;
            case ItemOutcome.Cancelled:
                _batchState = RowState.Cancelled;
                break;
            case ItemOutcome.NotStarted:
            default:
                // Never started → drop back to the computed state (re-runnable on a retry).
                _batchState = null;
                break;
        }

        OnPropertyChanged(nameof(RowState));
        OnPropertyChanged(nameof(OutputPath));
        OnPropertyChanged(nameof(Warning));
    }

    /// <summary>Clear the batch phase overlay back to the computed pre-batch state (on Clear / reset).</summary>
    internal void ResetBatchState()
    {
        _batchState = null;
        Error = null;
        _ledgerOutputPath = null;
        _ledgerWarnings = Array.Empty<string>();
        Progress = 0;
        SizeAfter = null;
        RecomputeAll();
    }

    /// <summary>Mark the row as failed-to-probe (excluded from the batch).</summary>
    internal void MarkLoadFailed()
    {
        _loadFailed = true;
        IsIndexingKeyframes = false;
        RecomputeAll();
    }

    // ---- Plumbing ---------------------------------------------------------------------------

    private void OnHandleChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A handle's Requested/Snapped change re-validates the row.
        if (e.PropertyName is nameof(CutMarkerViewModel.Snapped)
            or nameof(CutMarkerViewModel.Requested)
            or nameof(CutMarkerViewModel.Display))
        {
            RecomputeAll();
        }
    }

    private void RecomputeAll()
    {
        OnPropertyChanged(nameof(KeyframesReady));
        OnPropertyChanged(nameof(KeptDuration));
        OnPropertyChanged(nameof(MinKeptSpan));
        OnPropertyChanged(nameof(IsValidCut));
        OnPropertyChanged(nameof(IsNoOpTrim));
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(OutputPath));
        OnPropertyChanged(nameof(HasOutro));
        OnPropertyChanged(nameof(RowState));
    }

    private string ComputeBaseOutputPath()
    {
        var full = System.IO.Path.GetFullPath(Path);
        var dir = System.IO.Path.GetDirectoryName(full) ?? string.Empty;
        var name = System.IO.Path.GetFileNameWithoutExtension(full);
        var ext = System.IO.Path.GetExtension(full);
        return System.IO.Path.Combine(dir, $"{name}_trimmed{ext}");
    }

    /// <summary>On-disk byte size of <paramref name="path"/>, or 0 if it can't be read. Never throws.</summary>
    private static long SafeFileSize(string path)
    {
        try
        {
            var fi = new System.IO.FileInfo(path);
            return fi.Exists ? fi.Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}
