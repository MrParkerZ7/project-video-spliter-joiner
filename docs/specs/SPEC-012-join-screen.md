---
id: SPEC-012
slug: join-screen
area: app
title: Join screen
status: current
sources:
  - src/App/ViewModels/JoinViewModel.cs
  - src/App/ViewModels/JoinItemViewModel.cs
serves-goal: [G-001, G-003]
updated: 2026-08-22
---

## What
The Join tab view-model (`JoinViewModel`, T-008). It gathers video clips into an ordered `Items` list, probes each newly-added clip for a codec/resolution info chip and its duration, and on every list change re-runs a live concat-compatibility check against `IJoinEngine`. The banner (`CompatSummary` / `IsCompatible`) reports whether the current set can be stream-copy concatenated, and the primary button label (`RunLabel`) plus the estimated-result readouts (`EstimatedDuration` / `EstimatedSize`) are count-aware. When the set is ≥2 clips, compatible, and an `OutputPath` is set, `RunJoinAsync` builds a `JoinRequest` (clips in list order) and runs it through the shared `OperationViewModel`, mapping a refusal to a friendly error and remembering the input/output folders across sessions. The VM is WPF-free and constructor-injected for unit testing; `JoinItemViewModel` is the per-clip row (path, display filename, info chip, duration, size).

## Why
Joining clips must be lossless (stream-copy concat, no re-render), which only works when every input shares codec/container/resolution/timebase parameters. Rather than let the user run a join that ffmpeg would refuse mid-way (leaving a half-written file), the Join screen validates compatibility continuously as clips are added/removed/reordered and gates the Run button on the verdict. Order matters (it drives the output sequence), so the list is explicitly ordered with reorder affordances. Routing all engine work through the shared `OperationViewModel` gives the Join screen the same progress/cancel/error surface as the Split screen. Cross-session folder memory (T-038) saves the user re-navigating to their clip and output folders each launch.

## Scope
**In:** the `JoinViewModel` + `JoinItemViewModel` contract — item add/remove/reorder, per-item probe/info-chip/duration/size, the live compatibility check and its summary/verdict, `CanRunJoin`/`CanClear`/`HasClips` gating, count-aware `RunLabel` + estimated-result readouts, `RunJoinAsync` request building and success/refusal handling, `Clear`, and `LastInputDir`/`LastOutputDir` memory.

**Out:** the `IJoinEngine` concat/compatibility-detection internals (its own core spec — this spec treats the engine as a black box returning `CompatReport`/`JoinResult`); the shared `OperationViewModel` progress/cancel/error-surface mechanics (its own spec — this spec only verifies the join wires into it); the `IAppSettings` persistence mechanics; the WPF view / drag-drop code-behind hit-testing (this spec covers only the VM-level `AddFilesAsync`/`Move` entry points those call).

