# VideoSplitJoiner — Architecture

VideoSplitJoiner is a .NET 8 WPF desktop app split into two assemblies:

- **`VideoSplitJoiner.App`** (`net8.0-windows`) — the WPF UI and its view models (hand-rolled MVVM).
- **`VideoSplitJoiner.Core`** — a **UI-free** class library holding all media logic. It shells out
  to a bundled FFmpeg through a single runner choke-point.

Every operation is a lossless stream copy (`ffmpeg -c copy`) or a decode-only probe/detect pass —
the app never re-encodes.

## Layering

```
 ┌───────────────────────────────────────────────────────────────┐
 │  VideoSplitJoiner.App  (WPF, net8.0-windows)                   │
 │                                                               │
 │  MainWindow ─ TabControl                                       │
 │    ├─ Split tab  → SplitView  ⇄ SplitViewModel                 │
 │    └─ Join  tab  → JoinView   ⇄ JoinViewModel                  │
 │                                                               │
 │  MainViewModel = composition root (wires the Core graph)       │
 │  OperationViewModel = shared progress / cancel / error         │
 │  ObservableObject · RelayCommand  (hand-rolled MVVM)           │
 └───────────────┬───────────────────────────────────────────────┘
                 │  interfaces (ISplitEngine, IJoinEngine,
                 │  ISplitPointDetector, IMediaProbe)
 ┌───────────────▼───────────────────────────────────────────────┐
 │  VideoSplitJoiner.Core  (UI-free)                             │
 │                                                               │
 │   Split/     Join/      Detect/        Media/                  │
 │   SplitEngine JoinEngine SplitPoint-   MediaProbe              │
 │              +Compat-    Detector      (probe, keyframes,      │
 │              Checker                    snap, GOP)             │
 │        │        │           │             │                   │
 │        └────────┴─────┬─────┴─────────────┘                   │
 │                       ▼                                        │
 │            Ffmpeg/  FfmpegRunner · FfprobeRunner               │
 │                     (the SINGLE exec choke-point)             │
 │                     FfmpegBinaryLocator · FfmpegArgs           │
 │            Errors/  FfmpegErrorMapper · UserFacingError        │
 └───────────────────────┬───────────────────────────────────────┘
                         ▼
             bundled ffmpeg.exe / ffprobe.exe  (app-local ffmpeg/ folder)
```

**Core is UI-free by construction and by test.** `CoreIsUiFreeTests` asserts the Core assembly
references none of `PresentationFramework`, `PresentationCore`, or `WindowsBase`, keeping the UI ⇄
Core seam clean and Core independently testable/reusable.

## The FFmpeg choke-point

All FFmpeg / ffprobe execution flows through **`FfmpegRunner`** / **`FfprobeRunner`** (`Core/Ffmpeg/`).
No engine spawns a process directly. The runner:

- Launches with `UseShellExecute=false`, redirected std streams, no window; closes stdin so FFmpeg
  never blocks reading it; drains stdout so a full pipe cannot deadlock the child.
- Streams stderr line-by-line into a rolling tail buffer **and** a progress parser (progress 0..1
  derived from FFmpeg's `time=` markers against a supplied total duration).
- **Never throws on a non-zero exit** — it returns an `FfmpegResult` (exit code + stderr tail) for
  any exit code. Callers decide what a failure means. (`FfprobeRunner` does throw
  `FfprobeException` on failure, since a failed probe is genuinely exceptional.)
- On cancellation, **kills the entire process tree** and throws `OperationCanceledException`.

Arguments are built with **`FfmpegArgs`** — a typed, `ArgumentList`-based builder (no shell string
concatenation), so paths with spaces/quotes are safe.

## Binary resolution

**`FfmpegBinaryLocator`** resolves `ffmpeg` / `ffprobe` in this order, per tool:

1. **Explicit override** path passed to the constructor (used by integration tests).
2. **App-local `ffmpeg/` folder** next to the running assembly (`AppContext.BaseDirectory`) — this
   is how the packaged distributable finds its bundled binaries.
3. **PATH** — the bare name, letting the OS resolve it (only if actually discoverable, so a helpful
   error is thrown otherwise).

If nothing resolves, it throws `FfmpegNotFoundException` with guidance.

## Engine contracts

All three engines share the same guarantees: they build their command through a dedicated
args-builder that **structurally cannot** emit an encoder flag, and they **re-assert the invariant
at runtime** before launching (so a mis-built command is refused, not run). The invariants are also
asserted by unit tests on the produced token lists.

### Split — `SplitEngine` (`Core/Split/`)

- **Input:** a `SplitRequest` (input path, requested cut points, output dir, naming pattern,
  overwrite flag).
- **Behavior:** probe for duration + keyframes; `SplitPlanner` (pure, unit-tested) validates cuts
  (sort, dedupe within epsilon, drop out-of-bounds), **snaps each cut to the nearest keyframe**, and
  builds contiguous segments `[0..s1],[s1..s2],…,[sN..end]`. Extraction is a **single stream-copy
  pass** through FFmpeg's **segment muxer** (`-map 0 -c copy -f segment -segment_times … -reset_timestamps 1`).
- **No-re-encode invariant:** `SplitArgsBuilder` forbids every encoder-ish token (`-c:v`, `-crf`,
  `libx264`, `-vf`, …) and requires a bare `copy`. `SatisfiesCopyInvariant` is checked before the run.
- **Output:** a `SplitResult` — one `SplitSegment` per output file recording the requested boundary,
  the **actual snapped boundary**, and the **signed delta** — plus non-fatal warnings. Segments are
  extracted to a temp dir and moved into place only after FFmpeg succeeds, so a cancel never leaves a
  half-written final segment.

### Join — `JoinEngine` + `CompatChecker` (`Core/Join/`)

- **Input:** a `JoinRequest` (ordered input paths, output path, overwrite flag).
- **Compat pre-flight:** `CompatChecker` (pure) takes the first clip as the reference and compares
  each other clip's video (codec, width, height, pix_fmt, time_base) and audio (codec, sample_rate,
  channels). Each difference — and any missing/failed-to-probe input — is one `Mismatch` in a
  `CompatReport`. A single input is trivially compatible.
