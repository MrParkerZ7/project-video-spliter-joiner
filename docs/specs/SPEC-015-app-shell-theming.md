---
id: SPEC-015
slug: app-shell-theming
area: ui
title: App shell — window chrome, layout modes, theming, crash safety
status: current
sources:
  - src/App/ViewModels/MainViewModel.cs
  - src/App/Views/MainWindow.xaml
  - src/App/Views/MainWindow.xaml.cs
  - src/App/Views/OrientedSplitPanel.cs
  - src/App/Views/Converters.cs
  - src/App/Views/WindowStateConverters.cs
  - src/App/Themes/Tokens.xaml
  - src/App/Themes/Controls.xaml
  - src/App/App.xaml
  - src/App/App.xaml.cs
  - src/Core/Errors/ErrorLogWriter.cs
serves-goal: [G-023, G-027, G-029, G-031, G-032, G-035]
updated: 2026-08-22
---

## What
The application shell: the frame the three screens (Split / Join / Bulk Cut) live inside. It owns
(1) **tab routing** — `MainViewModel` re-points the shared taskbar/title binding, the shared tab-strip
Load/Clear buttons, and the inactive-screen player-stop from a single `SelectedTabIndex`; (2) **custom
window chrome** — a `WindowChrome` caption row with crisp vector min/max-restore/close icons, a
draggable caption, a themed 1px frame line, and a taskbar-safe maximize clamp; (3) **layout modes** —
an app-wide horizontal↔vertical axis flip (`OrientedSplitPanel`) with per-axis remembered split ratios
and a mode-aware nested markers/parts pair that flips opposite the outer axis; (4) **theming** — the
dark+gold design-token dictionary and the implicit control styles (scrollbar, tooltip, splitter,
caption buttons, HeroButton) applied app-wide; and (5) **crash safety** — three global unhandled-
exception sinks that always log the crash (via `ErrorLogWriter`) and keep a recoverable UI error from
tearing the process down.

## Why
The shell is the connective tissue that lets one window host three screens without each screen
re-implementing chrome, theming, taskbar progress, or the layout toggle. The custom `WindowChrome`
caption is what makes the dark+gold identity extend to the title bar (Windows' native caption cannot be
themed). Vertical layout (G-032) exists so the app is usable on a portrait/vertical monitor; the 50/50
tool panel + inverse-axis markers/parts (G-035) keep both editing lists visible in either orientation.
The global crash handlers (G-031) exist because, without them, an unhandled exception on the dispatcher,
a background task, or a native path silently kills the process with no dialog and no log — leaving the
user with nothing to report.

## Scope
**In:** `MainViewModel` tab routing (`SelectedTabIndex` → `CurrentOperation` / clear-command / load &
clear labels & tooltips / `StopInactiveScreenPlayers` / window-title & taskbar wiring); the
window-title composition (`ComposeWindowTitle` / `CaptionTitle`); custom `WindowChrome` caption +
vector caption-icon geometries + themed frame border & content margin + maximize clamp; layout-axis
state (`IsVertical`, per-axis split ratios) and the `OrientedSplitPanel` container + `InverseBoolConverter`;
the design tokens (`Tokens.xaml`) and shell-level implicit control styles (`Controls.xaml`: scrollbar,
tooltip, splitter, caption buttons, HeroButton); the global crash handlers in `App.xaml.cs` and the
`ErrorLogWriter` crash-log format.

**Out:** the individual screens' internal behavior (Split/Join/Bulk Cut view models & views — their own
specs); `OperationViewModel` progress/ETA/taskbar-state semantics (its own spec — this spec only covers
that the shell *routes* `CurrentOperation` to it); `AppSettings` file persistence mechanics (its own
spec — this spec covers only the VM-side write-through/seed of layout state); ffmpeg preview init
(`InitializeFfmpegForPreview` — covered by the FFME/preview spec); `ErrorLogWriter`'s ffmpeg-failure
`TryWrite`/`BuildLogBody` path (error-reporting spec — this spec covers only the crash path).

