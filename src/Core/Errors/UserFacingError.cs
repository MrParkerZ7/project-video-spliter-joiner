namespace VideoSplitJoiner.Core.Errors;

/// <summary>
/// A human-readable rendering of an ffmpeg failure. <see cref="Message"/> is a friendly
/// headline; <see cref="RawTail"/> preserves the real stderr tail for a "details" expander
/// so power users always have the underlying output. <see cref="Hint"/> is an optional
/// actionable suggestion.
/// </summary>
public sealed record UserFacingError(
    ErrorCategory Category,
    string Message,
    string RawTail,
    string? Hint = null);
