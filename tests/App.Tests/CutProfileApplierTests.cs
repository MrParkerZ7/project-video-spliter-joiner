using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Profiles;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// Unit tests for <see cref="CutProfileApplier"/> (T-102): applying a saved <see cref="CutProfile"/> to
/// Bulk Cut rows (intro ABSOLUTE-from-start, outro FROM END, per-row re-snap + re-validate, invalidated
/// rows REPORTED not dropped — through the reused <see cref="ApplyToAllReport"/> shape) and the inverse
/// <see cref="CutProfileApplier.BuildProfileFromRow"/>. Rows are built with the real-snap
/// <see cref="BulkFakeProbe"/> so validity/snap assertions exercise real logic.
/// </summary>
public sealed class CutProfileApplierTests
{
    private readonly BulkFakeProbe _probe = new();
    private readonly SemaphoreSlim _gate = new(3, 3);

    private async Task<BulkItemViewModel> MakeRowAsync(
        string path, double durationSeconds, double stepSeconds, double introSeconds, double? outroSeconds = null)
    {
        _probe.SetUniform(path, TimeSpan.FromSeconds(durationSeconds), stepSeconds);
        var row = new BulkItemViewModel(path, _probe, _gate) { Duration = TimeSpan.FromSeconds(durationSeconds) };
        await row.StartKeyframeScanAsync();
        row.IntroEnd.Requested = TimeSpan.FromSeconds(introSeconds);
        if (outroSeconds is double o)
        {
            row.AddOutro(TimeSpan.FromSeconds(o));
        }

        return row;
    }

    [Fact]
    public async Task ApplyProfile_AppliesIntroAbsolute_AndOutroFromEnd_PerRow()
    {
        var profile = new CutProfile("Series", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)); // tail = 10
        var shortRow = await MakeRowAsync(@"C:\v\a.mp4", 60, 2, introSeconds: 4);
        var longRow = await MakeRowAsync(@"C:\v\b.mp4", 100, 2, introSeconds: 4);

        var report = CutProfileApplier.ApplyProfile(profile, new[] { shortRow, longRow });

        shortRow.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10), "intro is applied ABSOLUTE (time-from-start)");
        shortRow.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(50), "outro is FROM END (60 − tail 10)");
        longRow.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(90), "outro is FROM END (100 − tail 10) — uneven lengths align");

        report.AppliedCount.Should().Be(2);
        report.InvalidatedRows.Should().BeEmpty("both cuts remain valid");
    }

    [Fact]
    public async Task ApplyProfile_ShorterTarget_IntroOvershoots_MarksInvalid_AndReportsIt_NotDropped()
    {
        var profile = new CutProfile("Long", TimeSpan.FromSeconds(80), null); // valid on a long episode
        var target = await MakeRowAsync(@"C:\v\b.mp4", 60, 2, introSeconds: 6);

        var report = CutProfileApplier.ApplyProfile(profile, new[] { target });

        target.IsValidCut.Should().BeFalse("intro 80 overshoots the 60s target (clamped to EOF ⇒ no kept span)");
        report.AppliedCount.Should().Be(1, "the row was applied-to");
        report.InvalidatedRows.Should().Contain(target, "an invalidated row is REPORTED, never silently dropped");
    }

    [Fact]
    public async Task ApplyProfile_NoOutroProfile_ClearsAnExistingOutro()
    {
        var profile = new CutProfile("Intro only", TimeSpan.FromSeconds(10), null);
        var target = await MakeRowAsync(@"C:\v\c.mp4", 100, 2, introSeconds: 4, outroSeconds: 80); // starts WITH an outro

        target.HasOutro.Should().BeTrue("precondition");

        var report = CutProfileApplier.ApplyProfile(profile, new[] { target });

        target.HasOutro.Should().BeFalse("a no-outro profile clears the row's outro (keep now runs to EOF)");
        target.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10));
        report.AppliedCount.Should().Be(1);
        report.InvalidatedRows.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyProfile_SkipsNotReadyRows_NotCountedAsApplied()
    {
        var profile = new CutProfile("P", TimeSpan.FromSeconds(10), null);
        var ready = await MakeRowAsync(@"C:\v\ready.mp4", 100, 2, introSeconds: 4);
        var notReady = new BulkItemViewModel(@"C:\v\pending.mp4", _probe, _gate); // no Duration / never scanned

        notReady.KeyframesReady.Should().BeFalse("precondition: an unprobed row is not ready");

        var report = CutProfileApplier.ApplyProfile(profile, new[] { ready, notReady });

        report.AppliedCount.Should().Be(1, "the not-ready row is skipped, not applied to");
        ready.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task BuildProfileFromRow_IsTheInverseOfApply()
    {
        var row = await MakeRowAsync(@"C:\v\src.mp4", 100, 2, introSeconds: 10, outroSeconds: 90);

        var profile = CutProfileApplier.BuildProfileFromRow("Captured", row);

        profile.Name.Should().Be("Captured");
        profile.IntroFromStart.Should().Be(TimeSpan.FromSeconds(10), "intro = the row's requested intro-end");
        profile.OutroFromEnd.Should().Be(TimeSpan.FromSeconds(10), "outro-from-end = Duration(100) − requested outro-start(90)");

        // Round-trip: applying the captured profile to a fresh equal-length row reproduces the cut.
        var fresh = await MakeRowAsync(@"C:\v\dst.mp4", 100, 2, introSeconds: 0);
        CutProfileApplier.ApplyProfile(profile, new[] { fresh });
        fresh.IntroEnd.Snapped.Should().Be(TimeSpan.FromSeconds(10));
        fresh.OutroStart!.Snapped.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task BuildProfileFromRow_NoOutro_ProducesNullOutro()
    {
        var row = await MakeRowAsync(@"C:\v\src.mp4", 100, 2, introSeconds: 12);

        var profile = CutProfileApplier.BuildProfileFromRow("NoOutro", row);

        profile.OutroFromEnd.Should().BeNull("a row without an outro captures a keep-to-EOF profile");
        profile.IntroFromStart.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void ApplyProfile_NullArgs_Throw()
    {
        var profile = new CutProfile("P", TimeSpan.Zero, null);

        ((Action)(() => CutProfileApplier.ApplyProfile(null!, Array.Empty<BulkItemViewModel>())))
            .Should().Throw<ArgumentNullException>();
        ((Action)(() => CutProfileApplier.ApplyProfile(profile, null!)))
            .Should().Throw<ArgumentNullException>();
    }
}
