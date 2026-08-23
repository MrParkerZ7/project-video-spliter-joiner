# VideoSplitJoiner

A fast Windows desktop app for **splitting** and **joining** video files **without re-encoding**.
Because every operation is a lossless stream copy (`ffmpeg -c copy`), splits and joins finish in
seconds instead of minutes and never lose a frame of quality — the deliberate trade-off is that
cuts land on the nearest **keyframe**, not on an exact frame.

Built as a .NET 8 WPF app with a UI-free Core library that shells out to a bundled FFmpeg. The
in-app preview decodes through the **same bundled FFmpeg** (via FFME), so it plays exactly what the
split/join engine can cut. **Loading is snappy** — the preview opens as soon as the file is probed
and its keyframe index builds in the background (via a fast demux-level scan), so you're never
staring at a spinner before you can start.

The app wears a **premium dark + gold theme** with a custom title bar and a **two-column layout** —
a left visual column (preview player + timeline) beside a right tool panel (load, file info, cut
markers, parts, output, Run) — laid out with the bundled **IBM Plex** typeface. Long operations
report progress on the **Windows taskbar button** and in the window title, so a running split is
never a silent window.

## Features

- **Split** a video at one or more cut points. Each cut snaps to the nearest keyframe (so the
  copy is clean), and the app shows you exactly how far each cut moved: `requested → snapped (±delta)`.
  Cuts drop **instantly** (a brief "snapping…" hint resolves to the keyframe once the background index
  arrives — no waiting).
- **Export only the parts you want** — after setting cuts, the Split screen lists the resulting parts
  (`Part 2 · 05:00–10:00 · 5:00`) with checkboxes and All / None. Only the checked parts are written,
  so skipping parts of a long recording costs no time or disk, and the export stays lossless. The Run
  button reflects the selection ("Split 2 of 3 parts").
- **Two-column layout with an IBM Plex look** — both screens split into a **left visual column** (the
  preview player + timeline/scrubber) and a **right tool panel** (Load / Clear, file info, cut markers,
  parts-to-export, output, Run) behind a **draggable column splitter**. The app ships the **IBM Plex
  Mono / Sans** fonts and a token-driven **dark + gold** palette: an app header with a
  "lossless · no re-encode" tagline, a gold **format badge** (`HEVC · MATROSKA`), a Split **file-info
  card** (`container · duration · size`), and a Join **"Estimated result"** panel (total duration +
  approximate size).
- **Cut markers are ordered by time** — placing cuts out of order (a cut at 5:00, then one at 2:00)
  still reads the "Cut markers" list top-to-bottom in time order. A marker placed while the keyframe
  scan is still running settles into its correct time slot once its snap resolves.
- **Clear / Clear all** — reset the Split screen (unload the file, blank the preview) or empty the
  Join clip list with one button. Both are disabled while an operation is running so you can't wipe the
  workspace mid-op.
- **In-app video preview player + visual cut selection** — the loaded file plays right on the Split
  screen (play / pause / stop / **scrub**). Park the playhead and click **"Set cut point at
  playhead"**, or **click the timeline strip** under the player, to drop a cut visually; clicking a
  marker tick seeks the player there. Every visually placed cut keyframe-snaps like any other.
  The preview **decodes through FFmpeg** (via FFME), so it plays what the app can cut — including
  **HEVC, MKV, and other exotic containers/codecs** that Windows' built-in codecs cannot. A file
  that still fails to preview shows a "preview unavailable, cut still works" banner and stays fully
  cuttable, but with FFmpeg decoding that banner is now rare.
- **Hover-thumbnail scrubbing** — hover the player's scrub bar to see a small **frame preview at that
  time**, following the cursor with an `mm:ss` label, so you can find a split point by sight without
  moving the main playhead. Thumbnails are grabbed by a separate ffmpeg process and cached; a failed
  grab simply shows nothing and never blocks.
- **Full player controls** to land the exact split point — **skip** ±1s / ±5s / ±10s / ±20s / ±1m /
  ±5m / ±10m / ±20m, **frame-step** ±1 frame, **jump to start / end**, a **volume slider + mute**, and
  a **playback-speed** selector (0.25×–2×). The ±10m / ±20m skips make traversing long clips quick;
  nudge the playhead onto the precise frame, then "Set cut at playhead".