- **Refusal contract:** if the set is not compatible, `JoinAsync` returns `JoinResult.Refused(report)`
  and **writes no output**. The UI turns the mismatches into a friendly error naming the offending
  clip and field. There is no re-encode fallback.
- **Behavior (compatible only):** stream-copy **concat demuxer** — a temp list file of quoted
  absolute paths is written, then `-f concat -safe 0 -i list -map 0 -c copy out`. `JoinArgsBuilder`
  enforces the same copy invariant as split. Output is written to a temp file and moved into place;
  cancellation removes the partial output.
- **Output:** a `JoinResult` — success (with the written path) or refusal (with the report).

### Detect — `SplitPointDetector` (`Core/Detect/`)

- **Input:** a file path + `DetectOptions` (which passes to run, thresholds, max candidates).
- **Decode-only invariant:** every pass outputs to the **null muxer** (`-vf <filter> -an -f null -`)
  — it writes no file and never re-encodes. `DetectArgsBuilder.SatisfiesDecodeOnlyInvariant` requires
  `-f null`, a trailing `-` sink, and no encoder token; the detector asserts it before each run.
- **Behavior:** up to three decode-only passes — **black** (`blackdetect`), **white**
  (`negate,blackdetect`), **scene** (`select='gt(scene,thr)',metadata=print`). Detection data is
  parsed from **stderr** only. Hits are merged within a window, snapped to the nearest keyframe, and
  returned as **ranked** `Candidate`s (rank 1 = best), capped at `MaxCandidates`.
- **Output:** a ranked candidate list. An empty list is a valid result (no events found), never an
  exception.

## Media probe (`Core/Media/`)

`MediaProbe` (over `FfprobeRunner`) provides:

- `ProbeAsync` — duration, container, and video/audio `StreamInfo` (codec, resolution, pix_fmt,
  sample rate, channels, time base). A bad/corrupt file returns a typed `ProbeResult.ProbeFailed`,
  not an exception.
- `GetKeyframesAsync` — sorted, distinct keyframe timestamps of the first video stream
  (`-skip_frame nokey`), **cached** by (path, mtime, length) so repeat calls are cheap.
- `SnapToNearestKeyframe` — nearest keyframe to a requested time; **ties resolve to the earlier**
  keyframe; requests past the ends **clamp**.
- `AverageGop` — mean keyframe spacing, used to warn when snapping will be coarse.

## Errors (`Core/Errors/`)

`FfmpegErrorMapper` turns a raw stderr tail + exit code into a `UserFacingError` (friendly category
+ headline + optional hint) via signature matching (disk full, permission denied, unsupported codec,
incompatible join, corrupt input, cancelled, …). The **raw tail is always preserved** on the error
so the UI's "Details" expander can show real FFmpeg output — a bare stderr string is never the headline.

## MVVM / composition-root shape

The UI uses **hand-rolled MVVM**: `ObservableObject` (INotifyPropertyChanged base) and `RelayCommand`
(ICommand) — no MVVM framework.

- **`MainViewModel`** is the **composition root**. Its parameterless ctor builds the real Core graph
  once — `FfmpegBinaryLocator` → `FfprobeRunner`/`FfmpegRunner` → `MediaProbe` → `SplitEngine`,
  `JoinEngine`, `SplitPointDetector` — and shares the probe across both screens. A second,
  DI-style ctor lets tests inject already-composed screen view models with fakes.
- **`SplitViewModel`** / **`JoinViewModel`** are the two screens, each constructor-injected with Core
  interfaces (so they are fully unit-testable without FFmpeg).
- **`OperationViewModel`** is composed into both screens to give split/join/detect a shared
  progress + cancel + friendly-error lifecycle. It is WPF-free (marshals via `Progress<T>`), so it
  runs off the UI thread under test. It maps engine failures (typed results *and* exceptions) into
  `UserFacingError`s.
- The view models themselves are **WPF-free and constructor-injected** — the WPF dependency lives
  only in the `App` assembly's views and `App.xaml`, never in Core.
