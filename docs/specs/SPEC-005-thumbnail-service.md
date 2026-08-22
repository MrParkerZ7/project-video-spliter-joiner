---
id: SPEC-005
slug: thumbnail-service
area: core
title: Thumbnail service
status: current
sources:
  - src/Core/Thumbnails/IThumbnailService.cs
  - src/Core/Thumbnails/FfmpegThumbnailService.cs
serves-goal: [G-030]
updated: 2026-08-22
---

## What
The thumbnail service extracts a single video frame at (or near) a requested time to a temp JPG file via the ffmpeg CLI, for the scrub-bar hover preview (G-030). `GetThumbnailAsync(inputPath, time, width, ct)` returns the temp `.jpg` **path** on success (never an image object — Core stays UI-free; the App layer loads the path itself, T-078) and `null` on any failure. It uses a **fast, keyframe-accurate seek** (`-ss` before `-i`), a **bounded time-bucket LRU cache** so repeat hovers within the same time bucket reuse the file without re-running ffmpeg, and **end-to-end cancellation** so a superseded request resolves to `null` and never clobbers a newer result. Everything is **best-effort**: no public method ever throws. `Clear(inputPath)` and `ClearAll()` drop cache entries and delete temp files. The concrete implementation is `FfmpegThumbnailService`.

