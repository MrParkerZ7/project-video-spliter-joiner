---
id: SPEC-010
slug: split-screen
area: app
title: Split screen (markers, segments, output)
status: current
sources:
  - src/App/ViewModels/SplitViewModel.cs
  - src/App/ViewModels/CutMarkerViewModel.cs
  - src/App/ViewModels/SplitSegmentViewModel.cs
serves-goal: [G-020, G-022, G-026]
updated: 2026-08-22
---

## What
The Split screen is the app's primary workflow: load a media file, place cut markers on its
timeline, choose which resulting parts to keep, and run a lossless (stream-copy) split that writes
the selected parts to disk. `SplitViewModel` is the WPF-free, constructor-injected orchestrator for
the whole screen. On load it probes the file (duration/streams appear at once) and indexes the video
keyframes in the background; each cut marker (`CutMarkerViewModel`) snaps to the nearest keyframe and
shows its signed offset; the markers project into an ordered, individually-selectable list of
contiguous parts (`SplitSegmentViewModel`); and the run is funnelled through a composed
`OperationViewModel` so progress, cancel, and friendly-error handling are shared with the rest of the
app. Every engine call goes through `ISplitEngine`, so the screen is fully unit-testable with fakes.

## Why
Splitting is the feature the app exists for. The screen has to stay responsive on large files (so
probe and keyframe indexing are decoupled and the scan runs in the background), let the user cut at
exact playhead positions without fighting keyframe boundaries (so markers snap and show the delta),
keep the cut list readable regardless of the order cuts were added (G-026 — ordered by time), make
the "kept parts" explicit rather than an all-or-nothing export (selectable segments), default the
output location somewhere sensible and predictable (G-020 — the loaded file's own folder, re-anchored
on every load), and add a cut reliably from any gesture — manual entry, playhead capture, or timeline
click — through one code path so snap and dedupe behave identically (G-022 — add-cut parity).

## Scope
**In:** `SplitViewModel` load/probe + background keyframe indexing and its stale-scan guard; cut
marker creation, keyframe snapping, optimistic (pending) markers and their async resolve,
time-ordering, and dedupe; the selectable segment projection and its selection state; `OutputDir`
defaulting/re-anchoring and folder memory; `CanRunSplit`/`RunSplitAsync` request-building and the
`OperationViewModel` hand-off; `SetCutAtPlayhead`/`SeekToMarker`/`NewMarkerPosition` playhead wiring;
`Clear` reset and safe reload. `CutMarkerViewModel` snap/delta/display and `SplitSegmentViewModel`
projection/selection are covered as the marker/segment contracts this screen depends on.

**Out:** The actual ffmpeg split (`ISplitEngine` implementation, args, segment-muxer vs per-segment
copy) — a Core concern. `OperationViewModel` progress/ETA/cancel/error-mapping mechanics — its own
surface. The audio-waveform band (T-084 / `WaveformViewModel`) — background-wired here but specified
separately. The Join screen. Keyframe-snap math and `MediaProbe` internals (Core).

## Current behavior & invariants

### Load & keyframe indexing
- **I1** — `LoadAsync(null-or-whitespace)` is a no-op: it returns immediately and mutates nothing
  (`SplitViewModel.LoadAsync`, early-return guard).
- **I2** — A `ProbeResult.ProbeFailed` surfaces a friendly `UserFacingError` (category
  `CorruptInput`) through `Operation`, leaves the VM unloaded (`InputPath` stays null), never throws,
  and mirrors a short line into `StatusText` (`LoadAsync`, `failureSelector` + the post-run failure
  branch).
- **I3** — On a successful probe the VM commits `Info`/`InputPath` (and the derived `FileName`,
  `MetaLine`, `Badge`) and opens the file in the preview player, and clears prior `Markers`,
  `LastResult`, and `KeyframeWarning` — all before any keyframe scan (`LoadAsync`, the success block +
  `Player.Open`).
- **I4** — Keyframe indexing runs in the background (non-blocking load): after a successful probe and
  until the scan finishes, `IsIndexingKeyframes` is true, `Keyframes` is empty, and `KeyframesReady`
  is false (`StartKeyframeIndex`; `KeyframesReady => HasFile && !IsIndexingKeyframes`).
