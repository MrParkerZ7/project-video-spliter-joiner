# VideoSplitJoiner — User Guide

VideoSplitJoiner has two screens, reached from the tabs at the top of the window: **Split** and
**Join**. Both perform lossless, no-re-encode operations, so they finish quickly and never
degrade quality. This guide walks through each feature.

- [Getting started](#getting-started)
- [Drag and drop](#drag-and-drop)
- [Split a video](#split-a-video)
- [Preview & pick cuts from the player](#preview--pick-cuts-from-the-player)
- [Join clips](#join-clips)
- [Progress, cancel, and errors](#progress-cancel-and-errors)

## Getting started

Launch `VideoSplitJoiner.App.exe`. FFmpeg ships bundled in the `ffmpeg/` folder next to the app, so
there is nothing to install. The window opens on the **Split** tab; switch to **Join** with the tab
header.

## Drag and drop

Besides the **Load…** / **Add files…** buttons, you can drag video files straight from File Explorer
onto either screen:

- **Onto the Split screen** — drop one or more video files; the app **loads the first one** (Split
  works on a single file). Dropping is the same as using **Load…**.
- **Onto the Join screen** — drop one or more video files and **all of them are added**, in the order
  they were dropped, then the compatibility verdict re-checks. Dropping is the same as **Add files…**.
- **Reorder Join clips by dragging** — drag a clip row up or down within the list to reorder it. This
  has the **same effect as the Up / Down buttons** (order is significant for the join). Dragging a row
  onto empty space below the list moves it to the end.

A drop zone highlights while you drag a valid file over a screen. **Non-video files are ignored** —
only recognised video extensions (`.mp4`, `.mkv`, `.mov`, `.avi`, `.m4v`, `.webm`, `.ts`, `.mpg`,
`.mpeg`, `.wmv`, `.flv`) are accepted; anything else in the drop is silently dropped.

## Split a video

The Split screen cuts one video into several contiguous segments at the cut points you choose.

1. **Load a video.** Choose your input file, or **drag a video onto the screen** (see
   [Drag and drop](#drag-and-drop)). Loading is **snappy**: as soon as the fast metadata probe
   succeeds, the file's info appears and the **preview opens immediately** — you don't wait on a full
   keyframe scan. The keyframe index (which cuts snap to) then builds **in the background**; while it
   runs, an **"indexing…"** hint shows. Placing a cut is ready once indexing finishes — and if you're
   very fast, a cut placed mid-index simply **waits briefly** for the scan to complete so it still
   snaps correctly (never to an empty list). If the file cannot be read, you get a friendly error and
   the screen stays unloaded.
2. **Add cut markers.** Add a marker at a time position, or use the
   [preview player](#preview--pick-cuts-from-the-player) to find the exact frame and drop a cut
   there. Each marker row shows the snap: `requested → snapped (±delta)` — for example
   `01:23.4 → 01:22.0 (−1.4s)`. That tells you exactly how far the cut moved to land on a keyframe.
3. **Choose output.** Set the output directory and, optionally, the segment **naming pattern**. The
   app **remembers your last folders**: the output directory defaults to the folder you last output
   to (and, until you've set one, to the input file's folder), and the **Load…** file picker reopens
   at your last-used input folder. These preferences persist across runs (stored in
   `%APPDATA%/VideoSplitJoiner/settings.json`). The default pattern is `{name}_part{index:00}{ext}`
   (e.g. `holiday_part01.mp4`, `holiday_part02.mp4`, …). Tokens: `{name}` = input name without
   extension, `{ext}` = input extension (with dot), `{index}` = 1-based segment number (supports a
   zero-pad form like `{index:00}`). Tick **Overwrite** to replace existing files; otherwise a run
   that would clobber an output is rejected before anything happens.
4. **Run.** The split runs as a single stream-copy pass. When it finishes, the results panel lists
   each produced segment with its actual snapped boundary and signed delta. Any non-fatal
   adjustments (a cut dropped for being out of bounds, near-duplicate cuts merged, a large snap on a
   coarse-GOP file) are reported as warnings.

### Why cuts snap to keyframes

Stream copy (`-c copy`) is lossless and near-instant precisely because it copies existing encoded
frames rather than re-encoding — but a copyable cut can only happen at a **keyframe**. So every
requested cut is moved to the nearest keyframe before extraction (ties resolve to the *earlier*
keyframe).

On most files keyframes are close together and the movement is tiny. On a source with a **coarse
GOP** (keyframes seconds apart), a cut can move by seconds. The app does not hide this:

- Once the background keyframe index finishes, if the mean keyframe spacing is coarse a **warning**
  appears telling you roughly how far cuts may move.
- Every marker shows its own `±delta`, so you always see the exact snapped result before running.

This is the deliberate trade-off for zero re-encode — it is not frame-exact editing.

## Preview & pick cuts from the player

Loading a file on the Split screen also opens it in an **in-app preview player** right there, so you
can watch the video and pick cut points visually instead of typing times by hand.

1. **Load a video** (as above). The first frame appears in the player and the transport enables once
   the duration is known.
2. **Play, pause, stop, and scrub.** Use the **Play / Pause** and **Stop** buttons, or drag the
   slider to **scrub** to any position. A `mm:ss.f / mm:ss.f` readout shows the current playhead
   and total duration. (Stop rewinds to the start; Play resumes from where you paused.)
3. **Land the exact split point with the player controls.** A row of jog buttons lets you home in on
   the precise frame before you cut:
   - **Skip** — jump the playhead by a fixed amount: **±1s, ±5s, ±10s, ±20s, ±1m, ±5m, ±10m, ±20m**
     (the minus buttons go back, the plus buttons go forward). The ±10m / ±20m jumps make it quick to
     traverse a long recording before homing in with the smaller skips.
   - **Frame-step** — nudge **±1 frame** (`⏴` / `⏵`) to settle on the exact frame; the video pauses
     for a clean single-frame move.
   - **Jump to start / end** — snap the playhead to `00:00` or to the very end of the clip.
   - **Volume & mute** — a volume slider plus a mute toggle (muting keeps the slider level, so
     unmute restores it).
   - **Playback speed** — pick a speed from **0.25× to 2×** to scan quickly or inspect slowly.
4. **Set a cut at the playhead.** With the video parked where you want to cut, click
   **"Set cut point at playhead"** to drop a cut marker at the current position. That cut
   **keyframe-snaps exactly like any other marker** — the new marker shows its `requested → snapped
   (±delta)` just like a hand-placed one. Dropping a second cut that snaps to the same keyframe is a
   no-op (it de-dupes), so double-tapping the button won't create duplicate cuts.
5. **Read the timeline strip.** Under the player is a **timeline strip** spanning the whole clip. It
   shows the **playhead** (moves as the video plays / scrubs) and a **tick per cut marker**.
6. **Click the strip to cut; click a tick to seek.**
   - **Click anywhere on the strip** to drop a cut at that position — it routes through the same
     snap-and-dedupe path as every other cut.
   - **Click a marker tick** to seek the player to that cut's snapped time (so you can see exactly
     where it lands).

### Resizing the video pane

The preview video area is **drag-resizable**. Grab the splitter bar directly beneath the player and
drag it up or down to shrink or grow the video against the markers / output panel below — handy for
giving the picture more room while lining up a cut, or reclaiming space for a long marker list.

### When the preview can't play (but the cut still works)

The preview **decodes through FFmpeg** (the same bundled build the engine uses), so it plays the
formats the app can cut — HEVC, MKV, 4K, and other exotic container/codec combinations that Windows'
built-in codecs cannot. As a result the "preview unavailable" case is now **rare**. If a file still
fails to open in the player you get a banner — **"Preview unavailable — you can still cut this
file."** — with the underlying reason beneath it. This is **not** a load failure: all the cutting
features still work; just place your cuts by time instead of by watching.

## Join clips

The Join screen glues several clips head-to-tail into one file — but only when they are truly
concat-compatible, because v1 never re-encodes.

1. **Add clips.** Add the files you want to join, or **drag videos onto the screen** — all dropped
   videos are added in drop order (see [Drag and drop](#drag-and-drop)). Each gets an info chip
   (codec · resolution) from a quick probe. Duplicates are allowed.
2. **Reorder.** Move clips up/down with the buttons **or drag a clip row** to a new position —
   **order is significant**; the output plays them in list order.
3. **Read the compatibility verdict.** The banner re-checks on every change:
   - **Green** — "*N* clips ready to join" — the set is compatible.
   - **Red** — "Cannot join — …" — naming each mismatch (e.g. *clip 2 is 1280x720, reference
     (clip 1) is 1920x1080*). The first clip is the reference; each other clip's video (codec,
     width, height, pixel format, time base) and audio (codec, sample rate, channels) must match it.
4. **Run.** With ≥2 compatible clips and an output path set, run the join. It is a single
   stream-copy concat pass. Tick **Overwrite** to replace an existing output; otherwise a clobbering
   run is refused up front.

### Why an incompatible set is refused (not fixed)

Stream-copy concat requires the inputs to share the same encoding parameters — you cannot losslessly
staple a 720p clip onto a 1080p clip, or an H.264 clip onto an HEVC clip. Rather than silently
re-encode (which would defeat the whole no-loss, fast design) or emit a broken file, the app
**refuses and tells you exactly which clip and which field conflict**, so you can re-export or remove
the offending clip. A refusal writes **no output file**. Re-encoding to force compatibility is out of
scope for v1.

## Progress, cancel, and errors

Every long-running operation (split, join) shares the same experience:

- A **progress** bar tracks the run from 0 to 100%.
- **Cancel** stops the in-flight operation immediately (FFmpeg's whole process tree is killed).
  Cancelling a split or join removes any partially written output — you never end up with a
  half-written final file.
- On failure, you get a **friendly headline** (e.g. "Not enough space to write the output — free up
  space or choose another output folder.") plus an optional **hint**, and a **Details** view that
  reveals the FFmpeg output for troubleshooting. The raw output is always preserved — the app never
  surfaces a cryptic stderr string as the headline.
- **The error is selectable and copyable.** Click **Copy error** to put the whole error — headline,
  hint, full detail, and the saved log path — on the clipboard for a bug report. Click **Open log
  file** to reveal the saved log in Explorer.
- **A full log file is saved** for every failed split/join at
  `%LOCALAPPDATA%/VideoSplitJoiner/logs/<operation>-<timestamp>.log`. It contains the complete FFmpeg
  stderr (not just the tail) plus the exact command, the exit code, and a UTC timestamp — everything
  needed to diagnose or report the failure. (Log writing is best-effort and never blocks the app.)
- **Out-of-space writes are named clearly.** If the output drive runs out of room, you get the "not
  enough space" message above rather than a confusing FFmpeg warning, and the app tries to catch an
  obviously-too-small output drive up front before the run even starts.

### Unicode paths and `.ts` files

Files with **non-ASCII paths** (for example Japanese filenames or folders) work end-to-end for both
split and join, and any error text shows the real characters rather than garbled ones. **`.ts`
(mpegts) files** split correctly too. If you ever see an error, the copyable full log will contain
the exact path and output for troubleshooting.
