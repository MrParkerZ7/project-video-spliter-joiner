namespace VideoSplitJoiner.Core.Ffmpeg;

/// <summary>
/// A single stage transition emitted by a long-running engine (split / join) as it enters each
/// real phase of work (T-044). Reported through an <see cref="IProgress{T}"/> of this type so the
/// UI can show a "what's happening now" line synced to the ACTUAL process action — never driven by
/// a timer. Distinct from the numeric <c>IProgress&lt;double&gt;</c> progress fraction (which drives
/// the bar): this carries the human-readable stage name and an optional detail.
/// </summary>
/// <param name="Stage">
/// The stage the engine just entered, e.g. "Preparing", "Splitting", "Checking compatibility",
/// "Joining", "Finalizing", "Done". Stable, UI-facing text.
/// </param>
/// <param name="Detail">
/// Optional extra context for the stage, e.g. "4 parts" or "segment 2 of 4" for a split. Null when
/// the stage needs no elaboration.
/// </param>
/// <param name="Fraction">
/// Optional progress fraction (0..1) associated with this stage, when the engine happens to know it
/// at the transition point. Usually null — the numeric bar is driven by the separate
/// <c>IProgress&lt;double&gt;</c> channel; this field exists so a caller that folds both channels into
/// one reporter still sees a fraction when one is available.
/// </param>
public sealed record OperationStatus(string Stage, string? Detail = null, double? Fraction = null);
