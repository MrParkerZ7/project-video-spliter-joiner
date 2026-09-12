---
id: SPEC-001
slug: stream-copy-split
area: core
title: Stream-copy split engine
status: current
sources:
  - src/Core/Split/SplitEngine.cs
  - src/Core/Split/SplitArgsBuilder.cs
  - src/Core/Split/SplitPlan.cs
  - src/Core/Split/SplitRequest.cs
  - src/Core/Split/SplitResult.cs
  - src/Core/Split/SplitSegment.cs
  - src/Core/Split/SplitException.cs
  - src/Core/Split/SmartCutEngine.cs
  - src/Core/Split/SmartCutPlanner.cs
  - src/Core/Split/SmartCutArgsBuilder.cs
  - src/Core/Split/PartProgress.cs
serves-goal: [G-001, G-005, G-042]
updated: 2026-09-12
---

## What
The split engine cuts one media file into contiguous segments at user-chosen cut points using
lossless ffmpeg stream-copy (`-c copy`) — no re-encode, so it is near-instant and resolution-independent.
Every requested cut is validated (sorted, de-duped, range-checked) and snapped to the nearest keyframe
so the copied boundary is clean, then the segments are extracted and written. Two extraction paths exist:
the full contiguous set goes through the single-pass segment muxer (`-f segment`), while a strict subset
of parts goes through a per-segment `-ss/-to -c copy` run per selected part. Planning is a pure,
ffmpeg-free function (`SplitPlanner.Plan`); the engine (`SplitEngine.SplitAsync`) adds probing, disk
pre-flight, temp-then-move cancel-safety, overwrite refusal, and friendly error mapping.

## Why
v1.0's headline promise (G-001) is fast, lossless splitting: cut a video at chosen points with no
quality loss and no minutes-long re-render. Stream-copy delivers that, but copy can only cut cleanly on
keyframes — so the engine must snap cuts, normalise messy input (unsorted / duplicate / out-of-range
cuts) into a valid plan, and guarantee it never silently re-encodes. Because copy reproduces the source
bytes, it is resolution-independent (G-005: 4K stays near-instant). The invariants below are the contract
that keeps the operation lossless, cancel-safe, and honest about what it wrote.

## Scope
**In:** The `-c copy` keyframe-snapped split — cut-point planning (`SplitPlanner`: sort / drop / merge /
snap rules), the segment-muxer vs per-segment extraction routing, `SplitArgsBuilder` ffmpeg-command shape
and the copy invariant (`SatisfiesCopyInvariant` / `ForbiddenEncoderTokens`), segment selection
(`SelectedSegmentIndices`), overwrite refusal, disk pre-flight, temp-then-move cancel-safety, request-shape
validation, ffmpeg-failure mapping, output naming, and the `SplitResult` / `SplitSegment` contract; and the
separate frame-exact engine (`SmartCutPlanner` / `SmartCutArgsBuilder` / `SmartCutEngine`, T-124, I40–I48);
and the per-part export-progress channel — the pure `PartMapping.PartAt` time→part function and both
extraction paths' `PartProgress` emission (T-069, I49–I64).
**Out:** Keyframe probing / snapping internals (`IMediaProbe.SnapToNearestKeyframe`, `GetKeyframesAsync`,
`AverageGop` — cited but owned by the probe spec); the ffmpeg runner and error-mapper internals
(`IFfmpegRunner`, `FfmpegErrorMapper`); staged `OperationStatus` progress reporting (T-044) except where it
gates extraction routing; the App-side mapping of per-part samples onto the "Parts to export" rows
(`SplitViewModel.ApplyPartProgress` / `SplitSegmentViewModel.WriteState` — SPEC-010); the bulk-trim
orchestrator and `KeptSegmentSelector` (D-004, its own spec — it
merely reuses this engine); the join engine. Also out: the **replace-the-original swap**
`MoveTempSegmentsIntoPlace` performs when a planned destination IS the input. That guarantee — replace
atomically behind a `.vsj-original` backup, rename-aside with restore-on-failure where the volume cannot,
`IOriginalDisposer` deciding the backup's fate, and the disposer called only once the swap has committed —
lives in `src/Core/Io/OriginalReplacer.cs`. `SplitEngine.ReplaceOriginalInPlace` is a one-line delegation to
it (T-130) and is **not** its only caller: `BulkTrimEngine` calls the same method on the frame-exact route.
It is specified in SPEC-002 I40–I44, with the frame-exact caller at I56–I60 — not here.

