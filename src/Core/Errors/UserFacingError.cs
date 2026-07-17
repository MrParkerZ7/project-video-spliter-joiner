namespace VideoSplitJoiner.Core.Errors;

/// <summary>
/// A human-readable rendering of an ffmpeg failure. <see cref="Message"/> is a friendly
/// headline; <see cref="RawTail"/> preserves the real stderr tail for a "details" expander
/// so power users always have the underlying output. <see cref="Hint"/> is an optional
/// actionable suggestion.
/// <para>
/// <see cref="FullText"/> carries the COMPLETE diagnostic text (headline + full stderr, not just the
/// tail) for the copyable error surface, and <see cref="LogFilePath"/> points at the on-disk full log
/// written for the failed run (both optional — null when no run-level detail exists, e.g. a
/// binary-not-found before any ffmpeg output).
/// </para>
/// </summary>
public sealed record UserFacingError(
    ErrorCategory Category,
    string Message,
    string RawTail,
    string? Hint = null,
    string? LogFilePath = null,
    string? FullText = null)
{
    /// <summary>
    /// The complete, copyable body for the error surface: the friendly headline followed by the
    /// fullest detail available (<see cref="FullText"/> when present, else <see cref="RawTail"/>),
    /// and the saved-log path when one exists. This is exactly what the "Copy" button places on the
    /// clipboard and what the read-only detail box shows — computed here so it is unit-testable and
    /// identical everywhere.
    /// </summary>
    public string CopyText
    {
        get
        {
            var detail = string.IsNullOrEmpty(FullText) ? RawTail : FullText;
            var sb = new System.Text.StringBuilder();
            sb.Append(Message);

            if (!string.IsNullOrEmpty(Hint))
            {
                sb.Append(System.Environment.NewLine).Append(Hint);
            }

            if (!string.IsNullOrEmpty(detail))
            {
                sb.Append(System.Environment.NewLine)
                    .Append(System.Environment.NewLine)
                    .Append(detail);
            }

            if (!string.IsNullOrEmpty(LogFilePath))
            {
                sb.Append(System.Environment.NewLine)
                    .Append(System.Environment.NewLine)
                    .Append("Full log: ")
                    .Append(LogFilePath);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// The detail text shown in the read-only, selectable detail box: the fullest output available
    /// (<see cref="FullText"/> when present, else <see cref="RawTail"/>). Kept separate from
    /// <see cref="CopyText"/> so the box shows just the log while the headline/hint render above it.
    /// </summary>
    public string DetailText => string.IsNullOrEmpty(FullText) ? RawTail : FullText;

    /// <summary>True when a saved full-log file path is available (drives the "Open log" affordance).</summary>
    public bool HasLogFile => !string.IsNullOrEmpty(LogFilePath);
}
