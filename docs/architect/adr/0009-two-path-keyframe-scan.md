# ADR 0009: Two-path keyframe scan — demux packets primary, decode fallback, cache

## Status

Accepted.

## Context

Every split is a lossless `-c copy` cut (see `docs/ARCHITECTURE.md`), so a cut can
only land cleanly on a **keyframe** boundary. To let the user drop a cut anywhere and
snap it to a copyable edge, `MediaProbe.GetKeyframesAsync`
(`src/Core/Media/MediaProbe.cs`) must build the full list of video keyframe timestamps
for a file — feeding `SnapToNearestKeyframe` / `AverageGop`, which are consumed by
`Split/SplitEngine.cs` (plan-time snapping) and `App/ViewModels/CutMarkerViewModel.cs`
(live marker snapping).

The original implementation scanned keyframes with `ffprobe -skip_frame nokey`
(`-show_entries frame=…`). That is a **decode-side** scan: ffprobe walks frames and
drops non-keyframes. It is always correct, but it forces per-frame work that scales
badly on long or high-resolution (4K) clips — the exact files this app targets. That
cost sits directly in the split/marker interaction path, so it is felt as UI latency
(T-031 / goal G-008).

Forces at play:

- **Performance** — the scan runs on 4K / long clips and gates a UI interaction; the
  decode-side walk is the bottleneck.
- **Correctness must not regress** — the keyframe set drives where a lossless cut is
  allowed to land; a missed or wrong keyframe corrupts the cut contract.
- **Format variance** — packet `flags` formatting varies across containers / VFR
  sources, so a purely faster query cannot be trusted blindly.
- **Repeat calls on an unchanged file** — split planning and marker snapping re-query
  the same file many times per session.

## Decision

Scan keyframes via **two paths behind one method**, with a **memoizing cache** in
front. `GetKeyframesAsync` keeps its exact contract — a sorted, distinct
`IReadOnlyList<TimeSpan>` — and chooses the path internally:

- **Primary (fast, demux-level)** — `ScanKeyframesFromPacketsAsync` runs
  `ffprobe -select_streams v:0 -show_packets -show_entries packet=pts_time,dts_time,flags
  -print_format json`, keeps packets whose `flags` contain the `K` marker
  (`IsKeyframeFlag`, matches any position: `"K__"`, `"K_"`), and takes `pts_time`
  (falling back to `dts_time`). No frame decoding — substantially cheaper on large
  clips. Packets arrive in DTS order, so results go through a `SortedSet<TimeSpan>` to
  emit sorted-distinct times.
- **Fallback (correct, decode-level)** — `ScanKeyframesFromFramesAsync` is the
  pre-T-031 `-skip_frame nokey` frame scan, preserved verbatim as the safety net.
- **Non-obvious fallback trigger** — the frame path fires on **either** of two
  conditions, not just an error: (1) the packet query throws `FfprobeException`
  (query failed outright), **or** (2) it returns successfully but with **zero**
  keyframes (`packetKeyframes.Count == 0`) — the odd-container / unexpected-flag case
  where the fast query "works" but yields nothing usable. The zero-result branch is the
  subtle one: a successful-but-empty packet scan is treated as a soft failure and
  silently retried on the decode path, so correctness never regresses on containers
  whose flag formatting the packet parser doesn't recognize.
- **Cache** — results are memoized in a `ConcurrentDictionary` keyed by
  `BuildCacheKey` = **`fullpath | mtime.Ticks | length`** (absolute path, last-write
  UTC ticks, byte length). Any edit that changes mtime or size invalidates the entry
  naturally; an unchanged file is served without re-scanning either path.
- **Path observability** — `LastScanPath` (`KeyframeScanPath { None, Packets, Frames }`)
  records which path the last non-cached scan used, purely so tests can assert the fast
  path ran and that the fallback fires on empty packets. It is `internal`, not part of
  the public contract, and is **not** updated on a cache hit.

Introduced under **T-031** (commit `d1cba99`), serving goal **G-008**.

## Consequences

**Positive**

- **Fast common path.** 4K / long clips index keyframes at the demux layer with no
  frame decode, removing the scan bottleneck from the split/marker interaction.
- **No correctness regression.** The exact old decode-side scan remains reachable as the
  fallback, so any container the packet query mishandles still yields a correct keyframe
  set — the snap/cut contract is unchanged.
- **Cheap repeats.** The `(path, mtime, length)` cache makes re-queries during split
  planning and marker snapping effectively free while auto-invalidating on file change.
- **Contract-stable swap.** `GetKeyframesAsync` returns the same sorted-distinct
  `IReadOnlyList<TimeSpan>`; `SnapToNearestKeyframe`, `AverageGop`, `SplitEngine`, and
  `CutMarkerViewModel` were untouched — the change is entirely an internal query swap.

**Negative**

- **Two query shapes to maintain.** Two distinct ffprobe invocations and two ffprobe
  JSON DTO sets (`FfprobePacketsRoot` / `FfprobeFramesRoot` in `Media/FfprobeJson.cs`)
  now describe "the keyframe scan" and must stay behaviourally equivalent.
- **Flag-parse fragility.** The fast path depends on ffprobe's packet `flags` string
  format; a container whose flags omit the recognizable `K` marker silently produces
  zero keyframes and pays the cost of *both* scans (a failed packet scan, then the
  decode fallback) rather than one.
- **Cache is process-local and not size-bounded.** It lives only for the process
  lifetime (no on-disk persistence — explicitly out of T-031 scope) and grows one entry
  per distinct `(path, mtime, length)` seen; long sessions over many files accumulate
  entries with no eviction.
- **mtime/length key is coarse.** An edit that preserves both last-write time and byte
  length (rare, but possible with timestamp-preserving tools) would serve a stale
  keyframe list.

**Forced follow-ons**

- Keep the **known-GOP equality test** (packet path vs frame path agree within
  tolerance on a fixture with keyframes at whole seconds) as the guard against a bad
  packet-flag parse — this is what makes the silent packet→frame fallback safe to trust.
- Keep exercising the **empty-packets → frame-fallback** path with a fake runner so the
  zero-result trigger (not just the exception trigger) stays covered.
- The `-c copy` cut contract (ADR 0004 context) still assumes cuts land on real
  keyframes; any future change to how keyframe times are derived must preserve that both
  paths return the same boundaries.
- A persistent on-disk keyframe index and a bounded/evicting cache remain open
  possibilities (deferred out of T-031) if session-scoped, unbounded memoization proves
  insufficient.