- **I5** — Each load cancels the previous file's in-flight keyframe index via its own
  `CancellationTokenSource`; a superseded (stale) scan's late completion is dropped and never
  overwrites the current file's `Keyframes` (`StartKeyframeIndex` CTS swap + the `ReferenceEquals`
  guard in the completion continuation).
- **I6** — When the current scan completes, `Keyframes` is populated and `IsIndexingKeyframes` flips
  false (both the synchronous fast path and the posted continuation in `StartKeyframeIndex`).
- **I7** — `KeyframeWarning` is set iff the mean GOP exceeds the 4s coarse threshold
  (`CoarseGopThreshold`), and is null otherwise (`UpdateKeyframeWarning`).
- **I8** — (G-020) On every successful load, `OutputDir` is re-anchored to the loaded file's folder,
  unconditionally discarding any prior manual or remembered value; a null/empty resolved folder
  leaves the previous `OutputDir` untouched rather than blanking it (`LoadAsync`, the T-061
  re-anchor block).
- **I9** — A successful load records `Settings.LastInputDir` = the input file's folder (`LoadAsync`,
  best-effort persistence block).

### Markers: snap, ordering & dedupe
- **I10** — (G-026 / T-071) `Markers` stays ordered ascending by `Snapped` regardless of add order —
  new markers are inserted at their time-sorted index (`InsertMarkerSorted` via `MarkerSortKey`).
- **I11** — `AddCutAt` is a no-op when no file is loaded (`AddCutAt`, `HasFile` guard).
- **I12** — With keyframes already available, `AddCutAt` snaps synchronously to the nearest keyframe
  and dedupes on the snapped time: two requests that snap to the same keyframe produce one marker
  (`AddSnappedMarker`, the `m.Snapped == marker.Snapped` guard).
- **I13** — (T-041) A cut placed while indexing (`IsIndexingKeyframes` and `Keyframes` empty) is added
  instantly as pending — `IsSnapPending` true with a provisional identity snap (`Snapped ==
  Requested`), deduped on the requested time — and re-snaps in place once the in-flight scan finishes
  (`AddPendingMarker` + `ResolvePendingMarkerAsync`, resolving against the same awaited scan via
  `EnsureKeyframesAsync`).
- **I14** — On a pending marker's resolve, if its final snapped time collides with an existing
  marker's snapped time, the just-resolved duplicate is removed (`ResolvePendingMarkerAsync`, the
  final-snap dedupe branch).
- **I15** — A pending resolve is discarded if the file changed while pending (the index CTS was
  swapped or the file was unloaded) or the marker was removed by the user — it never touches another
  file's markers (`ResolvePendingMarkerAsync`, the `HasFile`/`ReferenceEquals`/`Contains` guards).
- **I16** — When the keyframe index fails, is cancelled, or returns an empty list, snapping falls
  back to an identity snap (`Snapped == Requested`, `Delta` zero) without crashing
  (`EnsureKeyframesAsync` catch + `CutMarkerViewModel.Resnap` no-keyframes branch).
- **I17** — Setting `CutMarkerViewModel.Requested` re-snaps the marker against the current keyframes
  (`Requested` setter → `Resnap`).
- **I18** — (T-071) When a pending marker's resolved snap changes its sort key, it is re-positioned
  into its correct time-sorted slot (`RepositionMarkerSorted`).
- **I19** — `CutMarkerViewModel` exposes `Snapped` and `Delta == Snapped − Requested`, and `Display`
  renders `"<requested> → <snapped> (<±delta>s)"`, or `"<requested> → snapping…"` while
  `IsSnapPending` (`CutMarkerViewModel.Display` / `Resnap` / `FormatDelta`).
- **I20** — `RemoveMarker` removes the given marker from the collection (`RemoveMarker`).

### Segment projection & selection
- **I21** — `Segments` projects the ordered contiguous ranges `[0..s1],[s1..s2],…,[sN..end]` from the
  distinct snapped marker times strictly inside `(0, duration)` plus the file duration; each part
  carries a 1-based `Index` and its `Start`/`End`/`Duration` (`RebuildSegments` + `AddSegmentRow`).
