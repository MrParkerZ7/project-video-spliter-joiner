---
id: SPEC-013
slug: preview-player
area: app
title: Preview player (transport, seek, reopen safety)
status: current
sources:
  - src/App/ViewModels/PlayerViewModel.cs
  - src/App/ViewModels/ThumbnailPreviewViewModel.cs
  - src/App/ViewModels/NullThumbnailService.cs
  - src/App/Media/FfmeMediaPlayer.cs
  - src/App/Media/MediaReopenGuard.cs
  - src/App/Media/IMediaPlayer.cs
  - src/App/Media/MediaSourceUri.cs
  - src/App/Media/FileMediaInputStream.cs
  - src/App/Media/PreviewScale.cs
serves-goal: [G-005, G-009, G-016, G-028, G-030, G-031, G-045]
updated: 2026-09-12
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
VM **and the hover-preview VM itself** — debounce, latest-wins coalesce, capture width, result marshal,
visibility, and cache sweep (`ThumbnailPreviewViewModel`, T-078, I71–I101); and the
`FfmeMediaPlayer`/`MediaReopenGuard` Close→Open reopen safety + supersede/timeout
lifecycle (T-080), read through `IReopenTarget`; and how a path becomes something the player can open —
`MediaSourceUri` and the `FileMediaInputStream` fallback (T-131/T-132, I49–I52); and the preview
**downscale + hardware-decode setup** applied at open time — the pure size math and the FFME
`MediaOptions` hook that installs it (`PreviewScale` / `FfmeMediaPlayer.OnMediaOpening`, T-024, I53–I70).
**Out:** the Core frame extraction the hover preview calls (`IThumbnailService` /
`FfmpegThumbnailService` — SPEC-005); the WPF plumbing that feeds the preview VM (`PlayerView.xaml.cs`
cursor-X→time mapping + popup placement; `BulkRowScrubView`'s own preview instance); FFME's own
rendering/stretch behavior and the ffmpeg `scale` filter's internals; waveform, timeline markers, split-point
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
- **I50** — a refused path is **opened as a stream instead** (T-132): `FfmeMediaPlayer.TryOpenAsStream` hands
  FFME a `FileMediaInputStream` (FFME's `Open(IMediaInputStream)` entry point, so the path never has to be a
  `Uri`), opened share-`ReadWrite` so a preview is never the reason a cut is refused, and held so `Unload`
  releases the file handle. Only when the `FileStream` itself cannot be opened (missing, locked, unreachable)
  is the refusal logged and explained as `MediaSourceUri.ExplainRefusal`, never in .NET's wording. A stream
  that opens but that FFME then cannot play fails through the ordinary `MediaFailed` path, like any other
  file. The refusal message
  **names the share**, states that **cutting still works** (type the times into IN/OUT — the engine passes
  raw paths to ffmpeg as process arguments and never builds a `Uri`), and names the **mapped-drive-letter**
  workaround, which genuinely restores the preview. It never contains "Invalid URI" or "hostname could not
  be parsed" — replacing exactly that string is the point.
- **I51** — a refusal the stream fallback could not rescue is recorded: `FfmeMediaPlayer` best-effort writes the offending path through
  `ErrorLogWriter` (`preview-open-refused`) before raising the failure, and a logging failure never turns
  a handled refusal into a crash. The original defect logged nothing, which is why diagnosing it required
  reproducing the path shape from scratch.
- **I52** — the refusal does NOT widen `CanSetCutAtPlayhead`. With no video loaded there is no playhead,
  so the set-at-playhead gestures stay correctly disabled; the fix is to explain the failure, not to
  pretend the player is ready. *(Consequence: when the stream fallback of I50 also fails, cuts are placed by
  typing times.)*

### Preview downscale + hardware decode (T-024 — G-005; `PreviewScale` + `FfmeMediaPlayer.OnMediaOpening`)
The preview's decode/render resolution is capped so a 4K source cannot saturate the WPF UI thread.
`PreviewScale` is pure geometry (no I/O, no FFME types) and is unit-tested headlessly; its call site
`FfmeMediaPlayer.OnMediaOpening` is WPF/FFME-bound and — like the rest of that class — is **not**
unit-tested (it only has to compile; behavior verified live via `app-run`), so every call-site
invariant below is marked *(not asserted)*.

- **I53** — the preview target height never exceeds the cap: for a source taller than
  `maxPreviewHeight`, `ComputeTarget` returns exactly `maxPreviewHeight` (even-rounded) as the height —
  except the ≥2 floor of I59 at a cap below 2 — 3840×2160 @1080 → 1920×1080, @720 → 1280×720
  (`PreviewScale.ComputeTarget`, downscale branch).
- **I54** — the production cap is 1080: `FfmeMediaPlayer` passes its private const
  `MaxPreviewHeight = 1080` on every open, so a 4K source is previewed at ~1080p
  (`FfmeMediaPlayer.MaxPreviewHeight`, `OnMediaOpening`). *(not asserted — no test constructs `FfmeMediaPlayer`.)*
- **I55** — aspect ratio is preserved on downscale: width = `Round(sourceWidth × maxPreviewHeight /
  sourceHeight)`, then even-rounded — 4096×2160 @1080 → 2048×1080 (DCI 4K), 2560×1440 @1080 → 1920×1080
  (`ComputeTarget` scale factor).
- **I56** — never upscale: a source whose height is at or below the cap is previewed at its own
  resolution, even-rounded down (I57), and is never enlarged — 1280×720 @1080 → 1280×720; exactly at the
  cap, 1920×1080 @1080 → unchanged (`ComputeTarget` `sourceHeight <= maxPreviewHeight` branch).
- **I57** — both returned dimensions are even, rounded **down** on an odd value
  (`MakeEven(v) => v - v % 2`), because the yuv420p pixel format most H.264/HEVC sources use requires an
  even width and height (an odd dimension makes the ffmpeg `scale` filter fail). This holds on both
  **scaling** branches (the non-positive guard of I60 returns verbatim), and applies to the cap itself
  (a cap of 1081 yields height 1080) — 1921×1081 @1080 → both even; 1281×721 @1080 → 1280×720
  (`ComputeTarget` + `MakeEven`). *(the odd-cap clause is not asserted — every cap in the suite is even.)*
- **I58** — even-rounding alone can trigger a filter: an **under-cap** odd source computes to a strictly
  smaller even target, so `ShouldDownscale` is true and `BuildScaleFilter` emits a 1-px scale
  (1281×721 @1080 → `scale=1280:720`). "Never upscale" bounds the target; it does not promise "no filter"
  (`ShouldDownscale` compares the even target against the raw source). *(not asserted — the tests cover
  `ComputeTarget(1281, 721, 1080)` but not `ShouldDownscale`/`BuildScaleFilter` on that input.)*
- **I59** — the downscale branch floors each dimension at 2, so rounding can never yield a 0-sized
  target (`ComputeTarget` `if (targetWidth < 2) targetWidth = 2;`, same for height). *(not asserted; the
  floor exists only on the downscale branch — an under-cap source with a 1-px dimension even-rounds to 0,
  a shape no real video has.)*
- **I60** — unknown/garbage dimensions are returned verbatim and never scaled: if `sourceWidth`,
  `sourceHeight` **or** `maxPreviewHeight` is non-positive, `ComputeTarget` returns
  `(sourceWidth, sourceHeight)` unchanged — no even-rounding, no clamp, no throw (`ComputeTarget` guard).
- **I61** — `ShouldDownscale` is true iff the computed target is strictly smaller than the source in
  either dimension: true for 3840×2160 @1080, false for 1280×720 @1080 (under cap) and 1920×1080 @1080
  (at cap) (`PreviewScale.ShouldDownscale`).
- **I62** — `ShouldDownscale` is false for any non-positive input (e.g. 0×0 @1080) (`ShouldDownscale`
  guard — it returns before consulting `ComputeTarget`, but that short-circuit is behaviourally
  indistinguishable: `ComputeTarget` returns the source verbatim on the same inputs, so no test can
  separate the two).
- **I63** — `BuildScaleFilter` emits exactly `scale=<width>:<height>` from the `ComputeTarget`
  dimensions — `scale=1920:1080` at a 1080 cap, `scale=1280:720` at a 720 cap
  (`PreviewScale.BuildScaleFilter`).
- **I64** — `BuildScaleFilter` returns `null` whenever `ShouldDownscale` is false (source under the cap
  **with even dimensions**, at the cap, or dimensions unknown — an odd under-cap source is the I58
  exception), and the caller then installs **no** filter at all, letting FFME decode natively
  (`BuildScaleFilter` early return; `OnMediaOpening` `if (filter is not null)`). *(the `null` return is
  asserted; the caller-side half is not.)*
- **I65** — the filter is installed only when a video stream was probed **and** `e.Options.VideoFilter`
  is still blank, so the hook never overwrites a filter already set; the dimensions fed to
  `BuildScaleFilter` are the probed stream's `PixelWidth`/`PixelHeight`
  (`FfmeMediaPlayer.OnMediaOpening`, step 2). *(not asserted.)*
- **I66** — the video stream is the first probed stream whose `CodecTypeName` equals `"video"`
  (ordinal, case-insensitive), or `null` when there is none; an audio-only input therefore gets neither
  hardware-decode setup nor a scale filter, since both steps are conditioned on it
  (`FfmeMediaPlayer.FindVideoStream`). *(not asserted.)*
- **I67** — hardware decoding is opt-in from the probe: `e.Options.VideoHardwareDevices` is set to the
  probed stream's `HardwareDevices` array only when that list is non-empty; an absent or empty list is
  left untouched, i.e. software decode (`FfmeMediaPlayer.OnMediaOpening`, step 1). *(not asserted.)*
- **I68** — the two steps are independently best-effort: hardware setup and filter build sit in separate
  `try`/`catch (Exception)` blocks that write to `Debug` and swallow, so a HW-init failure still lets the
  downscale filter apply (and vice versa) and neither can fail the open
  (`FfmeMediaPlayer.OnMediaOpening`). *(not asserted — T-024's "HW unavailable → SW + downscale fallback"
  criterion was verified live via `app-run`.)*
- **I69** — the hook is bound to exactly the attached element: `Attach` subscribes
  `MediaOpening += OnMediaOpening` and `Detach` unsubscribes it before a swap, so a replaced element is
  never configured twice (`FfmeMediaPlayer.Attach`/`Detach`). *(not asserted.)*
- **I70** — the downscale is preview-only and can never change a produced file: `PreviewScale`'s output
  reaches nothing but FFME's `MediaOptions.VideoFilter` (`OnMediaOpening` is its only call site in
  `src/`), and the split/join command lines are pure stream-copy — `-vf` is in
  `SplitArgsBuilder.ForbiddenEncoderTokens` / `JoinArgsBuilder.ForbiddenEncoderTokens` and the engine
  asserts `SatisfiesCopyInvariant` on every command before launching, so the cut always runs at the
  source's full resolution. *(the only-call-site half is a static fact, not asserted; the copy-invariant
  half is SPEC-001 I20/I22 + SPEC-003 I21/I23, asserted by
  `tests/Core.Tests/SplitArgsInvariantTests.cs:34-39,54-59,74-79` and `JoinArgsInvariantTests.cs:28-33`.)*

