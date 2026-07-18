using System;
using System.Globalization;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// The live write-state of a split part row (T-069), driven by the engine's per-part progress
/// channel as the split runs. Drives the per-row progress affordance in the "Parts to export" list.
/// </summary>
public enum PartRowState
{
    /// <summary>Not yet written (or not selected for export) — neutral, no fill.</summary>
    Pending,

    /// <summary>Currently being written — the row shows a live 0..1 <see cref="SplitSegmentViewModel.PartFraction"/> fill, gold-accented.</summary>
    Writing,

    /// <summary>Finished writing — the row shows a "done" check.</summary>
    Done,
}

/// <summary>
/// One contiguous part of a planned split (T-049), projected from the cut markers + file duration.
/// The parts are the ordered ranges <c>[0..s1], [s1..s2], …, [sN..end]</c>; each carries its 1-based
/// <see cref="Index"/>, its <see cref="Start"/>/<see cref="End"/>/<see cref="Duration"/>, a
/// human-readable <see cref="Display"/> (e.g. <c>"Part 2 · 05:00–10:00 · 5:00"</c>), and an
/// observable <see cref="IsSelected"/> (default true). Only selected parts are written when the
/// split runs; the ORIGINAL <see cref="Index"/> is kept in the output filename, so a selected middle
/// part stays identifiable (<c>…_part02</c>). Deliberately WPF-free so it is fully unit-testable.
/// </summary>
public sealed class SplitSegmentViewModel : ObservableObject
{
    private bool _isSelected;
    private PartRowState _writeState = PartRowState.Pending;
    private double _partFraction;

    /// <summary>
    /// Create a segment row. <paramref name="index"/> is the part's 1-based position in the full
    /// contiguous plan (kept even when a subset is selected). <paramref name="isSelected"/> defaults
    /// to true so every part is exported unless the user unchecks it.
    /// </summary>
    public SplitSegmentViewModel(int index, TimeSpan start, TimeSpan end, bool isSelected = true)
    {
        Index = index;
        Start = start;
        End = end;
        _isSelected = isSelected;
    }

    /// <summary>1-based part number in the full plan (never renumbered by selection).</summary>
    public int Index { get; }

    /// <summary>Snapped start boundary of this part (zero for the first part).</summary>
    public TimeSpan Start { get; }

    /// <summary>Snapped end boundary of this part (the file duration for the last part).</summary>
    public TimeSpan End { get; }

    /// <summary>Length of this part (<c>End − Start</c>).</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>
    /// Whether this part is included in the export (T-049). Observable so the checkbox two-way binds
    /// and the owning VM can recompute the selected count / Run guard when it toggles.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// The live write-state of this part during a split (T-069): <see cref="PartRowState.Pending"/>
    /// before/while unwritten, <see cref="PartRowState.Writing"/> while it is the active part (with a
    /// live <see cref="PartFraction"/>), <see cref="PartRowState.Done"/> once written. Observable so
    /// the row's inline progress affordance updates as the engine reports per-part progress. Reset to
    /// <see cref="PartRowState.Pending"/> at the start of each run (see <see cref="ResetProgress"/>).
    /// </summary>
    public PartRowState WriteState
    {
        get => _writeState;
        private set
        {
            if (SetProperty(ref _writeState, value))
            {
                OnPropertyChanged(nameof(IsWriting));
                OnPropertyChanged(nameof(IsDone));
            }
        }
    }

    /// <summary>
    /// Local progress of THIS part (0..1) while it is being written (T-069) — the width of the row's
    /// inline fill. Meaningful only while <see cref="WriteState"/> is <see cref="PartRowState.Writing"/>;
    /// 0 when pending, forced to 1 when done. Observable.
    /// </summary>
    public double PartFraction
    {
        get => _partFraction;
        private set => SetProperty(ref _partFraction, Math.Clamp(value, 0d, 1d));
    }

    /// <summary>True while this row is the actively-writing part — drives the gold live-fill affordance.</summary>
    public bool IsWriting => WriteState == PartRowState.Writing;

    /// <summary>True once this row's part has been fully written — drives the "done" check.</summary>
    public bool IsDone => WriteState == PartRowState.Done;

    /// <summary>
    /// Mark this row as the actively-writing part at local <paramref name="fraction"/> (0..1) (T-069).
    /// Called by the owning VM as the engine reports the current part's progress.
    /// </summary>
    public void MarkWriting(double fraction)
    {
        PartFraction = fraction;
        WriteState = PartRowState.Writing;
    }

    /// <summary>Mark this row's part fully written (T-069): state Done, fraction pinned to 1.</summary>
    public void MarkDone()
    {
        PartFraction = 1d;
        WriteState = PartRowState.Done;
    }

    /// <summary>Mark this row not-yet-written (T-069): state Pending, fraction 0. Used for the pre-run reset and for parts a run skips.</summary>
    public void ResetProgress()
    {
        PartFraction = 0d;
        WriteState = PartRowState.Pending;
    }

    /// <summary>
    /// Per-row label, e.g. <c>"Part 2 · 05:00–10:00 · 5:00"</c>. The boundaries are zero-padded clock
    /// times (<see cref="FormatClock"/>); the duration is the compact, unpadded form
    /// (<see cref="FormatDuration"/>) so it reads like a length rather than a timestamp.
    /// </summary>
    public string Display =>
        $"Part {Index.ToString(CultureInfo.InvariantCulture)} · " +
        $"{FormatClock(Start)}–{FormatClock(End)} · {FormatDuration(Duration)}";

    /// <summary>Format a boundary time as <c>MM:SS</c> (or <c>H:MM:SS</c> past an hour), zero-padded.</summary>
    internal static string FormatClock(TimeSpan t)
    {
        var a = t < TimeSpan.Zero ? t.Negate() : t;
        return a.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)a.TotalHours}:{a.Minutes:00}:{a.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{a.Minutes:00}:{a.Seconds:00}");
    }

    /// <summary>Format a LENGTH as <c>M:SS</c> (unpadded minutes; <c>H:MM:SS</c> past an hour).</summary>
    internal static string FormatDuration(TimeSpan t)
    {
        var a = t < TimeSpan.Zero ? t.Negate() : t;
        return a.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)a.TotalHours}:{a.Minutes:00}:{a.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{a.Minutes}:{a.Seconds:00}");
    }
}
