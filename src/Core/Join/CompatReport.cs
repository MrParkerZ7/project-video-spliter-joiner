namespace VideoSplitJoiner.Core.Join;

/// <summary>
/// The result of a concat-compatibility pre-flight: whether the input set can be safely
/// stream-copied into one file, and every reason it cannot. By construction
/// <see cref="Compatible"/> is true exactly when <see cref="Mismatches"/> is empty.
/// </summary>
public sealed record CompatReport
{
    /// <summary>Create a report; <see cref="Compatible"/> is derived from the mismatch list.</summary>
    public CompatReport(IReadOnlyList<Mismatch> mismatches)
    {
        Mismatches = mismatches ?? throw new ArgumentNullException(nameof(mismatches));
    }

    /// <summary>True when the inputs are concat-compatible (no mismatches).</summary>
    public bool Compatible => Mismatches.Count == 0;

    /// <summary>Every reason the inputs are not concat-compatible; empty when compatible.</summary>
    public IReadOnlyList<Mismatch> Mismatches { get; }

    /// <summary>A compatible report (no mismatches).</summary>
    public static CompatReport Ok() => new(Array.Empty<Mismatch>());

    /// <summary>An incompatible report carrying the given mismatches.</summary>
    public static CompatReport Incompatible(IReadOnlyList<Mismatch> mismatches) => new(mismatches);
}