### Hover-thumbnail preview VM (T-078 — G-030; `ThumbnailPreviewViewModel`)
The internals behind I39's wiring. `ThumbnailPreviewViewModel` is WPF-free: the view feeds it hover
samples (`PlayerView.xaml.cs` `OnScrubMouseMove`; `BulkRowScrubView` builds its own instance) and it
debounces + coalesces them, calls the Core `IThumbnailService` (SPEC-005) off the UI thread, and exposes
only primitives the popup binds — `HoverThumbnailPath` (a temp **jpg path**, never an image object),
`HoverTimeText`, `HasThumbnail`, `IsThumbnailVisible`, `HoverOffsetX`. `InFlightGrab` is an internal
await-seam for tests (T-137); no production code reads it.

- **I71** — `UpdateHover(time, offsetX)` sets `HoverTime` (re-raising `HoverTimeText`) and `HoverOffsetX`
  **before** any file/grab check, so the label and popup placement follow the cursor with no file loaded
  and before any frame resolves (`UpdateHover`, first two statements). *(the values are asserted, with and
  without a loaded file; the `HoverTimeText` re-raise is not — no test subscribes to `PropertyChanged`.)*
- **I72** — with no input path set, `UpdateHover` returns after updating the label and never calls the
  service (`UpdateHover` `if (_inputPath is null) return;`).
