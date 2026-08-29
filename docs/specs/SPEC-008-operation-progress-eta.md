---
id: SPEC-008
slug: operation-progress-eta
area: app
title: Operation progress, status & ETA
status: current
sources:
  - src/App/ViewModels/OperationViewModel.cs
  - src/App/ViewModels/EtaEstimator.cs
  - src/App/ViewModels/OperationState.cs
  - src/Core/Ffmpeg/OperationStatus.cs
serves-goal: [G-013, G-044]
updated: 2026-08-30
---

## What
Every long-running operation (split / join / detect) is driven through one reusable, WPF-free
view-model, `OperationViewModel`, that gives the operation a consistent progress + status + cancel +
ETA + friendly-error experience. It owns a five-state lifecycle (Idle → Running → Completed /
Cancelled / Failed), a fractional `Progress` bar that is *indeterminate* (busy pulse) until a real
fraction arrives and *determinate* afterwards, a Windows taskbar-button progress state, a
human-readable `StatusText` that tracks the engine's actual stage (Preparing → Splitting →
Finalizing → Done), a friendly result summary on success, and a live "~40s left" ETA computed by
`EtaEstimator` from real elapsed time versus reported progress. The ETA converges to a decreasing
number even on a sparse `-c copy` pass (via a seeded duration-based fallback) instead of reading
"estimating…" for the whole run, and shows nothing at all for a genuinely instant op. Beside the run
lifecycle it exposes one out-of-band seam, `ReportFailure`, so a deliberate one-shot gesture that is
NOT a tracked run (the Bulk Cut profile-thumbnail upload) can put a friendly failure on the very same
error surface a failed run uses instead of failing silently.

## Why
Users reported that a fast `-c copy` split "looks stuck": ffmpeg's `time=` output is sparse or
instant, so a naive 0..1 bar sits frozen at 0% and an ETA sits at "estimating…" the entire run. The
VM cures that by (a) showing an animated busy indicator + a real status line the instant a run
starts, before any fraction is known, and (b) seeding the estimator with the media's own duration so
the ETA can produce a shrinking upper-bound estimate until an accurate fraction-based number takes
over. Centralising this in one unit-testable, GUI-free view-model means split, join, and detect all
present identical, correct progress feedback — and it stays verifiable off the UI thread.

## Scope
**In:** the `OperationViewModel` lifecycle state machine and its `Run*` entry points; `Progress` /
`IsIndeterminate`; `StatusText` and staged-status formatting (`FormatStatus`); `EtaText` wiring;
`TaskbarProgressState`; `CancelCommand` / `CanCancel`; `ResultSummary` / `IsCompleted` /
`IsCancelled`; `SeedEstimatedDuration`; the out-of-band `ReportFailure` entry point (T-129) and its
own `State` rules; and the full `EtaEstimator` (EMA math, `MinUsableFraction`, duration-based
fallback, `Reset`, `FormatEta`).
**Out:** the concrete per-engine stage sequences and per-part progress emitted by `SplitEngine` /
`JoinEngine` (their own specs); `PartProgress` per-part reporting (the T-069 channel is only noted
here as additive, not specified); `UserFacingError` mapping content and the error-log affordance
(the error/diagnostics spec); the callers of `ReportFailure` and the wording they compose (SPEC-007 —
the Bulk Cut profile-thumbnail upload); the XAML bindings and converters that render these properties.

## Current behavior & invariants

