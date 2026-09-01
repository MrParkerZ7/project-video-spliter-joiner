---
id: SPEC-011
slug: bulk-cut-screen
area: app
title: Bulk Cut screen (batch trim UI)
status: current
sources:
  - src/App/ViewModels/BulkCutViewModel.cs
  - src/App/ViewModels/BulkItemViewModel.cs
  - src/App/ViewModels/CutMarkerViewModel.cs
  - src/App/ViewModels/CutTimeCommit.cs
  - src/App/ViewModels/CutProfileApplier.cs
  - src/App/ViewModels/RelayCommand.cs
  - src/App/ViewModels/MainViewModel.cs
serves-goal: [G-036, G-037, G-038, G-039, G-040, G-041, G-042, G-043]
updated: 2026-09-01
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
apply-to-all, select-all/select-none intent writes, cut-profile commands, shared preview player + selection,
set-at-playhead, the dedicated cut-point thumbnail gate, `CanRunBatch`, `RunBatchAsync` delegation, progress
fan-out, ledger routing, batch-state mapping) and `BulkItemViewModel` (per-row handles, keyframe scan, computed
validity/`RowState`/`KeptDuration`/`Warning`, the checkbox-**intent** vs computed-**eligibility** split
(`IsCheckedByUser` → `IsEnabled` / `IsExcludedDespiteBeingChecked` / `ExclusionReason`), the per-row cut-point
frame thumbnails `IntroThumbnailPath`/`OutroThumbnailPath`, request builders, batch fan-out hooks), plus
`CutProfileApplier` (pure apply/build for profiles as used by the tab),
the **row-facing snap readout** on `CutMarkerViewModel` (`SnapNote`/`HasSnapNote`/`SuppressSnapNote`, G-041/G-042)
and the WPF-free IN/OUT commit guard `CutTimeCommit`, and the two batch-wide output/precision choices the tab
owns (`ReplaceOriginal` + `CollisionIsInert`/`OutputNote` + the counted `ConfirmReplaceOriginals` seam;
`ExactCut` + `PrecisionNote`) as they appear on this screen.

