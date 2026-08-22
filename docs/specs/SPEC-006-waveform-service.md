---
id: SPEC-006
slug: waveform-service
area: core
title: Audio waveform service
status: current
sources: [src/Core/Waveform/IWaveformService.cs, src/Core/Waveform/FfmpegWaveformService.cs]
serves-goal: [G-033]
updated: 2026-08-22
---

## What
A UI-free Core service (`IWaveformService`, default impl `FfmpegWaveformService`) that turns a video's audio
track into a normalized peak array for the Split screen's scrub-bar waveform. `GetPeaksAsync(inputPath, buckets, ct)`
shells out to the ffmpeg CLI to decode a downsampled **mono** PCM stream to a temp file
(`-vn -ac 1 -ar <rate> -f s16le -y <temp.pcm>`), reads it back as little-endian `Int16` samples, splits them into
`buckets` contiguous windows, and reduces each window to its **max absolute amplitude normalized to 0..1** (divide by
32768). The result is a `float[buckets]` on success, or `null` on any failure or no-audio — the method never throws.
Results are memoized in a bounded LRU cache keyed by the input's identity + bucket count, so repeat calls skip ffmpeg.
`Clear(inputPath)` and `ClearAll()` drop cached peaks + temp PCM best-effort. The service returns raw data only
(`float[]`, never a WPF/visual type); the App layer (T-084) draws it.

## Why
Finding a good cut point means finding a silence between sentences/scenes; today the user hunts for it by scrubbing
and listening. A waveform lets them *see* the quiet gaps and loud passages and drop a cut marker on a boundary
(design D-002, goal G-033). Core stays UI-free so the amplitude extraction is testable and reusable independent of
WPF. ffmpeg writes to a **temp file rather than a pipe** because `FfmpegRunner` reads stdout as UTF-8 text and would
corrupt raw PCM bytes — the same constraint that shaped the thumbnail service. Extraction is best-effort (never
throws, `null` on failure) so a missing ffmpeg or a no-audio clip just hides the band instead of breaking the screen;
the LRU cache and low sample rate keep even long videos cheap.

## Scope
**In:** the Core `IWaveformService` contract and its `FfmpegWaveformService` implementation — the ffmpeg argument
shape, PCM→peak bucketing/normalization math, best-effort/null semantics, the LRU cache (keying, eviction, defensive
copy, no-cache-on-failure), `Clear`/`ClearAll`, the temp-path layout, and construction defaults.
**Out:** the App-layer background extraction wiring in `SplitViewModel.LoadAsync` (stale-guard / cancel-on-new-load),
the `WaveformViewModel` (`Peaks`/`IsLoading`/`HasAudio`), and the `TimelineView` waveform-band rendering / fused
playhead-marker-click (T-084, a separate App spec). The `FfmpegRunner`/`FfmpegArgs` mechanics (SPEC for the ffmpeg
runner). Automatic silence-detection, spectrogram, and audio editing (explicitly out per D-002 §7).

