using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Profiles;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Thumbnails;
using VideoSplitJoiner.App.Io;
using VideoSplitJoiner.Core.Io;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// The batch lifecycle of a Bulk Cut run (D-004 / T-096): mirrors <see cref="BatchOutcome"/> with the
/// two pre-terminal states the tab VM owns (<see cref="Preparing"/> / <see cref="Running"/>).
/// </summary>
public enum BulkBatchState
{
    /// <summary>No run yet, or reset.</summary>
    Idle,

    /// <summary>Building the runnable list + seeding the estimate (before the engine loop starts).</summary>
    Preparing,

    /// <summary>The engine loop is running.</summary>
    Running,

    /// <summary>Every row finished Done.</summary>
    Completed,

    /// <summary>Ran to the end but ≥1 row Failed / Skipped.</summary>
    CompletedWithFailures,

    /// <summary>Cancelled mid-batch.</summary>
    Cancelled,

    /// <summary>A batch disk pre-flight blocked the whole run before any ffmpeg ran.</summary>
    Blocked,
}

/// <summary>
/// The outcome of an <c>Apply cut points → all</c> gesture (D-004 matrix #17): how many rows the copy
/// was applied to and which of those it <b>invalidated</b> (reported, never silently dropped).
/// </summary>
/// <param name="AppliedCount">Number of target rows the source's cut points were copied to.</param>
/// <param name="InvalidatedRows">The subset whose copied cut no longer produces a valid trim.</param>
public sealed record ApplyToAllReport(int AppliedCount, IReadOnlyList<BulkItemViewModel> InvalidatedRows);

/// <summary>
/// Why a profile-thumbnail attach attempt ended the way it did (T-107 store-and-attach, T-129 reporting).
/// <see cref="Attached"/> is success; every other member is a distinct, user-explainable failure. The
/// DELIBERATE gesture (<see cref="BulkCutViewModel.UploadThumbnail"/>) turns a failure into a message on
/// the screen's existing error surface; the AUTO capture on save
/// (<see cref="BulkCutViewModel.SaveProfileWithAutoThumbnailAsync"/>) keeps ignoring it, because a
/// best-effort side effect of "Save" must never interrupt the save.
/// </summary>
public enum ThumbnailAttachOutcome
{
    /// <summary>The image was copied into the store and its path folded onto the profile.</summary>
    Attached,

    /// <summary>No profile was supplied — there is nothing to hang a thumbnail off.</summary>
    NoProfile,

    /// <summary>No image path was supplied (null / blank) — nothing was chosen.</summary>
    NoImageChosen,

    /// <summary>The named profile is not persisted yet, so a thumbnail cannot be attached to it.</summary>
    ProfileNotSaved,

    /// <summary>The chosen image is missing or unusable as a source (the store refused it).</summary>
    ImageUnreadable,

    /// <summary>The store could not copy the image in (locked target, unwritable root, I/O error).</summary>
    StoreFailed,
}

/// <summary>
/// The Bulk Cut tab view-model (D-004 / T-096): a list of <see cref="BulkItemViewModel"/> rows, an
/// aggregate <see cref="OperationViewModel"/> (overall bar + taskbar/title + the batch cancel), the
/// apply-to-all gesture (outro measured FROM END, each target re-snapped + re-validated), and
/// <see cref="RunBatchAsync"/> which <b>delegates</b> the whole batch to the T-095
/// <see cref="IBulkTrimEngine.RunAsync"/> — the VM owns NO batch loop / collision resolution / disk
/// pre-flight / cancel-sweep. Mirrors <see cref="JoinViewModel"/> throughout; deliberately WPF-free.
/// </summary>
public sealed class BulkCutViewModel : ObservableObject
{
    private readonly IMediaProbe _probe;
    private readonly ISplitEngine _splitEngine;
    private readonly IThumbnailService _thumbnails;
    private readonly IAppSettings _settings;
    private readonly IBulkTrimEngine _bulkTrimEngine;
    private readonly ProfileThumbnailStore _thumbnailStore;

    // T-107: width (px) of the auto-captured intro-end frame stored as a profile's default thumbnail
    // (displayed tiny in the picker, but stored crisp so an upload/override reads cleanly too).
    private const int ProfileThumbnailWidth = 96;

    /// <summary>
    /// Default settle window (T-115) before a SETTLED row selection opens in the shared preview player.
    /// Short enough to feel instant on a deliberate single click (~250ms is imperceptible), long enough
    /// to coalesce a fast arrow/scroll through rows into ONE FFME open (the heavy decoder init) — only
    /// the row you land on opens, not every row swept past.
    /// </summary>
    public static readonly TimeSpan DefaultSelectionOpenDebounce = TimeSpan.FromMilliseconds(250);

    // §3 — the single bounded scan gate, owned here and shared into every row (max 3 concurrent ffprobe scans).
    private readonly SemaphoreSlim _scanGate = new(3, 3);

    // T-108 — a SEPARATE bounded gate shared into every row so a large batch caps concurrent ffmpeg
    // cut-point frame grabs at 3. Deliberately NOT the scan gate: frame thumbnails are eye-candy, and
    // sharing the scan gate would let ffmpeg grabs starve the ffprobe keyframe scans that gate CanRunBatch.
    private readonly SemaphoreSlim _thumbnailGate = new(3, 3);

    // T-108 test seams: the per-row grab debounce + delay func, forwarded into each row. Null ⇒ each row
    // uses its production defaults (BulkItemViewModel.DefaultThumbnailDebounce over Task.Delay).
    private readonly TimeSpan? _thumbnailDebounce;
    private readonly Func<TimeSpan, CancellationToken, Task>? _thumbnailDelay;

    // T-115: the preview-open debounce interval + delay-func seams (mirrors the T-108 thumbnail seams).
    // Production = DefaultSelectionOpenDebounce over Task.Delay; tests inject an immediate/gated delay so
    // the debounced open is deterministic.
    private readonly TimeSpan _selectionOpenDebounce;
    private readonly Func<TimeSpan, CancellationToken, Task> _selectionOpenDelay;

    // T-115: the pending debounced preview-open request. Swapped — and the prior one cancelled — on every
    // selection change (latest-wins), and cancelled outright on a null/clear selection and when a batch run
    // starts, so a stale open can never land after an Unload / after the run's Stop.
    private CancellationTokenSource? _openCts;

    // T-128 / review finding #3: set while a bulk selection write is in flight so the per-row
    // PropertyChanged fan-out does not trigger one O(N) batch refresh per row.
    // T-144: how a deleted original is disposed of. NULL means deletion is UNAVAILABLE rather than
    // "delete permanently" - a test that forgets to inject one must not be able to bin real files, and
    // the same defensive default is why Exact+ReplaceOriginal refuses without a disposer (T-130).
    private readonly IOriginalDisposer? _originalDisposer;

    private bool _suspendRunStateRefresh;

    private readonly object _progressLock = new();
    private double _lastOverall;

    private BulkBatchState _batchState = BulkBatchState.Idle;
    private CollisionPolicy _collisionPolicy = CollisionPolicy.AutoSuffix;
    private bool _overwrite;
    private bool _replaceOriginal;

    // T-133: null in settings (older file / never set) reads as the default, ON.
    private bool _applyCutToAllRows = true;
    private bool _exactCut;
    private ApplyToAllReport? _applyToAllReport;
    private IReadOnlyList<BulkTrimItemResult> _lastFailedItems = Array.Empty<BulkTrimItemResult>();
    private BulkItemViewModel? _selectedItem;
    private CutProfile? _selectedProfile;

    // T-129: the LAST failure this VM reported for an explicit thumbnail upload, remembered only so a
    // later successful upload retracts ITS OWN message and never a batch failure sharing the surface.
    private UserFacingError? _thumbnailUploadError;

