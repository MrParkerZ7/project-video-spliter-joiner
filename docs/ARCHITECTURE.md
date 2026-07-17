# VideoSplitJoiner — Architecture

VideoSplitJoiner is a .NET 8 WPF desktop app split into two assemblies:

- **`VideoSplitJoiner.App`** (`net8.0-windows`) — the WPF UI and its view models (hand-rolled MVVM).
- **`VideoSplitJoiner.Core`** — a **UI-free** class library holding all media logic. It shells out
  to a bundled FFmpeg through a single runner choke-point.

Every split/join operation is a lossless stream copy (`ffmpeg -c copy`) or a decode-only probe pass
— the engine never re-encodes. The in-app preview *does* decode (through FFME/FFmpeg), but only to
display frames on screen; it never touches the file that is cut.

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
                 │  IMediaProbe)
 ┌───────────────▼───────────────────────────────────────────────┐
 │  VideoSplitJoiner.Core  (UI-free)                             │
 │                                                               │
 │   Split/     Join/            Media/                           │
 │   SplitEngine JoinEngine      MediaProbe                       │
 │              +Compat-         (probe, keyframes,               │
 │              Checker           snap, GOP)                      │
 │        │        │                 │                           │
 │        └────────┴─────────┬───────┘                           │
 │                           ▼                                    │
 │            Ffmpeg/  FfmpegRunner · FfprobeRunner               │
 │                     (the SINGLE exec choke-point)             │
 │                     FfmpegBinaryLocator · FfmpegArgs           │
 │            Errors/  FfmpegErrorMapper · UserFacingError        │
 └───────────────────────┬───────────────────────────────────────┘
                         ▼
        one bundled ffmpeg SHARED build  (app-local ffmpeg/ folder)
        ├─ shared *.dll  → P/Invoke-loaded by FFME for the preview
        └─ ffmpeg.exe / ffprobe.exe → shelled out to by the engine
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

**Process std streams are decoded as UTF-8.** Both runners set
`StandardOutputEncoding = StandardErrorEncoding = Encoding.UTF8` on the `ProcessStartInfo`. ffmpeg /
ffprobe emit UTF-8 (ffprobe's JSON on stdout, diagnostics on stderr) regardless of the Windows console
codepage; without this the reader would fall back to the console's default codepage (cp1252 / cp932 /
…) and garble the bytes. Decoding as UTF-8 is what makes **non-ASCII (unicode) file paths** survive
intact through the probe JSON and the stderr tail instead of becoming mojibake — the fix that also
resolved the `.ts`/mpegts split failure whose exit `-28` was a mangled-path symptom.

## Binary resolution

One bundled ffmpeg **shared build** feeds two consumers, resolved by two independent mechanisms that
both point at the same app-local `ffmpeg/` folder:

**Engine exes — `FfmpegBinaryLocator`.** All split/join/probe execution resolves `ffmpeg` / `ffprobe`
in this order, per tool:

1. **Explicit override** path passed to the constructor (used by integration tests).
2. **App-local `ffmpeg/` folder** next to the running assembly (`AppContext.BaseDirectory`) — this
   is how the packaged distributable finds its bundled exes.
3. **PATH** — the bare name, letting the OS resolve it (only if actually discoverable, so a helpful
   error is thrown otherwise).

If nothing resolves, it throws `FfmpegNotFoundException` with guidance.

**Preview DLLs — `Library.FFmpegDirectory`.** The FFME preview P/Invoke-loads the native ffmpeg
**shared libraries** (`avcodec-*`, `avformat-*`, `avutil-*`, …, ffmpeg 7.x ABI). `App.OnStartup`
sets `Unosquare.FFME.Library.FFmpegDirectory` **before any FFME control loads**, pointing it at the
folder holding those DLLs. It probes, in order: the packaged app-local `ffmpeg/` folder, a repo-local
`ffmpeg-shared/` found by walking up from `BaseDirectory` (dev tree), then an absolute dev fallback —
selecting the first that actually contains an `avcodec-*.dll`. This step is **best-effort**: if no
shared build is found the preview is simply unavailable (the "preview unavailable" banner), and
startup never crashes over it.

Because the packaged `ffmpeg/` folder carries **both** the shared DLLs and the exes, one folder
satisfies both mechanisms.

## Engine contracts

Both engines share the same guarantees: they build their command through a dedicated
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

## In-app preview player + timeline (`App/Media/`, `App/ViewModels/`)

