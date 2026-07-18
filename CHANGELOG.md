# Changelog

All notable changes to VideoSplitJoiner are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project aims to follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The `1.0.0` release is the target
goal; `0.1.0` is the first end-to-end, shippable cut.

## [0.2.0] - Unreleased

Goal G-002: an **in-app video preview player** with **visual cut selection** on the Split screen,
then hardened over G-004→G-007 — the preview now decodes through **FFmpeg** (so it plays what the app
can cut, including HEVC / MKV / 4K), the black/white/scene **auto-detect feature was removed**, and
the player gained a full navigation-control surface plus a drag-resizable video pane. Still no
re-encode — the player only previews; every cut continues to keyframe-snap through the same path.

### Added

- **Scrub-bar hover thumbnail — hovering the timeline shows a frame preview at that time (G-030).**
  Hovering the player scrub bar shows a small frame image at the hovered time, following the cursor with
  an `mm:ss` label, so you can find a split point by sight without moving the main player. Frames come
  from a separate ffmpeg CLI process (bucket-cached temp jpgs, kept apart from the FFME preview); hover
  is debounced + coalesced latest-wins so a fast sweep stays smooth, best-effort (a failed grab shows
  nothing, never blocks), and the temp cache is swept on new load / clear.
- **Per-part split progress (G-025)** — splitting into N parts now shows each part's row in "Parts to
  export" advancing **Pending → Writing (live %) → Done (✓)** as it's written, not just one overall bar.
  A dedicated `IProgress<PartProgress>` channel drives it: the per-segment subset path reports its part
  index naturally, and the **fast single-pass segment-muxer path is preserved** — the current part is
  *derived* from ffmpeg's reported time via a pure, unit-tested `PartAt(time, boundaries)` mapping (no
  extra ffmpeg passes). The active row shows a gold live-fill; completed rows a green ✓.
- **Windows taskbar-button progress + ETA in the title (G-025)** — a running split/join shows a live
  progress fill on the **Windows taskbar button** (green while running, indeterminate pulse while
  preparing, red on failure, clearing cleanly when done), via `TaskbarItemInfo` bound to the active
  screen's operation. Because the taskbar button can't render text, the **ETA + %** ride in the window
  **title** (`"Splitting 45% · ~1m 20s — Video Split / Join"`, visible on taskbar hover / alt-tab); the
  in-app caption keeps showing the app name.
- **Clear operation states — done / cancelled are no longer invisible (G-027)** — the progress UI used
  to vanish silently on completion. Now the operation lifecycle has four distinct surfaces: **Running**
  (gold bar + status + ETA + Cancel), **Completed** (green ✓ + a result line — "Split into 3 parts" /
  "Joined 4 clips → joined.mkv" — plus **Open folder**), **Cancelled** (a muted note, not red), and
  **Failed** (the red error block with Copy error / Open log). Exactly one shows at a time and it resets
  on the next run / load / Clear (no stale "done"). On both Split and Join.
- **Themed scrollbars (G-027)** — scrollbars now use a thin dark-and-gold style consistent with the
  IBM Plex design (track on a low surface tier, a rounded `BorderStrong` thumb that turns **gold on
  hover/drag**), applied app-wide via an implicit `ScrollBar` style — replacing the default light
  Windows scrollbar chrome.
- **Cut markers list is ordered by time position, not the order added.** When you place cuts out of
  chronological order (a cut at 5:00, then one at 2:00), the "Cut markers" list now reads top-to-bottom
  in time order (2:00 above 5:00) instead of add order. A marker placed while the keyframe scan is still
  running settles into its correct time slot once its snap resolves, and removing a marker keeps the rest
  ordered. The split output was already time-ordered (the plan and the "Parts to export" segments sort by
  time) — this is a marker-list display fix only.