    /// <summary>
    /// Create the tab VM sharing the App's media probe / split engine / thumbnail service / settings.
    /// When <paramref name="bulkTrimEngine"/> is null a real <see cref="BulkTrimEngine"/> is default-
    /// constructed over the SAME <paramref name="splitEngine"/> + a <see cref="KeptMiddleRequestBuilder"/>
    /// over the SAME <paramref name="probe"/> (no second engine / probe); tests inject a fake.
    /// <paramref name="player"/> is the single shared mini-preview player (T-100): selecting a row opens
    /// that file in THIS one FFME-backed <see cref="PlayerViewModel"/> (never a player per row). The
    /// composition root passes the Bulk tab's own <see cref="FfmeMediaPlayer"/>; when omitted it defaults
    /// to a no-op <see cref="NullMediaPlayer"/> so existing constructions / tests keep compiling.
    /// <paramref name="selectionOpenDebounce"/> / <paramref name="selectionOpenDelay"/> are the T-115 test
    /// seams for the preview-open debounce (default = <see cref="DefaultSelectionOpenDebounce"/> over
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>); tests inject an immediate/gated delay so the
    /// debounced open is deterministic, mirroring the T-108 thumbnail seams.
    /// </summary>
    public BulkCutViewModel(
        IMediaProbe probe,
        ISplitEngine splitEngine,
        IThumbnailService thumbnails,
        IAppSettings settings,
        IBulkTrimEngine? bulkTrimEngine = null,
        IMediaPlayer? player = null,
        ProfileThumbnailStore? thumbnailStore = null,
        TimeSpan? thumbnailDebounce = null,
        Func<TimeSpan, CancellationToken, Task>? thumbnailDelay = null,
        TimeSpan? selectionOpenDebounce = null,
        Func<TimeSpan, CancellationToken, Task>? selectionOpenDelay = null,
        ISmartCutEngine? smartCut = null,
        IOriginalDisposer? originalDisposer = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _splitEngine = splitEngine ?? throw new ArgumentNullException(nameof(splitEngine));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        // T-125: the default batch runner also gets a smart-cut engine, so CutPrecision.Exact has
        // something to route to. Only built when a real ffmpeg runner is available (tests inject their
        // own engine and never reach this branch).
        // T-130: the disposer is passed too, so an Exact cut that replaces originals swaps the produced
        // file in through OriginalReplacer (backup + restore-on-failure + Recycle Bin) instead of the
        // combination being refused.
        _bulkTrimEngine = bulkTrimEngine ?? new BulkTrimEngine(
            _splitEngine,
            new KeptMiddleRequestBuilder(_probe),
            smartCut,
            new RecycleBinOriginalDisposer());
        _thumbnailDebounce = thumbnailDebounce; // T-108 test seams (null ⇒ per-row production defaults)
        _thumbnailDelay = thumbnailDelay;

        // T-115 preview-open debounce seams (null ⇒ production defaults; tests inject immediate/gated).
        // T-133: null (older settings file / never set) reads as the default, ON.
        _originalDisposer = originalDisposer;
        _applyCutToAllRows = _settings.BulkApplyCutToAllRows ?? true;

        _selectionOpenDebounce = selectionOpenDebounce is { } d && d > TimeSpan.Zero ? d : DefaultSelectionOpenDebounce;
        _selectionOpenDelay = selectionOpenDelay ?? ((wait, ct) => Task.Delay(wait, ct));

        // T-107: the file store for profile thumbnails (T-106). Default = the per-user root; tests inject a
        // store over a temp root. Constructing it is side-effect-free (only resolves a root string).
        _thumbnailStore = thumbnailStore ?? new ProfileThumbnailStore();

        // T-100: the ONE shared preview player, reusing the injected thumbnail service (mirrors how
        // SplitViewModel constructs its Player). Null player → inert NullMediaPlayer (tests / no-preview).
        Player = new PlayerViewModel(player ?? NullMediaPlayer.Instance, _thumbnails);

        Operation = new OperationViewModel();
        Items = new ObservableCollection<BulkItemViewModel>();
        Items.CollectionChanged += OnItemsChanged;
        Operation.PropertyChanged += OnOperationChanged;

        AddFilesCommand = new RelayCommand(p => _ = AddFilesAsync(AsPaths(p)));
        RemoveCommand = new RelayCommand(p => Remove(p as BulkItemViewModel), _ => true);
        ClearCommand = new RelayCommand(_ => Clear(), _ => CanClear);
        DeleteOriginalsCommand = new RelayCommand(_ => DeleteOriginals(), _ => CanDeleteOriginals);
        ApplyToAllCommand = new RelayCommand(p => ApplyToAll(p as BulkItemViewModel), _ => Items.Count > 1);
        SelectAllItemsCommand = new RelayCommand(_ => SetAllItemsChecked(true), _ => CanChangeSelection);
        SelectNoItemsCommand = new RelayCommand(_ => SetAllItemsChecked(false), _ => CanChangeSelection);
        RunBatchCommand = new RelayCommand(_ => _ = RunBatchAsync(), _ => CanRunBatch);
        SetIntroAtPlayheadCommand = new RelayCommand(_ => SetIntroAtPlayhead(), _ => CanSetCutAtPlayhead);
        SetOutroAtPlayheadCommand = new RelayCommand(_ => SetOutroAtPlayhead(), _ => CanSetCutAtPlayhead);
        CancelCommand = Operation.CancelCommand;

        // T-103: cut-profile commands — thin glue over the T-102 applier/persistence (no new model/persistence).
        Profiles = new ObservableCollection<CutProfile>();
        SaveProfileCommand = new RelayCommand(p => _ = SaveProfileWithAutoThumbnailAsync(p as string), _ => CanSaveProfile);
        ApplyProfileToSelectedCommand = new RelayCommand(_ => ApplyProfileToSelected(), _ => CanApplyProfileToSelected);
        ApplyProfileToAllCommand = new RelayCommand(_ => ApplyProfileToAll(), _ => CanApplyProfileToAll);
        DeleteProfileCommand = new RelayCommand(_ => DeleteSelectedProfile(), _ => HasSelectedProfile);
        ExportProfilesCommand = new RelayCommand(_ => ExportProfiles(), _ => HasProfiles);
        ImportProfilesCommand = new RelayCommand(_ => ImportProfiles());
        SnapshotProfileThumbnailCommand = new RelayCommand(
            _ => _ = SnapshotProfileThumbnailAsync(), _ => CanSnapshotProfileThumbnail);
        RefreshProfiles(); // project the persisted CutProfiles into the observable list on construct

        // T-101: the two "set at playhead" gestures enable only with a selected row AND a ready
        // preview player (a null-duration player has no real playhead to capture) — re-raise their
        // guards when the shared player's readiness flips (mirrors SplitViewModel.OnPlayerChanged).
        Player.PropertyChanged += OnPlayerChanged;
    }

    // ---- State ------------------------------------------------------------------------------

    /// <summary>The rows to trim, in add order (batch input order).</summary>
    public ObservableCollection<BulkItemViewModel> Items { get; }

    /// <summary>The aggregate operation — overall bar, taskbar/title, and the batch cancel.</summary>
    public OperationViewModel Operation { get; }

    /// <summary>
    /// The single shared mini-preview player (T-100 / G-037) — the ONE FFME-backed decoder the whole
    /// tab reuses. Selecting a row (<see cref="SelectedItem"/>) opens THAT file here; there is never a
    /// player per row. T-101's preview pane binds transport/scrub to this.
    /// </summary>
    public PlayerViewModel Player { get; }

    /// <summary>
    /// The currently-selected row (two-way bound from the list, T-101). Changing it lights the row up
    /// instantly (selection + <see cref="HasSelection"/> + the command CanExecute states are set
    /// synchronously) and, after a short debounce, opens the row's <see cref="BulkItemViewModel.Path"/>
    /// in the ONE shared <see cref="Player"/> (a null selection — list cleared / nothing selected —
    /// cancels any pending open and unloads it instead).
    ///
    /// <para>T-115: the preview open is <b>debounced (~250ms) + latest-wins</b> — a newer selection
    /// cancels the pending open, so arrowing/scrolling through N rows opens only the row you SETTLE on,
    /// never one heavy FFME decoder init per row swept past (the selection-lag root cause). The debounce
    /// is an ADDITIONAL upstream throttle in front of — not a replacement for — the last-line safety:
    /// the settled open still goes through <see cref="PlayerViewModel.Open"/> →
    /// <see cref="FfmeMediaPlayer"/>'s built-in <see cref="MediaReopenGuard"/> (T-080), so even a switch
    /// inside the debounce window never issues Open while a prior Close is still in flight.</para>
    ///
    /// <para>The first <see cref="AddFilesAsync"/> auto-selects the first row for convenience; removing
    /// the selected row re-points the selection at a neighbour (or unloads) so it never opens a
    /// just-removed file.</para>
    /// </summary>
    public BulkItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                // T-115: the selection and everything cheap that depends on it (highlight, HasSelection,
                // the playhead + profile command CanExecute states) update SYNCHRONOUSLY — the row lights
                // up instantly, never waiting on the heavy FFME preview open. ONLY that open is deferred
                // (debounced + latest-wins) in OpenOrUnloadSelected below, so arrowing through N rows opens
                // just the settled one instead of thrashing the element with N decoder inits.
                OnPropertyChanged(nameof(HasSelection));
                RaisePlayheadCommandStates();
                RaiseProfileCommandStates(); // Save / Apply→selected depend on the selection (T-103)

                OpenOrUnloadSelected(value);
            }
        }
    }

    /// <summary>
    /// True when a row is selected — drives the preview pane (T-101): the pane shows the reused
    /// <see cref="PlayerView"/> when a row is selected, and its "select a video to preview" hint when
    /// not.
    /// </summary>
    public bool HasSelection => _selectedItem is not null;

    // ---- Cut profiles (T-103) ---------------------------------------------------------------

    /// <summary>
    /// The saved cut profiles projected from <see cref="IAppSettings.CutProfiles"/> into an observable
    /// list the profiles-bar <c>ComboBox</c> binds to. Refreshed from settings on construct and after
    /// every <see cref="SaveProfile"/> / <see cref="DeleteSelectedProfile"/> (T-102 owns the persistence).
    /// </summary>
    public ObservableCollection<CutProfile> Profiles { get; }

    /// <summary>The profile chosen in the bar's <c>ComboBox</c> — the source for Apply / Delete.</summary>
    public CutProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                RaiseProfileCommandStates();
            }
        }
    }

    /// <summary>True when ≥1 profile is saved — drives the profiles bar's full-vs-empty affordance.</summary>
    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>True when a profile is selected in the bar (gates Apply/Delete).</summary>
    public bool HasSelectedProfile => _selectedProfile is not null;

    /// <summary>
    /// T-135 — the snapshot gesture needs a profile to attach to AND a frame on screen to capture.
    /// </summary>
    public bool CanSnapshotProfileThumbnail => _selectedProfile is not null && _selectedItem is not null && Player.IsReady;

    /// <summary>Why the snapshot button is unavailable, or null when it is available.</summary>
    public string? SnapshotUnavailableReason =>
        _selectedProfile is null ? "Pick a cut profile first"
        : _selectedItem is null || !Player.IsReady ? "Play a video in the preview first"
        : null;

    /// <summary>Save-current-as is enabled only with a selected source row to capture the cut from.</summary>
    public bool CanSaveProfile => _selectedItem is not null;

    /// <summary>Apply→selected needs both a chosen profile and a selected target row.</summary>
    public bool CanApplyProfileToSelected => _selectedProfile is not null && _selectedItem is not null;

    /// <summary>Apply→all needs a chosen profile and at least one user-checked row to apply it to.</summary>
    public bool CanApplyProfileToAll => _selectedProfile is not null && Items.Any(i => i.IsCheckedByUser);

    /// <summary>The batch lifecycle state.</summary>
    public BulkBatchState BatchState
    {
        get => _batchState;
        private set => SetProperty(ref _batchState, value);
    }

    /// <summary>How output-path collisions are resolved (default <see cref="CollisionPolicy.AutoSuffix"/>).</summary>
    public CollisionPolicy CollisionPolicy
    {
        get => _collisionPolicy;
        set => SetProperty(ref _collisionPolicy, value);
    }

    /// <summary>Per-run overwrite toggle — when true the run uses <see cref="CollisionPolicy.Overwrite"/>.</summary>
    public bool Overwrite
    {
        get => _overwrite;
        set => SetProperty(ref _overwrite, value);
    }

    /// <summary>
    /// T-123 (epic G-041): write the trimmed result OVER each original instead of a new <c>_trimmed</c>
    /// file. Destructive and opt-in - the engine still produces to a temp file and only replaces after a
    /// verified-complete run, and the replaced original goes to the Recycle Bin, but a wrong cut is far
    /// costlier here, so <see cref="RunBatchAsync"/> requires an explicit confirmation.
    ///
    /// <para>While on, the collision policy is inert (the destination is always the source) - the view
    /// greys the collision control and <see cref="CollisionIsInert"/> drives that.</para>
    /// </summary>
    public bool ReplaceOriginal
    {
        get => _replaceOriginal;
        set
        {
            if (SetProperty(ref _replaceOriginal, value))
            {
                OnPropertyChanged(nameof(CollisionIsInert));
                OnPropertyChanged(nameof(OutputNote));
            }
        }
    }

    /// <summary>
    /// T-133 — whether the two "set at playhead" gestures fan the cut out to every CHECKED row instead of
    /// only the previewed one. Defaults to ON: this tab is called Bulk Cut, and the single-row behaviour is
    /// exactly what made a user set one cut, press Run, and get one file out of twelve.
    ///
    /// <para>The fan-out reuses <see cref="ApplyToAll"/> wholesale rather than re-deriving it — including
    /// the clause that is easy to get wrong, that the outro is measured from the END of each file so one
    /// gesture fits episodes of different lengths.</para>
    /// </summary>
    public bool ApplyCutToAllRows
    {
        get => _applyCutToAllRows;
        set
        {
            if (SetProperty(ref _applyCutToAllRows, value))
            {
                _settings.BulkApplyCutToAllRows = value;
                OnPropertyChanged(nameof(SetAtPlayheadScopeNote));
            }
        }
    }

    /// <summary>One line stating which rows the next set-at-playhead gesture will touch.</summary>
    public string SetAtPlayheadScopeNote => _applyCutToAllRows
        ? "applies to every ticked video"
        : "applies to the previewed video only";

    /// <summary>True when the collision controls have no effect (replace-original owns the destination).</summary>
    public bool CollisionIsInert => _replaceOriginal;

    /// <summary>
    /// T-125 (epic G-042): cut EXACTLY where the user set the handles instead of snapping to the
    /// nearest keyframe. Off by default - the lossless path is the app's identity and the right choice
    /// for most batches. On, roughly one GOP (~1-2s of video) is re-encoded per cut and the rest is
    /// still copied untouched; a source whose codecs cannot be reproduced falls back automatically and
    /// says so on the row.
    /// </summary>
    public bool ExactCut
    {
        get => _exactCut;
        set
        {
            if (SetProperty(ref _exactCut, value))
            {
                OnPropertyChanged(nameof(PrecisionNote));
                foreach (var row in Items)
                {
                    row.SetExactCut(value);
                }
            }
        }
    }

    /// <summary>Plain-language statement of the active precision, so the trade-off is never hidden.</summary>
    public string PrecisionNote =>
        _exactCut
            ? "Exact — cuts land where you set them (re-encodes ~1s per cut)"
            : "Lossless — cuts snap to the nearest keyframe (instant, no quality loss)";

    /// <summary>The footer's plain-language statement of where output goes - it must never lie.</summary>
    public string OutputNote =>
        _replaceOriginal
            ? "Output → REPLACES each original file · originals go to the Recycle Bin"
            : "Output → same folder · _trimmed suffix · originals kept";

    /// <summary>
    /// Confirmation seam for the destructive run (T-123). Given the number of originals that would be
    /// replaced, returns true to proceed. Defaults to REFUSING, so a host that forgets to wire a prompt
    /// can never silently destroy the user's masters; the view supplies the real dialog.
    /// </summary>
    public Func<int, bool> ConfirmReplaceOriginals { get; set; } = _ => false;

    /// <summary>
    /// The most recent apply report (applied count + invalidated rows), or null. Shared surface for
    /// BOTH the per-row apply-to-all gesture (<see cref="ApplyToAll"/>) AND the profile apply commands
    /// (<see cref="ApplyProfileToSelected"/> / <see cref="ApplyProfileToAll"/>, T-103) — same
    /// <see cref="global::VideoSplitJoiner.App.ViewModels.ApplyToAllReport"/> shape, reported not silent.
    /// </summary>
    public ApplyToAllReport? ApplyToAllReport
    {
        get => _applyToAllReport;
        private set
        {
            if (SetProperty(ref _applyToAllReport, value))
            {
                OnPropertyChanged(nameof(ApplyReportSummary));
            }
        }
    }

    /// <summary>
    /// A compact, human note for the most recent apply (T-097's apply-to-all + T-103's profile apply): how
    /// many rows the cut was applied to and how many that left invalid (the invalidated rows ALSO keep their
    /// own per-row red <c>invalid</c> state chip — this is the aggregate line). Null ⇒ nothing to show.
    /// </summary>
    public string? ApplyReportSummary
    {
        get
        {
            if (_applyToAllReport is not { } r)
            {
                return null;
            }

            var invalid = r.InvalidatedRows.Count;
            return invalid == 0
                ? string.Create(CultureInfo.InvariantCulture, $"Applied to {r.AppliedCount} row(s).")
                : string.Create(CultureInfo.InvariantCulture, $"Applied to {r.AppliedCount} row(s) · {invalid} now invalid (see the red rows).");
        }
    }

    /// <summary>The failed rows from the last run — the subset the UI offers to retry (T-097 renders "Retry failed (N)").</summary>
    public IReadOnlyList<BulkTrimItemResult> LastFailedItems
    {
        get => _lastFailedItems;
        private set
        {
            if (SetProperty(ref _lastFailedItems, value))
            {
                OnPropertyChanged(nameof(FailedCount));
            }
        }
    }

    /// <summary>Count of failed rows from the last run (drives the retry relabel).</summary>
    public int FailedCount => _lastFailedItems.Count;

    /// <summary>Cross-session folder memory — exposed so the view's file-picker seeds its initial dir.</summary>
    public IAppSettings Settings => _settings;

    /// <summary>The shared thumbnail service (per-row hover preview, rendered by T-097).</summary>
    public IThumbnailService Thumbnails => _thumbnails;

    /// <summary>Run is enabled with ≥1 enabled+valid row, no run in flight, and every enabled row keyframes-ready.</summary>
    public bool CanRunBatch =>
        Items.Any(i => i.IsEnabled && i.IsValidCut)
        && !Operation.IsRunning
        && Items.Where(i => i.IsEnabled).All(i => i.KeyframesReady);

    /// <summary>
    /// T-134 — what Run will actually do, said BEFORE it is pressed. Null when every row will be cut (there
    /// is nothing to explain) or the list is empty.
    ///
    /// <para><b>Why this exists.</b> The information was already available — the button reads
    /// <c>Run bulk cut (N)</c> and every excluded row has carried an <see cref="BulkItemViewModel.ExclusionReason"/>
    /// since T-127 — and it was still not enough: a user imported a batch, set one cut, pressed Run, got one
    /// file, and had to ask why. A count that silently equals 1 beside a list of 12 is the state that
    /// produced that question.</para>
    ///
    /// <para>The reasons are taken VERBATIM from each row's <see cref="BulkItemViewModel.ExclusionReason"/>
    /// rather than restated here, so there is exactly one place the wording lives. Rows the user unticked
    /// deliberately carry no reason (T-127: unticking silences the explanation), so they are counted
    /// separately and phrased calmly — a deliberate choice is not a problem to warn about.</para>
    /// </summary>
    public string? RunScopeSummary
    {
        get
        {
            var total = Items.Count;
            if (total == 0)
            {
                return null;
            }

            var willCut = Items.Count(i => i.IsEnabled && i.IsValidCut);
            if (willCut == total)
            {
                return null; // everything runs — saying so would be noise
            }

            var parts = new List<string>();

            foreach (var group in Items
                .Where(i => i.ExclusionReason is not null)
                .GroupBy(i => i.ExclusionReason!, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()))
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{group.Count()} × {group.Key}"));
            }

            var unticked = Items.Count(i => !i.IsCheckedByUser);
            if (unticked > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{unticked} not ticked"));
            }

            var scanning = Items.Count(i => i.IsCheckedByUser && !i.KeyframesReady);
            if (scanning > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{scanning} still scanning"));
            }

            var head = string.Create(CultureInfo.InvariantCulture, $"Will cut {willCut} of {total}");
            return parts.Count == 0 ? head : head + " — " + string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// T-134 — whether the shortfall deserves visual weight. A row the user TICKED but the app is excluding
    /// is the surprising case; rows they unticked themselves are not, and alarming them about their own
    /// choice would teach them to ignore the line.
    /// </summary>
    public bool RunScopeIsWarning => Items.Any(i => i.IsExcludedDespiteBeingChecked);

    /// <summary>Count-aware primary-button label: <c>"Run bulk cut (N)"</c> over the enabled+valid rows.</summary>
    public string RunLabel =>
        string.Create(CultureInfo.InvariantCulture, $"Run bulk cut ({Items.Count(i => i.IsEnabled && i.IsValidCut)})");

    /// <summary>
    /// T-144 - the rows whose ORIGINAL can be binned right now. Every clause is a way this feature could
    /// destroy someone's footage, so it is evaluated FRESH on each read rather than remembered from the
    /// run: only a row the batch finished (RowState.Done); whose output exists and is non-empty NOW (the
    /// run may be minutes old and the output may have been moved since); whose output is not the original
    /// itself (under ReplaceOriginal the original already BECAME the output, so binning it destroys the
    /// only copy); and whose original is still there to bin.
    /// </summary>
    private IEnumerable<BulkItemViewModel> DeletableOriginals()
    {
        if (_originalDisposer is null || Operation.IsRunning)
        {
            yield break;
        }

        foreach (var row in Items)
        {
            if (row.RowState != RowState.Done)
            {
                continue;
            }

            var output = row.OutputPath;
            if (string.IsNullOrWhiteSpace(output))
            {
                continue;
            }

            if (SamePath(output, row.Path))
            {
                continue; // replace-originals: the output IS the original
            }

            if (!IsNonEmptyFile(output) || !FileThere(row.Path))
            {
                continue;
            }

            yield return row;
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNonEmptyFile(string path)
    {
        try
        {
            var fi = new System.IO.FileInfo(path);
            return fi.Exists && fi.Length > 0;
        }
        catch
        {
            return false; // unreadable => never treat its source as safe to delete
        }
    }

    private static bool FileThere(string path)
    {
        try
        {
            return System.IO.File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>T-144 - how many originals could be reclaimed.</summary>
    public int DeletableOriginalCount => DeletableOriginals().Count();

    private long DeletableOriginalBytes => DeletableOriginals().Sum(r => r.SizeBefore);

    /// <summary>T-144 - the delete-originals gate.</summary>
    public bool CanDeleteOriginals => DeletableOriginalCount > 0;

    /// <summary>Count-and-prize label, so the user sees what they get before pressing it.</summary>
    public string DeleteOriginalsLabel
    {
        get
        {
            var n = DeletableOriginalCount;
            return n == 0
                ? "✕ Delete originals"
                : string.Create(CultureInfo.InvariantCulture, $"✕ Delete originals ({n} · {FormatBytes(DeletableOriginalBytes)})");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d / 1024d / 1024d:0.#} GB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, bytes / 1024 / 1024)} MB");
    }

    /// <summary>
    /// Confirmation gate for <see cref="DeleteOriginals"/> - (count, bytes) =&gt; proceed. Defaults to
    /// false so a host that never wires it can never delete anything, mirroring ConfirmReplaceOriginals.
    /// </summary>
    public Func<int, long, bool> ConfirmDeleteOriginals { get; set; } = (_, _) => false;

    /// <summary>
    /// T-144 - send the eligible originals to the Recycle Bin. Per-row isolated: one file that cannot be
    /// binned never stops the rest, and the summary states binned vs refused.
    /// </summary>
    public void DeleteOriginals()
    {
        var rows = DeletableOriginals().ToList();
        if (rows.Count == 0 || _originalDisposer is null)
        {
            return;
        }

        if (!ConfirmDeleteOriginals(rows.Count, rows.Sum(r => r.SizeBefore)))
        {
            return;
        }

        // T-145: RELEASE OUR OWN HANDLE FIRST. The preview player holds the selected row's file open, so
        // without this the previewed original is refused by the disposer and the user asks to delete N
        // files and gets N-1. Stop() is NOT enough - it only halts playback; Unload closes the media
        // element and releases the handle. The replace-originals path already did exactly this for the
        // same reason (see RunBatchAsync); T-144 needed the same reasoning and did not get it.
        SelectedItem = null;   // or the next interaction re-opens a file we are about to bin
        Player.Unload();

        var binned = 0;
        var refusedPaths = new List<string>();

        foreach (var row in rows)
        {
            // Re-check at deletion time: the eligibility list was built BEFORE the unload, and the world
            // may have moved since the confirmation dialog was answered.
            if (!FileThere(row.Path) || !IsNonEmptyFile(row.OutputPath))
            {
                continue;
            }

            try
            {
                _originalDisposer.DisposeOriginalBackup(row.Path);

                // The disposer is best-effort BY CONTRACT, so verify rather than assume it worked.
                if (FileThere(row.Path))
                {
                    refusedPaths.Add(row.Path);
                    continue;
                }

                row.MarkOriginalDeleted();
                binned++;
            }
            catch
            {
                refusedPaths.Add(row.Path);
            }
        }

        // Name what was refused. "1 could not be removed" leaves the user hunting for which one.
        Operation.ResultSummary = refusedPaths.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Sent {binned} original(s) to the Recycle Bin")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Sent {binned} to the Recycle Bin. Still in use: {string.Join(", ", refusedPaths.Select(System.IO.Path.GetFileName))}");

        RaiseRunState();
    }

    /// <summary>Clear all is enabled with ≥1 row and no run in flight.</summary>
    public bool CanClear => Items.Count > 0 && !Operation.IsRunning;

    /// <summary>T-128 — select all / select none are enabled with ≥1 row and no run in flight.</summary>
    public bool CanChangeSelection => Items.Count > 0 && !Operation.IsRunning;

    /// <summary>
    /// The two "set at playhead" gestures (T-101) are enabled only when a row is selected AND the
    /// shared preview <see cref="Player"/> is ready (its duration is known — i.e. there is a real
    /// playhead position to capture). Mirrors <c>SplitViewModel.CanSetCutAtPlayhead</c>.
    /// </summary>
    public bool CanSetCutAtPlayhead => _selectedItem is not null && Player.IsReady;

    // ---- Commands ---------------------------------------------------------------------------

    /// <summary>Add videos (parameter = paths): dedup + probe + throttled background keyframe scan.</summary>
    public RelayCommand AddFilesCommand { get; }

    /// <summary>Remove a row (parameter = <see cref="BulkItemViewModel"/>); cancels its scan first.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Clear all rows (cancels every scan, resets the aggregate op) — guarded by <see cref="CanClear"/>.</summary>
    public RelayCommand ClearCommand { get; }

    /// <summary>T-144 - bin the originals of successfully trimmed rows.</summary>
    public RelayCommand DeleteOriginalsCommand { get; }

    /// <summary>Copy one row's cut points to every other enabled row (parameter = the source row).</summary>
    public RelayCommand ApplyToAllCommand { get; }

    /// <summary>T-128 — tick every row in one gesture. Mirrors the Split screen's Select all.</summary>
    public RelayCommand SelectAllItemsCommand { get; }

    /// <summary>T-128 — untick every row in one gesture. Mirrors the Split screen's Select none.</summary>
    public RelayCommand SelectNoItemsCommand { get; }

    /// <summary>Run the batch through the engine (guarded by <see cref="CanRunBatch"/>).</summary>
    public RelayCommand RunBatchCommand { get; }

    /// <summary>Set the selected row's intro-end to the preview playhead, snapped (guarded by <see cref="CanSetCutAtPlayhead"/>).</summary>
    public RelayCommand SetIntroAtPlayheadCommand { get; }

    /// <summary>Set the selected row's outro-start to the preview playhead — adds one if none, else moves it; snapped (guarded by <see cref="CanSetCutAtPlayhead"/>).</summary>
    public RelayCommand SetOutroAtPlayheadCommand { get; }

    /// <summary>Cancel the in-flight batch — delegates to the aggregate op's cancel.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Save the selected row's current cut as a named profile (parameter = the name). T-103.</summary>
    public RelayCommand SaveProfileCommand { get; }

    /// <summary>Apply the selected profile to the selected row only. T-103.</summary>
    public RelayCommand ApplyProfileToSelectedCommand { get; }

    /// <summary>Apply the selected profile to every user-checked row (one click, same-series batch). T-103.</summary>
    public RelayCommand ApplyProfileToAllCommand { get; }

    /// <summary>Delete the selected profile from settings + the bar. T-103.</summary>
    public RelayCommand DeleteProfileCommand { get; }

    /// <summary>T-147 - write all profiles + pictures to one portable file.</summary>
    public RelayCommand ExportProfilesCommand { get; }

    /// <summary>T-147 - restore profiles from such a file.</summary>
    public RelayCommand ImportProfilesCommand { get; }

    /// <summary>T-135 — capture the frame on screen as the selected profile's thumbnail.</summary>
    public RelayCommand SnapshotProfileThumbnailCommand { get; }

    // ---- Add / remove / clear ---------------------------------------------------------------

    /// <summary>
    /// Add one row per NEW path (deduped by normalized <see cref="System.IO.Path.GetFullPath"/> — never a
    /// second row per source, D-004 matrix #11): construct the row, best-effort probe → Duration/SizeBefore
    /// (probe-fail → LoadFailed, excluded), then fire the THROTTLED background keyframe scan. Never throws
    /// for a bad path.
    /// </summary>
    public async Task AddFilesAsync(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return;
        }

        var existing = new HashSet<string>(Items.Select(i => NormalizePath(i.Path)), StringComparer.OrdinalIgnoreCase);
        var added = new List<BulkItemViewModel>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var key = NormalizePath(path);
            if (!existing.Add(key))
            {
                continue; // dedup — already have a row for this source
            }

            // T-108: thread the SHARED thumbnail service + the dedicated bounded thumbnail gate into the row
            // so it grabs its cut-point frames through the same cache (no second frame-grab path), bounded.
            var item = new BulkItemViewModel(
                path,
                _probe,
                _scanGate,
                thumbnails: _thumbnails,
                thumbnailGate: _thumbnailGate,
                thumbnailDebounce: _thumbnailDebounce,
                thumbnailDelay: _thumbnailDelay)
            {
                SizeBefore = SafeFileSize(path),
            };
            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
            added.Add(item);
        }

        if (added.Count == 0)
        {
            return;
        }

        var lastAddedDir = SafeGetDirectory(added[^1].Path);
        if (!string.IsNullOrEmpty(lastAddedDir))
        {
            _settings.LastInputDir = lastAddedDir;
        }

        foreach (var item in added)
        {
            await PopulateAsync(item).ConfigureAwait(true);
        }

        // T-100: auto-select the first row for convenience — fires on the first add (nothing selected
        // yet), and again after a Clear/empty selection, opening it in the shared preview player.
        if (SelectedItem is null && Items.Count > 0)
        {
            SelectedItem = Items[0];
        }

        RaiseRunState();
    }

    private async Task PopulateAsync(BulkItemViewModel item)
    {
        ProbeResult probe;
        try
        {
            probe = await _probe.ProbeAsync(item.Path).ConfigureAwait(true);
        }
        catch
        {
            item.MarkLoadFailed();
            return;
        }

        if (probe is ProbeResult.ProbeSucceeded ok)
        {
            item.Duration = ok.Info.Duration;
            _ = item.StartKeyframeScanAsync(); // throttled background scan (§3)
        }
        else
        {
            item.MarkLoadFailed();
        }
    }

    private void Remove(BulkItemViewModel? item)
    {
        if (item is null || !Items.Contains(item))
        {
            return;
        }

        // T-100: capture whether we're removing the selected row (and its slot) BEFORE it leaves the
        // list, so we can re-point the shared player at a neighbour rather than leave SelectedItem
        // dangling at a just-removed file.
        var removingSelected = ReferenceEquals(_selectedItem, item);
        var index = Items.IndexOf(item);

        item.CancelScan();
        item.PropertyChanged -= OnItemChanged;
        Items.Remove(item);

        if (removingSelected)
        {
            // Neighbour at the same slot (or the new last row), else null → the setter unloads the player.
            SelectedItem = Items.Count > 0 ? Items[Math.Min(index, Items.Count - 1)] : null;
        }

        RaiseRunState();
    }

    /// <summary>Clear all rows: cancel every scan, drop them, reset the aggregate op + batch state.</summary>
    public void Clear()
    {
        if (!CanClear)
        {
            return;
        }

        // T-100: drop the selection (setter unloads the shared player), then blank the player
        // unconditionally so a Clear leaves the preview empty even if nothing was selected.
        SelectedItem = null;
        Player.Unload();

        foreach (var item in Items)
        {
            item.CancelScan();
            item.PropertyChanged -= OnItemChanged;
        }

        Items.Clear();
        Operation.Reset();
        BatchState = BulkBatchState.Idle;
        ApplyToAllReport = null;
        LastFailedItems = Array.Empty<BulkTrimItemResult>();
        RaiseRunState();
    }

    /// <summary>
    /// Route a selection change to the ONE shared <see cref="Player"/> (T-100), now DEBOUNCED (T-115):
    /// <list type="bullet">
    /// <item>Any change first <b>cancels the pending debounced open</b> (latest-wins) so a superseded
    /// row never opens — arrowing through N rows collapses to one open of the settled row.</item>
    /// <item>A <b>null</b> selection (list cleared / nothing selected) unloads IMMEDIATELY, having just
    /// cancelled the pending open above — so a stray open can never land after the unload.</item>
    /// <item>A <b>non-null</b> selection schedules <see cref="OpenAfterDebounceAsync"/>: settle for the
    /// debounce window, then — if not superseded — open. The open itself still goes through
    /// <see cref="PlayerViewModel.Open"/> → <see cref="FfmeMediaPlayer"/>'s <see cref="MediaReopenGuard"/>
    /// (T-080), the last-line native-AV safety the debounce sits in front of.</item>
    /// </list>
    /// </summary>
    private void OpenOrUnloadSelected(BulkItemViewModel? item)
    {
        // Latest-wins: a newer selection (or a clear) supersedes any still-pending debounced open.
        CancelPendingOpen();

        if (item is null)
        {
            Player.Unload();
            return;
        }

        var cts = new CancellationTokenSource();
        _openCts = cts;
        _ = OpenAfterDebounceAsync(item.Path, cts);
    }

    /// <summary>
    /// Cancel + dispose the pending debounced open's CTS (if any) so a superseded / cleared / run-preempted
    /// selection never reaches <see cref="PlayerViewModel.Open"/>. Idempotent; safe to call when none is
    /// pending. Mirrors the cancel-prior discipline of the T-108 grabber / <see cref="ThumbnailPreviewViewModel"/>.
    /// </summary>
    private void CancelPendingOpen()
    {
        var cts = _openCts;
        _openCts = null;
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

    /// <summary>
    /// Debounce → open, for one settled selection (T-115). Waits the debounce window off the immediate
    /// call (a newer selection / clear / run cancels the wait via the shared CTS), then — only if this
    /// request is still the current one — opens the file in the shared player.
    ///
    /// <para>Unlike the T-108 frame grabber (which awaits with <c>ConfigureAwait(false)</c> to run ffmpeg
    /// OFF the UI thread and marshals only the tiny result back), the deferred action here IS a UI-thread
    /// operation — <see cref="PlayerViewModel.Open"/> drives the FFME element + raises bound state — so the
    /// whole method stays on the captured context (<c>ConfigureAwait(true)</c>): in the app that resumes on
    /// the WPF dispatcher; under an immediate test delay the already-complete await continues inline, so the
    /// open is observable synchronously. WPF-free: pure <see cref="Task"/> / <see cref="CancellationToken"/>
    /// / delay-func, no PresentationFramework types.</para>
    /// </summary>
    private async Task OpenAfterDebounceAsync(string path, CancellationTokenSource cts)
    {
        try
        {
            // Settle before the heavy FFME open. A newer selection cancels this wait (latest-wins).
            await _selectionOpenDelay(_selectionOpenDebounce, cts.Token).ConfigureAwait(true);

            if (cts.Token.IsCancellationRequested)
            {
                return; // superseded after the wait resolved but before we opened
            }

            Player.Open(path);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection / a clear / a run — drop silently (no open lands).
        }
        finally
        {
            if (ReferenceEquals(_openCts, cts))
            {
                _openCts = null;
            }

            cts.Dispose();
        }
    }

    // ---- Set-at-playhead (T-101) ------------------------------------------------------------

    /// <summary>
    /// Set the selected row's intro-end to the shared preview player's current playhead
    /// (<see cref="PlayerViewModel.Position"/>). Writing <see cref="CutMarkerViewModel.Requested"/>
    /// re-snaps to the row's keyframes automatically — the same path the per-row scrub handles and the
    /// editable IN field use — so the row's scrub bar updates live. No-op without a selection / a ready
    /// player (also guarded by <see cref="CanSetCutAtPlayhead"/> on the command).
    /// </summary>
    public void SetIntroAtPlayhead()
    {
        if (!CanSetCutAtPlayhead)
        {
            return;
        }

        _selectedItem!.IntroEnd.Requested = Player.Position;
        FanOutToCheckedRows();
    }

    /// <summary>
    /// Set the selected row's outro-start to the shared preview player's current playhead: add the
    /// outro handle if the row has none (<see cref="BulkItemViewModel.AddOutro"/>), else move the
    /// existing handle by writing <see cref="CutMarkerViewModel.Requested"/> (re-snaps). No-op without
    /// a selection / a ready player (also guarded by <see cref="CanSetCutAtPlayhead"/> on the command).
    /// </summary>
    public void SetOutroAtPlayhead()
    {
        if (!CanSetCutAtPlayhead)
        {
            return;
        }

        var row = _selectedItem!;
        if (row.HasOutro)
        {
            row.OutroStart!.Requested = Player.Position;
        }
        else
        {
            row.AddOutro(Player.Position);
        }

        FanOutToCheckedRows();
    }

    /// <summary>
    /// T-133 — when <see cref="ApplyCutToAllRows"/> is on, copy the row just set onto every other CHECKED
    /// row. Delegates to <see cref="ApplyToAll"/> so there is exactly one implementation of the copy: it
    /// re-snaps against each target's own keyframes, measures the outro from the END of each file so
    /// uneven lengths align, mirrors a cleared outro, and reports rows the copy invalidated instead of
    /// dropping them silently. No-op when the toggle is off.
    /// </summary>
    private void FanOutToCheckedRows()
    {
        if (!_applyCutToAllRows)
        {
            return;
        }

        ApplyToAll(_selectedItem);
    }

    /// <summary>
    /// The shared player's readiness gates both set-at-playhead gestures — refresh their guards when
    /// it flips (mirrors <c>SplitViewModel.OnPlayerChanged</c>). Position ticks do not affect the
    /// guards, so they are ignored here.
    /// </summary>
    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.IsReady) or nameof(PlayerViewModel.Duration))
        {
            RaisePlayheadCommandStates();
        }
    }

    private void RaisePlayheadCommandStates()
    {
        OnPropertyChanged(nameof(CanSetCutAtPlayhead));
        SetIntroAtPlayheadCommand.RaiseCanExecuteChanged();
        SetOutroAtPlayheadCommand.RaiseCanExecuteChanged();

        // T-135 depends on the same selection + player-readiness signals.
        OnPropertyChanged(nameof(CanSnapshotProfileThumbnail));
        OnPropertyChanged(nameof(SnapshotUnavailableReason));
        SnapshotProfileThumbnailCommand.RaiseCanExecuteChanged();
        ExportProfilesCommand.RaiseCanExecuteChanged();
    }

    // ---- Apply-to-all (§2.3) ----------------------------------------------------------------

    /// <summary>
    /// T-128 — write the user's checkbox INTENT across every row in one pass. Deliberately writes
    /// <see cref="BulkItemViewModel.IsCheckedByUser"/>, never the computed <c>IsEnabled</c>: intent is what
    /// the user is expressing, and a row the app currently excludes (no cut set yet) keeps that intent so it
    /// joins the batch the moment it becomes eligible. Pure VM state — no probe, no thumbnail grab, no scan.
    /// </summary>
    public void SetAllItemsChecked(bool checkedByUser)
    {
        // Review finding #3: each row's setter raises BOTH IsCheckedByUser and IsEnabled, and OnItemChanged
        // fans EITHER out to RaiseRunState() — whose getters are themselves O(N). Without this guard a
        // single "Select all" on a large batch costs ~2N whole-list re-projections on the UI thread, so the
        // "refresh once" this method claimed was a comment, not a behaviour. Suspend the per-row fan-out for
        // the duration of the write and refresh exactly once at the end.
        _suspendRunStateRefresh = true;
        try
        {
            foreach (var item in Items)
            {
                item.IsCheckedByUser = checkedByUser;
            }
        }
        finally
        {
            _suspendRunStateRefresh = false;
        }

        RaiseRunState();
    }

    /// <summary>
    /// Copy <paramref name="source"/>'s <b>requested</b> cut points to every other checked, keyframes-ready
    /// row: the intro-end absolute (time-from-start), the outro <b>from END</b> (<c>Duration − outroStart</c>)
    /// so uneven-length episodes align (D-004 open-decision 1). Each target re-snaps (setting
    /// <c>Requested</c>) and re-validates against ITS OWN keyframes/duration; rows the copy invalidated are
    /// <b>reported</b> in <see cref="ApplyToAllReport"/> — never silently dropped (matrix #17). No-op if the
    /// source itself is not ready.
    /// </summary>
    public ApplyToAllReport? ApplyToAll(BulkItemViewModel? source)
    {
        if (source is null || !source.KeyframesReady || source.Duration is not { } sourceDuration)
        {
            return null;
        }

        var introReq = source.IntroEnd.Requested;
        TimeSpan? tail = source.HasOutro ? sourceDuration - source.OutroStart!.Requested : (TimeSpan?)null;

        var applied = 0;
        var invalidated = new List<BulkItemViewModel>();

        foreach (var target in Items)
        {
            if (ReferenceEquals(target, source) || !target.IsCheckedByUser || !target.KeyframesReady || target.Duration is not { } targetDuration)
            {
                continue;
            }

            target.IntroEnd.Requested = introReq; // re-snaps against the target's own keyframes

            if (tail is { } t)
            {
                var outroReq = targetDuration - t; // FROM END, so uneven lengths align
                if (target.HasOutro)
                {
                    target.OutroStart!.Requested = outroReq;
                }
                else
                {
                    target.AddOutro(outroReq);
                }
            }
            else
            {
                target.ClearOutro(); // mirror the source's no-outro shape
            }

            applied++;

            if (!target.IsValidCut)
            {
                invalidated.Add(target);
            }
        }

        var report = new ApplyToAllReport(applied, invalidated);
        ApplyToAllReport = report;
        RaiseRunState();
        return report;
    }

    // ---- Cut profiles (T-103 — thin glue over the T-102 applier/persistence) ----------------

    /// <summary>
    /// Save the SELECTED row's current cut as a profile named <paramref name="name"/>:
    /// <see cref="CutProfileApplier.BuildProfileFromRow"/> → <see cref="IAppSettings.SaveProfile"/>
    /// (upsert-by-name, case-insensitive) → refresh <see cref="Profiles"/> and select the saved one.
    /// No-op without a selected row or a blank/whitespace name (the name is trimmed).
    /// </summary>
    public void SaveProfile(string? name)
    {
        if (_selectedItem is not { } row)
        {
            return;
        }

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return; // naming UX: non-empty required (upsert on a duplicate name — T-102 already upserts)
        }

        var profile = CutProfileApplier.BuildProfileFromRow(trimmed, row);
        _settings.SaveProfile(profile);
        RefreshProfiles();
        SelectedProfile = Profiles.FirstOrDefault(
            p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Profile thumbnails (T-107 — thin glue over the T-106 ProfileThumbnailStore) --------

    /// <summary>
    /// Save the selected row's cut as <paramref name="name"/> (via <see cref="SaveProfile"/>) AND, best-effort
    /// and OFF the save path, capture the row's intro-end frame as the profile's <b>default thumbnail</b>
    /// (T-107). The profile is persisted + shown FIRST — the save is NEVER blocked on the grab — then a
    /// background <see cref="IThumbnailService.GetThumbnailAsync"/> at the row's snapped intro-end is copied
    /// into the <see cref="ProfileThumbnailStore"/> and its stored path folded onto the profile. A null /
    /// failed grab (or a store failure) simply leaves the profile with no thumbnail (the picker shows a
    /// placeholder). This is the async step the <see cref="SaveProfileCommand"/> drives (fire-and-forget);
    /// exposed awaitable for tests. Never throws.
    /// </summary>
    public async Task SaveProfileWithAutoThumbnailAsync(string? name, CancellationToken ct = default)
    {
        // 1. Persist + refresh + select immediately — a slow/failed grab must never delay the save.
        SaveProfile(name);

        if (_selectedItem is not { } row)
        {
            return;
        }

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || FindPersistedProfile(trimmed) is null)
        {
            return; // blank name / nothing was saved → no thumbnail to attach
        }

        // 2. Best-effort intro-end frame grab (the service seeks + shells ffmpeg off the UI thread and
        //    returns null on any failure — but guard anyway so nothing ever escapes onto the save path).
        string? framePath;
        try
        {
            framePath = await _thumbnails
                .GetThumbnailAsync(row.Path, row.IntroEnd.Snapped, ProfileThumbnailWidth, ct)
                .ConfigureAwait(true);
        }
        catch
        {
            return; // a failed grab keeps the null thumbnail (placeholder shows)
        }

        if (string.IsNullOrEmpty(framePath))
        {
            return;
        }

        // 3. Copy the frame into the store + fold its stored path onto the profile (re-persist + refresh).
        AttachThumbnail(trimmed, framePath);
    }

    /// <summary>
    /// Override <paramref name="profile"/>'s thumbnail with a user-chosen <paramref name="imagePath"/> (T-107):
    /// copies the image into the <see cref="ProfileThumbnailStore"/> and folds the stored path onto the
    /// profile (re-persist + refresh + reselect). Returns <c>true</c> only when the thumbnail was actually
    /// attached.
    /// <para><b>T-129 — this is a DELIBERATE user gesture, so it REPORTS.</b> "I picked this file" and
    /// getting neither a picture nor a word is indistinguishable from a broken button, so every failure —
    /// no profile selected, nothing chosen, a profile that was never saved, an unreadable/missing image, a
    /// store copy failure — is surfaced on the screen's existing error block via
    /// <see cref="OperationViewModel.ReportFailure"/>. What does NOT change: the profile's current thumbnail
    /// is still left untouched on failure, and this still never throws. A subsequent successful upload
    /// retracts the message it reported (and only that message — a batch failure sharing the surface
    /// survives). The AUTO capture on save (<see cref="SaveProfileWithAutoThumbnailAsync"/>) is deliberately
    /// NOT changed: it stays best-effort and silent.</para>
    /// </summary>
    /// <summary>
    /// T-135 — make the frame currently on screen the selected profile's thumbnail.
    ///
    /// <para>A THIRD entry point onto the same store-and-attach path, not new machinery: it grabs at
    /// <see cref="PlayerViewModel.Position"/> through the same <see cref="IThumbnailService"/> and at the
    /// same <see cref="ProfileThumbnailWidth"/> the auto path uses, then hands the frame to
    /// <see cref="TryAttachThumbnail"/>. Like the upload gesture and unlike the silent auto-capture, this
    /// is something the user deliberately pressed, so a failure is REPORTED (SPEC-007 I66/I69).</para>
    /// </summary>
    /// <returns>True when the frame became the profile's thumbnail.</returns>
    public async Task<bool> SnapshotProfileThumbnailAsync(CancellationToken ct = default)
    {
        if (_selectedProfile is not { } profile)
        {
            ReportThumbnailUploadFailure(ThumbnailAttachOutcome.NoProfile, null, null, string.Empty);
            return false;
        }

        if (_selectedItem is not { } row || !Player.IsReady)
        {
            // No video on screen means there is no frame to capture — say that rather than grabbing
            // whatever the last row happened to be.
            ReportThumbnailUploadFailure(ThumbnailAttachOutcome.NoImageChosen, profile.Name, null, string.Empty);
            return false;
        }

        string? framePath;
        try
        {
            framePath = await _thumbnails
                .GetThumbnailAsync(row.Path, Player.Position, ProfileThumbnailWidth, ct)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportThumbnailUploadFailure(ThumbnailAttachOutcome.ImageUnreadable, profile.Name, row.Path, ex.Message);
            return false;
        }

        if (string.IsNullOrEmpty(framePath))
        {
            ReportThumbnailUploadFailure(ThumbnailAttachOutcome.ImageUnreadable, profile.Name, row.Path, string.Empty);
            return false;
        }

        var outcome = TryAttachThumbnail(profile.Name, framePath, out var detail);
        if (outcome == ThumbnailAttachOutcome.Attached)
        {
            ClearThumbnailUploadError();
            return true;
        }

        ReportThumbnailUploadFailure(outcome, profile.Name, framePath, detail);
        return false;
    }

    /// <summary>
    /// T-147 - export every profile, pictures included, to one file the user chooses. The view supplies
    /// the destination (a save dialog); null/blank cancels. Reports through the same surface the other
    /// deliberate profile gestures use.
    /// </summary>
    public Func<string?>? ChooseProfileExportPath { get; set; }

    /// <summary>T-147 - the view supplies a backup file to import; null/blank cancels.</summary>
    public Func<string?>? ChooseProfileImportPath { get; set; }

    /// <summary>
    /// T-147 - asked when an import would overwrite profiles that already exist. Returning false keeps
    /// the existing ones and imports only the new. Defaults to KEEPING them: an unwired host must never
    /// silently overwrite.
    /// </summary>
    public Func<int, bool> ConfirmProfileOverwrite { get; set; } = _ => false;

    /// <summary>T-147 - write all profiles + their pictures to one portable file.</summary>
    public void ExportProfiles()
    {
        if (ChooseProfileExportPath?.Invoke() is not { Length: > 0 } destination)
        {
            return;
        }

        try
        {
            var (count, images) = ProfileBackup.Export(_settings.CutProfiles, destination);
            Operation.ResultSummary = string.Create(
                CultureInfo.InvariantCulture,
                $"Exported {count} profile(s), {images} with pictures");
            ClearThumbnailUploadError();
        }
        catch (Exception ex)
        {
            ReportProfileBackupFailure("Those profiles could not be exported.", ex.Message);
        }
    }

    /// <summary>
    /// T-147 - restore profiles from a backup. Plans first, so a corrupt file changes NOTHING and a name
    /// collision is the user's decision rather than a silent overwrite.
    /// </summary>
    public void ImportProfiles()
    {
        if (ChooseProfileImportPath?.Invoke() is not { Length: > 0 } source)
        {
            return;
        }

        var plan = ProfileBackup.Plan(source, _settings.CutProfiles);
        if (plan.Failed)
        {
            ReportProfileBackupFailure("That backup could not be imported.", plan.Error ?? string.Empty);
            return;
        }

        if (plan.Total == 0)
        {
            Operation.ResultSummary = "That backup contained no profiles";
            return;
        }

        var overwrite = plan.Colliding.Count > 0 && ConfirmProfileOverwrite(plan.Colliding.Count);

        var (written, restored) = ProfileBackup.Apply(plan, _settings, _thumbnailStore, overwrite);
        RefreshProfiles();

        var kept = plan.Colliding.Count > 0 && !overwrite
            ? string.Create(CultureInfo.InvariantCulture, $", kept {plan.Colliding.Count} existing")
            : string.Empty;

        Operation.ResultSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"Imported {written} profile(s), {restored} with pictures{kept}");
        ClearThumbnailUploadError();
    }

    private void ReportProfileBackupFailure(string message, string detail)
    {
        // (category, message, rawTail, hint) - the detail is the COPYABLE body, the hint is the advice.
        Operation.ReportFailure(new UserFacingError(
            ErrorCategory.InvalidArgument,
            message,
            detail,
            "Check the file is a profile backup written by this app, and that you can read and write it."));
    }

    public bool UploadThumbnail(CutProfile? profile, string? imagePath)
    {
        ThumbnailAttachOutcome outcome;
        var detail = string.Empty;

        if (profile is null)
        {
            outcome = ThumbnailAttachOutcome.NoProfile;
        }
        else if (string.IsNullOrWhiteSpace(imagePath))
        {
            outcome = ThumbnailAttachOutcome.NoImageChosen;
        }
        else
        {
            outcome = TryAttachThumbnail(profile.Name, imagePath, out detail);
        }

        if (outcome == ThumbnailAttachOutcome.Attached)
        {
            ClearThumbnailUploadError();
            return true;
        }

        ReportThumbnailUploadFailure(outcome, profile?.Name, imagePath, detail);
        return false;
    }

    /// <summary>
    /// Clear <paramref name="profile"/>'s thumbnail (T-107): best-effort delete the stored file(s) — by name
    /// (covers any stored extension) and by the exact recorded path — then null the profile's
    /// <see cref="CutProfile.ThumbnailPath"/> (re-persist + refresh + reselect), so the picker reverts to the
    /// placeholder. No-op when the profile is unset or has no persisted entry. Never throws.
    /// </summary>
    public void ClearThumbnail(CutProfile? profile)
    {
        if (profile is null || FindPersistedProfile(profile.Name) is not { } existing)
        {
            return;
        }

        _thumbnailStore.Delete(existing.Name); // best-effort (missing/locked file swallowed)
        if (existing.ThumbnailPath is { } path)
        {
            _thumbnailStore.DeleteByPath(path);
        }

        _settings.SaveProfile(existing with { ThumbnailPath = null });
        RefreshProfiles();
        SelectedProfile = FindBarProfile(existing.Name);
    }

    /// <summary>
    /// The SILENT store-and-attach wrapper the auto-default (<see cref="SaveProfileWithAutoThumbnailAsync"/>)
    /// uses: attempt the attach and discard the outcome. Best-effort by design — an un-saved profile or a
    /// store failure (blank/missing source, locked target) leaves the profile unchanged and says nothing,
    /// because the thumbnail is a side effect of "Save" and must never interrupt the save (T-107 / T-129).
    /// </summary>
    private void AttachThumbnail(string profileName, string imagePath)
        => TryAttachThumbnail(profileName, imagePath, out _);

    /// <summary>
    /// Shared store-and-attach step for the auto-default and upload paths: copy <paramref name="imagePath"/>
    /// into the store under the persisted profile's name, fold the returned stored path onto the profile,
    /// then re-persist + refresh + reselect. Never throws — it CLASSIFIES instead, so the deliberate
    /// upload gesture can report what went wrong (T-129) while the auto path keeps ignoring it.
    /// <para>On any failure it returns before writing anything: no profile upsert, no
    /// <see cref="RefreshProfiles"/> re-projection, and no file I/O beyond the single store call that
    /// refused — which is what leaves the profile's current thumbnail exactly as it was.</para>
    /// </summary>
    /// <param name="detail">The refusing exception's message (empty on success) — the copyable detail line.</param>
    private ThumbnailAttachOutcome TryAttachThumbnail(string profileName, string imagePath, out string detail)
    {
        detail = string.Empty;

        if (FindPersistedProfile(profileName) is not { } existing)
        {
            return ThumbnailAttachOutcome.ProfileNotSaved; // the profile must be saved before a thumbnail can hang off it
        }

        string storedPath;
        try
        {
            storedPath = _thumbnailStore.Save(existing.Name, imagePath);
        }
        catch (Exception ex)
        {
            detail = ex.Message;

            // A blank/missing source is the PICK being wrong; anything else (locked target, unwritable
            // root, I/O error) is the STORE failing to take the copy — two different things to tell a user.
            return ex is FileNotFoundException or DirectoryNotFoundException or ArgumentException
                ? ThumbnailAttachOutcome.ImageUnreadable
                : ThumbnailAttachOutcome.StoreFailed;
        }

        _settings.SaveProfile(existing with { ThumbnailPath = storedPath });
        RefreshProfiles();
        SelectedProfile = FindBarProfile(existing.Name);
        return ThumbnailAttachOutcome.Attached;
    }

    /// <summary>
    /// T-129: turn a failed <see cref="UploadThumbnail"/> into a message on the screen's EXISTING error
    /// surface (<see cref="OperationViewModel.Error"/> — headline + hint + Copy details), the same block
    /// a failed batch uses. Each outcome gets its own headline and the one action that fixes it; the chosen
    /// path plus the refusing exception's message become the copyable detail.
    /// </summary>
    private void ReportThumbnailUploadFailure(
        ThumbnailAttachOutcome outcome,
        string? profileName,
        string? imagePath,
        string detail)
    {
        var (category, message, hint) = outcome switch
        {
            ThumbnailAttachOutcome.NoProfile => (
                ErrorCategory.InvalidArgument,
                "No cut profile is selected, so there is nothing to give a thumbnail to.",
                "Pick a profile in the profile bar first, then choose an image."),
            ThumbnailAttachOutcome.NoImageChosen => (
                ErrorCategory.InvalidArgument,
                "No image was chosen, so the profile thumbnail was not changed.",
                "Choose a PNG or JPG (or another image file) and try again."),
            ThumbnailAttachOutcome.ProfileNotSaved => (
                ErrorCategory.InvalidArgument,
                string.Create(CultureInfo.InvariantCulture, $"'{profileName}' is not a saved profile yet, so its thumbnail could not be stored."),
                "Save the profile first (Save current as…), then set its thumbnail."),
            ThumbnailAttachOutcome.ImageUnreadable => (
                ErrorCategory.CorruptInput,
                "That image could not be read, so the profile thumbnail was not changed.",
                "The file may have been moved, renamed or deleted. Choose another image."),
            _ => (
                ErrorCategory.PermissionDenied,
                "That image could not be stored as the profile's thumbnail.",
                "The file may be open in another program, or the thumbnails folder may not be writable. Try another image."),
        };

        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            parts.Add(imagePath!);
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            parts.Add(detail);
        }

        var error = new UserFacingError(category, message, string.Join(Environment.NewLine, parts), hint);
        _thumbnailUploadError = error;
        Operation.ReportFailure(error);
    }

    /// <summary>
    /// T-129: retract the upload failure THIS VM reported, once a later upload succeeds. Deliberately
    /// reference-scoped — the same surface also carries batch failures (e.g. a Blocked disk pre-flight),
    /// and a successful thumbnail upload must never erase one of those.
    /// </summary>
    private void ClearThumbnailUploadError()
    {
        if (_thumbnailUploadError is null)
        {
            return;
        }

        if (ReferenceEquals(Operation.Error, _thumbnailUploadError))
        {
            Operation.ReportFailure(null);
        }

        _thumbnailUploadError = null;
    }

    /// <summary>The persisted profile whose name matches (case-insensitive), or null.</summary>
    private CutProfile? FindPersistedProfile(string name) => _settings.CutProfiles
        .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The bar's projected profile whose name matches (case-insensitive), or null — used to re-point the selection at the refreshed instance.</summary>
    private CutProfile? FindBarProfile(string name) => Profiles
        .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Apply <see cref="SelectedProfile"/> to the selected row only (<see cref="CutProfileApplier.ApplyProfile"/>),
    /// surfacing the returned <see cref="ApplyToAllReport"/> the same way apply-to-all does. No-op without both.
    /// </summary>
    public ApplyToAllReport? ApplyProfileToSelected()
    {
        if (_selectedProfile is not { } profile || _selectedItem is not { } row)
        {
            return null;
        }

        var report = CutProfileApplier.ApplyProfile(profile, new[] { row });
        ApplyToAllReport = report;
        RaiseRunState();
        return report;
    }

    /// <summary>
    /// Apply <see cref="SelectedProfile"/> to every user-checked row (mirrors the apply-to-all targeting —
    /// the raw <c>IsCheckedByUser</c> intent, so a profile can also re-validate a currently-invalid checked
    /// row). Each target re-snaps + re-validates in the applier; invalidated rows are REPORTED through the
    /// shared <see cref="ApplyToAllReport"/> (and keep their own red per-row chip). No-op without a profile.
    /// </summary>
    public ApplyToAllReport? ApplyProfileToAll()
    {
        if (_selectedProfile is not { } profile)
        {
            return null;
        }

        var targets = Items.Where(i => i.IsCheckedByUser).ToList();
        var report = CutProfileApplier.ApplyProfile(profile, targets);
        ApplyToAllReport = report;
        RaiseRunState();
        return report;
    }

    /// <summary>
    /// Delete <see cref="SelectedProfile"/> from settings (<see cref="IAppSettings.DeleteProfile"/>) and the
    /// bar, then refresh + re-point the selection at the first remaining profile (or none). No-op if unset.
    /// </summary>
    public void DeleteSelectedProfile()
    {
        if (_selectedProfile is not { } profile)
        {
            return;
        }

        _settings.DeleteProfile(profile.Name);
        RefreshProfiles();
        SelectedProfile = Profiles.FirstOrDefault();
    }

    /// <summary>Re-project <see cref="IAppSettings.CutProfiles"/> into <see cref="Profiles"/> + re-gate the profile commands.</summary>
    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _settings.CutProfiles)
        {
            Profiles.Add(profile);
        }

        OnPropertyChanged(nameof(HasProfiles));
        RaiseProfileCommandStates();
    }

    private void RaiseProfileCommandStates()
    {
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(CanSaveProfile));
        OnPropertyChanged(nameof(CanApplyProfileToSelected));
        OnPropertyChanged(nameof(CanApplyProfileToAll));

        // T-111: re-raise EACH profile command explicitly rather than leaning on the "global requery"
        // side effect of a single SaveProfileCommand.RaiseCanExecuteChanged(). Since RelayCommand now
        // raises its OWN CanExecuteChanged deterministically (not only via CommandManager), the Apply→all /
        // Apply→selected / Delete buttons must be re-raised directly so a profile-selection change (e.g.
        // "no profile → disabled", then "profile picked → enabled") notifies their bound buttons every time
        // — the root cause of "Apply → all works once then its enabled-state goes stale."
        SaveProfileCommand.RaiseCanExecuteChanged();
        ApplyProfileToSelectedCommand.RaiseCanExecuteChanged();
        ApplyProfileToAllCommand.RaiseCanExecuteChanged();
        DeleteProfileCommand.RaiseCanExecuteChanged();

        // T-135: the snapshot gate also depends on WHICH profile is selected.
        OnPropertyChanged(nameof(CanSnapshotProfileThumbnail));
        OnPropertyChanged(nameof(SnapshotUnavailableReason));
        SnapshotProfileThumbnailCommand.RaiseCanExecuteChanged();
    }

    // ---- Run batch (§4 — DELEGATES to T-095) ------------------------------------------------

    /// <summary>
    /// Build the runnable rows' batch inputs (input order) and <b>await</b>
    /// <see cref="IBulkTrimEngine.RunAsync"/> through the aggregate op, fanning per-item + weighted-monotonic
    /// overall progress and the returned ledger back onto the rows. Contains NO batch loop / collision /
    /// disk-preflight / cancel-sweep — all inherited from T-095.
    /// </summary>
    public async Task RunBatchAsync()
    {
        if (!CanRunBatch)
        {
            return;
        }

        // T-100: stop the preview decode before the batch trims — don't waste a decoder/CPU on the
        // shared player while ffmpeg is doing the real work. The file stays selected; it re-plays on demand.
        // T-115: also cancel any still-pending debounced open so a select-then-immediately-run gesture can't
        // let a stale open fire AFTER this Stop (stop-on-run wins over an in-flight preview open).
        CancelPendingOpen();

        // T-123: replacing originals is destructive - require an explicit, counted confirmation before
        // anything runs. Declining leaves the batch entirely untouched (zero engine calls).
        if (_replaceOriginal)
        {
            var atRisk = Items.Count(i => i.IsEnabled && i.IsValidCut);
            if (!ConfirmReplaceOriginals(atRisk))
            {
                return;
            }
        }

        // T-100/T-115: stop the preview decode before the batch trims.
        // T-122: under ReplaceOriginal a Stop is NOT enough - Stop only halts playback, while Unload is
        // what closes the media element and RELEASES the file handle. A still-open handle on the selected
        // row would make replacing that very file fail, so unload it outright in this mode.
        if (_replaceOriginal)
        {
            Player.Unload();
        }
        else
        {
            Player.Stop();
        }

        BatchState = BulkBatchState.Preparing;
        ApplyToAllReport = null;
        LastFailedItems = Array.Empty<BulkTrimItemResult>();

        var rows = Items.Where(i => i.IsEnabled && i.IsValidCut).ToList();
        var items = rows.Select(r => r.BuildBulkTrimItem()).ToList();
        var weights = rows.Select(RowWeight).ToList();

        foreach (var row in rows)
        {
            row.MarkQueued();
        }

        Operation.SeedEstimatedDuration(TimeSpan.FromSeconds(rows.Sum(r => r.KeptDuration!.Value.TotalSeconds)));

        lock (_progressLock)
        {
            _lastOverall = 0d;
        }

        var options = new BulkTrimOptions(
            Overwrite ? CollisionPolicy.Overwrite : CollisionPolicy,
            _replaceOriginal ? OutputMode.ReplaceOriginal : OutputMode.NewFile,
            _exactCut ? CutPrecision.Exact : CutPrecision.Lossless);
        BatchResult? batch = null;

        await Operation.RunWithResultAsync(
            work: async (overallProgress, ct) =>
            {
                BatchState = BulkBatchState.Running;
                var engineProgress = new Progress<BulkTrimProgress>(p => OnBatchProgress(p, weights, rows, overallProgress));

                // DELEGATION: the whole batch is the engine's job — the VM never calls ISplitEngine.SplitAsync.
                batch = await _bulkTrimEngine.RunAsync(items, options, engineProgress, ct).ConfigureAwait(true);

                ApplyLedger(batch, rows);

                // Re-throw so the aggregate op lands in Cancelled AFTER the ledger set per-row states.
                if (batch.Outcome == BatchOutcome.Cancelled)
                {
                    ct.ThrowIfCancellationRequested();
                }

                return batch;
            },
            failureSelector: b => b!.Outcome == BatchOutcome.Blocked
                ? new UserFacingError(
                    ErrorCategory.DiskFull,
                    "Not enough space to trim these videos.",
                    string.Empty,
                    "Free up disk space and try again.")
                : null, // Completed / CompletedWithFailures are NOT op-level failures
            runningStatus: "Trimming…").ConfigureAwait(true);

        BatchState = MapOutcome(batch);
        RaiseRunState();
    }

    /// <summary>
    /// Progress fan-out: forward the current row's fraction (+ Running) and report the VM-computed,
    /// kept-duration-weighted, monotonic-clamped overall bar (D-004 "monotonic overall" risk).
    /// </summary>
    private void OnBatchProgress(
        BulkTrimProgress p,
        IReadOnlyList<double> weights,
        IReadOnlyList<BulkItemViewModel> rows,
        IProgress<double> overallProgress)
    {
        if (p.Phase == BulkTrimPhase.Item && p.ItemIndex >= 0 && p.ItemIndex < rows.Count)
        {
            var row = rows[p.ItemIndex];
            row.MarkRunning();
            row.SetProgress(p.ItemFraction);
        }

        var overall = WeightedOverall(weights, p.ItemIndex, p.ItemFraction);

        double reported;
        lock (_progressLock)
        {
            if (overall > _lastOverall)
            {
                _lastOverall = overall;
            }

            reported = _lastOverall;
        }

        overallProgress.Report(reported);
    }

    /// <summary>
    /// Weighted overall fraction <c>Σ(wᵢ·fᵢ)/Σwᵢ</c> where rows before the current index are done
    /// (<c>fᵢ = 1</c>), the current row contributes <paramref name="itemFraction"/>, and later rows are 0.
    /// Pure + monotonic in (index, fraction) — unit-tested directly.
    /// </summary>
    internal static double WeightedOverall(IReadOnlyList<double> weights, int itemIndex, double itemFraction)
    {
        double num = 0, den = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            var w = weights[i] > 0 ? weights[i] : 1d;
            var f = i < itemIndex ? 1d : i == itemIndex ? Math.Clamp(itemFraction, 0d, 1d) : 0d;
            num += w * f;
            den += w;
        }

        return den > 0 ? num / den : 0d;
    }

    /// <summary>
    /// Ledger fan-out: route each <see cref="BulkTrimItemResult"/> back to its row by <c>Tag</c>
    /// (terminal RowState / Warning / OutputPath / SizeAfter / Error), and set the aggregate result summary.
    /// </summary>
    private void ApplyLedger(BatchResult batch, IReadOnlyList<BulkItemViewModel> rows)
    {
        foreach (var result in batch.Items)
        {
            if (result.Item.Tag is BulkItemViewModel row)
            {
                row.ApplyResult(result);
            }
        }

        LastFailedItems = batch.FailedItems;

        Operation.ResultSummary = batch.Outcome switch
        {
            BatchOutcome.CompletedWithFailures =>
                string.Create(CultureInfo.InvariantCulture, $"Trimmed {batch.DoneCount}, {batch.FailedCount} failed"),
            BatchOutcome.Completed =>
                string.Create(CultureInfo.InvariantCulture, $"Trimmed {batch.DoneCount}"),
            _ => Operation.ResultSummary,
        };
    }

    private static BulkBatchState MapOutcome(BatchResult? batch) => batch?.Outcome switch
    {
        BatchOutcome.Completed => BulkBatchState.Completed,
        BatchOutcome.CompletedWithFailures => BulkBatchState.CompletedWithFailures,
        BatchOutcome.Cancelled => BulkBatchState.Cancelled,
        BatchOutcome.Blocked => BulkBatchState.Blocked,
        _ => BulkBatchState.Idle,
    };

    private static double RowWeight(BulkItemViewModel row) =>
        row.KeptDuration is { } kept && kept.TotalSeconds > 0 ? kept.TotalSeconds : Math.Max(1d, row.SizeBefore);

    // ---- Plumbing ---------------------------------------------------------------------------

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseRunState();

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BulkItemViewModel.KeyframesReady)
            or nameof(BulkItemViewModel.IsValidCut)
            or nameof(BulkItemViewModel.IsEnabled)
            or nameof(BulkItemViewModel.IsCheckedByUser)
            or nameof(BulkItemViewModel.RowState)
            or nameof(BulkItemViewModel.KeptDuration))
        {
            RaiseRunState();
        }
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperationViewModel.IsRunning) or nameof(OperationViewModel.State))
        {
            RaiseRunState();
        }
    }

    private void RaiseRunState()
    {
        if (_suspendRunStateRefresh)
        {
            return;
        }

        OnPropertyChanged(nameof(CanRunBatch));
        OnPropertyChanged(nameof(RunLabel));
        OnPropertyChanged(nameof(RunScopeSummary));   // T-134: same inputs as RunLabel
        OnPropertyChanged(nameof(RunScopeIsWarning));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(CanChangeSelection));
        OnPropertyChanged(nameof(CanDeleteOriginals));
        OnPropertyChanged(nameof(DeleteOriginalsLabel));
        OnPropertyChanged(nameof(DeletableOriginalCount));
        DeleteOriginalsCommand.RaiseCanExecuteChanged();
        RunBatchCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        ApplyToAllCommand.RaiseCanExecuteChanged();
        SelectAllItemsCommand.RaiseCanExecuteChanged();
        SelectNoItemsCommand.RaiseCanExecuteChanged();

        // T-111: the profile Apply→all gate (CanApplyProfileToAll) ALSO depends on the checked-row set
        // (Items.Any(IsCheckedByUser)) and on Items membership, both of which change here (rows added/
        // removed, a row's IsEnabled toggled → OnItemsChanged/OnItemChanged → RaiseRunState). Re-raise it
        // directly so its button re-evaluates deterministically instead of only via the global requery.
        OnPropertyChanged(nameof(CanApplyProfileToAll));
        ApplyProfileToAllCommand.RaiseCanExecuteChanged();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static string? SafeGetDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

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

    private static IEnumerable<string>? AsPaths(object? parameter) => parameter switch
    {
        null => null,
        string s => new[] { s },
        IEnumerable<string> many => many,
        _ => null,
    };
}
