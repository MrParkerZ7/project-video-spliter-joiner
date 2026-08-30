---
id: SPEC-013
slug: preview-player
area: app
title: Preview player (transport, seek, reopen safety)
status: current
sources:
  - src/App/ViewModels/PlayerViewModel.cs
  - src/App/Media/FfmeMediaPlayer.cs
  - src/App/Media/MediaReopenGuard.cs
  - src/App/Media/IMediaPlayer.cs
serves-goal: [G-009, G-016, G-028, G-030, G-031]
updated: 2026-08-22
---

## What
The in-app video preview player: a WPF-free `PlayerViewModel` sitting over an `IMediaPlayer`
(the FFME-backed `FfmeMediaPlayer` in production, a fake in tests) that exposes an observable
transport surface — `Position` / `Duration` / `IsPlaying` / `IsReady`, formatted clock text,
play/pause, stop, relative jog (`SkipCommand`, seconds parameter), jump-to-start/end, single-frame
step, volume/mute/speed — plus a scrub bar that two-way-binds a slider to `Position`. On top of
plain transport it carries three robustness layers: a **scrub pop-back guard** that pins the playhead
at the seek target so stale async playback echoes can't warp it back (T-033); a **live-scrub
coalesce/throttle** so a fast drag never backs up a queue of seeks (T-051) and a **click-to-point
dedupe** so a single track click converges on exactly one seek (T-075); a **hover-thumbnail** preview
fed the loaded file (T-078); and, in `FfmeMediaPlayer` + `MediaReopenGuard`, a **crash-safe Close→Open
lifecycle** that waits an FFME element out of its transitional (`IsClosing`/`IsOpening`/`IsChanging`)
state before reopening, fixing a native AccessViolation on rapid clear→load (T-080).

## Why
FFME transport is asynchronous: a seek to T is dispatched but playback keeps ticking, so
`PositionChanged` echoes arrive with **stale** positions before the seek lands — naively applied they
yank the slider/display off T ("pop-back"), and a fast drag queues a backlog of seeks that lags the
pin. Worse, issuing FFME `Open` while a prior `Close` (fire-and-forget from Clear) is still in flight
is a known **native crash** (an AccessViolation that bypasses managed handlers). This feature exists
to give a responsive, correct preview: the playhead follows the user's intent deterministically, a
click lands exactly one seek, and clear→load never crashes. The VM is deliberately WPF-free so all of
this transport/guard logic is unit-testable headlessly; only the thin FFME plumbing needs a desktop.

## Scope
**In:** `PlayerViewModel` transport + observable state + command guards; the scrub pop-back /
seek-target hold (T-033); live-scrub coalesce + throttle + dead-band (T-051); click-to-point seek
dedupe (T-075); Open/Unload state reset; volume/mute/speed; hover-thumbnail *wiring* from the player
VM (T-078); and the `FfmeMediaPlayer`/`MediaReopenGuard` Close→Open reopen safety + supersede/timeout
lifecycle (T-080), read through `IReopenTarget`.
**Out:** the thumbnail *rendering/debounce/latest-wins* internals (`ThumbnailPreviewViewModel` +
`IThumbnailService` — its own spec); the preview downscale filter + hardware-decode setup
(`PreviewScale` / `OnMediaOpening`, T-024 — its own spec); waveform, timeline markers, split-point
capture, and the Split/BulkCut screens that host the player; media probing/duration derivation
(`Core` MediaProbe specs).

## Current behavior & invariants

### Readiness & transport (`PlayerViewModel`)
- **I1** — `IsReady` is `true` iff `Duration` is known (non-null); the player's `DurationAvailable`
  event sets `Duration` (from `IMediaPlayer.Duration`), flips `IsReady`, and re-raises every
  jog/step/jump/play command's `CanExecuteChanged` (`OnDurationAvailable`, `Duration` setter, `IsReady`).