## Current behavior & invariants
- **I1** — `AddFilesAsync(null)` is a no-op: no items added, no probe, no compat check. (`JoinViewModel.AddFilesAsync`, guard at top)
- **I2** — `AddFilesAsync` appends one `JoinItemViewModel` per non-blank path, preserving order; blank/whitespace-only entries are skipped; duplicates ARE permitted (no dedup). (`AddFilesAsync` loop)
- **I3** — Each added item gets `Display` = the filename of its path (falling back to the full path when the filename is empty) and `SizeBytes` = its on-disk byte size, or 0 when unreadable. (`JoinItemViewModel` ctor; `JoinViewModel.SafeFileSize`)
- **I4** — Each added item is probed; on `ProbeSucceeded`, `InfoText` = `"{codec} · {W}x{H}"` (or `"audio only"` / the container string when there is no video stream), and `Duration` = the probed duration; a probe failure leaves `InfoText`/`Duration` null and never throws. (`PopulateInfoChipAsync`, `FormatInfoChip`)
- **I5** — After a non-empty add, the estimate readouts are re-raised and compatibility is re-checked. (`AddFilesAsync` tail: `RaiseEstimate` + `RefreshCompatAsync`)
- **I6** — After a non-empty add, `IAppSettings.LastInputDir` is set to the directory of the last added file (best-effort; persistence failures swallowed). (`AddFilesAsync`, T-038)
- **I7** — With fewer than 2 items, `RefreshCompatAsync` sets `Compat = null`, `IsCompatible = false`, `CompatSummary = "Add at least 2 files to join."` and does NOT call the engine. (`RefreshCompatAsync` <2 branch)
- **I8** — With ≥2 items, `RefreshCompatAsync` calls `IJoinEngine.CheckCompatibilityAsync` with the item paths in current list order and sets `IsCompatible = report.Compatible`. (`RefreshCompatAsync`)
- **I9** — `CompatSummary` reflects the verdict: compatible → `"{count} clips ready to join."`; incompatible → `"Cannot join — {'; '-joined mismatch details}"` (or `"Inputs are not compatible."` when there are no mismatch details). (`RefreshCompatAsync`, `FormatMismatches`)
- **I10** — If `CheckCompatibilityAsync` throws, the VM defensively sets `Compat = null`, `IsCompatible = false`, `CompatSummary = "Could not verify compatibility: {message}"` so Run stays gated. (`RefreshCompatAsync` catch block)
- **I11** — `MoveAsync` is total and safe: a no-op when there are <2 items, when `fromIndex` is out of range, or when `fromIndex == toIndex`; `toIndex` is clamped into `[0, count-1]`; otherwise `Items.Move` is applied and compatibility re-checked; it never throws for any index. (`MoveAsync`)
- **I12** — `MoveUpAsync` / `MoveDownAsync` delegate to `MoveAsync(index, index∓1)` — a single reorder path shared by the Up/Down buttons and drag-reorder, producing one recheck. (`MoveUpAsync`, `MoveDownAsync`)
- **I13** — `CanMoveUp` is true only when the item's index > 0; `CanMoveDown` only when `0 ≤ index < Count-1`. (`CanMoveUp`, `CanMoveDown`)
- **I14** — `Move(int, int)` is a synchronous fire-and-forget wrapper over `MoveAsync` for the drag-reorder code-behind. (`Move`)
- **I15** — `RemoveAsync` removes the item when present, re-raises the estimate, and re-checks compatibility; it is a no-op when the item is null or not in the list. (`RemoveAsync`)
- **I16** — `RunLabel` = `"Join"` with 0 items, `"Join {count} clips"` otherwise. (`RunLabel`, T-059)
- **I17** — `HasClips` = `Count > 0`; `EstimatedDuration` = the formatted sum of probed clip durations (unprobed clips contribute 0); `EstimatedSize` = the formatted sum of clip sizes. (`HasClips`, `EstimatedDuration`, `EstimatedSize`, T-059)
- **I18** — `CanRunJoin` is true iff `Items.Count ≥ 2` AND `IsCompatible` AND `OutputPath` is non-blank. (`CanRunJoin`)
- **I19** — `CanClear` is true iff `Items.Count > 0` AND not `Operation.IsRunning`. (`CanClear`, T-047)
- **I20** — Setting `OutputPath` or `IsCompatible` re-raises `CanRunJoin` and the command `CanExecute` states; an item-collection change re-raises `CanRunJoin`/`CanClear`/`HasClips`/`RunLabel`; an `Operation.IsRunning`/`State` change re-raises `CanClear`. (`OutputPath` setter, `IsCompatible` setter, `OnItemsChanged`, `OnOperationChanged`)
- **I21** — `RunJoinAsync` is a no-op when `!CanRunJoin`: no `JoinRequest` is built and the operation stays `Idle`. (`RunJoinAsync` guard)
- **I22** — `RunJoinAsync` builds a `JoinRequest` with `InputPaths` in list order, the current `OutputPath` and `Overwrite`, and runs it through `OperationViewModel.RunWithResultAsync`. (`RunJoinAsync`)
- **I23** — On success (`Operation.State == Completed` && `result.Success`), `LastResult` is set to the engine result. (`RunJoinAsync` success block)
- **I24** — On success, `Operation.ResultSummary` = `"Joined 1 clip → {file}"` (1 clip) or `"Joined {N} clips → {file}"` using the written output filename (falling back to `OutputPath`). (`RunJoinAsync`, T-073)
- **I25** — On success, `IAppSettings.LastOutputDir` is set to the output file's directory (best-effort). (`RunJoinAsync`, T-038)
- **I26** — On a refusal (`result.Success == false`), the operation ends `Failed` with a `UserFacingError(IncompatibleJoin)` naming each mismatch, carrying `LogFilePath`/`FullStdErr` when the refusal came from a failed ffmpeg run; `LastResult` stays null. (`RunJoinAsync` `failureSelector`, `RefusalDetail`)
- **I27** — `Clear()` is a no-op when `!CanClear`; otherwise it empties `Items`, resets `Compat`/`IsCompatible`/`CompatSummary` to the "add at least 2 files" baseline, drops `LastResult`, resets `Operation`, and deliberately preserves `OutputPath`. (`Clear`, T-047)
- **I28** — `CancelCommand` is `Operation.CancelCommand` — cancelling the Join screen delegates to the shared operation's cancel. (ctor wiring)

