---
id: SPEC-011
slug: bulk-cut-screen
area: app
title: Bulk Cut screen (batch trim UI)
status: current
sources:
  - src/App/ViewModels/BulkCutViewModel.cs
  - src/App/ViewModels/BulkItemViewModel.cs
  - src/App/ViewModels/CutProfileApplier.cs
  - src/App/ViewModels/RelayCommand.cs
  - src/App/ViewModels/MainViewModel.cs
serves-goal: [G-036, G-037, G-038, G-039, G-040]
updated: 2026-08-24
---

## What
The Bulk Cut tab lets a user trim many videos in one pass. `BulkCutViewModel` holds a deduplicated,
add-ordered list of `BulkItemViewModel` rows; each row keeps exactly the middle segment
`[intro-end → outro-start | EOF]` via two `CutMarkerViewModel` handles (intro-end **required**, outro-start
**optional, measured from end**), snapped to that file's own keyframes by a throttled background scan. The tab
provides: an **apply-to-all** gesture that copies one row's cut to the other checked rows (intro absolute,
outro from end, each re-snapped + re-validated); reusable **cut profiles** (save / apply-to-selected /
apply-to-all / delete); a **single shared mini-preview player** where selecting a row opens that file and two
**set-at-playhead** gestures place the cut by watching (G-037); **per-row cut-point frame thumbnails** that grab a
small frame at each row's snapped intro-end (and outro-start) so the cut can be verified by eye (G-038); and a
batch **Run** that *delegates* the whole trim job to `IBulkTrimEngine.RunAsync` (the VM owns no batch loop),
fanning weighted-monotonic overall progress, per-row progress, and the returned ledger back onto the rows.

## Why
Trimming a season of episodes one-file-at-a-time on the Split screen is slow and error-prone. Bulk Cut (D-004 /
G-036) turns "trim the intro and outro off every episode" into one screen: add all files, set the cut once, apply
it across the batch (episodes of uneven length still align because the outro is measured **from the end**), and
run. Profiles (G-037) make the same cut reusable across sessions/series, and the shared preview + set-at-playhead
let the user place the cut by eye instead of typing timecodes. Keeping the batch loop, collision resolution, and
disk pre-flight inside the T-095 engine (not the VM) keeps this screen a thin, testable, WPF-free view-model.

## Scope
**In:** the Bulk Cut tab view-models — `BulkCutViewModel` (list management, dedup, throttled scan gate,
apply-to-all, cut-profile commands, shared preview player + selection, set-at-playhead, the dedicated cut-point
thumbnail gate, `CanRunBatch`, `RunBatchAsync` delegation, progress fan-out, ledger routing, batch-state mapping)
and `BulkItemViewModel` (per-row handles, keyframe scan, computed validity/`RowState`/`KeptDuration`/`Warning`,
`IsEnabled` auto-disable, the per-row cut-point frame thumbnails `IntroThumbnailPath`/`OutroThumbnailPath`, request
builders, batch fan-out hooks), plus `CutProfileApplier` (pure apply/build for profiles as used by the tab).

**Out:** the batch execution engine itself (`IBulkTrimEngine` / `BulkTrimEngine` — collision policy resolution,
disk pre-flight, the per-item ffmpeg trim, `BatchResult`/`BatchOutcome` construction); the kept-segment request
math (`KeptSegmentSelector`, T-094); keyframe snapping internals (`CutMarkerViewModel`); the reopen sequencing
inside `FfmeMediaPlayer`/`MediaReopenGuard` (T-080); cut-profile persistence (`IAppSettings.CutProfiles`,
`CutProfile`, T-102) and the **profile-thumbnail** model/store/persistence + the T-107 auto-default/upload/clear
glue (`ProfileThumbnailStore`, `SaveProfileWithAutoThumbnailAsync`/`UploadThumbnail`/`ClearThumbnail`, T-106/107 —
SPEC-007); the shared `IThumbnailService`/`FfmpegThumbnailService` frame source the cut-point grabs reuse
(SPEC-005); the WPF views and drag-drop routing. Those are adjacent specs.

## Current behavior & invariants

