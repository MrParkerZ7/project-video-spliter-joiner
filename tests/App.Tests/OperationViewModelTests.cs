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
}