### Dropped files are accounted for (`DropRefusal`, `JoinViewModel.AddDroppedFilesAsync` — T-154)
- **I29** — a dropped file that is **not added is explained**, never silently discarded. `DropSummary` states it in one line, and the drop handler passes the **raw** paths to the view-model rather than filtering first — filtering in the view and telling the VM only about the survivors is precisely why this screen could not report a refusal even in principle. (`AddDroppedFilesAsync`, `JoinView.HandleDroppedFiles`)
- **I30** — Join **never says "already in the list"**, unlike Bulk Cut. This screen permits the same clip twice on purpose (I3 — duplicates allowed), so borrowing that wording would contradict its own rule. Consistency across the three screens is one vocabulary, not one sentence.
- **I31** — what Join must report instead is the same path appearing **twice inside one drop**: the shared filter dedupes within a payload, so the second copy vanishes — invisible, and inconsistent with adding it twice through two gestures, which is allowed. ("1 was dropped twice", `DropRefusal.DroppedTwice`)
- **I32** — a mixed drop **still adds the videos**. One unsupported file must not poison the whole drop.
- **I33** — `DropSummary` is **null when nothing was refused**. A message on every drop is noise, and noise is what teaches people to ignore the one that matters.
- **I34** — a dropped **folder is called a folder**, not "not a video file" — describing the most natural gesture for a video tool with a false statement is a new defect, not a fix. (`DropRefusal.Classify`)
- **I35** — `Clear()` nulls `DropSummary`. The note describes a list that no longer exists; leaving it up is the stale-note bug that shipped on Bulk Cut and was fixed there in the same change.
- **I36** — *(uncovered — set in WPF code-behind; needs a windowed/STA harness, see `_GAPS.md`)* the `dragdrop.log` **`accepted` flag reports the real decision**, not a hard-coded `true`, and `note:` carries the same sentence the screen is showing. (`DropDiagnostics.Record`)
- **I37** — *(uncovered by design — describes a region no drop event reaches, so there is nothing to assert; the decision is ADR-0023)* **boundary, stated rather than fixed:** a drag containing *no* recognised video never reaches any of this — `OnDragOver` answers `VideoFileFilter.HasAnyVideo` with `DragDropEffects.None`, Windows shows a no-entry cursor, and no drop event is delivered. The cursor is that case's feedback.

## Links
- Design: —
- Goals: G-001 (ship v1.0 stream-copy splitter/joiner), G-003 (drag-and-drop — drag to reorder join clips)
- Related specs: SPEC (Split screen), SPEC (OperationViewModel surface), SPEC (join engine / compatibility) — cross-reference once authored
- Key code: `src/App/ViewModels/JoinViewModel.cs`, `src/App/ViewModels/JoinItemViewModel.cs`
