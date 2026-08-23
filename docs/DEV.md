# Development Guide

How to build, run, test, and contribute to VideoSplitJoiner. See also [ARCHITECTURE.md](ARCHITECTURE.md) (structure),
[GLOSSARY.md](GLOSSARY.md) (terms), and [specs/_index.md](specs/_index.md) (the living-spec contracts).

## Prerequisites
- **.NET 8 SDK** (Windows; the app is WPF → `net8.0-windows`).
- **ffmpeg / ffprobe** — bundled app-locally from `ffmpeg-shared/` on build (no PATH dependency); see
  [adr/0010-shared-ffmpeg-bundling.md](adr/0010-shared-ffmpeg-bundling.md). The `CopyBundledFfmpeg` MSBuild target copies
  them into the build output's `ffmpeg/` folder. If `ffmpeg-shared/` is absent (fresh clone before fetch), the build
  **warns** but does not fail.
- This project is typically built with a **portable, off-PATH .NET SDK** (see
  [adr/0013-off-path-portable-dotnet.md](adr/0013-off-path-portable-dotnet.md)). When the SDK isn't on `PATH`, set
  `DOTNET_ROOT` to the SDK folder and invoke `dotnet` by its full path (e.g. `<sdk>/dotnet.exe build`).

## Build · run · test
```
dotnet build -c Debug            # 0-warning build is the bar
dotnet run --project src/App     # launch the app
dotnet test                      # full suite (~880 tests: App + Core)
```
- **Zero warnings** is enforced by convention (Core builds with warnings-as-errors).
- The suite is **xUnit + FluentAssertions**. `CoreIsUiFreeTests` fails the build if `src/Core` ever references WPF —
  Core must stay UI-free.
- A **Release** build + relaunch is how visual changes are verified (headless tests can't confirm rendering).

## Project layout
```
src/Core/        UI-free engine + services (Split · Bulk · Join · Media · Thumbnails · Waveform · Profiles · Errors · Ffmpeg)
src/App/         WPF app — ViewModels (WPF-free) · Views · Media (FFME) · Settings · Themes
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
- **`-c copy` / keyframe-snap invariants are sacred** — a change must not re-encode or move the cut off a keyframe;
  `SatisfiesCopyInvariant` guards every ffmpeg launch.
- **Living specs + `serves-spec:`** — behavior is documented as numbered invariants in `docs/specs/SPEC-NNN`; new tests carry
  a `serves-spec:` trait. `todo-automate` derives/checks the coverage. A behavior change updates its spec.
- **ADRs for decisions** — a non-obvious architectural/technology choice gets an ADR under `docs/adr/`.
- **Commits** — explicit pathspec (never `git add -A`); one logical change per commit.

## Where to look
- A feature's contract → its `docs/specs/SPEC-NNN` (invariants) + its `docs/design/D-NNN` (if it had a design).
- Why something is the way it is → `docs/adr/`.
- The whole-system shape → [ARCHITECTURE.md](ARCHITECTURE.md).