- **I73** — `HoverTimeText` is zero-padded `mm:ss` in invariant culture under one hour — 65 s → `01:05`
  (`FormatClock`). *(the `mm:ss` shape is asserted; the invariant-culture pinning is not — the suite runs
  in one culture.)*
- **I74** — at or past one hour `HoverTimeText` is `h:mm:ss` (unpadded hours), and a negative time is
  formatted as its magnitude (`FormatClock`, `t.Negate()`). *(not asserted.)*
- **I75** — every request waits the debounce window **before** touching the service: `GrabAsync` awaits
  `_delay(_debounce, ct)` and only then calls `IThumbnailService.GetThumbnailAsync`, so a hover superseded
  inside the window never reaches ffmpeg — three hovers inside one window produce exactly **one** service
  call (`GrabAsync`).
- **I76** — the production debounce is `DefaultDebounce` = **60 ms** and the production wait is
  `Task.Delay`; the testable ctor's `debounce` + `delay` parameters are the only seam that changes either
  (`ThumbnailPreviewViewModel(IThumbnailService)` → `: this(thumbnails, DefaultDebounce, (d, ct) =>
  Task.Delay(d, ct))`). *(the 60 ms value itself is not asserted — the suite constructs with its own 60 ms
  and a gated delay.)*
- **I77** — a non-positive `debounce` argument falls back to `DefaultDebounce`
  (`_debounce = debounce > TimeSpan.Zero ? debounce : DefaultDebounce`). *(not asserted.)*
