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
  ±20s / ±1m / ±5m**), **frame-step** (±1 frame), **jump to start / end**, plus a **volume slider +
  mute** and a **playback-speed** selector (**0.25×–2×**), all on `PlayerViewModel` / `PlayerView`.
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