### Add / dedup / probe / scan
- **I1** — `AddFilesAsync` adds one row per path deduplicated by normalized `Path.GetFullPath` (case-insensitive);
  a source already present (within the same call or across calls) never yields a second row (`AddFilesAsync`,
  `NormalizePath`).
- **I2** — a row that cannot be probed is marked `LoadFailed` and excluded from the batch: `RowState ==
  LoadFailed` and `IsEnabled == false` (`PopulateAsync` catch → `MarkLoadFailed`; `BulkItemViewModel.MarkLoadFailed`,
  `IsAutoDisabled`).
- **I3** — background keyframe scans are bounded to **at most 3 concurrent** `GetKeyframesAsync` calls through the
  single `SemaphoreSlim(3,3)` owned by the tab VM and shared into every row (`_scanGate`,
  `BulkItemViewModel.ScanBodyAsync` `_scanGate.WaitAsync/Release`).
- **I4** — the first `AddFilesAsync` (nothing selected yet) auto-selects `Items[0]` and opens it in the shared
  player (`AddFilesAsync` `if (SelectedItem is null && Items.Count > 0)`).
- **I5** — `AddFilesAsync` records the last-added file's directory into `IAppSettings.LastInputDir` (`AddFilesAsync`
  → `_settings.LastInputDir = lastAddedDir`).

### Per-row keyframes, validity, and state (`BulkItemViewModel`)
- **I6** — `KeyframesReady` is true iff `Duration` is known **and** the background scan has finished
  (`IsIndexingKeyframes == false`); the completing scan resolves both handle snaps to the nearest keyframe
  (`KeyframesReady`, `ScanBodyAsync` → `IntroEnd.ResolveSnap()` / `OutroStart?.ResolveSnap()`).
- **I7** — `StartKeyframeScanAsync` flips `IsIndexingKeyframes` true then, on completion, commits `Keyframes`,
  resolves the intro (and outro, if any) snaps, and raises `KeyframesReady`; a requested time snaps to the nearest
  keyframe (e.g. 12s → 10s on a 5s grid) (`StartKeyframeScanAsync`, `ScanBodyAsync`).
- **I8** — `IsValidCut` is true iff `KeyframesReady`, `IntroEndSnapped ≥ 0`, `(OutroStartSnapped ?? Duration) ≤
  Duration`, **and** `IntroEndSnapped < (OutroStartSnapped ?? Duration) − MinKeptSpan` (`IsValidCut`).
- **I9** — `MinKeptSpan` is `max(1 second, average GOP)` of the row's keyframes (`MinKeptSpan`,
  `_probe.AverageGop`).
- **I10** — `IsNoOpTrim` is true iff the net result keeps the whole file: `IntroEndSnapped ≤ 500ms` **and**
  (no outro **or** `Duration − OutroStartSnapped ≤ 500ms`) (`IsNoOpTrim`, `BoundaryEpsilon`).
- **I11** — `RowState` returns the active batch-phase overlay when set, else the computed base state in priority
  order `LoadFailed → Loading (not KeyframesReady) → NoOpTrim → Ready (IsValidCut) → Invalid`
  (`RowState`, `ComputeBaseRowState`).
- **I12** — `KeptDuration` is `(OutroStartSnapped ?? Duration) − IntroEndSnapped`, and is null until keyframes are
  ready (`KeptDuration`).
- **I13** — `IsEnabled` is auto-forced false once a row is known-ineligible (`LoadFailed`, or `KeyframesReady` with
  `NoOpTrim`/`!IsValidCut`); a still-`Loading` row is **not** auto-disabled (`IsEnabled`, `IsAutoDisabled`).
- **I14** — `StartKeyframeScanAsync` supersedes any in-flight scan (`Interlocked.Exchange` of the per-row CTS
  cancels + disposes the prior one) and a superseded scan's result is dropped — only the current CTS commits
  `Keyframes` (`StartKeyframeScanAsync`, `ScanBodyAsync` `if (!ReferenceEquals(_scanCts, cts)) return`).
- **I15** — a scan that throws leaves `Keyframes` empty, clears `IsIndexingKeyframes`, and resolves both handles to
  identity snaps (so the row still becomes `KeyframesReady` with fallback snapping) (`ScanBodyAsync` catch block).
