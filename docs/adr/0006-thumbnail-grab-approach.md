# ADR 0006: Scrub-bar hover thumbnails via a second ffmpeg-CLI grab, not FFME frame captures

## Status

Accepted.

## Context

The Split screen needs a hover preview on the scrub bar: as the cursor sweeps the
timeline, a small popup shows the frame at the hovered time (T-077/T-078). The app
already has a native ffmpeg-backed preview player — FFME (ADR 0004) — so the obvious
question was whether to grab hover frames off the *same* FFME `MediaElement` that is
already open and decoding.

Reusing FFME for thumbnails is a poor fit:

- **It fights the transport.** FFME is a single stateful decoder driving the live
  preview (play/pause/seek/scrub). Yanking it to a hover time to snapshot a frame
  perturbs the position the user is actually watching, and racing hover grabs against
  transport seeks is exactly the "preview surface and I disagree" class of bug ADR 0004
  worked to remove.
- **It is not headless or testable.** Per ADR 0004 the FFME surface
  (`FfmeMediaPlayer`) is deliberately compile-only, non-unit-tested WPF plumbing behind
  the `IMediaPlayer` seam. A frame-grab feature built on it would inherit that
  untestability.
- **The engine already shells out to ffmpeg.** Split/join runs through the Core
  `FfmpegRunner` choke-point (`src/Core/Ffmpeg/FfmpegRunner.cs`). A frame grab is just
  another one-shot ffmpeg invocation — cheap to add as a **second, independent** CLI
  path that owns its own process per frame and never touches the live decoder.

Forces at play:

- **Isolation from the preview** — hover grabs must not disturb the FFME transport.
- **UI-free + testable Core** — the frame source belongs in Core behind an interface,
  returning a filesystem path, never a WPF `ImageSource`
  (`src/Core/Thumbnails/IThumbnailService.cs`).
- **Hover flood control** — a fast sweep fires many hover samples/second; each must not
  spawn an ffmpeg process, and stale grabs must never overwrite a newer frame.
- **File-lock hazard** — the temp jpg the service writes may be re-grabbed, evicted, or
  swept while the UI is still displaying it.
- **Best-effort** — a preview that can't be produced simply shows nothing; it never
  throws or blocks (matching `ErrorLogWriter`'s discipline).

## Decision

Grab hover frames via a **separate ffmpeg-CLI process per frame**, cached and coalesced,
completely independent of the FFME preview player.

- **Second ffmpeg path (Core, UI-free).** `FfmpegThumbnailService : IThumbnailService`
  (`src/Core/Thumbnails/FfmpegThumbnailService.cs`) extracts one frame to a temp jpg via
  the *same* `IFfmpegRunner` the engine uses, but as its own one-shot invocation. Args
  (`BuildArgs`) are `-ss <t>` **before** `-i <input>` (fast keyframe-accurate input
  seek — accuracy is fine for a hover), then `-frames:v 1 -vf scale=<width>:-1 -y
  <temp.jpg>`. It is wired in the composition root as a distinct service:
  `new FfmpegThumbnailService(ffmpegRunner)` in `MainViewModel`, sharing only the
  runner, not the FFME `MediaElement`. Every public method is best-effort — returns
  `null` / no-ops on any failure, never throws.
- **LRU time-bucket cache in `%LOCALAPPDATA%`.** Frames are keyed by `(inputPath,
  bucket)`, where `bucket` is the hovered time floored to a granularity (default 1 s).
  Temp files live at
  `%LOCALAPPDATA%/VideoSplitJoiner/thumb-cache/<sha256-of-input>/<bucketMs>.jpg`
  (`DefaultCacheRoot`, falls back to OS temp when local-app-data is unresolved). A cache
  hit returns the existing file **without running ffmpeg**; the in-memory index is an
  LRU (`LinkedList` + `Dictionary`, default cap 128) that evicts the least-recently-used
  entry *and deletes its file* past the cap. The cache root is injectable so tests never
  touch the real per-user folder.
- **Debounce + coalesce with a latest-wins requestId gate (App VM).**
  `ThumbnailPreviewViewModel` (`src/App/ViewModels/ThumbnailPreviewViewModel.cs`)
  debounces each hover (default 60 ms, cancellable) before touching ffmpeg, so a fast
  sweep does not flood the service. Every hover swaps a `CancellationTokenSource`
  (cancelling the prior in-flight grab) **and** stamps a monotonic `++_requestId`. A
  resolved grab is marshalled back onto the captured `SynchronizationContext` via
  `Progress<T>` and committed only when its id still equals `_requestId` and the cursor
  is still over the bar (`ApplyResult`) — so even two grabs that both complete cannot let
  a stale one clobber a newer frame.
- **Frozen `OnLoad` `BitmapImage` at the view boundary.** Core returns a *path*
  (string), keeping it UI-free; the App loads it in `PathToBitmapConverter`
  (`src/App/Views/Converters.cs`) with `BitmapCacheOption.OnLoad` (read the file fully at
  load time so the temp jpg is **not** left locked — the service may evict/overwrite it),
  `BitmapCreateOptions.IgnoreImageCache` (a re-grabbed bucket path re-reads rather than
  serving a stale decode), then `Freeze()` (immutable, cross-thread-safe). A
  null/missing/undecodable path yields null and the popup simply shows nothing.

## Consequences

**Positive**

- **Zero interference with the preview.** Hover grabs run in their own short-lived
  ffmpeg processes; the FFME transport (ADR 0004) is never perturbed.
- **Fast + cheap on repeat hovers.** Bucketing + LRU means a sweep back and forth over
  the same second is served from disk with no ffmpeg spawn; the debounce collapses a
  fast sweep into a handful of grabs.
- **Testable Core.** The frame source is a plain `IThumbnailService` returning a path,
  with an injectable cache root, bucket granularity, delay seam, and result sink — fully
  unit-testable without WPF, real ffmpeg, or the real per-user folder.
- **No stale-frame flicker, no file locks.** The requestId gate drops superseded results
  and the `OnLoad`+`Freeze` decode releases the temp file immediately, so eviction/sweep
  can delete it safely.

**Negative**

- **A second ffmpeg dependency + process-per-frame.** Hover previews spawn their own
  ffmpeg CLI processes; on a cache-cold sweep that is one short process per new bucket
  (bounded by the debounce, not eliminated).
- **Keyframe-accurate, not exact.** Input-seek (`-ss` before `-i`) snaps to the nearest
  keyframe, so the hovered frame can be off by up to a GOP — an accepted trade for speed
  on a hover.
- **On-disk cache to manage.** The service writes jpgs under `%LOCALAPPDATA%`; stale
  files can outlive a process (a fresh run reuses an untracked on-disk file if present),
  so cleanup relies on `Clear`/`ClearAll` and LRU eviction rather than the OS temp reaper.

**Forced follow-ons**

- `SetInput`/`Clear` on the VM sweep the previous input's cache dir (`Clear(prev)`) so a
  new load never leaks the prior file's thumbnails; the cache-key hash must stay stable
  per input path for that sweep to hit.
- The `OnLoad` + `IgnoreImageCache` decode contract in `PathToBitmapConverter` must be
  preserved — dropping `OnLoad` would re-lock the temp file against eviction, and
  dropping `IgnoreImageCache` would serve a stale frame for a re-grabbed bucket.
- Bucket granularity, debounce window, and LRU cap are tuning knobs on the
  responsiveness ↔ ffmpeg-spawn trade; they stay injectable so they can be adjusted (or
  driven from settings) without touching the grab or gate logic.
