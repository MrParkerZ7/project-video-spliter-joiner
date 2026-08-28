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

    // ==== SPEC-015#I8 gaps (todo-automate) ===================================================
    //
    // While running the title is "{verb} {pct}% · {eta} — {BaseTitle}". The verb + base-title suffix
    // are covered above; these three pin the remaining clauses — the conditional ETA segment, the
    // empty-verb lead, and the clamp + away-from-zero rounding of the percent. All are driven through
    // the public run/progress/StatusText seams (EtaText has a private setter), so the ETA's exact
    // magnitude is left free and only its SHAPE ("· ~…", no " left" suffix) is asserted.

    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public async Task Compose_AppendsEtaOnlyWhenReal_AndDropsLeftSuffix()
    {
        var op = new OperationViewModel();
        var gate = new TaskCompletionSource();

        string beforeAnyFraction = string.Empty;
        string afterRealFraction = string.Empty;

        var run = op.RunAsync(async (progress, _) =>
        {
            // No usable fraction yet → EtaText is the "estimating…" placeholder, which does NOT start
            // with "~" → the "· {eta}" segment is omitted entirely.
            beforeAnyFraction = MainViewModel.ComposeWindowTitle(op);

            // A real fraction against measured elapsed yields a concrete "~N… left" estimate.
            await Task.Delay(30); // let the run's stopwatch advance (elapsed > 0)
            progress.Report(0.5);
            await Task.Delay(30); // let the marshalled Progress<T> post land
            afterRealFraction = MainViewModel.ComposeWindowTitle(op);

            await gate.Task;
        }, "Splitting… (4 parts)");

        gate.SetResult();
        await run;

        beforeAnyFraction.Should().Be("Splitting 0% — " + MainViewModel.BaseTitle,
            "the 'estimating…' placeholder is not a real ETA, so no '· …' segment is appended at all");
        beforeAnyFraction.Should().NotContain("·");

        afterRealFraction.Should().StartWith("Splitting 50% · ~",
            "a real '~… left' estimate is appended after the '· ' separator");
        afterRealFraction.Should().NotContain("left", "the ETA's trailing ' left' is dropped for the title");
        afterRealFraction.Should().EndWith("— " + MainViewModel.BaseTitle);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public async Task Compose_EmptyVerb_LeadsWithPercentOnly()
    {
        var op = new OperationViewModel();
        var gate = new TaskCompletionSource();

        string titleMidRun = string.Empty;
        var run = op.RunAsync(async (progress, _) =>
        {
            op.StatusText = string.Empty; // a blank status → ShortVerb is empty
            progress.Report(0.25);
            await Task.Delay(20);
            titleMidRun = MainViewModel.ComposeWindowTitle(op);
            await gate.Task;
        }, "Splitting…");

        gate.SetResult();
        await run;

        titleMidRun.Should().StartWith("25%",
            "with no verb the lead is just '{pct}%' — never a stray leading space where the verb would be");
        titleMidRun.Should().EndWith("— " + MainViewModel.BaseTitle,
            "the base title still trails, verb or no verb");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-015")]
    public async Task Compose_Percent_ClampsAndRoundsAwayFromZero()
    {
        var op = new OperationViewModel();
        var gate = new TaskCompletionSource();

        string tinyFraction = string.Empty;
        string aboveOne = string.Empty;

        var run = op.RunAsync(async (progress, _) =>
        {
            // 0.005 → 0.5% → rounded AWAY FROM ZERO to 1% (banker's rounding would show 0%).
            progress.Report(0.005);
            await Task.Delay(20);
            tinyFraction = MainViewModel.ComposeWindowTitle(op);

            // Out of range → clamped to 1.0 before the ×100.
            progress.Report(1.5);
            await Task.Delay(20);
            aboveOne = MainViewModel.ComposeWindowTitle(op);

            await gate.Task;
        }, "Trimming…");

        gate.SetResult();
        await run;

        tinyFraction.Should().StartWith("Trimming 1%",
            "0.5% rounds AWAY FROM ZERO to 1%, so a just-started run never reads a flat 0%");
        aboveOne.Should().StartWith("Trimming 100%",
            "a fraction above 1 is clamped before it becomes a percentage");
    }
}
