# Development Guide

How to build, run, test, and contribute to VideoSplitJoiner. See also [ARCHITECTURE.md](ARCHITECTURE.md) (structure),
[GLOSSARY.md](GLOSSARY.md) (terms), and [specs/_index.md](specs/_index.md) (the living-spec contracts).

## Prerequisites
- **.NET 8 SDK** (Windows; the app is WPF → `net8.0-windows`).
- **ffmpeg / ffprobe** — bundled app-locally from `ffmpeg-shared/` on build (no PATH dependency); see
  [adr/0010-shared-ffmpeg-bundling.md](adr/0010-shared-ffmpeg-bundling.md). The `CopyBundledFfmpeg` MSBuild target copies
  them into the build output's `ffmpeg/` folder. If `ffmpeg-shared/` is absent (fresh clone before fetch), the build
  **warns** but does not fail. Populate it once per machine with `powershell -File packaging/fetch-ffmpeg-shared.ps1`
  (the binaries are gitignored, never committed).
- This project is typically built with a **portable, off-PATH .NET SDK** (see
  [adr/0013-off-path-portable-dotnet.md](adr/0013-off-path-portable-dotnet.md)). When the SDK isn't on `PATH`, set
  `DOTNET_ROOT` to the SDK folder and invoke `dotnet` by its full path (e.g. `<sdk>/dotnet.exe build`).

## Build · run · test
```
dotnet build -c Release          # 0-warning build is the bar
dotnet run --project src/App     # launch the app
dotnet test                      # full suite (App + Core)
```
- **Zero warnings** is enforced by convention (Core builds with warnings-as-errors).
- The suite is **xUnit + FluentAssertions**. `CoreIsUiFreeTests` fails the build if `src/Core` ever references WPF —
  Core must stay UI-free.
- **ffmpeg-dependent integration tests** find the binaries via `FfmpegTestBinaries` (`tests/Core.Tests`), searching in
  order: `VSJ_FFMPEG_DIR`; then `ffmpeg-shared/` and `ffmpeg/` in each folder walking up from the test output; the test
  output folder itself; `PATH`; and last a legacy hard-coded dev-machine path. When nothing is found the test is
  reported **Skipped** (visibly, never as passed). Set `VSJ_REQUIRE_FFMPEG=1` to make a missing binary a **failure**
  instead — the release gate in `release.yml` sets both variables.