- **I2** — `PlayPause()` toggles transport: when paused it calls `IMediaPlayer.Play()` and sets
  `IsPlaying=true` (`PlayPauseLabel`→"Pause"); when playing it calls `Pause()` and sets
  `IsPlaying=false` (label→"Play") (`PlayPause`).
- **I3** — `PlayPause()` is a no-op and `PlayPauseCommand.CanExecute` is `false` while not ready
  (`PlayPause` early-return; command predicate `_ => IsReady`).
- **I4** — `Stop()` calls `IMediaPlayer.Stop()`, sets `IsPlaying=false`, clears the seek hold and
  live-scrub state, and resets `Position` to `TimeSpan.Zero` (`Stop`).
- **I5** — the player's `Ended` event sets `IsPlaying=false` (`OnEnded`).
- **I6** — the player's `Failed` event sets `PreviewFailed=true`, surfaces the reason in
  `PreviewFailedReason` (defaulting to "The video could not be played." when blank), sets
  `IsPlaying=false`, and never throws (`OnFailed`).
- **I7** — `Position` is clamped to `[0, Duration]` on every set (`Clamp` in the `Position` setter;
  below 0 → 0, above `Duration` → `Duration`).

### Seek seam (user- vs player-driven `Position`)
- **I8** — a user-driven `Position` set (suppression flag off) issues a seek via `BeginSeek`, while a
  player-driven set (routed through `SetPositionFromPlayer`, suppression flag on) updates the display
  but does **not** re-seek (`Position` setter `if (!_suppressSeek)`; `SetPositionFromPlayer`).
- **I9** — `PositionSeconds` two-way maps to `Position` (getter = `TotalSeconds`; setter =
  `Position = FromSeconds(value)`), so a bound slider drives the same seek seam.
- **I10** — `Scrub(t)` seeks to `Clamp(t)` via `BeginSeek` (used by the timeline and every
  skip/jump/frame jog).

### Skip / jump / frame-step
- **I11** — `SkipCommand`'s parameter is a **number of seconds** — a `double`, an `int`, or a string
  such as `"10"`/`"-5"` (invariant-culture); an unparseable parameter yields `0` (no-op)
  (`ParseSeconds`, `SkipCommand` ctor wiring).
- **I12** — `SkipBy(delta)` jogs relative to the current `Position`, clamped to `[0, Duration]`, then
  seeks there: forward advances, a backward delta past 0 clamps to 0, a forward delta past `Duration`
  clamps to `Duration` (`SkipBy` → `ClampTo` → `Scrub`).
- **I13** — `SkipBy` and `StepFrame` are method-level no-ops when not ready (early `if (!IsReady)
  return;`), so a direct call — not just a disabled button — cannot seek/step before duration is known.
- **I14** — `JumpToStartCommand` seeks to `TimeSpan.Zero`; `JumpToEndCommand`/`JumpToEnd()` seeks to
  the full `Duration` (`JumpToStartCommand = Scrub(Zero)`; `JumpToEnd`).
- **I15** — `StepForwardCommand` calls `IMediaPlayer.StepFrame(+1)` and `StepBackCommand`
  `StepFrame(-1)`, delegating the single-frame step to the player (`StepFrame`).
- **I16** — all jog/step/jump commands (`SkipCommand`, `JumpToStartCommand`, `JumpToEndCommand`,
  `StepForwardCommand`, `StepBackCommand`) have `CanExecute` gated on `IsReady` and re-raise when
  `Duration` becomes known (command predicates `_ => IsReady`; `Duration` setter `RaiseCanExecuteChanged`).

### Scrub pop-back guard / seek-target hold (T-033 — G-009 / G-016)
- **I17** — `BeginSeek(target)` pins the visible `Position` at the target immediately via a suppressed
  set (no re-seek), records `_seekTarget`, arms `_seeking`, then calls `IMediaPlayer.Seek(target)`, so
  the slider shows the requested position before the async seek lands (`BeginSeek`).
- **I18** — while the hold is armed, a `PositionChanged` echo whose distance from `_seekTarget` exceeds
  `SeekTolerance` (250 ms) is swallowed — the display stays pinned at the target, no pop-back
  (`OnPositionChanged` case (2), the `delta > SeekTolerance` branch).