**Lifecycle state machine — `OperationViewModel` / `OperationState`**
- **I1** — A fresh/idle VM has `State == Idle`, `Progress == 0`, `Error == null`, `ResultSummary == null`, `IsRunning`/`IsCompleted`/`IsCancelled` all false, `CanCancel == false`, and `CancelCommand.CanExecute(null) == false`. *(ctor + state getters)*
- **I2** — Starting any `Run*` method (`BeginRun`) sets `State = Running` (so `IsRunning` true, `CanCancel` true, `CancelCommand` enabled), sets `StatusText` to the supplied running status, resets `Progress` to 0, and clears both `Error` and `ResultSummary` *before* the work body runs. *(`BeginRun`)*
- **I3** — Work that finishes without a failure runs `Complete()`: `Progress` is set to 1 and `State = Completed` (so `IsCompleted` true; `IsCancelled`/`IsRunning` false; `Error` null). *(`RunAsync` / `RunWithResultAsync` success path, `Complete`)*
- **I4** — In `RunWithResultAsync`, if `failureSelector(result)` returns a non-null `UserFacingError`, `State = Failed` and `Error` is that error; no terminal bool flag is set. *(`RunWithResultAsync`)*
- **I5** — In `RunWithResultAsync`, if `failureSelector(result)` returns null, the run completes (`State = Completed`). *(`RunWithResultAsync`)*
- **I6** — A non-cancellation exception thrown by the work → `State = Failed` with a mapped `UserFacingError` (category `Unknown` for a generic exception); the bare exception string is never used as the headline `Message` but is preserved as detail / `RawTail`. *(`RunAsync`/`RunWithResultAsync` `catch(Exception)`, `MapException`)*
- **I7** — An `OperationCanceledException` from the work → `State = Cancelled` and `Error` stays null (cancellation is not a mapped failure), so `IsCancelled` is true and the surface is neutral, never error-red. *(`catch(OperationCanceledException)`)*
- **I8** — `CanCancel == (State == Running)`; `CancelCommand` is enabled only while running and, when executed, cancels the run's `CancellationTokenSource` so the work's token trips and it observes cancellation. *(`CanCancel`, `Cancel`, `CancelCommand`)*
- **I9** — `Reset()` cancels any in-flight run, then returns to `Idle`, clearing `Error`, `ResultSummary`, `Progress` (0), `StatusText` (""), and `EtaText` (null), and resetting the stopwatch and estimator. *(`Reset`)*

**Progress & indeterminate bar**
- **I10** — A fraction reported through the run's `IProgress<double>` is marshalled onto the captured `SynchronizationContext` (UI thread in-app, test thread under xUnit) and reaches the `Progress` property. *(`BeginRun` `Progress<double>`)*
- **I11** — `Progress` is clamped to `[0,1]`: a reported value outside the range is bounded before it is stored. *(`BeginRun` `Math.Clamp(value, 0d, 1d)`)*
- **I12** — `IsIndeterminate == (IsRunning && Progress <= 0)`: true while running before any fraction (busy pulse), flips to false the instant a real fraction (> 0) arrives, and is false whenever not running. *(`IsIndeterminate`)*

**Windows taskbar state — `TaskbarProgressState`**
- **I13** — `Failed` maps to `TaskbarItemProgressState.Error` (red), and `Failed` is checked *first* so a failed run shows red rather than clearing to None. *(`TaskbarProgressState`)*
- **I14** — Any not-running state (`Idle` / `Completed` / `Cancelled`) maps to `None`, clearing the bar so there is no stuck fill after a run ends or is reset. *(`TaskbarProgressState`)*
- **I15** — Running with no usable fraction yet (`IsIndeterminate`) maps to `Indeterminate` (the "Preparing" pulse). *(`TaskbarProgressState`)*
- **I16** — Running with a real fraction maps to `Normal` (green determinate fill). *(`TaskbarProgressState`)*
- **I17** — `PropertyChanged` is raised for `TaskbarProgressState` (alongside `IsRunning`/`IsCompleted`/`IsCancelled`/`CanCancel`/`IsIndeterminate`) whenever `State` or `Progress` changes, so the bindings update. *(`State` setter, `Progress` setter)*

**Lifecycle surfaces**
- **I18** — `IsCompleted == (State == Completed)` and `IsCancelled == (State == Cancelled)`; the Running / Completed / Cancelled surfaces are mutually exclusive, and `Failed` sets neither bool (it is surfaced via `Error`). *(`IsCompleted` / `IsCancelled`)*
- **I19** — `ResultSummary` is publicly settable, is set by the producing VM after a successful run, survives until the next run, and is cleared both at the start of every new run (`BeginRun`) and on `Reset`; its setter raises `PropertyChanged`. *(`ResultSummary`, `BeginRun`, `Reset`)*
- **I20** — `StatusText` is publicly settable mid-run and raises `PropertyChanged` so a bound label updates. *(`StatusText`)*

