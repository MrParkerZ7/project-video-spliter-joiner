# ADR 0008: Custom WindowChrome dark/gold caption with WM_GETMINMAXINFO taskbar clamp

## Status

Accepted.

## Context

The app is a dark, gold-accented WPF shell — the design tokens are a near-black
window background (`BgColor #FF0D0F13`) and a gold primary accent
(`AccentColor #FFE0A83A`), merged app-wide from `src/App/Themes/Tokens.xaml`
(see `docs/adr/0004-ffme-over-mediaelement.md` for the sibling theming decisions).
The default Win32 non-client caption is painted by the OS: a light/system-colored
title bar with system min/max/close buttons that does not honor the app's theme and
sits jarringly above an otherwise fully dark surface.

Forces at play:

- **Theme consistency** — the caption must be the same dark surface + gold accent as
  the client area; the stock OS title bar cannot be recolored.
- **Keep real window behavior** — a fully borderless (`WindowStyle=None`) window loses
  OS-managed resize borders, snap, and drag; the shell still wants native resize/snap.
- **Taskbar-safe maximize** — a WindowChrome window that maximizes must clamp to the
  monitor *work area*, not the full monitor rect, or it covers the taskbar and pushes
  the caption buttons off-screen.
- **Crisp icons at fractional DPI** — the min/max/restore/close glyphs must stay sharp
  at 100 % and 150 % DPI without depending on a system symbol font.

## Decision

Keep a real `SingleBorderWindow` (`MainWindow.xaml` — `WindowStyle="SingleBorderWindow"`,
`ResizeMode="CanResize"`) but replace the non-client caption with a **themed WindowChrome
caption**, and clamp the maximized bounds to the work area via a native
`WM_GETMINMAXINFO` hook.

Concretely:

- **WindowChrome caption (T-056).** `WindowChrome.WindowChrome` on `MainWindow` sets
  `CaptionHeight="34"`, `ResizeBorderThickness="6"`, `GlassFrameThickness="0"`,
  `CornerRadius="0"`, `UseAeroCaptionButtons="False"`. A custom 34px caption `Grid`
  (background `BgBrush`) draws a gold accent mark (`AccentBrush`) + app title
  (`CaptionTitle`, bound from `MainViewModel.CaptionTitle => BaseTitle`) on the left and
  our own min / max-restore / close buttons on the right. The running-progress overlay
  (% + ETA) is deliberately kept OFF the caption and put on `Window.Title`
  (`MainViewModel.WindowTitle`) for the taskbar/alt-tab surface only.
- **Custom caption buttons.** `CaptionButton` / `CloseCaptionButton` styles
  (`src/App/Themes/Controls.xaml`) — transparent shell, `SurfaceHover` on hover, close
  hovers `DangerBrush` (red) with a near-white X. Each carries
  `WindowChrome.IsHitTestVisibleInChrome="True"` so it stays clickable inside the drag
  chrome. Handlers in `MainWindow.xaml.cs` (`Minimize_Click`, `MaxRestore_Click`,
  `Close_Click`, `Caption_MouseLeftButtonDown`) implement minimize / toggle-maximize /
  close / double-click-to-maximize; single drag is handled by WindowChrome, with
  `DragMove()` as a fallback guarded to never run while maximized.
- **Vector caption icons (T-076).** The glyphs are stroked `Path` geometries defined once
  in `Controls.xaml` (`MinimizeCaptionGeometry`, `MaximizeCaptionGeometry`,
  `RestoreCaptionGeometry`, `CloseCaptionGeometry`) drawn on a 10×10 field with `.5`-aligned
  coordinates and `SnapsToDevicePixels`/`UseLayoutRounding` for crisp 1px edges. This
  replaced an earlier Segoe MDL2 font-glyph converter — the retired
  `WindowStateToMaxRestoreGlyphConverter` (noted at the top of
  `src/App/Views/WindowStateConverters.cs`). The maximize↔restore swap is done purely by
  per-`Path` `WindowState` `DataTrigger`s in XAML (maximize square Visible in Normal,
  restore double-square in Maximized) — no click-handler role in the swap.
- **Per-state margin/border converters.** A WindowChrome window keeps its (invisible)
  resize border when maximized, so two `IValueConverter`s in `WindowStateConverters.cs`
  switch on `WindowState`: `WindowStateToContentMarginConverter` (T-056) insets the root
  `Grid` by `SystemParameters.WindowResizeBorderThickness` **only** when Maximized (zero
  otherwise) so content isn't clipped under the invisible border;
  `WindowStateToBorderThicknessConverter` (T-066) draws the themed 1px frame line
  (`BorderStrongBrush`) only in Normal and collapses it to 0 when Maximized to avoid a
  stray floating line.
- **Taskbar clamp (T-056).** `OnSourceInitialized` in `MainWindow.xaml.cs` adds an
  `HwndSource` hook (`WndProc`) that handles `WM_GETMINMAXINFO` (`0x0024`).
  `WmGetMinMaxInfo` calls `MonitorFromWindow(MONITOR_DEFAULTTONEAREST)` +
  `GetMonitorInfo` (both P/Invoked from `user32.dll`) and sets the incoming `MINMAXINFO`
  `ptMaxPosition`/`ptMaxSize` to the monitor **work area** (`rcWork`, which excludes the
  taskbar), expressed relative to the monitor origin, then marshals the struct back.

## Consequences

**Positive**

- **Full theme consistency.** The caption is the same near-black surface + gold accent as
  the rest of the shell; there is no light OS title bar breaking the dark look.
- **Native behavior retained.** Because it stays a `SingleBorderWindow` + WindowChrome
  (not `WindowStyle=None`), OS resize borders, snap, and the resize grips still work; the
  custom caption only replaces the visuals and the three buttons.
- **Taskbar-correct maximize by construction.** The `WM_GETMINMAXINFO` hook clamps the
  maximized window to `rcWork`, so it never covers the taskbar and the caption buttons
  stay on-screen — correct across monitors via `MONITOR_DEFAULTTONEAREST`.
- **Crisp, font-independent icons.** Vector `Path` geometries render sharply at 100 %/150 %
  DPI and don't depend on a system symbol font being present or theme-colored.

**Negative**

- **Native interop + Win32 surface.** The shell now owns P/Invoke to `user32.dll`, hand-marshalled
  `MINMAXINFO`/`MONITORINFO`/`RECT`/`POINT` structs, and a raw `WndProc` — Win32 plumbing that
  didn't exist with the stock caption and isn't unit-tested (verified live via `app-run`).
- **Re-implemented window management.** Minimize/maximize/restore/drag/double-click, tooltip
  swap (`OnStateChanged`), and the maximize↔restore glyph state are our code now, not the OS's —
  more surface to keep correct.
- **The invisible-border quirk is load-bearing.** The maximized state genuinely needs the
  margin/border converters; without the `WindowResizeBorderThickness` inset, content is clipped
  under the invisible resize border — the non-obvious gotcha this ADR records.

**Forced follow-ons**

- The `WM_GETMINMAXINFO` clamp must stay in place — removing it regresses to a taskbar-covering
  maximize. It is the deliberate cure for that gotcha, not incidental.
- Keep the two `WindowState` converters and the per-`Path` DataTriggers in lock-step with the
  WindowChrome `ResizeBorderThickness`/`CaptionHeight` — changing the chrome geometry changes the
  correct maximized inset.
- Caption glyph/style changes stay in the `*CaptionGeometry` resources + `CaptionButton`/
  `CloseCaptionButton` styles in `Controls.xaml` so the three icons stay visually consistent;
  don't reintroduce a symbol-font glyph path (the reason T-076 retired it).
