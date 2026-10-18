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
serves-goal: [G-008, G-059]
updated: 2026-10-18
---

## What
`MediaProbe` (`IMediaProbe`) is the core media-inspection service, built on the T-002 `IFfprobeRunner`. It does four jobs: **(1)** `ProbeAsync` reads a file's container, total duration, and video/audio streams into a typed `ProbeResult` (`ProbeSucceeded(MediaInfo)` or `ProbeFailed(reason)`), never throwing for a bad file, and caches a successful result per file version so a repeat call on an unchanged file runs no second ffprobe (T-193); **(2)** `GetKeyframesAsync` returns the file's video keyframe timestamps (sorted, distinct) using a fast demux-level packet scan with a decode-level frame-scan fallback, memoized per file and de-duplicated across concurrent callers (T-093); **(3)** `SnapToNearestKeyframe` snaps an arbitrary requested time to the nearest keyframe (ties → earlier, clamped to ends), returning a `KeyframeSnap(Snapped, Delta)`; **(4)** `AverageGop` reports mean keyframe spacing so callers can warn when snapping will be coarse.

## Why
Every split is a lossless `-c copy` cut, so a cut can only land cleanly on a **keyframe** boundary (ADR-0009). To let the user drop a cut anywhere and have it snap to a copyable edge, the app needs the full keyframe list plus a snapping primitive — consumed by `Split/SplitEngine.cs` (plan-time snapping) and the split/bulk marker view-models (live marker snapping). Because the keyframe scan runs on long/4K clips and gates a UI interaction, it uses a demux-level packet read (fast) with a decode fallback (always correct), a per-file cache, and in-flight dedup so a split starting before the load-time background scan finishes does not double the wait (T-093 / G-008). `ProbeAsync` returns a typed failure instead of throwing so callers branch on the result rather than catching (ADR-0002 error model). Join, Split and Bulk Cut each probe the same file at several stages (on add, in the compatibility check, again at Run), so a successful probe is cached under the keyframe cache's key and those repeat calls cost no ffprobe (T-193 / G-059).

## Scope
**In:** `MediaProbe.ProbeAsync` (duration/streams/container parsing + failure typing + the T-193 success-only result cache); `GetKeyframesAsync` (two-path scan, cache keyed by path+mtime+length, T-093 in-flight dedup + cancellation-safety + success-only caching); `IsKeyframeFlag` parsing; `SnapToNearestKeyframe` → `KeyframeSnap`; `AverageGop`; the `ProbeResult` / `MediaInfo` / `StreamInfo` / `KeyframeSnap` shapes these produce, including the start times and frame rates (T-219).
**Out:** the `IFfprobeRunner` process execution + `FfprobeException` mapping itself (T-002 — SPEC-017); how `SplitEngine` / view-models consume snapped times (Split spec); the JOIN-compatibility stream comparison that reuses `StreamInfo`; waveform extraction (its own service); ffmpeg binary location.

## Current behavior & invariants

### ProbeAsync → ProbeResult (`MediaProbe.ProbeAsync`, `ProbeResult`, `MediaInfo`, `StreamInfo`)
- **I1** — An empty/whitespace `path` returns `ProbeResult.Failure` ("Path is empty.") and does not throw (`ProbeAsync` guard).
- **I2** — A `path` that does not exist on disk returns `ProbeResult.Failure` ("File does not exist…") and does not throw (`File.Exists` guard).
- **I3** — When ffprobe exits non-zero (corrupt / non-media file), the `FfprobeException` is caught and converted to `ProbeResult.Failure` — `ProbeAsync` never throws for a bad file (catch of `FfprobeException`).
- **I4** — When ffprobe output is not valid JSON, the `JsonException` is caught and returns `ProbeResult.Failure` ("…was not valid JSON…").
- **I5** — When the parsed payload has no streams (`root.Streams` null or count 0), returns `ProbeResult.Failure` ("No media streams found…").
- **I6** — On success, returns `ProbeResult.Success(MediaInfo)` where each stream is partitioned by `codec_type` into `VideoStreams` (`IsVideo`) / `AudioStreams` (`IsAudio`) in container order, `Container` = ffprobe `format_name` (or `"unknown"` when absent), and each `StreamInfo` carries the mapped codec/dimensions/pix_fmt/sample_rate/channels/time_base, its frame rates and its start time (I38, I39); `MediaInfo` also carries the format's start time (I37); `MediaInfo.HasVideo`/`HasAudio` reflect the respective stream counts.
- **I7** — `Duration` resolves from `format.duration`; when that is absent/`N/A`/unparseable, it falls back to the **longest** per-stream duration (`ResolveDuration`), defaulting to `TimeSpan.Zero` when neither is available.
- **I8** — Cancellation surfaces as `OperationCanceledException` (propagated from the runner) — it is **not** swallowed into a `ProbeFailed` (only `FfprobeException`/`JsonException` are caught).

