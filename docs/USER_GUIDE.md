# VideoSplitJoiner — User Guide

VideoSplitJoiner has two screens, reached from the tabs at the top of the window: **Split** and
**Join**. Both perform lossless, no-re-encode operations, so they finish quickly and never
degrade quality. This guide walks through each feature.

- [Getting started](#getting-started)
- [Split a video](#split-a-video)
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
