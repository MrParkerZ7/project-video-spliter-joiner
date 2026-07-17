namespace VideoSplitJoiner.App.Settings;

/// <summary>
/// Persistent, cross-session app preferences (T-038). Deliberately tiny — just the two "remember
/// where I was" folders — but shaped so more keys can be added later without a contract change.
/// <para>
/// Both accessors are folder paths (nullable = "never set / unknown"). Setting a value persists it
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
}