- **I16** — `Warning` surfaces the computed notes `coarse keyframes — cuts may move ~Ns` (avg GOP > 4s),
  `nothing trimmed from the tail` (outro ≈ EOF but intro is real), and `very short keep (~Ns)`
  (`0 < KeptDuration < MinKeptSpan`), joined with any ledger warnings (`Warning`).

### Handles
- **I17** — `AddOutro(requested)` creates (or replaces) the optional outro handle — snapped immediately when
  keyframes are already ready — and `ClearOutro()` drops it; both toggle `HasOutro` and recompute the row
  (`AddOutro`, `ClearOutro`, `HasOutro`).

### Request builders (T-094)
- **I18** — `BuildRequest` produces a single-kept-middle `SplitRequest` with the `TrimmedNamingPattern`, the
  resolved selected segment index, this row's input path, and its output dir; it throws
  `InvalidOperationException` if called before the file is probed (`BuildRequest`).
- **I19** — `BuildBulkTrimItem` produces a `BulkTrimItem` carrying the requested intro, the requested outro (or
  null), the deterministic `<name>_trimmed<ext>` output path, and `Tag = this` for ledger correlation
  (`BuildBulkTrimItem`, `ComputeBaseOutputPath`).

### Apply-to-all
- **I20** — `ApplyToAll` copies the source's **requested** cut to every other target: intro-end **absolute**
  (time-from-start) and, when the source has an outro, outro **from end** (`targetDuration − (sourceDuration −
  source.OutroStart.Requested)`) so uneven-length episodes align; each target re-snaps against its own keyframes
  (`ApplyToAll`).
- **I21** — `ApplyToAll` returns null and mutates nothing when the source is null, not `KeyframesReady`, or has no
  `Duration` (`ApplyToAll` guard).
- **I22** — `ApplyToAll` only touches other rows that are `IsCheckedByUser`, `KeyframesReady`, and have a
  `Duration`; the source row itself is skipped (`ApplyToAll` `foreach` filter).
- **I23** — when the source has **no** outro, `ApplyToAll` calls `ClearOutro()` on each applied target so it mirrors
  the source's keep-to-EOF shape (`ApplyToAll` `else target.ClearOutro()`).
- **I24** — `ApplyToAll` records `AppliedCount` and collects every target the copied cut left invalid into
  `ApplyToAllReport.InvalidatedRows` — invalidated rows are **reported, never dropped** from the list (`ApplyToAll`,
  `ApplyToAllReport`).
- **I25** — `ApplyToAllCommand.CanExecute` is true only when `Items.Count > 1` (`ApplyToAllCommand` predicate).
- **I26** — `ApplyReportSummary` is `Applied to N row(s).` with no invalid rows, else `Applied to N row(s) · M now
  invalid (see the red rows).`; null when there is no report (`ApplyReportSummary`).

### Shared preview player + selection (T-100 / G-037)
- **I27** — setting `SelectedItem` opens that row's `Path` in the **one** shared `Player` (never a player per row);
  a null selection unloads it (`SelectedItem` setter → `OpenOrUnloadSelected`).
- **I28** — rapid selection switches never throw and the shared player converges on the last selection's file
  (routed through `PlayerViewModel.Open` / the reopen guard) (`OpenOrUnloadSelected`).
- **I29** — removing the selected row re-points the selection at the neighbour at the same slot (or the new last
  row), or unloads when it was the last row; removing a non-selected row leaves the selection and player untouched
  (`Remove`).
- **I30** — `Clear` drops the selection (unloading the shared player), unloads the player unconditionally, cancels
  every scan, empties `Items`, and resets `Operation` + `BatchState = Idle` + `ApplyToAllReport`/`LastFailedItems`
  (`Clear`).
- **I31** — `RunBatchAsync` calls `Player.Stop()` before trimming so the preview decoder is not competing with the
  batch ffmpeg (`RunBatchAsync` → `Player.Stop()`).
- **I32** — the `Player` is always non-null; the constructor defaults to a `NullMediaPlayer` when no player is
  supplied and nothing is selected before the first add (`ctor` → `new PlayerViewModel(player ??
  NullMediaPlayer.Instance, …)`).

