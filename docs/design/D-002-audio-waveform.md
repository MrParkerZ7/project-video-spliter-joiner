# D-002 · Audio Waveform Above the Timeline

> Design record (draft) — render the loaded video's **audio waveform** as an aligned band above the Split
> screen's timeline/mark bar, so the user can see speech vs silence to place cut points.
> Status: **draft** (sealed by `todo-design-done`). Board artifact: `docs/todo/D-002.md`.

## 1. Problem / motivation
A splitter's hardest task is finding the *right* cut point — usually a silence between sentences/scenes. Today
the user hunts for it by scrubbing and listening. Showing the **audio waveform** above the timeline lets them
*see* the quiet gaps and loud passages and drop a cut marker exactly on a boundary.

## 2. Confirmed decisions (clarify round 1)
| # | Fork | Decision |
|---|------|----------|
| D1 | **Rendering** | **Vector waveform aligned to the timeline** — extract audio peaks, draw a themed waveform in WPF that shares the timeline's exact `0..duration` coordinate system (crisp on resize; playhead + markers line up with the audio). Not a raster `showwavespic` PNG. |
| D2 | **Interaction** | **Fused with the timeline** — the playhead line runs across the wave, cut-marker ticks align on it, and clicking the wave seeks. Wave + mark bar are one aligned audio+timeline unit. |
| D3 | **Scope** | **Split screen only** (the cut-finding screen). Join unchanged. |

**Baked defaults** (not asked — sensible; change on request): audio = **mono mixdown** of all tracks · peaks
**computed in the background on load, cached + downsampled** (parallels the keyframe scan, never blocks the load) ·
**no audio track → hide the band** (never an error).

## 3. Layout / visual model

```
SPLIT screen (waveform fused above the mark bar)
┌───────────────────────────────────────────┐
│                  VIDEO                     │   ← PlayerView (Row 0)
├───────────────────────────────────────────┤
│  ▁▂▄█▆▃▁ ▁ ▁▂▅█▇▄▂▁▁ ▁▃▆█▅▂▁  ▁▁ ▁▂▄▆█▄▂  │   ← NEW waveform band (peaks, themed)
│  │        ▲cut        ▲cut          │      │   ← playhead + marker ticks span BOTH
│  ├────────┴───────────┴─────────────┤──────│   ← the 34px Track (click-to-seek)
└───────────────────────────────────────────┘
        one shared 0..duration x-axis
```

- The waveform sits **directly above the `Track`** and shares its width + `x = time/duration · width` mapping, so
  a peak's horizontal position == that moment on the timeline. The **playhead line and cut-marker ticks are drawn
  across the combined height** (wave + track), making them one aligned unit.
- **Clicking anywhere on the wave seeks** (routes to the same seek as `OnTrackClicked`); dragging behaves like the
  timeline. The wave is display+seek, not a separate scrubber.
- **Theme:** waveform fill in a muted surface tone with peaks in a gold/accent tint (UI Designer lens — the wave
  must read clearly under the gold markers/playhead without overpowering them). Dark-theme consistent.
- Composes with **D-001 vertical mode** (the wave+timeline unit stacks the same way) and with **G-030 hover
  thumbnails** (hovering the wave can also show the frame thumbnail — noted as an optional adjacency, not required).

## 4. Component / data model

- **Core — `IWaveformService` / `FfmpegWaveformService`** (new, in `src/Core/` — mirrors `IThumbnailService`):
  `Task<float[]?> GetPeaksAsync(string inputPath, int buckets, CancellationToken ct)` → a **normalized 0..1 peak
  array** (max-abs amplitude per bucket), or `null` (best-effort, never throws). Implementation: ffmpeg decodes a
  **downsampled mono PCM** stream to a temp file (e.g. `-ac 1 -ar <low> -f s16le`), the service reads the samples
  and buckets them into `buckets` columns (max-abs per bucket) → normalized peaks. **Temp file, not piped** —
  `FfmpegRunner` reads stdout as UTF-8 text, so raw PCM must go through a temp file (same constraint that shaped the
  thumbnail service). **Cache** the peak array keyed by `(path | mtime | length)` like the keyframe cache; downsample
  so a long video stays cheap. UI-free (returns data, not a visual).
- **App — VM:** a WPF-free `WaveformViewModel` (or fold into the timeline VM) exposing `Peaks` (float[]), `IsLoading`,
  `HasAudio`. Populated from the waveform service.
- **App — view:** extend **`TimelineView`** with a waveform band above its `Track` (one control = one coordinate
  system = one click handler = a playhead that spans both). Draw the waveform as a filled `StreamGeometry` /
  mirrored bars from `Peaks`, re-bucketed/scaled to the current width on `SizeChanged`. Keep the existing tick +
  playhead drawing, extended vertically over the wave.
- **Data flow:** `SplitViewModel.LoadAsync` kicks off waveform extraction in the **background** (like the keyframe
  index) — cancellable + stale-guarded (a new load cancels the prior extraction), cached per file. When peaks
  arrive, `WaveformViewModel.Peaks` updates → the view redraws. Never blocks the load or the preview.

## 5. States & edge cases (amplification)
- **States:** `Loading` (a faint baseline/shimmer while extracting) → `Ready` (waveform drawn) → `NoAudio`
  (band hidden or a flat muted line). Resize → redraw (re-bucket/scale to the new width).
- **No audio track / silent video** → `HasAudio=false` → band hidden (or flat line); never an error.
- **Extraction fails / ffmpeg missing** → best-effort `null` → band hidden; the rest of the screen works.
- **Very long video** → downsampled extraction keeps the temp + compute cheap; the peak array is width-bucketed, not
  per-sample.
- **Extraction in flight when a new file loads or Clear is pressed** → prior extraction cancelled + stale-guarded;
  temp swept (like the thumbnail cache) on load/clear.
- **Marker placed on a silence** → the whole point; the aligned ticks make it exact.
- **Toggle to D-001 vertical mode mid-view** → the wave+timeline unit re-flows with the stack (pure layout).

## 6. What does NOT change
- No change to the split/join engines, the `-c copy` invariant, the seek state machine, or the hover-thumbnail /
  op-state / progress systems. The waveform is a **new read-only visual layer** over the existing timeline; every
  existing command/binding is intact. Adds one Core service + one VM + a `TimelineView` extension.

## 7. Out of scope
Automatic silence-detection / auto-cut-at-silence (a separate feature — this is the *visual* that would make that
worth building later); spectrogram / per-frequency view; audio editing or level metering during playback; a Join
per-clip waveform (D3 = Split only).

## 8. Next
`todo-design-done` seals this, then `todo-task D-002` decomposes it into a build epic (`perfect-dev`: the Core
`IWaveformService` + peak extraction + cache & tests → the `WaveformViewModel` → the `TimelineView` waveform band +
fused playhead/markers/click → background wiring in `LoadAsync`). Pairs naturally after (or alongside) **D-001**.

_Status: **confirmed** (sealed 2026-07-19 via todo-design-done). All open questions resolved at clarify (D1 vector · D2 fused · D3 Split-only); no carries._