- **I19** — an echo within `SeekTolerance` of the target counts as "the seek landed": the hold clears
  and that on-target update is applied normally (`OnPositionChanged`, `delta <= SeekTolerance`).
- **I20** — the player's `Seeked` event releases the hold **deterministically** and snaps the display
  to the player's settled position (a suppressed set, so no re-seek) (`OnSeeked` → `ClearSeekHold` +
  `SetPositionFromPlayer`).
- **I21** — anti-freeze backstop: if echoes never match the target and no `Seeked` arrives, after
  `MaxHeldTicks` (12) non-matching echoes the hold releases and live echoes resume, so the slider can
  never freeze permanently (`OnPositionChanged`, `++_heldTicks >= MaxHeldTicks`).
- **I22** — a player-driven echo never loops back into a `Seek`, even while the hold is active (every
  echo path applies via `SetPositionFromPlayer` under `_suppressSeek`).
- **I23** — while the user is actively dragging the thumb (`BeginUserScrub()`..`EndUserScrub()`),
  `PositionChanged` echoes are fully suppressed (the slider follows the drag, not playback), and the
  final exact seek fires on release (`OnPositionChanged` case (1) `_isUserScrubbing`; `EndUserScrub`).

### Live-scrub coalesce + throttle (T-051)
- **I24** — `ScrubPreview(t)` with no seek in flight issues the seek immediately (`ScrubPreview` →
  `IssueScrubSeek`).
- **I25** — while a seek is in flight, further previews are **coalesced**: only the newest target is
  stashed in `_pendingScrubTarget` (overwriting any earlier stash, no backlog), and when the in-flight
  seek settles (`OnSeeked`) exactly one follow-up seek to the newest pending target is issued —
  intermediate targets are dropped (`ScrubPreview` `_seekInFlight` branch; `OnSeeked` pending drain).
- **I26** — the pending target is cleared once issued, so no further seek fires without a new preview
  (`OnSeeked` sets `_pendingScrubTarget = null` before/after issuing).
- **I27** — dead-band: a target within `ScrubDeadBand` (5 ms) of the last-issued target is skipped as
  redundant (`ScrubPreview` `Within(target, _lastIssuedTarget, ScrubDeadBand)`).
- **I28** — throttle: a preview arriving less than `ScrubThrottle` (70 ms) after the last issued seek
  is stashed as pending rather than issued now (`ScrubPreview` `(_nowMs() - _lastIssueTicksMs) <
  ScrubThrottle`), on the injected `_nowMs` clock.
- **I29** — every issued live-scrub seek routes through the T-033 seek-target hold, so a stale echo
  before it lands does not pop the playhead off the pin (`IssueScrubSeek` → `BeginSeek`).
- **I30** — `EndUserScrub(finalSeconds)` drops any pending preview and issues the **final exact** seek
  to the released position unconditionally, bypassing the dead-band/throttle (`EndUserScrub` clears
  `_pendingScrubTarget`, then `IssueScrubSeek(Clamp(final))`).

### Click-to-point seek dedupe (T-075 — G-028, `IsMoveToPointEnabled`)
- **I31** — `BeginSeek` dedupe: with the slider's `IsMoveToPointEnabled`, one track click fires both a
  `Value`-change seek and a zero-distance thumb-drag release seek to the same point; a second
  `BeginSeek` to the same target within `SeekTolerance` while the hold is still armed (not yet released
  by `Seeked`) is skipped, so a click converges on exactly **one** seek (`BeginSeek` `if (_seeking &&
  Within(target, _seekTarget, SeekTolerance)) return;`).
