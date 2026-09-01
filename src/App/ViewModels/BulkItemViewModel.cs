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
using VideoSplitJoiner.Core.Thumbnails;

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

    // A mean GOP at or beyond this is "coarse" - cuts can move noticeably (mirrors the Split screen).
    // T-120: the test is >= , not > . A file whose mean GOP is EXACTLY 4.0s is the very grid that makes
    // snapping surprising (5s and 6s both land on 4s), and the old strict > silently skipped warning it.
    private static readonly TimeSpan CoarseGopThreshold = TimeSpan.FromSeconds(4);

    // T-120: warn whenever a cut ACTUALLY moved more than this, regardless of the mean GOP - the mean
    // hides a locally-coarse stretch, and what surprises the user is the offset on THEIR cut. Matches
    // the planner's own rule (SplitPlan).
    private static readonly TimeSpan NoticeableSnapThreshold = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    /// <summary>Width (px) of the per-row cut-point frame thumbnails (T-108) handed to the shared service.</summary>
    public const int ThumbnailWidth = 64;

    /// <summary>
    /// Default settle window before a handle move triggers a per-row frame grab (T-108). Deliberately
    /// slower than the scrub-hover debounce (<see cref="ThumbnailPreviewViewModel.DefaultDebounce"/> = 60ms):
    /// the cut settles, so a slower debounce coalesces a drag into a single grab.
    /// </summary>
    public static readonly TimeSpan DefaultThumbnailDebounce = TimeSpan.FromMilliseconds(200);

    private readonly IMediaProbe _probe;
    private readonly SemaphoreSlim _scanGate;

    // T-108: per-handle frame grabbers (debounce + latest-wins + cancel-prior), null when no thumbnail
    // service was injected (existing BulkItemViewModel tests construct rows without one → grabbers inert).
    private readonly HandleThumbnailGrabber? _introGrabber;
    private readonly HandleThumbnailGrabber? _outroGrabber;

    /// <summary>
    /// Awaitable completion of this row's most recent cut-point frame grabs (T-137). Exposed
    /// <c>internal</c> for tests via <c>InternalsVisibleTo</c>: the grab pipeline is deliberately
    /// fire-and-forget so a handle move never blocks the UI, which left tests with nothing to wait on
    /// but a wall-clock timeout - and a wall-clock wait loses when the machine is busy. Nothing in the
    /// production path reads this.
    /// </summary>
    internal Task InFlightGrabs => Task.WhenAll(
        _introGrabber?.InFlightGrab ?? Task.CompletedTask,
        _outroGrabber?.InFlightGrab ?? Task.CompletedTask);
    private string? _introThumbnailPath;
    private string? _outroThumbnailPath;

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
    ///
    /// <para>T-108: <paramref name="thumbnails"/> is the tab VM's SHARED <see cref="IThumbnailService"/> —
    /// when non-null, the row grabs a small frame at the intro-end (and outro-start) cut point, debounced +
    /// latest-wins + cancel-prior (modelled on <see cref="ThumbnailPreviewViewModel"/>). <paramref name="thumbnailGate"/>
    /// bounds concurrent frame grabs across a large batch (a dedicated <see cref="SemaphoreSlim"/> mirroring
    /// the T-096 scan gate). <paramref name="thumbnailDebounce"/>/<paramref name="thumbnailDelay"/> are test
    /// seams (default = <see cref="DefaultThumbnailDebounce"/> over <see cref="Task.Delay(TimeSpan, CancellationToken)"/>).
    /// A null <paramref name="thumbnails"/> leaves the grabbers inert (existing tests keep compiling / stay
    /// grab-free).</para>
    /// </summary>
    public BulkItemViewModel(
        string path,
        IMediaProbe probe,
        SemaphoreSlim scanGate,
        TimeSpan? defaultIntro = null,
        IThumbnailService? thumbnails = null,
        SemaphoreSlim? thumbnailGate = null,
        TimeSpan? thumbnailDebounce = null,
        Func<TimeSpan, CancellationToken, Task>? thumbnailDelay = null)
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

        if (thumbnails is not null)
        {
            var debounce = thumbnailDebounce is { } d && d > TimeSpan.Zero ? d : DefaultThumbnailDebounce;
            var delay = thumbnailDelay ?? ((wait, ct) => Task.Delay(wait, ct));
            _introGrabber = new HandleThumbnailGrabber(
                thumbnails, thumbnailGate, ThumbnailWidth, debounce, delay, p => IntroThumbnailPath = p);
            _outroGrabber = new HandleThumbnailGrabber(
                thumbnails, thumbnailGate, ThumbnailWidth, debounce, delay, p => OutroThumbnailPath = p);
        }
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

        // T-108: the resolve above runs BEFORE the OutroStart setter subscribes OnHandleChanged, so the
        // handle event won't fire the first grab — kick it explicitly (also covers a no-change re-snap).
        RequestOutroThumbnail();
    }

    /// <summary>Drop the outro-start handle (keep now runs to EOF).</summary>
    public void ClearOutro()
    {
        OutroStart = null;
        RecomputeAll();

        // T-108: cancel any in-flight outro grab and drop its frame so the (now-hidden) outro chip clears.
        _outroGrabber?.Cancel();
        OutroThumbnailPath = null;
    }

    // ---- Cut-point frame thumbnails (T-108) -------------------------------------------------

    /// <summary>
    /// Temp jpg PATH of the frame at the intro-end cut point (or null while loading / if the grab failed).
    /// The view binds it through <c>PathToBitmapConverter</c> (OnLoad → the file isn't held open); null shows
    /// the muted placeholder chip. WPF-free (a plain string) — the grabber is pure Task/CancellationToken.
    /// </summary>
    public string? IntroThumbnailPath
    {
        get => _introThumbnailPath;
        private set => SetProperty(ref _introThumbnailPath, value);
    }

    /// <summary>
    /// Temp jpg PATH of the frame at the outro-start cut point (null while loading / if unavailable / when
    /// there is no outro). Cleared by <see cref="ClearOutro"/>; grabbed at the outro handle's snapped time.
    /// </summary>
    public string? OutroThumbnailPath
    {
        get => _outroThumbnailPath;
        private set => SetProperty(ref _outroThumbnailPath, value);
    }

    /// <summary>Re-grab the intro-end frame at the current snapped time (debounced + latest-wins). No-op when inert.</summary>
    private void RequestIntroThumbnail() => _introGrabber?.Request(Path, IntroEnd.Snapped);

    /// <summary>Re-grab the outro-start frame at the current snapped time (debounced + latest-wins). No-op without an outro.</summary>
    private void RequestOutroThumbnail()
    {
        if (HasOutro)
        {
            _outroGrabber?.Request(Path, OutroStart!.Snapped);
        }
    }

    /// <summary>Grab both cut-point frames (on keyframe-resolve / apply-to-all / profile-apply, when Snapped becomes real).</summary>
    private void RequestAllThumbnails()
    {
        RequestIntroThumbnail();
        RequestOutroThumbnail();
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

    /// <summary>
    /// The intro-end the BATCH will actually cut at — <c>Snapped</c> on the lossless path, <c>Requested</c>
    /// under Exact cut (T-127 review finding #1). Eligibility (<see cref="IsValidCut"/>,
    /// <see cref="IsNoOpTrim"/>, <see cref="KeptDuration"/>) must be computed from this, not from the
    /// snapped value: <see cref="BuildBulkTrimItem"/> hands the engine <c>IntroEnd.Requested</c>, so on a
    /// coarse GOP an Exact-mode row whose request snaps back to 0 would otherwise be judged a no-op and
    /// excluded — and, since T-127, told "nothing to trim yet" for a trim Exact mode performs correctly.
    /// </summary>
    private TimeSpan EffectiveIntroEnd => _exactCut ? IntroEnd.Requested : IntroEnd.Snapped;

    /// <summary>The outro-start the batch will actually cut at — see <see cref="EffectiveIntroEnd"/>.</summary>
    private TimeSpan? EffectiveOutroStart =>
        HasOutro ? (_exactCut ? OutroStart!.Requested : OutroStart!.Snapped) : (TimeSpan?)null;

    private TimeSpan? OutroStartSnapped => HasOutro ? OutroStart!.Snapped : (TimeSpan?)null;

    /// <summary>Kept-middle length <c>(OutroStartSnapped ?? Duration) − IntroEndSnapped</c>; null until keyframes ready.</summary>
    public TimeSpan? KeptDuration =>
        KeyframesReady && Duration is { } d ? (EffectiveOutroStart ?? d) - EffectiveIntroEnd : null;

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

            var upper = EffectiveOutroStart ?? d;
            return EffectiveIntroEnd >= TimeSpan.Zero
                && upper <= d
                && EffectiveIntroEnd < upper - MinKeptSpan;
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

            var introAtStart = EffectiveIntroEnd <= BoundaryEpsilon;
            var outroAtEof = HasOutro && d - EffectiveOutroStart!.Value <= BoundaryEpsilon;
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

    /// <summary>
    /// T-125: under EXACT cutting the cut lands on the requested time, so the row must stop advertising
    /// a keyframe offset that will not happen. Propagated from the tab's precision choice.
    /// </summary>
    public void SetExactCut(bool exact)
    {
        if (_exactCut == exact)
        {
            return;
        }

        _exactCut = exact;
        IntroEnd.SuppressSnapNote = exact;
        if (OutroStart is { } outro)
        {
            outro.SuppressSnapNote = exact;
        }

        // Review finding #1: the precision flip changes which cut point eligibility is measured against
        // (EffectiveIntroEnd), so it must recompute the whole derived set — not just the warning text.
        RecomputeAll();
    }

    private bool _exactCut;

    /// <summary>Largest absolute snap offset across this row's handles (T-120) — how far the cut really moved.</summary>
    private TimeSpan MaxSnapOffset()
    {
        var intro = IntroEnd.Delta.Duration();
        var outro = HasOutro ? OutroStart!.Delta.Duration() : TimeSpan.Zero;
        return intro >= outro ? intro : outro;
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
                if (gop >= CoarseGopThreshold)
                {
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"coarse keyframes — cuts may move ~{gop.TotalSeconds:0.0}s"));
                }

                // T-120: report the snap that actually happened on THIS row, even on a fine mean grid.
                var worstSnap = _exactCut ? TimeSpan.Zero : MaxSnapOffset();
                if (worstSnap >= NoticeableSnapThreshold)
                {
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"cut moved {worstSnap.TotalSeconds:0.0}s to the nearest keyframe"));
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

    /// <summary>
    /// The user's raw checkbox intent — what the row checkbox binds to (T-127). Independent of whether the
    /// row is currently ELIGIBLE: a freshly imported row is a no-op trim and therefore excluded, but the
    /// user's intent to include it is still <c>true</c>, so it starts ticked and stays ticked until they
    /// untick it. Used by apply-to-all targeting (SPEC-011 I56) and as one half of <see cref="IsEnabled"/>.
    /// </summary>
    /// <remarks>
    /// Before T-127 the checkbox bound to <see cref="IsEnabled"/>, which conflated intent with eligibility:
    /// the getter answered false for every freshly imported row while the backing intent field was already
    /// true, so a click wrote true over true, the setter's <c>!=</c> guard short-circuited, and no
    /// <c>PropertyChanged</c> was raised. The gesture was dead, and because nothing pushed the getter back
    /// to the target the box could sit rendering ticked while the row was excluded. Clicking twice wrote
    /// false and silently poisoned the row: it then stayed excluded even after a real cut was set, and
    /// apply-to-all skipped it. Intent is now its own property, and it always notifies.
    /// </remarks>
    public bool IsCheckedByUser
    {
        get => _isEnabledByUser;
        set
        {
            if (_isEnabledByUser != value)
            {
                _isEnabledByUser = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(ExclusionReason));
                OnPropertyChanged(nameof(IsExcludedDespiteBeingChecked));
            }
        }
    }

    /// <summary>
    /// Whether this row will actually be run: the user wants it AND the app judges it usable. Read-only —
    /// the engine and <c>CanRunBatch</c> filter on this; the user toggles <see cref="IsCheckedByUser"/>.
    /// </summary>
    public bool IsEnabled => _isEnabledByUser && !IsAutoDisabled;

    // Auto-disabled once the row is known-ineligible (Loading is NOT auto-disabled — CanRunBatch waits on it).
    private bool IsAutoDisabled => _originalDeleted || _loadFailed || (KeyframesReady && (IsNoOpTrim || !IsValidCut));

    /// <summary>
    /// True when the user has ticked this row but the app is still excluding it — the state that used to be
    /// invisible. The UI shows <see cref="ExclusionReason"/> for these rows so "ticked but not counted" is
    /// never silent.
    /// </summary>
    public bool IsExcludedDespiteBeingChecked => _isEnabledByUser && IsAutoDisabled;

    /// <summary>
    /// Why a ticked row is nonetheless excluded, or null when it is not excluded. Phrased as a state, not an
    /// error — "nothing to trim yet" is the normal condition of a row you just imported.
    /// </summary>
    public string? ExclusionReason
    {
        get
        {
            if (!_isEnabledByUser || !IsAutoDisabled)
            {
                return null;
            }

            if (_loadFailed)
            {
                return "can't read this file";
            }

            // A row that is still scanning is not excluded at all — IsAutoDisabled is false while
            // KeyframesReady is false, so the !IsAutoDisabled guard above has already returned null.

            if (IsNoOpTrim)
            {
                return "nothing to trim yet — set an intro or outro";
            }

            // IsValidCut fails for two materially different reasons and they need different sentences
            // (review finding #2): a handle genuinely outside the file, versus handles that are both
            // inside it but too close together to keep anything.
            if (Duration is { } dur)
            {
                var upper = EffectiveOutroStart ?? dur;
                if (EffectiveIntroEnd >= TimeSpan.Zero && upper <= dur)
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"intro and outro are too close — keep at least {MinKeptSpan.TotalSeconds:0.0}s");
                }
            }

            return "cut is outside the video";
        }
    }

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

        // Review finding #4: every OTHER mutator of the eligibility inputs funnels through RecomputeAll.
        // This one did not, so RESTARTING a scan on an already-scanned row flipped KeyframesReady (and
        // therefore IsAutoDisabled, IsEnabled and ExclusionReason) with nothing pushed to the view — a
        // stale "nothing to trim yet" line and a stale checkbox meaning, the exact bug class T-127 fixes.
        RecomputeAll();
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
                RequestAllThumbnails(); // T-108: grab at the (identity-snapped) cut even without keyframes
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

        // T-108: keyframes just resolved (Snapped became real) → initial cut-point frame grab. A ResolveSnap
        // that did NOT move Snapped raises no handle event, so this explicit kick covers that gap.
        RequestAllThumbnails();
    }

    /// <summary>
    /// Cancel this row's in-flight/queued scan AND any in-flight frame grabs (on Remove / Clear). The
    /// grabber CTS cancel drops a superseded grab so a removed row never touches ffmpeg or updates state.
    /// </summary>
    internal void CancelScan()
    {
        _scanCts?.Cancel();
        _introGrabber?.Cancel();
        _outroGrabber?.Cancel();
    }

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
    /// <summary>
    /// T-144 - the source file has been sent to the Recycle Bin. The row stays visible (the trim it
    /// produced is real and worth seeing) but it can never be cut again: its source is gone.
    /// </summary>
    internal void MarkOriginalDeleted()
    {
        _originalDeleted = true;
        _isEnabledByUser = false;   // it can no longer take part in a batch
        OnPropertyChanged(nameof(OriginalDeleted));
        OnPropertyChanged(nameof(IsCheckedByUser));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ExclusionReason));
        OnPropertyChanged(nameof(IsExcludedDespiteBeingChecked));
        OnPropertyChanged(nameof(Warning));
    }

    private bool _originalDeleted;

    /// <summary>True once this row's source has been binned (T-144) - it cannot be re-cut.</summary>
    public bool OriginalDeleted => _originalDeleted;

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

        // T-108: a Snapped change moves the cut → re-grab THAT handle's frame at the new snapped time.
        if (e.PropertyName == nameof(CutMarkerViewModel.Snapped))
        {
            if (ReferenceEquals(sender, IntroEnd))
            {
                RequestIntroThumbnail();
            }
            else if (ReferenceEquals(sender, OutroStart))
            {
                RequestOutroThumbnail();
            }
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
        OnPropertyChanged(nameof(ExclusionReason));
        OnPropertyChanged(nameof(IsExcludedDespiteBeingChecked));
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

    /// <summary>
    /// Per-handle cut-point frame grabber (T-108), modelled on <see cref="ThumbnailPreviewViewModel"/>'s
    /// debounce + latest-wins + cancel-prior discipline. Each <see cref="Request"/> cancels the prior
    /// in-flight grab (its debounce wait faults, so a superseded request never reaches ffmpeg), waits the
    /// debounce window off the UI thread, acquires the shared concurrency gate ONLY around the actual grab
    /// (never during the debounce), then — if still the newest request — marshals the resolved path back
    /// onto the captured <see cref="SynchronizationContext"/> (the WPF dispatcher in the app, the test's
    /// pumpable context under xUnit) via <see cref="Progress{T}"/>, exactly like the codebase's other VMs.
    /// WPF-free: pure <see cref="Task"/> / <see cref="CancellationToken"/> / delay-func. Best-effort — any
    /// failure resolves to a null path (the placeholder chip shows) and never throws into the UI.
    /// </summary>
    private sealed class HandleThumbnailGrabber
    {
        private readonly IThumbnailService _thumbnails;
        private readonly SemaphoreSlim? _gate;
        private readonly int _width;
        private readonly TimeSpan _debounce;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly Action<string?> _apply;
        private readonly IProgress<PathResult> _postResult;

        // The current (latest) request; swapped — and the prior cancelled — on every Request (latest-wins).
        private CancellationTokenSource? _requestCts;

        /// <summary>
        /// The most recently issued grab, kept ONLY so a test can await it (T-137). The pipeline is
        /// fire-and-forget by design; that left tests waiting on the clock, which loses under load.
        /// </summary>
        private Task _inFlight = Task.CompletedTask;

        /// <summary>Awaitable completion of the most recent grab (T-137) - tests only.</summary>
        internal Task InFlightGrab => _inFlight;

        // Monotonic request id; a resolved grab commits its path only when its id is still the newest, so a
        // superseded-but-not-yet-cancelled grab can never clobber a newer result.
        private long _requestId;

        public HandleThumbnailGrabber(
            IThumbnailService thumbnails,
            SemaphoreSlim? gate,
            int width,
            TimeSpan debounce,
            Func<TimeSpan, CancellationToken, Task> delay,
            Action<string?> apply)
        {
            _thumbnails = thumbnails;
            _gate = gate;
            _width = width;
            _debounce = debounce;
            _delay = delay;
            _apply = apply;
            _postResult = new Progress<PathResult>(OnResolved);
        }

        /// <summary>Debounce → (gated) grab → marshal-back for the frame of <paramref name="inputPath"/> at <paramref name="time"/>, latest-wins.</summary>
        public void Request(string inputPath, TimeSpan time)
        {
            Cancel();
            var cts = new CancellationTokenSource();
            _requestCts = cts;
            var id = ++_requestId;
            _inFlight = GrabAsync(inputPath, time, id, cts);
        }

        /// <summary>Cancel + dispose the in-flight request's CTS (if any) so a superseded/removed grab is dropped.</summary>
        public void Cancel()
        {
            var cts = _requestCts;
            _requestCts = null;
            if (cts is null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already retired by its own finally — nothing to cancel.
            }
        }

        private async Task GrabAsync(string inputPath, TimeSpan time, long id, CancellationTokenSource cts)
        {
            try
            {
                // Debounce: settle before touching ffmpeg. A newer Request cancels this wait.
                await _delay(_debounce, cts.Token).ConfigureAwait(false);

                string? path;
                if (_gate is not null)
                {
                    // Hold the concurrency permit ONLY around the grab — never during the debounce — so a
                    // large batch caps concurrent ffmpeg frame grabs without permits sitting idle.
                    await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
                    try
                    {
                        path = await _thumbnails.GetThumbnailAsync(inputPath, time, _width, cts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                else
                {
                    path = await _thumbnails.GetThumbnailAsync(inputPath, time, _width, cts.Token).ConfigureAwait(false);
                }

                if (cts.Token.IsCancellationRequested)
                {
                    return;
                }

                _postResult.Report(new PathResult(id, path));
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer request / a Cancel — drop silently (never clobbers a newer result).
            }
            catch
            {
                // Best-effort — any other failure shows the placeholder.
            }
            finally
            {
                if (ReferenceEquals(_requestCts, cts))
                {
                    _requestCts = null;
                }

                cts.Dispose();
            }
        }

        /// <summary>Commit a resolved grab on the captured context — only when it is still the newest request.</summary>
        private void OnResolved(PathResult result)
        {
            if (result.Id != _requestId)
            {
                return; // a newer request superseded this grab → drop it
            }

            _apply(string.IsNullOrEmpty(result.Path) ? null : result.Path);
        }

        private readonly record struct PathResult(long Id, string? Path);
    }
}