**Out:** the batch execution engine itself (`IBulkTrimEngine` / `BulkTrimEngine` — collision policy resolution,
disk pre-flight, the per-item ffmpeg trim, `BatchResult`/`BatchOutcome` construction); the kept-segment request
math (`KeptSegmentSelector`, T-094); keyframe snapping internals (`CutMarkerViewModel.Resnap` /
`IMediaProbe.SnapToNearestKeyframe` — the readout that surfaces the result *is* In, I74–I76); the engine-side
execution of `OutputMode.ReplaceOriginal` (verify-every-part-before-replace, `File.Replace` with a backup,
`IOriginalDisposer`) and of `CutPrecision.Exact` (`SmartCutPlanner` / `SmartCutArgsBuilder` / `SmartCutEngine`
and the per-row fall-back routing) — this screen only *chooses* those modes; the reopen sequencing
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
  LoadFailed` and `IsEnabled == false` **whatever the user's checkbox says** — the exclusion is eligibility-side
  and never writes `IsCheckedByUser`, so a ticked load-failed row reads ticked-but-excluded with
  `ExclusionReason == "can't read this file"` (I96–I98) (`PopulateAsync` catch → `MarkLoadFailed`;
  `BulkItemViewModel.MarkLoadFailed`, `IsAutoDisabled`).
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
- **I13** — **eligibility** is auto-withdrawn once a row is known-ineligible: `IsAutoDisabled` is true for
  `LoadFailed`, or for `KeyframesReady` with `NoOpTrim`/`!IsValidCut`; a still-`Loading` row is **not**
  auto-disabled (`CanRunBatch` waits on it instead, I37). Since T-127 this is one half of the **read-only**
  `IsEnabled` — the other half being the user's intent (I96) — and it never writes the user's checkbox
  (`IsEnabled`, `IsAutoDisabled`).
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
  `Duration`; the source row itself is skipped. The filter is the user's raw **intent**, not eligibility
  (I94/I96) — so a target the app is currently excluding for having no cut set yet is still a legitimate apply
  target, which is exactly how the copied cut makes it eligible (`ApplyToAll` `foreach` filter).
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
  (`!Operation.IsRunning`), and **every** enabled row is `KeyframesReady`. It filters on the computed `IsEnabled`
  (intent **and** eligibility — I96), never on intent alone (`CanRunBatch`).
- **I38** — `RunLabel` is `Run bulk cut (N)` counting the `IsEnabled && IsValidCut` rows — so a ticked row the
  app excludes is **not** counted, and says why through `ExclusionReason` (I98) instead of dropping out silently
  (`RunLabel`).
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
- **I56** — `ApplyProfileToAll` applies `SelectedProfile` to every `IsCheckedByUser` row — the user's **intent**,
  which since T-127 is the very property the row checkbox binds to (I94), so this is the plain reading of "every
  ticked row" rather than a special case. Targeting intent (not `IsEnabled`) is what lets a profile re-validate a
  row the app is currently excluding for having no usable cut yet. Invalidated rows are reported through
  `ApplyToAllReport`; no-op without a profile (`ApplyProfileToAll`, `CanApplyProfileToAll`).
- **I57** — `CutProfileApplier.ApplyProfile` sets each ready target's intro to `IntroFromStart` clamped to
  `[0, Duration]`; when the profile has an `OutroFromEnd` tail it sets the outro at `Duration − tail` (clamped,
  from end) else clears the outro; it skips rows that are not `KeyframesReady`/have no `Duration` (not counted as
  applied) and collects invalidated targets into the returned report; null `profile`/`targets` throw
  `ArgumentNullException` (`CutProfileApplier.ApplyProfile`).
- **I58** — `DeleteSelectedProfile` removes `SelectedProfile` via `IAppSettings.DeleteProfile`, refreshes the bar,
  and re-points the selection at the first remaining profile (or null when none remain); no-op when unset
  (`DeleteSelectedProfile`).
- **I59** — the profile-command gates reflect state: `HasProfiles` (`Profiles.Count > 0`), `HasSelectedProfile`,
  `CanApplyProfileToSelected` (profile + selection), `CanApplyProfileToAll` (profile + ≥1 `IsCheckedByUser`
  row — intent again, I94, so it is live even for a list of freshly imported rows)
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

### Visible keyframe snapping — requested **and** where it landed (G-041 / T-119)
- **I74** — the row surfaces the user's **requested** time as the editable IN/OUT value and the keyframe it
  lands on as a **separate** readout: `SnapNote` is `→ <snapped> (<±delta>)` (e.g. `→ 00:04.0 (−1.0s)`),
  shown only while `HasSnapNote` is true — i.e. the snap is still pending **or** the delta is non-zero. An
  identity snap (a request already sitting on a keyframe, or a fine grid where 6s lands on 6s) leaves
  `HasSnapNote == false` and `SnapNote` **empty**, so a fine-GOP file carries no visual noise
  (`CutMarkerViewModel.SnapNote`/`HasSnapNote`; `BulkCutView.xaml` `IntroEnd.Requested` + `IntroEnd.SnapNote`).
- **I75** — the readout re-renders when `Requested` changes **even when `Snapped` does not move**. On a ~4s
  grid both 5s and 6s snap to 4s (nearest keyframe, exact ties resolving to the **earlier** one), so a row
  that showed only `Snapped` was pixel-identical before and after the second gesture — a correct snap made
  indistinguishable from a click the app ignored (the G-041 bug report). Writing `Requested = 6s` keeps
  `Snapped` at 4s but moves the note from `(−1.0s)` to `(−2.0s)` and raises `PropertyChanged` for **both**
  `Requested` and `SnapNote` (`Requested` setter → `Resnap()` → the `Delta` setter → `RaiseSnapNote`).
- **I76** — while the row's background keyframe scan is in flight the marker reads `→ snapping…` with
  `HasSnapNote` true, and resolves to the real note (or to empty) when `ResolveSnap` clears `IsSnapPending`
  (`SnapNote`, `IsSnapPending`, `ResolveSnap`).

### Editable IN/OUT commit guard (G-041 / T-118)
- **I77** — an **untouched** field never commits: `CutTimeCommit.IsUnchanged` compares the box text
  (surrounding whitespace trimmed, ordinal) against the VM's **own** render of the value the field currently
  displays (`CutMarkerViewModel.FormatClock`), and `TryResolveEdit` returns false on a match — so a bare
  focus pass can never write a VM-rendered value back into `Requested`. That write-back was the pre-T-118
  corruption: the field rendered the **snapped**, 0.1s-truncated time and re-committed it on every
  `LostFocus`, replacing a real 5s request with 4s, zeroing the snap delta (the only evidence a snap
  occurred) and sending the truncated value to the engine. The same guard preserves sub-tenth precision — a
  real 4.06s request renders `00:04.0` and is **not** overwritten by it (`CutTimeCommit.IsUnchanged`,
  `TryResolveEdit`).
- **I78** — a **genuine, parseable** edit still commits, with the typed value (`00:06.0` → 6s); unparseable
  input returns false with `TimeSpan.Zero`, so the caller writes nothing and instead normalizes the field
  back to VM truth by refreshing the binding — which also reverts the bad text (`TryResolveEdit`,
  `BulkCutView.xaml.cs` `CommitCutTime` → `UpdateTarget()`).
- **I79** — `TryParseClock` accepts plain seconds (`12`), `mm:ss` (`01:30`), `mm:ss.f` (`00:04.5`) and
  `h:mm:ss` (`1:00:00`), tolerating surrounding whitespace, and rejects null / empty / whitespace-only,
  non-numeric text, more than three segments, and negatives — every rejection yielding `TimeSpan.Zero`
  (`CutTimeCommit.TryParseClock`).

### Coarse-keyframe advisory (G-041 / T-120)
- **I80** — the coarse-grid note fires at `AverageGop` **`≥ 4s`** — inclusive, **refining I16**, whose strict
  `>` silently skipped the exactly-4.0s grid that produces the reported "5s and 6s both cut at 4s" — reading
  `coarse keyframes — cuts may move ~Ns` (`CoarseGopThreshold`, `Warning`).
- **I81** — the row **also** reports the offset the cut actually took: when the largest absolute handle delta
  across `IntroEnd`/`OutroStart` is `≥ 0.5s`, `Warning` adds `cut moved Ns to the nearest keyframe`, so a
  locally-coarse stretch on an otherwise fine mean grid is surfaced too. A sub-threshold snap (0.1s on a 1s
  grid) and an exact-keyframe request (delta 0) add nothing (`MaxSnapOffset`, `NoticeableSnapThreshold`,
  `Warning`).
- **I82** — both notes are **derived from the already-loaded keyframes**: reading `Warning` repeatedly runs no
  additional `GetKeyframesAsync`/ffprobe work (`Warning` computed from `Keyframes` + `_probe.AverageGop` +
  the handles' `Delta`).

### Replace-originals output mode (G-041 / T-123)
- **I83** — the default is the **non-destructive** mode: `ReplaceOriginal == false`, `CollisionIsInert ==
  false`, and `OutputNote` reads `Output → same folder · _trimmed suffix · originals kept` — a user who
  ignores this feature keeps every original (`ReplaceOriginal`, `CollisionIsInert`, `OutputNote`).
- **I84** — turning it on makes the collision control **inert** and re-states the destination: the setter
  raises `CollisionIsInert` (now true — the view greys the "Overwrite existing output" checkbox, because a
  destination that is always the source makes collision policy meaningless) **and** `OutputNote`, which now
  reads `Output → REPLACES each original file · originals go to the Recycle Bin`. The note is **bound, never
  hard-coded**, so it can never contradict the active mode (`ReplaceOriginal` setter, `BulkCutView.xaml`
  `IsEnabled="{Binding CollisionIsInert, …InverseBool}"`).
- **I85** — under this mode `RunBatchAsync` blocks on a **counted** confirmation before anything runs:
  `ConfirmReplaceOriginals(atRisk)` is called **exactly once**, with `atRisk` = the number of
  `IsEnabled && IsValidCut` rows (so the prompt names the blast radius), ahead of the preview release, the
  `Preparing` state, and any item build (`RunBatchAsync` `if (_replaceOriginal)` gate).
- **I86** — declining performs **zero** engine calls: `RunBatchAsync` returns immediately — no
  `IBulkTrimEngine.RunAsync`, no ledger, and `BatchState` never reaches `Completed`. The seam **defaults to
  refusing** (`ConfirmReplaceOriginals = _ => false`), so a host that forgot to wire a prompt can never
  silently replace the user's masters; the view supplies the real dialog, itself defaulting to **No**
  (`ConfirmReplaceOriginals`, `BulkCutView.xaml.cs` `ConfirmReplaceOriginals` → `MessageBoxResult.No`).
- **I87** — the choice reaches the engine on the run's options: accepting sends `BulkTrimOptions.Output ==
  OutputMode.ReplaceOriginal`, while the safe mode **never prompts** and sends `OutputMode.NewFile`. It is an
  axis **orthogonal** to collision policy — the VM keeps passing `Overwrite ? CollisionPolicy.Overwrite :
  CollisionPolicy` unchanged (I40) (`RunBatchAsync` → `new BulkTrimOptions(...)`).
- **I88** — under `ReplaceOriginal` the shared preview is **`Unload()`ed, not `Stop()`ped**, before the batch:
  `Stop` only halts playback while `Unload` closes the media element and **releases the file handle**, and a
  still-open handle on the selected row would make replacing that very file fail. The safe mode keeps the
  plain `Player.Stop()` (refines I31; the pending-open cancel of I73 still runs first) (`RunBatchAsync`
  `Player.Unload()` / `Player.Stop()`).

### Cut precision — lossless vs exact (G-042 / T-125)
- **I89** — `ExactCut` defaults **false** (the lossless stream-copy path is the app's identity and stays the
  default) and `PrecisionNote` says so: `Lossless — cuts snap to the nearest keyframe (instant, no quality
  loss)`; the run sends `BulkTrimOptions.Precision == CutPrecision.Lossless` (`ExactCut`, `PrecisionNote`,
  `RunBatchAsync`).
- **I90** — turning it on states the **cost up front, before the run**: `PrecisionNote` becomes `Exact — cuts
  land where you set them (re-encodes ~1s per cut)`, and the run sends `CutPrecision.Exact` — a third axis on
  `BulkTrimOptions`, orthogonal to both `Collision` and `Output` (`ExactCut` setter, `PrecisionNote`,
  `RunBatchAsync`).
- **I91** — flipping the toggle propagates to **every row currently in `Items`** through `SetExactCut`, which
  sets `SuppressSnapNote` on `IntroEnd` (and on `OutroStart` when the row has one): under Exact the row's
  `HasSnapNote` is false and `SnapNote` empty — advertising a 4s keyframe for a cut that will land on 5s
  would mislead — while `Requested` itself is **untouched**, so switching back to Lossless restores the full
  readout of I74 (`ExactCut` setter, `BulkItemViewModel.SetExactCut`, `CutMarkerViewModel.SuppressSnapNote`).
- **I92** — Exact also suppresses the **snap-magnitude** advisory of I81 (`cut moved Ns…`), because under
  exact cutting that offset will not occur; the **grid** advisory of I80 (`coarse keyframes…`) describes the
  source file itself and still shows (`Warning` → `var worstSnap = _exactCut ? TimeSpan.Zero :
  MaxSnapOffset()`).
- **I93** — flipping precision is **pure VM state**: it re-raises the note and the rows' readouts and performs
  **no** probe work — no `GetKeyframesAsync`, no re-scan (`ExactCut` setter, `SetExactCut`).

### Row checkbox intent vs computed eligibility (G-043 / T-127)
- **I94** — the row checkbox binds **two-way to the user's intent**, `IsCheckedByUser` — never to the computed
  `IsEnabled`. Intent defaults **true** for every row and is independent of eligibility: a freshly imported row
  has intro 0 and no outro, so it is a no-op trim and therefore ineligible, yet it starts (and stays) **ticked**
  until the user unticks it. Nothing on the eligibility path ever writes intent — a failed probe, a resolving
  scan, an out-of-range cut all leave `IsCheckedByUser` untouched. (Pre-T-127 the box bound to `IsEnabled`, whose
  getter answered false for every imported row while the backing intent field was already true: a click wrote
  `true` over `true`, the setter's `!=` guard short-circuited, no `PropertyChanged` was raised, and the gesture
  was **dead** — while a second click wrote `false`, which *did* take, silently dropping that row from
  apply-to-all and from the run.) (`BulkItemViewModel.IsCheckedByUser`, `BulkCutView.xaml`
  `IsChecked="{Binding IsCheckedByUser, Mode=TwoWay}"`)
- **I95** — an intent change **always notifies**, and notifies the whole derived set: setting `IsCheckedByUser` to
  a new value raises `PropertyChanged` for `IsCheckedByUser`, `IsEnabled`, `ExclusionReason` **and**
  `IsExcludedDespiteBeingChecked` — so the row's reason line and the batch projections (`CanRunBatch`/`RunLabel`,
  via `OnItemChanged` → `RaiseRunState`) re-evaluate on every tick. The setter keeps its `!=` idempotence guard,
  which is harmless now precisely because the checkbox binds to the *same* property the guard compares: a
  same-value write is a genuine no-op instead of a swallowed gesture (`IsCheckedByUser` setter,
  `BulkCutViewModel.OnItemChanged`).
- **I96** — `IsEnabled` is **read-only computed eligibility**: `IsCheckedByUser && !IsAutoDisabled` — the user
  wants the row **and** the app judges it usable. It has **no setter**, so nothing outside the row can force a row
  into the batch or overwrite the user's intent; the engine-facing filters read it (`CanRunBatch`, `RunLabel`, the
  `BulkTrimItem` build — I37–I39) while every user gesture writes intent. The auto-disable rule itself is
  unchanged (I13) (`IsEnabled`, `IsAutoDisabled`).
- **I97** — `IsExcludedDespiteBeingChecked` is exactly `IsCheckedByUser && IsAutoDisabled` — the previously
  invisible "ticked but not counted" state, now a first-class property of the row
  (`IsExcludedDespiteBeingChecked`).
- **I98** — `ExclusionReason` names that state in the user's words, and is **null whenever it does not apply**:
  null for an unticked row (`!IsCheckedByUser`) and null for an eligible one (`!IsAutoDisabled`) — which includes
  a still-scanning row, since loading is not auto-disabled (I13). When it does apply it is `can't read this file`
  for a load-failed row; otherwise (keyframes are ready by then) `nothing to trim yet — set an intro or outro`
  for a no-op trim, and an **invalid** cut splits into **two** sentences, because `IsValidCut` fails for two
  materially different reasons (I8) that need different fixes. When both handles sit plainly inside the file and
  only the gap between them is too small, it reads `intro and outro are too close — keep at least <N>s` — the
  row's own `MinKeptSpan` (I9) to one decimal, invariant-culture (e.g. `keep at least 2.0s`), naming the number
  the user has to beat. When a handle is genuinely outside the file — the **range** half of I8 failing (effective
  intro-end `< 0`, or effective outro-start `>` `Duration`), plus the defensive unknown-`Duration` fall-through —
  it reads `cut is outside the video`. The phrase **"out of range" appears in neither sentence**: telling a
  degenerate-but-in-range cut to fix its range sent the user after something that was not wrong (the T-127 review
  finding this split cures). Phrased as a **state, not an error** — "nothing to trim yet" is the normal condition
  of a row you just imported. The row renders it as a muted italic line, collapsed while null, so a
  ticked-but-excluded row is never silent about why `Run bulk cut (N)` does not count it (`ExclusionReason`,
  `BulkCutView.xaml` `Visibility="{Binding ExclusionReason, …NullToCollapsed}"`).
