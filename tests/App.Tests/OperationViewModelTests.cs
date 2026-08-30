using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for <see cref="OperationViewModel"/>. No ffmpeg, no GUI — fake work is driven by
/// awaitable <see cref="TaskCompletionSource"/> instances so state transitions, progress,
/// cancel, and error surfacing can be exercised deterministically on the test thread.
/// </summary>
public sealed class OperationViewModelTests
{
    private sealed record FakeResult(bool Ok, UserFacingError? Failure);

    [Fact]
    public async Task RunAsync_HappyPath_ProgressesToOne_AndCompletes()
    {
        var vm = new OperationViewModel();

        // Report progress via an explicit gate so the marshalled Progress<T> callbacks are
        // observed deterministically before the work returns (Progress<T> posts to the captured
        // sync context / thread pool asynchronously, so we must let those callbacks drain).
        double midProgress = -1;
        await vm.RunAsync(async (progress, _) =>
        {
            progress.Report(0.5);
            // Give the Progress<T> post a chance to be applied on this thread.
            await Task.Delay(20);
            midProgress = vm.Progress;
        }, "Working…");

        vm.State.Should().Be(OperationState.Completed);
        vm.Progress.Should().Be(1d, "completion clamps progress to 1");
        vm.IsRunning.Should().BeFalse();
        vm.CanCancel.Should().BeFalse();
        vm.Error.Should().BeNull();
        midProgress.Should().Be(0.5, "the reported mid-run progress was marshalled through Progress<T>");
    }

