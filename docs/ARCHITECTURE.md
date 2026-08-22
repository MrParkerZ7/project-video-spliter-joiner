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
 │  MainWindow (WindowChrome dark caption + taskbar progress)     │
 │    └─ TabControl                                              │
 │        ├─ Split tab → SplitView  ⇄ SplitViewModel  (2-column) │
 │        └─ Join  tab → JoinView   ⇄ JoinViewModel   (2-column) │
 │                                                               │
 │  MainViewModel = composition root (wires the Core graph)       │
 │  OperationViewModel = 4-surface progress / cancel / error      │
 │  Themes/ Tokens · Controls  (dark+gold token system, Plex font)│
 │  ObservableObject · RelayCommand  (hand-rolled MVVM)           │
 │  App.xaml.cs = FFME init + global crash safety-net             │
 └───────────────┬───────────────────────────────────────────────┘
                 │  interfaces (ISplitEngine, IJoinEngine,
                 │  IMediaProbe, IThumbnailService)
 ┌───────────────▼───────────────────────────────────────────────┐
 │  VideoSplitJoiner.Core  (UI-free)                             │
 │                                                               │
 │   Split/       Join/          Media/       Thumbnails/         │
 │   SplitEngine  JoinEngine     MediaProbe   FfmpegThumbnail-    │
 │   PartProgress +Compat-       (probe,      Service            │
 │   PartMapping   Checker        keyframes,  (hover frame grab) │
 │        │          │            snap, GOP)      │              │
 │        └──────────┴─────────┬──────┴───────────┘              │
 │                            ▼                                   │
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
  overwrite flag, and an optional `SelectedSegmentIndices`).
