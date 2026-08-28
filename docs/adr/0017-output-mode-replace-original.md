# ADR 0017: "Replace originals" is a separate OutputMode axis, not a fourth CollisionPolicy — produced, verified in full, then replaced in place through a Recycle-Bin backup

## Status

Accepted.

## Context

G-041 answers a direct user request alongside its snap-visibility fix: *"I also want new option output by
override original file, is there possible?"* Until now Bulk Cut has only ever written a **new** `_trimmed`
file beside each source (design [D-004](../design/D-004/README.md),
[ADR 0015](0015-bulk-trim-reuses-split-single-segment.md)). Writing **over** the input is the first genuinely
destructive thing the app does — and it lands in the one screen that operates on **N files at once**.

Two questions had to be answered: **where does "which destination?" belong in the option model**, and **what
ordering makes overwriting a master safe enough to offer at all?**

The forces in tension:

- **Bulk already owns an enum about destinations — and it answers a different question.** `CollisionPolicy`
  {`AutoSuffix`, `Skip`, `Overwrite`} resolves *"what if the destination is already taken?"*, and its own file
  header states *"The source file is NEVER a write target under any policy"* — a contract SPEC-002 **I22**
  turns into a tested invariant (a desired output equal to the source is forced onto an AutoSuffix name **even
  under `Overwrite`**). A fourth enum value would make `ResolveCollision` return the source under that very
  enum.
- **The cost of being wrong is asymmetric.** Under `_trimmed`, a mis-set cut costs a disk write. Over the
  original it costs the master. The load-bearing property is therefore not the happy path but **every failure
  path** — a crash, a cancel, a short ffmpeg run, a blocked pre-flight must all end with the original
  byte-identical.
- **The engine already writes temp-then-move** ([ADR 0003](0003-cancel-safety.md)). `SplitEngine` extracts into
  a `.vsj-split-<guid>` folder inside the output directory and only then moves each part onto its planned
  destination, so a cancel leaves temp files rather than a half-written output. But the per-part
  "was not produced by ffmpeg" check (SPEC-001 **I35**) sat *inside* that move loop — harmless when every
  destination is a new file, fatal when a destination is the user's master.
- **Core is deliberately OS- and UI-free.** Core is `net8.0` and guarded by `CoreIsUiFreeTests`; the Recycle
  Bin is a Windows shell concept (`Microsoft.VisualBasic.FileIO`) reachable only from the `net8.0-windows`
  app.
- **The preview holds a handle on exactly the file we want to replace.** The tab's shared player
  ([ADR 0016](0016-shared-bulk-preview-player-and-cut-profiles.md)) keeps the **selected** row's file open in
  FFME, and the existing pre-run `Stop` halts playback without closing the element.

## Decision

Adopt replace-the-original as a **separate `OutputMode` axis**, executed **produce → verify every part →
replace in place through a backup**, with the backup's fate behind an injected seam and the whole run gated by
a counted confirmation that defaults to refusing.

- **(a) A new orthogonal axis — never a fourth collision value.** `OutputMode` (`Core/Bulk/OutputMode.cs`) is
  `{ NewFile, ReplaceOriginal }` and rides `BulkTrimOptions` as its own parameter —
  `(Collision, Output, Precision)` — exactly the "future knobs without breaking callers" the record's own
  comment anticipated, and every existing call site still compiles because `NewFile` is the default.
  `ResolveCollision` gains **one early branch, before the collision switch is reached**, returning
  `(Path.GetFullPath(item.InputPath), overwrite: true, skip: false)`; the collision policy is then moot,
  because the destination is always taken — by the source itself. Everything the guard protected stays
  **byte-identical and fully live for `NewFile`**: both `PathsEqual(desiredFull, inputFull)` force-AutoSuffix
  branches (under `Overwrite` *and* under `Skip`) and both source-refusals inside `ResolveAutoSuffix`.
  **I22 is not deleted, it is rescoped** — "the source is never a write target" now reads "…under `NewFile`" —
  and the relaxation lives in one named branch no existing caller can reach (SPEC-002 **I33–I39**).

- **(b) Verify EVERY produced part before touching ANY destination.** `SplitEngine.MoveTempSegmentsIntoPlace`
  now runs the "was not produced by ffmpeg (got fewer segments than planned)" existence check as a **full pass
  over `produced`** ahead of the move loop, rather than discovering a shortfall part-by-part while moving.
  Under `NewFile` the reordering is invisible; under `ReplaceOriginal` a destination **is** the user's master,
  so a missing part found halfway through the loop would mean an already-clobbered original. Verify-then-replace
  is what makes *"a failed or cancelled run leaves the original intact"* true rather than aspirational
  (SPEC-002 **I40**, **I45**).

