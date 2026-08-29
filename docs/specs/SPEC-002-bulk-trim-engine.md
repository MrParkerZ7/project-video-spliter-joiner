---
id: SPEC-002
slug: bulk-trim-engine
area: core
title: Bulk trim engine (keep one middle segment)
status: current
sources:
  - src/Core/Split/KeptSegmentSelector.cs
  - src/Core/Bulk/BulkTrimEngine.cs
  - src/Core/Bulk/KeptMiddleRequestBuilder.cs
  - src/Core/Bulk/BulkTrimItem.cs
  - src/Core/Bulk/BulkTrimItemResult.cs
  - src/Core/Bulk/BatchResult.cs
  - src/Core/Bulk/BatchOutcome.cs
  - src/Core/Bulk/ItemOutcome.cs
  - src/Core/Bulk/CollisionPolicy.cs
  - src/Core/Bulk/BulkTrimOptions.cs
  - src/Core/Bulk/BulkTrimProgress.cs
  - src/Core/Bulk/NoOpTrimException.cs
  - src/Core/Bulk/IDiskSpaceProbe.cs
  - src/Core/Bulk/IBulkTrimEngine.cs
  - src/Core/Bulk/IBulkTrimRequestBuilder.cs
  - src/Core/Bulk/OutputMode.cs
  - src/Core/Bulk/CutPrecision.cs
  - src/Core/Io/IOriginalDisposer.cs
  - src/Core/Split/SplitEngine.cs
  - src/Core/Split/SmartCutEngine.cs
serves-goal: [G-036, G-041, G-042]
updated: 2026-08-28
---

## What
The Bulk Cut Core feature (design D-004) batch-trims many source videos at once, keeping exactly the
middle segment `[introEnd → outroStart | EOF]` of each. Its load-bearing decision is that **a bulk
trim IS a Split that keeps one middle segment** — it adds NO second ffmpeg path. `KeptSegmentSelector`
(a pure, I/O-free helper) resolves which planned segment is the kept middle and assembles the
single-kept-segment `SplitRequest`; `KeptMiddleRequestBuilder` does the same in production (probing
each source, delegating the index math, honoring the collision-resolved output path);
`BulkTrimEngine.RunAsync` is the UI-free orchestrator that runs the rows **sequentially** and
**failure-isolated** over the existing `ISplitEngine`, owning the batch loop, cancel semantics,
collision policy, a batch disk pre-flight, progress rollup, and a per-row result ledger. Every row
still flows through `ISplitEngine.SplitAsync`, so the `-c copy` stream-copy invariant, temp-then-move
cancel-safety, and per-run disk pre-flight are all inherited for free — the one exception being a row
the batch opts into `CutPrecision.Exact` for, which is routed to the separate `ISmartCutEngine` and
falls back to that same lossless path whenever exact cutting is unavailable. Two later axes ride on the
same orchestrator without disturbing it: `OutputMode` chooses **where** a row writes (a new `_trimmed`
file beside the source by default, or over the original — verified first, then swapped in behind a
backup), and `CutPrecision` chooses **how exactly** the requested cut time is honored.

## Why
Users trimming intros/outros off a folder of clips need one action, not N. Reusing the single-segment
Split path (rather than writing a new batch ffmpeg command) means the batch inherits every safety
property the Split engine already proved (stream-copy losslessness, no partial output on cancel,
keyframe snapping) and only adds the batch-level concerns: order, isolation, collisions, disk
head-room, cancellation, and a completeness-guaranteed ledger the UI can report and retry from. The
kept-middle index is deliberately delegated to a pure helper because it is **not always 2** — the real
planner drops a cut that snaps to ~0 or ~duration, so the kept part can begin at file start (index 1),
and the both-boundaries-collapse case must be an honest no-op rather than a bogus trim.

## Scope
**In:** kept-middle index resolution across every planner outcome (`ResolveKeptIndex`); single-kept-segment
request assembly (`BuildKeptMiddleRequest`, `KeptMiddleRequestBuilder`); the batch orchestration contract
(sequential run, failure isolation, cancel sweep, collision policy + source-safety, batch disk
pre-flight, no-op skip, ledger completeness, batch-outcome resolution, progress rollup); the
output-destination axis (`OutputMode`, and the verify-then-replace + backup + `IOriginalDisposer` contract it
drives in `SplitEngine`); and the cut-precision axis (`CutPrecision` routing to `ISmartCutEngine`, with the
per-row fallback and its warning).