## Current behavior & invariants

### Planning — `SplitPlanner.Plan` (pure)
- **I1** — `Plan` over N surviving snapped cuts produces N+1 contiguous `PlannedSegment`s covering
  `[0..duration]`: segment 0's `SnappedStart` is `0`, the final segment's `SnappedEnd` is `duration`, and
  each segment's `SnappedStart` equals the previous segment's `SnappedEnd`.
- **I2** — Requested cuts are sorted ascending before planning; input order and duplication do not change
  the resulting segment order (`requestedCuts.OrderBy(c => c)`).
- **I3** — A requested cut at `<= 0` or `>= duration` is dropped (non-fatal) and recorded as an
  "outside the file bounds … was ignored" warning.
- **I4** — Two kept cuts closer than `Epsilon` (10 ms) are merged: the later one is dropped with a
  "within 10ms of an earlier cut and was merged" warning.
- **I5** — Each surviving cut is snapped to the nearest keyframe via the injected snapper; the signed snap
  offset is recorded as `PlannedSegment.StartDelta` (may be negative when the boundary snaps earlier).
- **I6** — Two cuts whose snapped times land within `Epsilon` of each other (collide on the same keyframe)
  → the colliding boundary is dropped with a "colliding with an earlier snapped cut — dropped" warning.
- **I7** — A cut whose SNAPPED time lands `<= 0` or `>= duration` is dropped with an "outside the file
  bounds — dropped" warning (post-snap guard, distinct from I3's pre-snap check).
- **I8** — When the probed `keyframes` list is empty, surviving cuts are left UNSNAPPED (raw requested
  times, `StartDelta = 0`) and the split still proceeds (a legal, if not guaranteed-clean, split).
- **I9** — A coarse GOP (`averageGop > 2s`) combined with a snap that moves more than `0.5s` raises a
  coarse-GOP precision warning ("this file has a coarse GOP … cuts cannot be precise").
- **I10** — If NO cut survives range validation (every requested cut at/beyond bounds) → `SplitException`
  ("No valid cut points remain after validation…").
- **I11** — If NO cut survives keyframe snapping (all collapsed onto the bounds) → `SplitException`
  ("No valid cut points remain after keyframe snapping…").
- **I12** — A probed `duration <= 0` → `SplitException` ("Cannot split: probed duration is … must be positive").
- **I13** — `InteriorSnappedCuts` are the snapped interior boundaries, ascending, with
  `Count == Segments.Count - 1`; `ToSegmentTimes` renders them as an invariant, comma-separated seconds
  list (no thousands separators) for `-segment_times`.

### Command building — `SplitArgsBuilder`
- **I14** — `SegmentMuxer` builds `-y -i <in> -map 0 -c copy -f segment -segment_times <cuts>
  -reset_timestamps 1 <pattern>`: it contains a bare `copy` token, `-map 0`, `-f segment`, `-segment_times`,
  and none of `ForbiddenEncoderTokens`.
- **I15** — `SegmentMuxer` called with zero interior cuts → `SplitException`
  ("Segment muxer needs at least one interior cut time").
- **I16** — `PerSegment` places `-ss` BEFORE `-i` (an input-side seek), so the input timeline resets to
  zero at the seek point.
- **I17** — `PerSegment` with a non-null `end` emits `-to == (end − start)` — a DURATION relative to the
  `-ss` seek, clamped to `>= 0` — NOT the absolute source end (emitting the absolute end would over-run
  by `start`).
- **I18** — `PerSegment` with `end == null` OMITS `-to` entirely (the part runs to end of file).
- **I19** — `PerSegment` emits `-map 0 -c copy -avoid_negative_ts make_zero`, a bare `copy` token, and no
  encoder tokens.
- **I20** — `SatisfiesCopyInvariant(tokens)` is true iff a bare `copy` token is present AND none of
  `ForbiddenEncoderTokens` appears (case-insensitive); it returns false on encoder contamination (e.g.
  `-c:v libx264`) or a missing `copy`.