## Current behavior & invariants

- **I1** — `MainViewModel.CurrentOperation` resolves to the active screen's operation by
  `SelectedTabIndex`: tab 2 → `BulkCut.Operation`, tab 1 → `Join.Operation`, else → `Split.Operation`
  (via `IsBulkActive`/`IsJoinActive`). The taskbar binding (`Window.TaskbarItemInfo`) and window title
  follow this. (`MainViewModel.CurrentOperation`, `IsBulkActive`, `IsJoinActive`)
- **I2** — Setting `SelectedTabIndex` (two-way bound to the `TabControl.SelectedIndex`) raises
  `PropertyChanged` for `CurrentOperation`, `CurrentClearCommand`, `CurrentLoadLabel`,
  `CurrentClearLabel`, `CurrentLoadTooltip`, `CurrentClearTooltip`, and `WindowTitle` on every change.
  (`MainViewModel.SelectedTabIndex` setter)
- **I3** — `CurrentClearCommand` routes to the active screen's `ClearCommand` (Bulk on 2, Join on 1,
  else Split); each screen's `ClearCommand` is self-guarded, so the shared button disables during a
  running op. (`MainViewModel.CurrentClearCommand`)
- **I4** — The shared tab-strip labels/tooltips follow the active screen: `CurrentLoadLabel` =
  "Add videos…" (Bulk) / "Add files…" (Join) / "Load…" (Split); `CurrentClearLabel` = "Clear all"
  (Bulk & Join) / "Clear" (Split); with the matching `CurrentLoadTooltip` / `CurrentClearTooltip`
  strings per screen. (`MainViewModel.CurrentLoadLabel`/`CurrentClearLabel`/`CurrentLoadTooltip`/`CurrentClearTooltip`)
- **I5** — Missing-screen fallback: when `BulkCut` is null (legacy 3-arg test ctor) selecting tab 2
  does not throw and routes to Split; likewise `IsJoinActive` requires `Join is not null`. Tab routing
  is null-safe for absent screens. (`MainViewModel.IsBulkActive`/`IsJoinActive`)
- **I6** — On every tab switch `StopInactiveScreenPlayers` stops the preview player of each non-active
  screen: `Split.Player.Stop()` when `SelectedTabIndex != 0`, `BulkCut?.Player.Stop()` when not
  bulk-active (Join has no player); idempotent and null-safe so only the active tab decodes.
  (`MainViewModel.StopInactiveScreenPlayers`)
- **I7** — `ComposeWindowTitle(op)` returns the plain `BaseTitle` ("Video Split / Join") when `op` is
  null or `!op.IsRunning`. (`MainViewModel.ComposeWindowTitle`)
- **I8** — While running, `ComposeWindowTitle` returns `"{verb} {pct}% · {eta} — {BaseTitle}"`: `verb`
  is `StatusText` with any "(detail)" suffix and trailing "…" stripped (`ShortVerb`); `pct` is
  `Progress` clamped 0..1 ×100 rounded away-from-zero; the "· {eta}" segment is appended only when
  `EtaText` starts with "~" (its " left" suffix dropped, `ShortEta`) and omitted otherwise; when `verb`
  is empty the lead is just "{pct}%". (`MainViewModel.ComposeWindowTitle`/`ShortVerb`/`ShortEta`)
- **I9** — `CaptionTitle` always equals `BaseTitle` and is decoupled from `WindowTitle`: the in-app
  caption row binds `CaptionTitle` (never flickers with progress) while the OS `Window.Title` binds the
  progress-overlaying `WindowTitle`. (`MainViewModel.CaptionTitle`/`WindowTitle`; `MainWindow.xaml` Title binding)
- **I10** — `HookOperations` subscribes to each present screen's `Operation.PropertyChanged`, and
  `WindowTitle` is re-raised only when the op's `State`, `IsRunning`, `Progress`, `StatusText`, or
  `EtaText` changes — so the taskbar/title live-update as the active op progresses.
  (`MainViewModel.HookOperations`/`OnOperationChanged`)