**Out:** the underlying Split engine's own behavior (per-segment `-c copy` command construction, temp-then-move,
keyframe snapping, the `SplitPlanner` drop/merge/snap rules) — that is the core Split-engine spec; this
spec only asserts the *reuse* and the batch layer on top. Likewise the smart-cut engine's own internals
(planner strategies, head-encode parameter matching, the three-run shape) — SPEC-001's frame-exact section
owns those; this spec asserts only the *routing* and the fallback contract. Also out: the App/WPF Bulk Cut
tab, its VMs (including the Replace-originals / Exact-cut surfaces and the confirmation dialog itself), the
preview player, and cut profiles (G-037) — those are app-layer specs.

## Current behavior & invariants

### Reuse (a bulk trim IS a single-segment Split)
- **I1** — The request a bulk trim produces carries a single-element `SelectedSegmentIndices` and runs through
  `ISplitEngine.SplitAsync` as **exactly one** per-segment `-ss/-to -c copy` command — no segment muxer
  (`segment`), no encoder tokens, no second ffmpeg path. (`KeptSegmentSelector.BuildKeptMiddleRequest`,
  `KeptMiddleRequestBuilder.BuildAsync` → `SelectedSegmentIndices: new[]{keptIndex}`.)

### Kept-middle index resolution (`KeptSegmentSelector.ResolveKeptIndex`)
- **I2** — Intro-end and outro-start both survive planning → the kept middle is **index 2**
  (parts `[0..intro],[intro..outro],[outro..EOF]`).
- **I3** — Intro-end survives, no outro (`outroStart == null`) → the kept part `[intro..EOF]` is the final part,
  **index 2**.
- **I4** — Intro-end snaps to ~0 and is dropped by the planner (with an outro present) → the kept part now
  begins at file start `[0..outro]`, so the kept **index is 1** (the "index-not-always-2" case).
- **I5** — Outro-start snaps to ~duration and is dropped → kept **index stays 2** and runs to EOF.
- **I6** — Empty keyframes list → snapping is a no-op and the index is resolved on the **raw (unsnapped) cut
  times** (guards `SnapToNearestKeyframe` throwing on an empty list); result is index 2 for the interior case.
- **I7** — Both boundaries collapse (intro snaps to ~0 **and** no outro, so no cut survives) → the
  `SplitException` thrown by `SplitPlanner.Plan` **propagates** unchanged; no bogus index is returned.

### Single-kept-segment request shape (`BuildKeptMiddleRequest`)
- **I8** — The built `SplitRequest` writes to the **source folder**, uses `TrimmedNamingPattern`
  (`"{name}_trimmed{ext}"`, no `{index}` token), and selects exactly the one kept index; the original
  source file is never a write target and stays byte-for-byte unmodified.
- **I9** — Cut points = `[introEnd, outroStart]` when an outro is present, `[introEnd]` (keep to EOF) when
  `outroStart` is null.
- **I10** — An interior kept trim carries `-to`; a kept-to-EOF trim (no outro, final part) **omits** `-to`.

### Production builder (`KeptMiddleRequestBuilder`)
- **I11** — The builder probes the source for duration + keyframes, delegates the kept index to
  `ResolveKeptIndex`, and honors the runner's collision-resolved effective path **verbatim** —
  `OutputDir` = its folder, `NamingPattern` = the literal file name (no `{index}`), and the passed
  `overwrite` flag threaded onto the request.
- **I12** — When `ResolveKeptIndex` throws the both-collapse `SplitException` (I7), the builder translates it
  into a distinct `NoOpTrimException` (the runner maps that to Skipped, never Failed).
- **I13** — A probe failure (`ProbeResult` not `ProbeSucceeded`) → the builder throws `SplitException`
  (`"Cannot trim '…': <reason>"`), which the runner records as a **Failed** row — distinct from the no-op
  path.

### Batch orchestration (`BulkTrimEngine.RunAsync`)
- **I14** — `null` or empty `items` → a no-op `BatchResult(BatchOutcome.Completed, empty ledger)`; the engine
  is never called.
- **I15** — Rows run **sequentially**, head-to-tail, in input order, each as one `SplitAsync` call for every
  runnable (non-pre-decided) row.
- **I16** — Failure isolation: a row whose trim throws `SplitException` is recorded **Failed** (with a mapped
  `UserFacingError`) and the batch **continues** — every remaining row is still attempted.
- **I17** — Cancel mid-run: the in-flight row (an `OperationCanceledException` with a genuinely-cancelled
  token) is recorded **Cancelled** with its ffmpeg temp already swept (no partial moved into place); the
  loop stops; every not-yet-started row is **NotStarted**; earlier **Done** rows are kept; batch outcome is
  **Cancelled**.