### The time base and the frame rate — T-219 (`MediaProbe.ProbeAsync`, `MapStream`, `ParseFrameRate`, `MediaInfo.StartTime`, `StreamInfo`)
- **I37** — `ProbeAsync` maps ffprobe `format.start_time` to `MediaInfo.StartTime`: the earliest stream's first timestamp, about 1.4 s on an MPEG-TS (1.443444 s on the test suite's TS) and 0 on an ordinary MP4. An absent, `N/A`, empty or unparsable value maps to zero, and a negative one (an MP4 whose edit list starts before 0) is kept. `StartTime` is an optional trailing parameter defaulting to zero, so every older construction is a zero start. It is the time base of ffmpeg's input `-ss` (an offset from it), which the split's per-segment path converts by (SPEC-001 I16, I67).
- **I38** — Each stream's `avg_frame_rate` and `r_frame_rate` map to `StreamInfo.AvgFrameRate` / `RFrameRate` in frames per second (`ParseFrameRate`: `"30000/1001"` → 29.97003, `"30/1"` → 30, a bare `"25"` → 25), null when absent, `N/A`, unparsable, not finite, or with a zero or negative part (`"0/0"`, which ffprobe prints for an audio stream or an unknown rate). `StreamInfo.FrameRate` is `AvgFrameRate ?? RFrameRate`; null means unknown. Both are optional trailing parameters defaulting to null. They are never a Join compatibility field: `CompatChecker` compares named fields only, so two clips that differ only in frame rate are not a mismatch. The split engine's half-frame `-segment_time_delta` reads the first video stream's `FrameRate` (SPEC-001 I67).
- **I39** — Each stream's own `start_time` maps to `StreamInfo.StartTime` (null when absent, `N/A` or unparsable; optional, default null). It can differ from the format's (I37), which is the earliest stream's: on the test TS the video starts at 1.466667 s and the audio at 1.443444 s, and an MP4 or MKV made by a `-c copy` remux or concat has its video 0.021-0.023 s after its audio at 0. The segment muxer counts from the first video packet, so the split engine converts its boundaries by the first video stream's `StartTime` (SPEC-001 I67). Not a Join compatibility field either.

### ProbeAsync result cache — T-193 (`MediaProbe.ProbeAsync`, `_probeCache`, `TryBuildCacheKey`)
- **I33** — A successful probe is cached keyed by `BuildCacheKey` — `(full path, file LastWriteTimeUtc ticks, file length)`, the keyframe cache's key (I16) in a store of its own — and a repeat `ProbeAsync` on the unchanged file returns the cached `ProbeSucceeded` (the same immutable `MediaInfo` instance) with **no** ffprobe run. The key is read before ffprobe runs. The engines and view models are unchanged: their repeat calls are cache hits, so on the one shared probe a Join run after the add-time compatibility check, a Split after the load's probe, and a Bulk Cut row after its add-time probe (Lossless or Exact, including an Exact row that falls back) start no metadata ffprobe for those files, and a Join remove or reorder re-checks compatibility with none.
- **I34** — A change of the file's last-write time **or** its length gives a new key and a fresh probe, whose result is returned and cached; a file edited between a Join's add and its Run is probed again at Run, and an incompatible change is still refused with named mismatches. A file rewritten in place with the same mtime and length would be served stale — the exposure the keyframe cache accepts (I17).
- **I35** — Failures and cancellations are never cached: a `ProbeFailed` (non-zero exit, invalid JSON, no streams — I3–I5) and an `OperationCanceledException` (I8) leave nothing behind, so the next call runs ffprobe again. A cache hit still honours a cancelled token (it throws `OperationCanceledException` and leaves the entry), as a call that reached ffprobe did. There is no in-flight dedup: two simultaneous calls on one uncached file each run ffprobe and store equal results.
- **I36** — The empty-path and missing-file guards (I1/I2) run before any cache lookup: they answer with zero runner calls and cache nothing, and a cached entry never masks a file deleted since. A file whose metadata cannot be read after the guard (deleted in between) has no key — `TryBuildCacheKey` returns false and never throws — and is probed uncached, as before the cache existed.