- **I11** — `IsVertical` is seeded at construction from `settings.LayoutMode` (`Vertical` → true), so
  the app reopens in the last-used axis. (`MainViewModel` ctors)
- **I12** — Setting `IsVertical` writes through to `settings.LayoutMode`
  (`Vertical`/`Horizontal`) and raises `PropertyChanged` for `IsVertical` and `LayoutToggleTooltip`;
  the write-through is best-effort (no-op when settings is null). (`MainViewModel.IsVertical` setter)
- **I13** — `ToggleLayoutCommand` flips `IsVertical` (`ToggleLayout` = `IsVertical = !IsVertical`).
  (`MainViewModel.ToggleLayoutCommand`/`ToggleLayout`)
- **I14** — `LayoutToggleTooltip` names the mode the click switches *to* (D5): "Switch to vertical
  layout" while horizontal, "Switch to horizontal layout" while vertical. (`MainViewModel.LayoutToggleTooltip`)
- **I15** — `HorizontalSplitRatio` / `VerticalSplitRatio` are seeded from settings (fallback 0.7 / 0.62),
  written through to their own independent settings keys (D6 — a flip never distorts the other axis),
  and every set is clamped to [0.05, 0.95]. (`MainViewModel.HorizontalSplitRatio`/`VerticalSplitRatio` setters)
- **I16** — `OrientedSplitPanel` hosts exactly two region instances (`FirstChild`, `SecondChild`) plus
  one `GridSplitter`, and flips its split axis from `IsVertical`: vertical builds 3 `RowDefinitions`
  (region · splitter · region, splitter `ResizeDirection=Rows`), horizontal builds 3 `ColumnDefinitions`
  (splitter `ResizeDirection=Columns`) — the same three visual children are re-placed, never re-parented.
  (`OrientedSplitPanel.Rebuild`/`EnsureBuilt`)
- **I17** — The active axis's ratio (`VerticalRatio` if `IsVertical` else `HorizontalRatio`) drives the
  first region's star weight, clamped to [0.05, 0.95] with the second region = 1 − first; each region
  carries a `RegionMinLength` (80px) minimum so neither collapses; the splitter is `SplitterThickness`
  (6px) along the axis. (`OrientedSplitPanel.Rebuild`/`ApplyRatioToDefinitions`/`CurrentRatio`)
- **I18** — On splitter `DragCompleted`, the realized star weights are read back and only the active
  axis's ratio DP is written (the other axis's remembered ratio is untouched — D6), guarded by
  `_applyingRatio` so the write-back does not recurse into a rebuild; the two ratio DPs are two-way by
  default. (`OrientedSplitPanel.OnSplitterDragCompleted`/`OnRatioChanged`)
- **I19** — `InverseBoolConverter` inverts a bool (true → false, false → true), treats a non-bool/null
  value as false → true, and is symmetric in `ConvertBack`; it drives the nested Cut-markers‖Parts
  `OrientedSplitPanel`'s `IsVertical` from the inverse of the main axis (T-091) so the pair stacks in
  horizontal mode and sits side-by-side in vertical mode. (`Converters.cs` `InverseBoolConverter`; `SplitView.xaml`)
- **I20** — The caption vector-icon geometry resources parse to non-empty geometries that fit the
  pixel-aligned 10×10 field: Minimize is a single horizontal line (height 0, width 10), Maximize is a
  10×10 square outline, Restore is the double-square path, Close is the two-diagonal X; the layout-toggle
  glyphs are two-pane split rectangles. (`Controls.xaml` `*CaptionGeometry` / `Layout*CaptionGeometry`)
- **I21** — The themed 1px frame border is shown only in the Normal state and collapses to 0 when
  Maximized (so no stray floating line): `WindowStateToBorderThicknessConverter` returns
  `Thickness(0)` for Maximized, `Thickness(1)` otherwise. (`WindowStateConverters.cs`; `MainWindow.xaml` border)
