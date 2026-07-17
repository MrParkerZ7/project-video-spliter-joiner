# CLAUDE.md — VideoSplitJoiner

A .NET 8 WPF app that splits and joins video **without re-encoding**. See [README](README.md),
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), and [docs/USER_GUIDE.md](docs/USER_GUIDE.md).

## Layout

- `src/Core/` — `VideoSplitJoiner.Core`, UI-free media logic (Ffmpeg, Media, Split, Join, Errors).
- `src/App/` — `VideoSplitJoiner.App`, WPF UI + hand-rolled MVVM view models.
- `tests/Core.Tests/`, `tests/App.Tests/` — xUnit + FluentAssertions.
- `packaging/package.ps1` — single-file self-contained win-x64 publish + bundled ffmpeg + zip.
- `docs/todo/` — the task board. **Do not touch it** during code/doc work.

## Conventions (keep these true)

- **Core stays UI-free.** No WPF references (`PresentationFramework`/`PresentationCore`/`WindowsBase`)
  in `VideoSplitJoiner.Core`. The `CoreIsUiFreeTests` guard enforces this — keep it green.
- **All FFmpeg/ffprobe execution goes through `FfmpegRunner` / `FfprobeRunner`.** Never spawn
  `ffmpeg`/`ffprobe` directly and never build commands by string concatenation — use the typed
  `FfmpegArgs` (`ArgumentList`-based) builder.
- **The `-c copy` no-re-encode invariant is sacred (split + join).** Split and join must never emit
  an encoder flag. The args-builders forbid encoder tokens and require a bare `copy`; the invariant
  is re-asserted at runtime before launch and by unit tests on the token list. Do not add a
  re-encode path in v1.
- **No auto-detect.** The black/white/scene auto-detect feature was removed (no `Core/Detect`,
  no `SplitPointDetector`, no candidate UI). Do not reintroduce a detect layer or candidate ticks;
  cuts are placed manually (typed marker, "set cut at playhead", or timeline click).
- **One bundled ffmpeg SHARED build serves preview + engine.** A single app-local `ffmpeg/` folder
  holds BOTH the shared `*.dll` (avcodec-*/avformat-*/… — P/Invoke-loaded by FFME for the preview)
  AND `ffmpeg.exe` / `ffprobe.exe` (shelled out to by the split/join engine). Don't split these into
  two bundles. Fetch it with `packaging/fetch-ffmpeg-shared.ps1`; `package.ps1` bundles the whole
  shared build. Binaries are not committed.
- **`Library.FFmpegDirectory` is set at startup.** `App.OnStartup` points
  `Unosquare.FFME.Library.FFmpegDirectory` at the ffmpeg shared DLLs **before any FFME control
  loads**, best-effort (no shared build → preview unavailable, never a crash). Keep that ordering.
- **The preview is downscaled + hardware-accelerated — never the cut.** The FFME preview enables
  HW decode and installs a `scale=W:H` downscale filter (`PreviewScale`, capped ~1080p) for smooth
  4K playback. This is preview-only: the split stays `-c copy` and is never decoded, so the cut is
  always full source resolution. Keep 4K/decode changes on the preview side of the seam.
- **Keyframe-snap is intentional.** Cuts snap to the nearest keyframe (ties → earlier, clamps at
  ends). This is a design guarantee, not a bug — surface deltas/warnings, don't try to make cuts
  frame-exact.
- **Keyframe indexing is background + non-blocking.** `SplitViewModel.LoadAsync` gates only on the
  fast metadata probe and **opens the preview before the keyframe scan runs**; the scan indexes in a
  cancellable background task (`IsIndexingKeyframes` / `KeyframesReady`), a new load cancels the prior
  index (stale-guard), and a cut placed mid-index awaits the same in-flight scan (snap-before-ready).
  Don't re-block the load on the full scan or add a second keyframe scan pass.
- **The keyframe scan is demux packet-flag, with a frame-scan fallback.** `MediaProbe.GetKeyframesAsync`
  reads keyframe packets at the demux layer (`-show_packets`, keeping `K`-flag packets) — no frame
  decode — and falls back to the decode-based `-skip_frame nokey` frame scan only when the packet
  query is empty or throws. Keep both paths producing the same sorted-distinct output; don't drop the
  fallback.
- **Compatibility is refused, not fixed.** An incompatible join reports named mismatches and writes
  no output. Do not silently re-encode to reconcile mismatched clips.
- **The preview player is behind `IMediaPlayer` (FFME impl).** The Split-screen preview transport
  goes through the `IMediaPlayer` abstraction (`App/Media/`). View models (`PlayerViewModel`,
  `TimelineViewModel`) stay **WPF-free** and are tested against a fake player; the only WPF-bound
  impl, `FfmeMediaPlayer` (over an FFME `Unosquare.FFME.MediaElement`, which decodes through
  ffmpeg), is thin plumbing that just has to compile — its **live playback is verified only via
  `app-run`**, never in the unit suite. `NullMediaPlayer` is the no-op default so non-UI
  constructions/tests keep working. Don't leak WPF types into the player/timeline VMs; keep new
  transport (skip/frame-step/jump/volume/mute/speed) behind `IMediaPlayer` too.
- **All cuts funnel through `AddCutAt` — one snap path.** Every way of placing a cut (manual add,
  "set cut at playhead", clicking the timeline strip) routes through `SplitViewModel.AddCutAt`, which
  keyframe-snaps and dedupes. Do not add a second snap/dedupe implementation for a new cut-entry
  surface — wire it through `AddCutAt`.
- **Drag/drop plumbing is code-behind that routes to existing VM commands.** Drop and drag handlers
  live in the view code-behind (`SplitView`/`JoinView`) and add **no** load/add/reorder logic — a
  file drop routes to `LoadCommand` (Split, first file) / `AddFilesCommand` (Join, all files); a
  clip-row drag routes to `JoinViewModel.Move` → the same `MoveAsync` the Up/Down buttons use (one
  reorder path). The **accept-filter is a pure, tested helper** (`VideoFileFilter.AcceptVideoFiles` /
  `HasAnyVideo`) — keep it WPF-free and unit-tested; don't inline extension checks in code-behind.
  **Internal vs external drags are distinguished by payload type** (`typeof(JoinItemViewModel)` =
  reorder, `DataFormats.FileDrop` = external add), never by guessing — preserve that when touching the
  Join drop handler. Do not add new drop-side business logic; wire it through the existing commands.

## Build / test / package

The .NET 8 SDK lives at `D:\_env_storeage\dotnet` and is **not on PATH** — invoke by full path:

```powershell
D:\_env_storeage\dotnet\dotnet.exe build VideoSplitJoiner.sln -c Release
D:\_env_storeage\dotnet\dotnet.exe test  VideoSplitJoiner.sln
powershell -File packaging/package.ps1        # produces dist/VideoSplitJoiner-v<Version>-win-x64.zip
```

- **Tests use synthetic, FFmpeg-generated fixtures** and **guard-skip when the binary is absent**
  (see `FfmpegTestBinaries.SkipIfMissing`), so a machine without FFmpeg still runs green. FFmpeg
  binaries are **not committed** — the test path references them as an override; packaging copies
  them in from `-FfmpegSource`.
- `Directory.Build.props` holds `<Version>` (currently `0.1.0`; `1.0.0` is the target).

## Git

- **Commit with explicit pathspecs** (`git commit -m … -- <paths>`), not `git add -A` — the board
  (`docs/todo/`) and build output must not be swept into unrelated commits.
- Do not edit `docs/todo/` as part of code/doc changes.