- **I78** — the constructor rejects a null `thumbnails` or a null `delay` with `ArgumentNullException`
  (ctor guards). *(not asserted.)*
- **I79** — the grab is fire-and-forget: `UpdateHover` starts `GrabAsync` without awaiting it
  (`_inFlight = GrabAsync(...)`), so a hover sample never blocks its caller and a second hover can arrive
  while the first is still parked in the debounce window (`UpdateHover`).
- **I80** — cancel-prior: each `UpdateHover` on a loaded file cancels the previous request's
  `CancellationTokenSource` before creating its own, so at most one request is live and the superseded
  one's debounce wait faults instead of proceeding to a grab (`UpdateHover` → `CancelInFlight`; the new
  CTS stored in `_requestCts`).
- **I81** — latest-wins by request id: every request is stamped `id = ++_requestId` and `ApplyResult`
  commits only when `result.Id == _requestId`, so a superseded grab that completed anyway can never
  clobber a newer result (`UpdateHover`; `ApplyResult` `result.Id != _requestId`). *(the id check is the
  belt to I80's braces and is not directly asserted — the suite's superseded requests are cancelled inside
  the debounce window, so they never produce a result to drop.)*
- **I82** — after N rapid hovers the committed frame is the **last** hovered time's (10 s / 40 s / 80 s →
  `frame-80.jpg`) (`ApplyResult` under I80 + I81).
- **I83** — every grab requests a capture width of `ThumbnailWidth` = **160** px (`GrabAsync` →
  `GetThumbnailAsync(inputPath, time, ThumbnailWidth, ct)`), the same 160 the popup box is fixed at
  (`PlayerView.xaml` `StackPanel Width="160"`, `PlayerView.xaml.cs` `HoverPopupWidth`). *(not asserted —
  the fake service records the requested time and token, not the width.)*
- **I84** — the hovered time is handed to the service **unrounded**; a settled hover at t commits the path
  the service returned for exactly that t, and any bucketing is the Core service's (SPEC-005 I3)
  (`GrabAsync` passes `time` through). *(the pass-through is asserted; the **unrounded** clause is not —
  every hover time in the suite is a whole second, so a 1 s floor inside the VM would pass too.)*
- **I85** — the resolved path is committed through `IProgress<PathResult>` — a `Progress<PathResult>`
  built in the ctor, so it posts to the `SynchronizationContext` captured **at construction** (the WPF
  dispatcher in the app), never on the grab's continuation thread (`_postResult`; both awaits are
  `ConfigureAwait(false)`). *(not asserted — the `PumpContext` harness accommodates the post but asserts
  nothing about it: `Settle` awaits `InFlightGrab` before draining and `PumpSettled` discards the drained
  count, so committing the path directly from `GrabAsync` would pass every test.)*
