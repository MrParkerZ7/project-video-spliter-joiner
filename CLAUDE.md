# CLAUDE.md — VideoSplitJoiner

A .NET 8 WPF app that splits and joins video **without re-encoding**. See [README](README.md),
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), and [docs/USER_GUIDE.md](docs/USER_GUIDE.md).

## Layout

- `src/Core/` — `VideoSplitJoiner.Core`, UI-free media logic (Ffmpeg, Media, Split, Join, Errors).
- `src/App/` — `VideoSplitJoiner.App`, WPF UI + hand-rolled MVVM view models.
- `tests/Core.Tests/`, `tests/App.Tests/` — xUnit + FluentAssertions.
- `packaging/package.ps1` — single-file self-contained win-x64 publish + bundled ffmpeg + zip.
- `docs/todo/` — the task board. **Do not touch it** during code/doc work.

## Conventions (keep these true)

- **Core stays UI-free.** No WPF references (`PresentationFramework`/`PresentationCore`/`WindowsBase`)
  in `VideoSplitJoiner.Core`. The `CoreIsUiFreeTests` guard enforces this — keep it green.
- **UI colors come from the design tokens — never hardcode.** The theme lives in `src/App/Themes/`
  `Tokens.xaml` (brushes/radii/type) + `Controls.xaml` (control templates), merged in `App.xaml`. Views
  reference token brushes (`AccentBrush` gold, `SurfaceBrush`, `TextPrimaryBrush`, …) as `StaticResource`
  — do not add hardcoded hex/`Colors.*` in a view (a code-behind converter that can't use `StaticResource`
  is the only exception, and it uses the same token color values). Gold `AccentBrush` = primary actions /
  timeline pins / playhead / focus. The redesign reference is `docs/design/references/`.
- **Typography is IBM Plex, bundled — not system fonts (G-019).** `IBM Plex Mono` (primary/mono readouts,
  `MonoFontFamily`) + `IBM Plex Sans` (`SansFontFamily`) ship as `Resource` TTFs in `src/App/Fonts/`
  (SIL OFL-1.1 — see `THIRD-PARTY-NOTICES.md`) and are referenced via pack URI with system fallbacks
  (`…#IBM Plex Mono, Consolas, Cascadia Mono`). Reference the font tokens, don't name a face directly.
- **Both screens are two-column: visual left / tools right (G-019).** Split and Join use a Grid with a
  draggable `GridSplitter` — LEFT is the preview + timeline (Split) / clip list (Join), RIGHT is a
  scrollable tool panel (Load, Clear, and every control below). The right panel is 360px by default
  (300–520 range). Match the sample's structure — header + "lossless · no re-encode" tagline, gold
  format badge, file-info card, section headers, mono DIR/NAME fields, Join "Estimated result" panel.
  Formatting/estimate helpers are pure and unit-tested in `Core/Media/MediaFormat.cs`.
- **The window uses a custom `WindowChrome` title bar** (`MainWindow.xaml` caption row + caption-button
  styles in `Themes/Controls.xaml` + `WindowStateConverters.cs`), not the native chrome. Keep the
  `WM_GETMINMAXINFO` work-area clamp + the maximized content margin — they stop a maximized window from
  covering the taskbar / clipping content. Caption buttons drive `WindowState`; close hovers `DangerBrush`.
- **All FFmpeg/ffprobe execution goes through `FfmpegRunner` / `FfprobeRunner`.** Never spawn
  `ffmpeg`/`ffprobe` directly and never build commands by string concatenation — use the typed
  `FfmpegArgs` (`ArgumentList`-based) builder.
- **Child-process std streams are decoded as UTF-8.** Both runners set
  `StandardOutputEncoding`/`StandardErrorEncoding = Encoding.UTF8` on the `ProcessStartInfo`. ffmpeg/
  ffprobe emit UTF-8 regardless of the console codepage; this is what keeps **non-ASCII (unicode)
  paths** intact through the probe JSON and the stderr tail (and it's what resolved the `.ts`/mpegts
  split failure — the exit `-28` was a mangled-path symptom). Don't remove the encoding overrides.
- **Exit `-28` / `ENOSPC` maps to `DiskFull`; disk space is pre-flighted.** `FfmpegErrorMapper` keys
  the disk-full category on the exit code (`-28` == `AVERROR(ENOSPC)`) as well as the stderr phrase,
  because an out-of-space write often leaves only a benign mpegts warning in the tail. `SplitEngine`
  runs a best-effort `DriveInfo` free-space pre-flight (`EnsureEnoughFreeSpace`) that fails early with
  the friendly message; keep both. Don't surface a raw stderr warning as the headline.
- **Errors are copyable and a full log is saved.** `UserFacingError` carries `FullText` + `LogFilePath`
  (with computed `CopyText`/`DetailText`); `ErrorLogWriter` writes the complete stderr + command +
  exit + UTC timestamp to `%LOCALAPPDATA%/VideoSplitJoiner/logs/<op>-<timestamp>.log`, best-effort
  (a logging failure never crashes the run). The `App` layer's `ErrorActions` copies to the clipboard
  and reveals the log in Explorer (Copy error / Open log file buttons on Split + Join). Keep log
  writing best-effort and the copy text unit-testable on `UserFacingError`.
