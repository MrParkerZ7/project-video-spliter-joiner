# D-003 · Split Tool-Panel — Position on the Add-cut line; Cut markers + Parts 50/50 vertical

> Design record (draft) — a further refinement of the Split tool-panel layout (after reviewing T-089).
> Status: **draft** (sealed by `todo-design-done`). Board artifact: `docs/todo/D-003.md`.

## 1. What changes (from the current T-089 layout)
Two moves:
1. **Position onto the Add-cut line.** The POSITION add-at-time control (the `POSITION` label + time field +
   "Add at time" button) moves **onto the same row as the "Add cut at playhead" button**, inside the Cut markers
   section — so both add gestures share one line at the top of the section.
2. **Cut markers + Parts to export share 50/50 vertical.** The two list-bearing sections **stack** and **split the
   available vertical height equally** (each ~50%). Parts to export returns to **full width** (it's no longer a
   side column beside Position).

## 2. Layout sketch

```
CURRENT (T-089)                          TARGET (D-003)
┌───────────────────────────┐           ┌──────────────────────────────────────┐
│ Cut markers               │           │ Cut markers                          │ ┐
│ [Add cut at playhead]     │           │ [Add cut at playhead] │POSITION[__]│Add│ │ 50%
│ ┌───────────────────────┐ │           │ ┌──────────────────────────────────┐ │ │
│ │ marker list           │ │           │ │ marker list                      │ │ │
│ └───────────────────────┘ │           │ └──────────────────────────────────┘ │ ┘
├────────────┬──────────────┤           │ Parts to export        [All] [None]  │ ┐
│ Position   │ Parts to     │           │ ┌──────────────────────────────────┐ │ │ 50%
│ [__] [Add] │  export list │           │ │ parts list                       │ │ │
└────────────┴──────────────┘           │ └──────────────────────────────────┘ │ ┘
                                         └──────────────────────────────────────┘
                                          the two sections split the height 50/50
```

## 3. Decisions
- **D1 · Position on the Add-cut row.** The Cut-markers section's first row becomes a horizontal group:
  **`[Add cut at playhead]` · `POSITION` label · time field · `[Add at time]`**. Both add gestures on one line.
  At the **~360px narrow horizontal panel** this row is busy (button + label + 86px field + button) — let it
  **wrap gracefully** (WrapPanel) when it doesn't fit, single-line when it does (roomy in vertical mode).
- **D2 · 50/50 vertical split — bounded Grid star-rows (recommended).** To make the two sections truly share the
  height (and grow with the window), the tool-panel container changes from `ScrollViewer→StackPanel` to a
  **`Grid`** where the fixed elements (file-info card, keyframe warning, Output, Run) are `Auto` rows and the
  **Cut-markers list row + Parts-to-export list row are two `*` (star) rows** → each gets 50% of the leftover
  vertical space. Each list scrolls **internally** if its content overflows its half. (The alternative — equal
  fixed `MaxHeight`s — is simpler but doesn't grow with the window; the star-row split is the real "50/50".)
- **D3 · Short-window fallback.** Give each list-section a sensible `MinHeight` so the controls stay usable; if the
  panel is genuinely too short for everything (fixed rows + 2×min lists + Output + Run), fall back to an outer
  scroll (keep a `ScrollViewer` wrapper that only kicks in below the min, or let the two lists' internal scroll
  absorb it). The Run button must always be reachable (the existing constraint).
- **D4 · Parts full-width.** Parts to export is no longer a side column — it's the full-width bottom section. Its
  header (title + "(N selected)" + All/None chips) + segments list are unchanged, just re-parented.

## 4. Preserved
Every control/binding intact — `SetCutAtPlayheadCommand`, `NewMarkerPosition` + `AddMarkerCommand`, All/None
(`SelectAll/NoSegmentsCommand`), per-part `IsSelected`, `SeekToMarkerCommand`/`RemoveMarkerCommand`,
`SelectedSegmentCount`, per-part progress. **Re-parent, don't rewire.** Composes with **D-001** vertical mode
(the 50/50 split is axis-agnostic — it splits the tool panel's height in both modes) and the D-002 waveform
(video region, untouched). Load/Clear stay on the tab line (T-088). Output/Run unchanged.

## 5. Resolved at seal (todo-design-done)
- **D5 · 50/50 mechanism = bounded Grid star-rows.** The two list sections are equal `*` rows in a bounded Grid
  (fixed elements `Auto`), so they truly split the leftover height 50/50 and grow with the window. Not fixed heights.
- **D6 · Short-window fallback = internal list scroll + `MinHeight`.** Each list scrolls inside its half; a
  `MinHeight` on each keeps controls usable; the Run button stays reachable (an outer scroll only as a last resort).

_Status: **confirmed** (sealed 2026-07-19 via todo-design-done). All open questions resolved._

## 6. Next
`todo-design-done` seals this, then `todo-task D-003` decomposes it into a build epic (`perfect-dev`: re-parent the
Add-cut row + restructure the tool-panel container to the star-row 50/50 Grid + full-width Parts).