    [Fact]
    public async Task RunAsync_SetsRunningAndCanCancel_WhileInFlight()
    {
        var vm = new OperationViewModel();
        var gate = new TaskCompletionSource();

        bool runningObserved = false;
        bool canCancelObserved = false;

        var run = vm.RunAsync(async (_, token) =>
        {
            runningObserved = vm.State == OperationState.Running && vm.IsRunning;
            canCancelObserved = vm.CanCancel && vm.CancelCommand.CanExecute(null);
            await gate.Task;
        }, "Working…");

        // Let the work body start.
        await Task.Yield();
        runningObserved.Should().BeTrue();
        canCancelObserved.Should().BeTrue();

        gate.SetResult();
        await run;

        vm.State.Should().Be(OperationState.Completed);
        vm.CanCancel.Should().BeFalse();
        vm.CancelCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CancelCommand_DuringSlowWork_TransitionsToCancelled_NoErrorMapped()
    {
        var vm = new OperationViewModel();
        var started = new TaskCompletionSource();

        var run = vm.RunAsync(async (_, token) =>
        {
            started.SetResult();
            // Wait indefinitely until cancelled.
            await Task.Delay(Timeout.Infinite, token);
        }, "Working…");

        await started.Task;
        vm.CanCancel.Should().BeTrue();

        vm.CancelCommand.Execute(null);
        await run;

        vm.State.Should().Be(OperationState.Cancelled);
        vm.Error.Should().BeNull("cancellation is not a failure to map");
        vm.IsRunning.Should().BeFalse();
        vm.CanCancel.Should().BeFalse();
    }

    [Fact]
    public async Task RunWithResultAsync_FailureSelectorYieldsError_TransitionsToFailed()
    {
        var vm = new OperationViewModel();
        var mappedError = new UserFacingError(
            ErrorCategory.IncompatibleJoin,
            "These clips can't be joined directly.",
            "raw stderr tail");

        await vm.RunWithResultAsync(
            work: async (_, _) =>
            {
                await Task.Yield();
                return new FakeResult(Ok: false, Failure: mappedError);
            },
            failureSelector: r => r.Failure,
            runningStatus: "Joining…");

        vm.State.Should().Be(OperationState.Failed);
        vm.Error.Should().BeSameAs(mappedError);
        vm.IsRunning.Should().BeFalse();
        vm.CanCancel.Should().BeFalse();
    }

    [Fact]
    public async Task RunWithResultAsync_SuccessResult_TransitionsToCompleted()
    {
        var vm = new OperationViewModel();

        await vm.RunWithResultAsync(
            work: async (progress, _) =>
            {
                progress.Report(0.5);
                await Task.Yield();
                return new FakeResult(Ok: true, Failure: null);
            },
            failureSelector: r => r.Failure,
            runningStatus: "Splitting…");

        vm.State.Should().Be(OperationState.Completed);
        vm.Progress.Should().Be(1d);
        vm.Error.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_UnexpectedException_TransitionsToFailed_WithMappedError()
    {
        var vm = new OperationViewModel();

        await vm.RunAsync((_, _) => throw new InvalidOperationException("boom"), "Working…");

        vm.State.Should().Be(OperationState.Failed);
        vm.Error.Should().NotBeNull();
        vm.Error!.Category.Should().Be(ErrorCategory.Unknown);
        vm.Error.Message.Should().NotContain("boom", "the bare exception string must not be the headline");
        vm.Error.RawTail.Should().Contain("boom");
    }

    [Fact]
    public void CanCancel_IsFalse_WhenIdle()
    {
        var vm = new OperationViewModel();

        vm.State.Should().Be(OperationState.Idle);
        vm.CanCancel.Should().BeFalse();
        vm.CancelCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Reset_ReturnsToIdle_AndClearsErrorAndProgress()
    {
        var vm = new OperationViewModel();
        await vm.RunAsync((_, _) => throw new InvalidOperationException("boom"), "Working…");
        vm.State.Should().Be(OperationState.Failed);

        vm.Reset();

        vm.State.Should().Be(OperationState.Idle);
        vm.Error.Should().BeNull();
        vm.Progress.Should().Be(0d);
        vm.StatusText.Should().BeEmpty();
    }

    // ---- T-042: visible progress — status text + indeterminate busy state -------------------

    [Fact]
    public async Task WhileRunningWithNoProgress_IsIndeterminateTrue_AndStatusTextSet()
    {
        var vm = new OperationViewModel();
        var gate = new TaskCompletionSource();

        bool indeterminateObserved = false;
        string statusObserved = string.Empty;

        var run = vm.RunAsync(async (_, _) =>
        {
            // No progress reported yet → the bar should animate as a busy indicator.
            indeterminateObserved = vm.IsIndeterminate;
            statusObserved = vm.StatusText;
            await gate.Task;
        }, "Splitting…");

        await Task.Yield();
        indeterminateObserved.Should().BeTrue("running with no fraction reported yet → busy/indeterminate");
        statusObserved.Should().Be("Splitting…", "the running status is shown immediately on run");
        vm.IsRunning.Should().BeTrue();

        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task WhenRealFractionArrives_IsIndeterminateFlipsToFalse_AndProgressReflectsIt()
    {
        var vm = new OperationViewModel();

        double fractionSeen = -1;
        bool indeterminateAfterReport = true;

        await vm.RunAsync(async (progress, _) =>
        {
            vm.IsIndeterminate.Should().BeTrue("no progress reported yet");
            progress.Report(0.42);
            // Let the Progress<T> post drain on this thread.
            await Task.Delay(20);
            fractionSeen = vm.Progress;
            indeterminateAfterReport = vm.IsIndeterminate;
        }, "Splitting…");

        fractionSeen.Should().Be(0.42, "the reported fraction reaches the bound Progress property");
        indeterminateAfterReport.Should().BeFalse("a real fraction (>0) flips the bar to determinate");
    }

    [Fact]
    public async Task OnComplete_IsIndeterminateFalse_ProgressOne()
    {
        var vm = new OperationViewModel();

        await vm.RunAsync((_, _) => Task.CompletedTask, "Splitting…");

        vm.State.Should().Be(OperationState.Completed);
        vm.IsIndeterminate.Should().BeFalse("not running → never indeterminate");
        vm.Progress.Should().Be(1d);
    }

    [Fact]
    public async Task OnReset_IsIndeterminateFalse_AndStatusCleared()
    {
        var vm = new OperationViewModel();
        await vm.RunAsync((_, _) => throw new InvalidOperationException("boom"), "Splitting…");

        vm.Reset();

        vm.IsIndeterminate.Should().BeFalse();
        vm.StatusText.Should().BeEmpty();
    }

    [Fact]
    public void StatusText_IsPubliclySettable_ForStagedUpdates()
    {
        // T-044/T-045 extend StatusText per stage — verify the setter is public + notifies.
        var vm = new OperationViewModel();
        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StatusText)) raised = true; };

        vm.StatusText = "Stage 2 of 3…";

        vm.StatusText.Should().Be("Stage 2 of 3…");
        raised.Should().BeTrue("StatusText change must raise PropertyChanged so the UI updates");
    }

    // ---- T-045: ETA text wiring --------------------------------------------------------------

    [Fact]
    public async Task Indeterminate_BeforeAnyFraction_EtaTextIsEstimating()
    {
        var vm = new OperationViewModel();
        var gate = new TaskCompletionSource();

        string? etaObserved = null;
        bool indeterminateObserved = false;

        var run = vm.RunAsync(async (_, _) =>
        {
            // No fraction reported yet → indeterminate → ETA must read "estimating…", not a number.
            indeterminateObserved = vm.IsIndeterminate;
            etaObserved = vm.EtaText;
            await gate.Task;
        }, "Splitting…");

        await Task.Yield();
        indeterminateObserved.Should().BeTrue();
        etaObserved.Should().Be("estimating…", "no usable fraction yet → estimating, never a fake number");

        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task DuringRun_ProgressSamples_SetEtaTextNonEmpty_AndUpdate()
    {
        var vm = new OperationViewModel();

        var etaValues = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationViewModel.EtaText))
            {
                etaValues.Add(vm.EtaText);
            }
        };

