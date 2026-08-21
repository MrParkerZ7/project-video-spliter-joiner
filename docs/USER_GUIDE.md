# VideoSplitJoiner — User Guide

VideoSplitJoiner has three screens, reached from the tabs at the top of the window: **Split**,
**Join**, and **Bulk Cut**. All perform lossless, no-re-encode operations, so they finish quickly and
never degrade quality. This guide walks through each feature.

- [Getting started](#getting-started)
- [Screen layout](#screen-layout)
- [Drag and drop](#drag-and-drop)
- [Split a video](#split-a-video)
- [Preview & pick cuts from the player](#preview--pick-cuts-from-the-player)
- [Join clips](#join-clips)
- [Bulk Cut](#bulk-cut)
- [Progress, cancel, and errors](#progress-cancel-and-errors)

## Getting started

Launch `VideoSplitJoiner.App.exe`. FFmpeg ships bundled in the `ffmpeg/` folder next to the app, so
there is nothing to install. The window opens on the **Split** tab; switch to **Join** with the tab
header. The app wears a dark **IBM Plex** theme with a gold accent, its own dark title bar, and a
header carrying the "lossless · no re-encode" tagline.

## Screen layout

Both screens use a **two-column layout** behind a **draggable column splitter** — drag the divider
between the columns to trade space between them:

- **Left column — the visual pane.** On **Split**, this is the preview player and the timeline /
  scrubber beneath it. On **Join**, it is the ordered clip list. It flexes to fill the window.
- **Right column — the tool panel.** Everything else lives here: **Load…** / **Clear** at the top,
  then (on Split) the **file-info card** (`container · duration · size`), a gold **format badge**
  (e.g. `HEVC · MATROSKA`), the **Cut markers** and **Parts to export** sections, the mono
  **DIR / NAME** output fields, and the **Run** button. On Join the right panel holds the
  compatibility verdict, the **Estimated result** panel, output fields, and Run.

The right panel defaults to 360px wide and can be dragged within a 300–520px range. On the Split
screen the video area also has its own horizontal splitter (see
[Resizing the video pane](#resizing-the-video-pane)).

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
   runs, an **"indexing…"** hint shows. You can place cuts right away even while it runs — a cut
   dropped mid-index appears immediately (see step 2) and snaps as soon as the scan completes, never to
   an empty list. If the file cannot be read, you get a friendly error and the screen stays unloaded.
2. **Add cut markers.** Add a marker at a time position, or use the
   [preview player](#preview--pick-cuts-from-the-player) to find the exact frame and drop a cut
   there. **The marker appears instantly** — even if the background keyframe index is still building,
   the cut drops right away showing a transient **"snapping…"** hint, then resolves in place to its
   nearest keyframe the moment the index finishes (no waiting). Once resolved, each marker row shows
   the snap: `requested → snapped (±delta)` — for example `01:23.4 → 01:22.0 (−1.4s)`. That tells you
   exactly how far the cut moved to land on a keyframe. **The "Cut markers" list is ordered by time
   position, not the order you added them** — place a cut at 5:00, then one at 2:00, and the 2:00 row
   sits above the 5:00 row. A marker placed while the keyframe scan is still running settles into its
   correct time slot once its snap resolves, and removing a marker keeps the rest ordered.
3. **Pick which parts to export.** As soon as you have cut points, the screen lists the resulting
   parts as a checklist — each row reads like `Part 2 · 05:00–10:00 · 5:00` with its own checkbox, plus
   **All** / **None** buttons. Every part is checked by default. **Only the checked parts are written**;
   unchecked parts are simply never produced, so skipping parts of a long recording costs no extra time
   or disk. Selected parts keep their **original** part number in the filename (a chosen middle part
   stays `…_part02`), and the export is still lossless either way. The Run button reflects your choice
   — e.g. "Split 3 parts" when all are checked, "Split 2 of 3 parts" for a subset.
4. **Choose output.** Set the output directory and, optionally, the segment **naming pattern**. The
   app **remembers your last folders**: the output directory defaults to the folder you last output
   to (and, until you've set one, to the input file's folder), and the **Load…** file picker reopens
   at your last-used input folder. These preferences persist across runs (stored in
   `%APPDATA%/VideoSplitJoiner/settings.json`). The default pattern is `{name}_part{index:00}{ext}`
   (e.g. `holiday_part01.mp4`, `holiday_part02.mp4`, …). Tokens: `{name}` = input name without
   extension, `{ext}` = input extension (with dot), `{index}` = 1-based segment number (supports a
   zero-pad form like `{index:00}`). Tick **Overwrite** to replace existing files; otherwise a run
   that would clobber an output is rejected before anything happens.
5. **Run.** The split writes your selected parts (a full selection runs as a single stream-copy pass;
   a subset extracts only the chosen parts). While it runs you always see a **progress bar** (an
   animated busy bar until granular progress arrives, so it never looks stuck), a **stage** label
   (Preparing → Splitting → Finalizing → Done), and an **estimated time remaining** beside it. On top
   of the overall bar, **each row in "Parts to export" shows its own progress** — every part advances
   **Pending → Writing (a live gold fill) → Done (✓)** as it is written, so you see exactly which part
   is being produced right now rather than just one aggregate bar. Progress also surfaces **outside the
   window**: the **Windows taskbar button** fills as the run advances (green while running, an
   indeterminate pulse while preparing, red on failure, clearing when done), and because the taskbar
   button can't show text the **percent + ETA ride in the window title** — e.g.
   `Splitting 45% · ~1m 20s — Video Split / Join`, visible on taskbar hover and alt-tab. When it
   finishes, the results panel lists each produced segment with its actual snapped boundary and signed
   delta, and a **Completed** surface confirms the outcome (see
   [Progress, cancel, and errors](#progress-cancel-and-errors)). Any non-fatal adjustments (a cut
   dropped for being out of bounds, near-duplicate cuts merged, a large snap on a coarse-GOP file) are
   reported as warnings.

You can also press **Clear** at any time (when no split is running) to unload the file and reset the
screen — the preview goes blank and the markers, parts, and results are all cleared, ready for a new
file.

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
   - **Hover the scrub bar for a frame preview.** Move the cursor along the scrub bar (without
     clicking) and a small **thumbnail popup** of the frame at that time appears, following the
     cursor with an `mm:ss` label — so you can find a split point **by sight without moving the main
     player**. The preview is best-effort: it settles briefly before grabbing a frame (so a fast sweep
     stays smooth) and simply shows nothing if a particular frame can't be grabbed. Its temporary
     frame files are cleaned up automatically when you load a new file or Clear.
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

Press **Clear all** (when no join is running) to empty the clip list and reset the compatibility
verdict, ready to start a fresh set.

### Why an incompatible set is refused (not fixed)

Stream-copy concat requires the inputs to share the same encoding parameters — you cannot losslessly
staple a 720p clip onto a 1080p clip, or an H.264 clip onto an HEVC clip. Rather than silently
re-encode (which would defeat the whole no-loss, fast design) or emit a broken file, the app
**refuses and tells you exactly which clip and which field conflict**, so you can re-export or remove
the offending clip. A refusal writes **no output file**. Re-encoding to force compatibility is out of
scope for v1.

## Bulk Cut

The Bulk Cut screen **trims the intro — and optionally the outro — off many videos at once**, keeping
the middle of each. It's built for the repetitive case: a folder of episodes with the same opening
titles (and maybe the same end card) you want stripped in one pass. Every trim is the same lossless
stream-copy, keyframe-snapped cut as the Split screen — just applied to a whole list — and **your
original files are never touched**: each trim is written to a **new** `_trimmed` file beside its source.

1. **Add videos.** Click **Add videos…** or **drag video files onto the screen** (see
   [Drag and drop](#drag-and-drop)) — every dropped video is added, in a scrollable list, one row per
   file. The list **de-dupes by path**, so adding the same file twice makes only one row. Each row
   probes in the background for its duration and builds its keyframe index (which cuts snap to); a row
   that can't be read is flagged and left out of the run.
2. **Mark the cut points on each row.** Every row has its own **scrub bar with two handles**:
   - the **gold intro-end handle (`▸`)** marks where the intro ends — everything before it is dropped;
   - the optional **blue outro-start handle (`◂`)** marks where the outro begins — everything after it
     is dropped. Leave it off to **keep to the end of the file**.

   The **kept middle glows gold** between the two handles, and hovering the bar shows a frame preview so
   you can find the exact spot by sight. Both handles **keyframe-snap** on commit, and each shows its
   snapped time — the same `requested → snapped (±delta)` readout as the Split screen (see
   [Why cuts snap to keyframes](#why-cuts-snap-to-keyframes) — the snapped time is the real cut, shown
   per handle). A row whose handles leave nothing meaningful to keep is marked invalid and excluded.
3. **Apply cut points to all (optional).** Set one row the way you want, then **apply its cut points to
   every other row** so you don't mark each by hand. The intro-end copies as an absolute time from the
   start, but **the outro is measured from the *end* of each file** — so a set of episodes of *different
   lengths* still line up (you trim the same amount off every tail, not to the same absolute timestamp).
   If a copied cut doesn't fit a shorter video, that row is **flagged for you to fix**, never silently
   dropped.
4. **Run.** Click **Run bulk cut** (the button shows how many rows will run). The videos are trimmed
   **one after another**, with each row's progress plus an overall bar on the **Windows taskbar and the
   window title**. **One bad row never stops the batch** — it's recorded and the run moves on, and at the
   end any failures are offered for a **Retry failed** pass. **Cancel** stops before the next file: every
   already-finished trim is kept, and only the one in progress is rolled back (you never get a
   half-written file).
5. **Output.** Each trim is written as `<name>_trimmed<ext>` in the **same folder as its source** (e.g.
   `episode1.mkv` → `episode1_trimmed.mkv`). **The original is never modified.** If a `_trimmed` file
   already exists, the app **auto-suffixes** the new one (`_trimmed_2`, `_trimmed_3`, …) so nothing is
   ever clobbered — unless you tick **Overwrite**, which replaces the existing output in place. (Even
   then, the source file itself is never a write target.)

Press **Clear all** (when no run is in progress) to empty the list and start a fresh set.

## Progress, cancel, and errors

Every long-running operation (split, join, and a Bulk Cut batch) shares the same experience, and the
operation now has **four clearly distinct surfaces** — exactly one shows at a time, and it resets on the
next run, load, or Clear (no stale "done" lingering):

**Running** — while an operation is in flight you see:

- A **progress** bar tracking the run from 0 to 100%. Until granular progress arrives it shows as an
  **animated busy bar** rather than a frozen 0%, so a fast stream-copy run never looks stuck.
- A **stage label** showing what's happening now, synced to the real work — split: *Preparing →
  Splitting (N parts) → Finalizing → Done*; join: *Checking compatibility → Joining → Finalizing →
  Done*.
- An **estimated time remaining** beside the status ("~1m 20s left"), reading "estimating…"
  until there's enough progress to judge. It disappears when the run ends.
- The **Windows taskbar button** fills alongside the in-app bar (green while running, an
  indeterminate pulse while preparing, red on failure, clearing when done), and the **window title**
  carries the live percent + ETA (e.g. `Splitting 45% · ~1m 20s — …`) so you can track progress from
  the taskbar / alt-tab without the window focused. A split additionally shows **per-part progress**
  on the "Parts to export" rows (see [Run](#split-a-video), step 5).
- **Cancel** stops the in-flight operation immediately (FFmpeg's whole process tree is killed).

**Completed** — a successful run shows a **green ✓** with a one-line result summary — "Split into 3
parts" (or "Wrote 2 of 3 parts" for a subset) / "Joined 4 clips → joined.mkv" — plus an **Open
folder** button that reveals the output directory in Explorer.

**Cancelled** — a cancelled run shows a **muted note** (neutral, not error-red). Cancelling a split
or join removes any partially written output — you never end up with a half-written final file.

**Failed** — a failed run shows the **red error block** described next.

### When a run fails

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

### If the app hits an unexpected error

Beyond split/join failures, the app has a **global safety net** for unexpected errors. If something
goes wrong on the UI, the app **stays open** and shows a friendly dialog — *"The app hit an
unexpected error but stayed open."* — that names the **saved crash log** and **copies the full
details to your clipboard** so you can paste them straight into a bug report. Every crash (whether or
not it was recoverable) is logged best-effort to the same folder as operation logs,
`%LOCALAPPDATA%/VideoSplitJoiner/logs/`, so there is always a record to attach even if the dialog
didn't appear. Background hiccups (like a preview or keyframe scan faulting) are caught the same way
and never tear the app down.

### Unicode paths and `.ts` files

Files with **non-ASCII paths** (for example Japanese filenames or folders) work end-to-end for both
split and join, and any error text shows the real characters rather than garbled ones. **`.ts`
(mpegts) files** split correctly too. If you ever see an error, the copyable full log will contain
the exact path and output for troubleshooting.
