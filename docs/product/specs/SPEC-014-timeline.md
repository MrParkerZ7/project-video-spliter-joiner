---
id: SPEC-014
slug: timeline
area: app
title: Timeline strip (playhead, markers, waveform)
status: current
sources:
  - src/App/ViewModels/TimelineMath.cs
  - src/App/ViewModels/BulkScrubMath.cs
  - src/App/ViewModels/TimelineViewModel.cs
  - src/App/ViewModels/TimelineTick.cs
  - src/App/ViewModels/WaveformViewModel.cs
  - src/App/ViewModels/SplitViewModel.cs
  - src/App/ViewModels/NullWaveformService.cs
  - src/App/Views/TimelineView.xaml.cs
  - src/App/Views/BulkRowScrubView.xaml.cs
  - src/App/ViewModels/BulkItemViewModel.cs
  - src/App/ViewModels/CutMarkerViewModel.cs
serves-goal: [G-002, G-033, G-036]
updated: 2026-09-12
---

## What
The timeline strip is the horizontal scrub surface under the Split player and the per-row scrub bar in the Bulk Cut tab. It renders a **playhead**, one **marker tick** per cut, and an optional **audio-waveform band** over a normalized `x = time/duration · width` coordinate system, and turns clicks into either a **seek** (click near a tick) or a **snapped cut** (click on empty track). In the Bulk Cut tab the same coordinate system drives a **dual-handle scrub** — a gold intro-end handle, an optional blue outro-start handle, a bright keep-span between them and dimmed drop-scrims outside — where dragging a handle pushes a live requested time to the marker VM, which re-snaps to the nearest keyframe on release. All projection/mapping logic lives in WPF-free view models (`TimelineMath`, `BulkScrubMath`, `TimelineViewModel`, `WaveformViewModel`, `BulkItemViewModel`, `CutMarkerViewModel`); the `*.xaml.cs` code-behind is a pure render + hit-test seam.

## Why
A time-only marker list is hard to reason about spatially; users need to *see* where their cuts fall relative to the whole clip and to place/seek cuts by pointing. The strip gives that spatial map while keeping the risky parts — snapping, dedupe, seek — routed through the already-tested owner commands (`SplitViewModel.AddCutAt`, `SeekToMarkerCommand`, `CutMarkerViewModel.Requested`→re-snap) so the timeline adds *projection*, never new cut logic. Splitting the pure time↔width mapping into `TimelineMath` makes the geometry unit-testable without a WPF host, and the waveform band (D-002) plus the Bulk dual-handle scrub (D-004) reuse that exact mapping so every overlay aligns to the same moment.

## Scope
**In:** the pure normalized time↔width mapping (`TimelineMath`); `TimelineViewModel` projection (playhead, marker ticks, event-driven re-projection) and its click/seek command routing; `WaveformViewModel` data/state contract (peaks, HasAudio, IsLoading lifecycle); the App-side **waveform extraction wiring** in `SplitViewModel` that drives that contract — kick-off, cancel + stale-result guard, bucket count, cache sweep, `Clear` reset (I36–I53); the Bulk dual-handle scrub *geometry/interaction invariants that are unit-testable via the VM* — handle re-snap on requested-change, kept-duration, valid-cut/no-op-trim geometry, outro toggle. The render/hit-test geometry is extracted into pure helpers (`TimelineMath`, `BulkScrubMath`, T-105) the code-behind delegates to; what remains purely WPF layout is explicitly tagged **(view-only)**.
**Out:** the actual pixel rendering (`DrawWave`/`DrawTrack`/`DrawOverlay`/`DrawHandle` Canvas draws, brush/theme resolution, `StreamGeometry` build) — verified by visual QA, not unit tests. Keyframe snapping internals (owned by `CutMarkerViewModel` / the Core media-probe snap spec), the split/trim engines, thumbnail-hover preview, and the player itself are adjacent specs.

## Current behavior & invariants