## Why
A scrub-bar hover preview must feel instant and must never destabilize the UI. Hovering across the bar could otherwise spawn one ffmpeg process per pixel, block the UI thread, or crash on a bad frame. This service makes previews cheap (input-seek keyframe grab), idempotent within a time bucket (LRU cache, bounded so the temp tree can't grow without limit), and safe (best-effort `null` on any failure, honored cancellation for superseded hovers). Temp images live under a per-user cache root that is injectable so tests never touch the real folder or a real ffmpeg binary.

## Scope
**In:** the Core `IThumbnailService` contract and its `FfmpegThumbnailService` implementation — frame extraction, ffmpeg arg construction, time bucketing, the temp-path layout, the in-memory LRU cache + eviction, per-call cancellation, best-effort null/no-op semantics, and the `Clear`/`ClearAll` cache-cleanup methods.
**Out:** the App-layer hover UI and its debounce + latest-wins request coalescing (`ThumbnailPreviewViewModel`, T-078 — an adjacent App spec); the ffmpeg process runner itself (`IFfmpegRunner` / T-002); loading the returned path into a `BitmapImage`; and the actual ffmpeg binary's frame-accuracy behavior.

## Current behavior & invariants
Grounded in `FfmpegThumbnailService` (`GetThumbnailAsync`, `BuildArgs`, `FloorToBucket`, `ResolveTempPath`, the LRU plumbing, `Clear`/`ClearAll`) and the `IThumbnailService` contract.

- **I1** — On success `GetThumbnailAsync` returns a non-null temp path ending in `.jpg` (the resolved `tempPath`, `GetThumbnailAsync` lines 147-148; `ResolveTempPath` appends `.jpg`).
- **I2** — The ffmpeg command is a fast input-seek: token `-ss <secs>` appears **before** `-i <input>`, followed by `-frames:v 1`, `-vf scale=<width>:-1`, `-y`, and the output temp path (`BuildArgs`, lines 205-212).
- **I3** — The requested `time` is floored to the bucket granularity (default 1s) and that same bucket drives **both** the `-ss` seek value and the temp-file name (`FloorToBucket` + `BuildArgs`/`ResolveTempPath`; e.g. 7.85s @ 1s bucket → seek `7`, file `7000.jpg`).
- **I4** — A non-positive `time` (≤ `TimeSpan.Zero`) floors to bucket 0 → seek `0`, file `0.jpg` (`FloorToBucket` lines 217-219).
- **I5** — The `-ss` value is the bucketed timestamp formatted as invariant-culture seconds via `"0.######"` (`FormatSeconds`, lines 227-228).
- **I6** — The temp path is `<cacheRoot>/<hash>/<bucketMs>.jpg`, where `<hash>` is the first 16 bytes of SHA-256(inputPath) as lowercase hex and `<bucketMs>` is the bucket's total milliseconds (`InputCacheDir` + `HashInput` + `ResolveTempPath`, lines 231-255).
- **I7** — A second request for the **same** (input, bucket) returns the cached path **without** re-running ffmpeg (runner invoked once for two same-bucket calls) (cache-hit branch, lines 114-117; `TryGetCached`).
- **I8** — Requests for **distinct** buckets each run ffmpeg (no false cache sharing across buckets) (distinct `cacheKey` per bucket, `BuildCacheKey` line 240).
- **I9** — The in-memory cache is LRU-bounded to `maxEntries` (default 128); when the cap is exceeded the least-recently-used entry is evicted **and its temp file is deleted from disk** (`Remember` eviction loop, lines 294-299).
- **I10** — A bucket whose entry was LRU-evicted is re-extracted (ffmpeg re-runs) on its next request (eviction removes it from `_index`, so the next call misses; lines 294-299 + cache-miss path).
- **I11** — A tracked cache entry is only treated as a hit if its file **still exists on disk**; a tracked path whose file was removed externally falls through to re-extraction (`File.Exists(cachedPath)` guard, line 114).
- **I12** — A temp file left on disk by a prior process (present on disk but not in the in-memory cache) is reused **without** running ffmpeg and is re-tracked in the cache (`File.Exists(tempPath)` reuse branch, lines 122-126).
- **I13** — An already-cancelled token short-circuits: `GetThumbnailAsync` returns `null` and **never launches ffmpeg** (early `ct.ThrowIfCancellationRequested()`, line 108; runner not called).
- **I14** — Cancellation while the runner is in flight resolves to `null` and never throws (the `OperationCanceledException` catch, lines 150-154).
- **I15** — An ffmpeg failure (runner `Success == false`) returns `null` and is **not cached**, so a retry re-runs ffmpeg (lines 142-145; failure never reaches `Remember`).
- **I16** — A runner that reports success but produces no output file returns `null` (post-run `File.Exists(tempPath)` check, line 142).
- **I17** — An empty/whitespace `inputPath` returns `null` and never launches ffmpeg (guard line 103).
- **I18** — A non-positive `width` (≤ 0) returns `null` and never launches ffmpeg (guard line 103).
- **I19** — Best-effort throughout: any other failure (I/O, security, locator-missing) is swallowed and resolves to `null` — `GetThumbnailAsync` never throws (catch-all, lines 155-159).
- **I20** — `Clear(inputPath)` deletes that input's cache dir **and** drops its in-memory entries, so a subsequent same-bucket request re-runs ffmpeg (`ForgetUnder` + `TryDeleteDirectory`, lines 163-180).
- **I21** — `Clear` on a missing dir or an empty/whitespace path is a no-op and never throws (guard line 167 + best-effort catch, lines 176-179).
- **I22** — `ClearAll()` clears the in-memory cache and deletes the entire cache root dir, and never throws (lines 183-199).
- **I23** — The constructor rejects a null `runner` or null `cacheRoot` with `ArgumentNullException` (lines 72-73).
- **I24** — A non-positive `bucketGranularity` falls back to the 1s default, and a non-positive `maxEntries` falls back to the 128 default (constructor normalization, lines 75-77).
- **I25** — `DefaultCacheRoot()` resolves to `%LOCALAPPDATA%/VideoSplitJoiner/thumb-cache`, falling back to the OS temp folder when LocalApplicationData cannot be resolved (lines 87-96).

## Links
- Design: —
- Goals: G-030 (hover-thumbnail preview on the scrub bar)
- Related specs: the App-layer `ThumbnailPreviewViewModel` hover/debounce/latest-wins spec (T-078); the ffmpeg-runner spec (T-002)
- Key code: `src/Core/Thumbnails/IThumbnailService.cs`, `src/Core/Thumbnails/FfmpegThumbnailService.cs`
- Tests: `tests/Core.Tests/FfmpegThumbnailServiceTests.cs`