- **(c) Replace in place through a backup — never a delete-then-move.** The replace path is chosen by
  **destination identity, not a mode flag**: a planned output whose full path equals the input's goes through
  `ReplaceOriginalInPlace`, and every other destination keeps the ordinary delete-if-exists → `File.Move`
  (SPEC-002 **I41**). `ReplaceOriginalInPlace` first deletes any stale `<original>.vsj-original` left by an
  earlier interrupted run (best-effort; a failure there is non-fatal, since the replace itself surfaces any real
  problem), then prefers `File.Replace(temp, original, backup, ignoreMetadataErrors: true)` — one call in which
  the original's bytes move to the backup as the new bytes take their place. Volumes that cannot do that
  (`PlatformNotSupportedException` / `IOException` / `UnauthorizedAccessException` — exFAT, some SMB shares)
  fall back to **rename-aside**: move the *original* to the backup **first**, then move the temp into place; if
  that second move fails, move the backup **back** over the original and rethrow. Both routes hold the same
  property — **the bytes always exist under one name or the other**, never nowhere (SPEC-002 **I42–I43**).

- **(d) `IOriginalDisposer` — the seam in Core, the Recycle Bin in the app.** What becomes of the backup after
  a successful replace is a policy decision, so it is a seam modelled on `IDiskSpaceProbe`
  (`Core/Io/IOriginalDisposer.cs`) — which also makes the destructive step deterministically unit-testable
  against a recording fake. Core ships the two OS-free implementations: `KeepOriginalBackupDisposer` (the
  `SplitEngine` default — nothing is ever destroyed, at the cost of a `.vsj-original` file left beside the
  output) and `DeleteOriginalBackupDisposer`. The **Windows** implementation lives in
  `App/Io/RecycleBinOriginalDisposer.cs`, because
  `FileSystem.DeleteFile(…, RecycleOption.SendToRecycleBin)` is a Windows shell call and Core must stay
  OS-free. The production composition root wires it exactly once —
  `new SplitEngine(ffmpegRunner, probe, new RecycleBinOriginalDisposer())` in `MainViewModel` — so a replaced
  original stays restorable after the batch **and after the app exits**, which is what makes an otherwise
  irreversible feature safe to offer. Disposal is invoked **only** after a verified output has taken the
  original's place, and is best-effort by contract: failing to bin the backup leaves a recoverable file, never
  a failed run (SPEC-002 **I44**).

- **(e) The confirmation seam defaults to REFUSING.** `BulkCutViewModel.ConfirmReplaceOriginals` is a
  `Func<int, bool>` initialised to `_ => false`. `RunBatchAsync` counts the rows actually at risk
  (`Items.Count(i => i.IsEnabled && i.IsValidCut)`) and, when the seam declines, **returns before the engine is
  entered at all** — zero engine calls, every original untouched. The real prompt is supplied by the view
  (`BulkCutView.ConfirmReplaceOriginals`): a counted `MessageBoxImage.Warning` Yes/No dialog naming the file
  count and the Recycle-Bin undo, with `MessageBoxResult.No` as the default so a reflex Enter or Escape
  destroys nothing. Defaulting the seam to refuse means a **host that forgets to wire a prompt is inert, not
  dangerous** (SPEC-002 **I46**).

- **(f) Unload, not Stop — only Unload releases the file handle.** The existing pre-run `Player.Stop()` (T-100)
  halts playback but leaves the FFME element open on the selected row's file: `FfmeMediaPlayer.Stop` calls
  `_element.Stop()`, whereas `Unload` calls `_element.Close()` (after `NotifySuperseded()` on the
  [ADR 0016](0016-shared-bulk-preview-player-and-cut-profiles.md) reopen guard) — the close is what drops the
  handle. A still-open handle on the very file being replaced would fail that row, so `RunBatchAsync` branches:
  `Player.Unload()` under `ReplaceOriginal`, `Player.Stop()` otherwise. The screen states the consequence rather
  than hiding it — `OutputNote` reads "Output → REPLACES each original file · originals go to the Recycle Bin",
  and `CollisionIsInert` greys the collision control this mode makes meaningless.