- **Last input folder persists via `AppSettings`.** `LastInputDir`/`LastOutputDir` are saved to
  `%APPDATA%/VideoSplitJoiner/settings.json` (temp-then-rename, robust to missing/corrupt/unwritable —
  never throws). The file picker opens at the last input folder. Keep settings failures non-fatal
  (in-memory fallback).
- **The split output dir defaults to the loaded file's folder and re-anchors on every load (G-020).**
  `SplitViewModel.LoadAsync` sets `OutputDir = Path.GetDirectoryName(path)` unconditionally on each load
  (drag or picker), so exports default next to the source. It stays user-editable, but a new load
  discards the previous manual value and re-anchors to the new file's folder. Do **not** reinstate the
  old "default to remembered `LastOutputDir`" behavior — the file's folder wins.
- **The `-c copy` no-re-encode invariant is sacred (split + join).** Split and join must never emit
  an encoder flag. The args-builders forbid encoder tokens and require a bare `copy`; the invariant
  is re-asserted at runtime before launch and by unit tests on the token list. Do not add a
  re-encode path in v1.
- **No auto-detect.** The black/white/scene auto-detect feature was removed (no `Core/Detect`,
  no `SplitPointDetector`, no candidate UI). Do not reintroduce a detect layer or candidate ticks;
  cuts are placed manually (typed marker, "set cut at playhead", or timeline click).
- **One bundled ffmpeg SHARED build serves preview + engine.** A single app-local `ffmpeg/` folder
  holds BOTH the shared `*.dll` (avcodec-*/avformat-*/… — P/Invoke-loaded by FFME for the preview)
  AND `ffmpeg.exe` / `ffprobe.exe` (shelled out to by the split/join engine). Don't split these into
  two bundles. Fetch it with `packaging/fetch-ffmpeg-shared.ps1`; `package.ps1` bundles the whole
  shared build. Binaries are not committed.
- **`Library.FFmpegDirectory` is set at startup.** `App.OnStartup` points
  `Unosquare.FFME.Library.FFmpegDirectory` at the ffmpeg shared DLLs **before any FFME control
  loads**, best-effort (no shared build → preview unavailable, never a crash). Keep that ordering.
- **The preview is downscaled + hardware-accelerated — never the cut.** The FFME preview enables
  HW decode and installs a `scale=W:H` downscale filter (`PreviewScale`, capped ~1080p) for smooth
  4K playback. This is preview-only: the split stays `-c copy` and is never decoded, so the cut is
  always full source resolution. Keep 4K/decode changes on the preview side of the seam.
- **Keyframe-snap is intentional.** Cuts snap to the nearest keyframe (ties → earlier, clamps at
  ends). This is a design guarantee, not a bug — surface deltas/warnings, don't try to make cuts
  frame-exact.
- **Keyframe indexing is background + non-blocking.** `SplitViewModel.LoadAsync` gates only on the
  fast metadata probe and **opens the preview before the keyframe scan runs**; the scan indexes in a
  cancellable background task (`IsIndexingKeyframes` / `KeyframesReady`), a new load cancels the prior
  index (stale-guard), and a cut placed mid-index awaits the same in-flight scan (snap-before-ready).
  Don't re-block the load on the full scan or add a second keyframe scan pass.
- **The keyframe scan is demux packet-flag, with a frame-scan fallback.** `MediaProbe.GetKeyframesAsync`
  reads keyframe packets at the demux layer (`-show_packets`, keeping `K`-flag packets) — no frame
  decode — and falls back to the decode-based `-skip_frame nokey` frame scan only when the packet
  query is empty or throws. Keep both paths producing the same sorted-distinct output; don't drop the
  fallback.
- **Compatibility is refused, not fixed.** An incompatible join reports named mismatches and writes
  no output. Do not silently re-encode to reconcile mismatched clips.
- **The preview player is behind `IMediaPlayer` (FFME impl).** The Split-screen preview transport
  goes through the `IMediaPlayer` abstraction (`App/Media/`). View models (`PlayerViewModel`,
  `TimelineViewModel`) stay **WPF-free** and are tested against a fake player; the only WPF-bound
  impl, `FfmeMediaPlayer` (over an FFME `Unosquare.FFME.MediaElement`, which decodes through
  ffmpeg), is thin plumbing that just has to compile — its **live playback is verified only via
  `app-run`**, never in the unit suite. `NullMediaPlayer` is the no-op default so non-UI
  constructions/tests keep working. Don't leak WPF types into the player/timeline VMs; keep new
  transport (skip/frame-step/jump/volume/mute/speed) behind `IMediaPlayer` too. The jog row's skip
  buttons pass signed-seconds `CommandParameter`s to one `SkipCommand` (±1/±5/±10/±20/±60/±300/±600/
  ±1200 — the ±10m/±20m being ±600/±1200); add new skips as buttons, not new commands.
