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

## In-app preview player + timeline (`App/Media/`, `App/ViewModels/`)

The Split screen embeds a live video preview and a visual cut-selection strip. This layer lives
**entirely in the `App` assembly** — Core stays UI-free — and is built so all its *logic* is
testable without a GUI or real playback.

### The player abstraction — `IMediaPlayer` (`App/Media/`)

`IMediaPlayer` is a small, testable transport contract: `Position` (get/set — the setter seeks),
`Duration`, `IsPlaying`, `Open` / `Play` / `Pause` / `Stop` / `Seek`, plus the events
`PositionChanged`, `DurationAvailable`, `Ended`, and `Failed` (carrying a human-readable reason).
Two implementations:

- **`MediaElementPlayer`** — the production impl. It wraps a WPF `MediaElement` handed in from the
  view (`LoadedBehavior=Manual`, `ScrubbingEnabled=true`, so it fully drives transport). A ~200ms
  `DispatcherTimer` polls position and pumps `PositionChanged` while playing; the element's own
  `MediaOpened` / `MediaEnded` / `MediaFailed` map to `DurationAvailable` / `Ended` / `Failed`. It is
  thin WPF plumbing — **not unit-tested, only compiled** — and verified live via `app-run`.
- **`NullMediaPlayer`** — a no-op null object (shared singleton). It is the **default** player when a
  `SplitViewModel` is constructed without one, so pre-player constructions and tests keep working; it
  records nothing, plays nothing, and raises no events.

### The player view model — `PlayerViewModel`

`PlayerViewModel` sits over an `IMediaPlayer` and exposes a **WPF-free** transport surface:
observable `Position` / `Duration` / `IsPlaying` / `IsReady`, formatted `PositionText` /
`DurationText` (`mm:ss.f`), a slider-friendly `PositionSeconds` / `DurationSeconds`, the
`PlayPauseCommand` / `StopCommand`, and a `PreviewFailed` + `PreviewFailedReason` pair that drives
the "preview unavailable" banner. The `Position` setter is the **scrub seam**: a user-driven set
(bound slider) calls `Seek`, while a player-driven `PositionChanged` echo is applied under a
`_suppressSeek` guard so a playback tick can never loop back into a re-seek. `IsReady` gates play and
scrubbing until the duration is known. Because it holds no WPF types, it is fully unit-tested with a
fake `IMediaPlayer`.

### The timeline strip — `TimelineMath` / `TimelineViewModel` / `TimelineTick`

- **`TimelineMath`** — a pure, WPF-free pair of inverse mappings: `ToNormalized(time, duration)` →
  `[0,1]` (for rendering: tick X = normalized × width) and `FromNormalized(x, duration)` → time (for
  a track click). Both clamp their inputs, so a click past either edge or a zero/unknown duration can
  never divide by zero or escape the box.
- **`TimelineViewModel`** — a projection over the owning `SplitViewModel`. It observes the player's
  Position/Duration and the `Markers` / `Candidates` collections and re-projects a `PlayheadNormalized`
  plus two flat, bindable tick lists (marker ticks + candidate ticks) whenever anything moves.
- **`TimelineTick`** — one projected tick: its normalized X, source time, candidate `Kind` (null for
  marker ticks, so the view colours candidates by Black/White/Scene), and a `Ref` back to the
  originating marker/candidate view model for click routing.

### How visual cuts reuse the existing snap path (no new snap logic)

The two new ways to place a cut — **"Set cut point at playhead"** (`SplitViewModel.SetCutAtPlayhead`
→ `SetCutAtPlayheadCommand`) and **clicking the timeline** (`TimelineViewModel.ClickAt` →
`FromNormalized` → the owner) — both funnel into `SplitViewModel.AddCutAt(TimeSpan)`, the **single**
entry point that manual add already used. `AddCutAt` builds a snapping `CutMarkerViewModel` and
**dedupes on the snapped keyframe**, so every cut — typed, playhead-captured, timeline-clicked, or
from an accepted candidate — snaps and de-dupes identically. There is deliberately **no second snap
implementation**. Clicking a marker tick routes to `SeekToMarkerCommand` (seek to the marker's
*snapped* time); clicking a candidate tick routes to `PreviewCandidateCommand` (seek to the
candidate's *raw detected* time) — both reuse commands that already existed on `SplitViewModel`.

`SetCutAtPlayhead` is guarded by `CanSetCutAtPlayhead` (`HasFile && Player.IsReady`), so it only
enables once the preview has a real playhead to capture.

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
- **`MainViewModel` composes the real player** by passing a `new MediaElementPlayer()` into
  `SplitViewModel`. The player starts unattached; `PlayerView`'s code-behind calls `Attach(Media)` on
  load to bind it to the view's `MediaElement` (the one place WPF and the player meet). A
  `SplitViewModel` built without a player falls back to `NullMediaPlayer`, keeping tests and non-UI
  constructions working.

## Design decisions (preview player)

### D1 — WPF `MediaElement` for playback (FFME parked)

The preview uses WPF's built-in **`MediaElement`** (Windows Media Foundation) rather than a
richer player. It ships in-box, needs no extra dependency, and is enough for a *preview* — the app
never decodes frames for cutting itself (that is FFmpeg's job). The trade-off is **codec coverage**:
Media Foundation handles fewer exotic containers/codecs than the bundled FFmpeg does, so some files
that cut perfectly may fail to *preview*. That path is handled gracefully (see below).
**FFME** (`FFmpegMediaElement`) is the **parked upgrade**: if Media Foundation's coverage proves too
narrow in practice, swapping the `MediaElementPlayer` impl behind `IMediaPlayer` for an FFME-backed
one is a contained change — no view-model or timeline code moves.

### D2 — `IMediaPlayer` abstraction so the VMs stay testable

Playback is behind the `IMediaPlayer` seam specifically so `PlayerViewModel` and `TimelineViewModel`
carry **no WPF types** and can be exercised headlessly with a fake player (position/duration/
events driven by the test). The only piece that touches WPF — `MediaElementPlayer` — is thin plumbing
that "just has to compile"; its **live playback is verified only via `app-run`** on a real desktop,
not in the unit suite. This keeps the testable logic (scrub-seam suppression, ready-gating,
tick projection, click routing) fully covered while confining the untestable WPF surface to one class.
