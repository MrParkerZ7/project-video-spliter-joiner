namespace VideoSplitJoiner.Core.Profiles;

/// <summary>
/// A reusable, named cut profile (G-037 / T-102): the two trim offsets that define a "keep the middle"
/// cut in a source-agnostic way — an <see cref="IntroFromStart"/> measured as an ABSOLUTE time from the
/// start of the file, plus an optional <see cref="OutroFromEnd"/> measured as a time-from-END. Storing
/// the outro from the end (rather than an absolute time) is what lets ONE profile apply cleanly to
/// episodes of DIFFERENT lengths — the same "trim the last 12s of credits" lands correctly on a
/// 22-minute and a 24-minute episode alike (the outro-from-end convention apply-to-all established in
/// T-096).
///
/// <para>Plain, immutable data — deliberately Core-resident and WPF-free so it can be persisted, unit
/// tested, and reused without an App/UI dependency (guarded by <c>CoreIsUiFreeTests</c>). Persisted by
/// <c>IAppSettings</c> as seconds (double) for stable, human-readable JSON — never TimeSpan ticks.</para>
///
/// <para>Validation (throwing, matching the Core convention — e.g. <c>MediaProbe</c>'s argument guards):
/// the name must be non-empty (stored trimmed; it is the case-insensitive upsert key), and both offsets
/// must be non-negative. Invalid inputs throw at construction, so an in-memory profile is always
/// well-formed; the settings loader tolerates a corrupt persisted entry by skipping it rather than
/// crashing.</para>
/// </summary>
/// <param name="Name">Human-facing profile name — non-empty (stored trimmed); the case-insensitive upsert key.</param>
/// <param name="IntroFromStart">Absolute intro-end offset from the START of the file — the start of the kept middle. Non-negative.</param>
/// <param name="OutroFromEnd">Optional outro length measured from the END of the file; <c>null</c> ⇒ keep runs to EOF (no tail trim). Non-negative when present.</param>
public sealed record CutProfile(string Name, TimeSpan IntroFromStart, TimeSpan? OutroFromEnd)
{
    /// <summary>The profile name — non-empty, stored trimmed; the case-insensitive upsert key.</summary>
    public string Name { get; init; } = ValidateName(Name);

    /// <summary>Absolute intro-end offset from the start of the file (non-negative).</summary>
    public TimeSpan IntroFromStart { get; init; } = ValidateOffset(IntroFromStart, nameof(IntroFromStart));

    /// <summary>Optional outro length measured from the end of the file (non-negative when present); <c>null</c> ⇒ keep to EOF.</summary>
    public TimeSpan? OutroFromEnd { get; init; } = ValidateOffset(OutroFromEnd, nameof(OutroFromEnd));

    private static string ValidateName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A cut profile name must be non-empty.", nameof(name))
            : name.Trim();

    private static TimeSpan ValidateOffset(TimeSpan value, string paramName) =>
        value < TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(paramName, value, "A cut profile offset must be non-negative.")
            : value;

    private static TimeSpan? ValidateOffset(TimeSpan? value, string paramName) =>
        value is { } v ? ValidateOffset(v, paramName) : value;
}