The Split screen embeds a live video preview and a visual cut-selection strip. This layer lives
**entirely in the `App` assembly** — Core stays UI-free — and is built so all its *logic* is
testable without a GUI or real playback.

### The player abstraction — `IMediaPlayer` (`App/Media/`)

`IMediaPlayer` is a small, testable transport contract: `Position` (get/set — the setter seeks),
`Duration`, `IsPlaying`, `Open` / `Play` / `Pause` / `Stop` / `Seek` / `StepFrame`, the audio/speed
knobs `Volume` / `IsMuted` / `SpeedRatio`, plus the events `PositionChanged`, `DurationAvailable`,
`Ended`, and `Failed` (carrying a human-readable reason). Two implementations:

- **`FfmeMediaPlayer`** — the production impl. It wraps an **FFME** `Unosquare.FFME.MediaElement`
  (`ffme.win`) handed in from the view (`LoadedBehavior=Manual`, `ScrubbingEnabled=true`, so it fully
  drives transport). Because FFME decodes through **FFmpeg**, the preview plays formats WPF's native
  `MediaElement` could not (HEVC, MKV, many container/codec combos) — it now plays what the app can
  cut. FFME's transport methods are asynchronous, so each is adapted fire-and-forget with faults
  routed to `Failed`; FFME raises `PositionChanged` natively, so there is **no `DispatcherTimer`** (a
  change from the retired WPF `MediaElementPlayer`). The element's `MediaOpened` / `MediaEnded` /
  `MediaFailed` map to `DurationAvailable` / `Ended` / `Failed`. `Volume` / `IsMuted` / `SpeedRatio`
  map straight to the FFME control's properties. It is thin WPF plumbing — **not unit-tested, only
  compiled** — and verified live via `app-run`.
- **`NullMediaPlayer`** — a no-op null object (shared singleton). It is the **default** player when a
  `SplitViewModel` is constructed without one, so pre-player constructions and tests keep working; it
  records nothing, plays nothing, and raises no events.

### 4K preview strategy — HW-decode + downscale (never the cut)

Smooth 4K playback is handled entirely inside the preview path, on FFME's pre-open hook
(`MediaOpening`), and never touches the cut:

- **Hardware decoding** — the probed video stream's compatible hardware devices (D3D11VA / DXVA2 /
  …) are handed to `MediaOptions.VideoHardwareDevices`, letting FFME decode on the GPU; an empty
  list falls back to software decode.
- **Downscaled preview surface** — `PreviewScale` (a pure, unit-tested geometry helper in
  `App/Media/`) computes an even-dimensioned target height (capped at ~1080p, aspect-preserving,
  never upscaling) and builds an ffmpeg `scale=W:H` `VideoFilter`, so a 3840×2160 source renders at
  ~1080p and the WPF UI thread isn't saturated pushing full 4K BGRA frames every tick.

Both steps are best-effort and independently guarded — a HW-init or filter-build failure falls back
silently to software / native-resolution decode, never a crash. Crucially this affects **only** the
on-screen preview: the split is `-c copy` and never decodes, so **the cut always runs at the source's
full resolution** regardless of the preview scale.

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

**Player-control surface (find the exact split point).** On top of play/pause/stop/scrub the VM
exposes the fine-navigation controls the Split screen surfaces so the user can land the precise
frame before "Set cut at playhead":

- **`SkipCommand`** — relative jog by a signed seconds delta (the bound buttons pass ±1 / ±5 / ±10 /
  ±20 / ±60 / ±300 / ±600 / ±1200 — the ±10m / ±20m buttons are the last two), clamped to
  `0..Duration`.
- **`StepForwardCommand` / `StepBackCommand`** — single-frame `StepFrame(±1)` (a paused operation on
  the underlying FFME player).
- **`JumpToStartCommand` / `JumpToEndCommand`** — seek to `00:00` / the full duration.
- **`Volume`** (0..1, clamped) + **`MuteCommand`** / `IsMuted` — muting toggles the player's
  `IsMuted` without disturbing the `Volume` slider, so unmute restores the prior level.
- **`SpeedRatio`** against a `SpeedPresets` list (**0.25× … 2×**), written straight to the player.

All jog/step/jump commands are gated on `IsReady`. Every control remains WPF-free and unit-tested via
the fake player.

### The timeline strip — `TimelineMath` / `TimelineViewModel` / `TimelineTick`

- **`TimelineMath`** — a pure, WPF-free pair of inverse mappings: `ToNormalized(time, duration)` →
  `[0,1]` (for rendering: tick X = normalized × width) and `FromNormalized(x, duration)` → time (for
  a track click). Both clamp their inputs, so a click past either edge or a zero/unknown duration can
  never divide by zero or escape the box.
