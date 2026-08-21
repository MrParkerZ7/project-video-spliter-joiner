# ADR 0015: Batch intro/outro trim reuses SplitEngine's single-segment path — no second ffmpeg code path

## Status

Accepted.

## Context

The Bulk Cut tab (design [D-004](../design/D-004/README.md), epic G-036) batch-trims
the intro — and optionally the outro — off many videos at once, keeping the middle
of each. Structurally, **a bulk trim is a Split that keeps exactly one middle
segment**: for a source with an intro-end at `t₁` (and an optional outro-start at
`t₂`), the wanted output is the single segment `[t₁ .. (t₂ | EOF)]` — precisely one
of the parts a two-cut Split would produce.

The forces in tension:

- **The app's whole reason to exist is lossless `-c copy` ([ADR 0001](0001-stream-copy-only.md)).**
  Every write must reproduce the source bitstream byte-for-byte; a bulk trim is no
  exception. That invariant is not a local flag — it *forces* keyframe-snapped cut
  boundaries, temp-then-move cancel-safety, a runtime encoder-token denylist, and a
  disk pre-flight, all already built and guarded on the split path.
- **A second ffmpeg code path would double the surface that must uphold all of it.**
  A bespoke "bulk trimmer" that built its own ffmpeg command would have to re-implement
  (and keep in sync, forever) the copy-invariant assertion, the nearest-keyframe snap,
  the `-ss`/`-to`/`-avoid_negative_ts` argument shape, the EOF-omits-`-to` rule, the
  temp-then-move rename, and the ENOSPC/DiskFull mapping — every one of which is a place
  the lossless promise could silently break.
- **The one genuinely new problem is orchestration, not cutting.** Bulk adds a *list*
  (N sources), *two* cut points per row, an apply-to-all gesture, an aggregate progress
  surface, and a batch runner — none of which touches how a single file is cut.

## Decision

Adopt **stream-trim-by-reuse**: a bulk trim is expressed as a single-kept-segment
`SplitRequest` and run through the **existing** `ISplitEngine.SplitAsync` per-segment
`-c copy` path. There is **no new args-builder, no new engine method, and no second
ffmpeg code path** — Bulk adds only orchestration around the unchanged split engine.

