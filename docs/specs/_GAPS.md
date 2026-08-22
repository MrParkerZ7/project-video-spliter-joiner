# Deferred spec-coverage gaps (todo-automate)

After the 2026-08-22 `todo-automate` bootstrap, **496 / 512 invariants (97%) are covered** by automated tests
(94 new gap tests added). The **16 below are deferred** — each needs a source refactor (extract a pure helper /
add an injectable seam) or is verified another way — NOT a test that can be written against the code as-is.
Tracked as a follow-up by **T-105** (make-testable refactors).

## Intentionally-unreachable defensive assertions (2)
- **SPEC-001 I22** — `SplitEngine.AssertCopyInvariant` throw: the real `SplitArgsBuilder` can't produce an
  encoder-contaminated command, so the guard is unreachable without an injection seam. Belt-and-suspenders; low value.
- **SPEC-003 I23** — Join runtime copy-invariant refusal: same — unreachable via the real builder.

## Needs an injectable seam (1)
- **SPEC-001 I31** — `SplitEngine.EnsureEnoughFreeSpace` uses `DriveInfo` directly (no seam). Add an
  `IDiskSpaceProbe` (mirroring `BulkTrimEngine`'s) → then the DiskFull + unmeasurable-drive branches unit-test cleanly.

## WPF-bound, verified live (2)
- **SPEC-013 I47** — `FfmeMediaPlayer.Seek` completion raises `PositionChanged` then `Seeked` (MediaElement-bound).
- **SPEC-013 I48** — `FfmeMediaPlayer.StepFrame` pauses first (MediaElement-bound).
  Both need a real `MediaElement` harness; verified via `app-run`.

## View-only WPF render — extract a pure helper to test (5)
- **SPEC-014 I31** — `TimelineView` click prefers nearest marker tick within 6px (extract `NearestTick(x,width,ticks,radius)`).
- **SPEC-014 I32** — Bulk scrub span geometry (introX/outroX/keep-span) (extract a pure span-geometry function).
- **SPEC-014 I33** — Bulk handle pick within 8px + tie-by-Y (extract `PickHandle` math).
- **SPEC-014 I34** — Waveform band visibility + dual-canvas overlay (WPF layout — visual QA).
- **SPEC-014 I35** — Waveform `PeakForColumn` max-per-column bucketing (promote to an internal pure helper → testable).

## Needs a windowed/STA harness or src refactor (6)
- **SPEC-015 I18** — Splitter `DragCompleted` ratio write-back (needs a real Measure/Arrange + simulated drag).
- **SPEC-015 I23** — Maximized clamp via native `WM_GETMINMAXINFO` P/Invoke (extract `rcWork→ptMax` math).
- **SPEC-015 I24** — App.xaml.cs global crash-handler wiring (refactor handler bodies to injectable methods).
- **SPEC-015 I26/I27/I28** — implicit ToolTip / ScrollBar / HeroButton resource styles (need the full merged
  theme-dictionary load — no cheap in-isolation path; the ErrorLogWriter/crash-format side I25 IS tested).

> The OrientedSplitPanel axis-flip + clamped sizing (SPEC-015 I16/I17) WERE closed via a minimal STA-thread harness
> (`OrientedSplitPanelTests`); the remaining resource-style gaps need the full theme graph, which that harness can't load cheaply.