- **Behavior:** probe for duration + keyframes; `SplitPlanner` (pure, unit-tested) validates cuts
  (sort, dedupe within epsilon, drop out-of-bounds), **snaps each cut to the nearest keyframe**, and
  builds contiguous segments `[0..s1],[s1..s2],…,[sN..end]`. **Selected-segment routing (T-049):** when
  `SelectedSegmentIndices` is `null` (the default) or selects the **full** set, extraction is a
  **single stream-copy pass** through FFmpeg's **segment muxer** (`-map 0 -c copy -f segment
  -segment_times … -reset_timestamps 1`) — the fast path, unchanged. When a strict **subset** is
  selected, the engine instead runs the **per-segment `-ss/-to -c copy` path** — one ffmpeg run per
  chosen part — so only the wanted ranges are written (unselected parts cost no time/disk). Each part
  keeps its **original 1-based index** in the filename (a selected middle part is still `_part02`); the
  plan's final part omits `-to` (runs to EOF) while interior parts pass an explicit `-to == snapped
  end`. An empty (non-null) selection is an invalid request; out-of-range indices are ignored.
- **Staged status (T-044):** the engine takes an optional `IProgress<OperationStatus>` and reports each
  real phase as it enters it — **Preparing → Splitting (N parts) → Finalizing → Done** — so the UI's
  status line tracks the actual work, never a timer. This is separate from the numeric
  `IProgress<double>` that drives the bar.
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
- **Staged status (T-044):** like split, `JoinAsync` takes an optional `IProgress<OperationStatus>` and
  reports **Checking compatibility → Joining → Finalizing → Done** as it enters each phase.
- **Output:** a `JoinResult` — success (with the written path) or refusal (with the report).

## Bulk Cut tab — batch intro/outro trim (`Core/Bulk/`, `App/ViewModels/`, `App/Views/`)

The **third tab** (`SelectedTabIndex == 2`, sibling to Split (0) / Join (1)) batch-trims the intro —
and optionally the outro — off many videos at once, keeping the middle of each. Its one load-bearing
idea is a **reuse**, recorded as **[ADR 0015](adr/0015-bulk-trim-reuses-split-single-segment.md)**:

> **A bulk trim is a Split that keeps exactly one middle segment.** For a source with an intro-end at
> `t₁` (and an optional outro-start at `t₂`), the wanted output is the single segment `[t₁ .. (t₂ |
> EOF)]` — one of the parts a two-cut Split already produces. Bulk adds a **list**, **two cut points
> per row**, an **apply-to-all** gesture, and a **batch runner** — it adds **no new ffmpeg path**.

Every row therefore funnels through the *existing* `ISplitEngine.SplitAsync` per-segment `-c copy`
path (the T-049 subset path), so the copy invariant, keyframe-snap, temp-then-move cancel-safety, and
disk pre-flight are all inherited rather than re-implemented. The pieces:

### The batch engine — `BulkTrimEngine` (`Core/Bulk/`)

The batch loop lives in **Core**, not the view model. `BulkTrimEngine` (behind `IBulkTrimEngine`) takes
a list of `BulkTrimItem`s (source path, requested intro-end, optional outro-start, desired output path)
plus `BulkTrimOptions` (the collision policy), and runs them **sequentially and failure-isolated**:

- **Collision pre-resolve** (per item, before any run) → an effective output path + overwrite flag +
  skip decision. Default **`CollisionPolicy.AutoSuffix`** appends `_2`, `_3`, … until a free path is
  found (never clobbers); a per-run **`Overwrite`** toggle replaces in place; **under every policy the
  source path is never a write target** (a resolution that would hit the input is forced onto an
  AutoSuffix name).
- **Batch disk pre-flight** blocks the *whole* batch before any ffmpeg runs only on a knowable per-drive
  shortfall (source size is a safe upper bound on each output); an unmeasurable drive skips the check —
  never a false-positive block.
- **Sequential run**, one row at a time. Each row's request is assembled by an `IBulkTrimRequestBuilder`
  (`KeptMiddleRequestBuilder`) and handed to `SplitEngine.SplitAsync`. A row that throws is recorded
  `Failed` (a mid-run ENOSPC on one large file never aborts a smaller later one); a both-boundaries-
  collapse trim surfaces as `NoOpTrimException` → `Skipped`; **Cancel** stops before the next row,
  classifies the in-flight row `Cancelled` (its temp already swept by the split path — no partial is
  moved into place), and **keeps every already-finished output**.
- **Ledger + rollup.** It returns a `BatchResult` (`Completed` / `CompletedWithFailures` / `Cancelled` /
  `Blocked`) carrying one `BulkTrimItemResult` per row (outcome, written path, size, warnings, error),
  and reports a `BulkTrimProgress` stream (per-item fraction + a batch overall). It is **UI-free** — BCL
  + Core types only.

### The kept-index cure — `KeptSegmentSelector` (`Core/Split/`)

"Keep the middle segment" is **not always index 2**. `SplitPlanner.Plan` drops a cut that snaps to ~0
or ~duration, merges near-duplicate cuts, and drops a snap colliding with a neighbour — so if the
intro-end snaps to ~0 it is dropped and the kept part is segment **1** (`[0..outro]`), otherwise the
intro survives and it is segment **2** (`[introEnd..outro|EOF]`). The shipped cure (chosen over the
D-004 alternative of emitting a raw `PerSegment` call and bypassing `SelectedSegmentIndices`) is the
pure `KeptSegmentSelector.ResolveKeptIndex`: it runs the **real** `SplitPlanner.Plan` (never re-deriving
its drop/merge/snap rules) and reads back which planned segment starts at the snapped intro-end.
`BuildKeptMiddleRequest` then assembles the single-kept-segment `SplitRequest` — `CutPoints =
[introEnd]` or `[introEnd, outroStart]`, `NamingPattern = "{name}_trimmed{ext}"` (no `{index}` token,
safe because exactly one segment is selected), `SelectedSegmentIndices = [keptIndex]` — which drives the
engine's per-segment subset path to write only the kept middle. `KeptMiddleRequestBuilder` wraps this,
probing each source (through the shared `MediaProbe` cache, so the engine's later re-probe is free) and
translating the planner's both-collapse `SplitException` into the distinct `NoOpTrimException`.

### The two view models — `BulkCutViewModel` / `BulkItemViewModel` (`App/ViewModels/`)

Both are **WPF-free** (`ObservableObject` + Core/BCL types), mirroring `JoinViewModel` / `JoinItemViewModel`:

- **`BulkCutViewModel`** — the tab VM: an `ObservableCollection<BulkItemViewModel>` in add order, the
  apply-to-all gesture, and **`RunBatchAsync`**, which builds each runnable row's `BulkTrimItem` and
  **delegates the whole batch** to `IBulkTrimEngine.RunAsync` — it owns **no** batch loop, collision
  resolution, disk pre-flight, or cancel-sweep (all inherited from the engine). It fans the engine's
  progress back onto the rows and holds the **aggregate `OperationViewModel`** (see below). It owns and
  shares a single **`SemaphoreSlim(3)`** scan gate into every row, so adding N videos fires at most three
  concurrent keyframe scans (the thundering-herd throttle). `CanRunBatch` gates on every enabled row being
  keyframes-ready, so a request is never built on un-snapped identity times.
- **`BulkItemViewModel`** — one row: the Join-item shape (path / duration / size), **two
  `CutMarkerViewModel` handles** (intro-end required, outro-start optional/nullable — both created
  optimistically `snapPending` and resolved when the row's background scan lands), a per-file keyframe
  scan through the shared gate, computed validity (`IsValidCut` / `IsNoOpTrim` / `RowState`), its **own**
  per-row `OperationViewModel`, and the request builders (`BuildRequest` for the preview/validity cross-
  check via `KeptSegmentSelector`, `BuildBulkTrimItem` for the batch). Output path is the deterministic
  `<dir>/<name>_trimmed<ext>` until a Done ledger entry supplies the collision-resolved written path.

**Apply-to-all — outro measured from END.** `ApplyToAll(source)` copies the source row's requested
cut points to every other checked, keyframes-ready row: the intro-end as an **absolute time-from-start**,
the outro as a **time-from-end** (`Duration − outroStart`) re-anchored on each target's own duration, so
same-series episodes of *different* lengths align. Each target **re-snaps against its own keyframes and
re-validates**; rows the copy invalidated are returned in an `ApplyToAllReport` and **reported, never
silently dropped**.

**Aggregate-vs-per-row operation pattern.** Each row has its own `OperationViewModel` (per-row state /
progress / error), but the Windows taskbar button and window title bind exactly **one**
`CurrentOperation`, so the tab holds an **aggregate** `OperationViewModel` fed a weighted, monotonic-
clamped overall fraction (`WeightedOverall`, kept-duration weighted). `MainViewModel` routes a 3-way
`switch` on `SelectedTabIndex` (0/1/2) for `CurrentOperation`, `CurrentClearCommand`, `CurrentLoadLabel`
("Add videos…"), and `CurrentClearLabel` ("Clear all"), re-pointed on tab switch.

### The view — `BulkCutView` / `BulkRowScrubView` (`App/Views/`)

The tab's XAML is **view-only**: `BulkRowScrubView` renders the per-row **dual-handle scrub bar** (a
`Canvas` drawn in code-behind — gold `▸` intro-end handle, blue `◂` outro-start handle, the kept span
highlighted gold, hover frame preview) and `BulkCutView` lays out the list + tool panel, but neither
holds cut logic — all of it is on the WPF-free VMs. Keeping the batch engine, the kept-index selector,
and the request builder in Core (referencing no WPF) is what keeps **`CoreIsUiFreeTests` green**. A new
`DropScrimBrush` token backs the drag-drop highlight.

### The shared preview player + cut profiles (G-037)

G-037 adds a **preview player** and **reusable cut profiles** to the tab, recorded as
**[ADR 0016](adr/0016-shared-bulk-preview-player-and-cut-profiles.md)**:

- **One shared preview player, bound to the selected row — not one per row.** `BulkCutViewModel` owns a
  single `PlayerViewModel` over **one** `FfmeMediaPlayer` (`Player`) — the tab's own FFME element,
  distinct from the Split tab's. Selecting a row (`SelectedItem`, two-way bound from the list `ListBox`)
  opens **that** file in the one player; a null selection (list cleared / nothing selected) unloads it,
  and removing the selected row re-points the selection at a neighbour so it never opens a just-removed
  file. The pane **reuses the Split `PlayerView` wholesale** (`DataContext = Player`), inheriting the
  whole transport surface — play/pause/stop, scrub, hover-thumbnail, the ±1s…±20m jog set (`SkipCommand`),
  frame-step, jump-to-ends, and volume/speed — so there is **no second player view or view model**.
- **`MediaReopenGuard` auto-engages on a fast row switch.** Because `Player.Open` routes through
  `FfmeMediaPlayer`'s built-in `MediaReopenGuard` (T-080), a rapid row-to-row switch **supersedes the
  prior pending Open** instead of issuing one while a previous Close is still in flight (the native-AV
  race) — no bulk-specific guarding needed.
- **Only the active tab decodes.** Two FFME elements (Split + Bulk) live in one process, so
  `MainViewModel.StopInactiveScreenPlayers` stops the inactive screens' players on **every tab switch**,
  and `RunBatchAsync` stops the preview before the batch trims — at most one decoder is ever busy,
  regardless of how long the list is.
- **Set-at-playhead reuses the existing snap path.** `SetIntroAtPlayheadCommand` /
  `SetOutroAtPlayheadCommand` write the selected row's `CutMarkerViewModel.Requested` from the live
  playhead — the **same setter** the per-row scrub handles and the IN/OUT fields use — so the cut
  re-snaps to keyframes identically (no new snap logic). Both are gated on a selected row **and** a ready
  player (`CanSetCutAtPlayhead`).
- **Cut profiles are a WPF-free Core record persisted in settings.** `CutProfile` (`Core/Profiles/`) is a
  plain immutable record — `{ Name, IntroFromStart` (absolute time-from-start)`, OutroFromEnd?` (measured
  from the END; `null` ⇒ keep to EOF)` }` — validated at construction and Core-resident so it stays
  unit-testable and `CoreIsUiFree`-clean. `IAppSettings` persists it to `settings.json` as **seconds
  (double)**, not `TimeSpan` ticks, and is **backward-compatible**: a missing `cutProfiles` key loads as
  an empty list (older files stay valid), the key is omitted entirely when there are none, and a corrupt
  entry is skipped rather than crashing. Saves are **upsert-by-name** (case-insensitive, position
  preserved). The **outro-from-END** convention is what lets one profile fit episodes of different lengths
  — the same convention `ApplyToAll` uses. `CutProfileApplier` (`App/ViewModels/`, a WPF-free static
  helper) applies a profile to a set of rows (intro absolute + clamped, outro from-end + clamped, each
  target re-snapped + re-validated, invalidated rows **reported** through the shared `ApplyToAllReport` —
  never silently dropped) and builds a profile from a row's current cut, **reusing** the apply-to-all
  convention rather than duplicating it.

### Profile thumbnails + per-row cut-point frames (G-038)

G-038 adds two thumbnail affordances to the tab, both **reusing the existing frame source** — no second
ffmpeg/frame path:

- **A profile's thumbnail is a path, stored by `ProfileThumbnailStore` — never bytes in the JSON.**
  `CutProfile` gains an optional `ThumbnailPath` (Core, WPF-free; a blank normalizes to `null`, no
  existence/format check — it is metadata, not a cut offset). `ProfileThumbnailStore` (`App/Settings/`,
  BCL-only) copies a chosen frame/image into `%LOCALAPPDATA%/VideoSplitJoiner/profile-thumbs` (mirroring
  the thumb-cache root composition, OS-temp fallback) under a **deterministic, collision-resistant safe
  name** — invalid chars sanitized, a short SHA-256 suffix keying off the case-folded profile name so it
  resolves the same file across sessions and never collides — and best-effort removes it. `IAppSettings`
  round-trips the **path** as human-readable JSON (`thumbnailPath`, omitted when null → byte-clean; an
  older file with no key loads as `null` — backward-compatible), and `AppSettings.DeleteProfile`
  **cascades** to delete the stored file (by safe-name and by the exact recorded path). The T-107 glue on
  `BulkCutViewModel` (`SaveProfileWithAutoThumbnailAsync`) captures the row's **intro-end frame** via
  `IThumbnailService.GetThumbnailAsync` as the default, with `UploadThumbnail`/`ClearThumbnail` overrides —
  all **best-effort and off the save path**: the profile persists first, so a slow/failed grab or a store
  failure just leaves the placeholder and never blocks the save.
- **Per-row cut-point frames reuse `IThumbnailService` behind a dedicated concurrency gate.** Each
  `BulkItemViewModel` grabs a small frame at its keyframe-**snapped** intro-end (and outro-start, when
  `HasOutro`) through the **same** shared `IThumbnailService` the hover-preview uses — no new frame path —
  driven by an internal `HandleThumbnailGrabber` that copies `ThumbnailPreviewViewModel`'s **debounce +
  cancel-prior + latest-wins** discipline (a slower 200ms settle so a drag coalesces to one grab; results
  marshalled back over the captured `SynchronizationContext` via `Progress<T>`). Crucially the grabs run
  through a **separate** `SemaphoreSlim(3,3)` on `BulkCutViewModel` — *not* the keyframe-scan gate — so a
  large batch's eye-candy frame grabs can never starve the ffprobe keyframe scans that gate `CanRunBatch`;
  the permit is held only around the grab, never during the debounce. Every grab is best-effort (null →
  the muted placeholder chip) and cancelled per row on Remove/Clear (`CancelScan`).

## In-app preview player + timeline (`App/Media/`, `App/ViewModels/`)

The Split screen embeds a live video preview and a visual cut-selection strip. This layer lives
**entirely in the `App` assembly** — Core stays UI-free — and is built so all its *logic* is
testable without a GUI or real playback.

### The player abstraction — `IMediaPlayer` (`App/Media/`)

`IMediaPlayer` is a small, testable transport contract: `Position` (get/set — the setter seeks),
`Duration`, `IsPlaying`, `Open` / `Play` / `Pause` / `Stop` / `Seek` / `Unload` / `StepFrame`, the
audio/speed knobs `Volume` / `IsMuted` / `SpeedRatio`, plus the events `PositionChanged`, `Seeked`,
`DurationAvailable`, `Ended`, and `Failed` (carrying a human-readable reason). `Unload` (T-047) closes
the current source and blanks the preview surface (resetting duration/playing state) so the **Clear**
button can reset the Split screen to empty; a subsequent `Open` loads a fresh source. Two
implementations:

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

**Time-ordered marker list — `InsertMarkerSorted` (T-071).** The "Cut markers" list reads
chronologically regardless of add order (place a cut at 5:00 then one at 2:00 and 2:00 sits above).
Rather than appending, every add funnels through `InsertMarkerSorted`, which walks to the ascending
insertion index by the marker's sort key (`Snapped` time) and inserts there — **stable**, so equal-key
markers keep add order. A **pending** marker (T-041) sorts on its provisional requested time; when its
snap resolves and the key changes, `RepositionMarkerSorted` moves it (remove + sorted-insert) into its
correct slot, skipping the move when it is already correctly placed to avoid spurious collection churn.
This is a **marker-list display fix only** — the split output was already time-ordered (the plan and the
"Parts to export" segments sort by time).

**Instant (optimistic) markers — `IsSnapPending` (T-041).** A cut placed while the background keyframe
index is still running does **not** wait for the scan: `AddCutAt` sees `IsIndexingKeyframes` with no
keyframes yet and adds the marker **immediately** via `AddPendingMarker`, constructing the
`CutMarkerViewModel` with `snapPending: true` — an identity snap (`Snapped = Requested`, delta 0) whose
`Display` reads `"01:23.4 → snapping…"`. A continuation awaits the **same** in-flight index
(`EnsureKeyframesAsync`), then calls `ResolveSnap()` to recompute the real snap in place and clear
`IsSnapPending`, re-deduping on the **final** snapped time (a resolved duplicate is dropped). The
resolve is guarded against a stale file — a newer load / unload swaps the index CTS, so a late resolve
never touches a different file's markers. When keyframes are already present, the marker snaps
synchronously as before (unchanged contract).

**Selectable split parts — `SplitSegmentViewModel` (T-049).** `SplitViewModel` projects the current
markers' **snapped** times + the probed duration into an observable `Segments` collection of
`SplitSegmentViewModel` — the ordered contiguous ranges `[0..s1],[s1..s2],…,[sN..end]`, each with a
1-based `Index`, `Start`/`End`/`Duration`, a `Display` (`"Part 2 · 05:00–10:00 · 5:00"`), and an
observable `IsSelected` (default true). `RebuildSegments` re-projects whenever the markers change, a
marker resolves its snap, or the duration becomes known, **preserving each part's `IsSelected` by index**
across a rebuild. `RunSplitAsync` passes `null` for `SelectedSegmentIndices` when all parts are selected
(the fast muxer path) and the selected original indices otherwise (the per-segment path); `CanRunSplit`
also requires ≥1 selected part.

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

## Scrub-bar hover thumbnails (`Core/Thumbnails/`, `App/ViewModels/`)

Hovering the player's scrub bar shows a small frame preview at the hovered time (G-030). This is a
**second, independent ffmpeg path** — deliberately kept apart from the FFME preview so a fast hover
sweep never disturbs playback — split cleanly into a UI-free Core service and a WPF-free App view model.

### The frame source — `IThumbnailService` / `FfmpegThumbnailService` (`Core/Thumbnails/`)

`IThumbnailService.GetThumbnailAsync(inputPath, time, width, ct)` extracts one frame to a temp jpg and
returns its **path** (never an `ImageSource` — Core stays UI-free; the App layer loads the path into a
frozen `BitmapImage`). It is **best-effort throughout**: any failure (missing input, ffmpeg error,
cancellation, I/O) resolves to `null` — a preview that can't be produced simply shows nothing, never
throws. `FfmpegThumbnailService` is the production impl over the same `IFfmpegRunner` choke-point:

- **Fast keyframe-accurate seek** — `-ss <t>` placed **before** `-i`, then `-frames:v 1 -vf
  scale=<width>:-1 -y <temp.jpg>` (input-seek keeps it near-instant; keyframe accuracy is fine for a
  hover). Args are built via the same `FfmpegArgs` builder and exposed `internal` for token-order tests.
- **Bucketed LRU cache** — requests are keyed by `(inputPath, bucket)` where `bucket` is the hovered
  time floored to a configurable granularity (default 1s), so repeat hovers within a bucket reuse the
  file **without** re-running ffmpeg. The cache is LRU-bounded (default 128 entries); evicting an entry
  deletes its temp file. Temp files live under
  `%LOCALAPPDATA%/VideoSplitJoiner/thumb-cache/<hash-of-input>/<bucketMs>.jpg` (root injectable for tests).
- **Cancellable, cache-swept** — the token is honored end-to-end so a superseded request never clobbers
  a newer one; `Clear(inputPath)` sweeps one file's cache dir and `ClearAll()` the whole root (both
  best-effort, never throw). `NullThumbnailService` is the inert no-op default (every grab → `null`) so
  player constructions/tests that don't exercise thumbnails need no null-checks.

### The hover view model — `ThumbnailPreviewViewModel` (`App/ViewModels/`)

A **WPF-free** VM the view feeds hover samples (`UpdateHover(time, offsetX)` from the scrub slider's
`MouseMove`) and enter/leave toggles. It exposes only primitives the view binds to a `Popup`: the temp
jpg `HoverThumbnailPath`, the `HoverTimeText` (`mm:ss`) label, `HoverOffsetX` for placement, and
`IsThumbnailVisible`. Its crux is **debounce + coalesce (latest-wins)**: each hover cancels the prior
in-flight request (CTS swap), waits a short settle window (default 60ms) before touching ffmpeg, and
commits a resolved grab only when it is still the newest request (monotonic id check) and the cursor is
still over the bar — a superseded or post-leave grab is dropped. The result is marshalled back onto the
captured sync context via `Progress<T>`, exactly like `OperationViewModel`'s progress channel. `SetInput`
(new load) and `Clear` (unload) sweep the previous file's cache; `MouseLeave` hides without sweeping so
cached frames are reused on the next hover.

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
  `JoinEngine`, plus the `FfmpegThumbnailService` (hover-frame source) and the shared `AppSettings` — and
  shares the probe across both screens. A second, DI-style ctor lets tests inject already-composed screen
  view models with fakes. It also owns the active-tab `CurrentOperation` / `WindowTitle` binding that
  drives the taskbar + title progress (G-025, above).
- **`SplitViewModel`** / **`JoinViewModel`** are the two screens, each constructor-injected with Core
  interfaces (so they are fully unit-testable without FFmpeg).
- **`OperationViewModel`** is composed into both screens to give split/join a shared
  progress + cancel + friendly-error lifecycle. It is WPF-free (marshals via `Progress<T>`), so it
  runs off the UI thread under test. It maps engine failures (typed results *and* exceptions) into
  `UserFacingError`s. **Four mutually-exclusive lifecycle surfaces (`OperationState`, G-027).** The
  operation used to vanish silently on completion; now exactly one of four surfaces shows at a time,
  each computed from `State` (`Idle / Running / Completed / Failed / Cancelled`) and reset on the next
  run / load / Clear so no stale "done" ever lingers:
  - **Running** — gold bar + `StatusText` + `EtaText` + Cancel (`IsRunning`).
  - **Completed** — green ✓ + a **`ResultSummary`** line ("Split into 3 parts" / "Joined 4 clips →
    joined.mkv", supplied by the producing VM since it knows the real counts/output name) + **Open
    folder** (`IsCompleted`).
  - **Cancelled** — a muted note, deliberately *not* error-red (`IsCancelled`).
  - **Failed** — the red error block with Copy error / Open log (surfaced via the `Error` block, not a
    bool). `ResultSummary` is cleared at every `BeginRun` so a prior run's line never bleeds into a new one.

  It exposes these additional visible-progress signals:
  - **`IsIndeterminate` (T-042)** — true while running with no usable fraction yet, so the bar animates
    as a busy indicator instead of sitting frozen at 0% (ffmpeg's `time=` can be sparse); it flips to a
    determinate bar the instant a real fraction (>0) arrives. This cures the "-c copy split looks stuck"
    problem — a running operation is never silent.
  - **`StatusText` fed by an `IProgress<OperationStatus>` (T-044)** — the `RunWithResultAsync` overload
    that takes a stage channel formats each reported `OperationStatus` (a `Core/Ffmpeg` record carrying
    a human-readable `Stage` + optional `Detail`) into the one-line status label ("Splitting… (4
    parts)"), marshalled through the same captured sync context as the numeric progress.
  - **`EtaText` from `EtaEstimator` (T-045)** — on each progress sample the VM feeds real elapsed
    (a `Stopwatch`) vs the reported fraction to `EtaEstimator`, whose EMA-smoothed
    `remaining ≈ elapsed × (1 − fraction) / fraction` becomes a friendly "~1m 20s left"
    (or "estimating…" when too early). Both are per-run — primed at `BeginRun`, cleared at `EndRun`.
    `EtaEstimator` is WPF- and wall-clock-free (fed explicit `TimeSpan` elapsed), so it is fully
    unit-tested with synthetic sequences.
  - **`TaskbarProgressState` + the running window title (G-025).** A pure computed
    `System.Windows.Shell.TaskbarItemProgressState` maps the operation to the Windows taskbar button:
    Failed → `Error` (red), not-running → `None` (clears the fill), `IsIndeterminate` → `Indeterminate`
    (the "preparing" pulse), running-with-a-fraction → `Normal` (green). `MainWindow`'s `TaskbarItemInfo`
    binds it (plus `Progress` for the fill) to `MainViewModel.CurrentOperation` — the operation of the
    *active tab*, re-pointed on tab switch. Because the taskbar button can't render text, the ETA + %
    ride in the **OS window title** (`MainViewModel.WindowTitle`, a pure `ComposeWindowTitle` →
    `"Splitting 45% · ~1m 20s — Video Split / Join"`, visible on taskbar hover / alt-tab); the in-app
    caption stays on the fixed `CaptionTitle` so it never flickers with progress.

- **Per-part split progress — `IProgress<PartProgress>` + `PartMapping.PartAt` (G-025).** Beyond the
  overall bar, splitting into N parts advances each row in "Parts to export" **Pending → Writing (live
  %) → Done ✓**. A third, purely-additive `IProgress<PartProgress>` channel (a `RunWithResultAsync`
  overload) carries a `PartProgress(PartIndex, PartCount, PartFraction)` record — the part's **original**
  1-based index (a selected middle part stays *part 2 of 3*) and its local 0..1 fraction — without ever
  touching the overall `Progress`/`StatusText`. The per-segment subset path reports its part index
  naturally; the **fast single-pass segment-muxer path is preserved** — the current part is *derived*
  from ffmpeg's one monotonic `time=` via **`PartMapping.PartAt(time, boundaries, duration)`**, a pure,
  unit-tested, I/O-free mapping of an absolute file time onto "(which part, how far into it)" using the
  interior snapped-cut boundaries (half-open `[start,end)`, boundary belongs to the later part, clamps at
  both ends) — so no extra ffmpeg passes are needed. `SplitViewModel.ApplyPartProgress` marks every
  selected part before the active one Done, the active one Writing at its throttled fraction, and leaves
  later/unselected parts Pending; the active row shows a gold live-fill, completed rows a green ✓.
- The view models themselves are **WPF-free and constructor-injected** — the WPF dependency lives
  only in the `App` assembly's views and `App.xaml`, never in Core.
- **`MainViewModel` composes the real player** by passing a `new FfmeMediaPlayer()` into
  `SplitViewModel`. The player starts unattached; `PlayerView`'s code-behind calls `Attach(Media)` on
  load to bind it to the view's FFME `MediaElement` (the one place WPF and the player meet). A
  `SplitViewModel` built without a player falls back to `NullMediaPlayer`, keeping tests and non-UI
  constructions working.

## Window shell, theme, and two-column layout (`App/Views/`, `App/Themes/`)

The 0.2.0 UI adopts the design sample's identity — a custom dark title bar, a token-driven dark+gold
theme, bundled IBM Plex fonts, and a two-column split of each screen.

### Custom themed window frame — `WindowChrome` (G-018)

`MainWindow` replaces the default light Windows title bar with a **custom dark caption** via
`WindowChrome.WindowChrome` (`CaptionHeight=34`, `ResizeBorderThickness=6`, `UseAeroCaptionButtons=False`,
`WindowStyle=SingleBorderWindow`): the app title with a gold accent on the left, themed minimize /
maximize-restore / close buttons (close hovers red) on the right, over the theme surface. The window
still drags (WindowChrome, with a `DragMove` fallback), resizes on all edges, and **maximizes to the
monitor work area without covering the taskbar** — `MainWindow.xaml.cs` hooks `WM_GETMINMAXINFO` on
`SourceInitialized` and clamps the maximized bounds to `rcWork`, and a `WindowState`→margin/border-
thickness converter pair collapses the 1px frame line and adds the maximized content margin so nothing
spills off-screen.

### Dark + gold theme tokens (G-017)

The whole app is restyled to a **design-token system** merged in `App.xaml` from `App/Themes/`:
`Tokens.xaml` (the sample palette — a full surface scale `#0A0B0D`→`#232935`, near-black window `#0D0F13`,
charcoal panels `#15181E`, pure-black video area, the gold accent `#E0A83A`, text/border/semantic scales,
tight 6–12px corner radii, typography) and `Controls.xaml` (themed control templates — buttons, the gold
slider track/thumb, and the themed **scrollbars**). Views reference token **keys** only — no hardcoded
colors; token keys are preserved verbatim (values remapped), new keys additive. Text uses theme tokens
readable on dark; compat green/red and error affordances are preserved, dark-tuned.