- **All cuts funnel through `AddCutAt` — one snap path.** Every way of placing a cut (manual add,
  "set cut at playhead", clicking the timeline strip) routes through `SplitViewModel.AddCutAt`, which
  keyframe-snaps and dedupes. Do not add a second snap/dedupe implementation for a new cut-entry
  surface — wire it through `AddCutAt`.
- **Cuts appear instantly — optimistic markers (`IsSnapPending`).** A cut placed while the background
  keyframe index is still running is added immediately at the requested time with `snapPending: true`
  (shows "snapping…"), then resolves in place to its nearest keyframe once the same in-flight scan
  finishes (`ResolveSnap`, re-dedupe on the final snapped time, stale-file guard). Don't re-block cut
  placement on keyframes; the keyframes-ready path stays synchronous.
- **The cut-markers list is time-ordered (T-071).** New markers are inserted into `Markers` at their
  time-sorted index (`InsertMarkerSorted`, key = `Snapped`, provisionally `Requested` while pending),
  and a pending marker re-settles into its slot when its snap resolves — so the list reads chronologically
  regardless of add order. The split plan and the "Parts to export" segments were already time-ordered
  (both sort independently); keep marker display order == chronological, and don't rely on a marker's
  list index meaning "Nth part".
- **Operations are never silent — visible progress + stage + ETA.** A running split/join must always
  show a progress bar and a status line. `OperationViewModel.IsIndeterminate` drives a busy bar until a
  real fraction (>0) arrives (ffmpeg `time=` is sparse); the engines report an `IProgress<OperationStatus>`
  stage channel (split: Preparing → Splitting (N parts) → Finalizing → Done; join: Checking
  compatibility → Joining → Finalizing → Done) synced to real work, not a timer; and `EtaEstimator`
  turns elapsed-vs-fraction into a friendly "~Ns left". Keep `OperationStatus` distinct from the numeric
  bar and keep `EtaEstimator` WPF/wall-clock-free (unit-tested).
- **Selectable parts route by selection (`SelectedSegmentIndices`).** After cuts are set, the Split
  screen projects selectable `SplitSegmentViewModel` rows; only checked parts are written. A full
  selection passes `null` and keeps the fast segment-muxer path; a strict subset passes the selected
  **original** 1-based indices and the engine uses the per-segment `-ss/-to -c copy` path (final part
  omits `-to`). Preserve original part indices in filenames; keep it lossless (no re-encode on either
  path); an empty non-null selection is an invalid request.
- **Clear / Clear all reset the screen (`IMediaPlayer.Unload`).** Split's **Clear** unloads the file,
  blanks the preview via `IMediaPlayer.Unload()`, cancels the background keyframe index, and clears
  markers/segments/results; Join's **Clear all** empties the clip list. Both are guarded off while an
  operation is running. Keep `Unload` on the player seam (blank surface + reset duration/playing state).
- **Drag/drop plumbing is code-behind that routes to existing VM commands.** Drop and drag handlers
  live in the view code-behind (`SplitView`/`JoinView`) and add **no** load/add/reorder logic — a
  file drop routes to `LoadCommand` (Split, first file) / `AddFilesCommand` (Join, all files); a
  clip-row drag routes to `JoinViewModel.Move` → the same `MoveAsync` the Up/Down buttons use (one
  reorder path). The **accept-filter is a pure, tested helper** (`VideoFileFilter.AcceptVideoFiles` /
  `HasAnyVideo`) — keep it WPF-free and unit-tested; don't inline extension checks in code-behind.
  **Internal vs external drags are distinguished by payload type** (`typeof(JoinItemViewModel)` =
  reorder, `DataFormats.FileDrop` = external add), never by guessing — preserve that when touching the
  Join drop handler. Do not add new drop-side business logic; wire it through the existing commands.

## Build / test / package

The .NET 8 SDK lives at `D:\_env_storeage\dotnet` and is **not on PATH** — invoke by full path:

```powershell
D:\_env_storeage\dotnet\dotnet.exe build VideoSplitJoiner.sln -c Release
D:\_env_storeage\dotnet\dotnet.exe test  VideoSplitJoiner.sln
powershell -File packaging/package.ps1        # produces dist/VideoSplitJoiner-v<Version>-win-x64.zip
```

- **Tests use synthetic, FFmpeg-generated fixtures** and **guard-skip when the binary is absent**
  (see `FfmpegTestBinaries.SkipIfMissing`), so a machine without FFmpeg still runs green. FFmpeg
  binaries are **not committed** — the test path references them as an override; packaging copies
  them in from `-FfmpegSource`.
- `Directory.Build.props` holds `<Version>` (currently `0.1.0`; `1.0.0` is the target).

## Git

- **Commit with explicit pathspecs** (`git commit -m … -- <paths>`), not `git add -A` — the board
  (`docs/todo/`) and build output must not be swept into unrelated commits.
- Do not edit `docs/todo/` as part of code/doc changes.