- **(a) Reuse `ISplitEngine.SplitAsync` / `SplitRequest` wholesale — one kept-middle
  segment per row.** Each row builds a `SplitRequest` with `CutPoints = [introEnd]`
  (keep to EOF) or `[introEnd, outroStart]`, `NamingPattern = "{name}_trimmed{ext}"`
  (`KeptSegmentSelector.TrimmedNamingPattern` — deliberately no `{index}` token), and a
  single-element `SelectedSegmentIndices = [keptIndex]`. That drives the engine's
  **per-segment subset path** (one `-ss/-to -map 0 -c copy -avoid_negative_ts make_zero`
  run; the plan's final/EOF part omits `-to`; interior `-to` is the snapped end) — the
  same code T-049 shipped for "export a subset of parts". The copy-invariant assertion
  (`SatisfiesCopyInvariant` in `SplitEngine`), keyframe-snap, temp-then-move, and the
  free-space pre-flight are therefore all inherited, not re-written.

- **(b) The kept-index cure that shipped: `KeptSegmentSelector.ResolveKeptIndex`,
  post-plan.** A bulk trim keeps "the middle segment", but its 1-based index is **not
  always 2**: `SplitPlanner.Plan` drops a cut that snaps to ~0 or ~duration, merges
  cuts within ~10ms, and drops a snap colliding with a neighbour — so if the intro-end
  snaps to ~0 it is dropped and the kept part becomes segment **1** (`[0..outro]`), else
  the intro survives and it is segment **2** (`[introEnd..outro|EOF]`). The cure that
  shipped runs the **real** `SplitPlanner.Plan` (never re-deriving its drop/merge/snap
  rules) and reads back which planned segment starts at the snapped intro-end
  (`src/Core/Split/KeptSegmentSelector.cs`). The alternative considered in D-004 —
  emitting one `PerSegment` call directly and bypassing `SelectedSegmentIndices` — was
  **not** taken; routing through `SelectedSegmentIndices` keeps a single selection path
  in the engine. When both boundaries collapse (no cut survives), `Plan` throws, and the
  builder translates that into a distinct `NoOpTrimException` the runner records as
  *Skipped*, never *Failed*.

- **(c) Apply-to-all: outro measured FROM END, intro absolute-from-start.** Copying one
  row's cut points to the others sets the intro-end as an absolute time-from-start, but
  the outro as a **time-from-end** (`Duration − outroStart`) re-anchored on each target's
  own duration, so same-series episodes of *different* lengths line up (you trim the same
  N seconds off each tail). Every target **re-snaps against its own keyframes and
  re-validates**; rows the copied time invalidates are **reported** (in the apply-to-all
  report), never silently dropped.

- **(d) Collision default AutoSuffix; per-run Overwrite; the source is never a write
  target.** The base output is `<source dir>/<name>_trimmed<ext>`. If that path is taken,
  the default `AutoSuffix` policy appends `_2`, `_3`, … until a free name is found — an
  existing file is never clobbered. A per-run **Overwrite** toggle switches to the
  `Overwrite` policy for a replace-in-place run. Under **every** policy, a resolution that
  would land on the input path is forced onto an AutoSuffix name instead, so a run can
  never overwrite the very file it is reading.

- **(e) Sequential, failure-isolated batch, orchestrated in Core.** The batch loop lives
  in `BulkTrimEngine` (`src/Core/Bulk/`), not the view model. Rows run **one after
  another** (v1 is sequential) and each is **failure-isolated** — a mid-run ENOSPC or a
  bad row is recorded `Failed`/`Skipped` and the batch continues to the next. **Cancel**
  stops before the next row and classifies the in-flight row `Cancelled`; because each
  row is an ordinary split, its temp is swept and no partial is ever moved into place, so
  already-finished outputs are kept. A batch-level disk pre-flight blocks the *whole*
  batch up front only on a knowable per-drive shortfall (unmeasurable drives skip the
  check — never a false-positive block).

## Consequences

**Positive**

- **One copy-invariant choke-point.** Because every row is an ordinary `SplitRequest`,
  the `-c copy` guarantee is asserted in exactly one place (`SplitEngine`), the same one
  ADR 0001 protects. A bulk trim cannot re-encode unless a plain split could.
- **Correctness inherited for free.** Keyframe-snap, temp-then-move cancel-safety, the
  free-space pre-flight, the ENOSPC→DiskFull mapping, and the `FfmpegErrorMapper` /
  `ErrorLogWriter` diagnostics all come from the reused path — no bulk-specific
  re-implementation to drift.
- **Row independence falls out naturally.** Each row is its own single-file split, so
  failure isolation, mixed codecs across the list, and per-row progress require no extra
  machinery — they are simply what "run N independent splits" means.

**Negative**

- **The kept-index math is a real edge to test.** "Keep the middle" is index 1 or 2
  depending on whether the intro cut was dropped by the planner; `KeptSegmentSelector`
  carries that logic and its own unit tests, and a future change to `SplitPlanner`'s
  drop/merge rules must keep it honest.
- **An aggregate `OperationViewModel` is required.** The taskbar button and window title
  bind exactly one `CurrentOperation`, so the tab owns an aggregate op (weighted,
  monotonic overall bar) on top of each row's own per-row op — `MainViewModel` routes a
  3-way `switch` on `SelectedTabIndex` (0/1/2) for `CurrentOperation` and the Load/Clear
  labels.
- **N-file keyframe scans are a thundering herd to throttle.** Adding many videos would
  otherwise fire N concurrent ffprobe keyframe scans; the tab caps concurrency with a
  shared `SemaphoreSlim(3)` handed into every row.

**Forced follow-ons** (this decision *causes* these; they are not optional)

- **Pending snaps must resolve before a request is built.** Both handles are created
  optimistically (`snapPending`), so a run must wait until every enabled row is
  keyframes-ready (`CanRunBatch` gates on it) — otherwise a request would be built on
  identity (un-snapped) times and cut in the wrong place.
- **Apply-to-all must report, not silently drop, invalidated rows.** Re-anchoring the
  outro from-end can push a copied cut out of a shorter target's range; those rows are
  surfaced to the user rather than quietly excluded.
- **Core stays WPF-free.** `BulkTrimEngine` and `KeptSegmentSelector` are Core types that
  reference no WPF, so `CoreIsUiFreeTests` stays green; the two view models
  (`BulkCutViewModel` / `BulkItemViewModel`) and the view-only scrub render live in the
  `App` assembly.