- **`TimelineViewModel`** — a projection over the owning `SplitViewModel`. It observes the player's
  Position/Duration and the `Markers` collection and re-projects a `PlayheadNormalized` plus a flat,
  bindable `MarkerTicks` list whenever anything moves.
- **`TimelineTick`** — one projected tick: its normalized X, source time, and a `Ref` back to the
  originating `CutMarkerViewModel` for click routing.

### How visual cuts reuse the existing snap path (no new snap logic)

The two visual ways to place a cut — **"Set cut point at playhead"** (`SplitViewModel.SetCutAtPlayhead`
→ `SetCutAtPlayheadCommand`) and **clicking the timeline** (`TimelineViewModel.ClickAt` →
`FromNormalized` → the owner) — both funnel into `SplitViewModel.AddCutAt(TimeSpan)`, the **single**
entry point that manual add already used. `AddCutAt` builds a snapping `CutMarkerViewModel` and
**dedupes on the snapped keyframe**, so every cut — typed, playhead-captured, or timeline-clicked —
snaps and de-dupes identically. There is deliberately **no second snap implementation**. Clicking a
marker tick routes to `SeekToMarkerCommand` (seek to the marker's *snapped* time), reusing a command
that already existed on `SplitViewModel`.

`SetCutAtPlayhead` is guarded by `CanSetCutAtPlayhead` (`HasFile && Player.IsReady`), so it only
enables once the preview has a real playhead to capture.

### Non-blocking load — probe → preview, then background keyframe index

`SplitViewModel.LoadAsync` gates on **only** the fast metadata probe. As soon as `ProbeAsync`
succeeds it commits `Info` / `InputPath` and **opens the preview** (`Player.Open`) at once — the load
no longer blocks on the (formerly synchronous) full keyframe scan. The keyframe index then runs in a
**cancellable background task** started by `StartKeyframeIndex`:

- **`IsIndexingKeyframes`** flips true while the background scan runs and false when it completes,
  faults, or is cancelled; the view binds it to a non-blocking **"indexing…"** hint. **`KeyframesReady`**
  (`HasFile && !IsIndexingKeyframes`) is the "index done" signal.
- **Stale-guard.** Each load cancels the previous file's in-flight index via a per-load
  `CancellationTokenSource`, and the completion continuation drops its result unless it is still the
  current scan — so a slow scan of an old file can never overwrite the newer file's keyframes. A scan
  that has already completed synchronously (a cache hit, or a fake in tests) commits inline with no
  thread hop.
- **Snap-before-ready.** A cut placed while indexing is still running awaits the **same** in-flight
  scan (`EnsureKeyframesAsync` → the stored index task) before snapping, so it snaps against the real
  keyframes rather than an empty list; if that index failed/was cancelled it falls back to whatever
  `Keyframes` holds (an identity snap on empty), never crashing. When keyframes are already present a
  cut is added synchronously, exactly as before.

## Drag and drop (`App/Views/`, `App/VideoFileFilter.cs`)

Drag-and-drop adds **no new load / add / reorder logic** — it is thin WPF **code-behind** wiring that
routes drop and drag events to the view-model commands that already existed.

- **The accept filter is a pure helper.** `VideoFileFilter` (in `App`, WPF-free so it is directly
  unit-testable) exposes `AcceptVideoFiles(paths)` — keep only known video extensions
  (`.mp4 .mkv .mov .avi .m4v .webm .ts .mpg .mpeg .wmv .flv`, case-insensitive), dedupe on the full
  path, preserve first-seen order — and `HasAnyVideo(paths)`, used by the `DragOver` accept check to
  decide whether to show the copy effect + drop highlight. Non-video paths are dropped.
- **External file drop → existing VM commands.** `SplitView` code-behind filters the dropped paths
  and loads the **first** video via `SplitViewModel.LoadCommand` (Split is single-file). `JoinView`
  code-behind adds **all** dropped videos (order preserved) via `JoinViewModel.AddFilesCommand`, whose
  compatibility re-check then runs. The drop-routing methods (`HandleDroppedFiles`) are extracted as
  `internal static` so they are testable without a live drag.
- **Internal reorder vs external file-drop are distinguished by clipboard data format.** On the Join
  clip list, dragging a row starts a `DragDrop.DoDragDrop` carrying a `JoinItemViewModel` payload
  (the `typeof(JoinItemViewModel)` format — deliberately **not** `DataFormats.FileDrop`). The list's
  drop handler treats a `JoinItemViewModel` payload as a **reorder** and marks the event handled; any
  other payload is left to bubble up to the root grid's `FileDrop` handler, which treats it as an
  **external add**. So the same surface accepts both gestures without ambiguity.
- **One reorder path shared by drag and Up/Down.** A reorder drop computes `from`/`to` list indices
  and calls `JoinViewModel.Move(from, to)` — the synchronous wrapper over `MoveAsync(from, to)`. The
  **Up/Down buttons delegate to the same `MoveAsync`** (`MoveUpAsync`/`MoveDownAsync` compute the
  neighbouring index and call it), so drag-reorder and button-reorder run through one implementation.

## Media probe (`Core/Media/`)

`MediaProbe` (over `FfprobeRunner`) provides:

- `ProbeAsync` — duration, container, and video/audio `StreamInfo` (codec, resolution, pix_fmt,
  sample rate, channels, time base). A bad/corrupt file returns a typed `ProbeResult.ProbeFailed`,
  not an exception.
- `GetKeyframesAsync` — sorted, distinct keyframe timestamps of the first video stream, **cached** by
  (path, mtime, length) so repeat calls are cheap. It uses a **demux-level packet-flag scan** as its
  primary path (see below) with a decode-based fallback.
- `SnapToNearestKeyframe` — nearest keyframe to a requested time; **ties resolve to the earlier**
  keyframe; requests past the ends **clamp**.
- `AverageGop` — mean keyframe spacing, used to warn when snapping will be coarse.

### Keyframe scan — demux packet-flag (fast) with a frame-decode fallback

`GetKeyframesAsync` reads keyframes at the **demux (packet) layer** rather than decoding frames. The
primary path (`ScanKeyframesFromPacketsAsync`) runs
`ffprobe -select_streams v:0 -show_packets -show_entries packet=pts_time,dts_time,flags` and keeps
packets whose `flags` carry the **`K`** keyframe marker, taking `pts_time` (falling back to
`dts_time`) as the timestamp. Because packets arrive in DTS order the times are sorted-distinct before
return. Skipping frame decode makes this markedly faster on high-resolution sources (measured ~3.86×
on a 4K clip). If the packet query throws or yields **zero** keyframes, it **falls back** to the
decode-based scan (`ScanKeyframesFromFramesAsync`, the pre-existing `-skip_frame nokey` frame pass) so
correctness never regresses. Both paths produce the same sorted-distinct output; the cache, snapping,
and `AverageGop` are unchanged. (Which path ran is tracked internally for tests only.)

## Errors (`Core/Errors/`)

`FfmpegErrorMapper` turns a raw stderr tail + exit code into a `UserFacingError` (friendly category
+ headline + optional hint) via signature matching (disk full, permission denied, unsupported codec,
incompatible join, corrupt input, cancelled, …). The **raw tail is always preserved** on the error
so the UI's "Details" surface can show real FFmpeg output — a bare stderr string is never the headline.

- **Exit `-28` / `ENOSPC` → `DiskFull`.** The mapper keys the disk-full category on the **exit code**
  (`-28` == `AVERROR(ENOSPC)`) as well as the `"No space left on device"` / `ENOSPC` stderr phrases.
  An out-of-space write often leaves only an unrelated benign mpegts warning (`start time for stream N
  is not set…`) in the tail, so keying on the phrase alone would mis-classify it as `Unknown` and
  surface the warning as the headline. `SplitEngine` also runs a **best-effort pre-flight free-space
  check** (`EnsureEnoughFreeSpace` via `DriveInfo`) so an obviously-too-small output drive fails early
  with the friendly `DiskFull` message rather than mid-write; any inability to measure skips the check.
- **Copyable error + saved full log.** `UserFacingError` carries `FullText` (the complete diagnostic
  text — headline + full stderr, not just the tail) and `LogFilePath` (the on-disk log for the run),
  and exposes computed `CopyText` / `DetailText` / `HasLogFile` so the copy surface and read-only
  detail box are identical and unit-testable. **`ErrorLogWriter`** writes the full log — a UTC
  timestamp, the exact command, the exit code, and the complete stderr (`BuildLogBody`, deterministic
  and I/O-free so it is testable) — to `%LOCALAPPDATA%/VideoSplitJoiner/logs/<op>-<yyyyMMdd-HHmmss>.log`
  (base dir injectable for tests). Writing is **best-effort** — any failure returns `null` and never
  crashes the operation. In the `App` layer, `ErrorActions` (thin code-behind glue) copies `CopyText`
  to the clipboard and reveals the log file in Explorer; both `SplitView` and `JoinView` expose a
  **Copy error** + **Open log file** button over a selectable error box.

## Settings store (`App/Settings/`)

`AppSettings` (behind `IAppSettings`) persists the two "remember where I was" folders —
`LastInputDir` / `LastOutputDir` — to `%APPDATA%/VideoSplitJoiner/settings.json` via
`System.Text.Json` (file path injectable for tests, mirroring `ErrorLogWriter`'s convention). Setting a
property saves immediately via a **temp-then-rename** write so a crash mid-write can't replace a good
file with a half-written one. It is **robust by design** — a missing file, corrupt JSON, or an
unwritable dir all fall back to in-memory defaults and never throw. The file picker seeds its
`InitialDirectory` from `LastInputDir`, `SplitViewModel` defaults its output directory to
`LastOutputDir` (on construction and after a load), and both are written back when a run's input/output
folders are chosen — so the app reopens where you left off.

## MVVM / composition-root shape

The UI uses **hand-rolled MVVM**: `ObservableObject` (INotifyPropertyChanged base) and `RelayCommand`
(ICommand) — no MVVM framework.

- **`MainViewModel`** is the **composition root**. Its parameterless ctor builds the real Core graph
  once — `FfmpegBinaryLocator` → `FfprobeRunner`/`FfmpegRunner` → `MediaProbe` → `SplitEngine`,
  `JoinEngine` — and shares the probe across both screens. A second, DI-style ctor lets tests inject
  already-composed screen view models with fakes.
- **`SplitViewModel`** / **`JoinViewModel`** are the two screens, each constructor-injected with Core
  interfaces (so they are fully unit-testable without FFmpeg).
- **`OperationViewModel`** is composed into both screens to give split/join a shared
  progress + cancel + friendly-error lifecycle. It is WPF-free (marshals via `Progress<T>`), so it
  runs off the UI thread under test. It maps engine failures (typed results *and* exceptions) into
  `UserFacingError`s.
- The view models themselves are **WPF-free and constructor-injected** — the WPF dependency lives
  only in the `App` assembly's views and `App.xaml`, never in Core.
- **`MainViewModel` composes the real player** by passing a `new FfmeMediaPlayer()` into
  `SplitViewModel`. The player starts unattached; `PlayerView`'s code-behind calls `Attach(Media)` on
  load to bind it to the view's FFME `MediaElement` (the one place WPF and the player meet). A
  `SplitViewModel` built without a player falls back to `NullMediaPlayer`, keeping tests and non-UI
  constructions working.

## Design decisions (preview player)

### D1 — FFME/FFmpeg for playback (replaces WPF `MediaElement`)

The preview decodes through **FFME** (`Unosquare.FFME.MediaElement`, package `FFME.Windows`), which
P/Invoke-loads the bundled ffmpeg **shared** libraries. This replaces the earlier WPF built-in
`MediaElement` (Windows Media Foundation). The reason is **codec coverage**: Media Foundation handled
fewer exotic containers/codecs than the bundled FFmpeg, so files that cut perfectly could fail to
*preview*. Decoding the preview through the same FFmpeg the engine uses means **the preview plays
exactly what the app can cut** (HEVC, MKV, 4K, …), so the "preview unavailable" path is now rare. The
app still never decodes frames for the *cut* itself — that stays `-c copy`; FFME's decoding is
preview-only. Startup points FFME at the shared DLLs via `Library.FFmpegDirectory` before any FFME
control loads; a still-unplayable file is handled gracefully via the `Failed` → banner path.

### D2 — `IMediaPlayer` abstraction so the VMs stay testable

Playback is behind the `IMediaPlayer` seam specifically so `PlayerViewModel` and `TimelineViewModel`
carry **no WPF types** and can be exercised headlessly with a fake player (position/duration/
events driven by the test). The only piece that touches WPF — `FfmeMediaPlayer` — is thin plumbing
that "just has to compile"; its **live playback is verified only via `app-run`** on a real desktop,
not in the unit suite. This keeps the testable logic (scrub-seam suppression, ready-gating, jog/step/
speed/volume control, tick projection, click routing) fully covered while confining the untestable
WPF surface to one class. The seam is exactly what let the player swap from `MediaElementPlayer` to
`FfmeMediaPlayer` without any view-model or timeline change.
