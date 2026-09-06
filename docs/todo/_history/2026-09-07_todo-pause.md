# ⏸ Pause — 2026-09-07, `todo-task-next` (profiles-card relayout)

Paused by `todo-pause` at a **clean ticket boundary** (stop-point kind **a**) — the drive was stopped
**before its first edit**, so nothing was mid-flight, nothing is half-applied, and no landed work was
touched. Tree clean at `e260dbf`, 1460 green, 0 warnings.

## What was running

A `todo-task-next` on the user's request:

> *"on profile section the profile list should be full width bar of item full width then new line instead
> scroll able, for action buttons should be separate lines from profile list also full width, but need to
> make sure to be auto new line if overflow too"*

The command writes its reviewed plan to the board **before** building, so the pause landed the plan and
stopped there. **[[G-053]]** and **[[T-168]]** are on the board; zero production code has changed.

## What the pause had to clean up

The discovery/review run (`wf_059b63a3-84c`) left **six untracked scratch files** in `tests/App.Tests/` —
`ZzCapSweep.cs`, `ZzChromeProbe.cs`, `ZzMinSizeSweep.cs`, `ZzScratchMeasure.cs`, `ProbeGeometryTemp.cs`,
`ScratchGeometryProbe.cs`. They were geometry probes the review agents wrote to *measure* the layout rather
than guess at it, which is the right instinct — but they sat in a compiled test project, untracked.

Removed (archived to the session scratchpad first), suite re-run green at 1460. A pause must never leave a
dirty tree, and untracked scratch inside a build path is worse than dirty: it compiles.

## What the killed review bought before it died

9 of 16 agents completed. Two findings changed the plan materially and are folded into [[T-168]]:

- **An uncapped wrap re-creates [[T-141]].** Measured at 760x620: the header grows **+154px** and pushes
  **Run 74px off-screen**, against only 72px of available slack. So [[T-161]]'s `MaxWidth` cap does not
  disappear — it rotates onto the Y axis as a two-row `MaxHeight` with vertical scrolling. Without this the
  change would have shipped a regression the original decision existed to prevent.
- **`HorizontalScrollBarVisibility="Disabled"` is load-bearing.** A `WrapPanel` inside a `ScrollViewer`
  whose horizontal scrolling is `Auto` or `Hidden` is measured at **infinite width** and never wraps. Miss
  it and the change looks right in the diff and silently does nothing.

Full salvage is in the session scratchpad as `profile-relayout-findings.md` — **temporary**; anything still
needed should be lifted into the ticket before it is swept.

## Resume

No new verb. `todo-next-all` / `todo-next` / `proceed G-053` picks [[T-168]] first — it is `in-progress`
with a ⏸ note and no live driver. The note names the exact next step: **start at Design step 1; no code has
been written.**

`spec: draft`, not `reviewed` — the third design lens and 6 critique agents never ran.