- **I32** — the click still seeks to the point under either event shape: a value-change with no drag
  events seeks via the `Position` setter; a drag-release with no prior value change still seeks via
  `EndUserScrub` (neither the setter's `BeginSeek` nor `EndUserScrub` is gated on scrubbing state).
- **I33** — the click dedupe does not wedge the T-051 coalesce state: after the click's in-flight seek
  settles (`Seeked` → `_seekInFlight=false`), a later click still issues its own distinct seek.

### Open / Unload state reset
- **I34** — `Open(path)` resets preview state and loads the source: `PreviewFailed`/`PreviewFailedReason`
  cleared, `Duration=null` (→ not ready), `IsPlaying=false`, `Volume=1.0`/`IsMuted=false`/`SpeedRatio=1.0`
  (written through to the player), seek hold + live-scrub state cleared, `Position→0`, then
  `IMediaPlayer.Open(path)` (`Open`).
- **I35** — `Unload()` calls `IMediaPlayer.Unload()` and resets the same state as `Open` — banner
  cleared, `Duration=null` (→ `IsReady` false, re-raising the command guards), `IsPlaying=false`, audio
  /speed back to defaults, holds cleared, `Position→0` — and clears the hover thumbnail (`Unload`).

### Audio & speed
- **I36** — `Volume` is clamped to `[0, 1]` and written through to `IMediaPlayer.Volume`; it defaults
  to `1.0` (`Volume` setter).
- **I37** — `MuteCommand`/`ToggleMute()` flips `IsMuted` (writing `IMediaPlayer.IsMuted`) but leaves
  the slider `Volume` value untouched, so an unmute restores the exact prior level with no separate
  "restore" (`IsMuted` setter; `Volume` unchanged across the cycle).
- **I38** — `SpeedRatio` is written through to `IMediaPlayer.SpeedRatio` and updates `SpeedText`;
  `SpeedPresets` is exactly `{0.25, 0.5, 1.0, 1.5, 2.0}` (`SpeedRatio` setter; `SpeedPresets`).

### Hover-thumbnail wiring (T-078 — G-030)
- **I39** — `Open` forwards `(path, current duration)` to `Thumbnail.SetInput`; `OnDurationAvailable`
  forwards the now-known duration to `Thumbnail.SetDuration`; `Unload` calls `Thumbnail.Clear()`
  (sweeping the temp cache + hiding the popup) (`Open`, `OnDurationAvailable`, `Unload`).

### Crash-safe Close→Open reopen (T-080 — G-031; `FfmeMediaPlayer` + `MediaReopenGuard`)
- **I40** — `FfmeMediaPlayer.Open` registers a lifecycle generation (`MediaReopenGuard.RequestOpen`)
  and defers the actual `_element.Open(...)` until `WaitUntilReopenableAsync` reports the element has
  left every transitional state (`!IsClosing && !IsOpening && !IsChanging`), fixing the Open-while-closing
  native AccessViolation (`Open` → `OpenWhenSettledAsync`; `IReopenTarget.IsReopenable`;
  `WaitUntilReopenableAsync`).
- **I41** — a settled (already reopenable) element opens immediately with no settle-poll
  (`WaitUntilReopenableAsync` returns `Open` on the first check when `IsReopenable`).
- **I42** — a newer `Open` or `Unload` arriving while an open is waiting **supersedes** it: `RequestOpen`
  and `NotifySuperseded` each bump the generation, so the stale wait returns `ReopenDecision.Superseded`
  and drops without opening against a swapped/closing element (`RequestOpen`/`NotifySuperseded`;
  `WaitUntilReopenableAsync` generation re-check; `FfmeMediaPlayer.Unload`/`Detach` call `NotifySuperseded`).
- **I43** — a wedged element that never settles times out at `DefaultSettleTimeout` (5 s) returning
  `ReopenDecision.Timeout` — never an infinite wait or crash; `FfmeMediaPlayer` maps `Timeout` to a
  recoverable `Failed` ("The previous video is still closing — please try loading again.")
  (`WaitUntilReopenableAsync` deadline; `OpenWhenSettledAsync` Timeout case).
- **I44** — a detached target (no element attached, `IsDetached`) drops the wait as `Superseded` — the
  guard stops waiting on nothing to open (`WaitUntilReopenableAsync` `_target.IsDetached`).
- **I45** — a fault reading the element's transitional state is treated as "still transitional" (keep
  polling), never surfaced — a torn-down element cannot crash the load (`SafeIsReopenable` swallow →
  keep waiting).
