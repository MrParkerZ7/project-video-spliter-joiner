using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// View model for the Join screen (T-008). Gathers clips into an ordered list, probes each for an
/// info chip, runs a live concat-compatibility check on every list change, and — only when the set
/// is compatible and an output path is set — stream-copy concats them via <see cref="IJoinEngine"/>.
/// A refusal (incompatible inputs) surfaces the exact mismatch reasons. All engine work is funnelled
/// through a composed <see cref="OperationViewModel"/> so progress / cancel / friendly-error handling
/// is shared with the Split screen. Deliberately WPF-free and constructor-injected for unit testing.
/// </summary>
public sealed class JoinViewModel : ObservableObject
{
    private readonly IJoinEngine _joinEngine;
    private readonly IMediaProbe _probe;
    private readonly IAppSettings _settings;

    private CompatReport? _compat;
    private string _compatSummary = "Add at least 2 files to join.";
    private bool _isCompatible;
    private string _outputPath = string.Empty;
    private bool _overwrite;
    private JoinResult? _lastResult;

    /// <summary>
    /// Create the join VM over the join engine + media probe (real or fake). <paramref name="settings"/>
    /// is the cross-session folder memory (T-038); when omitted a real file-backed store is used so
    /// existing constructions keep working.
    /// </summary>
    public JoinViewModel(IJoinEngine joinEngine, IMediaProbe probe, IAppSettings? settings = null)
    {
        _joinEngine = joinEngine ?? throw new ArgumentNullException(nameof(joinEngine));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _settings = settings ?? new AppSettings();

        Operation = new OperationViewModel();
        Items = new ObservableCollection<JoinItemViewModel>();

        // CanRunJoin depends on the item count → recompute when the collection changes.
        Items.CollectionChanged += OnItemsChanged;

        AddFilesCommand = new RelayCommand(p => _ = AddFilesAsync(AsPaths(p)));
        RemoveCommand = new RelayCommand(p => _ = RemoveAsync(p as JoinItemViewModel), _ => true);
        MoveUpCommand = new RelayCommand(p => _ = MoveUpAsync(p as JoinItemViewModel), CanMoveUp);
        MoveDownCommand = new RelayCommand(p => _ = MoveDownAsync(p as JoinItemViewModel), CanMoveDown);
        RunJoinCommand = new RelayCommand(_ => _ = RunJoinAsync(), _ => CanRunJoin);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => !string.IsNullOrWhiteSpace(OutputPath));
        CancelCommand = Operation.CancelCommand;
    }

    // ---- State ------------------------------------------------------------------------------

    /// <summary>The clips to join, in play order. Order is significant (drives the output).</summary>
    public ObservableCollection<JoinItemViewModel> Items { get; }

    /// <summary>The most recent compatibility report; null before the first check.</summary>
    public CompatReport? Compat
    {
        get => _compat;
        private set => SetProperty(ref _compat, value);
    }

    /// <summary>
    /// One-line compat verdict: green "N clips ready to join" when compatible, or a red line naming
    /// the mismatch(es) when not. Bound by the view's banner.
    /// </summary>
    public string CompatSummary
    {
        get => _compatSummary;
        private set => SetProperty(ref _compatSummary, value);
    }

    /// <summary>True when the current item set is concat-compatible (drives the banner colour + run gate).</summary>
    public bool IsCompatible
    {
        get => _isCompatible;
        private set
        {
            if (SetProperty(ref _isCompatible, value))
            {
                OnPropertyChanged(nameof(CanRunJoin));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>Destination path for the joined file.</summary>
    public string OutputPath
    {
        get => _outputPath;
        set
        {
            if (SetProperty(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(CanRunJoin));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>When true, an existing file at <see cref="OutputPath"/> is replaced.</summary>
    public bool Overwrite
    {
        get => _overwrite;
        set => SetProperty(ref _overwrite, value);
    }

    /// <summary>The most recent successful join result (written output path), for the result line.</summary>
    public JoinResult? LastResult
    {
        get => _lastResult;
        private set => SetProperty(ref _lastResult, value);
    }

    /// <summary>The shared progress / cancel / error operation state for the run.</summary>
    public OperationViewModel Operation { get; }

    /// <summary>
    /// The cross-session folder memory (T-038). Exposed so the view's file-picker code-behind can seed
    /// <c>OpenFileDialog.InitialDirectory</c> from <see cref="IAppSettings.LastInputDir"/> (add-files
    /// picker) and the output-save picker from <see cref="IAppSettings.LastOutputDir"/>, sharing the
    /// same instance the VM writes to.
    /// </summary>
    public IAppSettings Settings => _settings;

    /// <summary>
    /// Run is enabled only with ≥2 items, a compatible set, and an output path set. (Single-file
    /// passthrough is deliberately NOT offered by the UI — keep the gate at ≥2.)
    /// </summary>
    public bool CanRunJoin =>
        Items.Count >= 2
        && IsCompatible
        && !string.IsNullOrWhiteSpace(OutputPath);

    // ---- Commands ---------------------------------------------------------------------------

    /// <summary>Append clips (parameter = paths); each is probed for its chip, then compat re-checks.</summary>
    public RelayCommand AddFilesCommand { get; }

    /// <summary>Remove a clip (parameter = <see cref="JoinItemViewModel"/>) then re-check compat.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Move a clip one place earlier in the order (parameter = item) then re-check.</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>Move a clip one place later in the order (parameter = item) then re-check.</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>Run the join through the engine (guarded by <see cref="CanRunJoin"/>).</summary>
    public RelayCommand RunJoinCommand { get; }

    /// <summary>Open the folder containing the output file.</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Cancel the in-flight run — delegates to <see cref="OperationViewModel.CancelCommand"/>.</summary>
    public RelayCommand CancelCommand { get; }

    // ---- Add / remove / reorder -------------------------------------------------------------

    /// <summary>
    /// Append one <see cref="JoinItemViewModel"/> per path (duplicates allowed), fill each info chip
    /// via a best-effort probe, then re-run the compat check. Never throws for a bad path — a probe
    /// failure only leaves that chip blank; the compat check still names the underlying problem.
    /// </summary>
    public async Task AddFilesAsync(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return;
        }

        var added = new List<JoinItemViewModel>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var item = new JoinItemViewModel(path);
            Items.Add(item);
            added.Add(item);
        }

        if (added.Count == 0)
        {
            return;
        }

        // Remember the folder the last added file came from (T-038) so next session's add-files picker
        // opens there. Best-effort — persistence failures are swallowed inside the settings store.
        var lastAddedDir = Path.GetDirectoryName(Path.GetFullPath(added[^1].Path));
        if (!string.IsNullOrEmpty(lastAddedDir))
        {
            _settings.LastInputDir = lastAddedDir;
        }

        // Probe each newly-added item for its info chip (best-effort, order-independent).
        foreach (var item in added)
        {
            await PopulateInfoChipAsync(item).ConfigureAwait(true);
        }

        await RefreshCompatAsync().ConfigureAwait(true);
    }

    private async Task RemoveAsync(JoinItemViewModel? item)
    {
        if (item is null || !Items.Remove(item))
        {
            return;
        }

        await RefreshCompatAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The single reorder path: move the item at <paramref name="fromIndex"/> to
    /// <paramref name="toIndex"/> and re-run the SAME compat check the Up/Down buttons and the
    /// drag-reorder gesture (T-017) all funnel through. Invalid or equal indices are a no-op — a
    /// bad/out-of-range index never throws (guarded here so callers, including the drag hit-test,
    /// can pass a raw computed index safely). <see cref="MoveUpAsync"/> / <see cref="MoveDownAsync"/>
    /// delegate here so there is no duplicated reorder logic.
    /// </summary>
    public async Task MoveAsync(int fromIndex, int toIndex)
    {
        var count = Items.Count;
        if (count < 2)
        {
            return; // nothing to reorder
        }

        // Clamp the destination into range; ignore an out-of-range source outright (nothing to move).
        if (fromIndex < 0 || fromIndex >= count)
        {
            return;
        }

        if (toIndex < 0)
        {
            toIndex = 0;
        }
        else if (toIndex >= count)
        {
            toIndex = count - 1;
        }

        if (fromIndex == toIndex)
        {
            return; // same slot → no reorder, no needless recheck
        }

        Items.Move(fromIndex, toIndex);
        await RefreshCompatAsync().ConfigureAwait(true);
    }

    /// <summary>Synchronous convenience wrapper over <see cref="MoveAsync"/> for the drag-reorder code-behind.</summary>
    public void Move(int fromIndex, int toIndex) => _ = MoveAsync(fromIndex, toIndex);

    private Task MoveUpAsync(JoinItemViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var index = Items.IndexOf(item);
        return MoveAsync(index, index - 1);
    }

    private Task MoveDownAsync(JoinItemViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var index = Items.IndexOf(item);
        return MoveAsync(index, index + 1);
    }

    private bool CanMoveUp(object? parameter) => parameter is JoinItemViewModel item && Items.IndexOf(item) > 0;

    private bool CanMoveDown(object? parameter) =>
        parameter is JoinItemViewModel item && Items.IndexOf(item) is var i && i >= 0 && i < Items.Count - 1;

    // ---- Compatibility ----------------------------------------------------------------------

    /// <summary>
    /// Re-run the compatibility check over the current item order and update
    /// <see cref="Compat"/> / <see cref="CompatSummary"/> / <see cref="IsCompatible"/>. With fewer
    /// than 2 items the check is skipped: the summary invites more files and the set is not runnable.
    /// </summary>
    public async Task RefreshCompatAsync(CancellationToken ct = default)
    {
        if (Items.Count < 2)
        {
            Compat = null;
            IsCompatible = false;
            CompatSummary = "Add at least 2 files to join.";
            return;
        }

        var paths = Items.Select(i => i.Path).ToList();

        CompatReport report;
        try
        {
            report = await _joinEngine.CheckCompatibilityAsync(paths, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A thrown check is unexpected (the engine reports mismatches as data, not throws) —
            // treat it defensively as "not compatible" so Run stays gated.
            Compat = null;
            IsCompatible = false;
            CompatSummary = $"Could not verify compatibility: {ex.Message}";
            return;
        }

        Compat = report;
        IsCompatible = report.Compatible;
        CompatSummary = report.Compatible
            ? $"{Items.Count} clips ready to join."
            : FormatMismatches(report);
    }

    private static string FormatMismatches(CompatReport report)
    {
        if (report.Mismatches.Count == 0)
        {
            return "Inputs are not compatible.";
        }

        var details = report.Mismatches.Select(m => m.Detail);
        return "Cannot join — " + string.Join("; ", details);
    }

    // ---- Run join ---------------------------------------------------------------------------

    /// <summary>
    /// Build a <see cref="JoinRequest"/> from the current items (in list order) and run it through
    /// the engine via <see cref="OperationViewModel.RunWithResultAsync{T}"/>. A refusal result
    /// (<see cref="JoinResult.Success"/> == false) is mapped to a friendly error naming the
    /// mismatches; on success <see cref="LastResult"/> is set.
    /// </summary>
    public async Task RunJoinAsync()
    {
        if (!CanRunJoin)
        {
            return;
        }

        var request = new JoinRequest(
            InputPaths: Items.Select(i => i.Path).ToList().AsReadOnly(),
            OutputPath: OutputPath,
            Overwrite: Overwrite);

        LastResult = null;
        JoinResult? result = null;

        await Operation.RunWithResultAsync(
            work: async (progress, status, ct) =>
            {
                // T-044: pass the stage reporter so Operation.StatusText tracks each real phase
                // (Checking compatibility → Joining → Finalizing → Done) as the engine progresses.
                result = await _joinEngine.JoinAsync(request, progress, ct, status).ConfigureAwait(true);
                return result;
            },
            // A refusal is reported as a JoinResult with Success == false — turn it into a friendly
            // error that names each mismatch (never surface a broken/half-written file).
            failureSelector: r => r.Success
                ? null
                : new UserFacingError(
                    ErrorCategory.IncompatibleJoin,
                    "The clips could not be joined.",
                    RefusalDetail(r.Refusal),
                    "Fix or remove the offending clip, then try again.",
                    // When the refusal came from a failed ffmpeg run, carry its saved-log path + full
                    // stderr so the error surface is copyable and the "Open log" affordance lights up.
                    // Pre-flight refusals (incompatible inputs) leave both null.
                    LogFilePath: r.LogFilePath,
                    FullText: r.FullStdErr),
            runningStatus: "Joining…").ConfigureAwait(true);

        if (Operation.State == OperationState.Completed && result is { Success: true })
        {
            LastResult = result;

            // Remember the folder we just wrote the joined file into (T-038) so it seeds next session's
            // output-save picker. Best-effort — swallowed inside the settings store.
            var outputDir = SafeGetDirectory(OutputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                _settings.LastOutputDir = outputDir;
            }
        }
    }

    /// <summary>The containing folder of <paramref name="path"/>, or null if it can't be resolved. Never throws.</summary>
    private static string? SafeGetDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    private static string RefusalDetail(CompatReport? refusal)
    {
        if (refusal is null || refusal.Mismatches.Count == 0)
        {
            return "The inputs were not concat-compatible.";
        }

        return string.Join(Environment.NewLine, refusal.Mismatches.Select(m => $"[{m.Field}] {m.Detail}"));
    }

    // ---- Info chip --------------------------------------------------------------------------

    private async Task PopulateInfoChipAsync(JoinItemViewModel item)
    {
        try
        {
            var result = await _probe.ProbeAsync(item.Path).ConfigureAwait(true);
            if (result is ProbeResult.ProbeSucceeded ok)
            {
                item.InfoText = FormatInfoChip(ok.Info);
            }
        }
        catch
        {
            // Info chip is best-effort — a probe failure just leaves it blank; the compat check
            // (which also probes) is the authoritative verdict.
        }
    }

    private static string FormatInfoChip(MediaInfo info)
    {
        var video = info.VideoStreams.Count > 0 ? info.VideoStreams[0] : null;
        if (video is null)
        {
            return info.HasAudio ? "audio only" : info.Container;
        }

        var codec = video.CodecName;
        if (video.Width is int w && video.Height is int h)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{codec} · {w}x{h}");
        }

        return codec;
    }

    // ---- Open folder ------------------------------------------------------------------------

    private void OpenFolder()
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(OutputPath));
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch
        {
            // Best-effort; opening Explorer is non-critical and never runs under tests.
        }
    }

    // ---- Plumbing ---------------------------------------------------------------------------

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanRunJoin));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        RunJoinCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    private static IEnumerable<string>? AsPaths(object? parameter) => parameter switch
    {
        null => null,
        string s => new[] { s },
        IEnumerable<string> many => many,
        _ => null,
    };
}