- A **Release** build + relaunch is how visual changes are verified (headless tests can't confirm rendering). A change
  that affects the running app is not done until `powershell -File packaging/package.ps1` has rebuilt what the user runs
  and `dist/publish/VideoSplitJoiner.App.exe` has been relaunched (see the root `CLAUDE.md`).

## Package · install · release
Fuller notes: [README § Build from source](../README.md#build-from-source) and
[adr/0014-no-ci-yet.md](adr/0014-no-ci-yet.md) (what the release workflow does and does not gate).
```
powershell -File packaging/fetch-ffmpeg-shared.ps1                    # once: ffmpeg SHARED build -> ffmpeg-shared/
powershell -File packaging/package.ps1 [-FfmpegSource <dir>] [-Dotnet <path>]
ISCC.exe /DMyAppVersion=<version> packaging\VideoSplitJoiner.iss        # needs Inno Setup 6
```
- **`package.ps1`** reads `<Version>` from `Directory.Build.props`, runs a single-file, self-contained win-x64
  `dotnet publish -c Release` into `dist/publish/`, copies the ffmpeg shared build (all `*.dll` + `ffmpeg.exe` +
  `ffprobe.exe`) into `dist/publish/ffmpeg/`, copies `THIRD-PARTY-NOTICES.md` and any `LICENSE` found beside or
  above the ffmpeg source (by default the repo's own) into `dist/publish/`, and zips it to
  `dist/VideoSplitJoiner-v<Version>-win-x64.zip`. It throws before publishing if the ffmpeg source is incomplete.
  `-FfmpegSource` defaults to `ffmpeg-shared/`; `-Dotnet` defaults to a machine-specific portable-SDK path
  (`D:\_env_storeage\dotnet\dotnet.exe`), so pass `-Dotnet dotnet` where the SDK is on `PATH` (as CI does).
- **The installer** (`VideoSplitJoiner.iss`) is a per-user install (no elevation) of everything in `dist/publish/`,
  so run `package.ps1` first. Output: `dist/VideoSplitJoiner-v<version>-setup.exe`.
- **Releasing:** bump `<Version>` in `Directory.Build.props` and add the matching `CHANGELOG.md` section, then push a
  `vX.Y.Z` tag. `.github/workflows/release.yml` (also runnable by `workflow_dispatch` with a `version` input) fails
  fast if the tag and `Directory.Build.props` disagree, fetches ffmpeg, runs the test gate, packages, installs Inno
  Setup and builds the installer, writes `dist/SHA256SUMS.txt`, and publishes the setup `.exe`, zip and checksums to
  the separate `MrParkerZ7/installer-video-spliter-joiner` repo — **only when the `RELEASE_PAT` secret is set**;
  without it the build runs and the publish is skipped with a warning. The `release.yml` header names a local
  counterpart, `release-local` (not a script in this repo), that builds and publishes from a dev machine with `gh`
  auth and needs no PAT at all. Which releases have
  actually gone out, and how, is tracked in the Release rows of [ROADMAP.md](ROADMAP.md).
- **The ffmpeg build is not pinned.** `fetch-ffmpeg-shared.ps1` downloads BtbN's moving `latest` n7.1 GPL shared
  build and checks only that `avcodec-*.dll` arrived — no checksum. So the 7.1 line is fixed but the exact build is
  not, nothing records which build went into a release, and `SHA256SUMS.txt` covers only the produced artifacts.

## Project layout
```
src/Core/        UI-free engine + services (Split · Bulk · Join · Media · Thumbnails · Waveform · Profiles · Errors · Ffmpeg · Io)
src/App/         WPF app — ViewModels (WPF-free) · Views · Media (FFME) · Settings · Themes · Io · Fonts (bundled IBM Plex)
                 + root helpers: VideoFileFilter · DropRefusal · DropDiagnostics · ImageSignature · AppVersion
tests/Core.Tests App-free Core unit tests
tests/App.Tests  App VM/view-model tests (WPF-free VMs, testable headlessly)
ffmpeg-shared/   the bundled ffmpeg/ffprobe binaries (copied into output on build)
docs/            architecture · ADRs · designs · living specs · standards · guides (see docs/README.md)
```

## Conventions (the bar for a change)
- **Hand-rolled MVVM, WPF-free VMs** — VMs use only `ObservableObject`/`RelayCommand` + Core/BCL types (no
  `PresentationFramework`). All WPF lives in Views/code-behind. Keep it testable headlessly.
  ([adr/0007-hand-rolled-mvvm.md](adr/0007-hand-rolled-mvvm.md))
- **Reuse before build** — survey the existing engine/VM surface first; a bulk trim reuses the Split engine, the preview
  player is shared, thumbnails go through one `IThumbnailService`. No second ffmpeg code path.
- **TDD + Case-Coverage Matrix** — tests precede/accompany the code; 100% line is the floor, the target is the Case-Coverage
  Matrix (Required-Success · Required-Fail · Optional · boundary). Never weaken an assertion to green a run.
- **`-c copy` / keyframe-snap invariants are sacred** — `SplitEngine` and `JoinEngine` are copy-only and must not
  re-encode or move a cut off a keyframe; `SatisfiesCopyInvariant` guards their launches. The only re-encode path is
  the opt-in `CutPrecision.Exact` (`SmartCutEngine`, [adr/0018-smart-cut-exact-trimming.md](adr/0018-smart-cut-exact-trimming.md)),
  which re-encodes just the fragment up to the next keyframe. Do not add another.
- **Living specs + `serves-spec:`** — behavior is documented as numbered invariants in `docs/specs/SPEC-NNN`; new tests carry
  a `serves-spec:` trait. `todo-automate` derives/checks the coverage. A behavior change updates its spec.
- **ADRs for decisions** — a non-obvious architectural/technology choice gets an ADR under `docs/adr/`.
- **Commits** — explicit pathspec (never `git add -A`); one logical change per commit.

## Where to look
- A feature's contract → its `docs/specs/SPEC-NNN` (invariants) + its `docs/design/D-NNN` (if it had a design).
- Why something is the way it is → `docs/adr/`.
- The whole-system shape → [ARCHITECTURE.md](ARCHITECTURE.md).
- The UI stops responding → run `powershell -ExecutionPolicy Bypass -File packaging/diagnose-frozen-ui.ps1` *while* it
  is stuck, before clicking anything. It is read-only (UI-thread responsiveness, mouse capture, the app's windows,
  foreground/active window) and saves its report to `%TEMP%\vsj-freeze-<timestamp>.log`.
