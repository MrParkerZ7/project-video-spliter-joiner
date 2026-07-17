using System;
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
    private UserFacingError? _error;
    private CancellationTokenSource? _cts;

    public OperationViewModel()
    {
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
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
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(IsIndeterminate));
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

    /// <summary>The friendly error when <see cref="State"/> is <see cref="OperationState.Failed"/>; otherwise null.</summary>
    public UserFacingError? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>True while an operation is running.</summary>
    public bool IsRunning => State == OperationState.Running;

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
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(failureSelector);

        BeginRun(runningStatus, out var progress, out var token);
        try
        {
            var result = await work(progress, token).ConfigureAwait(true);
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
        Progress = 0;
        StatusText = string.Empty;
        State = OperationState.Idle;
    }

    private void Cancel()
    {
        _cts?.Cancel();
    }

    private void BeginRun(string runningStatus, out IProgress<double> progress, out CancellationToken token)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        token = _cts.Token;

        Error = null;
        Progress = 0;
        StatusText = runningStatus;
        State = OperationState.Running;

        // Progress<T> captures the current SynchronizationContext (the UI thread in a real app,
        // the test thread under xUnit) and posts updates back to it.
        progress = new Progress<double>(value => Progress = Math.Clamp(value, 0d, 1d));
    }

    private void Complete()
    {
        Progress = 1d;
        State = OperationState.Completed;
    }

    private void EndRun()
    {
        _cts?.Dispose();
        _cts = null;
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
