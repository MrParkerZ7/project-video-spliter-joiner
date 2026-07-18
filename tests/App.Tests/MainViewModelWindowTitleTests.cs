using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-068 tests for <see cref="MainViewModel.ComposeWindowTitle"/> — the OS window/taskbar title
/// overlay. It reads the plain base title when idle and overlays the short verb + overall % (and,
/// when present, the compact ETA) while an operation runs. Verb/% are deterministic; the ETA is
/// timing-derived so these assertions cover the idle + running-verb-% shape without ETA flakiness.
/// </summary>
public sealed class MainViewModelWindowTitleTests
{
    [Fact]
    public void Idle_IsPlainBaseTitle()
    {
        var op = new OperationViewModel();

        MainViewModel.ComposeWindowTitle(op).Should().Be(MainViewModel.BaseTitle);
    }

    [Fact]
    public void Null_IsPlainBaseTitle()
    {
        MainViewModel.ComposeWindowTitle(null).Should().Be(MainViewModel.BaseTitle);
    }

    [Fact]
    public async Task Running_OverlaysVerbAndPercent_AndKeepsBaseTitleSuffix()
    {
        var op = new OperationViewModel();
        var gate = new TaskCompletionSource();

        string titleMidRun = string.Empty;
        var run = op.RunAsync(async (progress, _) =>
        {
            op.StatusText = "Splitting… (4 parts)";
            progress.Report(0.5);
            await Task.Delay(20);
            titleMidRun = MainViewModel.ComposeWindowTitle(op);
            await gate.Task;
        }, "Splitting…");

        gate.SetResult();
        await run;

        titleMidRun.Should().StartWith("Splitting 50%",
            "the verb is stripped of its ellipsis/detail and the overall % is appended");
        titleMidRun.Should().EndWith("— " + MainViewModel.BaseTitle,
            "the plain base title stays as the suffix so the app name is still visible");
    }

    [Fact]
    public async Task AfterRun_RevertsToBaseTitle()
    {
        var op = new OperationViewModel();

        await op.RunAsync((_, _) => Task.CompletedTask, "Splitting…");

        MainViewModel.ComposeWindowTitle(op).Should().Be(MainViewModel.BaseTitle,
            "a completed/idle op reverts the title to the plain app name");
    }
}