**Alternatives rejected.** **A fourth `CollisionPolicy` value** — it would make `ResolveCollision` return the
SOURCE under an enum whose own header promises the source is never a write target, falsifying that header and
SPEC-002 I22 for *every* policy instead of relaxing it inside one branch reachable only on purpose.
**Delete-then-move** — it opens a window in which the user's bytes exist nowhere; `File.Replace` (and the
rename-aside fallback) never does. **Permanently deleting the original** — `DeleteOriginalBackupDisposer`
exists for callers that genuinely want no undo, and the app deliberately does **not** wire it. **Putting the
Recycle-Bin call in Core** — it would drag a Windows shell dependency into the `net8.0` assembly and break
`CoreIsUiFreeTests`. **A confirmation defaulting to yes, or none at all** — the blast radius is N masters, so
the safe answer has to be the default, at both the seam and the dialog.

## Consequences

**Positive**

- **Every failure path leaves the original byte-identical.** `ReplaceOriginalSafetyTests` pins the paths that
  matter — ffmpeg produced nothing, the disk pre-flight blocked, the request was invalid — each asserting the
  master's bytes survive **and** that no backup was ever handed to the disposer.
- **The non-destructive default is untouched.** `NewFile` is the record default, so a caller passing no
  `Output` keeps today's behaviour exactly;
  `OutputModeTests.NewFile_StillRefusesToWriteTheSource_EvenUnderOverwritePolicy` pins the source-safety guard
  still live, and `ReplaceOriginal_IgnoresTheCollisionPolicy_Identically` pins the new branch's precedence
  across all three policies.
- **The destructive step is undoable, and the undo outlives the process.** A replaced original sits in the
  Recycle Bin, so "I trimmed the wrong second across 40 episodes" is a restore, not a loss.
- **Core stays OS-free and the destructive step stays testable.** The seam keeps the shell call in the app while
  letting Core tests drive replace-in-place against a recording fake, no Recycle Bin involved.
- **The mode costs no extra disk probing.** Resolution under `ReplaceOriginal` is pure path math — no
  `File.Exists` probe of the desired path, no auto-suffix search — leaving the single batch pre-flight as the
  only disk measurement (SPEC-002 **I38**).

**Negative**

- **`.vsj-original` is now a meaningful name on disk.** An interrupted run can leave one beside the output; the
  next replace of that same file deletes it first, best-effort, but nothing else sweeps it.
- **`File.Replace` is single-volume by construction.** That holds today only because the temp folder is created
  *inside* the output directory; the rename-aside fallback is the net for filesystems that refuse the call, and
  it is the less atomic of the two routes.
- **The feature's safety depends on wiring the host supplies.** Both the confirmation prompt and the
  Recycle-Bin disposer are injected — a new host inherits a refusing seam (inert, correct) but Core's default
  `KeepOriginalBackupDisposer`, i.e. backups accumulating rather than binned.
- **A live option is now deliberately inert in the UI.** While the mode is on the collision policy does
  nothing; `CollisionIsInert` has to keep saying so, or the screen starts lying.
- **The `Exact` route into a replace destination does not inherit these guarantees.** `CutPrecision.Exact` rows
  are produced by `SmartCutEngine`, whose `MoveIntoPlace` is a `File.Delete(dest)` + `File.Move` — no backup,
  no disposer, no Recycle Bin — so (c) and (d) describe the **lossless** route into a `ReplaceOriginal`
  destination only (SPEC-002 **I53**). The two axes are freely combinable and nothing currently blocks the
  pair.

**Forced follow-ons** (this decision *causes* these; they are not optional)

- **Any engine that can be pointed at the source must adopt verify-then-replace, or be excluded from
  `ReplaceOriginal`.** `SmartCutEngine` is the open instance today; a third producing engine would be the next.
- **The verify pass must stay AHEAD of the move loop.** Folding the existence check back into the loop — an
  easy, innocent-looking refactor — silently reintroduces the clobber-then-fail window (b) exists to close.
- **The shipping composition root must keep injecting a Recycle-Bin disposer**, and must never wire
  `DeleteOriginalBackupDisposer`: with the Delete implementation the run becomes irreversible while every other
  guarantee still reads as satisfied.
- **Any future surface that opens a row's file must release it before a replace run**, the way the preview now
  does — a `Stop` where an `Unload` is needed fails the very row the user asked to replace.
- **The mode's wording must keep telling the truth.** `OutputNote`, `CollisionIsInert` and the counted dialog
  are the only places the user learns the blast radius; a change to the destination rules has to change them
  too.
