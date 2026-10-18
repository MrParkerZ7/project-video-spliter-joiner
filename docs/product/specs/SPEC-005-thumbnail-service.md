---
id: SPEC-005
slug: thumbnail-service
area: core
title: Thumbnail service
status: current
sources:
  - src/Core/Thumbnails/IThumbnailService.cs
  - src/Core/Thumbnails/FfmpegThumbnailService.cs
serves-goal: [G-030, G-058, G-059]
updated: 2026-10-05
---

## What
The thumbnail service extracts a single video frame at (or near) a requested time to a temp JPG file via the ffmpeg CLI, for the scrub-bar hover preview (G-030). `GetThumbnailAsync(inputPath, time, width, ct)` returns the temp `.jpg` **path** on success (never an image object — Core stays UI-free; the App layer loads the path itself, T-078) and `null` on any failure. It uses a **fast, frame-accurate input seek** (`-ss` before `-i`: ffmpeg jumps to the keyframe before the time and decodes to the time itself — measured on MP4, MOV and MKV; an MPEG-TS source can land on the next keyframe) that **keeps ffmpeg's stream-info pass on every input**, MP4/MOV included, so `-ss` counts from the file's format start time (T-195, I27), a **bounded LRU cache keyed by time bucket and width** so a repeat request for the same second at the same width reuses the file without re-running ffmpeg — and the same second at another width is another file (T-179), and **end-to-end cancellation** so a superseded request resolves to `null` and never clobbers a newer result. Everything is **best-effort**: no public method ever throws. `Clear(inputPath)` and `ClearAll()` drop cache entries and delete temp files. The concrete implementation is `FfmpegThumbnailService`.