- **I22** — Root-content margin insets by the resize-border thickness only when Maximized (else zero) so
  content clears the invisible `WindowChrome` resize border: `WindowStateToContentMarginConverter`
  returns `SystemParameters.WindowResizeBorderThickness` for Maximized, `Thickness(0)` otherwise.
  (`WindowStateConverters.cs`; `MainWindow.xaml` root grid margin)
- **I23** — The maximized window is clamped to the monitor **work area** (excludes the taskbar) via a
  `WM_GETMINMAXINFO` hook that sets `ptMaxPosition`/`ptMaxSize` from `MONITORINFO.rcWork` relative to the
  monitor origin. (`MainWindow.xaml.cs` `WndProc`/`WmGetMinMaxInfo`)
- **I24** — `App.OnStartup` wires all three managed unhandled-exception sinks: `DispatcherUnhandledException`
  logs, shows a copyable crash dialog, and sets `e.Handled = true` (recoverable UI error stays alive);
  `AppDomain.UnhandledException` logs best-effort; `TaskScheduler.UnobservedTaskException` logs and calls
  `SetObserved()`; every handler body is wrapped in its own try/catch so a throw inside a crash handler
  never recurses. (`App.xaml.cs` `WireGlobalExceptionHandlers` and the three handlers)
- **I25** — `ErrorLogWriter.TryWriteCrash(source, ex)` writes `crash-<sanitized-source>-<yyyyMMdd-HHmmss>.log`
  (Guid-suffixed on same-second collision) under `%LOCALAPPDATA%/VideoSplitJoiner/logs`, containing the
  UTC timestamp and each exception's Type/Message/Stack walked through the whole inner-exception chain
  (`BuildCrashBody`); a null exception is noted without throwing and any write failure is swallowed →
  returns null. (`ErrorLogWriter.TryWriteCrash`/`BuildCrashBody`)
- **I26** — The implicit (un-keyed) `ToolTip` style themes every tooltip app-wide as light `TextPrimary`
  text on a `Surface3` bordered card with wrapping at `MaxWidth=360` (T-099), overriding WPF's pale
  default. (`Controls.xaml` `ToolTip` style)
- **I27** — The implicit `ScrollBar` style themes every scrollbar app-wide: 10px thin dark
  (`Surface1`) track, collapsed line buttons, a `BorderStrong` rounded thumb that turns gold
  (`AccentBrush`) on hover/drag, with a horizontal-orientation trigger swapping in the horizontal
  template (G-027 / T-072). (`Controls.xaml` `ScrollBar` style + `Vertical/HorizontalScrollBar` templates)
- **I28** — `HeroButton` (keyed) is based on `AccentButton` and adds prominence on ≥2 axes — Bold weight,
  larger font (14) + padding (16,8), and a soft gold `DropShadowEffect` (AccentColor, 0.35 opacity) —
  using tokens only, no hardcoded hex (T-092). (`Controls.xaml` `HeroButton` style)

## Links
- Design: D-001 (layout axis) · D-003 / D-004 (50/50 tool panel + Bulk tab)
- Goals: G-023 (themed border) · G-027 (themed scrollbar) · G-029 (vector caption icons) · G-031 (global crash handlers) · G-032 (vertical layout) · G-035 (50/50 tool panel)
- Related specs: SPEC (Split screen) · SPEC (Join screen) · SPEC (Bulk Cut screen) · SPEC (OperationViewModel progress/ETA/taskbar) · SPEC (AppSettings persistence) · SPEC (error reporting / ErrorLogWriter ffmpeg path)
- Key code: `src/App/ViewModels/MainViewModel.cs` · `src/App/Views/MainWindow.xaml(.cs)` · `src/App/Views/OrientedSplitPanel.cs` · `src/App/Views/Converters.cs` · `src/App/Views/WindowStateConverters.cs` · `src/App/Themes/Tokens.xaml` · `src/App/Themes/Controls.xaml` · `src/App/App.xaml.cs` · `src/Core/Errors/ErrorLogWriter.cs`
