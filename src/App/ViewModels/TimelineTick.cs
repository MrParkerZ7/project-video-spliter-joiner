using System;
using VideoSplitJoiner.Core.Detect;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// One projected tick on the timeline strip (T-014): a marker or a candidate reduced to what the
/// view needs to draw + route a click. <see cref="Normalized"/> is its X position in [0,1] (from
/// <see cref="TimelineMath.ToNormalized"/>); <see cref="Time"/> is the source time it sits at;
/// <see cref="Kind"/> is the candidate kind (null for marker ticks) so the view can colour it; and
/// <see cref="Ref"/> is the underlying <see cref="CutMarkerViewModel"/> / <see cref="CandidateViewModel"/>
/// so a click on the tick can be routed back to the seek / preview command.
/// </summary>
/// <param name="Normalized">X position on the strip in [0,1].</param>
/// <param name="Time">The source time this tick represents.</param>
/// <param name="Kind">Candidate kind for a candidate tick; null for a marker tick.</param>
/// <param name="Ref">The originating marker/candidate view model (click routing back-reference).</param>
public sealed record TimelineTick(
    double Normalized,
    TimeSpan Time,
    CandidateKind? Kind,
    object Ref);
