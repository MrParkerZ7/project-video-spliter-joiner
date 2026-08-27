namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// WHERE a batch writes its trimmed result (T-121, epic G-041) — orthogonal to
/// <see cref="CollisionPolicy"/>, which answers the different question of what to do when the chosen
/// destination is already taken.
/// </summary>
public enum OutputMode
{
    /// <summary>
    /// Default, non-destructive: write a NEW file beside the source (the <c>_trimmed</c> name the VM
    /// computes), resolved through the active <see cref="CollisionPolicy"/>. The source is never a
    /// write target in this mode.
    /// </summary>
    NewFile,

    /// <summary>
    /// Destructive, opt-in: write OVER the original input file. The engine still produces the output in
    /// a temp location first and only replaces the original after a verified-complete run, so a failed
    /// or cancelled batch always leaves the original intact. <see cref="CollisionPolicy"/> is ignored in
    /// this mode — the destination is always "taken", by the source itself.
    /// </summary>
    ReplaceOriginal,
}