- **I86** — a request whose token was cancelled while the service call was in flight returns **without
  reporting**, so it cannot even reach `ApplyResult` (`GrabAsync`
  `if (cts.Token.IsCancellationRequested) return;`). *(not asserted — the suite's cancellations trip the
  debounce wait first.)*
- **I87** — a result arriving after the cursor left is dropped: `ApplyResult` returns without committing
  when `_isHovering` is false, so a late frame can never re-show the popup (`ApplyResult` `|| !_isHovering`).
  *(not asserted — `ResultAfterLeave_IsDropped` leaves BEFORE releasing the debounce gate, so
  `MouseLeave`'s `CancelInFlight` faults the wait and no result is ever produced to drop; the
  `!_isHovering` branch is unreached, exactly like I81/I86. It is the belt to I95's cancel: removing
  both would break that test, removing either alone breaks nothing.)*
- **I88** — a failed or absent frame shows nothing: a `null`/empty path from the service leaves
  `HoverThumbnailPath` null rather than a stale image (`ApplyResult`
  `string.IsNullOrEmpty(result.Path) ? null : result.Path`). *(the `null` case is asserted; the
  empty-string case is not — the fake only ever returns `null`.)*
- **I89** — `HasThumbnail` is derived from the path (`!string.IsNullOrEmpty(HoverThumbnailPath)`) and is
  re-raised on every path change, so the popup's image box appears only when a frame exists
  (`HoverThumbnailPath` setter → `OnPropertyChanged(nameof(HasThumbnail))`). *(the derived value is
  asserted; the re-raise is not — no test subscribes to `PropertyChanged`.)*
- **I90** — best-effort: `GrabAsync` swallows `OperationCanceledException` **and** every other exception,
  so a throwing service never faults the fire-and-forget task and never leaves a stuck popup — the
  displayed frame simply does not change (`GrabAsync` catch + catch-all). *(not asserted — the fake
  service never throws.)*
- **I91** — CTS bookkeeping never crosses requests: `GrabAsync`'s `finally` retires `_requestCts` only
  when it is still reference-equal to its own CTS (an older grab's teardown never drops a newer hover's
  CTS), and `CancelInFlight` swallows `ObjectDisposedException` from an already-retired one
  (`GrabAsync` finally `ReferenceEquals`; `CancelInFlight` catch). *(not asserted.)*
