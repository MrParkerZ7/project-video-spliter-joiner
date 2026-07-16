# CLAUDE.md — VideoSplitJoiner

A .NET 8 WPF app that splits and joins video **without re-encoding**. See [README](README.md),
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), and [docs/USER_GUIDE.md](docs/USER_GUIDE.md).

## Layout

- `src/Core/` — `VideoSplitJoiner.Core`, UI-free media logic (Ffmpeg, Media, Split, Join, Detect, Errors).
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
- **Detection is decode-only.** Every detect pass targets the null muxer (`-f null -`), parses stderr,
  writes no file, re-encodes nothing. The decode-only invariant is asserted before each run and by tests.
- **Keyframe-snap is intentional.** Cuts snap to the nearest keyframe (ties → earlier, clamps at
  ends). This is a design guarantee, not a bug — surface deltas/warnings, don't try to make cuts
  frame-exact.
- **Compatibility is refused, not fixed.** An incompatible join reports named mismatches and writes
  no output. Do not silently re-encode to reconcile mismatched clips.
- **The preview player is behind `IMediaPlayer`.** The Split-screen preview transport goes through
  the `IMediaPlayer` abstraction (`App/Media/`). View models (`PlayerViewModel`, `TimelineViewModel`)
  stay **WPF-free** and are tested against a fake player; the only WPF-bound impl, `MediaElementPlayer`
  (over a WPF `MediaElement`), is thin plumbing that just has to compile — its **live playback is
  verified only via `app-run`**, never in the unit suite. `NullMediaPlayer` is the no-op default so
  non-UI constructions/tests keep working. Don't leak WPF types into the player/timeline VMs.
- **All cuts funnel through `AddCutAt` — one snap path.** Every way of placing a cut (manual add,
  "set cut at playhead", clicking the timeline strip, accepting a candidate) routes through
  `SplitViewModel.AddCutAt`, which keyframe-snaps and dedupes. Do not add a second snap/dedupe
  implementation for a new cut-entry surface — wire it through `AddCutAt`.
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