## Why
A scrub-bar hover preview must feel instant and must never destabilize the UI. Hovering across the bar could otherwise spawn one ffmpeg process per pixel, block the UI thread, or crash on a bad frame. This service makes previews cheap (an input-seek grab), idempotent within a time bucket (LRU cache, bounded so the temp tree can't grow without limit), and safe (best-effort `null` on any failure, honored cancellation for superseded hovers). Temp images live under a per-user cache root that is injectable so tests never touch the real folder or a real ffmpeg binary.

## Scope
**In:** the Core `IThumbnailService` contract and its `FfmpegThumbnailService` implementation — frame extraction, ffmpeg arg construction, time bucketing, the temp-path layout, the in-memory LRU cache + eviction, per-call cancellation, best-effort null/no-op semantics, and the `Clear`/`ClearAll` cache-cleanup methods.
**Out:** the App-layer hover UI and its debounce + latest-wins request coalescing (`ThumbnailPreviewViewModel`, T-078 — an adjacent App spec); the ffmpeg process runner itself (`IFfmpegRunner` / T-002); loading the returned path into a `BitmapImage`; and the actual ffmpeg binary's frame-accuracy behavior.

## Current behavior & invariants
Grounded in `FfmpegThumbnailService` (`GetThumbnailAsync`, `BuildArgs`, `FloorToBucket`, `ResolveTempPath`, the LRU plumbing, `Clear`/`ClearAll`) and the `IThumbnailService` contract.

- **I1** — On success `GetThumbnailAsync` returns a non-null temp path ending in `.jpg` (the resolved `tempPath`, `GetThumbnailAsync` lines 157-158; `ResolveTempPath` appends `.jpg`).
- **I2** — The ffmpeg command is a fast input-seek: token `-ss <secs>` appears **before** `-i <input>`, followed by `-frames:v 1`, `-vf scale=<width>:-1`, `-y`, and the output temp path (`BuildArgs`, lines 215-222). Every input gets exactly these tokens — MP4/MOV included, never `-nofind_stream_info` (I27).
- **I3** — The requested `time` is floored to the bucket granularity (default 1s) and that same bucket drives **both** the `-ss` seek value and the temp-file name, which also carries the requested width (`FloorToBucket` + `BuildArgs`/`ResolveTempPath`; e.g. 7.85s @ 1s bucket at width 160 → seek `7`, file `7000_w160.jpg`).
- **I4** — A non-positive `time` (≤ `TimeSpan.Zero`) floors to bucket 0 → seek `0`, file `0_w<width>.jpg` (`FloorToBucket` lines 227-230).
- **I5** — The `-ss` value is the bucketed timestamp formatted as invariant-culture seconds via `"0.######"` (`FormatSeconds`, lines 237-238).
- **I6** — The temp path is `<cacheRoot>/<hash>/<bucketMs>_w<width>.jpg`, where `<hash>` is the first 16 bytes of SHA-256(inputPath) as lowercase hex, `<bucketMs>` is the bucket's total milliseconds and `<width>` the requested width (`InputCacheDir` + `HashInput` + `ResolveTempPath` + `BucketName`).
- **I7** — A second request for the **same** (input, bucket, width) returns the cached path **without** re-running ffmpeg (runner invoked once for two same-bucket, same-width calls) (cache-hit branch; `TryGetCached`).
- **I8** — Requests for **distinct** buckets, or for **distinct widths** in one bucket, each run ffmpeg — no cache entry is shared across buckets or across widths (the `cacheKey` is `<input>|<bucketMs>|w<width>`, `BuildCacheKey`).
- **I9** — The in-memory cache is LRU-bounded to `maxEntries` (default 128); when the cap is exceeded the least-recently-used entry is evicted **and its temp file is deleted from disk** (`Remember` eviction loop, lines 307-312).
- **I10** — A bucket whose entry was LRU-evicted is re-extracted (ffmpeg re-runs) on its next request (eviction removes it from `_index`, so the next call misses; lines 307-312 + cache-miss path).
- **I11** — A tracked cache entry is only treated as a hit if its file **still exists on disk**; a tracked path whose file was removed externally falls through to re-extraction (`File.Exists(cachedPath)` guard, line 123).
- **I12** — A temp file left on disk by a prior process (present on disk but not in the in-memory cache) is reused **without** running ffmpeg and is re-tracked in the cache — **only at its own width**, because the width is in the file name, so a file another width left behind is never handed back (`File.Exists(tempPath)` reuse branch).
- **I13** — An already-cancelled token short-circuits: `GetThumbnailAsync` returns `null` and **never launches ffmpeg** (early `ct.ThrowIfCancellationRequested()`, line 117; runner not called).
- **I14** — Cancellation while the runner is in flight resolves to `null` and never throws (the `OperationCanceledException` catch, lines 160-164).
- **I15** — An ffmpeg failure (runner `Success == false`) returns `null` and is **not cached**, so a retry re-runs ffmpeg (lines 152-155; failure never reaches `Remember`).
- **I16** — A runner that reports success but produces no output file returns `null` (post-run `File.Exists(tempPath)` check, line 152).
- **I17** — An empty/whitespace `inputPath` returns `null` and never launches ffmpeg (guard line 112).
- **I18** — A non-positive `width` (≤ 0) returns `null` and never launches ffmpeg (guard line 112).
- **I19** — Best-effort throughout: any other failure (I/O, security, locator-missing) is swallowed and resolves to `null` — `GetThumbnailAsync` never throws (catch-all, lines 165-169).
- **I20** — `Clear(inputPath)` deletes that input's cache dir **and** drops its in-memory entries, so a subsequent same-bucket request re-runs ffmpeg (`ForgetUnder` + `TryDeleteDirectory`, lines 173-190).
- **I21** — `Clear` on a missing dir or an empty/whitespace path is a no-op and never throws (guard line 177 + best-effort catch, lines 186-189).
- **I22** — `ClearAll()` clears the in-memory cache and deletes the entire cache root dir, and never throws (lines 193-209).
- **I23** — The constructor rejects a null `runner` or null `cacheRoot` with `ArgumentNullException` (lines 81-82).
- **I24** — A non-positive `bucketGranularity` falls back to the 1s default, and a non-positive `maxEntries` falls back to the 128 default (constructor normalization, lines 84-86).
- **I25** — `DefaultCacheRoot()` resolves to `%LOCALAPPDATA%/VideoSplitJoiner/thumb-cache`, falling back to the OS temp folder when LocalApplicationData cannot be resolved (lines 96-105).
- **I26** — Two widths at one second run ffmpeg **twice** and return **two files**, each really that many pixels wide: a 640 request is never handed the 64px frame a row chip cached for the same second, in this process or from a file an earlier run left on disk (T-179 / G-058 — the defect that made "640px" profile pictures come back 64px wide). Files written before T-179 (`<bucketMs>.jpg`, no width) are never matched again and go with `Clear`/`ClearAll`.
- **I27** — The grab **keeps ffmpeg's stream-info pass on every input**: no `-nofind_stream_info`, for a `.mp4`, `.m4v` or `.mov` input too (`BuildArgs`, lines 215-222), so an input `-ss` counts from the file's format start time and the picture is the frame at the requested time from that start. Skipping the pass — which the waveform does on MP4/MOV (SPEC-006 I3) — also leaves ffmpeg without the format start time, so `-ss` would count from timestamp 0, and on an MP4/MOV whose start time is not 0 the grab exits 0 with another frame, which no retry can see. The first part of the app's own lossless split (`SplitArgsBuilder.SegmentMuxer`) of an H.264 B-frame + AAC MP4 is such a file: it starts at 0.043 s, and a skipping grab came back one frame early at 1 s and 3 s; on an `-output_ts_offset 10` file it came back as the first frame (measured at T-195's review, bundled ffmpeg 7.1). Letting the grab skip the pass only when the probed format start time is exactly 0 is a follow-up ticket (it needs T-219's `MediaInfo.StartTime`) (T-195 / G-059).

## Links
- Design: —
- Goals: G-030 (hover-thumbnail preview on the scrub bar) · G-058 (a profile picture is really stored at the width it asks for — T-179) · G-059 (the grab keeps the stream-info pass, I27 — T-195)
- Related specs: the App-layer `ThumbnailPreviewViewModel` hover/debounce/latest-wins spec (T-078); the ffmpeg-runner spec (T-002)
- Key code: `src/Core/Thumbnails/IThumbnailService.cs`, `src/Core/Thumbnails/FfmpegThumbnailService.cs`
- Tests: `tests/Core.Tests/FfmpegThumbnailServiceTests.cs` · `tests/Core.Tests/FfmpegThumbnailServiceIntegrationTests.cs` (real ffmpeg: the widths read from the JPEG headers) · `tests/Core.Tests/StreamInfoSkipTests.cs` (I27: the grab tokens and runs carry no `-nofind_stream_info`) · `tests/Core.Tests/StreamInfoSkipIntegrationTests.cs` (I27, real ffmpeg: on the app's own first split part and an offset MOV, both starting after 0, the service's grab decodes to today's picture)
