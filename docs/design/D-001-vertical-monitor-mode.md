# D-001 · Vertical-Monitor Mode

> Design record (draft) — a switchable **portrait/vertical layout** for the video splitter/joiner, so the UI
> suits a vertical monitor instead of only the current horizontal two-column design.
> Status: **draft** (sealed by `todo-design-done`). Board artifact: `docs/todo/D-001.md`.

## 1. Problem / motivation
Both screens are built for a **landscape** monitor — a 3-column Grid (`SplitView.xaml` / `JoinView.xaml`:
`video * (MinWidth 320)` | `GridSplitter 6` | `tool panel 360 (300–520)`). On a **portrait** monitor that
layout cramps the video into a narrow left strip and wastes the tall vertical space. The user wants a button
to flip into a **vertical layout** that gives the video the full width up top and stacks the tools below.

## 2. Confirmed decisions (clarify round 1)
| # | Fork | Decision |
|---|------|----------|
| D1 | **Trigger** | A **manual toggle button in the title bar** (caption row). No auto-detect (a portrait/landscape auto-flip was considered and rejected as surprising on resize). |
| D2 | **Vertical arrangement** | **Video + timeline on TOP (full width), tool panel STACKED below** (scrollable). Natural portrait reading order. |
| D3 | **Scope** | **Both Split and Join** — one toggle flips both consistently. |
| D4 | **Persistence** | **Remembered across launches** in `settings.json` (`AppSettings`, beside window size / last folders). |

## 3. Layout model

```
HORIZONTAL (today, landscape)                 VERTICAL (new, portrait)
┌───────────────────┬──────────┐              ┌────────────────────────┐
│                   │  tools   │              │                        │
│      VIDEO        │  load    │              │         VIDEO          │
│                   │  clear   │              │                        │
│                   │  markers │              ├────────────────────────┤
│ ▸ timeline ─────  │  parts   │              │ ▸ timeline ──────────  │
│                   │  output  │              ├───────═════─────────── │  ← horizontal GridSplitter
│                   │  Split ▸ │              │  load    clear         │
└───────────────────┴──────────┘              │  cut markers           │
   ▲ vertical GridSplitter                     │  parts to export       │
                                               │  output   │  Split ▸   │
                                               └────────────────────────┘
```

- **The flip is axis-only.** The same three regions — **video (`PlayerView`)**, **timeline (`TimelineView`)**,
  **tool panel** — are re-hosted from **3 columns → 3 rows**. No control is added, removed, or re-parented into a
  different logical group; only the container axis changes. Every command/binding is untouched.
- **GridSplitter rotates with the axis:** a **vertical** splitter (drag left↔right, video vs tools) in horizontal
  mode becomes a **horizontal** splitter (drag up↕down, video/timeline vs tools) in vertical mode.
- **Timeline stays a horizontal strip** in both modes (it sits directly under the video); only the *stacking* of
  video-block vs tool-panel changes.
- **Default vertical split:** video+timeline block ≈ **62%** top, tool panel ≈ **38%** bottom, tool panel
  **scrollable** with a sensible `MinHeight` so the Run button is always reachable. (Recommendation — final ratio a build detail.)
- **Join screen** flips the same way: clip-list (left in horizontal) → **top**, tool panel (add/clear/overwrite/
  estimated-result/Run) → **bottom**.

## 4. Component / interaction model

- **`LayoutMode` (enum `Horizontal | Vertical`)** — a single app-wide setting (not per-screen; D3 says both flip
  together). Lives on a shared surface both screens observe: exposed by **`MainViewModel`** (e.g. `bool IsVertical`
  + `ToggleLayoutCommand`), backed by **`AppSettings.LayoutMode`** (persisted per D4; robust load/save like the
  existing settings — never throws). Restored on startup.
- **The toggle affordance** — a small **vector icon button in the caption row** (`MainWindow.xaml`), matching the
  G-029 caption-icon language (1px `Path` stroke, `TextPrimaryBrush`, gold on hover, ~46×34 hit area). Icon = a
  rectangle in the *target* orientation (wide-rect when currently vertical → "switch to horizontal"; tall-rect when
  currently horizontal → "switch to vertical"), tooltip `Switch to vertical / horizontal layout`. Bound to
  `ToggleLayoutCommand`. (Convention — show-target-mode — is a recommendation; show-current is the alternative.)
- **How the views adapt** — each screen (`SplitView`/`JoinView`) reads `IsVertical` and lays its three regions on
  the corresponding axis. Recommended WPF mechanism (a build decision): keep ONE instance of each region control and
  drive a **layout-mode-aware container** (a `Grid` whose `RowDefinitions`/`ColumnDefinitions` + each region's
  `Grid.Row`/`Grid.Column` + the `GridSplitter`'s orientation are switched by `DataTrigger`s on `IsVertical`), OR a
  small custom `OrientedThreePane` panel. Avoid duplicating the region markup. `perfect-dev` picks the exact XAML.
- **Per-axis split ratio** — remember the splitter position **separately per axis** (a horizontal drag shouldn't
  distort the vertical split, and vice-versa), or reset to the default ratio on each flip. Recommendation: remember
  per-axis; acceptable fallback: reset-to-default on flip.

## 5. States & edge cases (amplification)
- **States:** `Horizontal` (default) ⇄ `Vertical`. Persisted; restored on launch. Toggle is instant, no reload.
- Window **resized across the portrait/landscape boundary** → mode does **NOT** change (manual only, per D1).
- **Very short/narrow window in vertical** → tool panel scrolls (its `ScrollViewer` already exists); video block
  honors a min height; Run button always reachable.
- **Empty state** (no file loaded) → the placeholder adapts to the active axis (centered in the video region either way).
- **Mid-operation** → toggling layout during a running split/join is allowed (pure view change; the operation is
  unaffected); the progress/op-state surfaces re-flow with their panel.
- **Hover thumbnail (G-030) + scrub click (G-028)** → unaffected (they live on the timeline strip, which is identical
  in both modes).
- **First launch / missing setting** → default `Horizontal` (today's behavior; no migration needed).

## 6. What does NOT change
- No view-model logic beyond adding `IsVertical`/`ToggleLayoutCommand` + the `AppSettings` field. Every existing
  command, binding, the two-column *content*, the WindowChrome caption, the seek/thumbnail/progress systems — all intact.
- Not a responsive breakpoint system, not per-screen different modes, not a re-flow of individual controls within a
  panel — **one horizontal⇄vertical axis toggle**, applied to both screens.

## 7. Resolved at seal (todo-design-done)
- **D5 · Toggle icon convention:** the button shows the mode it will switch **to** (tall-rect while horizontal →
  "switch to vertical"; wide-rect while vertical → "switch to horizontal"), with the matching tooltip.
- **D6 · Split-ratio memory:** the splitter position is remembered **per-axis** — a horizontal drag and a vertical
  drag keep independent stored ratios, so flipping never distorts the other axis.
- **Deferred (explicitly out of D-001):** a keyboard shortcut (e.g. `Ctrl+L`) and auto-detect-portrait — noted as
  possible follow-ups, not part of this design.

_Status: **confirmed** (sealed 2026-07-19 via todo-design-done). All open questions resolved._

## 8. Next
`todo-design-done` seals this (verifies the open items above are answered or carried), then
`todo-task D-001` decomposes it into a build epic (`perfect-dev` implements — likely: AppSettings field +
MainViewModel toggle + the layout-mode-aware container on both screens + the caption toggle button + tests).
