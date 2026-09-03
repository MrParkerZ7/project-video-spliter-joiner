using System.Collections.Generic;
using VideoSplitJoiner.Core.Profiles;

namespace VideoSplitJoiner.App.Settings;

/// <summary>
/// The app-wide layout axis (D-001 / T-081). <see cref="Horizontal"/> is the original landscape
/// two-column layout (video/timeline left, tools right); <see cref="Vertical"/> is the portrait
/// stacked layout (video/timeline top, tools below). Persisted in settings and restored on startup.
/// </summary>
/// <summary>
/// Which screen the app reopens on (T-143). A NAMED value rather than the raw tab index: an int silently
/// points at the wrong screen the moment tabs are reordered, and a stored int from a future build with
/// fewer tabs has no safe meaning.
/// </summary>
public enum AppTab
{
    /// <summary>The Split screen - the default, and the fallback for anything unrecognised.</summary>
    Split = 0,

    /// <summary>The Join screen.</summary>
    Join = 1,

    /// <summary>The Bulk Cut screen.</summary>
    BulkCut = 2,
}

public enum LayoutMode
{
    /// <summary>Landscape two-column layout — the default (today's behavior / missing setting).</summary>
    Horizontal = 0,

    /// <summary>Portrait stacked layout — video/timeline on top, tool panel below.</summary>
    Vertical = 1,
}

/// <summary>
/// Persistent, cross-session app preferences (T-038). Deliberately tiny — the two "remember
/// where I was" folders plus the D-001 vertical-monitor layout state — but shaped so more keys can
/// be added later without a contract change.
/// <para>
/// The folder accessors are paths (nullable = "never set / unknown"). Setting any value persists it
/// best-effort immediately; a persistence failure is swallowed and the value stays in memory for the
/// session (never crashes the caller). Reads never throw.
/// </para>
/// </summary>
public interface IAppSettings
{
    /// <summary>The folder the last input file was chosen from, or <c>null</c> if never set.</summary>
    string? LastInputDir { get; set; }

    /// <summary>The folder the last output was written to, or <c>null</c> if never set.</summary>
    string? LastOutputDir { get; set; }

    /// <summary>
    /// The persisted layout axis (D-001 / T-081) — <see cref="LayoutMode.Horizontal"/> by default
    /// (first launch / missing setting). Setting it persists immediately, like the folder accessors.
    /// </summary>
    LayoutMode LayoutMode { get; set; }

    /// <summary>
    /// The remembered split-ratio for the HORIZONTAL layout — the video-column fraction of the total
    /// width (0..1), so a flip back to horizontal restores the user's last drag. <c>null</c> = use the
    /// default. Kept independent from <see cref="VerticalSplitRatio"/> so flipping never distorts the
    /// other axis (D6). Setting it persists immediately.
    /// </summary>
    double? HorizontalSplitRatio { get; set; }

    /// <summary>
    /// The remembered split-ratio for the VERTICAL layout — the video-block fraction of the total
    /// height (0..1). <c>null</c> = use the default (≈0.62). Independent of
    /// <see cref="HorizontalSplitRatio"/> (D6). Setting it persists immediately.
    /// </summary>
    double? VerticalSplitRatio { get; set; }

    /// <summary>
    /// The remembered split-ratio for the Bulk Cut tab's HORIZONTAL (side-by-side) layout — the
    /// preview-pane fraction of the total width (0..1) when the pane sits BESIDE the row-list
    /// (G-039 / T-112). Kept SEPARATE from <see cref="HorizontalSplitRatio"/> (Split's video↔tools
    /// ratio) so dragging the Bulk split never distorts the Split tab's split, and vice-versa.
    /// <c>null</c> = use the Bulk default. Independent of <see cref="BulkVerticalSplitRatio"/> (D6).
    /// Setting it persists immediately.
    /// </summary>
    /// <summary>
    /// T-133 — whether "Set intro-end / outro-start here" fans the cut out to every checked row instead of
    /// only the previewed one. <c>null</c> = the default, which is ON: the tab is called Bulk Cut, and the
    /// single-row behaviour is what made a user set one cut, press Run, and get one file.
    /// </summary>
    bool? BulkApplyCutToAllRows { get; set; }

    /// <summary>
    /// T-156 — delete each original automatically once a batch finishes with no failed rows.
    ///
    /// <para>Reclaiming space is otherwise two manual steps after every batch, which matters when the
    /// disk fills mid-session. Null/absent = OFF: a destructive default is not a default.</para>
    /// </summary>
    bool? BulkAutoDeleteOriginals { get; set; }

    /// <summary>
    /// T-156 — empty the Recycle Bin after an automatic delete, because binning alone frees no space.
    ///
    /// <para><b>Combined with <see cref="BulkAutoDeleteOriginals"/> this is permanent deletion with no
    /// undo</b>, so it is meaningless (and refused) on its own, defaults OFF, and is gated behind an
    /// explicit confirmation. Everything that makes deleting originals safe rests on the file still
    /// being in the bin afterwards.</para>
    /// </summary>
    bool? BulkAutoEmptyRecycleBin { get; set; }

    /// <summary>
    /// The screen the app was last on (T-143), restored on startup beside <see cref="LayoutMode"/>.
    /// <c>null</c> = never recorded / an older settings file, which reads as <see cref="AppTab.Split"/>.
    /// </summary>
    AppTab? LastTab { get; set; }

    double? BulkHorizontalSplitRatio { get; set; }

    /// <summary>
    /// The remembered split-ratio for the Bulk Cut tab's VERTICAL (stacked) layout — the preview-pane
    /// fraction of the total height (0..1) when the pane stacks ABOVE the row-list (G-039 / T-112).
    /// Kept SEPARATE from <see cref="VerticalSplitRatio"/> (Split's ratio) to avoid cross-tab coupling.
    /// <c>null</c> = use the Bulk default. Independent of <see cref="BulkHorizontalSplitRatio"/> (D6).
    /// Setting it persists immediately.
    /// </summary>
    double? BulkVerticalSplitRatio { get; set; }

    /// <summary>
    /// The saved reusable cut profiles (G-037 / T-102) — a named intro-from-start + optional
    /// outro-from-end that can be applied to any Bulk Cut row/batch. Ordered by save order, deduped by
    /// <see cref="CutProfile.Name"/> (case-insensitive). Empty when none are saved / on a first launch or
    /// a legacy settings file that predates the feature. Read-only — mutate via <see cref="SaveProfile"/>
    /// / <see cref="DeleteProfile"/>.
    /// </summary>
    IReadOnlyList<CutProfile> CutProfiles { get; }

    /// <summary>
    /// Upsert a cut profile by <see cref="CutProfile.Name"/> (case-insensitive): an existing profile with
    /// the same name is replaced in place (position preserved), otherwise the profile is appended.
    /// Persists immediately, best-effort, like the other setters (a write failure keeps the change in
    /// memory for the session and never throws to the caller).
    /// </summary>
    void SaveProfile(CutProfile profile);

    /// <summary>
    /// Delete the cut profile whose <see cref="CutProfile.Name"/> matches <paramref name="name"/>
    /// (case-insensitive). A no-op (no persist) when nothing matches / the name is blank. Persists
    /// immediately, best-effort, like the other setters.
    /// </summary>
    void DeleteProfile(string name);
}