### Set-at-playhead (T-101 / G-037)
- **I33** — `SetIntroAtPlayhead` writes the selected row's `IntroEnd.Requested` from `Player.Position` (which
  re-snaps to that row's keyframes) (`SetIntroAtPlayhead`).
- **I34** — `SetOutroAtPlayhead` adds an outro handle at `Player.Position` when the row has none, else moves the
  existing handle (same instance) by writing `OutroStart.Requested` (`SetOutroAtPlayhead`).
- **I35** — `CanSetCutAtPlayhead` (and thus both playhead commands) is true only with a selected row **and**
  `Player.IsReady`; a forced execute with no selection / an unready player is a guarded no-op
  (`CanSetCutAtPlayhead`, `SetIntroAtPlayhead`/`SetOutroAtPlayhead` guards).
- **I36** — a change in the shared player's readiness (`IsReady`/`Duration`) re-raises the playhead command guards
  (`OnPlayerChanged`, `RaisePlayheadCommandStates`).

### Run batch — delegation to the engine (§4)
- **I37** — `CanRunBatch` is true iff there is ≥1 `IsEnabled && IsValidCut` row, no run is in flight
  (`!Operation.IsRunning`), and **every** enabled row is `KeyframesReady` (`CanRunBatch`).
- **I38** — `RunLabel` is `Run bulk cut (N)` counting the `IsEnabled && IsValidCut` rows (`RunLabel`).
- **I39** — `RunBatchAsync` **delegates**: it calls `IBulkTrimEngine.RunAsync` exactly once with one
  `BulkTrimItem` per enabled+valid row (no-op/invalid rows excluded) and never calls `ISplitEngine.SplitAsync`
  directly — the VM owns no batch loop (`RunBatchAsync`).
- **I40** — the run's `BulkTrimOptions` collision policy is `CollisionPolicy.Overwrite` when `Overwrite` is set,
  else `CollisionPolicy` (`RunBatchAsync` → `new BulkTrimOptions(...)`).
- **I41** — `WeightedOverall(weights, index, fraction)` is a pure `Σ(wᵢ·fᵢ)/Σwᵢ` where rows before `index` count as
  done, the current row contributes its clamped fraction, and later rows are 0; it is monotonic non-decreasing in
  `(index, fraction)`, weighted (finishing a light row of weight 10 of 40 gives 0.25), and reaches exactly 1.0 when
  all rows finish (`WeightedOverall`).
- **I42** — the reported overall bar is monotonic-clamped: `OnBatchProgress` only ever reports a value ≥ the last
  reported one (`OnBatchProgress`, `_progressLock` / `_lastOverall`).
- **I43** — per-row progress fans out by `ItemIndex`: the addressed row is advanced to `Running` and its fraction
  set (`OnBatchProgress` → `row.MarkRunning()` / `row.SetProgress`).
- **I44** — a late `MarkRunning`/`SetProgress` after a row reached a terminal batch state (Done/Failed/Skipped/
  Cancelled) is ignored — it never overrides the terminal `RowState`/`Progress` (`MarkRunning` guard,
  `SetProgress` `IsTerminalBatchState` guard).
- **I45** — the returned ledger is routed back to each row by `Tag`: `ApplyResult` sets the terminal `RowState`,
  folds warnings, adopts the collision-resolved output path, fills `SizeAfter` on Done, and sets `Error`
  (`ApplyLedger`, `BulkItemViewModel.ApplyResult`).
- **I46** — a `NotStarted` ledger entry reverts the row's batch overlay to null so it shows its computed pre-batch
  state and is re-runnable (`ApplyResult` `NotStarted` → `_batchState = null`).
- **I47** — `BatchState` maps the engine outcome: `Completed → Completed`, `CompletedWithFailures →
  CompletedWithFailures`, `Cancelled → Cancelled`, `Blocked → Blocked` (`MapOutcome`).
- **I48** — a `Blocked` outcome makes the aggregate operation **Failed** with a `DiskFull` `UserFacingError`
  ("Not enough space…"), while its rows (all `NotStarted`) revert to their computed states (`RunBatchAsync`
  `failureSelector`, `ApplyResult`).
