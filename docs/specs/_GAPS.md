# Deferred spec-coverage gaps (todo-automate)

After the 2026-08-22 `todo-automate` bootstrap, 549 / 565 invariants (97%) were covered and **16 were
deferred** — each needing a source refactor (extract a pure helper / add an injectable seam) or verified
another way. **T-105 (2026-08-24) closed 7 of them** by making the code testable (behavior-preserving
extractions, views/handlers delegating to the new helpers). Coverage is now **556 / 565 (98%)**; the
**9 below remain deferred**.

## ✅ Closed by T-105 (helper-extraction / seam + `serves-spec` tests)
- **SPEC-001 I31** — `SplitEngine` now takes an injectable `IDiskSpaceProbe` (relocated to
  `Core/Io/IDiskSpaceProbe.cs`, shared with `BulkTrimEngine`); `SplitEngineSpecGapTests` covers the
  DiskFull-shortfall (rejects before ffmpeg, zero runs) + unmeasurable-drive (skips the pre-flight) branches.
- **SPEC-014 I31** — timeline marker-tick hit test → `TimelineMath.NearestNormalizedIndex`.
- **SPEC-014 I32** — Bulk scrub span geometry → `BulkScrubMath.SecondsToX` + `KeepSpan`.
- **SPEC-014 I33** — Bulk handle pick (8px + tie-by-Y) → `BulkScrubMath.PickHandle`.
- **SPEC-014 I35** — waveform max-per-column bucketing → `TimelineMath.PeakForColumn`.
  (All four SPEC-014 helpers covered by `ViewGeometryMathTests`; `TimelineView`/`BulkRowScrubView` delegate.)
- **SPEC-015 I23** — maximized `WM_GETMINMAXINFO` `rcWork→ptMax` clamp → `WindowChromeMath.MaximizedWorkAreaBounds`.
- **SPEC-015 I24** — crash-dialog message composition → `CrashReport.ComposeMessage` (the exact user-facing +
  clipboard text). *Note:* the per-sink recovery disposition (Dispatcher = `Handled`, UnobservedTask =
  `SetObserved`, AppDomain = log-only) is a per-handler constant, verified by inspection — no indirection
  seam was added for it (that would be over-engineering a 3-way constant).
  (Both covered by `WindowChromeAndCrashTests`; `MainWindow`/`App.xaml.cs` delegate.)

---

## Remaining deferred (13)

### Intentionally-unreachable defensive assertions (2)
- **SPEC-001 I22** — `SplitEngine.AssertCopyInvariant` throw: the real `SplitArgsBuilder` can't produce an
  encoder-contaminated command, so the guard is unreachable without an injection seam. Belt-and-suspenders; low value.
- **SPEC-003 I23** — Join runtime copy-invariant refusal: same — unreachable via the real builder.

### WPF-bound, verified live (2)
- **SPEC-013 I47** — `FfmeMediaPlayer.Seek` completion raises `PositionChanged` then `Seeked` (MediaElement-bound).
- **SPEC-013 I48** — `FfmeMediaPlayer.StepFrame` pauses first (MediaElement-bound).
  Both need a real `MediaElement` harness; verified via `app-run`.

### View-only WPF render / layout (1)
- **SPEC-014 I34** — Waveform band visibility + dual-canvas overlay (WPF layout — visual QA).

### Needs a windowed/STA harness (6)
- **SPEC-015 I18** — Splitter `DragCompleted` ratio write-back (needs a real Measure/Arrange + simulated drag).
- **SPEC-015 I26/I27/I28** — implicit ToolTip / ScrollBar / HeroButton resource styles (need the full merged
  theme-dictionary load — no cheap in-isolation path; the `ErrorLogWriter`/crash-format side I25 IS tested).

> The OrientedSplitPanel axis-flip + clamped sizing (SPEC-015 I16/I17) WERE closed via a minimal STA-thread harness
> (`OrientedSplitPanelTests`); the remaining resource-style gaps need the full theme graph, which that harness can't load cheaply.

- **SPEC-010 I47 / SPEC-012 I36** (added 2026-09-04, T-154) — the `dragdrop.log` `accepted` flag reporting
  the real accept/refuse decision. The value is computed in each view's `OnDrop` code-behind from a live
  `DragEventArgs`, so asserting it needs a windowed harness that can raise a real drop. The view-model
  half — what the refusal SAYS — is fully covered by `SplitAndJoinDropFeedbackTests`.

### Uncovered by design — nothing to assert (2)
- **SPEC-010 I48 / SPEC-012 I37 / SPEC-011 I143** (added 2026-09-04, T-154) — a drag holding no recognised
  video is refused by `OnDragOver` before any drop event exists, so no application code runs and there is
  no observable behaviour to test. Recorded as an invariant so the boundary is known rather than
  rediscovered; the decision to keep it that way is **ADR-0023**.
