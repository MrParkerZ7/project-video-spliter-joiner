# ADR 0016: One shared Bulk Cut preview player bound to the selected row (not per-row) + cut profiles persisted in AppSettings

## Status

Accepted.

## Context

G-037 finishes the Bulk Cut tab (design [D-004](../design/D-004/README.md)) by adding two things a
batch-trim workflow was missing: a way to **watch** a video and set its cut points by eye, and a way to
**reuse** a cut across a whole series. Unlike the Split screen — which owns exactly one loaded file —
the Bulk Cut tab holds a **list of N rows** (a folder of episodes). That single difference drives two
design questions:

1. **How many preview decoders?** One per row, or one shared player the selected row borrows?
2. **Where do reusable cut profiles live**, and in what shape, so one profile fits episodes of
   *different* lengths and survives a restart?

The forces in tension:

- **FFME/ffmpeg decoders are heavy, native resources.** Each `FfmeMediaPlayer` wraps a real
  `Unosquare.FFME.MediaElement` P/Invoking the bundled ffmpeg ([ADR 0004](0004-ffme-over-mediaelement.md),
  [ADR 0010](0010-shared-ffmpeg-bundling.md)). A player *per row* would spin up N simultaneous decoders
  for a list where **only one row is ever being watched** — a thundering herd of GPU/CPU and native AV
  handles.
- **The Split screen already has a complete, tested player surface.** `PlayerView` + `PlayerViewModel`
  over the `IMediaPlayer` seam ([ADR 0004](0004-ffme-over-mediaelement.md)) already carry transport,
  scrub, hover-thumbnails, the full jog/frame-step/jump control set, and volume/speed — all WPF-free and
  unit-tested. Re-authoring any of it for Bulk would duplicate a tested surface.
- **Two FFME elements already coexist in one process** (Split + Bulk). Rapid source switching is the
  known native-AV hazard — issuing an `Open` while a prior `Close` is still in flight — already solved
  for a single element by `MediaReopenGuard` (T-080). More elements multiply that hazard.
- **A profile must apply across uneven-length episodes and outlive the session.** A 22-minute and a
  24-minute episode share the *same* end card, so a reusable "trim the last 12s" cannot be an absolute
  timestamp — it has to be measured from the end. And it must persist to disk, robustly, the way the
  app already remembers its folders ([the settings store](../ARCHITECTURE.md)).
- **Core must stay WPF-free.** `CoreIsUiFreeTests` guards it; any persisted, testable model belongs in
  Core, not the `App` assembly.

## Decision

Adopt **one shared preview player bound to the selected row**, reusing the Split player wholesale, and
model **cut profiles as a WPF-free Core record persisted by `IAppSettings`**.

- **(a) One shared player, bound to `SelectedItem` — never per-row.** `BulkCutViewModel` owns a single
  `PlayerViewModel` over **one** `FfmeMediaPlayer` (`Player`) — the tab's own FFME element, distinct from
  the Split tab's. Selecting a row (`SelectedItem`, two-way bound from the list `ListBox`) opens **that**
  file in the one player; a null selection unloads it; removing the selected row re-points the selection
  at a neighbour so it never opens a just-removed file; the first add auto-selects the first row. The
  pane **reuses the Split `PlayerView` wholesale** (`DataContext = Player`), inheriting play/pause/stop,
  scrub, hover-thumbnail, the ±1s / 5s / 10s / 20s / 1m / 5m / 10m / 20m jog set (`SkipCommand`),
  frame-step, jump-to-ends, and volume/speed. **No per-row player, no second player view or view model.**

- **(b) `MediaReopenGuard` auto-engages on a fast switch.** `Player.Open` routes through
  `FfmeMediaPlayer`'s built-in `MediaReopenGuard` (T-080), so a rapid row-to-row switch **supersedes the
  prior pending Open** rather than racing a still-in-flight Close — no bulk-specific guarding is added.

- **(c) Only the active tab decodes.** Because two FFME elements (Split + Bulk) live in one process,
  `MainViewModel.StopInactiveScreenPlayers` stops the inactive screens' players on **every tab switch**,
  and `RunBatchAsync` stops the preview before the batch trims. At most one decoder is ever busy,
  regardless of list length.