## Current behavior & invariants
- **I1** — Input guard: `GetPeaksAsync` returns `null` and never launches ffmpeg when `inputPath` is null/empty/whitespace **or** `buckets <= 0` (`FfmpegWaveformService.GetPeaksAsync`, lines 113–116).
- **I2** — Pre-cancelled token: an already-cancelled `ct` yields `null` with the runner never invoked — cancellation is checked (`ThrowIfCancellationRequested`) before args are built or ffmpeg runs (line 118).
- **I3** — Extraction args shape: `BuildArgs` emits `-i <input> -vn -ac 1 -ar <rate> -f s16le -y <temp>` — video dropped, mono mixdown, raw signed 16-bit little-endian PCM, overwrite — with `-i <input>` before the extraction flags and the temp `.pcm` path as the last (output) token; PCM goes to a temp file, never a pipe (`BuildArgs`, lines 227–235).
- **I4** — Sample rate: the configured mono sample rate is emitted as the `-ar` value; the default is `DefaultSampleRateHz` = 4000 (`_sampleRateHz`, `BuildArgs`).
- **I5** — Peak reduction: on ffmpeg success the temp PCM is read as `Int16` LE samples and split into `buckets` contiguous, near-even windows; each output peak = `max(|sample|)` over its window divided by `Int16NormalizationDivisor` (32768), clamped to a max of 1.0 (`ComputePeaks`, lines 242–287).
- **I6** — Full-length array: the returned array always has exactly `buckets` elements even when the sample count is smaller than `buckets`; windows containing no samples remain 0 (`ComputePeaks`, lines 261–267).
- **I7** — Normalization boundary: `short.MinValue` (|−32768|) normalizes to exactly 1.0 and every returned peak lies within [0, 1] (`ComputePeaks`, lines 275–283).
- **I8** — Silent-but-present audio: non-empty PCM that is all zeros yields an all-zero peak array of length `buckets`, **not** `null` (a present silent track is a valid waveform).
- **I9** — No audio / empty PCM: a temp PCM of fewer than 2 bytes (no whole `Int16` sample — the no-audio-track case) yields `null` (`GetPeaksAsync`, lines 153–157; `ComputePeaks` `sampleCount <= 0`, lines 249–253).
- **I10** — ffmpeg failure: when the runner result is not `Success`, `GetPeaksAsync` returns `null` (lines 145–148).
- **I11** — Missing temp: when the runner reports success but no temp file exists on disk, `GetPeaksAsync` returns `null` (lines 145–148).
- **I12** — Never throws (general): any non-cancellation exception (I/O, security, runner throw) is swallowed and resolves to `null` (`catch`, lines 178–182).
- **I13** — Cancellation resolves to null: an `OperationCanceledException` from a superseded/mid-run request yields `null` without throwing and without clobbering a newer result (`catch (OperationCanceledException)`, lines 173–177).
- **I14** — Cache hit skips ffmpeg: a repeat call with the same cache key returns the cached peaks **without** invoking the runner; the key is `inputPath | LastWriteTimeUtc.Ticks | Length | buckets` (`TryBuildCacheKey`, lines 300–318; hit path lines 124–127).
- **I15** — Defensive copy: every array handed back — cache hit or fresh compute — is an independent `Clone()`; a caller mutating its result does not corrupt the cached array (lines 126, 171).
- **I16** — Bucket count in key: a different `buckets` value is a distinct cache key and forces a fresh extraction (key includes `buckets`).
- **I17** — File identity in key: a change to the input's last-write-time or byte length invalidates the key and forces re-extraction (key includes `LastWriteTimeUtc.Ticks` + `Length`).
- **I18** — LRU bound: the in-memory cache is capped at `maxEntries` (default `DefaultMaxEntries` = 16); once the cap is exceeded the least-recently-used entry is evicted and must be re-extracted on its next request (`Remember`, lines 357–380; `TryGetCached` move-to-front, lines 339–355).
- **I19** — Failures not cached: `null` outcomes (no-audio/empty PCM, ffmpeg failure, missing temp) are never cached — a retry re-runs the runner (caching only on the success path, lines 165–168).
- **I20** — `Clear(inputPath)`: drops that input's on-disk cache dir and its in-memory entries so the same key re-extracts on next call; a missing dir or empty/whitespace path is a no-op that never throws (`Clear` + `ForgetForInput` + `TryDeleteDirectory`, lines 186–202, 383–401, 403–416).
- **I21** — `ClearAll()`: clears the whole in-memory LRU and deletes the entire cache root so all keys re-extract; never throws (lines 205–221).
- **I22** — Temp layout & default root: the temp PCM path is `<cacheRoot>/<sha256-hex first-16-bytes of inputPath>/audio.pcm`, and the default cache root is `%LOCALAPPDATA%/VideoSplitJoiner/waveform-cache`, falling back to the OS temp folder when `LocalApplicationData` cannot be resolved (`ResolveTempPath`/`InputCacheDir`/`HashInput`, lines 289–332; `DefaultCacheRoot`, lines 97–106).
- **I23** — Construction: the constructor throws `ArgumentNullException` for a null `runner` or null `cacheRoot`, and a non-positive `sampleRateHz` or `maxEntries` falls back to the defaults (4000 / 16) (ctor, lines 78–88).

## Links
- Design: D-002 (`docs/design/D-002-audio-waveform.md`; board artifact `docs/todo/D-002.md`)
- Goals: G-033 (tasks T-083 Core service · T-084 App VM+view · T-085 docs)
- Related specs: — (App-layer waveform VM/TimelineView band = T-084, spec TBD)
- Key code: `src/Core/Waveform/IWaveformService.cs`, `src/Core/Waveform/FfmpegWaveformService.cs`; tests `tests/Core.Tests/FfmpegWaveformServiceTests.cs`
