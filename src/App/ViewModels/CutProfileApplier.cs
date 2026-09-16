using System;
using System.Collections.Generic;
using VideoSplitJoiner.Core.Profiles;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// The pure apply/build logic for reusable cut profiles (G-037 / T-102): applies a saved
/// <see cref="CutProfile"/> to a set of Bulk Cut rows, and builds a profile from a row's current cut.
/// Kept as a small static helper (NOT bolted onto <see cref="BulkCutViewModel"/>) so T-103 can wire its
/// profile commands to it directly and it stays trivially unit-testable.
///
/// <para>Mirrors the T-096 apply-to-all convention exactly (<see cref="BulkCutViewModel.ApplyToAll"/>):
/// the intro is applied as an ABSOLUTE time-from-start, the outro FROM END (<c>Duration − tail</c>) so
/// uneven-length episodes align, each target re-snaps (via the <c>Requested</c> setter) + re-validates
/// against ITS OWN keyframes/duration, and rows the profile invalidates are REPORTED — never silently
/// dropped — through the SAME <see cref="ApplyToAllReport"/> shape (reused, not duplicated).</para>
///
/// <para>WPF-free — depends only on the (WPF-free) App view-models + Core/BCL, no PresentationFramework.</para>
/// </summary>
public static class CutProfileApplier
{
    /// <summary>
    /// Apply <paramref name="profile"/> to every target row whose duration is known — its keyframe scan may still be
    /// running (T-173): set the intro-end to
    /// <see cref="CutProfile.IntroFromStart"/> (clamped to <c>[0, Duration]</c>); if the profile carries an
    /// <see cref="CutProfile.OutroFromEnd"/> tail set the outro at <c>Duration − tail</c> (clamped, measured
    /// FROM END) else clear the outro. Each target re-snaps against its own keyframes and re-validates;
    /// a row the profile invalidates (intro overshoots, tail longer than the file) is collected into the
    /// returned <see cref="ApplyToAllReport.InvalidatedRows"/> — applied-to but flagged, not dropped.
    /// Rows with no duration yet (not probed, or the probe failed) are skipped: untouched, counted in
    /// <see cref="ApplyToAllReport.SkippedNotLoadedCount"/>, not as applied. Applied rows are classified by
    /// <see cref="ApplyOutcome"/> — a row still scanning is reported as waiting, or as invalid only when no snap can
    /// rescue it.
    /// </summary>
    /// <param name="profile">The cut profile to apply.</param>
    /// <param name="targets">The rows to apply it to.</param>
    /// <returns>An <see cref="ApplyToAllReport"/>: how many rows were applied to + which of those it invalidated.</returns>
    public static ApplyToAllReport ApplyProfile(CutProfile profile, IEnumerable<BulkItemViewModel> targets)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(targets);

        var outcome = new ApplyOutcome();

        foreach (var target in targets)
        {
            if (target is null)
            {
                continue;
            }

            if (!target.CanTakeCut)
            {
                outcome.SkipNotLoaded(); // no duration to clamp against yet — a still-scanning row is fine (T-173)
                continue;
            }

            var duration = target.Duration.GetValueOrDefault(); // CanTakeCut guarantees a value

            // Intro is ABSOLUTE from start, clamped into the file's bounds; the setter re-snaps to keyframes.
            target.IntroEnd.Requested = Clamp(profile.IntroFromStart, TimeSpan.Zero, duration);

            if (profile.OutroFromEnd is { } tail)
            {
                var outro = Clamp(duration - tail, TimeSpan.Zero, duration); // FROM END → uneven lengths align
                if (target.HasOutro)
                {
                    target.OutroStart!.Requested = outro;
                }
                else
                {
                    target.AddOutro(outro);
                }
            }
            else
            {
                target.ClearOutro(); // mirror the profile's no-outro shape
            }

            outcome.Applied(target);
        }

        return outcome.ToReport();
    }

    /// <summary>
    /// Build a <see cref="CutProfile"/> named <paramref name="name"/> from a row's CURRENT (requested) cut —
    /// the inverse of <see cref="ApplyProfile"/>: <see cref="CutProfile.IntroFromStart"/> is the row's
    /// requested intro-end, and <see cref="CutProfile.OutroFromEnd"/> is the tail measured FROM END
    /// (<c>Duration − requested outro-start</c>) when the row has an outro, else <c>null</c>. The
    /// <see cref="CutProfile"/> constructor validates the name/offsets.
    /// </summary>
    /// <param name="name">The name for the new profile (validated non-empty by <see cref="CutProfile"/>).</param>
    /// <param name="row">The row to capture the current cut from.</param>
    public static CutProfile BuildProfileFromRow(string name, BulkItemViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var intro = row.IntroEnd.Requested;
        TimeSpan? outro = row.HasOutro && row.Duration is { } duration
            ? duration - row.OutroStart!.Requested
            : null;

        return new CutProfile(name, intro, outro);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}

/// <summary>
/// Accumulates one apply gesture into an <see cref="ApplyToAllReport"/> (T-173): the ONE place an applied row is
/// classified, shared by <see cref="CutProfileApplier.ApplyProfile"/> and <see cref="BulkCutViewModel.ApplyToAll"/>
/// (and through it the set-at-playhead fan-out), so every copy path publishes the same outcome contract.
/// </summary>
internal sealed class ApplyOutcome
{
    private readonly List<BulkItemViewModel> _invalidated = new();
    private readonly List<BulkItemViewModel> _pendingSnap = new();
    private int _applied;
    private int _invalidStillScanning;
    private int _skippedNotLoaded;

    /// <summary>An in-scope target had no duration, so nothing was written to it.</summary>
    public void SkipNotLoaded() => _skippedNotLoaded++;

    /// <summary>Record a target the cut was just written to, classified by what is known at the click.</summary>
    public void Applied(BulkItemViewModel target)
    {
        _applied++;

        if (target.KeyframesReady)
        {
            if (!target.IsValidCut)
            {
                _invalidated.Add(target); // judged exactly as before T-173
            }
        }
        else if (target.IsCutHopelessBeforeSnap)
        {
            _invalidated.Add(target);     // no snap and no precision can rescue it…
            _invalidStillScanning++;      // …but it is not red until its scan lands
        }
        else
        {
            _pendingSnap.Add(target);     // the scan decides: Ready, no-op or red
        }
    }

    /// <summary>The finished report — the snapshot of this click.</summary>
    public ApplyToAllReport ToReport() =>
        new(_applied, _invalidated, _invalidStillScanning, _pendingSnap, _skippedNotLoaded);
}
