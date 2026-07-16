using System;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// One projected tick on the timeline strip (T-014): a marker reduced to what the view needs to
/// draw + route a click. <see cref="Normalized"/> is its X position in [0,1] (from
/// <see cref="TimelineMath.ToNormalized"/>); <see cref="Time"/> is the source time it sits at; and
/// <see cref="Ref"/> is the underlying <see cref="CutMarkerViewModel"/> so a click on the tick can
/// be routed back to the seek command.
/// </summary>
/// <param name="Normalized">X position on the strip in [0,1].</param>
/// <param name="Time">The source time this tick represents.</param>
/// <param name="Ref">The originating marker view model (click routing back-reference).</param>
public sealed record TimelineTick(
    double Normalized,
    TimeSpan Time,
    object Ref);