- **(d) Set-at-playhead reuses the existing snap path.** `SetIntroAtPlayheadCommand` /
  `SetOutroAtPlayheadCommand` write the selected row's `CutMarkerViewModel.Requested` from the live
  playhead — the **same setter** the per-row scrub handles and the IN/OUT fields use — so the cut
  re-snaps to keyframes identically. `Set outro-start here` adds an outro handle if the row had none.
  Both gestures are gated on a selected row **and** a ready player (`CanSetCutAtPlayhead`) — a
  null-duration player has no real playhead to capture.

- **(e) Cut profiles: a WPF-free Core record, persisted as seconds, applied by a static helper.**
  `CutProfile` (`Core/Profiles/`) is a plain immutable record — `{ Name, IntroFromStart` (absolute
  time-from-start)`, OutroFromEnd?` (measured from the END; `null` ⇒ keep to EOF)` }` — validated at
  construction (non-empty name, non-negative offsets) and Core-resident, so it stays unit-testable and
  `CoreIsUiFree`-clean. `IAppSettings` persists it to `settings.json` as **seconds (double)**, not
  `TimeSpan` ticks, via the same temp-then-rename write the folder memory uses, and is
  **backward-compatible**: a missing `cutProfiles` key loads as an empty list (older files stay valid),
  the key is omitted entirely when there are none, and a corrupt entry is skipped rather than crashing.
  Saves are **upsert-by-name** (case-insensitive, position preserved). `CutProfileApplier`
  (`App/ViewModels/`, a WPF-free static helper — deliberately not bolted onto the VM) applies a profile
  to a set of rows and builds a profile from a row's current cut, **mirroring the T-096 apply-to-all
  convention exactly**: intro absolute + clamped, outro **from END** (`Duration − tail`) + clamped so
  uneven lengths align, each target re-snapped against its own keyframes and re-validated, and rows the
  profile invalidates **reported** through the shared `ApplyToAllReport` — never silently dropped.

**Alternatives rejected.** A **player per row** — N heavy native decoders for a list where only one is
ever watched, and N times the reopen-race surface. **A bespoke profiles sidecar / new store** —
`AppSettings` already persists robustly (temp-then-rename, corrupt/missing-tolerant); profiles ride the
proven path. **Storing the outro as an absolute time** — it would not align uneven-length episodes;
from-END does. **A new snap path for set-at-playhead** — the `Requested` setter already snaps.

## Consequences

**Positive**

- **One decoder busy at a time.** The tab costs roughly a single preview's worth of native AV regardless
  of how many videos are in the list — no thundering herd, and `StopInactiveScreenPlayers` keeps the
  inactive Split element quiet too.
- **Almost no new UI code.** The pane *is* the Split `PlayerView`; set-at-playhead re-snaps through the
  existing `Requested` setter; profile apply reuses the apply-to-all convention and the `ApplyToAllReport`
  shape. Less surface to drift or re-test.
- **Profiles survive restart and cross uneven lengths for free.** The from-END convention makes one
  "same series" profile fit a 22- and a 24-minute episode alike, on the already-hardened settings write.
- **Core stays UI-free.** `CutProfile` in `Core/Profiles/` keeps `CoreIsUiFreeTests` green and makes the
  model trivially unit-testable without an `App` dependency.

**Negative**

- **`SelectedItem` is now load-bearing.** Selection changes must drive Open/Unload correctly — selecting,
  clearing, and especially *removing* the selected row (re-point at a neighbour, never open a removed
  file) — which is extra selection bookkeeping the VM now owns.
- **Reopen-safety hinges on `MediaReopenGuard` staying in front of every `Open`.** A future code path
  that opened the FFME element directly would reintroduce the native-AV race the guard exists to prevent.
- **The profiles JSON is now a persistence contract.** The seconds-as-double shape and the
  missing-key-tolerant loader must be kept backward-compatible as the model evolves.

**Forced follow-ons** (this decision *causes* these; they are not optional)

- **Any future tab that hosts a player must join the `StopInactiveScreenPlayers` pass**, or the
  "only the active tab decodes" invariant breaks.
- **The set-at-playhead gestures must stay gated on a ready player** (`CanSetCutAtPlayhead`) — capturing
  a playhead from a not-yet-ready player would set a cut at time zero.
- **`CutProfileApplier` must keep mirroring the apply-to-all convention** (intro absolute, outro
  from-END, re-snap + re-validate, report-don't-drop). If one path's convention changes, both must — they
  deliberately share the `ApplyToAllReport` contract rather than duplicate it.
