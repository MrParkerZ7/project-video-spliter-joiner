using VideoSplitJoiner.Core.Errors;

namespace VideoSplitJoiner.Core.Bulk;

/// <summary>
/// The ledger entry for one batch row: its terminal <see cref="ItemOutcome"/>, where the output
/// landed (when <see cref="ItemOutcome.Done"/>), the mapped error (when
/// <see cref="ItemOutcome.Failed"/>), and any non-fatal warnings the engine surfaced (coarse GOP,
/// no keyframes, …) even on a successful trim.
/// </summary>
/// <param name="Item">The original request row (carries the caller's <see cref="BulkTrimItem.Tag"/>).</param>
/// <param name="Outcome">This row's terminal state.</param>
/// <param name="OutputPath">The written output path for a <see cref="ItemOutcome.Done"/> row; <c>null</c> otherwise.</param>
/// <param name="Error">The mapped, user-facing error for a <see cref="ItemOutcome.Failed"/> row; <c>null</c> otherwise.</param>
/// <param name="Warnings">Non-fatal notes from the split (never <c>null</c> — empty when there were none).</param>
public sealed record BulkTrimItemResult(
    BulkTrimItem Item,
    ItemOutcome Outcome,
    string? OutputPath,
    UserFacingError? Error,
    IReadOnlyList<string> Warnings);