- **Bundled fonts** — IBM Plex Mono / Sans (OFL-1.1) ship in `src/App/Fonts/` (Regular / Medium /
  SemiBold of each) and are referenced by the type tokens, so the UI renders in Plex regardless of
  installed system fonts.
- **Themed scrollbars (T-072)** — a thin dark-and-gold implicit `ScrollBar` style (track on a low
  surface tier, a rounded `BorderStrong` thumb that turns **gold on hover/drag**) replaces the default
  light Windows scrollbar chrome app-wide.

### Two-column screen layout (G-019)

Both screens split into a **left visual column** (the preview player + timeline/scrubber) and a **right
tool panel** (Load / Clear and everything below — file-info, cut markers, parts-to-export, output, Run),
separated by a **draggable `GridSplitter`**. The columns are a three-column `Grid` — `* (MinWidth 320)` /
`6` (the splitter) / `360 (MinWidth 300, MaxWidth 520)` — so the right panel defaults to 360px and drags
within 300–520. (Inside the left column a second `GridSplitter` keeps the earlier drag-resizable video
pane, G-006.) The sample structure — app header with the "lossless · no re-encode" tagline, a gold
format badge (`HEVC · MATROSKA`), the Split file-info card (`container · duration · size`), the
"Cut markers" / "Parts to export" headers, mono DIR / NAME output fields, and the Join "Estimated result"
panel — is a **relayout + restyle, not a rewire**: all existing bindings/commands are preserved, and the
pure formatting/estimate helpers were extracted to `Core/Media/MediaFormat.cs` (unit-tested).

