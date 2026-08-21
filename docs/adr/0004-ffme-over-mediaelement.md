# ADR 0004: FFME (native ffmpeg) preview player over WPF MediaElement

## Status

Accepted.

## Context

The in-app video preview must play **exactly what the engine can cut**. Every
split/join is a lossless stream copy (`ffmpeg -c copy`, see `docs/ARCHITECTURE.md`),
so the app happily cuts HEVC, MKV, 4K, and many exotic container/codec combinations.
The original preview used WPF's built-in `MediaElement`, backed by Windows Media
Foundation. Media Foundation decodes a **narrower** set of codecs/containers than the
bundled ffmpeg, so a file that cut perfectly could still fail to *preview* — the
preview surface and the engine disagreed about what was playable.

The preview player already sits behind a testable seam — the `IMediaPlayer` interface
(`src/App/Media/IMediaPlayer.cs`) — with the view-model logic (scrub-seam suppression,
ready-gating, jog/step/speed/volume, tick projection) covered by a fake and the thin
WPF surface isolated in a single implementation class. That seam is what makes the
decoder swappable without touching any view-model or timeline code.

Forces at play:

- **Codec coverage** — preview parity with the cut engine is the primary requirement.
- **No re-encode contract** — the cut must stay `-c copy`; any decoder introduced for
  the preview must be *preview-only* and never touch the output file.
- **Native dependency management** — an ffmpeg-based decoder means shipping native
  libraries and loading them by P/Invoke at a precise point in the app lifecycle.
- **Testability** — WPF media controls cannot run headlessly; the seam must keep the
  untestable surface confined to one class.

## Decision

Swap the preview player from WPF `MediaElement` to **FFME**
(`Unosquare.FFME.MediaElement`, NuGet package `FFME.Windows` `7.0.361-beta.1`, which
binds to the FFmpeg.AutoGen 7.0.0 / ffmpeg 7.x ABI — `src/App/VideoSplitJoiner.App.csproj`).
FFME decodes through the same **native ffmpeg shared build** the engine shells out to,
so the preview plays whatever the app can cut.

Concretely:

- **`FfmeMediaPlayer`** (`src/App/Media/FfmeMediaPlayer.cs`) is the production
  `IMediaPlayer`. It wraps the view's `Unosquare.FFME.MediaElement`
  (`LoadedBehavior=Manual`, `ScrubbingEnabled=true`, so it fully drives transport and
  shows the first frame on Open without auto-playing). FFME's transport methods are
  async, so each is adapted fire-and-forget via `Run(...)` with faults routed to the
  `Failed` event.
- **Native shared build via P/Invoke.** FFME does not ship the native libraries. A
  matching ffmpeg 7.x **shared** win64 build (`avcodec-61` / `avformat-61` /
  `avutil-59` / …) is fetched into repo-local `ffmpeg-shared/` by
  `packaging/fetch-ffmpeg-shared.ps1` (gitignored), and the `CopyBundledFfmpeg`
  MSBuild target in the App `.csproj` copies it into an app-local `ffmpeg/` folder in
  the build output.
- **App-startup `FFmpegDirectory` init.** `App.OnStartup` calls
  `InitializeFfmpegForPreview()` (`src/App/App.xaml.cs`) **before any FFME control
  loads**, setting `Unosquare.FFME.Library.FFmpegDirectory` to the resolved folder.
  Resolution probes, in order: the packaged app-local `ffmpeg/`, a repo-local
  `ffmpeg-shared/` found by walking up from `BaseDirectory` (dev tree), then an
  absolute dev fallback — selecting the first that actually contains an
  `avcodec-*.dll`. The step is **best-effort**: if no shared build is found, the
  preview is simply unavailable and startup never crashes.
- **The WPF `MediaElementPlayer` is retired** — no source implementation remains; the
  only references left are historical (docs/todo, ARCHITECTURE).

## Consequences

**Positive**

- **Preview/cut codec parity.** The preview decodes through the same ffmpeg the engine
  cuts with, so HEVC / MKV / 4K files that cut fine now also preview fine; the "preview
  unavailable" path is now rare rather than routine.
- **No `DispatcherTimer`.** FFME raises `PositionChanged` natively, so the position
  tick is event-driven — the polling timer the retired `MediaElementPlayer` needed is
  gone.
- **The cut is untouched.** FFME's decode is preview-only; the engine stays `-c copy`.
  4K smoothness is handled inside this same preview path (HW-decode + downscale on the
  `MediaOpening` hook, `FfmeMediaPlayer.OnMediaOpening` / `PreviewScale`), never on the
  output — a distinct concern worth its own ADR.
- **Seam preserved.** The swap happened entirely behind `IMediaPlayer` — no view-model
  or timeline change was needed.

**Negative**

- **Native dependency.** The preview now depends on a native ffmpeg shared build that
  must be present and ABI-matched (ffmpeg 7.x / `avcodec-61`). A missing or mismatched
  build silently disables the preview.
- **Pre-fetch step.** Every dev / CI box must run `fetch-ffmpeg-shared.ps1` once; a
  fresh clone before the fetch produces a build-time warning and no bundled `ffmpeg/`.
- **Beta dependency.** `FFME.Windows 7.0.361-beta.1` is a beta release pinned to a
  specific ffmpeg ABI — upgrading ffmpeg means re-matching FFME and the fetched build.
- **`FfmeMediaPlayer` is not unit-tested.** As thin, non-headless WPF plumbing it is
  compile-only and verified live via `app-run`; only the seam-covered view-model logic
  is unit-tested.

**Forced follow-ons**

- Keep the fetch/copy pipeline (`fetch-ffmpeg-shared.ps1` + `CopyBundledFfmpeg`
  target) and the `FFmpegDirectory` ABI expectation in lock-step with the pinned
  `FFME.Windows` version.
- Single-file publish must self-extract the native libs
  (`IncludeNativeLibrariesForSelfExtract`, already set for the `PublishSingleFile`
  configuration); trimming stays disabled (unsafe for WPF).
- A still-unplayable source is handled gracefully through the `Failed` → banner path
  rather than a crash.