- **Two-column layout matching the design sample (G-019)** — both screens now split into a **left
  visual column** (the preview player + timeline/scrubber) and a **right tool panel** (Load / Clear
  and everything below — file-info, cut markers, parts-to-export, output, Run) behind a **draggable
  column splitter** (right panel 360px default, 300–520 range). The app adopts the sample's identity:
  **IBM Plex Mono / Sans** bundled (OFL-1.1, in `src/App/Fonts/`), the full dark surface + gold +
  semantic palette, and tight 6–12px radii. New sample structure — an app header with the
  "lossless · no re-encode" tagline, a gold **format badge** (`HEVC · MATROSKA`), a Split **file-info
  card** (`container · duration · size`), **"Cut markers"** and **"Parts to export"** section headers,
  mono **DIR / NAME** output fields, and a Join **"Estimated result"** panel (total duration + approx
  size). Pure formatting/estimate helpers extracted to `Core/Media/MediaFormat.cs` (fully unit-tested);
  all existing bindings/commands preserved — a relayout + restyle, not a rewire.
- **Output folder defaults to the loaded file's folder (G-020)** — the split output directory now
  **defaults to wherever the loaded file lives** and **re-anchors on every new load** (drag or picker),
  so exports land next to the source by default. It stays fully editable for the one-off case; a manual
  change is discarded the next time you load a file. The file-picker's remembered *input* folder is
  unchanged.
- **Selectable split parts (G-015)** — after you set cut points, the Split screen lists the resulting
  parts as a checklist (`Part 2 · 05:00–10:00 · 5:00`) with **All / None** toggles, and **only the
  parts you check are written** (`SplitSegmentViewModel` + `SplitRequest.SelectedSegmentIndices`).
  Unselected parts cost no time or disk — a strict subset extracts via a per-segment `-ss/-to -c copy`
  path (one ffmpeg run per chosen part), while a full selection keeps the fast single-pass segment-muxer
  path. Still lossless, and each selected part keeps its **original** part index in the filename (a
  chosen middle part stays `…_part02`). The Run button reflects the selection ("Split 2 of 3 parts").
- **Clear / Clear all (G-014)** — a **Clear** button on the Split screen unloads the current video and
  resets the whole screen (blank preview via `IMediaPlayer.Unload()`, markers / timeline / output /
  results cleared, the background keyframe index cancelled); a **Clear all** button on the Join screen
  empties the clip list and resets the compatibility verdict. Both are disabled while an operation is
  running so you can't wipe the workspace mid-op.
- **Staged operation status (G-013)** — a running split/join shows the current **stage** synced to the
  real work rather than a timer (`OperationStatus` reported through an `IProgress<OperationStatus>`):
  split runs **Preparing → Splitting (N parts) → Finalizing → Done**; join runs **Checking
  compatibility → Joining → Finalizing → Done**.