- **I49** — a `Cancelled` outcome lands the aggregate op in `Cancelled` **after** the ledger set per-row states:
  the in-flight row is `Cancelled`, already-`Done` rows are kept, `NotStarted` rows revert (`RunBatchAsync`
  re-throw on `Cancelled`, `ApplyLedger`).
- **I50** — `CompletedWithFailures` is **not** an op-level failure (the op state stays Completed); `ResultSummary`
  is `Trimmed D, F failed` (`Trimmed D` for a clean run), and `LastFailedItems`/`FailedCount` reflect the failed
  subset (`ApplyLedger`, `failureSelector` returning null for that outcome).

### Cut profiles (T-103, thin glue over T-102)
- **I51** — `RefreshProfiles` projects `IAppSettings.CutProfiles` into the observable `Profiles` on construct and
  after every save/delete (`RefreshProfiles`, `ctor`).
- **I52** — `SaveProfile(name)` builds a profile from the **selected** row via
  `CutProfileApplier.BuildProfileFromRow`, persists it via `IAppSettings.SaveProfile` (upsert by
  case-insensitive name), refreshes `Profiles`, and selects the saved profile (`SaveProfile`).
- **I53** — `SaveProfile` is a no-op with no selected row or a blank/whitespace name (the name is trimmed);
  `CanSaveProfile`/`SaveProfileCommand` are disabled without a selection (`SaveProfile`, `CanSaveProfile`).
- **I54** — `BuildProfileFromRow` captures `IntroFromStart` = the row's requested intro-end and `OutroFromEnd` =
  `Duration − requested outro-start` when the row has an outro, else null (`CutProfileApplier.BuildProfileFromRow`).
- **I55** — `ApplyProfileToSelected` applies `SelectedProfile` to the selected row and surfaces the returned
  report through `ApplyToAllReport`; it is a no-op returning null without both a profile and a selection
  (`ApplyProfileToSelected`, `CanApplyProfileToSelected`).
- **I56** — `ApplyProfileToAll` applies `SelectedProfile` to every `IsCheckedByUser` row (raw checkbox intent, so a
  profile can re-validate a currently-invalid checked row) and reports invalidated rows through `ApplyToAllReport`;
  no-op without a profile (`ApplyProfileToAll`, `CanApplyProfileToAll`).
- **I57** — `CutProfileApplier.ApplyProfile` sets each ready target's intro to `IntroFromStart` clamped to
  `[0, Duration]`; when the profile has an `OutroFromEnd` tail it sets the outro at `Duration − tail` (clamped,
  from end) else clears the outro; it skips rows that are not `KeyframesReady`/have no `Duration` (not counted as
  applied) and collects invalidated targets into the returned report; null `profile`/`targets` throw
  `ArgumentNullException` (`CutProfileApplier.ApplyProfile`).
- **I58** — `DeleteSelectedProfile` removes `SelectedProfile` via `IAppSettings.DeleteProfile`, refreshes the bar,
  and re-points the selection at the first remaining profile (or null when none remain); no-op when unset
  (`DeleteSelectedProfile`).
- **I59** — the profile-command gates reflect state: `HasProfiles` (`Profiles.Count > 0`), `HasSelectedProfile`,
  `CanApplyProfileToSelected` (profile + selection), `CanApplyProfileToAll` (profile + ≥1 checked row)
  (`HasProfiles`, `HasSelectedProfile`, `CanApplyProfileToSelected`, `CanApplyProfileToAll`).
- **I60** — `ApplyToAllReport` is the single shared surface for **both** the per-row apply-to-all gesture and the
  profile-apply commands (same property instance is returned/surfaced) (`ApplyToAllReport`, `ApplyProfileToSelected`
  / `ApplyProfileToAll` / `ApplyToAll` all assign it).

### Per-row cut-point frame thumbnails (T-108, `BulkItemViewModel`)
- **I61** — when a row's keyframes resolve, it grabs a small frame (`ThumbnailWidth` = 64) at the keyframe-**snapped**
  intro-end and sets `IntroThumbnailPath` to it — the initial grab fires on scan-resolve, at `IntroEnd.Snapped` (e.g.
  an intro requested at 11s on a 2s grid grabs the frame at the snapped 10s) (`RequestAllThumbnails`,
  `HandleThumbnailGrabber`).
