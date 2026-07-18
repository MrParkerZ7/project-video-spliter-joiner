using System.Threading.Tasks;
using System.Windows.Shell;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Errors;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-068 unit tests for <see cref="OperationViewModel.TaskbarProgressState"/> — the Windows
/// taskbar-button progress state the MainWindow binds to. No ffmpeg / no GUI: fake work is driven
/// by awaitable gates so each lifecycle state (idle / running-indeterminate / running-fraction /
/// failed / completed / cancelled) is observed deterministically. The mapping under test:
/// idle→None, running+indeterminate→Indeterminate, running+fraction→Normal, failed→Error,
/// completed/cancelled→None; and it must clear back to None after a reset (no stuck bar).
/// </summary>
public sealed class TaskbarProgressStateTests
{
    private sealed record FakeResult(UserFacingError? Failure);

    [Fact]
    public void Idle_MapsToNone()
    {
        var vm = new OperationViewModel();

        vm.TaskbarProgressState.Should().Be(TaskbarItemProgressState.None,
            "a fresh/idle operation shows no taskbar fill");
    }

    [Fact]
    public async Task Running_WithNoFractionYet_MapsToIndeterminate()
    {
        var vm = new OperationViewModel();
        var gate = new TaskCompletionSource();

        var observed = TaskbarItemProgressState.None;
        var run = vm.RunAsync(async (_, _) =>
        {
            // No progress reported yet → indeterminate busy state.
            observed = vm.TaskbarProgressState;
            await gate.Task;
        }, "Splitting…");

        gate.SetResult();
        await run;

        observed.Should().Be(TaskbarItemProgressState.Indeterminate,
            "running with no usable fraction yet is the 'Preparing' pulse");
    }

    [Fact]
    public async Task Running_WithRealFraction_MapsToNormal()
    {
        var vm = new OperationViewModel();
        var gate = new TaskCompletionSource();

        var observed = TaskbarItemProgressState.None;
        var run = vm.RunAsync(async (progress, _) =>
        {
            progress.Report(0.5);
            // Let the marshalled Progress<T> callback apply on this thread.
            await Task.Delay(20);
            observed = vm.TaskbarProgressState;
            await gate.Task;
        }, "Splitting…");

        gate.SetResult();
        await run;

        observed.Should().Be(TaskbarItemProgressState.Normal,
            "a real fraction flips the taskbar from indeterminate to a determinate green fill");
    }

    [Fact]
    public async Task Failed_MapsToError()
    {
        var vm = new OperationViewModel();
        var failure = new UserFacingError(ErrorCategory.Unknown, "boom", "raw");

        await vm.RunWithResultAsync<FakeResult>(
            (_, _) => Task.FromResult(new FakeResult(failure)),
            r => r.Failure,
            "Splitting…");

        vm.State.Should().Be(OperationState.Failed);
        vm.TaskbarProgressState.Should().Be(TaskbarItemProgressState.Error,
            "a failed run shows the red taskbar state");
    }

    [Fact]
    public async Task Completed_MapsToNone()
    {
        var vm = new OperationViewModel();

        await vm.RunAsync((_, _) => Task.CompletedTask, "Splitting…");

        vm.State.Should().Be(OperationState.Completed);
        vm.TaskbarProgressState.Should().Be(TaskbarItemProgressState.None,
            "completion clears the taskbar fill — no stuck bar");
    }

    [Fact]
    public async Task Cancelled_MapsToNone()
    {
        var vm = new OperationViewModel();
        var gate = new TaskCompletionSource();

        var run = vm.RunAsync(async (_, token) =>
        {
            vm.CancelCommand.Execute(null);
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        }, "Splitting…");

        gate.SetResult();
        await run;

        vm.State.Should().Be(OperationState.Cancelled);
        vm.TaskbarProgressState.Should().Be(TaskbarItemProgressState.None,
            "a cancelled run clears the taskbar fill — no stuck bar");
    }

    [Fact]
    public async Task AfterFailure_ResetClearsToNone()
    {
        var vm = new OperationViewModel();
        var failure = new UserFacingError(ErrorCategory.Unknown, "boom", "raw");

        await vm.RunWithResultAsync<FakeResult>(
            (_, _) => Task.FromResult(new FakeResult(failure)),
            r => r.Failure,
            "Splitting…");
        vm.TaskbarProgressState.Should().Be(TaskbarItemProgressState.Error);

        vm.Reset();

        vm.TaskbarProgressState.Should().Be(TaskbarItemProgressState.None,
            "reset returns the operation to idle → taskbar cleared");
    }

    [Fact]
    public async Task RaisesPropertyChanged_OnStateAndProgressChanges()
    {
        var vm = new OperationViewModel();
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationViewModel.TaskbarProgressState))
            {
                raised++;
            }
        };

        await vm.RunAsync(async (progress, _) =>
        {
            progress.Report(0.5);
            await Task.Delay(20);
        }, "Splitting…");

        raised.Should().BeGreaterThan(0,
            "TaskbarProgressState change notifications fire as State/Progress move so the binding updates");
    }
}
