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
    /// Apply <paramref name="profile"/> to every ready target row: set the intro-end to
    /// <see cref="CutProfile.IntroFromStart"/> (clamped to <c>[0, Duration]</c>); if the profile carries an
    /// <see cref="CutProfile.OutroFromEnd"/> tail set the outro at <c>Duration − tail</c> (clamped, measured
    /// FROM END) else clear the outro. Each target re-snaps against its own keyframes and re-validates;
    /// a row the profile invalidates (intro overshoots, tail longer than the file) is collected into the
    /// returned <see cref="ApplyToAllReport.InvalidatedRows"/> — applied-to but flagged, not dropped.
    /// Rows that are not keyframes-ready / have no probed duration are skipped (not counted as applied).
    /// </summary>
    /// <param name="profile">The cut profile to apply.</param>
    /// <param name="targets">The rows to apply it to.</param>
    /// <returns>An <see cref="ApplyToAllReport"/>: how many rows were applied to + which of those it invalidated.</returns>
    public static ApplyToAllReport ApplyProfile(CutProfile profile, IEnumerable<BulkItemViewModel> targets)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(targets);

        var applied = 0;
        var invalidated = new List<BulkItemViewModel>();

        foreach (var target in targets)
        {
            if (target is null || !target.KeyframesReady || target.Duration is not { } duration)
            {
                continue; // can't apply against an unprobed / still-indexing row
            }

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

            applied++;

            if (!target.IsValidCut)
            {
                invalidated.Add(target);
            }
        }

        return new ApplyToAllReport(applied, invalidated);
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