- **I99** — an **eligibility-side** change republishes the same trio: `RecomputeAll` — run on every handle
  `Requested`/`Snapped` move, on scan completion, and on `MarkLoadFailed` — raises `IsEnabled`, `ExclusionReason`
  and `IsExcludedDespiteBeingChecked` alongside the other computed row properties, so setting a real cut clears
  the reason line and lights the row without the user touching the checkbox (`RecomputeAll`, `OnHandleChanged`,
  `MarkLoadFailed`).

### Select all / select none for the item list (G-043 / T-128)
- **I100** — `SetAllItemsChecked(bool)` — behind `SelectAllItemsCommand` / `SelectNoItemsCommand` — writes
  **intent** across every row: `IsCheckedByUser`, never the computed `IsEnabled`. Select-all therefore ticks
  auto-excluded rows too; they stay excluded-with-a-reason (I98) until they have a real cut, at which point the
  preserved intent puts them straight into the batch — which is also what makes *select all* followed by
  *apply profile → all* (I56) compose as the user expects (`SetAllItemsChecked`, `SelectAllItemsCommand`,
  `SelectNoItemsCommand`).
- **I101** — the bulk write is **pure O(N) VM state**: a single `foreach` over `Items` setting a bool, with **no**
  probe, no keyframe scan, no thumbnail grab and no re-snap triggered by it. It ends with **one** trailing
  `RaiseRunState()` so the batch projections (`CanRunBatch`/`RunLabel`/`CanClear`/`CanChangeSelection` plus the
  run / clear / apply-to-all / select-all / select-none / apply-profile-to-all command gates) are published for
  the whole write even when no row's value actually changed (an already-all-ticked list raises nothing per row).
  Rows that *did* change also notify individually through `OnItemChanged`, so the refresh is O(N) notifications of
  O(1) work each — never per-row I/O (`SetAllItemsChecked`, `RaiseRunState`, `OnItemChanged`).