- **Estimated time remaining (G-013)** — while a split/join runs, a friendly ETA shows beside the
  status ("~1m 20s left", or "estimating…" until there's enough signal). It's smoothed (EMA over
  elapsed-vs-progress in `EtaEstimator`) so it trends down without lurching on ffmpeg's bursty
  `time=` reports, and it clears when the run ends.
- **In-app preview player** on the Split screen — the loaded file plays right there with
  play / pause / stop and a scrubbable timeline slider, and a `mm:ss.f / mm:ss.f` position/duration
  readout. Built behind an `IMediaPlayer` abstraction (`FfmeMediaPlayer` in production;
  `NullMediaPlayer` no-op default) with a WPF-free `PlayerViewModel`.
- **FFmpeg-decoded preview (G-004)** — the preview now decodes via **FFME/FFmpeg**
  (`src/App/Media/FfmeMediaPlayer.cs`, package `FFME.Windows`, behind the unchanged `IMediaPlayer`
  seam), **replacing the WPF `MediaElement`**. Because it decodes through the same bundled FFmpeg as
  the split/join engine, it plays formats Windows Media Foundation could not — **HEVC, MKV, 4K, and
  other exotic container/codec combos** — so the "preview unavailable" banner is now rare.
  `App.OnStartup` sets `Unosquare.FFME.Library.FFmpegDirectory` before any FFME control loads. **One
  bundled ffmpeg *shared* build** (`ffmpeg/` folder) now serves **both** the preview (shared DLLs)
  and the engine (`ffmpeg.exe` / `ffprobe.exe`); dev setup fetches it via
  `packaging/fetch-ffmpeg-shared.ps1`, packaging bundles it, and `THIRD-PARTY-NOTICES.md` covers FFME
  + the GPL ffmpeg build.
- **Player controls to find the exact split point (G-007)** — skip buttons (**±1s / ±5s / ±10s /
  ±20s / ±1m / ±5m / ±10m / ±20m**), **frame-step** (±1 frame), **jump to start / end**, plus a
  **volume slider + mute** and a **playback-speed** selector (**0.25×–2×**), all on `PlayerViewModel`
  / `PlayerView`.
- **±10m / ±20m skip buttons (G-011)** — the player's jog row gained back/forward **10-minute** and
  **20-minute** skips alongside the existing 1s/5s/10s/20s/1m/5m jumps, so long clips can be traversed
  in far fewer clicks (`PlayerView.xaml`, routed through the same `SkipCommand`).
- **Copyable error + saved full log (G-010)** — a failed split/join now shows a **selectable** error
  with a **Copy error** button (copies the headline + hint + full detail + log path) and an **Open
  log file** button that reveals the saved log in Explorer. The complete ffmpeg output — command,
  exit code, UTC timestamp, and full stderr — is written to
  `%LOCALAPPDATA%/VideoSplitJoiner/logs/<op>-<timestamp>.log` (`ErrorLogWriter`), and `UserFacingError`
  gained `LogFilePath` / `FullText` to carry it. The preview-unavailable banner is likewise selectable
  with its own Copy button.
- **Remembered last folders (G-010)** — the app now remembers your **last input and output folders**
  across runs. They persist to `%APPDATA%/VideoSplitJoiner/settings.json` (`AppSettings`); the file
  picker opens at the last input folder and the output directory defaults to the last-used one.
- **Resizable video pane (G-006)** — the Split screen's preview area is drag-resizable via a
  `GridSplitter` in `SplitView.xaml`.
- **Set cut point at playhead** — park the player and drop a cut marker at the current position.
- **Visual timeline strip** under the player showing the **playhead** and a tick per **cut marker**.
  **Click the strip** to drop a cut at that position; **click a marker tick** to seek to its snapped
  cut. Built from pure `TimelineMath` (normalized ↔ time) + a `TimelineViewModel` projection.
- Every visually placed cut (playhead-capture or timeline-click) funnels through the existing
  `AddCutAt` → **keyframe-snap + dedupe** path — one snap implementation, no new cut logic.
- **Drag and drop** — drag video files from Explorer onto the **Split** screen to load the first file
  (via the existing `SplitViewModel.LoadCommand`) or onto the **Join** screen to add them all in drop
  order (via `JoinViewModel.AddFilesCommand`, compat re-check follows), plus **drag-to-reorder** the
  Join clip list (same `MoveAsync` path as the Up/Down buttons). Drop plumbing is thin code-behind
  over the existing VM commands; the accept-filter is a pure, tested `VideoFileFilter` helper. An
  internal reorder drag is distinguished from an external file drop by its clipboard payload type
  (`JoinItemViewModel` = reorder, `FileDrop` = add). Non-video files are ignored.

### Changed

- **Custom themed window frame (G-018)** — the default light Windows title bar is replaced by a custom
  **dark title bar** (WindowChrome) matching the theme: the app title with a gold accent on the left,
  and themed minimize / maximize-restore / close caption buttons (close hovers red) on the right. The
  window still drags, resizes on all edges, and maximizes/restores correctly without covering the
  taskbar (a `WM_GETMINMAXINFO` work-area clamp + maximized content margin).
- **New premium dark + gold theme (G-017)** — the whole app is restyled to a token-driven **dark theme
  with a gold accent** (`#e0a83a`): near-black window (`#0d0f13`), charcoal rounded panels (`#15181e`),
  pure-black video area, and gold used consistently for primary actions (Run, play), the timeline
  **playhead and cut pins**, focus, and selection. Built as a design-token system — `src/App/Themes/`
  `Tokens.xaml` (brushes, corner radii, typography) + `Controls.xaml` (themed control templates), merged
  in `App.xaml`; every view references tokens, no hardcoded colors. Text uses theme tokens (readable on
  dark); compat green/red and error affordances are preserved, dark-tuned.
- **Split/join always show visible progress (G-012)** — a running operation now always shows a progress
  bar plus a status label, never a silent window. When granular progress hasn't arrived yet the bar
  animates as an **indeterminate busy indicator** (`OperationViewModel.IsIndeterminate`) instead of
  sitting frozen at 0% — the cure for the "-c copy split looks stuck" problem, since ffmpeg's `time=`
  can be sparse. It flips to a determinate bar the instant a real fraction arrives.
- **Faster, non-blocking video load (G-008)** — loading a file on the Split screen no longer waits on
  the full keyframe scan. `SplitViewModel.LoadAsync` now gates only on the fast metadata probe: it
  shows the file info and **opens the preview immediately**, then indexes keyframes in a
  **cancellable background task**. A new `IsIndexingKeyframes` flag (with `KeyframesReady`) drives a
  non-blocking **"indexing…"** hint; a new load cancels the previous file's index (stale-guard), and a
  cut placed while indexing awaits the same in-flight scan so it still snaps correctly (never to an
  empty list). Separately, `MediaProbe.GetKeyframesAsync` now scans keyframes at the **demux (packet)
  layer** (`-show_packets`, keeping `K`-flag packets) instead of decoding frames (**~3.86× faster**;
  4K: 216ms→56ms), with the previous `-skip_frame nokey` frame scan kept as a **fallback** when the
  packet query is empty or throws. Same sorted-distinct keyframes, per-file cache, snapping, and GOP
  behavior.
- **4K preview performance (G-005)** — the FFME preview now uses **hardware-accelerated decoding**
  (D3D11VA / DXVA2 / …) plus a **downscaled preview surface** (`src/App/Media/PreviewScale.cs`, capped
  at ~1080p, aspect-preserving, even dimensions) so large 4K sources play back smoothly without
  saturating the WPF UI thread. The **cut is unaffected** — it stays `-c copy` and is never decoded,
  so it always runs at the source's full resolution.

### Removed

- **Auto-detect (G-005)** — the black/white/scene **auto-detect** feature has been **removed**: no
  more `Core/Detect` layer, `SplitPointDetector`, detect passes, or candidate UI (candidate ticks /
  ranked candidate list). Manual cut markers, playhead-capture, and timeline-click cuts remain the
  ways to place cuts.

### Fixed

- **Cut markers appear instantly (G-012)** — placing a cut (Set-cut-at-playhead, manual add, or a
  timeline click) now drops the marker **immediately**, even while the background keyframe index is
  still running, instead of waiting for the scan. The optimistic marker shows a **"snapping…"** hint
  (`CutMarkerViewModel.IsSnapPending`) and resolves in place to its nearest keyframe once the index
  arrives (re-deduping on the final snapped time). When keyframes are already present the cut still
  snaps synchronously as before.
- **Per-segment cut end boundary (G-015)** — the subset export path extracts each selected part with an
  explicit `-to == snapped end`, while the plan's final part omits `-to` to run to end of file — so a
  selected middle part gets the same boundary the segment muxer would have produced.
- **Non-ASCII / unicode paths (G-010)** — files with non-ASCII paths (e.g. Japanese characters) now
  work end-to-end. Both `FfmpegRunner` and `FfprobeRunner` decode the child process' stdout/stderr as
  **UTF-8** (`StandardOutputEncoding` / `StandardErrorEncoding = UTF8`) regardless of the Windows
  console codepage, so unicode paths in the ffprobe JSON and in error output survive intact instead of
  becoming mojibake, and error text shows the real characters.
- **`.ts` / mpegts split failure (G-010)** — splitting an `.ts` (mpegts) file (which previously failed
  with exit `-28`) now works. The failure was the mangled-path symptom of the encoding issue above and
  is resolved by the UTF-8 fix. Relatedly, an out-of-space write (exit `-28` / `ENOSPC`) is now mapped
  to a clear **"not enough space to write the output"** (DiskFull) error — instead of surfacing an
  unrelated benign mpegts warning as the headline — and `SplitEngine` runs a best-effort **pre-flight
  free-space check** so an obviously-too-small output drive fails early with that friendly message.
- **Scrub pop-back (G-009)** — dragging the scrub slider (or using skip / frame-step / jump) now
  lands the playhead **at the position you chose and holds it there**, paused or playing. Previously a
  stale `PositionChanged` echo arriving during FFME's async seek (or ongoing playback) would yank the
  slider back to where playback actually was. `PlayerViewModel` now arms a **seek-target hold** on
  every user seek and ignores off-target echoes until the seek settles — cleared deterministically by a
  new `IMediaPlayer.Seeked` completion event, with a ~250ms tolerance and a bounded-tick anti-freeze
  backstop so the slider can never get stuck. The scrub slider also suppresses echoes while the thumb
  is being dragged (seek on release).
- **Responsive scrub (G-016)** — the video now follows the pin **live while you drag** it, instead of
  staying frozen until release. Seeks are **coalesced** (only one seek is in flight at a time; while it
  runs, only the *latest* pin position is kept and issued on completion — stale intermediate positions
  are dropped) and **throttled** (~70ms), so a fast drag converges to where the pin is now with no
  backlog/lag. Routes through the same seek-target hold, so the pop-back protection above still holds.

### Notes

- The preview decodes through **FFmpeg** (via FFME) — the same bundled build the engine uses — so it
  plays what the app can cut. A file the player still cannot open shows a **"Preview unavailable —
  you can still cut this file"** banner and remains fully cuttable — preview failure is not a load
  failure.

## [0.1.0] - 2026-07-15

First end-to-end release (goal G-001): a working Windows split/join app with a bundled FFmpeg and
a packaged distributable. Every operation is lossless stream-copy (`-c copy`) — no re-encode.

### Added

- **Split screen** — load a video, place cut markers manually or via auto-detect, and extract
  contiguous segments in a single stream-copy pass via FFmpeg's segment muxer. Cuts snap to the
  nearest keyframe; each segment reports its actual snapped boundary and signed delta. Coarse-GOP
  files raise a warning that cuts may move noticeably. Configurable output directory and segment
  naming pattern; overwrite protection.
- **Auto-detect split points** — three decode-only passes (black via `blackdetect`, white via
  `negate,blackdetect`, hard scene cuts via `select=gt(scene),metadata=print`), merged and returned
  as keyframe-snapped, ranked candidates. Never writes a file, never re-encodes.
- **Join screen** — gather and reorder clips, with a live compatibility verdict. A compat pre-flight
  compares codec, resolution, pixel format, time base, and audio layout against the first clip;
  incompatible sets are **refused with a named reason and no output written**. Compatible clips are
  joined via the stream-copy concat demuxer.
- **UI-free Core library** — `MediaProbe` (duration/streams/codecs, cached keyframe index,
  nearest-keyframe snapping, average GOP), `SplitEngine`, `JoinEngine` + `CompatChecker`,
  `SplitPointDetector`.
- **Single FFmpeg choke-point** — `FfmpegRunner` / `FfprobeRunner` (all execution flows through here;
  kill-tree cancel; never throws on non-zero exit), `FfmpegBinaryLocator` (explicit override →
  app-local `ffmpeg/` folder → PATH), and a typed `ArgumentList`-based `FfmpegArgs` builder.
- **No-re-encode invariant** enforced structurally, at runtime, and by tests for both split and join;
  detection enforced decode-only the same way.
- **Friendly errors** — `FfmpegErrorMapper` categorizes raw FFmpeg stderr into user-facing messages
  with hints, always preserving the raw output for a details expander. Shared progress / cancel /
  error handling via `OperationViewModel`.
- **Packaging** — `packaging/package.ps1` produces a single-file, self-contained win-x64 publish,
  bundles `ffmpeg.exe` / `ffprobe.exe` into an app-local `ffmpeg/` folder, includes license notices,
  and zips a versioned distributable. `THIRD-PARTY-NOTICES.md` documents FFmpeg attribution and flags
  that the bundled gyan.dev "essentials" build is GPL (swap to an LGPL build before public release).

### Known limitations

- Cuts are keyframe-accurate, not frame-exact — the deliberate trade-off for zero re-encode.
- Join refuses incompatible clip sets rather than re-encoding to reconcile them (no re-encode in v1).

[0.2.0]: https://keepachangelog.com/
[0.1.0]: https://keepachangelog.com/