- **I92** — `IsThumbnailVisible` (the popup's `IsOpen`) is computed: true iff the cursor is over the bar
  **and** an input path is set **and** a duration greater than `TimeSpan.Zero` is known
  (`IsThumbnailVisible` getter). *(the duration-must-exceed-zero clause is not asserted; the
  null-duration clause is.)*
- **I93** — `SetDuration(d)` records the now-known duration and re-raises `IsThumbnailVisible` without
  touching the input path, the hover state, or any in-flight grab, so a popup suppressed only for want of
  a duration becomes showable when the player learns it (`SetDuration`). *(the recorded duration is
  asserted — `IsThumbnailVisible` flips true afterwards, which also shows the input path survived; the
  `PropertyChanged` re-raise and the untouched hover state / in-flight grab are not.)*
- **I94** — a hover sample on a loaded file also shows the popup (`UpdateHover` sets
  `IsThumbnailVisible = true`), so `MouseEnter` is not a precondition for the preview appearing.
  *(not asserted.)*
- **I95** — `MouseLeave()` hides the popup, cancels any in-flight grab, and clears `HoverThumbnailPath`
  (`MouseLeave`). *(the hide + frame-drop are asserted; the in-flight cancel is not —
  `MouseLeave_HidesPopupAndDropsFrame` settles the grab before leaving, and `ResultAfterLeave_IsDropped`
  passes on either this cancel or I87's `_isHovering` check.)*
- **I96** — `MouseLeave()` does **not** sweep the service cache — it makes no `IThumbnailService.Clear`
  call — so frames already extracted for the still-loaded file are reused on the next hover (`MouseLeave`
  has no `SweepPrevious`). *(not asserted.)*
- **I97** — `SetInput(path, duration)` sweeps the **outgoing** file's temp frames first and only then
  adopts the new input (`SetInput` → `SweepPrevious()` → `IThumbnailService.Clear(previous)`, before
  `_inputPath` is reassigned), so a new load never leaks the previous file's cache; a first `SetInput`
  (no previous input) sweeps nothing. *(the sweep of the outgoing file is asserted; the first-`SetInput`
  clause is not — the test asserts `Contain`, never a sweep count.)*
- **I98** — `SetInput` also resets the hover surface: any in-flight grab is cancelled,
  `HoverThumbnailPath` is cleared, and the popup is hidden (`SetInput` → `CancelInFlight`,
  `HoverThumbnailPath = null`, `IsThumbnailVisible = false`). *(not asserted — only the I97 sweep is.)*
- **I99** — a null/whitespace `inputPath` given to `SetInput` stores **no** input
  (`string.IsNullOrWhiteSpace(inputPath) ? null : inputPath`), so later hovers fall into I72's no-grab
  path. *(not asserted.)*
- **I100** — `Clear()` sweeps the **current** input's temp frames, drops the input and duration, cancels
  any in-flight grab, clears the frame, and hides the popup (`Clear`). *(the sweep, the frame-clear and
  the hide are asserted; dropping the input/duration and cancelling an in-flight grab are not. Reached
  from the player by `Unload` — I39.)*
- **I101** — with no `IThumbnailService` supplied, `PlayerViewModel` gives its `Thumbnail` the inert
  `NullThumbnailService.Instance`, whose grabs resolve to `null` and whose clears do nothing — the hover
  machinery stays live-but-empty instead of needing null checks inside the preview VM (`PlayerViewModel`
  ctor `thumbnails ?? NullThumbnailService.Instance`; `NullThumbnailService`). *(not asserted.)*

## Links
- Design: — (no D-NNN; grounded directly in the cited src, tasks T-012/T-024/T-028/T-029/T-033/T-047/T-051/T-075/T-078/T-080)
- Goals: G-005 (smooth 4K preview — the downscale/HW-decode of I53–I70), G-009, G-016 (scrub pop-back),
  G-028 (click-to-point seek), G-030 (hover thumbnail), G-031 (crash-safe reopen),
  G-045 (network shares whose name has a space; tasks T-131/T-132)
- Related specs: SPEC-005 (the `IThumbnailService` the hover preview calls — the calling
  `ThumbnailPreviewViewModel` is specified here, I71–I101); the preview downscale/hw-decode
  (`PreviewScale` / `OnMediaOpening`, T-024) is specified **here**, I53–I70; SPEC-001 (I20/I22) and
  SPEC-003 (I21/I23) own the stream-copy invariant I70 leans on — the copy rule lives there, not here
- Key code: `src/App/ViewModels/PlayerViewModel.cs`, `src/App/ViewModels/ThumbnailPreviewViewModel.cs`,
  `src/App/ViewModels/NullThumbnailService.cs`, `src/App/Media/FfmeMediaPlayer.cs`,
  `src/App/Media/MediaReopenGuard.cs`, `src/App/Media/IMediaPlayer.cs`, `src/App/Media/MediaSourceUri.cs`,
  `src/App/Media/FileMediaInputStream.cs`, `src/App/Media/PreviewScale.cs`
- Tests: `tests/App.Tests/PlayerViewModelTests.cs`, `tests/App.Tests/MediaReopenGuardTests.cs`,
  `tests/App.Tests/MediaSourceUriTests.cs` (I49, and the `ExplainRefusal` wording of I50),
  `tests/App.Tests/FileMediaInputStreamTests.cs` (the stream adapter behind I50 — share-ReadWrite, EOF,
  synthetic `StreamUri`),
  `tests/App.Tests/ThumbnailPreviewViewModelTests.cs` (the hover preview VM — I71–I73, I75, I79–I82, I84,
  I88, I89, I92, I93, I95, I97, I100, several of them partially — see each row's inline marker; over a
  fake `IThumbnailService`, a gated debounce seam and a pumpable `SynchronizationContext`),
  `tests/App.Tests/PreviewScaleTests.cs` (the pure downscale geometry — I53, I55–I57, I60–I64; the
  call-site invariants I54, I65–I69 are WPF/FFME-bound and have no test, and I58/I59 are documented
  edges the suite does not reach)