- **I46** — repeated split→clear→load cycles stay stable: each open waits out its own close and the
  lifecycle generation converges monotonically (`RequestOpen`/`NotifySuperseded` via `Interlocked`;
  guard settle loop across cycles).

### FFME transport contract (`FfmeMediaPlayer`, WPF-bound)
- **I47** — `IMediaPlayer.Seek` completion surfaces `PositionChanged` **then** `Seeked` so the VM can
  release its T-033 hold deterministically (`FfmeMediaPlayer.Seek` `Run(seek, onSuccess = raise
  PositionChanged + Seeked)`; contract declared on `IMediaPlayer.Seeked`).
- **I48** — `FfmeMediaPlayer.StepFrame` is a paused operation: if playing it pauses first before the
  single-frame `StepForward()`/`StepBackward()`, so the frame lands stable rather than fighting the play
  loop (`StepFrame` `if (_isPlaying) { ... Pause() }`). *(WPF/MediaElement-bound — not headlessly
  unit-testable; verified live via app-run.)*
- **I49** — a path is turned into a media address by `MediaSourceUri.TryCreate`, which **decides rather
  than throws** (T-131). It answers true for every shape the player could already open — a local path, a
  mapped drive letter, and a UNC path whose server name is a legal URI host (plain, dotted, dashed,
  underscored, or an IP) — and false, with no exception, for a UNC path whose server name cannot be a URI
  authority. The realistic case is a **space in the server name** (`\\Seagate NAS\...`), common on
  consumer NAS boxes; `\\host:port\...` and `\\host[1]\...` fail the same way. A blank path answers false.
- **I50** — a refused path yields `MediaSourceUri.ExplainRefusal`, never .NET's wording. The message
  **names the share**, states that **cutting still works** (type the times into IN/OUT — the engine passes
  raw paths to ffmpeg as process arguments and never builds a `Uri`), and names the **mapped-drive-letter**
  workaround, which genuinely restores the preview. It never contains "Invalid URI" or "hostname could not
  be parsed" — replacing exactly that string is the point.
- **I51** — the refusal is recorded: `FfmeMediaPlayer` best-effort writes the offending path through
  `ErrorLogWriter` (`preview-open-refused`) before raising the failure, and a logging failure never turns
  a handled refusal into a crash. The original defect logged nothing, which is why diagnosing it required
  reproducing the path shape from scratch.
- **I52** — the refusal does NOT widen `CanSetCutAtPlayhead`. With no video loaded there is no playhead,
  so the set-at-playhead gestures stay correctly disabled; the fix is to explain the failure, not to
  pretend the player is ready. *(Consequence: on such a share, cuts are placed by typing times.)*

## Links
- Design: — (no D-NNN; grounded directly in the cited src, tasks T-012/T-024/T-028/T-029/T-033/T-047/T-051/T-075/T-078/T-080)
- Goals: G-009, G-016 (scrub pop-back), G-028 (click-to-point seek), G-030 (hover thumbnail), G-031 (crash-safe reopen),
  G-045 (network shares whose name has a space; tasks T-131/T-132)
- Related specs: SPEC (thumbnail preview — `ThumbnailPreviewViewModel`), SPEC (preview downscale/hw-decode — `PreviewScale`/T-024)
- Key code: `src/App/ViewModels/PlayerViewModel.cs`, `src/App/Media/FfmeMediaPlayer.cs`,
  `src/App/Media/MediaReopenGuard.cs`, `src/App/Media/IMediaPlayer.cs`
- Tests: `tests/App.Tests/PlayerViewModelTests.cs`, `tests/App.Tests/MediaReopenGuardTests.cs`,
  `tests/App.Tests/MediaSourceUriTests.cs` (I49-I52)