- **I22** — With no file loaded, or a duration ≤ 0, `Segments` is empty (`RebuildSegments`, the
  `!HasFile || duration <= Zero` early return).
- **I23** — Each part's `IsSelected` defaults to true, and prior selection is preserved by 1-based
  index across a rebuild so re-snapping a marker never silently re-checks parts the user unchecked
  (`RebuildSegments` `priorSelection` map + `AddSegmentRow`).
- **I24** — `SegmentCount`, `SelectedSegmentCount`, and `RunLabel` track the projection + selection:
  `RunLabel` is "Split N parts" when all are selected, "Split M of N parts" for a subset, and "Split"
  when there are no parts (`RunLabel`, `SelectedSegmentCount`, `SegmentCount`).
- **I25** — `SelectAllSegmentsCommand`/`SelectNoSegmentsCommand` set every part's `IsSelected`
  true/false and are enabled only when `Segments.Count > 0` (`SetAllSegmentsSelected` + the command
  `canExecute` guards).

### Run split
- **I26** — `CanRunSplit` is true iff `InputPath` is set, there is ≥1 marker, `OutputDir` is set, and
  ≥1 segment is selected (`CanRunSplit`).
- **I27** — `RunSplitAsync` is a no-op unless `CanRunSplit` (its guard at method entry).
- **I28** — `RunSplitAsync` builds `SplitRequest.CutPoints` from the markers' `Requested` times sorted
  ascending (`RunSplitAsync`, `Markers.Select(m => m.Requested).OrderBy(...)`).
- **I29** — When all parts are selected (or the projection is empty), `SelectedSegmentIndices` is null
  (keeping the fast segment-muxer path); a strict subset passes the selected parts' ORIGINAL 1-based
  indices (`RunSplitAsync`, the `allSelected` branch).
- **I30** — A blank `NamingPattern` is replaced with `SplitRequest.DefaultNamingPattern`; `Overwrite`
  is passed through to the request verbatim (`RunSplitAsync`, request construction).
- **I31** — The run is executed through `OperationViewModel.RunWithResultAsync`: on success
  `LastResult` is set and `Operation.ResultSummary` gets a human count; a thrown `SplitException`
  leaves `Operation.State == Failed` with `LastResult` null (`RunSplitAsync`, the post-run success
  branch).
- **I32** — A successful split records `Settings.LastOutputDir` = the `OutputDir` just written to
  (`RunSplitAsync`, success block).

### Playhead capture, seek & typed position
- **I33** — `CanSetCutAtPlayhead` is true iff a file is loaded and the preview player is ready
  (`CanSetCutAtPlayhead => HasFile && Player.IsReady`).
- **I34** — `SetCutAtPlayhead` adds a cut at `Player.Position` via `AddCutAt`, and is a no-op when its
  guard is false (`SetCutAtPlayhead`).
- **I35** — (G-022) Every add gesture — manual `AddMarker`, playhead capture, and timeline-click
  `AddCutAt` — routes through the single `AddCutAt` entry point so snap + dedupe are identical:
  distinct playhead captures add distinct markers, a repeated same-position capture dedupes to one
  (`AddMarker`/`SetCutAtPlayhead`/`AddCutAtCommand` all → `AddCutAt`).
- **I36** — (T-064) `NewMarkerPosition` follows the live playhead until the user types a value
  differing from the last VM-seeded one (which pins it, stopping the follow), and re-arms following
  after a load or `Clear` (`NewMarkerPosition` setter + `SeedNewMarkerPositionFromPlayhead` +
  `_positionFollowsPlayhead` re-arm on load/clear).
- **I37** — `SeekToMarker` scrubs the preview player to the marker's `Snapped` time (`SeekToMarker` →
  `Player.Scrub(marker.Snapped)`).

### Clear & reload
- **I38** — `CanClear` is true iff a file is loaded and no split is running
  (`CanClear => HasFile && !Operation.IsRunning`).
- **I39** — `Clear` resets the screen to empty (drops file, markers, keyframes, info, result,
  warning, status; unloads the player; resets `Operation`) and cancels the in-flight keyframe (and
  waveform) scans so a late completion can never repopulate state; it is a no-op unless `CanClear`
  (`Clear`).