### GetKeyframesAsync — two-path scan + cache + T-093 dedup (`MediaProbe.GetKeyframesAsync`, `RunSharedScanAsync`, `IsKeyframeFlag`, `FfprobeJson`)
- **I9** — An empty/whitespace `path` throws `ArgumentException` (this method throws rather than returning a typed result).
- **I10** — A non-existent `path` throws `FileNotFoundException`.
- **I11** — Returns keyframe timestamps **sorted ascending and distinct** (accumulated via `SortedSet<TimeSpan>` on both scan paths), as an `IReadOnlyList<TimeSpan>`.
- **I12** — Primary path is the demux-level packet scan: `-select_streams v:0 -show_packets`, keeping only packets whose `flags` mark a keyframe, timestamp = `pts_time` (falling back to `dts_time`); when it yields ≥1 keyframe, `LastScanPath == KeyframeScanPath.Packets`.
- **I13** — `IsKeyframeFlag` returns true iff the flags string contains `'K'` in any position (`"K__"`, `"K_"`, `"K"`, `"KD_"` → true) and false for null/empty/no-`K` (`"___"`, `"__"`, `"_D_"`, `""`, null → false).
- **I14** — When the packet query returns **zero** keyframes, the scan falls back to the decode-level frame scan (`-skip_frame nokey`, `-show_entries frame=…`) and `LastScanPath == KeyframeScanPath.Frames`.
- **I15** — When the packet query **throws** `FfprobeException`, the scan falls back to the decode-level frame scan (correctness never regresses).
- **I16** — The successful result is cached keyed by `(full path, file LastWriteTimeUtc ticks, file length)` (`BuildCacheKey`, also the metadata probe's key since T-193 — I33); a repeat call on the unchanged file returns the cached list with **no** second ffprobe scan.
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
- Goals: G-008 (fast video load — non-blocking + faster keyframe indexing); T-031 (packet-flag scan); T-093 (in-flight dedup + reuse load-time keyframes) · G-059 (4K fast and correct — T-193: a file is probed once until it changes, I33–I36; bounding both caches is T-200; T-219: the start times and frame rates the lossless cut converts by, I37–I39)
- Related specs: SPEC-001 (SplitEngine keyframe-snapped `-c copy` cuts) · Ffprobe runner ([SPEC-017](SPEC-017-ffmpeg-process.md), `IFfprobeRunner`/`FfprobeException`)
- Key code: `src/Core/Media/MediaProbe.cs` · `KeyframeSnap.cs` · `ProbeResult.cs` · `MediaInfo.cs` · `StreamInfo.cs` · `FfprobeJson.cs`
- Tests: `tests/Core.Tests/MediaProbeSnapTests.cs` · `MediaProbeKeyframePacketTests.cs` · `MediaProbeInFlightDedupTests.cs` · `MediaProbeIntegrationTests.cs` · the probe cache (I33–I36, T-193): `MediaProbeProbeCacheTests.cs` (the cache, its key, the guards) · `ProbeOnceAcrossEnginesTests.cs` (Join, Split, Smart Cut and a Bulk row over one shared probe) · `tests/App.Tests/JoinProbeOnceTests.cs` (Join add, reorder, remove and Run) · the start times and frame rates (I37–I39, T-219): `LosslessBoundaryTests.cs` (canned JSON, `ParseFrameRate`, `FrameRate`, the `CompatChecker` case) · `LosslessBoundaryIntegrationTests.cs` (`Probe_MapsTheFormatStart_TheVideoStart_AndTheFrameRate`, on a real TS, its MP4 remux and a B-frame MP4)
