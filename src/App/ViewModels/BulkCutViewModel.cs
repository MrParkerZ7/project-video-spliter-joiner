using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
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

    // §3 — the single bounded scan gate, owned here and shared into every row (max 3 concurrent ffprobe scans).
    private readonly SemaphoreSlim _scanGate = new(3, 3);

    private readonly object _progressLock = new();
    private double _lastOverall;

    private BulkBatchState _batchState = BulkBatchState.Idle;
    private CollisionPolicy _collisionPolicy = CollisionPolicy.AutoSuffix;
    private bool _overwrite;
    private ApplyToAllReport? _applyToAllReport;
    private IReadOnlyList<BulkTrimItemResult> _lastFailedItems = Array.Empty<BulkTrimItemResult>();
    private BulkItemViewModel? _selectedItem;
    private CutProfile? _selectedProfile;

    /// <summary>
    /// Create the tab VM sharing the App's media probe / split engine / thumbnail service / settings.
    /// When <paramref name="bulkTrimEngine"/> is null a real <see cref="BulkTrimEngine"/> is default-
    /// constructed over the SAME <paramref name="splitEngine"/> + a <see cref="KeptMiddleRequestBuilder"/>
    /// over the SAME <paramref name="probe"/> (no second engine / probe); tests inject a fake.
    /// <paramref name="player"/> is the single shared mini-preview player (T-100): selecting a row opens
    /// that file in THIS one FFME-backed <see cref="PlayerViewModel"/> (never a player per row). The
    /// composition root passes the Bulk tab's own <see cref="FfmeMediaPlayer"/>; when omitted it defaults
    /// to a no-op <see cref="NullMediaPlayer"/> so existing constructions / tests keep compiling.
    /// </summary>
    public BulkCutViewModel(
        IMediaProbe probe,
        ISplitEngine splitEngine,
        IThumbnailService thumbnails,
        IAppSettings settings,
        IBulkTrimEngine? bulkTrimEngine = null,
        IMediaPlayer? player = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _splitEngine = splitEngine ?? throw new ArgumentNullException(nameof(splitEngine));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _bulkTrimEngine = bulkTrimEngine ?? new BulkTrimEngine(_splitEngine, new KeptMiddleRequestBuilder(_probe));

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
        ApplyToAllCommand = new RelayCommand(p => ApplyToAll(p as BulkItemViewModel), _ => Items.Count > 1);
        RunBatchCommand = new RelayCommand(_ => _ = RunBatchAsync(), _ => CanRunBatch);
        SetIntroAtPlayheadCommand = new RelayCommand(_ => SetIntroAtPlayhead(), _ => CanSetCutAtPlayhead);
        SetOutroAtPlayheadCommand = new RelayCommand(_ => SetOutroAtPlayhead(), _ => CanSetCutAtPlayhead);
        CancelCommand = Operation.CancelCommand;

        // T-103: cut-profile commands — thin glue over the T-102 applier/persistence (no new model/persistence).
        Profiles = new ObservableCollection<CutProfile>();
        SaveProfileCommand = new RelayCommand(p => SaveProfile(p as string), _ => CanSaveProfile);
        ApplyProfileToSelectedCommand = new RelayCommand(_ => ApplyProfileToSelected(), _ => CanApplyProfileToSelected);
        ApplyProfileToAllCommand = new RelayCommand(_ => ApplyProfileToAll(), _ => CanApplyProfileToAll);
        DeleteProfileCommand = new RelayCommand(_ => DeleteSelectedProfile(), _ => HasSelectedProfile);
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
    /// The currently-selected row (two-way bound from the list, T-101). Changing it opens the row's
    /// <see cref="BulkItemViewModel.Path"/> in the ONE shared <see cref="Player"/> (a null selection —
    /// list cleared / nothing selected — unloads it instead). The open goes through
    /// <see cref="PlayerViewModel.Open"/> → <see cref="FfmeMediaPlayer"/>'s built-in
    /// <see cref="MediaReopenGuard"/> (T-080), so a rapid row-to-row switch never issues Open while a
    /// prior Close is still in flight (the native-AV hazard) — each Open supersedes the previous pending
    /// one. The first <see cref="AddFilesAsync"/> auto-selects the first row for convenience; removing
    /// the selected row re-points the selection at a neighbour (or unloads) so it never opens a
    /// just-removed file.
    /// </summary>
    public BulkItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OpenOrUnloadSelected(value);
                OnPropertyChanged(nameof(HasSelection));
                RaisePlayheadCommandStates();
                RaiseProfileCommandStates(); // Save / Apply→selected depend on the selection (T-103)
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

    /// <summary>Count-aware primary-button label: <c>"Run bulk cut (N)"</c> over the enabled+valid rows.</summary>
    public string RunLabel =>
        string.Create(CultureInfo.InvariantCulture, $"Run bulk cut ({Items.Count(i => i.IsEnabled && i.IsValidCut)})");

    /// <summary>Clear all is enabled with ≥1 row and no run in flight.</summary>
    public bool CanClear => Items.Count > 0 && !Operation.IsRunning;

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

    /// <summary>Copy one row's cut points to every other enabled row (parameter = the source row).</summary>
    public RelayCommand ApplyToAllCommand { get; }

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

            var item = new BulkItemViewModel(path, _probe, _scanGate)
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
    /// Open the newly-selected row's file in the ONE shared <see cref="Player"/>, or unload it on a
    /// null selection (T-100). The open goes through <see cref="PlayerViewModel.Open"/>, which for the
    /// production <see cref="FfmeMediaPlayer"/> registers the open with its <see cref="MediaReopenGuard"/>
    /// (T-080) and only issues the native Open once the element has settled out of any closing/opening
    /// state — so a rapid row-to-row switch supersedes the prior pending open instead of crashing.
    /// </summary>
    private void OpenOrUnloadSelected(BulkItemViewModel? item)
    {
        if (item is null)
        {
            Player.Unload();
        }
        else
        {
            Player.Open(item.Path);
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
    }

    // ---- Apply-to-all (§2.3) ----------------------------------------------------------------

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
        SaveProfileCommand.RaiseCanExecuteChanged(); // global requery re-evaluates every RelayCommand's CanExecute
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
        Player.Stop();

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

        var options = new BulkTrimOptions(Overwrite ? CollisionPolicy.Overwrite : CollisionPolicy);
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
        OnPropertyChanged(nameof(CanRunBatch));
        OnPropertyChanged(nameof(RunLabel));
        OnPropertyChanged(nameof(CanClear));
        RunBatchCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        ApplyToAllCommand.RaiseCanExecuteChanged();
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
