---
id: SPEC-004
slug: media-probe
area: core
title: Media probe — duration, keyframes, snapping
status: current
sources:
  - src/Core/Media/MediaProbe.cs
  - src/Core/Media/KeyframeSnap.cs
  - src/Core/Media/ProbeResult.cs
  - src/Core/Media/MediaInfo.cs
  - src/Core/Media/StreamInfo.cs
  - src/Core/Media/FfprobeJson.cs
serves-goal: [G-008]
updated: 2026-08-22
---

## What
`MediaProbe` (`IMediaProbe`) is the core media-inspection service, built on the T-002 `IFfprobeRunner`. It does four jobs: **(1)** `ProbeAsync` reads a file's container, total duration, and video/audio streams into a typed `ProbeResult` (`ProbeSucceeded(MediaInfo)` or `ProbeFailed(reason)`), never throwing for a bad file; **(2)** `GetKeyframesAsync` returns the file's video keyframe timestamps (sorted, distinct) using a fast demux-level packet scan with a decode-level frame-scan fallback, memoized per file and de-duplicated across concurrent callers (T-093); **(3)** `SnapToNearestKeyframe` snaps an arbitrary requested time to the nearest keyframe (ties → earlier, clamped to ends), returning a `KeyframeSnap(Snapped, Delta)`; **(4)** `AverageGop` reports mean keyframe spacing so callers can warn when snapping will be coarse.

## Why
Every split is a lossless `-c copy` cut, so a cut can only land cleanly on a **keyframe** boundary (ADR-0009). To let the user drop a cut anywhere and have it snap to a copyable edge, the app needs the full keyframe list plus a snapping primitive — consumed by `Split/SplitEngine.cs` (plan-time snapping) and the split/bulk marker view-models (live marker snapping). Because the keyframe scan runs on long/4K clips and gates a UI interaction, it uses a demux-level packet read (fast) with a decode fallback (always correct), a per-file cache, and in-flight dedup so a split starting before the load-time background scan finishes does not double the wait (T-093 / G-008). `ProbeAsync` returns a typed failure instead of throwing so callers branch on the result rather than catching (ADR-0002 error model).

## Scope
**In:** `MediaProbe.ProbeAsync` (duration/streams/container parsing + failure typing); `GetKeyframesAsync` (two-path scan, cache keyed by path+mtime+length, T-093 in-flight dedup + cancellation-safety + success-only caching); `IsKeyframeFlag` parsing; `SnapToNearestKeyframe` → `KeyframeSnap`; `AverageGop`; the `ProbeResult` / `MediaInfo` / `StreamInfo` / `KeyframeSnap` shapes these produce.
**Out:** the `IFfprobeRunner` process execution + `FfprobeException` mapping itself (T-002, its own spec); how `SplitEngine` / view-models consume snapped times (Split spec); the JOIN-compatibility stream comparison that reuses `StreamInfo`; waveform extraction (its own service); ffmpeg binary location.

## Current behavior & invariants

### ProbeAsync → ProbeResult (`MediaProbe.ProbeAsync`, `ProbeResult`, `MediaInfo`, `StreamInfo`)
- **I1** — An empty/whitespace `path` returns `ProbeResult.Failure` ("Path is empty.") and does not throw (`ProbeAsync` guard).
- **I2** — A `path` that does not exist on disk returns `ProbeResult.Failure` ("File does not exist…") and does not throw (`File.Exists` guard).
- **I3** — When ffprobe exits non-zero (corrupt / non-media file), the `FfprobeException` is caught and converted to `ProbeResult.Failure` — `ProbeAsync` never throws for a bad file (catch of `FfprobeException`).
- **I4** — When ffprobe output is not valid JSON, the `JsonException` is caught and returns `ProbeResult.Failure` ("…was not valid JSON…").
- **I5** — When the parsed payload has no streams (`root.Streams` null or count 0), returns `ProbeResult.Failure` ("No media streams found…").
- **I6** — On success, returns `ProbeResult.Success(MediaInfo)` where each stream is partitioned by `codec_type` into `VideoStreams` (`IsVideo`) / `AudioStreams` (`IsAudio`) in container order, `Container` = ffprobe `format_name` (or `"unknown"` when absent), and each `StreamInfo` carries the mapped codec/dimensions/pix_fmt/sample_rate/channels/time_base; `MediaInfo.HasVideo`/`HasAudio` reflect the respective stream counts.
- **I7** — `Duration` resolves from `format.duration`; when that is absent/`N/A`/unparseable, it falls back to the **longest** per-stream duration (`ResolveDuration`), defaulting to `TimeSpan.Zero` when neither is available.
- **I8** — Cancellation surfaces as `OperationCanceledException` (propagated from the runner) — it is **not** swallowed into a `ProbeFailed` (only `FfprobeException`/`JsonException` are caught).