- **I62** — moving the intro handle re-grabs at the **new** snapped cut time: a `Snapped` change on `IntroEnd`
  re-requests that handle's frame and `IntroThumbnailPath` updates to the frame at the new snapped position
  (`OnHandleChanged` → `RequestIntroThumbnail`).
- **I63** — rapid handle moves are **debounced + cancel-prior + latest-wins** (the settle window defaults to
  `DefaultThumbnailDebounce` = 200ms): while several moves happen inside the debounce window, only the **final**
  snapped cut reaches the service (one grab), the superseded requests being cancelled before they touch ffmpeg
  (`HandleThumbnailGrabber.Request`/`GrabAsync`).
- **I64** — the outro thumbnail is **gated on `HasOutro`**: `AddOutro` grabs the outro-start frame at its snapped
  time into `OutroThumbnailPath`, and `ClearOutro` cancels any in-flight outro grab and drops the frame
  (`OutroThumbnailPath` → null, the chip hides) (`AddOutro`/`RequestOutroThumbnail`, `ClearOutro`).
- **I65** — the grab is **best-effort / null-safe**: a grab that returns null (or fails) leaves the corresponding
  thumbnail path `null` — the view shows the muted placeholder chip, never an image, and nothing throws into the UI
  (`HandleThumbnailGrabber.OnResolved`, `GrabAsync` catch).
- **I66** — grabs are cancelled **per row on Remove/Clear**: `CancelScan` cancels the intro and outro grabbers'
  in-flight requests, so a removed/cleared row's parked grab faults, never reaches the service, and commits no frame
  path (`CancelScan` → `_introGrabber?.Cancel()` / `_outroGrabber?.Cancel()`).
- **I67** — cut-point frame grabs are bounded to **at most 3 concurrent** through a **dedicated** `SemaphoreSlim(3,3)`
  owned by the tab VM and shared into every row — separate from the keyframe-scan gate, so eye-candy frame grabs
  never starve the ffprobe scans that gate `CanRunBatch`; the permit is held only around the grab, never during the
  debounce (`_thumbnailGate`, `HandleThumbnailGrabber.GrabAsync` gate scope).

### Layout-mode-aware body (G-039 / T-112)
- **I68** — the Bulk body is **layout-mode-aware**: the preview pane and the row-list are the two
  children (`FirstChild` / `SecondChild`) of a single `OrientedSplitPanel` bound to
  `MainViewModel.IsVertical` — the same axis-flip container the Split screen uses — so the tab tracks
  the app's vertical/horizontal toggle: **vertical** stacks the pane above the list, **horizontal**
  places them side-by-side. The panel owns the themed splitter (draggable in both axes), the profiles
  header stays above and the output/Run footer below the split, and the one shared preview player
  survives the axis re-parent (the reused `PlayerView` reloads and re-attaches the Bulk FFME element).
  The Bulk split remembers its own position with **Bulk-specific** per-axis ratios
  (`MainViewModel.BulkHorizontalSplitRatio` / `BulkVerticalSplitRatio`, two-way bound, clamped
  `[0.05, 0.95]`, write-through to the matching `IAppSettings` keys, defaults `0.4` / `0.5`), kept
  **separate** from the Split tab's `HorizontalSplitRatio`/`VerticalSplitRatio` so dragging one tab's
  split never disturbs the other's. The `OrientedSplitPanel` flip mechanism is covered by SPEC-015
  (I16/I17); the settings round-trip by SPEC-009 (I23–I25). (`BulkCutView.xaml`,
  `MainViewModel.BulkHorizontalSplitRatio`/`BulkVerticalSplitRatio`)

