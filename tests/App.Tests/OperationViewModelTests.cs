using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Errors;
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
}
