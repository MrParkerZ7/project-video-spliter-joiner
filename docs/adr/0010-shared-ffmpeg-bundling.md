# ADR 0010: Shared (not static) ffmpeg build, gitignored, dual-consumer, ABI-pinned 7.x

## Status

Accepted

## Context

Two independent subsystems need ffmpeg at runtime, and they need it in **different shapes**:

- **The split/join engine** (`src/Core/Ffmpeg`) shells out to the `ffmpeg.exe` / `ffprobe.exe`
  **executables** as child processes. `FfmpegBinaryLocator` resolves them by (a) explicit override,
  (b) an app-local `ffmpeg/` folder next to the assembly, then (c) `PATH`.
- **The FFME video preview** (`src/App/Media/FfmeMediaPlayer.cs`, package `FFME.Windows 7.0.361-beta.1`)
  does **not** run a child process. It **P/Invoke-loads the native ffmpeg shared libraries**
  (`avcodec-61.dll`, `avformat-61.dll`, `avutil-59.dll`, …) in-process. `App.OnStartup` →
  `InitializeFfmpegForPreview()` sets `Unosquare.FFME.Library.FFmpegDirectory` to a folder that must
  contain those DLLs, *before* any FFME control loads.

This forces two hard constraints:

1. **Shared, not static.** FFME binds transitively to `FFmpeg.AutoGen 7.0.0`, whose P/Invoke surface
   requires the versioned `av*-NN.dll` **shared** libraries. A statically-linked ffmpeg (one exe, no
   DLLs) would satisfy the engine but leave the preview with nothing to load. A shared build satisfies
   **both** consumers from one folder (`package.ps1` lays the DLLs + exes flat into
   `dist/publish/ffmpeg/`, which is exactly where both `Library.FFmpegDirectory` and
   `FfmpegBinaryLocator`'s app-local probe look).
2. **ABI pinned to ffmpeg 7.x.** `FFmpeg.AutoGen 7.0.0` marshals structs/entrypoints for the ffmpeg 7.x
   ABI. A wrong major (e.g. `avcodec-60`/`-62`) makes `Library.LoadFFmpeg()` throw at load. The
   `avcodec-61` / `avformat-61` / `avutil-59` major numbers are the 7.x ABI marker.

The native binaries are ~180 MB (avcodec alone is ~90 MB), GPL-licensed (BtbN `n7.1 gpl shared`), and
platform-specific — inappropriate to commit into a source repo.

## Decision

- **Bundle a shared (not static) ffmpeg 7.x build.** Fetched reproducibly by
  `packaging/fetch-ffmpeg-shared.ps1` / `.sh` from
  `BtbN/FFmpeg-Builds …/ffmpeg-n7.1-latest-win64-gpl-shared-7.1.zip`, laid **flat** into repo-root
  `ffmpeg-shared/` (all `av*-NN.dll` + `swscale`/`swresample`/`postproc` DLLs + `ffmpeg.exe` +
  `ffprobe.exe`).

- **`ffmpeg-shared/` is gitignored** (root `.gitignore`), never committed. Every dev/CI box that wants
  the preview or a packaged build runs the fetch script once.

- **One folder feeds both consumers.** The MSBuild target `CopyBundledFfmpeg` (`AfterTargets="Build"`
  in `VideoSplitJoiner.App.csproj`) copies `ffmpeg-shared/*.exe;*.dll` into `$(OutDir)ffmpeg/`, so a
  plain `dotnet build` yields a runnable app that splits/joins **without ffmpeg on PATH** and can load
  the preview DLLs. If `ffmpeg-shared/` is absent (fresh clone before fetch), the target emits a
  `<Warning>` and **skips** rather than failing the build — clean checkouts and CI stay green.

- **Pin ffmpeg 7.x and assert it.** The fetch scripts assert `avcodec-*.dll` exists after copy
  (*"avcodec-\*.dll missing after copy — the FFME preview will not load"*), `package.ps1` sanity-checks
  the same ABI marker before publishing, and `tests/App.Tests/FfmpegInitProbeTests.cs` runs a headless
  `Library.LoadFFmpeg()` probe that fails loudly on an ABI mismatch (and `SkippableFact`-skips when
  `ffmpeg-shared/` is absent).

## Consequences

**Positive**

- A single shared-build folder satisfies both the exe consumer and the DLL consumer — no duplicate
  ffmpeg copies, no divergent versions between engine and preview.
- The repo stays lean (no ~180 MB GPL binaries in history); the fetch is reproducible and pinned.
- ABI drift is caught at three layers (fetch assert → package sanity-check → init-probe test) instead
  of surfacing as an opaque runtime P/Invoke crash.
- Build degrades gracefully: no `ffmpeg-shared/` → warning + PATH fallback for the engine, "preview
  unavailable" banner for FFME — never a broken build.

**Negative / trade-offs**

- Requires a **shared** build specifically; a static single-exe ffmpeg is not an option while FFME is
  the preview engine.
- GPL by default. Shipping a permissive release means pointing the fetch/package `-FfmpegSource` at an
  **LGPL** shared build (see `package.ps1` `-FfmpegSource`; THIRD-PARTY-NOTICES.md).
- P/Invoke-loading in-process means a corrupt/mismatched DLL can fault the host process, not just a
  child — hence the pre-load init probe and the best-effort `try/catch` around `InitializeFfmpegForPreview`.

**Forced follow-ons**

- The one-time fetch step is a mandatory setup for every dev/CI box wanting preview or packaging;
  documented in `ffmpeg-shared/README.md` and both script headers.
- Upgrading FFME (hence `FFmpeg.AutoGen`) forces re-pinning the fetched ffmpeg major in
  `fetch-ffmpeg-shared.{ps1,sh}`, the ABI marker checks in `package.ps1`, and the probe test's expected
  major.