**Staged status text — `FormatStatus` / `OperationStatus` (G-013)**
- **I21** — Each `OperationStatus` reported on the staged channel updates `StatusText` via `FormatStatus`, in order and marshalled through the captured context: a split transitions "Preparing…" → "Splitting… (N parts)" → "Finalizing…" → "Done"; a join transitions "Checking compatibility…" → "Joining… (N clips)" → "Finalizing…" → "Done". *(`RunWithResultAsync` status channel, `FormatStatus`)*
- **I22** — `FormatStatus`: a non-"Done" stage with no detail → `"Stage…"`; with a detail → `"Stage… (detail)"`; the "Done" stage collapses to a plain `"Done"` (no ellipsis). *(`FormatStatus`)*
- **I23** — `FormatStatus` edge forms: a detail that itself ends in an ellipsis (an ongoing-action phrase) renders as `"Stage — detail"` (sub-status) rather than `"Stage… (detail)"`; a null/blank stage → empty string. *(`FormatStatus`)*

**ETA wiring — `OperationViewModel`**
- **I24** — While running, before any usable fraction arrives `EtaText` reads `"estimating…"` (never a fake number); a null estimate formats to `"estimating…"`. *(`BeginRun` seed, `UpdateEta`, `EtaEstimator.FormatEta(null)`)*
- **I25** — While running, a real fraction with measured elapsed time sets `EtaText` to a concrete `"~…left"` label. *(`UpdateEta`)*
- **I26** — `EtaText` is cleared to null at every terminal path (complete / cancel / fail via `EndRun`, and `Reset`) — the single clear point every terminal path funnels through — so the label is hidden once the run ends. *(`EndRun`, `Reset`)*
- **I27** — `SeedEstimatedDuration(TimeSpan?)` seeds the *next* run's duration-based fallback (a positive value only; null/non-positive disables it); the pending value is consumed once at `BeginRun` and then cleared, so it never leaks into a subsequent run. *(`SeedEstimatedDuration`, `BeginRun`)*

**ETA math — `EtaEstimator`**
- **I28** — The first usable sample seeds the estimate directly: `remaining ≈ elapsed × (1 − fraction) / fraction` (10s @ 0.25 → 30s; 20s @ 0.5 → 20s). *(`Update`)*
- **I29** — `MinUsableFraction` is a tiny epsilon (`1e-6`): the first real positive fraction, however small (e.g. 0.005), seeds a fraction-based estimate rather than being discarded as "too early". *(`Update`, `MinUsableFraction`)*
- **I30** — With no duration seed, a fraction of 0 or NaN yields null (unknowable → "estimating…"). *(`Update`, `UpdateFromDurationFallback`)*
- **I31** — A fraction ≥ 1 returns null and latches "done"; a stray later sample cannot resurrect an ETA — so a completed / instant op shows nothing. *(`Update` done latch)*
- **I32** — Subsequent fraction samples are EMA-smoothed (default `alpha = 0.4`): a jumpy raw spike is damped below the raw value yet still trends toward the new signal. *(`Update` EMA)*
- **I33** — Over a steady linear run the returned ETA trends monotonically downward (non-increasing) and stays bounded by a sane run length. *(`Update`)*
- **I34** — The constructor rejects an `alpha` outside `(0, 1]` with `ArgumentOutOfRangeException` (1.0 inclusive is accepted). *(ctor)*
- **I35** — `Reset()` clears the smoothed estimate, the done latch, the duration seed, and the fraction-estimate flag, so `CurrentEstimate()` is null and the next sample seeds fresh. *(`Reset`)*
- **I36** — Duration-based fallback (T-093): with a duration seeded via `SeedDuration` and only near-zero fractions, a running op (`elapsed > 0`) yields a non-null estimate that *decreases* as elapsed grows and converges within a few samples — never null for the whole run. *(`SeedDuration`, `UpdateFromDurationFallback`)*
- **I37** — The duration fallback is superseded the moment a real usable fraction arrives: the accurate fraction-based estimate seeds on that first usable fraction and takes over from the crude fallback. *(`Update`, `_haveFractionEstimate`)*