**Pure time↔width mapping — `TimelineMath`**
- **I1** — `ToNormalized(t, duration)` returns `clamp(t.Ticks / duration.Ticks, 0, 1)` when `duration > 0` (e.g. 5s of 10s → 0.5, 0s → 0, 10s → 1). *(TimelineMath.ToNormalized)*
- **I2** — `ToNormalized` returns `0` (never throws / divides by zero) when `duration <= TimeSpan.Zero`. *(TimelineMath.ToNormalized guard)*
- **I3** — `ToNormalized` clamps out-of-range times: `t < 0` → `0`, `t > duration` → `1`. *(Math.Clamp in ToNormalized)*
- **I4** — `FromNormalized(x, duration)` returns `FromTicks(round(duration.Ticks · clamp(x,0,1)))` when `duration > 0` (0.5 of 10s → 5s). *(TimelineMath.FromNormalized)*
- **I5** — `FromNormalized` clamps `x` before mapping: `x < 0` → `Zero`, `x > 1` → `duration`. *(Math.Clamp in FromNormalized)*
- **I6** — `FromNormalized` returns `TimeSpan.Zero` when `duration <= TimeSpan.Zero`. *(FromNormalized guard)*
- **I7** — `ToNormalized` and `FromNormalized` are inverse within rounding: `ToNormalized(FromNormalized(x, d), d) ≈ x` for `x ∈ [0,1]`. *(the two are documented inverses)*

**Projection — `TimelineViewModel`**
- **I8** — `PlayheadNormalized = ToNormalized(Player.Position, Duration)`, and is `0` when duration is unknown/zero. *(Reproject)*
- **I9** — `MarkerTicks` has exactly one `TimelineTick` per marker in `_owner.Markers`, with `Normalized = ToNormalized(m.Snapped, duration)`, `Time = m.Snapped`, and `Ref = m` (the originating `CutMarkerViewModel`). *(Reproject)*
- **I10** — Ticks are positioned by the marker's **Snapped** time, not its Requested time (a request of 30.4s that snaps to the 30s keyframe of a 60s clip → `Normalized ≈ 0.5`, `Time = 30s`). *(Reproject uses `m.Snapped`)*
- **I11** — Adding/removing a marker re-projects the tick list (subscribed to `Markers.CollectionChanged`). *(OnCollectionChanged → Reproject)*
- **I12** — A player `Position`, `Duration`, or `IsReady` change re-projects the strip (playhead + ticks recomputed). *(OnPlayerChanged → Reproject)*
- **I13** — The constructor throws `ArgumentNullException` when `owner` is null. *(TimelineViewModel ctor `owner ?? throw`)*