        await vm.RunWithResultAsync(
            work: async (progress, _) =>
            {
                progress.Report(0.2);
                await Task.Delay(20);
                progress.Report(0.5);
                await Task.Delay(20);
                progress.Report(0.9);
                await Task.Delay(20);
                return new FakeResult(Ok: true, Failure: null);
            },
            failureSelector: r => r.Failure,
            runningStatus: "Splitting…");

        // While running, at least one progress sample produced a concrete "~…left" ETA label.
        etaValues.Should().Contain(v => v != null && v.Contains("left"),
            "a real fraction with measured elapsed yields a concrete ETA");

        // Cleared on completion.
        vm.State.Should().Be(OperationState.Completed);
        vm.EtaText.Should().BeNull("ETA is cleared when the run ends");
    }

    [Fact]
    public async Task EtaText_ClearsOnCancel()
    {
        var vm = new OperationViewModel();
        var started = new TaskCompletionSource();

        var run = vm.RunAsync(async (progress, token) =>
        {
            progress.Report(0.3);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, "Splitting…");

        await started.Task;
        vm.CancelCommand.Execute(null);
        await run;

        vm.State.Should().Be(OperationState.Cancelled);
        vm.EtaText.Should().BeNull("ETA is cleared on cancel");
    }

    [Fact]
    public async Task EtaText_ClearsOnReset()
    {
        var vm = new OperationViewModel();
        await vm.RunAsync((_, _) => throw new InvalidOperationException("boom"), "Splitting…");

        vm.Reset();

        vm.EtaText.Should().BeNull("ETA is cleared on Reset");
    }

    // ==== SPEC-008 gaps (todo-automate) ======================================================

    // SPEC-008#I11 — Progress is clamped to [0,1]: a reported value outside the range is bounded
    // before it is stored (BeginRun's Progress<double> → Math.Clamp(value,0,1)).
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task Progress_ReportedOutOfRange_IsClampedToBounds()
    {
        var vm = new OperationViewModel();

        double afterHigh = -1, afterLow = -1;
        await vm.RunAsync(async (progress, _) =>
        {
            progress.Report(1.5);        // above 1 → clamps to 1
            await Task.Delay(20);
            afterHigh = vm.Progress;

            progress.Report(-0.5);       // below 0 → clamps to 0
            await Task.Delay(20);
            afterLow = vm.Progress;
        }, "Working…");

        afterHigh.Should().Be(1d, "a reported fraction above 1 is clamped to the upper bound");
        afterLow.Should().Be(0d, "a reported fraction below 0 is clamped to the lower bound");
    }

    // SPEC-008#I23 — FormatStatus edge forms: a detail that itself ends in an ellipsis renders as
    // "Stage — detail" (sub-status) rather than "Stage… (detail)"; a null/blank stage → empty string.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task FormatStatus_EllipsisDetail_RendersAsSubStatus_AndBlankStageIsEmpty()
    {
        var vm = new OperationViewModel();

        string? subStatus = null;
        string? blankStage = null;

        await vm.RunWithResultAsync(
            work: async (_, status, _) =>
            {
                // A detail that is itself an "…ing…" phrase reads as a sub-status: "Stage — detail".
                status.Report(new OperationStatus("Preparing", "scanning keyframes…"));
                await Task.Delay(20);
                subStatus = vm.StatusText;

                // A null/blank stage collapses the whole line to empty.
                status.Report(new OperationStatus("   "));
                await Task.Delay(20);
                blankStage = vm.StatusText;

                return new FakeResult(Ok: true, Failure: null);
            },
            failureSelector: r => r.Failure,
            runningStatus: "Preparing…");

        subStatus.Should().Be("Preparing — scanning keyframes…",
            "an ellipsis-ending detail renders as a 'Stage — detail' sub-status, not 'Stage… (detail)'");
        blankStage.Should().Be(string.Empty, "a null/whitespace stage collapses the status line to empty");
    }

    // SPEC-008#I27 — SeedEstimatedDuration seeds the NEXT run's duration fallback, consumed once at
    // BeginRun and cleared so it never leaks into a later run.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task SeedEstimatedDuration_ConsumedOnceAndCleared_NoLeakIntoLaterRun()
    {
        var vm = new OperationViewModel();

        // Run 1 — seeded: with no usable fraction (a tiny sub-threshold fraction just below
        // MinUsableFraction, reported so it advances Progress and drives UpdateEta), the duration-based
        // fallback must fire → a concrete "~…left" appears while running.
        vm.SeedEstimatedDuration(TimeSpan.FromSeconds(120));

        var seededEtas = new List<string?>();
        void CaptureSeeded(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OperationViewModel.EtaText)) seededEtas.Add(vm.EtaText);
        }

        vm.PropertyChanged += CaptureSeeded;
        await vm.RunWithResultAsync(
            work: async (progress, _) =>
            {
                await Task.Delay(30);        // let the run's stopwatch advance (elapsed > 0)
                progress.Report(1e-7);       // sub-usable fraction → duration fallback path (not fraction-based)
                await Task.Delay(30);
                return new FakeResult(Ok: true, Failure: null);
            },
            failureSelector: r => r.Failure,
            runningStatus: "Splitting…");
        vm.PropertyChanged -= CaptureSeeded;

        seededEtas.Should().Contain(v => v != null && v.Contains("left"),
            "a seeded duration lets the fallback produce a concrete ETA before any usable fraction arrives");

        // Run 2 — NOT seeded: the seed was consumed once at the previous BeginRun and cleared, so the
        // fallback stays disabled and the ETA never leaves "estimating…".
        var unseededEtas = new List<string?>();
        void CaptureUnseeded(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OperationViewModel.EtaText)) unseededEtas.Add(vm.EtaText);
        }

        vm.PropertyChanged += CaptureUnseeded;
        await vm.RunWithResultAsync(
            work: async (progress, _) =>
            {
                await Task.Delay(30);
                progress.Report(1e-7);
                await Task.Delay(30);
                return new FakeResult(Ok: true, Failure: null);
            },
            failureSelector: r => r.Failure,
            runningStatus: "Splitting…");
        vm.PropertyChanged -= CaptureUnseeded;

        unseededEtas.Should().NotContain(v => v != null && v.Contains("left"),
            "the seed is consumed once and cleared — a later unseeded run never fires the fallback");
    }

    // SPEC-008#I27 (boundary) — a non-positive / null seed leaves the fallback disabled.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task SeedEstimatedDuration_ZeroOrNull_LeavesFallbackDisabled()
    {
        var vm = new OperationViewModel();
        vm.SeedEstimatedDuration(TimeSpan.Zero); // non-positive → ignored (fallback stays off)

        var etas = new List<string?>();
        void Capture(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OperationViewModel.EtaText)) etas.Add(vm.EtaText);
        }

        vm.PropertyChanged += Capture;
        await vm.RunWithResultAsync(
            work: async (progress, _) =>
            {
                await Task.Delay(30);
                progress.Report(1e-7);
                await Task.Delay(30);
                return new FakeResult(Ok: true, Failure: null);
            },
            failureSelector: r => r.Failure,
            runningStatus: "Splitting…");
        vm.PropertyChanged -= Capture;

        etas.Should().NotContain(v => v != null && v.Contains("left"),
            "a zero/non-positive seed leaves the duration fallback disabled (fraction-only behaviour)");
    }

    // SPEC-008#I9 (the "cancels any in-flight run" clause) — Reset() cancels the running operation's
    // token FIRST, then lands on Idle with Error / ResultSummary / Progress / StatusText / EtaText all
    // cleared. The body parks on a test-owned gate (never on the token) so nothing can race the
    // post-Reset assertions: the cancelled run only unwinds once the test releases it.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task Reset_WhileRunning_CancelsInFlightRun()
    {
        var vm = new OperationViewModel();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        CancellationToken captured = default;

        var run = vm.RunAsync(async (progress, token) =>
        {
            captured = token;
            vm.ResultSummary = "a line left over from this run";
            progress.Report(0.4);
            started.SetResult();
            await release.Task;                   // only the test releases the body…
            token.ThrowIfCancellationRequested(); // …which then observes Reset's cancel
        }, "Working…");

        await started.Task;
        await Task.Delay(20); // let the marshalled Progress<T> post land
        vm.IsRunning.Should().BeTrue("precondition — the run is in flight");
        vm.Progress.Should().Be(0.4);

        vm.Reset();

        captured.IsCancellationRequested.Should().BeTrue(
            "Reset cancels the in-flight run before resetting — it never abandons a running operation");
        vm.State.Should().Be(OperationState.Idle, "Reset lands on Idle without waiting for the body to unwind");
        vm.Progress.Should().Be(0d, "Reset zeroes the bar");
        vm.StatusText.Should().BeEmpty("Reset clears the status line");
        vm.EtaText.Should().BeNull("Reset clears the ETA");
        vm.Error.Should().BeNull();
        vm.ResultSummary.Should().BeNull("Reset drops any lingering success line");

        release.SetResult();
        await run;

        vm.State.Should().Be(OperationState.Cancelled,
            "the cancelled body funnels through RunAsync's OperationCanceledException path");
        vm.EtaText.Should().BeNull("EndRun leaves the ETA cleared on every terminal path");
    }

    // SPEC-008#I2 (the Progress-reset clause) — BeginRun zeroes Progress BEFORE the work body runs.
    // The sibling clauses are pinned elsewhere (Running/CanCancel, StatusText, Error, ResultSummary);
    // the bar reset is the one that is only observable from INSIDE a second run's body, because the
    // previous run left Progress clamped at 1. Without it a re-run would open on a stale full bar.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task SecondRun_ResetsProgressToZero_BeforeWorkBody()
    {
        var vm = new OperationViewModel();

        await vm.RunAsync((_, _) => Task.CompletedTask, "Working…");
        vm.Progress.Should().Be(1d, "precondition — the finished run left the bar full");

        double progressAtStart = -1;
        var indeterminateAtStart = false;
        var gate = new TaskCompletionSource();

        var run = vm.RunAsync(async (_, _) =>
        {
            progressAtStart = vm.Progress;
            indeterminateAtStart = vm.IsIndeterminate;
            await gate.Task;
        }, "Working…");

        await Task.Yield();
        progressAtStart.Should().Be(0d, "BeginRun resets the bar before the work body runs");
        indeterminateAtStart.Should().BeTrue(
            "a fresh run reopens as an indeterminate busy pulse, not a stale full bar");

        gate.SetResult();
        await run;
    }

    // ==== SPEC-008 I41-I44: out-of-band failure reporting — ReportFailure (T-129) =============

    // The gesture this seam exists for (SPEC-007's Bulk Cut profile-thumbnail upload) is NOT a tracked
    // run: it has no progress, no ETA and nothing to cancel. The cases below pin the four rules that let
    // it borrow the run's error surface without borrowing the run's lifecycle. Mid-run state is observed
    // through the same seam the Reset-while-running case uses: the work body parks on a test-owned gate,
    // so the report lands with the run genuinely in flight and nothing racing the assertions.
    //
    // Every case also carries the structural performance contract — reporting does no I/O and starts no
    // task: the call is synchronous, marshals nothing through the captured SynchronizationContext (a run's
    // Progress<T> channels and any deferred work would), and its notification cost is a bounded O(1) set
    // that collapses to ZERO when the report changes nothing.

    /// <summary>
    /// Counts every callback a call posts/sends to the captured context. A started run marshals its
    /// <see cref="Progress{T}"/> samples through exactly this seam, so a zero count is the structural
    /// signature of "no run started, nothing scheduled".
    /// </summary>
    private sealed class CountingSyncContext : SynchronizationContext
    {
        public int Posts { get; private set; }

        public int Sends { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Posts++;
            base.Post(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Sends++;
            base.Send(d, state);
        }
    }

    /// <summary>The kind of friendly failure the profile-thumbnail upload composes and reports.</summary>
    private static UserFacingError UploadFailure(string message = "That image could not be read.") =>
        new(ErrorCategory.CorruptInput, message, @"C:\pics\cover.png", "Pick a different image.");

    // SPEC-008#I41 — ReportFailure(error) with NO run in flight puts the failure on the SAME surface a
    // failed run uses: Error becomes it and State moves to Failed, so the Completed/Cancelled surfaces
    // drop (I18) and the taskbar goes red (I13). A deliberate gesture never fails silently.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public void ReportFailure_NoRunInFlight_OwnsTheErrorSurface_AndMovesToFailed()
    {
        var vm = new OperationViewModel();
        var error = UploadFailure();

        vm.ReportFailure(error);

        vm.Error.Should().BeSameAs(error, "the reported failure IS the surface's error, not a copy of it");
        vm.State.Should().Be(OperationState.Failed, "an untracked gesture's failure still owns the error surface");
        vm.IsRunning.Should().BeFalse("no run was started");
        vm.IsCompleted.Should().BeFalse();
        vm.IsCancelled.Should().BeFalse("Failed sets no terminal bool — it is surfaced via Error (I18)");
        vm.CanCancel.Should().BeFalse("there is nothing to cancel");
        vm.CancelCommand.CanExecute(null).Should().BeFalse();
        vm.TaskbarProgressState.Should().Be(System.Windows.Shell.TaskbarItemProgressState.Error,
            "Failed is checked first, so the taskbar button goes red (I13)");

        // Performance (structural): re-reporting the SAME failure is a pure no-op — the SetProperty
        // equality guards mean zero further notifications, so a caller that reports on every retry of a
        // gesture costs nothing per repeat.
        var raisedAfter = 0;
        vm.PropertyChanged += (_, _) => raisedAfter++;

        vm.ReportFailure(error);

        raisedAfter.Should().Be(0, "an identical re-report changes nothing and therefore notifies nothing");
    }

    // SPEC-008#I41 (the stale-success clause) — reporting after a COMPLETED run clears ResultSummary and
    // drops the Completed surface, so a green "done" line never sits beside a fresh red error.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task ReportFailure_AfterCompletedRun_ClearsTheStaleSuccessLine()
    {
        var vm = new OperationViewModel();
        await vm.RunAsync((_, _) => Task.CompletedTask, "Working…");
        vm.ResultSummary = "Split into 3 parts"; // the producing VM's success line
        vm.IsCompleted.Should().BeTrue("precondition — the finished run owns the success surface");

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        var error = UploadFailure();
        vm.ReportFailure(error);

        vm.Error.Should().BeSameAs(error);
        vm.State.Should().Be(OperationState.Failed);
        vm.ResultSummary.Should().BeNull("a stale green done-line must not sit beside a fresh red error");
        vm.IsCompleted.Should().BeFalse("the Completed surface drops the moment the error takes over (I18)");
        vm.TaskbarProgressState.Should().Be(System.Windows.Shell.TaskbarItemProgressState.Error);

        // Performance (structural) + I44: no run machinery ran — the finished run's bar is left exactly
        // where it was, and not one progress / status / ETA notification fired.
        vm.Progress.Should().Be(1d, "ReportFailure starts no run, so it never resets the bar");
        raised.Should().NotContain(nameof(OperationViewModel.Progress));
        raised.Should().NotContain(nameof(OperationViewModel.StatusText));
        raised.Should().NotContain(nameof(OperationViewModel.EtaText));
    }

    // SPEC-008#I42 — ReportFailure WHILE a run is in flight sets Error and NOTHING else: State stays
    // Running, ResultSummary is left alone, and the run keeps its own lifecycle (CanCancel still true).
    // A side gesture cannot derail a tracked run.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task ReportFailure_WhileRunning_SetsErrorOnly_AndDoesNotDerailTheRun()
    {
        var vm = new OperationViewModel();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        CancellationToken captured = default;

        var run = vm.RunAsync(async (progress, token) =>
        {
            captured = token;
            vm.ResultSummary = "a line this run set for itself";
            progress.Report(0.4);
            started.SetResult();
            await release.Task;    // only the test releases the body — nothing races the assertions
        }, "Splitting…");

        await started.Task;
        await Task.Delay(20); // let the marshalled Progress<T> post land
        vm.IsRunning.Should().BeTrue("precondition — the run is in flight");
        vm.Progress.Should().Be(0.4, "precondition — the run's own fraction has already landed");

        // Subscribed only AFTER the run's own sample settled, so every name captured below belongs to
        // the report and to nothing else.
        var etaBefore = vm.EtaText;
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        var error = UploadFailure();
        vm.ReportFailure(error);

        vm.Error.Should().BeSameAs(error, "the side gesture's message appears beside the running operation");
        vm.State.Should().Be(OperationState.Running, "the run owns its State — a side gesture must not move it");
        vm.IsRunning.Should().BeTrue();
        vm.CanCancel.Should().BeTrue("the run stays cancellable while the reported error is shown");
        vm.CancelCommand.CanExecute(null).Should().BeTrue();
        vm.ResultSummary.Should().Be("a line this run set for itself",
            "only the no-run branch clears the summary — a run's own line is left alone");
        captured.IsCancellationRequested.Should().BeFalse("reporting never cancels the run it sits beside");

        // Performance (structural) + I44: purely additive — the run's own channels are untouched, so the
        // bar, the status line and the ETA neither move nor notify. Error is the ONLY notification.
        vm.Progress.Should().Be(0.4, "the report does not touch the bar");
        vm.StatusText.Should().Be("Splitting…", "nor the status line");
        vm.EtaText.Should().Be(etaBefore, "nor the ETA");
        raised.Should().OnlyContain(n => n == nameof(OperationViewModel.Error),
            "a mid-run report raises Error and nothing else — no State block, no run machinery re-entered");

        release.SetResult();
        await run;

        // I42 (tail): the run still ends in its OWN terminal state, and its end does not clear the error.
        vm.State.Should().Be(OperationState.Completed, "the run finished on its own terms");
        vm.Error.Should().BeSameAs(error, "Complete/EndRun leave a side-reported error standing (I42)");
        vm.EtaText.Should().BeNull("the run's own EndRun still cleared the ETA (I26)");
    }

    // SPEC-008#I42 (survival clause) — a mid-run report survives the run's own end (here the CANCELLED
    // path, where the run itself maps no error at all — I7) and stays on the terminal surface until the
    // next BeginRun clears it (I2).
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task ReportFailure_WhileRunning_SurvivesTheRunsEnd_UntilTheNextRunClearsIt()
    {
        var vm = new OperationViewModel();
        var started = new TaskCompletionSource();

        var run = vm.RunAsync(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, "Splitting…");

        await started.Task;

        var error = UploadFailure("The thumbnail could not be saved.");
        vm.ReportFailure(error);
        vm.State.Should().Be(OperationState.Running, "precondition — the report left the run running");

        vm.CancelCommand.Execute(null);
        await run;

        vm.State.Should().Be(OperationState.Cancelled, "the run still ends in its own terminal state");
        vm.IsCancelled.Should().BeTrue("the run's neutral cancelled surface is the one that lights up");
        vm.Error.Should().BeSameAs(error,
            "the run's end never clears a side-reported error — it survives onto the terminal surface (I42)");

        // I2/I42: the NEXT run is what clears it — observed from inside the new work body.
        string? errorAtStart = "unset";
        var gate = new TaskCompletionSource();
        var next = vm.RunAsync(async (_, _) =>
        {
            errorAtStart = vm.Error?.Message;
            await gate.Task;
        }, "Splitting…");

        await Task.Yield();
        errorAtStart.Should().BeNull("BeginRun clears the standing error before the new work body runs");

        gate.SetResult();
        await next;

        // Performance (structural): the report scheduled no work of its own — one cancel ended exactly one
        // run, and the next run behaves like any other (no half-open run left behind to poison it).
        vm.State.Should().Be(OperationState.Completed, "the second run completed normally");
        vm.Error.Should().BeNull();
        vm.EtaText.Should().BeNull();
    }

    // SPEC-008#I43 — ReportFailure(null) RETRACTS a previously reported failure: Error goes back to null
    // and a State of Failed returns to Idle, so no red taskbar lingers with nothing left to explain it
    // (I13/I14). The ResultSummary the earlier report cleared is NOT restored.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task ReportFailure_Null_RetractsTheFailure_AndReturnsFailedToIdle()
    {
        var vm = new OperationViewModel();
        await vm.RunAsync((_, _) => Task.CompletedTask, "Working…");
        vm.ResultSummary = "Split into 3 parts";
        vm.ReportFailure(UploadFailure());
        vm.State.Should().Be(OperationState.Failed, "precondition — the reported failure owns the surface");

        vm.ReportFailure(null);

        vm.Error.Should().BeNull("a later successful gesture retracts the message it reported");
        vm.State.Should().Be(OperationState.Idle,
            "and drops the surface out of Failed — no red taskbar with nothing left to explain it");
        vm.TaskbarProgressState.Should().Be(System.Windows.Shell.TaskbarItemProgressState.None,
            "a not-running state clears the taskbar bar (I14)");
        vm.ResultSummary.Should().BeNull("the summary the report cleared is NOT restored by the retraction");
        vm.IsCompleted.Should().BeFalse("the retraction lands on Idle, not back on the old Completed surface");

        // Performance (structural) + I44: retracting is as cheap as reporting — no run started, the bar
        // left exactly where the finished run put it, and a repeat retraction notifies nothing at all.
        vm.Progress.Should().Be(1d, "the retraction starts no run, so it never resets the bar");
        var raisedAfter = 0;
        vm.PropertyChanged += (_, _) => raisedAfter++;

        vm.ReportFailure(null);

        raisedAfter.Should().Be(0, "retracting when there is nothing to retract changes nothing");
    }

    // SPEC-008#I43 (the leave-alone clause) — a retraction touches State ONLY when it is Failed. Idle,
    // Completed and Cancelled are left exactly as they are; only the error line clears.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task ReportFailure_Null_LeavesEveryNonFailedStateExactlyAsItIs()
    {
        // Idle — nothing to retract, nothing moves.
        var idle = new OperationViewModel();
        idle.ReportFailure(null);
        idle.State.Should().Be(OperationState.Idle);
        idle.Error.Should().BeNull();

        // Completed — the success surface survives the retraction (summary and full bar included).
        var completed = new OperationViewModel();
        await completed.RunAsync((_, _) => Task.CompletedTask, "Working…");
        completed.ResultSummary = "Split into 3 parts";

        completed.ReportFailure(null);

        completed.State.Should().Be(OperationState.Completed, "only Failed is rewound by a retraction");
        completed.IsCompleted.Should().BeTrue("the success surface is not collateral damage");
        completed.ResultSummary.Should().Be("Split into 3 parts", "nor is the success line");
        completed.Progress.Should().Be(1d);

        // Cancelled — the neutral surface likewise survives.
        var cancelled = new OperationViewModel();
        var started = new TaskCompletionSource();
        var run = cancelled.RunAsync(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, "Working…");

        await started.Task;
        cancelled.CancelCommand.Execute(null);
        await run;

        cancelled.ReportFailure(null);

        cancelled.State.Should().Be(OperationState.Cancelled,
            "a retraction never turns a cancelled run into an idle one");
        cancelled.IsCancelled.Should().BeTrue();
        cancelled.Error.Should().BeNull();

        // Performance (structural): each retraction is a single guarded assignment — with nothing to
        // retract, not one notification fires on any of the three surfaces.
        var raised = 0;
        idle.PropertyChanged += (_, _) => raised++;
        completed.PropertyChanged += (_, _) => raised++;
        cancelled.PropertyChanged += (_, _) => raised++;

        idle.ReportFailure(null);
        completed.ReportFailure(null);
        cancelled.ReportFailure(null);

        raised.Should().Be(0, "a no-op retraction costs nothing on any surface");
    }

    // SPEC-008#I43 (in-flight clause) — retracting WHILE a run is in flight clears the error line only;
    // the run is left exactly as it is and finishes on its own terms.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task ReportFailure_Null_WhileRunning_ClearsTheErrorLine_WithoutTouchingTheRun()
    {
        var vm = new OperationViewModel();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var run = vm.RunAsync(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
        }, "Splitting…");

        await started.Task;
        vm.ReportFailure(UploadFailure());
        vm.Error.Should().NotBeNull("precondition — a side gesture reported a failure mid-run");

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.ReportFailure(null);

        vm.Error.Should().BeNull("the retraction clears the line it put up");
        vm.State.Should().Be(OperationState.Running, "a run in flight is left exactly as it is");
        vm.IsRunning.Should().BeTrue();
        vm.CanCancel.Should().BeTrue("the run keeps its own lifecycle through both the report and the retraction");

        // Performance (structural): one notification, nothing else — the run's machinery is not re-entered.
        raised.Should().OnlyContain(n => n == nameof(OperationViewModel.Error),
            "an in-flight retraction raises Error alone — no State block, no progress/status/ETA churn");

        release.SetResult();
        await run;

        vm.State.Should().Be(OperationState.Completed, "the run finished on its own terms");
        vm.Error.Should().BeNull();
    }

    // SPEC-008#I43 (scope) — the retraction is deliberately NOT reference-scoped inside this VM: any
    // Failed state rewinds to Idle, whoever set it. Callers are the ones expected to retract only the
    // error they themselves reported (SPEC-007 I71 — BulkCutViewModel does that reference check before
    // calling here). Pinned so the responsibility stays where the spec puts it.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task ReportFailure_Null_AfterARunFailure_AlsoReturnsToIdle()
    {
        var vm = new OperationViewModel();
        await vm.RunAsync((_, _) => throw new InvalidOperationException("boom"), "Working…");
        vm.State.Should().Be(OperationState.Failed, "precondition — the RUN failed and owns the surface");

        vm.ReportFailure(null);

        vm.Error.Should().BeNull("the VM-level retraction clears whatever error is standing…");
        vm.State.Should().Be(OperationState.Idle, "…and rewinds any Failed state, not only a reported one");

        // Performance (structural) + I44: the rewind is a pair of guarded field writes — it starts no run
        // and re-does nothing the failed run's own EndRun already did.
        vm.Progress.Should().Be(0d, "the failed run never advanced the bar, and the retraction does not either");
        vm.EtaText.Should().BeNull("EndRun had already cleared the ETA (I26) — nothing to redo");
        vm.StatusText.Should().Be("Working…",
            "ReportFailure never touches the status line — only Reset clears it (I9)");
    }

    // SPEC-008#I44 — ReportFailure is purely ADDITIVE: it starts no run and ends none, so Progress,
    // StatusText, EtaText, the stopwatch, the estimator and the run's CancellationTokenSource are all left
    // as they were (neither BeginRun nor EndRun runs). From a fresh VM that means Progress == 0,
    // EtaText == null and CanCancel == false even though the state moved to Failed.
    //
    // Performance (structural — no I/O, no task started): the call is fully synchronous and marshals
    // NOTHING through the captured SynchronizationContext. That is the structural signature of "no run
    // started" — BeginRun's Progress<double> / Progress<OperationStatus> channels post through exactly
    // this seam, as would any deferred or async work.
    [Fact]
    [Trait("serves-spec", "SPEC-008")]
    public async Task ReportFailure_StartsNoRunAndEndsNone_MarshallingNothing()
    {
        var vm = new OperationViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        var probe = new CountingSyncContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(probe);
        try
        {
            vm.ReportFailure(UploadFailure()); // synchronous — no await inside the probed window
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        // Correctness: the failure surfaced, and nothing that belongs to a RUN moved.
        vm.State.Should().Be(OperationState.Failed);
        vm.Progress.Should().Be(0d, "no run started, so the bar is never touched");
        vm.StatusText.Should().BeEmpty("no running status was set — ReportFailure has no status of its own");
        vm.EtaText.Should().BeNull("a gesture with no duration seeds no ETA (BeginRun never ran)");
        vm.IsIndeterminate.Should().BeFalse("not running → never the busy pulse");
        vm.CanCancel.Should().BeFalse("no CancellationTokenSource was created, so there is nothing to cancel");
        vm.CancelCommand.CanExecute(null).Should().BeFalse();
        raised.Should().NotContain(nameof(OperationViewModel.Progress));
        raised.Should().NotContain(nameof(OperationViewModel.StatusText));
        raised.Should().NotContain(nameof(OperationViewModel.EtaText));

        // Performance (structural): nothing was marshalled or scheduled — no I/O, no task, no timer.
        probe.Posts.Should().Be(0,
            "a started run marshals through the captured context; reporting a failure starts none");
        probe.Sends.Should().Be(0, "nor does it send anything synchronously through the context");

        // …and the VM is still a healthy, never-started operation: the very next run opens exactly like a
        // first run (proof that no half-open run, stopwatch or estimator was left behind).
        await vm.RunAsync(async (progress, _) =>
        {
            vm.IsIndeterminate.Should().BeTrue("the next run opens as a fresh busy pulse, not a stale bar");
            vm.Error.Should().BeNull("BeginRun clears the reported failure like any other error (I2)");
            progress.Report(0.5);
            await Task.Delay(20);
        }, "Splitting…");

        vm.State.Should().Be(OperationState.Completed, "the report left nothing half-open behind it");
        vm.Progress.Should().Be(1d);
        vm.EtaText.Should().BeNull();
    }
}
