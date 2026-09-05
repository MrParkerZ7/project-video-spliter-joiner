using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Reusable view-model helper that gives every long-running operation (split/join/detect) a
/// consistent progress + cancel + friendly-error experience. Deliberately WPF-free (no Window
/// refs) so it is unit-testable off the UI thread. Progress is marshalled via
/// <see cref="Progress{T}"/> which captures the current synchronization context, so a real UI
/// updates on the UI thread while tests run on the test thread.
/// </summary>
public sealed class OperationViewModel : ObservableObject
{
    private OperationState _state = OperationState.Idle;
    private double _progress;
    private string _statusText = string.Empty;
    private string? _etaText;
    private string? _resultSummary;
    private UserFacingError? _error;
    private CancellationTokenSource? _cts;

    // T-045: ETA is estimated from real elapsed time vs reported fraction. The stopwatch measures the
    // run's wall-clock; the estimator turns (elapsed, fraction) samples into a smoothed remaining-time
    // and the friendly EtaText label. Both are per-run — reset at BeginRun, cleared on end/Reset.
    private readonly Stopwatch _stopwatch = new();
    private readonly EtaEstimator _eta = new();

    // T-093: optional total run duration the producing VM (Split/Join) sets BEFORE calling a Run*
    // method — seeds the estimator's duration-based fallback so the ETA converges to a decreasing
    // number even before ffmpeg reports a usable fraction (the "estimating… forever" cure). Consumed
    // once at BeginRun and cleared afterward so it never leaks into the next run.
    private TimeSpan? _pendingEstimatedDuration;

    public OperationViewModel()
    {
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
    }

    /// <summary>
    /// T-093: seed the total run duration for the NEXT run so the ETA can fall back to a
    /// duration-based estimate before ffmpeg reports a usable fraction (avoids "estimating…" for the
    /// whole run on a sparse <c>-c copy</c>). Call this immediately BEFORE a <c>Run*</c> method; the
    /// value is applied once at <see cref="BeginRun(string, out IProgress{double}, out CancellationToken)"/>
    /// and then cleared. A non-positive / null duration simply leaves the fallback disabled
    /// (fraction-only behaviour). Purely additive — it never changes what a fraction-based estimate
    /// produces once real progress arrives.
    /// </summary>
    public void SeedEstimatedDuration(TimeSpan? totalDuration)
    {
        _pendingEstimatedDuration = totalDuration is { } d && d > TimeSpan.Zero ? d : null;
    }