**Click routing — `TimelineViewModel` (VM-level, testable)**
- **I14** — `ClickAt(x)` drops a cut at `FromNormalized(x, duration)` routed through `_owner.AddCutAt` (which snaps + dedupes) — a click at 0.5 of a 10s clip with 1s keyframes yields one marker snapped to 5s. *(ClickAt)*
- **I15** — `ClickAt` is a no-op (no marker added) when no file is loaded. *(ClickAt `!_owner.HasFile` guard)*
- **I16** — `ClickAt` is a no-op when the duration is unknown/`<= Zero`. *(ClickAt duration guard)*
- **I17** — `ClickAt` respects clamped boundaries: `x = 0` → cut at `Zero`, `x = 1` → cut at `duration`. *(ClickAt via FromNormalized clamp)*
- **I18** — `SeekMarkerTick(tick)` routes to `_owner.SeekToMarkerCommand` (seeking to the marker's snapped time) only when `tick.Ref` is a `CutMarkerViewModel` and the command `CanExecute`; otherwise it is a no-op. *(SeekMarkerTick)*

**Waveform band data/state — `WaveformViewModel` (D-002 / T-084)**
- **I19** — A fresh VM is empty: `Peaks` empty, `HasAudio == false`, `IsLoading == false`. *(field initializers)*
- **I20** — `BeginLoad()` enters loading: `IsLoading == true`, `HasAudio == false`, and any prior file's `Peaks` are dropped (no stale wave against a new file). *(BeginLoad)*
- **I21** — `ApplyPeaks(nonNull)` shows the band: `HasAudio == true`, `Peaks` stored, `IsLoading == false`. *(ApplyPeaks)*
- **I22** — `ApplyPeaks(null)` and `ApplyNoAudio()` hide the band: `HasAudio == false`, `Peaks` empty, `IsLoading == false`. *(ApplyPeaks null branch / ApplyNoAudio)*
- **I23** — `ApplyPeaks` stores a defensive **copy** — mutating the caller's array afterward does not corrupt the stored peaks. *(ApplyPeaks `.Clone()`)*
- **I24** — `Reset()` returns to the empty/no-audio state: `Peaks` empty, `HasAudio == false`, `IsLoading == false`. *(Reset)*
- **I25** — `Peaks` is never null — it is an empty array when absent, so the view may index it freely. *(Peaks setter `?? Array.Empty`)*

**Bulk dual-handle scrub — VM-testable geometry/snap (`BulkItemViewModel` + `CutMarkerViewModel`, D-004 / T-097)**
- **I26** — Setting a handle's `Requested` re-snaps its `Snapped` to the nearest keyframe (the VM half of drag→snap-on-release): `IntroEnd.Requested = 12s` with keyframes every 10s → `Snapped == 10s`. *(CutMarkerViewModel.Requested setter → Resnap)*
- **I27** — `KeptDuration = (OutroStartSnapped ?? Duration) − IntroEndSnapped`, and is `null` until keyframes are ready (no outro: `Duration − introSnapped`; with outro: `outroSnapped − introSnapped`). *(BulkItemViewModel.KeptDuration)*
- **I28** — `IsValidCut` is true iff keyframes are ready, `IntroEndSnapped >= 0`, `upper <= Duration`, and `IntroEndSnapped < upper − MinKeptSpan` (where `upper = OutroStartSnapped ?? Duration`); a collapsed kept span (e.g. intro at 58s of 60s) → false → `RowState.Invalid`. *(BulkItemViewModel.IsValidCut)*
- **I29** — `AddOutro`/`ClearOutro` toggle `HasOutro`; `OutroStart` is the snapped handle when present and `null` when absent. *(BulkItemViewModel.HasOutro / AddOutro / ClearOutro)*
- **I30** — `IsNoOpTrim` is true when the net result keeps the whole file — intro ≈ 0 **and** (no outro, or outro ≈ EOF) — driving `RowState.NoOpTrim` and auto-disabling the row. *(BulkItemViewModel.IsNoOpTrim)*

**Render / hit-test — the geometry (x-mapping, keep-span, tick and handle hit tests, per-column peak) lives in pure helpers (`TimelineMath`, `BulkScrubMath`, T-105); the brushes, drag-time clamp and `minBar` floor stay in code-behind; I34 is wholly view-only**
- **I31** — A timeline click prefers the nearest marker tick within `TickHitRadiusPx` (6px) → routes to seek; otherwise `ClickAt(clickX / width)` drops a snapped cut. Both the wave band and the track route through the same handler. *(TimelineMath.NearestNormalizedIndex, called from TimelineView.OnTrackClicked + NearestTick)*
- **I32** — Bulk scrub render: `introX = clamp(introSnapped/total)·width`; dropped-intro scrim `[0→introX]` + dropped-outro scrim `[outroX→width]` (`DropScrimBrush`); keep-span `[min(introX,outroX)→max]` (`AccentMutedBrush`, the brightest element); while dragging, the grabbed handle paints at the clamped cursor X and on release repaints at the settled `Snapped` (snap-on-release). *(BulkScrubMath.SecondsToX / KeepSpan; drawn by BulkRowScrubView.Redraw / OnUp)*
- **I33** — `PickHandle` grabs the nearer of intro/outro within `HandleHitRadiusPx` (8px); a miss does nothing (rows are not click-to-seek); an equidistant tie is broken by vertical position (top half → intro, bottom half → outro). *(BulkScrubMath.PickHandle, called from BulkRowScrubView.PickHandle)*
- **I34 (view-only)** — The waveform band is `Visible` only when `Waveform.HasAudio` is true, else `Collapsed` (zero layout height); the playhead + marker ticks are drawn full-height across BOTH the wave and track canvases so they align as one unit. *(TimelineView.ApplyWaveBandVisibility + DrawOverlay)*
- **I35** — Waveform re-bucketing: each pixel column takes the **max** peak over its source window (`PeakForColumn`, so downsampling keeps the loudest sample rather than dropping it; fewer peaks than columns → nearest sample), and a `minBar` (0.75px) floor keeps silence visible as a faint centre line. *(TimelineMath.PeakForColumn; `minBar` in TimelineView.BuildWaveGeometry)*

**Waveform extraction wiring — `SplitViewModel.StartWaveformExtraction` (T-084 / D-002)**

SPEC-006 owns the Core extraction **service** (`IWaveformService` / `FfmpegWaveformService`); I19–I25 above own the band's **state**. This block owns the **wiring between them** — the load-side kick-off, the cancel + stale-result guard, the fixed bucket count, `BeginLoad`, and the outgoing file's cache sweep, all in `SplitViewModel`. Every adjacent spec currently pushes that wiring somewhere else: SPEC-006 § Scope calls it "the App-layer background extraction wiring in `SplitViewModel.LoadAsync` … a separate App spec", SPEC-010 § Scope calls the band "background-wired here but specified separately", SPEC-013 § Scope lists the waveform as out, and I19–I25 defers the lifecycle to "the owning `SplitViewModel`". It is specified **here**, and nowhere else.

- **I36** — Extraction is fire-and-forget: `LoadAsync` calls `StartWaveformExtraction(path, previousInput)` as its last step and never awaits it, so the load task completes with extraction still in flight (band loading, preview already usable). *(SplitViewModel.LoadAsync → StartWaveformExtraction; the `_ = extractTask.ContinueWith(…)` discard)*
- **I37** — A load that returns early — blank path, `Operation.IsRunning` (split in flight), or a failed/cancelled probe — never starts extraction, so the band keeps the state it already had. *(the guard returns in LoadAsync all precede the StartWaveformExtraction call — **not asserted**)*
- **I38** — Each successful load issues exactly **one** `GetPeaksAsync` call, for the freshly loaded path (load A then load B → the service saw requests `[A, B]`). *(StartWaveformExtraction, the single call site)*
- **I39** — Every request asks for the same fixed column count, `WaveformBuckets` = 1800 — the extraction resolution, never derived from the band's pixel width or the clip duration (the view re-buckets to width per I35). *(SplitViewModel.WaveformBuckets const; the test asserts only that the requested count is `> 0`)*
- **I40** — `Waveform.BeginLoad()` runs **before** the service call, so from the moment a new load commits the band is in the loading state with the previous file's peaks already dropped (I20) — a stale wave is never shown against the new file. *(StartWaveformExtraction — BeginLoad precedes GetPeaksAsync; the loading state is asserted for a first load, but the **ordering** is not observable through the gated fake and the drop-prior-peaks half is **not asserted** for a load-over-load — it follows from I20)*
- **I41** — Starting an extraction first cancels + disposes the previous extraction's `CancellationTokenSource` and nulls the field, so a superseded request observes a cancelled token before the replacement is created. *(StartWaveformExtraction `_waveformCts?.Cancel()/Dispose()/= null`; asserted jointly with I42, not in isolation)*
- **I42** — Stale-result guard: a continuation whose captured `cts` is no longer `_waveformCts` returns without touching the band — a late result from a superseded extraction can never overwrite the current file's wave (A resolves after B was loaded → B's peaks stand). *(the `ReferenceEquals(_waveformCts, cts)` check in the continuation)*
- **I43** — A current, non-null result is committed to the band: `Waveform.ApplyPeaks(peaks)` → `HasAudio == true`, `Peaks` = a copy of the returned array (I23), `IsLoading == false`. *(the continuation's ApplyPeaks call)*
- **I44** — A current `null` result hides the band: `HasAudio == false`, `Peaks` empty, `IsLoading == false` — a no-audio clip or a best-effort failure **resolves** the load rather than leaving it loading forever. *(ApplyPeaks(null) via the continuation)*
- **I45** — A faulted or cancelled extraction task is mapped to `null` by the `t.Status == TaskStatus.RanToCompletion` test and takes that same band-hidden path; the continuation never rethrows (a faulted antecedent's exception is never read — it is dropped, not propagated; the service is best-effort and does not throw). *(the continuation's status check — **not asserted**)*
- **I46** — The result is committed on the captured `SynchronizationContext` (`TaskScheduler.FromCurrentSynchronizationContext()` — the WPF dispatcher in the app) when one exists, and on `TaskScheduler.Default` when there is none, so VM state is mutated on the UI thread rather than the extraction thread. *(completionScheduler in StartWaveformExtraction; the tests' single-threaded `Pump` stands in for the dispatcher — the marshalling itself is **not asserted**)*
- **I47** — Load-over-load sweeps the **outgoing** file's waveform cache: the new extraction calls `_waveforms.Clear(previousInput)` before it starts, so replacing a file without an explicit `Clear` never leaks the prior file's cached peaks / temp PCM. *(LoadAsync captures `previousInput = _inputPath` before the commit; the sweep in StartWaveformExtraction)*
- **I48** — That sweep is guarded: nothing is cleared on the first load (no previous input) or when the **same** path is reloaded (ordinal compare), so reloading a file keeps its cached peaks. *(the `!IsNullOrEmpty(previousInput) && !Equals(previousInput, path, Ordinal)` guard — **not asserted**)*
- **I49** — `Clear()` — which no-ops unless `CanClear` (a file is loaded and no split is running — SPEC-010 I38), as do I50–I51 — cancels + disposes the in-flight extraction and nulls `_waveformCts`, which trips I42's guard: an extraction superseded by `Clear` can never re-show the band, however late it completes. *(SplitViewModel.Clear)*
- **I50** — `Clear()` sweeps the current file's waveform cache (`_waveforms.Clear(_inputPath)`), reading `_inputPath` **before** the reset nulls `InputPath`, so the sweep names the file being unloaded. *(Clear — the `if (_inputPath is { } clearedInput)` sweep precedes `InputPath = null`)*
- **I51** — `Clear()` resets the band synchronously via `Waveform.Reset()` (I24): `IsLoading == false`, `HasAudio == false`, `Peaks` empty — the same outcome whether extraction was still in flight or had already drawn a wave. *(Clear → Waveform.Reset)*
- **I52** — There is exactly one band VM: `SplitViewModel.Waveform` **is** `Timeline.Waveform`, so this wiring writes to the very instance `TimelineView` binds through the single `Timeline` DataContext. *(SplitViewModel.Waveform => Timeline.Waveform; constructed in the TimelineViewModel ctor — **not asserted**: every `vm.Waveform` read in the suite goes through that same expression-bodied property, so no assertion can distinguish it from a separate field)*
- **I53** — An omitted/null `waveforms` dependency falls back to `NullWaveformService.Instance`, whose `GetPeaksAsync` always resolves `null` and whose `Clear`/`ClearAll` are no-ops — the wiring above runs unchanged and the band simply stays hidden, so `SplitViewModel` needs no null checks. *(SplitViewModel ctor `waveforms ?? NullWaveformService.Instance`; NullWaveformService — **not asserted**)*

## Links
- Design: [D-002](../design/D-002-audio-waveform.md) (audio-waveform band) · [D-004](../design/D-004/README.md) (Bulk Cut dual-handle scrub) · [D-001](../design/D-001-vertical-monitor-mode.md) (vertical mode reuses the strip)
- Goals: G-002 (timeline overlay — playhead/markers/click-to-cut·seek, T-014) · G-033 (audio waveform, T-084) · G-036 (Bulk Cut tab, T-097)
- Related specs: SPEC-004 (keyframe-snap / media-probe) · SPEC-013 (preview player — position/duration) · SPEC-006 (waveform service — the peaks the band draws; the wiring that calls it is I36–I53 here) · SPEC-010 (Split screen — `LoadAsync`/`Clear` around the wiring; its I39 covers the same `Clear` from the screen's side)
- Key code: `src/App/ViewModels/TimelineMath.cs` · `BulkScrubMath.cs` · `TimelineViewModel.cs` · `TimelineTick.cs` · `WaveformViewModel.cs` · `src/App/ViewModels/SplitViewModel.cs` (`StartWaveformExtraction`, `LoadAsync`, `Clear`) · `NullWaveformService.cs` · `src/App/Views/TimelineView.xaml.cs` · `BulkRowScrubView.xaml.cs` · `src/App/ViewModels/BulkItemViewModel.cs` · `CutMarkerViewModel.cs`
- Tests: `tests/App.Tests/TimelineTests.cs` · `WaveformViewModelTests.cs` · `BulkItemViewModelTests.cs` · `ViewGeometryMathTests.cs` (the T-105 helpers — I31–I33, I35) · `SplitViewModelWaveformTests.cs` (the extraction wiring — I36, I38–I44, I47, I49–I51)
