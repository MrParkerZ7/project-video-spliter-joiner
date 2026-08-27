using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-123 (epic G-041) — the opt-in "replace originals" output mode. Because this is the one genuinely
/// destructive gesture in the app, the tests that matter most are the refusals: the default must be the
/// safe mode, and declining the confirmation must run absolutely nothing.
/// </summary>
public sealed class ReplaceOriginalModeTests
{
    private static (BulkCutViewModel Vm, BulkFakeProbe Probe, FakeBulkTrimEngine Engine) Build()
    {
        var probe = new BulkFakeProbe();
        var engine = new FakeBulkTrimEngine();
        var vm = new BulkCutViewModel(
            probe, new ThrowingFakeSplitEngine(), new FakeThumbnailService(), new FakeSettings(), engine);
        return (vm, probe, engine);
    }

    private static async Task AddValidRowAsync(BulkCutViewModel vm, BulkFakeProbe probe, string path)
    {
        probe.SetUniform(path, TimeSpan.FromSeconds(60), 2);
        await vm.AddFilesAsync(new[] { path });
        vm.Items.Single(i => i.Path == path).IntroEnd.Requested = TimeSpan.FromSeconds(10);
    }

    // ---- The safe default ---------------------------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TheDefaultIsTheNonDestructiveMode()
    {
        var (vm, _, _) = Build();

        vm.ReplaceOriginal.Should().BeFalse("a user who ignores this feature keeps every original");
        vm.CollisionIsInert.Should().BeFalse();
        vm.OutputNote.Should().Contain("_trimmed").And.Contain("originals kept");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public void TurningItOn_MakesTheCollisionControlInert_AndTheNoteTellsTheTruth()
    {
        var (vm, _, _) = Build();

        vm.ReplaceOriginal = true;

        vm.CollisionIsInert.Should().BeTrue("the collision policy cannot matter when the destination is the source");
        vm.OutputNote.Should().Contain("REPLACES").And.Contain("Recycle Bin",
            "the footer must state plainly what is about to happen — it can never contradict the mode");
    }

    // ---- The refusals (the load-bearing cases) -------------------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task DecliningTheConfirmation_RunsNothingAtAll()
    {
        var (vm, probe, engine) = Build();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");
        vm.ReplaceOriginal = true;

        var asked = 0;
        vm.ConfirmReplaceOriginals = _ => { asked++; return false; };

        await vm.RunBatchAsync();

        asked.Should().Be(1, "the user is asked exactly once, before anything runs");
        engine.CallCount.Should().Be(0, "declining performs ZERO engine calls — nothing is trimmed or replaced");
        vm.BatchState.Should().NotBe(BulkBatchState.Completed, "a declined batch never reports success");
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheConfirmationIsCounted_SoTheUserKnowsTheBlastRadius()
    {
        var (vm, probe, engine) = Build();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");
        await AddValidRowAsync(vm, probe, @"C:\v\b.mp4");
        await AddValidRowAsync(vm, probe, @"C:\v\c.mp4");
        vm.ReplaceOriginal = true;

        var reported = -1;
        vm.ConfirmReplaceOriginals = n => { reported = n; return false; };

        await vm.RunBatchAsync();

        reported.Should().Be(3, "the prompt names how many originals are at risk");
        engine.CallCount.Should().Be(0);
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task AnUnwiredHost_CannotDestroyAnything_TheDefaultRefuses()
    {
        var (vm, probe, engine) = Build();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");
        vm.ReplaceOriginal = true;

        // Deliberately do NOT wire ConfirmReplaceOriginals — the VM default must refuse.
        await vm.RunBatchAsync();

        engine.CallCount.Should().Be(0, "a host that forgot to supply a prompt must never silently replace masters");
    }

    // ---- Accepting: the mode actually reaches the engine ---------------------------------------

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task Accepting_PassesReplaceOriginalToTheEngine()
    {
        var (vm, probe, engine) = Build();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");
        vm.ReplaceOriginal = true;
        vm.ConfirmReplaceOriginals = _ => true;

        await vm.RunBatchAsync();

        engine.CallCount.Should().Be(1);
        engine.ReceivedOptions!.Output.Should().Be(OutputMode.ReplaceOriginal);
    }

    [Trait("serves-spec", "SPEC-011")]
    [Fact]
    public async Task TheDefaultModeNeverPrompts_AndSendsNewFile()
    {
        var (vm, probe, engine) = Build();
        await AddValidRowAsync(vm, probe, @"C:\v\a.mp4");

        var asked = 0;
        vm.ConfirmReplaceOriginals = _ => { asked++; return true; };

        await vm.RunBatchAsync();

        asked.Should().Be(0, "the safe mode must not nag");
        engine.ReceivedOptions!.Output.Should().Be(OutputMode.NewFile);
    }
}
