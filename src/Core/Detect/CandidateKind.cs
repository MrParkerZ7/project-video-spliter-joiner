namespace VideoSplitJoiner.Core.Detect;

/// <summary>
/// What kind of natural boundary a detected split candidate is.
/// </summary>
public enum CandidateKind
{
    /// <summary>A black interval (fade-to-black / gap) — via <c>blackdetect</c>.</summary>
    Black,

    /// <summary>A white interval (fade-to-white / flash) — via <c>negate</c> + <c>blackdetect</c>.</summary>
    White,

    /// <summary>A hard scene cut — via <c>select=gt(scene,…)</c> + <c>metadata=print</c>.</summary>
    Scene,
}
