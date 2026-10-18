# ADR 0005: Async-seek state machine — hold, coalesce-throttle, click dedupe

## Status

Accepted.

## Context

The preview player decodes through native FFME (ADR 0004). FFME's transport
methods are **asynchronous**: `FfmeMediaPlayer.Seek(t)` fire-and-forgets
`_element.Seek(clamped)` via `Run(...)`, and only on the awaitable's completion
does it raise `PositionChanged` **and** `Seeked`
(`src/App/Media/FfmeMediaPlayer.cs`, lines ~201–217). Meanwhile FFME keeps
raising `PositionChanged` natively (~every 200 ms). This async gap is the source
of three distinct defects the VM has to absorb, all in `PlayerViewModel`
(`src/App/ViewModels/PlayerViewModel.cs`) — the most intricate VM in the app:

- **Stale-echo pop-back (T-033).** A seek to `T` is dispatched, but playback
  ticks keep arriving with *pre-seek* positions before the seek lands. Bound to
  the two-way `Position` slider, those stale echoes yank the playhead **off** `T`
  — the slider visibly jumps back.
- **Live-scrub seek pile-up (T-051).** `Thumb.DragDelta` fires continuously
  (`OnScrubDragDelta` → `ScrubPreview`). Issuing one FFME seek per delta on a fast
  drag backs up a queue of async seeks the player crawls through, lagging the pin.
- **Click double-seek (T-075).** `IsMoveToPointEnabled=True` on the scrub
  `Slider` (`src/App/Views/PlayerView.xaml`, line 68) makes a **track click** jump
  the thumb to the click point. WPF delivers that as *both* a `Value` change (→
  `Position` setter → `BeginSeek`) **and** a zero-distance thumb drag whose
  `DragCompleted` (`EndUserScrub`) issues a *second* `BeginSeek` to the same
  point — one click, two seeks.

The base scrub seam already existed: the `Position` setter seeks on user-driven
sets but is suppressed (`_suppressSeek`) for player-driven sets so a playback tick
never re-seeks (`SetPositionFromPlayer`). That alone does not solve async ordering
— the three guards below layer a small state machine on top of it.

## Decision

Encode a single seek state machine in `PlayerViewModel`, funnelling **every**
seek (slider, jog, jump, frame-step, live-scrub, click) through one private
`BeginSeek(target)` path, plus a coalesce/throttle front-end for live scrub.

- **Seek-target hold (T-033).** `BeginSeek` pins the display at `target` (a
  suppressed set), records `_seekTarget`, sets `_seeking`, and calls the async
  `Seek`. While `_seeking`, `OnPositionChanged` ignores any echo farther than
  `SeekTolerance` (250 ms) from the target, keeping the playhead pinned. The hold
  is released **deterministically** by the player's `Seeked` event
  (`OnSeeked`), with two backstops so it can never freeze: a tolerance match (an
  echo lands within 250 ms) or a bounded `MaxHeldTicks` (12) non-matching echoes.
  During an active thumb drag `_isUserScrubbing` short-circuits echoes entirely —
  the slider owns the display.
- **Coalesce + throttle (T-051).** Only **one** live-scrub seek is ever in flight
  (`_seekInFlight`). A preview arriving while one runs is stashed in
  `_pendingScrubTarget`, overwriting any earlier stash (intermediate targets are
  dropped — no backlog). On `OnSeeked` the newest stash is issued, so the player
  **converges on the latest pin**. Issued seeks are additionally throttled to one
  per `ScrubThrottle` (70 ms) window, and a `ScrubDeadBand` (5 ms) skips a target
  ≈ the last issued. The final exact seek always fires unthrottled on release
  (`EndUserScrub` clears the pending stash and calls `IssueScrubSeek`).
- **Click dedupe (T-075).** `BeginSeek` drops a target that duplicates the
  in-flight seek — `_seeking && Within(target, _seekTarget, SeekTolerance)`. The
  click's second (drag-completed) seek chasing its first (Value-change) seek is
  collapsed, so a click converges on exactly **one** seek. A *distinct* target
  (a real drag) is never suppressed — it is gated only by the T-051 dead-band.
- **XAML–VM coupling.** `IsMoveToPointEnabled=True` is the XAML half of T-075: it
  buys click-to-seek but *causes* the double-seek, which the VM dedupe cancels —
  the two are one decision. The `Thumb.DragStarted/Delta/Completed` handlers in
  `PlayerView.xaml.cs` are thin adapters to `BeginUserScrub` / `ScrubPreview` /
  `EndUserScrub`; the hover `MouseMove` handler is deliberately **passive** (never
  sets `e.Handled`, never seeks) so it can't perturb the state machine.

## Consequences

**Positive**

- The playhead no longer pops back on seek; fast scrubs track the pin without
  lag; a track click produces one clean seek. All three async defects are absorbed
  in the VM, behind `IMediaPlayer`, with **no WPF types** in the VM.
- `Seeked`-driven release makes the common path deterministic rather than
  tolerance-guessed; the tolerance/tick backstops only guard the degenerate case.
- Fully unit-testable: an injectable `Func<long> nowMs` clock makes throttle
  behavior deterministic with a fake player and no wall-clock waits.

**Negative**

- Real intricacy — five interacting flags (`_seeking`, `_seekInFlight`,
  `_pendingScrubTarget`, `_isUserScrubbing`, `_suppressSeek`) plus four tuned
  constants (250 ms / 12 ticks / 70 ms / 5 ms). Changes here are subtle and easy
  to regress; the guards must be reset on every `Open`/`Unload`/`Stop`
  (`ClearSeekHold` + `ResetScrubState`).
- The constants are empirical to FFME's echo cadence; a different decoder or tick
  rate could require re-tuning `SeekTolerance` / `ScrubThrottle`.
- T-075 dedupe is intentionally coupled to `IsMoveToPointEnabled` — flipping that
  XAML flag off would strand the dedupe branch as dead code.

**Forced follow-ons**

- `FfmeMediaPlayer.Seek` **must** raise `Seeked` on async completion; the whole
  hold-release contract depends on it (`IMediaPlayer.Seeked`). A decoder swap
  (see ADR 0004's seam) must honour the same event.
- Keep the passive-hover invariant: any future scrub-bar handler must not set
  `e.Handled` or seek, or it will fight the state machine.
- The scrub-bar constants are documented in-code; retune them together, not
  individually, if FFME's version or tick cadence changes.
