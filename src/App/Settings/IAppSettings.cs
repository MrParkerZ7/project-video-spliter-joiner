namespace VideoSplitJoiner.App.Settings;

/// <summary>
/// The app-wide layout axis (D-001 / T-081). <see cref="Horizontal"/> is the original landscape
/// two-column layout (video/timeline left, tools right); <see cref="Vertical"/> is the portrait
/// stacked layout (video/timeline top, tools below). Persisted in settings and restored on startup.
/// </summary>
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
}