- **I21** — The copy invariant holds identically for non-mp4 containers (e.g. `.ts` / mpegts, unicode
  paths): no container-specific re-encode ever leaks in.
- **I22** — Before launching ANY ffmpeg command the engine asserts `SatisfiesCopyInvariant`
  (`AssertCopyInvariant`) and throws `SplitException` ("would re-encode. Refusing to run") if it fails —
  a runtime guard on both extraction paths, not just a build-time property.

### Extraction routing & segment selection — `SplitEngine`
- **I23** — `SelectedSegmentIndices == null` → the FULL contiguous set → the single-pass segment-muxer
  path (one ffmpeg run producing all parts).
- **I24** — A strict SUBSET selection → the per-segment `-ss/-to -c copy` path: exactly one ffmpeg run per
  selected part, and ONLY the chosen parts are written (unselected output files are never created).
- **I25** — A selected part keeps its ORIGINAL 1-based index in its output filename (a selected middle
  part is still `…_part02`); indices are de-duped and clamped to the planned range.
- **I26** — An EMPTY (non-null) `SelectedSegmentIndices` → `SplitException` ("No segments selected…").
- **I27** — A non-null selection none of whose indices fall within the planned range → `SplitException`
  ("None of the selected segment indices fall within the planned parts…").
- **I28** — The plan's FINAL selected part omits `-to` (extracts to EOF via `IsFinalPart`); interior
  selected parts pass an explicit `-to == SnappedEnd`.

### Safety, validation & failure mapping — `SplitEngine`
- **I29** — With `Overwrite == false`, an existing SELECTED output file → `SplitException`
  ("already exists. Pass Overwrite=true…") before any ffmpeg runs; only the selected outputs are
  collision-checked.
- **I30** — Extraction writes into a temp dir (`.vsj-split-<guid>`) and each part is moved into place only
  AFTER ffmpeg succeeds; a cancel mid-run leaves NO final output file (the temp dir is deleted in `finally`).
- **I31** — `EnsureEnoughFreeSpace`: when the output drive's free space is knowably below
  `inputSize + 16 MB`, throw `SplitException` (DiskFull, "Not enough space…") before ffmpeg; any
  unmeasurable drive (unknown / UNC / exception) silently skips the check (never a false-positive block).
- **I32** — `ValidateRequestShape` rejects with `SplitException`, before probing, each of: empty
  `InputPath`, a missing input file, empty `OutputDir`, a null/empty `CutPoints`, and an unwritable
  `OutputDir` (write-probe fails).
- **I33** — A failed probe (`ProbeAsync` not `ProbeSucceeded`) → `SplitException`
  ("Cannot split '<input>': <reason>").
- **I34** — A non-zero ffmpeg exit → a mapped, friendly `SplitException` carrying `LogFilePath` +
  `FullStdErr`, with the full stderr (+ command + exit code) persisted to a per-run log; the mapped cause
  (e.g. disk-full / exit -28) is the headline.
- **I35** — If ffmpeg produced fewer parts than planned (an expected temp file is missing at move time) →
  `SplitException` ("was not produced by ffmpeg (got fewer segments than planned)").

### Output contract — `SplitResult` / `SplitSegment`
- **I36** — All input streams are preserved (`-map 0`): output segments retain their audio (and other)
  streams, not just video.
- **I37** — Each written `SplitSegment` records the requested `Start`/`End`, the snapped `ActualStart`,
  and the signed `Delta`; the produced segment durations sum to the whole file duration. `SplitResult`
  also surfaces the planner's `Warnings`.
- **I38** — `ApplyNamingPattern` renders `{name}`, `{ext}`, `{index}`, and zero-padded `{index:00}` /
  `{index:000}` (pad width = zero-count); a blank/whitespace pattern falls back to
  `DefaultNamingPattern` (`{name}_part{index:00}{ext}`).

### Per-part export progress — `PartProgress` / `PartMapping` / `SplitEngine` (T-069)

Alongside the overall `IProgress<double>` bar and the staged `IProgress<OperationStatus>` channel, the
engine reports *which part is being written and how far into it* through an optional
`IProgress<PartProgress>`. `PartProgress(PartIndex, PartCount, PartFraction)` is a readonly record
struct. Both extraction paths feed it, from different sources: the per-segment subset path reports it
naturally (one ffmpeg run == one part), while the single-pass segment muxer **derives** it from the one
monotonic overall fraction it already parses, via the pure `PartMapping.PartAt` — so the fast path stays
one ffmpeg pass.