**ETA formatting — `EtaEstimator.FormatEta`**
- **I38** — `FormatEta`: null or a negative remaining → `"estimating…"`. *(`FormatEta`)*
- **I39** — `FormatEta` granularity: under a minute → `"~Ns left"`; a minute or more → `"~Xm Ys left"`, with a whole-"0s" tail dropped to `"~Xm left"`. *(`FormatEta`)*
- **I40** — `FormatEta`: a sub-second remaining rounds up to `"~1s left"`, never `"~0s"`. *(`FormatEta`)*

**Out-of-band failure reporting — `ReportFailure` (T-129)**
- **I41** — `ReportFailure(error)` with a non-null error and **no run in flight** puts that failure on the same surface a failed run uses: `Error` becomes it, `ResultSummary` is cleared, and `State = Failed` — so the Completed / Cancelled surfaces drop (I18) and the taskbar goes red (I13). A deliberate gesture that is not a tracked run therefore never fails silently, and a stale green "done" line never sits beside a fresh red error. *(`ReportFailure`)*
- **I42** — `ReportFailure(error)` **while a run is in flight** (`IsRunning`) sets `Error` and nothing else: `State` stays `Running`, `ResultSummary` is left alone, and the run keeps its own lifecycle (`CanCancel` still true) and still ends in its own Completed / Cancelled / Failed — a side gesture cannot derail it. The run's own end does not clear that error either: it survives onto the run's terminal surface until the next `BeginRun` or `Reset` clears it (I2 / I9). *(`ReportFailure` `IsRunning` early return; `Complete` / `EndRun` leave `Error` untouched)*
- **I43** — `ReportFailure(null)` **retracts** a previously reported failure: `Error` goes back to null, and a `State` of `Failed` returns to `Idle` so no red taskbar lingers with nothing left to explain it (I13 / I14). Every other state (`Idle` / `Completed` / `Cancelled`, or a run in flight) is left exactly as it is, and a `ResultSummary` an earlier report cleared is **not** restored. *(`ReportFailure` null branch)*
- **I44** — `ReportFailure` starts no run and ends none — it is purely additive: `Progress`, `StatusText`, `EtaText`, the stopwatch, the estimator and the run's `CancellationTokenSource` are all left as they were (neither `BeginRun` nor `EndRun` runs). Reporting from a fresh/idle VM therefore leaves `Progress == 0`, `EtaText == null` and `CanCancel == false`; reporting after a completed run leaves that run's `Progress == 1` in place. *(`ReportFailure`)*

## Links
- Design: —
- Goals: G-013 (staged status Preparing→…→Done) · G-044 (a failed thumbnail upload reaches the screen — `ReportFailure`)
- Related specs: SPEC-007 (cut-profiles — the profile-thumbnail upload that calls `ReportFailure` and composes its messages) (adjacent: split-engine, join-engine, per-part progress, error/diagnostics specs)
- Key code: `src/App/ViewModels/OperationViewModel.cs`, `src/App/ViewModels/EtaEstimator.cs`, `src/App/ViewModels/OperationState.cs`, `src/Core/Ffmpeg/OperationStatus.cs`
- Tests: `tests/App.Tests/OperationViewModelTests.cs`, `tests/App.Tests/EtaEstimatorTests.cs`, `tests/App.Tests/TaskbarProgressStateTests.cs`, `tests/App.Tests/OperationLifecycleSurfaceTests.cs`, `tests/App.Tests/OperationProgressVisibilityTests.cs`, `tests/App.Tests/StagedStatusWiringTests.cs`, `tests/App.Tests/BulkCutProfileThumbnailTests.cs` (`ReportFailure`, reached only through SPEC-007's upload path — no direct test)