- **Resizable video pane** — drag the splitter under the preview to grow or shrink the video area
  against the markers / output panel below it.
- **4K support** — the preview uses hardware-accelerated decoding and a downscaled preview surface so
  large 4K sources play back smoothly, while the cut itself stays resolution-independent (`-c copy`
  is never decoded).
- **Join** compatible clips head-to-tail. A compatibility pre-flight compares codec, resolution,
  pixel format, time base, and audio layout; an incompatible set is **refused with a named reason**
  rather than producing a broken file. v1 never re-encodes to force a fit.
- **Drag and drop** — drag video files from Explorer onto the **Split** screen to load (the first
  file) or onto the **Join** screen to add them all in drop order, and **drag Join clips to reorder**
  them (same effect as the Up/Down buttons). Non-video files are ignored.
- Live **progress** with a **stage label** (Preparing → Splitting → Finalizing → Done for a split;
  Checking compatibility → Joining → Finalizing → Done for a join) and an **estimated time remaining**
  ("~1m 20s left") — the bar animates as a busy indicator until real progress arrives, so a run never
  looks silent or stuck.
- **Per-part split progress** — splitting into N parts advances each row in "Parts to export"
  **Pending → Writing (live %) → Done (✓)** as it's written, not just one overall bar (the active row
  shows a gold live-fill, completed rows a green ✓).
- **Taskbar-button progress + ETA in the title** — a running split/join shows a live progress fill on
  the **Windows taskbar button** (green while running, indeterminate while preparing, red on failure,
  clearing when done), and the **ETA + %** ride in the window title
  (`"Splitting 45% · ~1m 20s — Video Split / Join"`), visible on taskbar hover / alt-tab.
- **Clear operation outcomes** — the operation lifecycle now has four distinct surfaces so a finished
  run is never invisible: **Running** (gold bar + status + ETA + Cancel), **Completed** (green ✓ + a
  result line — "Split into 3 parts" / "Joined 4 clips → joined.mkv" — plus an **Open folder** button),
  **Cancelled** (a muted note, not red), and **Failed** (the red error block). Exactly one shows at a
  time and it resets on the next run / load / Clear.
- Friendly **error** messages plus **cancel**. Errors are **selectable** with a **Copy error** button
  and an **Open log file** button; the full FFmpeg output (command, exit code, timestamp, and complete
  stderr) is also saved to a per-run log under `%LOCALAPPDATA%/VideoSplitJoiner/logs/`. A failed write
  for lack of space reports a clear "not enough space" message rather than a cryptic FFmpeg warning.
- **Crash safety net** — an unexpected error no longer makes the window vanish silently. A recoverable
  UI-thread error shows a **friendly copyable dialog** (naming the saved log path, with the full detail
  copied to your clipboard) and the app **stays open**; background and last-ditch crashes are logged to
  `%LOCALAPPDATA%/VideoSplitJoiner/logs/` either way, so there is always a log to attach to a report.
- **Unicode paths and `.ts` files** — files with non-ASCII paths (e.g. Japanese) and `.ts` (mpegts)
  sources split and join correctly; process output is decoded as UTF-8 so paths and error text never
  garble.
- **Output lands next to the source** — the split output directory **defaults to the loaded file's
  folder** and **re-anchors on every new load** (drag or picker), so exports land beside the source by
  default. It stays fully editable for the one-off case; a manual change is discarded the next time you
  load a file.
- **Remembers your last input folder** — the input file picker reopens at your last-used input folder,
  persisted to `%APPDATA%/VideoSplitJoiner/settings.json`.

## Install & run (packaged release)

1. Download `VideoSplitJoiner-v<version>-win-x64.zip` from a release.
2. Unzip it anywhere.
3. Run `VideoSplitJoiner.App.exe`.

