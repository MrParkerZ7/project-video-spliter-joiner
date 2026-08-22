---
id: SPEC-014
slug: timeline
area: app
title: Timeline strip (playhead, markers, waveform)
status: current
sources:
  - src/App/ViewModels/TimelineMath.cs
  - src/App/ViewModels/TimelineViewModel.cs
  - src/App/ViewModels/TimelineTick.cs
  - src/App/ViewModels/WaveformViewModel.cs
  - src/App/Views/TimelineView.xaml.cs
  - src/App/Views/BulkRowScrubView.xaml.cs
  - src/App/ViewModels/BulkItemViewModel.cs
  - src/App/ViewModels/CutMarkerViewModel.cs
serves-goal: [G-002, G-033, G-036]
updated: 2026-08-22
---

## What
The timeline strip is the horizontal scrub surface under the Split player and the per-row scrub bar in the Bulk Cut tab. It renders a **playhead**, one **marker tick** per cut, and an optional **audio-waveform band** over a normalized `x = time/duration · width` coordinate system, and turns clicks into either a **seek** (click near a tick) or a **snapped cut** (click on empty track). In the Bulk Cut tab the same coordinate system drives a **dual-handle scrub** — a gold intro-end handle, an optional blue outro-start handle, a bright keep-span between them and dimmed drop-scrims outside — where dragging a handle pushes a live requested time to the marker VM, which re-snaps to the nearest keyframe on release. All projection/mapping logic lives in WPF-free view models (`TimelineMath`, `TimelineViewModel`, `WaveformViewModel`, `BulkItemViewModel`, `CutMarkerViewModel`); the `*.xaml.cs` code-behind is a pure render + hit-test seam.

## Why
A time-only marker list is hard to reason about spatially; users need to *see* where their cuts fall relative to the whole clip and to place/seek cuts by pointing. The strip gives that spatial map while keeping the risky parts — snapping, dedupe, seek — routed through the already-tested owner commands (`SplitViewModel.AddCutAt`, `SeekToMarkerCommand`, `CutMarkerViewModel.Requested`→re-snap) so the timeline adds *projection*, never new cut logic. Splitting the pure time↔width mapping into `TimelineMath` makes the geometry unit-testable without a WPF host, and the waveform band (D-002) plus the Bulk dual-handle scrub (D-004) reuse that exact mapping so every overlay aligns to the same moment.

## Scope
**In:** the pure normalized time↔width mapping (`TimelineMath`); `TimelineViewModel` projection (playhead, marker ticks, event-driven re-projection) and its click/seek command routing; `WaveformViewModel` data/state contract (peaks, HasAudio, IsLoading lifecycle); the Bulk dual-handle scrub *geometry/interaction invariants that are unit-testable via the VM* — handle re-snap on requested-change, kept-duration, valid-cut/no-op-trim geometry, outro toggle. View-only render/hit-test behaviors (WPF code-behind) are documented and explicitly tagged **(view-only)**.
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

**View-only render / hit-test — WPF code-behind (documented, NOT unit-test targets)**
- **I31 (view-only)** — A timeline click prefers the nearest marker tick within `TickHitRadiusPx` (6px) → routes to seek; otherwise `ClickAt(clickX / width)` drops a snapped cut. Both the wave band and the track route through the same handler. *(TimelineView.OnTrackClicked + NearestTick)*
- **I32 (view-only)** — Bulk scrub render: `introX = clamp(introSnapped/total)·width`; dropped-intro scrim `[0→introX]` + dropped-outro scrim `[outroX→width]` (`DropScrimBrush`); keep-span `[min(introX,outroX)→max]` (`AccentMutedBrush`, the brightest element); while dragging, the grabbed handle paints at the clamped cursor X and on release repaints at the settled `Snapped` (snap-on-release). *(BulkRowScrubView.Redraw / OnUp)*
- **I33 (view-only)** — `PickHandle` grabs the nearer of intro/outro within `HandleHitRadiusPx` (8px); a miss does nothing (rows are not click-to-seek); an equidistant tie is broken by vertical position (top half → intro, bottom half → outro). *(BulkRowScrubView.PickHandle)*
- **I34 (view-only)** — The waveform band is `Visible` only when `Waveform.HasAudio` is true, else `Collapsed` (zero layout height); the playhead + marker ticks are drawn full-height across BOTH the wave and track canvases so they align as one unit. *(TimelineView.ApplyWaveBandVisibility + DrawOverlay)*
- **I35 (view-only)** — Waveform re-bucketing: each pixel column takes the **max** peak over its source window (`PeakForColumn`, so downsampling keeps the loudest sample rather than dropping it; fewer peaks than columns → nearest sample), and a `minBar` (0.75px) floor keeps silence visible as a faint centre line. *(TimelineView.PeakForColumn / BuildWaveGeometry)*

## Links
- Design: [D-002](../design/D-002-audio-waveform.md) (audio-waveform band) · [D-004](../design/D-004/README.md) (Bulk Cut dual-handle scrub) · [D-001](../design/D-001-vertical-monitor-mode.md) (vertical mode reuses the strip)
- Goals: G-002 (timeline overlay — playhead/markers/click-to-cut·seek, T-014) · G-033 (audio waveform, T-084) · G-036 (Bulk Cut tab, T-097)
- Related specs: — (none authored yet — adjacent: keyframe-snap / media-probe, player position/duration)
- Key code: `src/App/ViewModels/TimelineMath.cs` · `TimelineViewModel.cs` · `TimelineTick.cs` · `WaveformViewModel.cs` · `src/App/Views/TimelineView.xaml.cs` · `BulkRowScrubView.xaml.cs` · `src/App/ViewModels/BulkItemViewModel.cs` · `CutMarkerViewModel.cs`
- Tests: `tests/App.Tests/TimelineTests.cs` · `WaveformViewModelTests.cs` · `BulkItemViewModelTests.cs`