### Apply-to-all re-fires every invocation (G-039 / T-111)
- **I69** — **apply-to-all re-fires on every invocation, not just the first** (both the per-row `⧉`
  apply-to-all and the profile **Apply → all**): `RelayCommand.RaiseCanExecuteChanged` raises the
  command's **own** `CanExecuteChanged` directly (deterministic notify) **in addition** to chaining
  `CommandManager.RequerySuggested`, so a gate-input change re-enables the bound button every time.
  (Pre-fix bug: `CanExecuteChanged` forwarded *solely* to `CommandManager.RequerySuggested`, so an
  explicit `RaiseCanExecuteChanged()` notified a subscribed handler **zero** times and the Apply→all
  button went stale after first use.) The `RelayCommand` change is **app-wide and additive** — every
  command's own subscribers now fire deterministically while the automatic input-driven and
  cross-command global requery is fully preserved. (`RelayCommand.RaiseCanExecuteChanged`,
  `RelayCommand.CanExecuteChanged`)
- **I70** — the Bulk VM re-raises the profile-command gates **explicitly** rather than leaning on the
  global-requery side effect: `RaiseProfileCommandStates` re-raises each of `SaveProfileCommand` /
  `ApplyProfileToSelectedCommand` / `ApplyProfileToAllCommand` / `DeleteProfileCommand`, and
  `RaiseRunState` **also** re-raises `ApplyProfileToAllCommand` (its gate additionally depends on the
  checked-row set / `Items` membership) — so a profile-selection change (no profile → disabled,
  profile picked → enabled) or a checked-row change re-evaluates the affected buttons deterministically.
  (`RaiseProfileCommandStates`, `RaiseRunState`)
- **I71** — a re-apply reads the **current** source and propagates its values every time: after the
  source row's cut is changed (or a profile re-saved under the same name), the next `ApplyToAll` /
  `ApplyProfileToAll` re-syncs every target to the current source values (never swallowed as a no-op),
  the `SelectedItem` / `SelectedProfile` selection **survives** the apply, and `ApplyToAllReport` is
  re-assigned on **each** apply so an identical re-apply never *looks* like a no-op. Apply semantics are
  unchanged (outro-from-END, re-snap + re-validate per target, invalidated rows reported — see I20–I24,
  I55–I57). (`ApplyToAll`, `ApplyProfileToAll`, `ApplyToAllReport`)

> **T-113 (profiles-card regroup) — no new invariant.** The flat inline profiles strip became a
> bordered **"Profiles"** card (thumbnail-aware picker · gold primary **Save current as…** · paired
> **Apply → selected / → all** split-control · muted **Delete**) — a **tokens-only, view-only** restyle
> with the **same** commands, thumbnail display, Save popup, and `HasProfiles` gating (I51–I60). No
> behavior changed, so it adds no invariant. (`BulkCutView.xaml`)

### Debounced preview-open on selection (G-040 / T-115)
- **I72** — selecting a row **lights it up synchronously** while the preview open is **debounced (~250ms) +
  latest-wins**: the `SelectedItem` setter updates the selection, raises `HasSelection`, and re-raises the
  playhead + profile command `CanExecute` states **synchronously** (instant highlight — none of these wait on
  the player), and only the shared-player `Player.Open` of the row's `Path` is deferred, behind the
  `DefaultSelectionOpenDebounce` (250ms) settle window. Each selection change first cancels the prior pending
  open (`CancelPendingOpen`, CTS-swap) then schedules `OpenAfterDebounceAsync`, so sweeping/arrowing through N
  rows issues **exactly one** `Player.Open` — of the row finally settled on, not one heavy FFME decoder init
  per row swept past (refines I27–I28: the open now defers behind the debounce instead of firing on set).
  (`SelectedItem` setter, `OpenOrUnloadSelected`, `CancelPendingOpen`, `OpenAfterDebounceAsync`,
  `DefaultSelectionOpenDebounce`)
- **I73** — the pending open is **cancelled before an unload or a run can be overtaken by a stale open**: a
  null/clear selection calls `CancelPendingOpen()` and then `Player.Unload()` immediately
  (`OpenOrUnloadSelected(null)`), and `RunBatchAsync` calls `CancelPendingOpen()` **before** `Player.Stop()`
  (stop-on-run wins) — so an open scheduled just before a clear or a batch run never lands after the
  unload/stop. The settled open still routes through `PlayerViewModel.Open` → `FfmeMediaPlayer`'s
  `MediaReopenGuard` (T-080), the last-line native-AV safety the debounce sits in front of and does **not**
  replace. (`OpenOrUnloadSelected`, `CancelPendingOpen`, `RunBatchAsync`)