FFmpeg is **bundled** — a single ffmpeg **shared build** ships inside an `ffmpeg/` folder next to the
executable, so there is nothing else to install. That one folder serves **both** the preview (the
shared `avcodec-*` / `avformat-*` / … DLLs that FFME P/Invoke-loads) **and** the split/join engine
(`ffmpeg.exe` / `ffprobe.exe`). The app resolves the DLLs (via `Library.FFmpegDirectory`) and the
exes (via `FfmpegBinaryLocator`) from that folder automatically; see
[Architecture → binary resolution](docs/ARCHITECTURE.md#binary-resolution).

> The bundled FFmpeg in the developer distributable is a **GPL** build. See
> [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) before redistributing publicly — an LGPL build
> can be swapped in at package time.

## Build from source

The .NET 8 SDK in this environment lives at `D:\_env_storeage\dotnet` and is **not on PATH**, so
invoke it by full path (or add it to PATH yourself):

```powershell
# Dev setup: fetch the ffmpeg SHARED build (shared DLLs for the preview + ffmpeg/ffprobe exes for
# the engine) into ffmpeg-shared/. Needed to run the app with a working preview and to package.
powershell -File packaging/fetch-ffmpeg-shared.ps1

# Build
D:\_env_storeage\dotnet\dotnet.exe build VideoSplitJoiner.sln -c Release

# Test (integration tests self-skip when FFmpeg binaries are absent)
D:\_env_storeage\dotnet\dotnet.exe test VideoSplitJoiner.sln

# Produce the distributable zip (single-file self-contained win-x64 + bundled ffmpeg)
powershell -File packaging/package.ps1
```

`packaging/package.ps1` publishes a single-file, self-contained win-x64 build, copies the ffmpeg
**shared build** (shared `*.dll` + `ffmpeg.exe` / `ffprobe.exe`) into an app-local `ffmpeg/` folder,
bundles the license notices, and zips the result into `dist/`. It defaults to the `ffmpeg-shared/`
folder populated by `fetch-ffmpeg-shared.ps1`; point it at an alternate build with
`-FfmpegSource <path>`.

Running the app itself needs the ffmpeg shared build present — either bundled (as in the packaged
zip), placed in an `ffmpeg/` folder next to the built exe, or (in a dev tree) discoverable via the
repo-local `ffmpeg-shared/` folder. Without it the split/join engine cannot run and the preview
falls back to the "preview unavailable" banner.

## How it works / limitations

- **Keyframe-snap is intentional.** Stream-copy can only cut on a keyframe, so a requested cut is
  moved to the nearest keyframe before extraction. On a source with a coarse GOP (keyframes far
  apart) a cut can move by **seconds** — the app surfaces this both as a per-file warning after
  load and as the per-marker `±delta` so nothing is hidden. This is the price of zero re-encode and
  near-instant operation; it is not frame-exact editing.
- **Join refuses incompatible sets.** v1 will not re-encode to reconcile mismatched clips. If the
  clips differ in codec, resolution, pixel format, time base, or audio layout, the join is refused
  with the exact reason so you can fix the offending clip rather than getting a silently broken file.

## Documentation

- **[Docs portal](docs/README.md)** — the full documentation index (guides · architecture · ADRs · designs · specs · standards).
- [User Guide](docs/USER_GUIDE.md) — step-by-step for Split, the preview player, hover-thumbnail
  scrubbing, per-part / taskbar progress, operation outcomes, Join, and Bulk Cut.
- [Architecture](docs/ARCHITECTURE.md) — layering, engine contracts, binary resolution, MVVM shape,
  and the theming / two-column-layout / progress subsystems.
- [Architecture decisions (ADRs)](docs/adr/) — the "why" behind key choices (e.g.
  [FFME over MediaElement](docs/adr/0004-ffme-over-mediaelement.md)).
- [Feature specs](docs/specs/_index.md) — the living-spec layer (numbered invariants, `serves-spec:` test traceability).
- [Developer guide](docs/DEV.md) — build · test · conventions. [Glossary](docs/GLOSSARY.md) — domain terms.
  [Roadmap](docs/ROADMAP.md) — shipped + future.
- [Dev setup / build from source](#build-from-source) — the .NET 8 SDK path, the
  `packaging/fetch-ffmpeg-shared.ps1` bootstrap, and `packaging/package.ps1` for the distributable.
- [Changelog](CHANGELOG.md) — release history.
- [Third-party notices](THIRD-PARTY-NOTICES.md) — FFmpeg attribution + licensing caveat.

## FFmpeg attribution

This product uses **FFmpeg** (https://ffmpeg.org), bundled as external `ffmpeg.exe` / `ffprobe.exe`
processes. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full attribution and the
GPL/LGPL licensing note.