### GetKeyframesAsync — two-path scan + cache + T-093 dedup (`MediaProbe.GetKeyframesAsync`, `RunSharedScanAsync`, `IsKeyframeFlag`, `FfprobeJson`)
- **I9** — An empty/whitespace `path` throws `ArgumentException` (this method throws rather than returning a typed result).
- **I10** — A non-existent `path` throws `FileNotFoundException`.
- **I11** — Returns keyframe timestamps **sorted ascending and distinct** (accumulated via `SortedSet<TimeSpan>` on both scan paths), as an `IReadOnlyList<TimeSpan>`.
- **I12** — Primary path is the demux-level packet scan: `-select_streams v:0 -show_packets`, keeping only packets whose `flags` mark a keyframe, timestamp = `pts_time` (falling back to `dts_time`); when it yields ≥1 keyframe, `LastScanPath == KeyframeScanPath.Packets`.
- **I13** — `IsKeyframeFlag` returns true iff the flags string contains `'K'` in any position (`"K__"`, `"K_"`, `"K"`, `"KD_"` → true) and false for null/empty/no-`K` (`"___"`, `"__"`, `"_D_"`, `""`, null → false).
- **I14** — When the packet query returns **zero** keyframes, the scan falls back to the decode-level frame scan (`-skip_frame nokey`, `-show_entries frame=…`) and `LastScanPath == KeyframeScanPath.Frames`.
- **I15** — When the packet query **throws** `FfprobeException`, the scan falls back to the decode-level frame scan (correctness never regresses).
- **I16** — The successful result is cached keyed by `(full path, file LastWriteTimeUtc ticks, file length)` (`BuildCacheKey`); a repeat call on the unchanged file returns the cached list with **no** second ffprobe scan.
- **I17** — Because the cache key includes mtime + length, modifying the file (changing its length or last-write time) produces a different key and forces a fresh re-scan rather than returning stale keyframes.
- **I18** — T-093: two concurrent `GetKeyframesAsync` calls for the same cache key share **one** underlying scan — the second attaches to the first's in-flight `Task` (`_inFlightScans.GetOrAdd`) rather than launching a duplicate ffprobe pass; both awaiters observe the same result.
- **I19** — T-093 cancellation-safety: the shared scan runs on `CancellationToken.None`; a caller awaits via `Task.WaitAsync(ct)`, so a caller cancelling its **own** await throws `OperationCanceledException` for that caller only and does **not** tear down the shared scan — other awaiters still complete from the one scan.
- **I20** — T-093 success-only caching: only a **successful** result is promoted to `_keyframeCache`; a faulted/failed shared scan caches nothing, always removes its `_inFlightScans` entry (finally block), and a later retry starts a fresh scan.
- **I21** — After a scan completes, its in-flight entry is removed; a subsequent call for the same unchanged file is served from the durable `_keyframeCache` (not treated as still-in-flight and not re-scanned).
- **I22** — The packet path and the frame path produce matching keyframe **count** and matching timestamps (within a small pts-vs-best-effort rounding tolerance) on the same file — the fast path is a faithful substitute for the correct path.

### SnapToNearestKeyframe → KeyframeSnap (`MediaProbe.SnapToNearestKeyframe`, `KeyframeSnap`)
- **I23** — A null `keyframes` list throws `ArgumentNullException` (`ArgumentNullException.ThrowIfNull`).
- **I24** — An empty `keyframes` list throws `ArgumentException` ("Keyframe list is empty…").
- **I25** — Returns the keyframe with the smallest absolute distance to `requested` (e.g. 1.4s → 1.0s, 1.6s → 2.0s against 1-second keyframes).
- **I26** — On an exact-midpoint tie the **earlier** keyframe wins (1.5s against {…,1,2,…} → 1.0s), enforced by strictly-less distance comparison plus an explicit earlier-on-equal guard.
- **I27** — A request past the last keyframe clamps to the last keyframe.
- **I28** — A request before the first keyframe clamps to the first keyframe.
- **I29** — `KeyframeSnap.Delta == Snapped - requested` (signed: negative when snapped earlier, positive when later).
- **I30** — Snapping is order-independent: an **unsorted** keyframe list still yields the nearest keyframe with earlier-on-tie (every candidate is evaluated, not just neighbours).

### AverageGop (`MediaProbe.AverageGop`)
- **I31** — Fewer than two keyframes returns `TimeSpan.Zero`.
- **I32** — For ≥2 keyframes, returns `(max − min) / (count − 1)` computed on defensively-sorted input (mean spacing; ~1s for keyframes one second apart).

## Links
- Design: ADR-0009 (two-path keyframe scan — packets primary, decode fallback, cache); ADR-0002 (typed error model)
- Goals: G-008 (fast video load — non-blocking + faster keyframe indexing); T-031 (packet-flag scan); T-093 (in-flight dedup + reuse load-time keyframes)
- Related specs: Split (SplitEngine keyframe-snapped `-c copy` cuts) · Ffprobe runner (T-002, `IFfprobeRunner`/`FfprobeException`)
- Key code: `src/Core/Media/MediaProbe.cs` · `KeyframeSnap.cs` · `ProbeResult.cs` · `MediaInfo.cs` · `StreamInfo.cs` · `FfprobeJson.cs`
- Tests: `tests/Core.Tests/MediaProbeSnapTests.cs` · `MediaProbeKeyframePacketTests.cs` · `MediaProbeInFlightDedupTests.cs` · `MediaProbeIntegrationTests.cs`