- **I49** — `partProgress` is optional (parameter default `null`) and purely additive: omitting it leaves
  the overall `progress` and staged `status` channels' behaviour unchanged, and the split itself
  identical (`ISplitEngine.SplitAsync` / `SplitEngine.SplitAsync` signature; every non-T-069 engine test
  calls the method without it).
- **I50** — Every emitted sample identifies its part by its **ORIGINAL 1-based index in the full plan**
  and reports `PartCount` as the **full plan's** part count (`plan.Segments.Count`) — on BOTH paths. A
  subset export is never renumbered to `1..M` (`SplitEngine.SplitAsync` passes `plan.Segments.Count` as
  `planPartCount`; `ExtractSelectedPerSegment` uses `sel.OneBasedIndex`).
- **I51** — `PartMapping.PartAt(time, boundaries, duration)` maps an absolute time onto the **half-open**
  parts `[0,c1), [c1,c2), …, [cN,duration)` — `boundaries.Count + 1` of them — returning the containing
  part's 1-based index and the fraction **local to that part's own span**, not a whole-file fraction
  (`PartAt` walk + `Fraction`).
- **I52** — A time exactly ON a boundary belongs to the **LATER** part, at fraction `0` (the boundary is
  the next part's start; `time < end` comparison in the walk).
- **I53** — With zero boundaries there is exactly one part `[0,duration)` and every in-range time maps to
  index `1` (`partCount = boundaries.Count + 1`).
- **I54** — A time at or below zero clamps to `(1, 0.0)` — the first part, not yet started
  (`time <= TimeSpan.Zero` guard).
- **I55** — A time at or beyond `duration` (when `duration > 0`) clamps to `(partCount, 1.0)` — the last
  part, fully done (`time >= duration` guard).
- **I56** — `PartFraction` is always finite and within `[0,1]`: `Fraction` clamps both ends and returns
  `1.0` for a non-positive span rather than dividing by zero, so coincident boundaries (a zero-length
  part, which `SplitPlanner` I4/I6 already prevent) cannot produce `NaN`/`Infinity` (`PartMapping.Fraction`).
  *The non-positive-span branch itself is only reachable through a `duration <= 0`, which `SplitPlanner`
  I12 rejects upstream — not asserted.*
- **I57** — `PartAt` rejects a null `boundaries` list with `ArgumentNullException`
  (`ArgumentNullException.ThrowIfNull`). *Not asserted.*
- **I58** — Turning per-part progress on does **not** cost an extra pass: the muxer path stays a **single**
  ffmpeg run and derives each sample from the overall fraction it already receives —
  `time = fraction × duration`, then `PartMapping.PartAt` (`ExtractAllViaSegmentMuxer`, the
  `SyncProgress<double>` wrapper). Nothing is re-extracted or re-probed.
- **I59** — That wrapper forwards each fraction **verbatim** to the overall `progress` channel before
  deriving the part sample, so the overall bar reads the same with per-part reporting on or off
  (`progress?.Report(fraction)` precedes the derivation). *Not asserted — the T-069 engine test passes
  `progress: null`.*
- **I60** — After the muxer run succeeds the engine emits a final `PartProgress(partCount, partCount, 1.0)`,
  so the last part is marked done even when ffmpeg's last `time=` sample stopped short of the final
  boundary (`ExtractAllViaSegmentMuxer`, the post-`ThrowIfFailed` report).
- **I61** — On the per-segment subset path there is exactly **one ffmpeg run per SELECTED part**, each
  run's local fraction is reported as that part's `PartFraction`, and an **unselected part never appears
  in the sample stream at all** (`ExtractSelectedPerSegment` loop; unselected parts are not in `selected`).
- **I62** — Each selected part is followed by an explicit `PartProgress(index, planPartCount, 1.0)` once its
  run succeeds, so a part reads as done even if its runner never reported a fraction reaching 1
  (`ExtractSelectedPerSegment`, the post-`ThrowIfFailed` report).
- **I63** — When BOTH `progress` and `partProgress` are null the engine hands the runner a **null**
  reporter rather than allocating a wrapper, so a progressless run pays no per-line derivation cost (the
  `progress is null && partProgress is null` ternary on both paths). *Not asserted.*
- **I64** — Core reports per-part samples **synchronously and in order**, inline on the thread that parsed
  the ffmpeg `time=` line (`SyncProgress<T>`, deliberately not `System.Progress<T>` — Core has no
  synchronization context to marshal to; UI-thread affinity is the App's job, SPEC-010 I75). *Not asserted
  directly, but the muxer test's `samples[^1]` assertion depends on the ordering.*

## Links
- Design: — (no D-NNN for the v1.0 core; goal G-001) · related D-004 (bulk cut reuses this engine via `KeptSegmentSelector`)
- Goals: G-001 (ship v1.0 stream-copy splitter) · G-005 (fast 4K split — copy is resolution-independent)
- Related specs: SPEC-002 (bulk-trim-engine) — reuses this engine's per-segment path; SPEC-003 (join-concat) — sibling copy operation
- Key code: `src/Core/Split/SplitEngine.cs` · `SplitArgsBuilder.cs` · `SplitPlan.cs` (`SplitPlanner`) ·
  `SplitRequest.cs` · `SplitResult.cs` · `SplitSegment.cs` · `SplitException.cs` · per-part progress:
  `PartProgress.cs` (`PartProgress`, `PartMapping`) · frame-exact:
  `SmartCutEngine.cs` · `SmartCutPlanner.cs` · `SmartCutArgsBuilder.cs`
- Tests: per-part progress (I49–I64) — `tests/Core.Tests/PartMappingTests.cs` (the pure mapping,
  I51–I56) · `tests/Core.Tests/SplitEnginePartProgressTests.cs` (both paths' emission, I50 · I58 · I60 ·
  I61 · I62)

## Frame-exact ("smart") cutting — SmartCutEngine (T-124, epic G-042)

`SplitEngine` remains **copy-only**: its `-c copy` invariant and `SatisfiesCopyInvariant` assertion are
unchanged, and nothing below alters them. Frame-exact cutting is a SEPARATE engine
(`SmartCutEngine`) used when `CutPrecision.Exact` is selected.

- **I40** — `SmartCutPlanner.Plan(start, end, keyframes)` returns `PureCopy` when `start` is on a
  keyframe (within `OnKeyframeTolerance`, 10ms), so an already-exact request never re-encodes.
- **I41** — Otherwise it returns `HeadReencode` with `HeadEnd` = the first keyframe strictly after
  `start`: the head `[start, HeadEnd)` is re-encoded, the tail `[HeadEnd, end)` is stream-copied.
- **I42** — When no keyframe lies strictly between `start` and `end`, it returns `FullReencode` — there
  is no copyable tail, and the re-encoded range is by construction shorter than one GOP.
- **I43** — `Start` is honoured EXACTLY; the planner never moves the user's requested time.
- **I44** — The head command uses an OUTPUT seek (`-ss` AFTER `-i`) so the fragment begins on the exact
  requested frame; the tail command is `SplitArgsBuilder.PerSegment`, i.e. byte-identical to what the
  lossless path would have produced.
- **I45** — The head is encoded with parameters read from the source's own probe (video codec→encoder,
  pixel format, resolution; audio codec→encoder, sample rate, channels) so the concat demuxer accepts
  the join.
- **I46** — A codec with no known encoder mapping yields a FALLBACK result (`FellBack = true` with a
  stated reason) — never a guessed encoder and never a silently corrupt concat. The caller then runs
  the ordinary lossless cut.
- **I47** — All intermediates live in a `.vsj-smartcut-<guid>` temp dir swept in a `finally`; the final
  file is moved into place only after it exists (same cancel-safety contract as `SplitEngine`). That move is
  `MoveIntoPlace`, a **delete-then-move** onto the destination it was given — so a caller must never hand this
  engine a destination that is its own source; one that wants to write over the original hands it a sibling
  temp and performs the swap through `Core/Io/OriginalReplacer` instead (SPEC-002 I53/I56–I57).
- **I48** — Exactly three ffmpeg invocations for a `HeadReencode` (head encode, tail copy, concat) and
  one for a `FullReencode` — never one per GOP.