- **I102** — both commands are gated by `CanChangeSelection` = `Items.Count > 0 && !Operation.IsRunning`
  (mirroring `CanClear`): dead on an empty list and for the duration of a run. `RaiseRunState` re-raises
  `CanChangeSelection` **and** both commands' `CanExecuteChanged`, so adds/removes/`Clear` and the run's start/end
  re-evaluate the two buttons deterministically (I69's own-subscriber notify); the header strip that carries them
  is itself collapsed while the list is empty (`CanChangeSelection`, the two command predicates, `RaiseRunState`,
  `BulkCutView.xaml` header `Visibility`).

- **I103** — the two set-at-playhead gestures have a user-visible SCOPE: `ApplyCutToAllRows`, bound to a
  checkbox beside them and persisted as `BulkApplyCutToAllRows`. It defaults to **ON** — absent in an older
  settings file reads as ON — because the single-row behaviour is what let a user set one cut, press Run,
  and cut one file out of a batch (T-133).
- **I104** — when ON, `SetIntroAtPlayhead` / `SetOutroAtPlayhead` write the selected row and then delegate to
  `ApplyToAll(_selectedItem)` — NOT a second copy implementation. The fan-out therefore inherits every
  apply-to-all rule verbatim: it targets `IsCheckedByUser` rows, re-snaps against each target's own
  keyframes, measures the outro **from the END of each file** so uneven lengths align, mirrors a cleared
  outro, and reports invalidated rows through `ApplyToAllReport`.
- **I105** — when OFF, only the selected row changes. An unticked row is never written to in either mode.

- **I106** — `RunScopeSummary` states what Run will do BEFORE it is pressed: `Will cut N of M`, followed by
  each distinct `ExclusionReason` with a count, plus `not ticked` and `still scanning` tallies. It is
  **null when every row will run** (and when the list is empty), so the line never becomes furniture.
  The reasons are read VERBATIM off the rows — the wording lives only in `ExclusionReason` (T-134).
- **I107** — `RunScopeIsWarning` is true only when a row the user TICKED is being excluded
  (`IsExcludedDespiteBeingChecked`). Rows the user unticked are counted calmly: alarming someone about
  their own deliberate choice teaches them to ignore the line.
- **I108** — both republish on exactly the signals `RunLabel` does, so the stated count can never lag the
  list it describes.

- **I109** — the profile bar is a **`WrapPanel`**, never a horizontal `StackPanel`. A `StackPanel` neither
  wraps nor scrolls: it silently CLIPS whatever does not fit off the right edge, and the bar carries eight
  controls while the tab's split is user-resizable (T-136).
- **I110** — the bar's ACTION buttons (📷 Use current frame, 🖼 Thumbnail…) are **shown and disabled with a
  reason**, never hidden, when there is no profile to act on — `SnapshotUnavailableReason` names the
  missing precondition. Only the picker and Delete are `HasProfiles`-gated, because a picker with nothing
  to pick is genuinely meaningless. Hiding a control is how the upload became unreachable in G-044 and how
  the snapshot button became unfindable in T-136.

- **I111** — **Delete originals** (T-144) offers a row's SOURCE for the Recycle Bin only when every one of
  these holds, re-evaluated on each read rather than remembered from the run: the row is
  `RowState.Done`; its output exists and is **non-empty now**; the output is **not the original itself**
  (under `ReplaceOriginal` the original already became the output, so binning it destroys the only copy);
  the original is still present; no run is in flight; and an `IOriginalDisposer` is available — a null
  disposer means the feature is UNAVAILABLE, never "delete permanently".
- **I112** — deletion goes through `IOriginalDisposer` (the Recycle Bin in production), never
  `File.Delete`, behind a confirmation that is told the file COUNT and the BYTES reclaimed and defaults to
  No. Declining touches nothing.
- **I113** — the sweep is per-row isolated: a file the disposer declines (locked, permission) does not stop
  the others, and the result summary states binned vs refused rather than implying total success. The
  disposer is best-effort by contract, so success is VERIFIED (the file is gone) rather than assumed.
- **I114** — a row whose original has been binned is marked `OriginalDeleted`, is auto-excluded from any
  future batch, and is not offered for deletion again. Its source no longer exists.

- **I115** — the app **releases its own hold before binning** (T-145). The shared preview keeps an open
  handle on the selected row's file, so a delete-originals sweep run while a row is previewed refused
  exactly that file and quietly returned N-1. Before the sweep the selection is dropped and the player is
  `Unload()`ed — **`Unload`, not `Stop`**: `Stop` halts playback but leaves the media element holding the
  handle, which is precisely why the first attempt looked correct and was not. The replace-originals path
  already did this for the same reason (I88); the delete path did not, and now does.
- **I116** — dropping the selection is part of the release, not incidental tidying: leaving a row selected
  lets the next interaction **re-open a file that is about to be binned**, re-acquiring the handle after it
  was released.
- **I117** — releasing our own handle does not weaken the external case: a file locked by **another**
  process still lands in `refused` and is named in the summary (I113), so "we let go of it" never gets
  reported as "it was deleted".
- **I118** — eligibility is re-evaluated **after** the unload rather than captured before it, so the set
  that is binned is the set that was actually eligible at deletion time.

- **I121** — the per-row cut-point grab is **fire-and-forget but observable** (T-137). A handle move must
  never block the UI, so the grab is started and not awaited — and that left tests with nothing to wait on
  but a wall-clock timeout, which loses whenever the thread pool is busy (a ~40%-per-run flake at
  solution level). `BulkItemViewModel.InFlightGrabs` exposes the in-flight work `internal`ly so a test can
  await the work itself. Production behaviour is unchanged; only its observability is.

- **I120** — the screen's **content area keeps its space however tall the header grows** (T-138). The
  window is a three-row grid — header `Auto`, content `*` with `MinHeight="220"`, footer `Auto` — and the
  middle row's star-sizing plus its floor are what stop a growing header from collapsing the screen. This
  matters because saving the FIRST profile flips `HasProfiles` false→true and reveals the picker, Delete
  and apply controls at once: the header genuinely does get taller at that moment. Asserted by laying the
  real view out at five window sizes and forcing the header taller
  (`BulkCutViewLayoutTests`); making the row `Auto`, dropping the floor, or shrinking it all fail there.

- **I119** — the Delete-originals control is styled in the screen's existing **danger vocabulary** and sits
  at the far LEFT of the footer, away from *Run bulk cut* (T-146). It is destructive and irreversible in a
  way nothing else in the footer is, so it must read as such at a glance without shouting at rest — and it
  must not sit next to the button people press repeatedly. It still names the file COUNT and the BYTES
  reclaimed, which is the entire reason to press it.

## Links
- Design: D-004 (Bulk Cut screen)
- Goals: G-036 (batch trim), G-037 (shared preview + set-at-playhead + reusable cut profiles), G-038 (profile
  thumbnails + per-row cut-point frame previews — feature task T-108 for the per-row thumbnails here), G-039
  (Bulk Cut polish — layout-mode-aware body T-112, profiles-card regroup T-113, apply-to-all re-activation T-111),
  G-040 (Bulk Cut fixes — debounced preview-open T-115 (I72–I73), apply-to-all discoverability T-116 (view-only note)),
  G-041 (make keyframe snapping visible + the replace-originals output mode — commit guard T-118 (I77–I79),
  requested→snapped readout T-119 (I74–I76), real-snap-magnitude advisory T-120 (I80–I82), replace-originals
  UI T-123 (I83–I88)), G-042 (frame-exact cutting — the precision choice T-125 (I89–I93)), G-043 (tick the
  rows you want and have Run agree — the checkbox-intent/eligibility split T-127 (I94–I99), select all /
  select none T-128 (I100–I102))
- Related specs: SPEC-002 (the T-095 batch engine `IBulkTrimEngine` / `BulkTrimEngine` — incl. the engine-side
  handling of the `OutputMode`/`CutPrecision` axes this screen selects, kept orthogonal to `CollisionPolicy`'s
  own "what if the destination is taken?" question); T-094 kept-segment request (`KeptSegmentSelector`);
  T-080 media reopen guard (`MediaReopenGuard`); T-102 cut-profile
  persistence (`CutProfile` / `IAppSettings.CutProfiles`); SPEC-007 (cut profiles — incl. the T-106/107
  profile-thumbnail model/store/glue); SPEC-005 (`IThumbnailService` frame source the cut-point grabs reuse);
  SPEC-009 (app settings — the Bulk-specific per-axis split ratios I68 persists, I23–I25); SPEC-015 (app shell —
  the `OrientedSplitPanel` axis-flip container I68 reuses, I16/I17); SPEC-001 (stream-copy split — the
  `SmartCutEngine` that services `CutPrecision.Exact`).
- Key code: `src/App/ViewModels/BulkCutViewModel.cs` (`_thumbnailGate`, `RaiseProfileCommandStates`,
  `RaiseRunState`; `ReplaceOriginal`/`CollisionIsInert`/`OutputNote`/`ConfirmReplaceOriginals` — T-123;
  `ExactCut`/`PrecisionNote` — T-125; `SelectAllItemsCommand`/`SelectNoItemsCommand`/`SetAllItemsChecked`/
  `CanChangeSelection` — T-128), `src/App/ViewModels/BulkItemViewModel.cs`
  (`IsCheckedByUser`/`IsEnabled`/`IsAutoDisabled`/`IsExcludedDespiteBeingChecked`/`ExclusionReason` — T-127;
  `IntroThumbnailPath`/`OutroThumbnailPath`, `HandleThumbnailGrabber`; `CoarseGopThreshold`/
  `NoticeableSnapThreshold`/`MaxSnapOffset` — T-120; `SetExactCut` — T-125),
  `src/App/ViewModels/CutMarkerViewModel.cs` (`SnapNote`/`HasSnapNote` — T-119; `SuppressSnapNote` — T-125),
  `src/App/ViewModels/CutTimeCommit.cs` (`IsUnchanged`/`TryResolveEdit`/`TryParseClock` — T-118),
  `src/App/ViewModels/CutProfileApplier.cs`,
  `src/App/ViewModels/RelayCommand.cs` (`RaiseCanExecuteChanged` — T-111 deterministic notify),
  `src/App/ViewModels/MainViewModel.cs` (`BulkHorizontalSplitRatio`/`BulkVerticalSplitRatio` — T-112),
  `src/App/Views/BulkCutView.xaml` (`OrientedSplitPanel` body — T-112; "Profiles" card — T-113; the row
  checkbox bound to `IsCheckedByUser` + the `ExclusionReason` line — T-127; the Select all / Select none
  header — T-128).
- Tests: `tests/App.Tests/BulkItemThumbnailTests.cs` (T-108 cut-point thumbnails) · `tests/App.Tests/BulkSpecGapTests.cs`
  · `tests/App.Tests/BulkCutApplyToAllReactivationTests.cs` (T-111 apply-to-all re-fires — I69–I71) ·
  `tests/App.Tests/AppSettingsTests.cs` (T-112 Bulk-ratio round-trip — I68's persisted ratios, tagged `serves-spec=SPEC-011`) ·
  `tests/App.Tests/BulkCutViewModelDebouncedPreviewTests.cs` (T-115 debounced preview-open — I72–I73, 4 tests tagged `serves-spec=SPEC-011`) ·
  `tests/App.Tests/CutTimeCommitTests.cs` (T-118 commit guard + clock parser — I77–I79, 7 test methods) ·
  `tests/App.Tests/SnapVisibilityTests.cs` (T-119 requested→snapped readout — I74–I76, 5 tests) ·
  `tests/App.Tests/SnapWarningTests.cs` (T-120 coarse-GOP + real-snap advisory — I80–I82, 5 tests) ·
  `tests/App.Tests/ReplaceOriginalModeTests.cs` (T-123 replace-originals UI — I83–I88, 7 tests) ·
  `tests/App.Tests/ExactCutModeTests.cs` (T-125 precision choice — I89–I93, 8 tests) — all tagged `serves-spec=SPEC-011`.
  The intent-side targeting filters are additionally asserted by `tests/App.Tests/BulkCutProfileCommandsTests.cs`
  (I56) and `tests/App.Tests/BulkSpecGapTests.cs` (I22), both reading `IsCheckedByUser` directly.
