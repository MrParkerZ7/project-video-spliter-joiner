# VideoSplitJoiner — User Guide

VideoSplitJoiner has two screens, reached from the tabs at the top of the window: **Split** and
**Join**. Both perform lossless, no-re-encode operations, so they finish quickly and never
degrade quality. This guide walks through each feature.

- [Getting started](#getting-started)
- [Split a video](#split-a-video)
- [Preview & pick cuts from the player](#preview--pick-cuts-from-the-player)
- [Auto-detect split points](#auto-detect-split-points)
- [Join clips](#join-clips)
- [Progress, cancel, and errors](#progress-cancel-and-errors)

## Getting started

Launch `VideoSplitJoiner.App.exe`. FFmpeg ships bundled in the `ffmpeg/` folder next to the app, so
there is nothing to install. The window opens on the **Split** tab; switch to **Join** with the tab
header.

## Split a video

The Split screen cuts one video into several contiguous segments at the cut points you choose.

1. **Load a video.** Choose your input file. The app probes it for duration, streams, and its
   keyframe positions. If the file cannot be read, you get a friendly error and the screen stays
   unloaded.
2. **Add cut markers.** Add a marker at a time position, or use **Auto-detect** (below) to place
   candidates automatically. Each marker row shows the snap: `requested → snapped (±delta)` — for
   example `01:23.4 → 01:22.0 (−1.4s)`. That tells you exactly how far the cut moved to land on a
   keyframe.
3. **Choose output.** Set the output directory (it defaults to the input file's folder) and,
   optionally, the segment **naming pattern**. The default pattern is `{name}_part{index:00}{ext}`
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

- After loading, if the mean keyframe spacing is coarse, a **warning** appears telling you roughly
  how far cuts may move.
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
3. **Set a cut at the playhead.** With the video parked where you want to cut, click
   **"Set cut point at playhead"** to drop a cut marker at the current position. That cut
   **keyframe-snaps exactly like any other marker** — the new marker shows its `requested → snapped
   (±delta)` just like a hand-placed one. Dropping a second cut that snaps to the same keyframe is a
   no-op (it de-dupes), so double-tapping the button won't create duplicate cuts.
4. **Read the timeline strip.** Under the player is a **timeline strip** spanning the whole clip. It
   shows:
   - the **playhead** (moves as the video plays / scrubs),
   - a **tick per cut marker**, and
   - a **tick per detected candidate**, coloured by kind — **black**, **white**, and **scene** (see
     [Auto-detect](#auto-detect-split-points)). A small legend beneath the strip names each colour.
5. **Click the strip to cut; click a tick to seek.**
   - **Click anywhere on the strip** to drop a cut at that position — it routes through the same
     snap-and-dedupe path as every other cut.
   - **Click a marker tick** to seek the player to that cut's snapped time (so you can see exactly
     where it lands).
   - **Click a candidate tick** to preview it — the player seeks to the candidate's detected time so
     you can judge it before adding it as a marker.

### When the preview can't play (but the cut still works)

The player uses Windows' built-in Media Foundation codecs, whose coverage is narrower than FFmpeg's.
Some exotic containers or codecs may **fail to preview** even though the file is perfectly cuttable.
When that happens you get a banner — **"Preview unavailable — you can still cut this file."** — with
the underlying reason beneath it. This is **not** a load failure: all the cutting features still work.
You just won't get in-app playback for that file, so place your cuts by time / auto-detect instead.

## Auto-detect split points

Instead of placing markers by hand, let the app find natural boundaries. Auto-detect runs up to
three **decode-only** passes over the loaded file (it never writes a file and never re-encodes):

- **Black** — fade-to-black / gap intervals (FFmpeg `blackdetect`).
- **White** — fade-to-white / flash intervals (the signal is negated, then `blackdetect` is reused).
- **Scene** — hard scene cuts, where the frame-to-frame scene score exceeds a threshold.

Hits are merged (near-duplicates within a small window collapse into one), each is snapped to the
nearest keyframe, and the results come back as **ranked candidates** (rank 1 = highest confidence).
Review them, tick the ones you want, and add them as markers — each added candidate becomes a cut
marker at its detected time and re-snaps to its keyframe.

Notes:

- An **empty result is normal**, not an error — a busy clip may simply have no black/white/scene
  events. You will see a "no candidates" status; add markers manually instead.
- Detection **may over-detect** on busy footage. The candidates are suggestions you accept or
  adjust, not a final cut list.

## Join clips

The Join screen glues several clips head-to-tail into one file — but only when they are truly
concat-compatible, because v1 never re-encodes.

1. **Add clips.** Add the files you want to join. Each gets an info chip (codec · resolution) from a
   quick probe. Duplicates are allowed.
2. **Reorder.** Move clips up/down — **order is significant**; the output plays them in list order.
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

Every long-running operation (split, join, detect) shares the same experience:

- A **progress** bar tracks the run from 0 to 100%.
- **Cancel** stops the in-flight operation immediately (FFmpeg's whole process tree is killed).
  Cancelling a split or join removes any partially written output — you never end up with a
  half-written final file.
- On failure, you get a **friendly headline** (e.g. "The disk ran out of space while writing the
  output.") plus an optional **hint**, and a **Details** expander that reveals the raw FFmpeg output
  for troubleshooting. The raw output is always preserved — the app never surfaces a cryptic stderr
  string as the headline.