> **T-116 (apply-to-all discoverability) — no new invariant.** The per-row apply-to-all (**⧉ "all"**) and
> remove (**✕**) buttons were the two rightmost `Auto` columns of one wide row `Grid`; after T-108's
> cut-point thumbnails + IN/OUT readouts that row overflowed the (horizontal-scroll-disabled) list viewport,
> so in **horizontal / narrow-list** mode they **clipped off the right edge and were unreachable** ("where's
> the apply-to-all button?"). The row content is now a `DockPanel` with those two actions in a **right-docked
> cluster** (DockPanel reserves their width from the edge before the fill) + `ClipToBounds` on the fill grid,
> so apply-to-all + remove **stay reachable on every row in both layout modes**; the per-row button keeps the
> ⧉ glyph and gains a short **"all"** label + tooltip, and the profiles split-control is relabelled
> **⧉ Apply to selected / ⧉ Apply to all**. **View/label/layout only** — same commands + parameters, no
> behavior change (apply semantics unchanged — see I20–I26, I55–I57), so it adds no invariant. This is a
> **layout-reachability** property (view-only WPF render, not unit-testable — the deferred-gap class of
> [_GAPS.md](_GAPS.md)). (`BulkCutView.xaml`)

## Links
- Design: D-004 (Bulk Cut screen)
- Goals: G-036 (batch trim), G-037 (shared preview + set-at-playhead + reusable cut profiles), G-038 (profile
  thumbnails + per-row cut-point frame previews — feature task T-108 for the per-row thumbnails here), G-039
  (Bulk Cut polish — layout-mode-aware body T-112, profiles-card regroup T-113, apply-to-all re-activation T-111),
  G-040 (Bulk Cut fixes — debounced preview-open T-115 (I72–I73), apply-to-all discoverability T-116 (view-only note))
- Related specs: the T-095 batch engine (`IBulkTrimEngine` / `BulkTrimEngine`) spec — not yet authored; T-094
  kept-segment request (`KeptSegmentSelector`); T-080 media reopen guard (`MediaReopenGuard`); T-102 cut-profile
  persistence (`CutProfile` / `IAppSettings.CutProfiles`); SPEC-007 (cut profiles — incl. the T-106/107
  profile-thumbnail model/store/glue); SPEC-005 (`IThumbnailService` frame source the cut-point grabs reuse);
  SPEC-009 (app settings — the Bulk-specific per-axis split ratios I68 persists, I23–I25); SPEC-015 (app shell —
  the `OrientedSplitPanel` axis-flip container I68 reuses, I16/I17).
- Key code: `src/App/ViewModels/BulkCutViewModel.cs` (`_thumbnailGate`, `RaiseProfileCommandStates`,
  `RaiseRunState`), `src/App/ViewModels/BulkItemViewModel.cs` (`IntroThumbnailPath`/`OutroThumbnailPath`,
  `HandleThumbnailGrabber`), `src/App/ViewModels/CutProfileApplier.cs`,
  `src/App/ViewModels/RelayCommand.cs` (`RaiseCanExecuteChanged` — T-111 deterministic notify),
  `src/App/ViewModels/MainViewModel.cs` (`BulkHorizontalSplitRatio`/`BulkVerticalSplitRatio` — T-112),
  `src/App/Views/BulkCutView.xaml` (`OrientedSplitPanel` body — T-112; "Profiles" card — T-113).
- Tests: `tests/App.Tests/BulkItemThumbnailTests.cs` (T-108 cut-point thumbnails) · `tests/App.Tests/BulkSpecGapTests.cs`
  · `tests/App.Tests/BulkCutApplyToAllReactivationTests.cs` (T-111 apply-to-all re-fires — I69–I71) ·
  `tests/App.Tests/AppSettingsTests.cs` (T-112 Bulk-ratio round-trip — I68's persisted ratios, tagged `serves-spec=SPEC-011`) ·
  `tests/App.Tests/BulkCutViewModelDebouncedPreviewTests.cs` (T-115 debounced preview-open — I72–I73, 4 tests tagged `serves-spec=SPEC-011`).