- **I18** — A `NoOpTrimException` from the builder → the row is **Skipped** (deliberate no-op, not Failed), the
  engine is not called for it, and the batch continues.

### Collision policy (`CollisionPolicy`, resolved before any ffmpeg runs)
- **I19** — `AutoSuffix` (default): a colliding output resolves to the first free `<stem>_2<ext>`, `_3`, …; the
  existing file is left untouched and the request runs with `overwrite == false`.
- **I20** — `Skip`: an existing output → the row is **Skipped** with **zero** engine calls.
- **I21** — `Overwrite`: an existing output → the request runs with `SplitRequest.Overwrite == true` and the
  base output name is kept.
- **I22** — Source-safety (any policy): the **source path is never a write target** — a desired output equal to
  the source is forced onto an `AutoSuffix` name with `overwrite == false`, even under `Overwrite`, and the
  source bytes stay unmodified.
- **I23** — A collision-resolution exception (e.g. `ResolveAutoSuffix` exhausting 10 000 attempts) is isolated
  to its own row (recorded **Failed** with the wrapped error) and never aborts the batch.

### Batch disk pre-flight (`IDiskSpaceProbe`)
- **I24** — A knowable per-drive shortfall (a measurable drive's free bytes < Σ source sizes + a 16 MB margin)
  **blocks the whole batch before any ffmpeg runs**: every row is **NotStarted** carrying a `DiskFull`
  error, batch outcome is **Blocked**, and the engine is called zero times.
- **I25** — Source size is used as a safe upper bound (trimming only removes bytes); an **unmeasurable** drive
  (probe returns null) or an **unsizable** input skips that root's check — never a false-positive block, so
  the batch runs.
- **I26** — The pre-flight per-root size estimate **excludes** rows already decided at pre-resolve
  (collision-Skipped or resolution-Failed) — only runnable rows count toward the required space.

### Ledger, outcome, and progress (`BatchResult`, `BulkTrimItemResult`, `BulkTrimProgress`)
- **I27** — Ledger completeness: `BatchResult.Items` has exactly one entry per input row, in **input order**,
  preserving each row's identity and opaque `Tag`.
- **I28** — Batch-outcome resolution: **Completed** iff every row is Done; **CompletedWithFailures** iff any row
  is Failed or Skipped (and not cancelled); **Cancelled** iff cancelled; **Blocked** iff the disk pre-flight
  blocked.
- **I29** — Ledger-entry fields: a **Done** row records the effective `OutputPath` and surfaces the
  `SplitResult`'s non-fatal warnings (coarse GOP, no keyframes, …); a **Failed** row records the mapped
  `UserFacingError`; both are null on the other outcomes.
- **I30** — A `SplitException` carrying ffmpeg stderr is mapped to a **categorized** `UserFacingError` (e.g. an
  ENOSPC stderr signature → `ErrorCategory.DiskFull`), keeping the log path + full text.
- **I31** — Progress: `OverallFraction` is monotonic non-decreasing, reaches `1.0` on normal completion, and the
  first reported sample is the `Preflight` phase.
- **I32** — `BatchResult` tallies (`DoneCount`, `FailedCount`, `SkippedCount`, `FailedItems`) reflect the ledger
  outcomes.

### Output destination (`OutputMode`, resolved with the collision policy before any ffmpeg runs)
- **I33** — `OutputMode` (T-121) is a **third, orthogonal axis** on `BulkTrimOptions` —
  `(Collision, Output, Precision)` — deliberately **not** a fourth `CollisionPolicy` value: collision policy
  answers *"what if the destination is taken?"*, output mode answers *"which destination?"*. Folding it in
  would falsify `CollisionPolicy`'s own header contract and I22 ("the source is NEVER a write target under
  any policy").
- **I34** — `OutputMode.NewFile` is the record **default**, so a caller that passes no `Output` keeps today's
  non-destructive behavior (I19–I23) unchanged.
- **I35** — Under `NewFile` the source-safety guard stays **fully live**: a desired output equal to the input
  is still forced onto an `AutoSuffix` name with `overwrite == false` under every policy, `Overwrite`
  included (I22 verbatim). The guard is bypassed for `ReplaceOriginal` — and for nothing else.
- **I36** — Under `ReplaceOriginal`, `ResolveCollision` returns
  `(Path.GetFullPath(item.InputPath), overwrite: true, skip: false)` from one early branch **before** the
  collision switch is reached: the effective path is the **input**, the request runs with `Overwrite == true`,
  and the row is never pre-skipped.
- **I37** — `CollisionPolicy` is **ignored** under `ReplaceOriginal` — `AutoSuffix`, `Skip`, and `Overwrite`
  resolve identically (same effective path, same overwrite flag, exactly one builder dispatch per row), and an
  already-existing file at the desired `_trimmed` path changes nothing.
- **I38** — Resolution under `ReplaceOriginal` is pure path math: no `File.Exists` probe of the desired path
  and no auto-suffix search; the only disk measurement in a row's pre-run remains the single batch pre-flight
  (I24–I26).
- **I39** — The builder honors that effective path **verbatim** per I11 — `OutputDir` = the input's folder,
  `NamingPattern` = the input's literal file name — so the one kept segment is planned to land **on** the
  original, and the engine recognizes it by path identity rather than by a flag (I41).

### Replacing the original in place (`SplitEngine`, `IOriginalDisposer`)
- **I40** — Verify-then-replace: `MoveTempSegmentsIntoPlace` checks that **every** produced temp part exists
  before it touches **any** destination; a missing part throws `SplitException`
  (`"… was not produced by ffmpeg …"`) with zero destinations written. Under `ReplaceOriginal` a destination
  IS the user's master, so discovering a shortfall halfway through the move loop would mean an
  already-clobbered original — this ordering is what makes "a failed run leaves the original intact" true.
- **I41** — Destination **identity**, not a mode flag, selects the replace path: a planned output whose full
  path equals the input's goes through `ReplaceOriginalInPlace`; every other destination keeps the ordinary
  delete-if-exists → `File.Move`.
- **I42** — The replace is atomic-with-a-backup:
  `File.Replace(temp, original, original + ".vsj-original", ignoreMetadataErrors: true)` — **never** a
  delete-then-move, so the bytes are never in a state where they exist nowhere. A stale `.vsj-original` from
  an earlier interrupted run is deleted first, best-effort (a failure there is non-fatal — the replace itself
  surfaces any real problem).
- **I43** — Volumes that cannot `File.Replace` (`PlatformNotSupportedException` / `IOException` /
  `UnauthorizedAccessException` — exFAT, some SMB shares) fall back to **rename-aside**: move the *original*
  to the backup FIRST, then move the temp into place; if that second move fails, the backup is moved **back**
  over the original and the exception rethrown — so even a failed fallback ends with the user's file present.
- **I44** — The backup's fate is the injected `IOriginalDisposer`'s decision, and it is called **only** after
  a verified output has taken the original's place. Core defaults to **keeping** it
  (`KeepOriginalBackupDisposer`, the `SplitEngine` default); `DeleteOriginalBackupDisposer` removes it; the
  app injects `RecycleBinOriginalDisposer`, so a replaced original stays recoverable after the batch — and
  after the app exits. Every implementation is best-effort: failing to dispose the backup never fails an
  otherwise-successful run (the trimmed output is already safely in place).
- **I45** — **Every** failure path leaves the original byte-identical with **zero** destructive calls (no
  replace, no backup, no disposer call): ffmpeg produced nothing (I40 fires first), the per-run disk
  pre-flight blocked (`EnsureEnoughFreeSpace` throws before any ffmpeg run), and an invalid request
  (`ValidateRequestShape` rejects before the probe, the plan, or a run).
- **I46** — The destructive mode is additionally gated **outside** this engine: the run blocks on a **counted**
  confirmation whose seam (`BulkCutViewModel.ConfirmReplaceOriginals`, a `Func<int, bool>`) **defaults to
  refusing**, so a host that forgets to wire a prompt can never silently replace masters. Declining returns
  before `RunAsync` is entered — zero engine calls, every original untouched. (The gate itself, and the
  preview `Unload` that releases the file handle so the replace can succeed at all, are app-layer — SPEC-011.)

### Cut precision routing (`CutPrecision` → `ISmartCutEngine`)
- **I47** — `CutPrecision` (T-125) is the **third axis** on `BulkTrimOptions`, orthogonal to both `Collision`
  and `Output`; `Lossless` is the record **default**, so an unchanged caller keeps the keyframe-snapped
  stream-copy path of I1.
- **I48** — A row is routed to
  `ISmartCutEngine.CutAsync(InputPath, IntroEnd, OutroStart, <effective path>, <row progress>, ct)` **iff**
  `Precision == Exact` **and** a smart-cut engine was injected. On a non-fallback result that row is **Done**
  from the smart cut alone — `IBulkTrimRequestBuilder.BuildAsync` and `ISplitEngine.SplitAsync` are not called
  for it (so the builder's no-op translation, I12/I18, is not consulted for that row).
- **I49** — A **null** smart-cut engine (the ctor overloads that take none, or an explicitly-null one) means
  every row stays lossless whatever `Precision` says: `Exact` degrades silently rather than failing, so
  existing callers keep working unchanged.
- **I50** — Per-row fallback: `FellBack == true` → that row runs the ordinary lossless path (build request →
  `SplitAsync`) and is recorded exactly like a `Lossless` row. The fallback is **per row** — it never aborts,
  downgrades, or re-routes the rest of the batch.
- **I51** — The fallback reason is surfaced as a **row warning** —
  `"exact cut unavailable (<reason>) - cut snapped to the nearest keyframe"` — when `FallbackReason` is present
  **and** `Strategy != SmartCutStrategy.PureCopy`. A `PureCopy` fallback (the requested time was already on a
  keyframe, so the lossless cut IS exact there) adds **no** warning. Any `SplitResult` warnings from the
  fallback run are appended to it, so I29's warning surface is unchanged.
- **I52** — Exact rows sit inside the same batch contract: the smart cut writes to the **same**
  collision-resolved effective path and reports through the same per-row progress reporter, so collision
  resolution + source safety (I19–I23, I33–I39), the batch disk pre-flight (I24–I26), cancel (I17), failure
  isolation (I16), and ledger completeness (I27–I32) all apply identically to `Exact` and `Lossless` rows.
- **I53** — On the Exact route the produced file is put in place by `SmartCutEngine`'s own delete-then-move
  (`MoveIntoPlace`), not by `ReplaceOriginalInPlace` — so I42-I44's backup + disposer guarantees describe the
  **lossless** route into a `ReplaceOriginal` destination.
- **I54** — Because of I53, `CutPrecision.Exact` is **refused** when `Output == ReplaceOriginal`: the row
  takes the lossless path and the smart cutter is never handed a destination that is its own source. Under
  `ReplaceOriginal` the resolved destination IS the input (I40), and `MoveIntoPlace` deletes its destination
  before moving — which would hard-delete the user's original with no backup, no disposer and no
  restore-on-failure, and lose the file outright if the move then failed.
- **I55** — The I54 refusal is **announced, never silent**: the row carries
  `"exact cut unavailable (replacing originals) - cut snapped to the nearest keyframe"`, reusing I51's
  fallback wording, because the user asked for exact cutting and did not get it. The refusal is scoped to
  `ReplaceOriginal` — an ordinary Exact row writing beside its source is unaffected.

## Links
- Design: D-004 (`docs/design/D-004/README.md`, `docs/design/D-004/core-flow.md`)
- Goals: G-036 (Build the Bulk Cut tab; tasks T-094→T-098); G-041 (make keyframe snapping visible + the
  opt-in replace-original output mode; tasks T-121→T-123); G-042 (frame-exact "Exact cut"; tasks T-124/T-125)
- ADR: 0015 — bulk trim reuses Split single-segment (`docs/adr/0015-bulk-trim-reuses-split-single-segment.md`);
  0018 — frame-exact cutting as a separate opt-in engine (`docs/adr/0018-smart-cut-exact-trimming.md`)
- Related specs: SPEC-001 (the `-c copy` / temp-then-move / planner contract this feature reuses, plus its
  frame-exact `SmartCutEngine` section — the engine `CutPrecision.Exact` routes to); SPEC-011 (the Bulk Cut
  app layer: the preview player, cut profiles, the Replace-originals + Exact-cut surfaces and the counted
  confirmation dialog)
- Key code: `src/Core/Split/KeptSegmentSelector.cs`, `src/Core/Bulk/BulkTrimEngine.cs`,
  `src/Core/Bulk/KeptMiddleRequestBuilder.cs`, `src/Core/Bulk/{BulkTrimItem,BulkTrimItemResult,BatchResult,BatchOutcome,ItemOutcome,CollisionPolicy,BulkTrimOptions,BulkTrimProgress,NoOpTrimException,IDiskSpaceProbe,OutputMode,CutPrecision}.cs`,
  `src/Core/Io/IOriginalDisposer.cs`, `src/Core/Split/SplitEngine.cs` (`MoveTempSegmentsIntoPlace` /
  `ReplaceOriginalInPlace`), `src/Core/Split/SmartCutEngine.cs`, `src/App/Io/RecycleBinOriginalDisposer.cs`