## Startup + crash safety-net (`App/App.xaml.cs`)

`App.OnStartup` does two things before the shell loads: **(1)** `InitializeFfmpegForPreview` points FFME
at the shared ffmpeg build (`Library.FFmpegDirectory`, best-effort — see *Binary resolution* above), and
**(2)** `WireGlobalExceptionHandlers` installs a **global crash safety-net (T-079)**. Without it an
unhandled exception on the UI dispatcher, a background task, or a native path would silently kill the
process (no dialog, no log). All three managed sinks are wired:

- **`DispatcherUnhandledException`** (UI thread) — logs the crash, shows a **friendly, copyable** dialog
  naming the saved log path (reusing the `UserFacingError` / `ErrorActions` copy affordance), and marks
  it **`Handled = true`** so a recoverable UI error does **not** tear down the app.
- **`AppDomain.CurrentDomain.UnhandledException`** — last-ditch synchronous log (the process is going
  down regardless; managed handlers can't recover this).
- **`TaskScheduler.UnobservedTaskException`** (e.g. a faulted keyframe index / thumbnail grab) — logs it
  and calls `SetObserved()` so it never escalates to a process kill.

Every crash is recorded best-effort via `ErrorLogWriter.TryWriteCrash` to
`%LOCALAPPDATA%/VideoSplitJoiner/logs/`, and **every handler body is itself wrapped in try/catch** so a
throw inside a crash handler can never recurse.

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

## Key Decisions (ADRs)

The architectural choices above are recorded as immutable Architecture Decision
Records under [`docs/adr/`](adr/README.md) — see that index for the full list.
The most load-bearing ones, each shaping a core seam of the app:

- **[ADR 0001 — Stream-copy only](adr/0001-stream-copy-only.md):** the lossless
  `-c copy` cutting invariant — no re-encode, ever — which forces keyframe-snapped
  cut points, a join-compatibility contract, and a runtime-enforced codec denylist.
  This is the root decision the preview, keyframe-scan, and bundling choices below
  all build on.
- **[ADR 0002 — Error model](adr/0002-error-model.md):** a per-subsystem error
  contract (probe / split / join / diagnostics each define "failure" their own
  way) rather than one uniform all-exceptions or all-results strategy.
- **[ADR 0004 — FFME over MediaElement](adr/0004-ffme-over-mediaelement.md):**
  the preview decodes through native FFME/ffmpeg so it plays *exactly* what the
  engine can cut, instead of WPF `MediaElement`'s narrower Media Foundation codec set.
- **[ADR 0010 — Shared ffmpeg bundling](adr/0010-shared-ffmpeg-bundling.md):** a
  single shared (not static) ABI-pinned ffmpeg build serves both the CLI split/join
  engine and the P/Invoke FFME preview from one dual-consumer bundle.
- **[ADR 0011 — Single-file publish, no trim](adr/0011-single-file-publish-no-trim.md):**
  self-contained single-file win-x64 + ReadyToRun with `PublishTrimmed` banned, so
  the reflection-heavy WPF + FFME stack ships runnable on a machine with no .NET runtime.
- **[ADR 0015 — Bulk trim reuses Split's single-segment path](adr/0015-bulk-trim-reuses-split-single-segment.md):**
  the Bulk Cut tab expresses a batch intro/outro trim as a single-kept-segment
  `SplitRequest` run through the existing `SplitEngine` `-c copy` path — **no second
  ffmpeg code path** — so the copy invariant, keyframe-snap, and cancel-safety are
  inherited; Bulk adds only orchestration (list, two cut points per row, a Core
  `BulkTrimEngine` batch runner).

The stream-copy (`-c copy`) cutting invariant — now recorded as
[ADR 0001](adr/0001-stream-copy-only.md) above — underpins ADR 0004, 0009, and
0010, and is described in the engine sections earlier in this document.