- **I40** — Loading a new file replaces a previously loaded one safely — the player's `Unload`
  precedes the reopen, and repeated split→clear→load cycles stay stable — whether or not `Clear` was
  called first (`LoadAsync` re-open path; `Player.Open`/`Unload` ordering).

### Dropped files are accounted for (`DropRefusal`, `SplitViewModel.AddDroppedFilesAsync` — T-154)
- **I41** — a dropped file that is **not loaded is explained**, never silently discarded. `DropSummary`
  states it in one line, and the drop handler passes the **raw** paths to the view-model rather than
  filtering first — filtering in the view and telling the VM only about the survivors is precisely why
  this screen could not report a refusal even in principle.
- **I42** — Split's own refusal, which Bulk Cut has no phrase for: it **opens one file at a time**, so a
  drop of several videos loads the first and names the rest ("2 other videos were skipped — Split opens
  one file at a time"). The count alone reads as a malfunction; the reason makes it a rule.
- **I43** — `DropSummary` is **null when nothing was refused**. A message on every drop is noise, and
  noise is what teaches people to ignore the one that matters.
- **I44** — a dropped **folder is called a folder**, not "not a video file". Explorer delivers a folder as
  an ordinary FileDrop path with no extension; describing the most natural gesture for a video tool
  with a false statement is a new defect, not a fix (`DropRefusal.Classify`).
- **I45** — `Clear` nulls `DropSummary`. The note describes a drop whose screen no longer exists; leaving
  it up is the stale-note bug that shipped on Bulk Cut and was fixed there in the same change.
- **I46** — the note is assigned **before the load is awaited**, so it is on screen immediately and the
  drop handler can carry the same sentence into `dragdrop.log` for that drop. Asserting this needs a
  probe that genuinely suspends — against a `Task.FromResult` fake the whole method completes
  synchronously and the assertion passes either way (`BlockingProbe`).
- **I47** — *(uncovered — set in WPF code-behind; needs a windowed/STA harness, see `_GAPS.md`)* the
  `dragdrop.log` **`accepted` flag reports the real decision**, not a hard-coded `true`. The
  log exists to tell "we never saw the drag" apart from "we saw it and refused it"; a drop recorded as
  accepted when the filter took nothing defeats the one artifact the reporter is asked to paste into a
  bug report (`DropDiagnostics.Record`, `note:` carries the refusal sentence).
- **I48** — *(uncovered by design — describes a region no drop event reaches, so there is nothing to
  assert; the decision is ADR-0023)* **boundary, stated rather than fixed:** a drag containing *no* recognised video never reaches
  any of this. `OnDragOver` answers `VideoFileFilter.HasAnyVideo` with `DragDropEffects.None`, so Windows
  shows a no-entry cursor and **no drop event is delivered** — the cursor is that case's feedback. Every
  invariant above describes a drop that WAS accepted and still could not take everything in it.

## Links
- Design: — (goal-driven; see G-020 / G-022 / G-026 task threads under `docs/todo/`)
- Goals: G-020 (output-dir defaults to file folder + resets per load) · G-022 (add-cut parity across
  gestures) · G-026 (markers ordered by time). Related tasks: T-030 (non-blocking load), T-041
  (optimistic pending markers), T-047 (Clear), T-049 (selectable segments), T-061 (output-dir
  re-anchor), T-064 (playhead-follow field), T-071 (time-ordered markers), T-080 (reload-after-clear).
- Related specs: keyframe-snap / `MediaProbe` (Core) · `OperationViewModel` progress/cancel/error ·
  waveform band (T-084) — all adjacent, not covered here.
- Key code: `src/App/ViewModels/SplitViewModel.cs` · `src/App/ViewModels/CutMarkerViewModel.cs` ·
  `src/App/ViewModels/SplitSegmentViewModel.cs` · `src/App/ViewModels/OperationViewModel.cs` ·
  `src/App/ViewModels/PlayerViewModel.cs` · `src/Core/Split/SplitRequest.cs` + `ISplitEngine`.