    /// <summary>Current lifecycle state.</summary>
    public OperationState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                // T-073: the per-state lifecycle surfaces (Running / Completed / Cancelled) are all
                // computed from State, so re-raise them here alongside IsRunning. Failed is surfaced
                // via the existing Error block (NullToCollapsed), not a bool, so it needs no flag.
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsCancelled));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(IsIndeterminate));
                OnPropertyChanged(nameof(TaskbarProgressState));
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Fractional progress in the range 0..1.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
            {
                // A real fraction (>0) flips the busy indicator off — see IsIndeterminate.
                OnPropertyChanged(nameof(IsIndeterminate));

                // T-068: crossing 0 → >0 also flips the taskbar state Indeterminate → Normal
                // (and every value change moves ProgressValue), so re-raise it here too.
                OnPropertyChanged(nameof(TaskbarProgressState));

                // T-045: feed the real elapsed vs this fraction to the ETA estimator. Only while
                // actually running — completion sets Progress = 1 through here too, and we don't want
                // that to compute (or resurrect) an ETA; EndRun/Complete clear it explicitly.
                if (IsRunning)
                {
                    UpdateEta(value);
                }
            }
        }
    }

    /// <summary>
    /// Human-readable "what's happening" line (e.g. "Splitting…"). Set from the operation's
    /// running status at the start of a run and cleared on Reset/complete. Public setter so
    /// callers can update the stage text mid-run — later tickets (T-044 staged status, T-045 ETA)
    /// extend this line per stage.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// True while an operation is running but NO usable granular progress has arrived yet — so the
    /// bar animates as a busy indicator instead of sitting stuck at 0. Heuristic: indeterminate
    /// while running AND <see cref="Progress"/> is still 0 (nothing reported yet); it flips to
    /// determinate the instant a real fraction (&gt;0) arrives, and is false whenever not running.
    /// This is the cure for the "-c copy split looks stuck" problem — ffmpeg's <c>time=</c> can be
    /// sparse/instant, so the bar shows motion immediately rather than a frozen 0%.
    /// </summary>
    public bool IsIndeterminate => IsRunning && _progress <= 0d;

    /// <summary>
    /// T-068: the Windows taskbar-button progress state for this operation, bound by
    /// <c>Window.TaskbarItemInfo.ProgressState</c> (the fill uses <see cref="Progress"/> for
    /// <c>ProgressValue</c>). Mapping:
    /// <list type="bullet">
    /// <item><see cref="OperationState.Failed"/> → <see cref="TaskbarItemProgressState.Error"/> (red).</item>
    /// <item>not running (Idle / Completed / Cancelled) → <see cref="TaskbarItemProgressState.None"/>
    /// (clears the bar — no stuck fill after a run ends or is reset).</item>
    /// <item>running with no usable fraction yet (<see cref="IsIndeterminate"/>) →
    /// <see cref="TaskbarItemProgressState.Indeterminate"/> (the busy pulse while "Preparing").</item>
    /// <item>running with a real fraction → <see cref="TaskbarItemProgressState.Normal"/> (green fill).</item>
    /// </list>
    /// Failed is checked first so a failed run shows red rather than clearing to None. This is a
    /// pure computed property (unit-testable); <see cref="OnPropertyChanged"/> is raised for it
    /// wherever <see cref="State"/> / <see cref="Progress"/> / <see cref="IsIndeterminate"/> change.
    /// </summary>
    public System.Windows.Shell.TaskbarItemProgressState TaskbarProgressState
    {
        get
        {
            if (State == OperationState.Failed)
            {
                return System.Windows.Shell.TaskbarItemProgressState.Error;
            }

            if (!IsRunning)
            {
                return System.Windows.Shell.TaskbarItemProgressState.None;
            }

            return IsIndeterminate
                ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate
                : System.Windows.Shell.TaskbarItemProgressState.Normal;
        }
    }

    /// <summary>
    /// T-045: friendly estimated-time-remaining label shown while the op runs — e.g. "~40s left",
    /// "~1m 20s left". Set from the <see cref="EtaEstimator"/> on each progress sample. While the run
    /// is indeterminate (no usable fraction yet) it reads "estimating…" rather than a fake number;
    /// it is null when no operation is running (the UI hides the label). Cleared on
    /// complete/fail/cancel and <see cref="Reset"/>.
    /// </summary>
    public string? EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    /// <summary>The friendly error when <see cref="State"/> is <see cref="OperationState.Failed"/>; otherwise null.</summary>
    public UserFacingError? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>True while an operation is running.</summary>
    public bool IsRunning => State == OperationState.Running;

    /// <summary>
    /// T-073: true once an operation finished successfully — drives the Completed success surface
    /// (✓ + <see cref="ResultSummary"/> + Open folder). Stays true until the next run/load/Reset moves
    /// the state away from <see cref="OperationState.Completed"/>, so "done" no longer silently vanishes.
    /// </summary>
    public bool IsCompleted => State == OperationState.Completed;

    /// <summary>
    /// T-073: true once an operation was cancelled by the user — drives the muted "Cancelled" surface
    /// (neutral, NOT error-red). Stays true until the next run/load/Reset moves the state away from
    /// <see cref="OperationState.Cancelled"/>.
    /// </summary>
    public bool IsCancelled => State == OperationState.Cancelled;

    /// <summary>
    /// T-073: a short human line describing what a successful run produced — e.g. "Split into 3 parts"
    /// or "Joined 4 clips → joined.mkv". Supplied by the producing VM (Split/Join) after a successful
    /// run, since it knows the real counts + output name; shown in the Completed surface. Cleared at the
    /// start of every new run (<see cref="BeginRun(string, out IProgress{double}, out CancellationToken)"/>)
    /// and on <see cref="Reset"/>, so a stale "done" line never lingers into a new run/load.
    /// </summary>
    public string? ResultSummary
    {
        get => _resultSummary;
        set => SetProperty(ref _resultSummary, value);
    }

    /// <summary>True only while running — the <see cref="CancelCommand"/> is enabled solely in this window.</summary>
    public bool CanCancel => State == OperationState.Running;

    /// <summary>Cancels the in-flight operation. Enabled only while running.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// Runs <paramref name="work"/> as a tracked operation. Sets <see cref="OperationState.Running"/>,
    /// wires an <see cref="IProgress{T}"/> that marshals into <see cref="Progress"/>, and awaits.
    /// On success → Completed (Progress = 1); on cancellation → Cancelled; on an unexpected
    /// exception → Failed with a mapped error (if the exception carries an <see cref="FfmpegResult"/>
    /// it is mapped via <see cref="FfmpegErrorMapper"/>, otherwise a generic Unknown error).
    /// </summary>
    public async Task RunAsync(
        Func<IProgress<double>, CancellationToken, Task> work,
        string runningStatus)
    {
        ArgumentNullException.ThrowIfNull(work);

        BeginRun(runningStatus, out var progress, out var token);
        try
        {
            await work(progress, token).ConfigureAwait(true);
            Complete();
        }
        catch (OperationCanceledException)
        {
            State = OperationState.Cancelled;
        }
        catch (Exception ex)
        {
            Error = MapException(ex);
            State = OperationState.Failed;
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> which returns a typed result, then inspects it with
    /// <paramref name="failureSelector"/>. Engines report failure via typed results (not
    /// exceptions), so this is the primary path the Split/Join VMs use to wire an engine result
    /// to a friendly error: if the selector returns a non-null <see cref="UserFacingError"/> →
    /// Failed + Error; otherwise → Completed. Cancellation and unexpected exceptions are handled
    /// exactly as in <see cref="RunAsync"/>.
    /// </summary>
    public async Task RunWithResultAsync<T>(
        Func<IProgress<double>, CancellationToken, Task<T>> work,
        Func<T, UserFacingError?> failureSelector,
        string runningStatus)
    {
        await RunWithResultAsync(
            (progress, _, token) => work(progress, token),
            failureSelector,
            runningStatus).ConfigureAwait(true);
    }

    /// <summary>
    /// T-044 overload of <see cref="RunWithResultAsync{T}(Func{IProgress{double}, CancellationToken, Task{T}}, Func{T, UserFacingError?}, string)"/>
    /// that also hands the work an <see cref="IProgress{OperationStatus}"/>. Each reported
    /// <see cref="OperationStatus"/> updates <see cref="StatusText"/> (formatted stage + detail),
    /// marshalled onto the captured synchronization context exactly like the numeric progress — so a
    /// real UI updates the bound status label on the UI thread while tests observe it after awaiting.
    /// The engine emits these as it enters each real stage (Preparing → Splitting → Finalizing → Done),
    /// so the label tracks the actual work rather than a timer.
    /// </summary>
    public async Task RunWithResultAsync<T>(
        Func<IProgress<double>, IProgress<OperationStatus>, CancellationToken, Task<T>> work,
        Func<T, UserFacingError?> failureSelector,
        string runningStatus)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(failureSelector);

        BeginRun(runningStatus, out var progress, out var status, out var token);
        try
        {
            var result = await work(progress, status, token).ConfigureAwait(true);
            var failure = failureSelector(result);
            if (failure is not null)
            {
                Error = failure;
                State = OperationState.Failed;
            }
            else
            {
                Complete();
            }
        }
        catch (OperationCanceledException)
        {
            State = OperationState.Cancelled;
        }
        catch (Exception ex)
        {
            Error = MapException(ex);
            State = OperationState.Failed;
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// T-069 overload that ALSO hands the work an <see cref="IProgress{PartProgress}"/> for per-part
    /// split reporting, alongside the numeric progress and staged status channels. Each reported
    /// <see cref="PartProgress"/> is forwarded to <paramref name="onPartProgress"/>, marshalled onto the
    /// captured synchronization context exactly like the numeric progress — so a real UI updates the
    /// bound part rows on the UI thread while tests observe them after awaiting. The part channel does
    /// NOT touch <see cref="Progress"/> / <see cref="StatusText"/> — it is purely additive; the overall
    /// bar and staged status behave identically to the T-044 overload.
    /// </summary>
    public async Task RunWithResultAsync<T>(
        Func<IProgress<double>, IProgress<OperationStatus>, IProgress<PartProgress>, CancellationToken, Task<T>> work,
        Func<T, UserFacingError?> failureSelector,
        Action<PartProgress> onPartProgress,
        string runningStatus)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(failureSelector);
        ArgumentNullException.ThrowIfNull(onPartProgress);

        BeginRun(runningStatus, out var progress, out var status, out var token);

        // Marshal per-part samples onto the captured context, like the numeric/staged channels.
        var partProgress = new Progress<PartProgress>(onPartProgress);

        try
        {
            var result = await work(progress, status, partProgress, token).ConfigureAwait(true);
            var failure = failureSelector(result);
            if (failure is not null)
            {
                Error = failure;
                State = OperationState.Failed;
            }
            else
            {
                Complete();
            }
        }
        catch (OperationCanceledException)
        {
            State = OperationState.Cancelled;
        }
        catch (Exception ex)
        {
            Error = MapException(ex);
            State = OperationState.Failed;
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>Resets the VM back to <see cref="OperationState.Idle"/> for reuse.</summary>
    public void Reset()
    {
        if (IsRunning)
        {
            Cancel();
        }

        Error = null;
        // T-073: clear the success line so a Clear/reset drops any lingering "done" surface.
        ResultSummary = null;
        Progress = 0;
        StatusText = string.Empty;
        EtaText = null;
        _stopwatch.Reset();
        _eta.Reset();
        State = OperationState.Idle;
    }

    /// <summary>
    /// T-129: report a user-facing failure for a deliberate gesture that is NOT a tracked run — e.g. the
    /// Bulk Cut profile-thumbnail upload, which copies a single file and has no progress/ETA of its own.
    /// It sets <see cref="Error"/>, the SAME surface a failed run uses, so the screen's existing error
    /// block (headline + hint + Copy details / Open log) renders it instead of the gesture failing
    /// silently. Purely additive: no run is started, no progress/ETA/stopwatch is touched, and the
    /// cancellation source is left alone.
    /// <para>When no run is in flight the state also moves to <see cref="OperationState.Failed"/> (and the
    /// success line is cleared), keeping the mutually-exclusive Running/Completed/Cancelled surfaces
    /// honest — a stale green "Completed ✓" must not sit beside a red error. While a run IS in flight the
    /// state is deliberately left untouched: the run owns its own lifecycle and must not be derailed by a
    /// side gesture, so only the error line appears.</para>
    /// <para>Passing <c>null</c> retracts a previously reported failure (what a later SUCCESSFUL gesture
    /// does) and, when the state is <see cref="OperationState.Failed"/>, returns it to
    /// <see cref="OperationState.Idle"/> so no red taskbar lingers with nothing to explain it. Callers are
    /// expected to retract only the error they themselves reported — see
    /// <c>BulkCutViewModel.ClearThumbnailUploadError</c>.</para>
    /// </summary>
    public void ReportFailure(UserFacingError? error)
    {
        if (IsRunning)
        {
            // A run owns its own State; only surface the message beside it.
            Error = error;
            return;
        }

        Error = error;

        if (error is not null)
        {
            ResultSummary = null;
            State = OperationState.Failed;
        }
        else if (State == OperationState.Failed)
        {
            State = OperationState.Idle;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
    }

    private void BeginRun(string runningStatus, out IProgress<double> progress, out CancellationToken token)
        => BeginRun(runningStatus, out progress, out _, out token);

    private void BeginRun(
        string runningStatus,
        out IProgress<double> progress,
        out IProgress<OperationStatus> status,
        out CancellationToken token)
    {
        // T-157 — refuse re-entry loudly instead of corrupting the run in flight.
        //
        // The two statements below dispose and replace the CancellationTokenSource. Entered while a run
        // is live, that disposes the RUNNING operation's token (which it is still observing, and which
        // WaitForExitAsync has registered on), repoints Cancel at the newcomer, and resets the progress
        // and status the user is watching. It did all of that silently.
        //
        // Split reached this by ordinary use: LoadAsync and RunSplitAsync share one instance, so
        // dropping a second video mid-export tore the export down. That door is now shut at the caller
        // (SplitViewModel refuses the load and says so), and this guard is the backstop for every other
        // caller — present and future — because a second run on a live operation is a programming error,
        // and the old behaviour made it an invisible one.
        if (IsRunning)
        {
            throw new InvalidOperationException(
                "This operation is already running. Starting a second run would dispose the first one's " +
                "CancellationTokenSource out from under it and detach Cancel. Wait for it, or cancel it.");
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        token = _cts.Token;

        Error = null;
        // T-073: clear any prior success line so a new run never shows the previous run's "done".
        ResultSummary = null;
        Progress = 0;
        StatusText = runningStatus;

        // T-045: start timing this run and prime the estimator. State is flipped to Running BEFORE
        // touching ETA so the "estimating…" seed and any progress-driven UpdateEta see IsRunning=true.
        _eta.Reset();
        // T-093: hand the estimator this run's total duration (if the caller seeded one) so its
        // duration-based fallback can produce a converging estimate before a usable fraction arrives.
        _eta.SeedDuration(_pendingEstimatedDuration ?? TimeSpan.Zero);
        _pendingEstimatedDuration = null;
        _stopwatch.Restart();
        State = OperationState.Running;
        // No usable fraction yet → show "estimating…" rather than nothing or a fake number. Once the
        // first progress sample arrives the estimator (fraction-based, or duration-based fallback)
        // replaces this with a real "~Ns left".
        EtaText = EtaEstimator.FormatEta(null);

        // Progress<T> captures the current SynchronizationContext (the UI thread in a real app,
        // the test thread under xUnit) and posts updates back to it.
        progress = new Progress<double>(value => Progress = Math.Clamp(value, 0d, 1d));

        // T-044: the stage channel. Each OperationStatus becomes the human-readable StatusText line,
        // marshalled through the same captured context as the numeric progress.
        status = new Progress<OperationStatus>(s => StatusText = FormatStatus(s));
    }

    /// <summary>
    /// Format a stage transition into the one-line status label: "Stage… (detail)" — e.g.
    /// "Splitting… (4 parts)", "Preparing…", "Finalizing…". A "Done" stage collapses to a plain
    /// "Done" (no ellipsis) since it marks completion rather than ongoing work.
    /// <para>T-093: when the detail is itself an ongoing-action phrase ending in an ellipsis (e.g.
    /// "scanning keyframes…"), render it as "Stage — detail" instead of "Stage… (detail)" so the
    /// active sub-status reads cleanly ("Preparing — scanning keyframes…") rather than doubling the
    /// ellipsis and wrapping in parentheses.</para>
    /// </summary>
    private static string FormatStatus(OperationStatus s)
    {
        if (s is null || string.IsNullOrWhiteSpace(s.Stage))
        {
            return string.Empty;
        }

        var isDone = string.Equals(s.Stage, "Done", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(s.Detail))
        {
            return isDone ? "Done" : s.Stage + "…";
        }

        // A detail that is itself an "…ing…" progress phrase reads as a sub-status: "Stage — detail".
        var detail = s.Detail.TrimEnd();
        if (!isDone && detail.EndsWith('…'))
        {
            return $"{s.Stage} — {detail}";
        }

        var stage = isDone ? "Done" : s.Stage + "…";
        return $"{stage} ({s.Detail})";
    }

    private void Complete()
    {
        Progress = 1d;
        State = OperationState.Completed;
    }

    /// <summary>
    /// T-045: feed the current elapsed time + reported fraction to the estimator and update
    /// <see cref="EtaText"/>. A fraction too early (or NaN) leaves it at "estimating…"; a real
    /// estimate becomes a friendly "~Ns left". Called only while running (from the Progress setter).
    /// </summary>
    private void UpdateEta(double fraction)
    {
        var remaining = _eta.Update(_stopwatch.Elapsed, fraction);
        EtaText = EtaEstimator.FormatEta(remaining);
    }

    private void EndRun()
    {
        _cts?.Dispose();
        _cts = null;

        // T-045: the run is over (completed / cancelled / failed) — stop timing and clear the ETA so
        // the UI hides the label. This is the single clear point every terminal path funnels through.
        _stopwatch.Stop();
        EtaText = null;
    }

    private static UserFacingError MapException(Exception ex)
    {
        // Engines report ffmpeg failures via typed FfmpegResults (see RunWithResultAsync), but a
        // few exceptional paths still throw. Map the ones that carry ffmpeg diagnostics through
        // the signature mapper so they get a friendly headline too.
        switch (ex)
        {
            case SplitException sx:
                // A split ffmpeg failure carries the friendly headline in its message's first line
                // plus the full stderr + saved-log path. Surface all three so the error is copyable
                // and the "Open log" affordance lights up. The message already leads with the mapped
                // friendly headline (see SplitEngine), so keep it as the headline.
                return new UserFacingError(
                    ErrorCategory.Unknown,
                    HeadlineOf(sx.Message),
                    sx.FullStdErr ?? sx.Message,
                    "See the details for the raw output from ffmpeg.",
                    LogFilePath: sx.LogFilePath,
                    FullText: sx.FullStdErr);

            case FfprobeException fpx:
                return FfmpegErrorMapper.Map(fpx.StdErrTail, fpx.ExitCode);

            case FfmpegNotFoundException:
                return new UserFacingError(
                    ErrorCategory.BinaryNotFound,
                    "The ffmpeg tool could not be found.",
                    ex.Message,
                    "Make sure ffmpeg is installed and its location is configured.");

            case OperationCanceledException:
                // Handled by the caller's catch, but map defensively for completeness.
                return new UserFacingError(ErrorCategory.Cancelled, "The operation was cancelled.", ex.Message);
        }

        // Otherwise never surface the bare exception string as the headline — keep it as detail.
        return new UserFacingError(
            ErrorCategory.Unknown,
            "The operation failed unexpectedly.",
            ex.Message,
            "See the details for the underlying error.");
    }

    /// <summary>
    /// The friendly headline of a multi-line engine message: its FIRST line (the mapped headline the
    /// engine leads with), never the full stderr that follows. Falls back to a generic headline if the
    /// message is empty.
    /// </summary>
    private static string HeadlineOf(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The operation failed.";
        }

        var newline = message.IndexOfAny(new[] { '\r', '\n' });
        return newline >= 0 ? message.Substring(0, newline) : message;
    }
}
