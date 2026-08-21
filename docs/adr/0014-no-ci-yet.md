# ADR 0014: Deferred CI gate despite 517 tests + CoreIsUiFree guard

## Status

Accepted

## Context

The repository already carries the machinery a CI gate would enforce, but nothing
enforces it automatically yet:

- **A large, runnable test suite.** ~517 xUnit tests (FluentAssertions) live under
  `tests/Core.Tests/` and `tests/App.Tests/`, run via
  `dotnet test VideoSplitJoiner.sln` (README § quick-start). Integration tests that
  need real ffmpeg **self-skip when the binaries are absent** (`FfmpegTestBinaries`,
  README:134), so the suite is green even on a clean checkout without
  `ffmpeg-shared/`.
- **An architectural invariant with a test guard.**
  `CoreIsUiFreeTests.CoreAssembly_ShouldNotReferenceWpf`
  (`tests/Core.Tests/CoreIsUiFreeTests.cs`) reflects over
  `typeof(AppInfo).Assembly.GetReferencedAssemblies()` and asserts Core references
  none of `PresentationFramework` / `PresentationCore` / `WindowsBase` — the
  mechanical enforcement of the Core ⇄ App seam (Core is `net8.0`; App is
  `net8.0-windows`, `UseWPF=true`). `src/Core/VideoSplitJoiner.Core.csproj` also sets
  `TreatWarningsAsErrors=true`, so a warning already fails the build locally.
- **No automation exists.** There is **no `.github/workflows/`** (nor any
  `azure-pipelines` / `.gitlab-ci` / other pipeline config) anywhere in the tree. The
  quality bar — build clean, 517 tests green, Core stays UI-free — is real but rests
  entirely on the developer choosing to run `dotnet build` / `dotnet test` before
  pushing. Nothing blocks a push or PR that skips them.

The project is a solo/small-scope Windows desktop app with a heavy, gitignored,
platform-specific dependency (the ffmpeg shared build — ADR 0010) that a hosted runner
would have to fetch (`packaging/fetch-ffmpeg-shared.ps1`) before the FFmpeg-dependent
integration tests could exercise anything beyond their self-skip path. Standing up that
runner is real work that has not yet been prioritized against feature delivery.

## Decision

**Defer the CI gate. Ship no pipeline for now, and record the gap explicitly rather
than let it pass unnoticed.** The quality bar stays developer-enforced: run
`dotnet build -c Release` (warnings-as-errors) and `dotnet test VideoSplitJoiner.sln`
locally before pushing; keep `CoreIsUiFreeTests` green.

When CI is prioritized, the intended shape is a **GitHub Actions** workflow on
`push` / `pull_request` (Windows runner) that:

1. Restores + builds the solution `-c Release` (warnings-as-errors already fails here).
2. Runs `dotnet test VideoSplitJoiner.sln`, which includes the
   `CoreAssembly_ShouldNotReferenceWpf` guard as an ordinary test — so the UI-free
   invariant becomes a merge blocker, not a convention.
3. Fetches the ffmpeg shared build (`packaging/fetch-ffmpeg-shared.ps1`) so the
   integration tests run for real instead of self-skipping — a follow-on, since the
   unit-level suite already gates most regressions without it.

Packaging (`packaging/package.ps1`, single-file publish — ADR 0011) is **out of scope**
for the first gate; a release-artifact job can follow once the test gate exists.

## Consequences

**Positive**

- **Zero infra to maintain today.** No runner, no secrets, no ffmpeg-fetch cost in
  hosted CI while the app is still in fast local iteration.
- **The bar is not fictional.** The suite + `TreatWarningsAsErrors` + the UI-free guard
  already catch the regressions that matter; running them is one command.
- **The future is spec'd, not guessed.** This ADR names the exact workflow to add, so
  turning CI on is a scoped task, not a design exercise.

**Negative**

- **Unenforced quality bar — the recorded risk.** Nothing stops a push or merged PR
  that never ran the tests. A broken build, a red test, or a Core→WPF leak
  (`CoreIsUiFreeTests` regressing) can land on the default branch and only surface when
  the next developer builds locally. The guard is only as good as the human who
  remembers to run it.
- **No cross-machine signal.** Green-on-my-box is the only signal; environment-specific
  breakage (the pinned dotnet at `D:\_env_storeage\dotnet`, ffmpeg fetch) is invisible
  until someone else clones.

**Forced follow-ons**

- Add the GitHub Actions workflow above (Windows runner: build `-c Release` →
  `dotnet test`) — the primary debt this ADR tracks. Supersede or amend this ADR when
  it lands.
- Keep `dotnet test VideoSplitJoiner.sln` as the single CI entry point so the
  `CoreIsUiFreeTests` guard and the stream-copy split invariants (ADR 0009's
  keyframe-snap `-c copy` cut; `SplitArgsBuilder`) are enforced the moment CI exists,
  with no extra wiring.
- A release-artifact job (single-file publish — ADR 0011; `packaging/package.ps1`) is a
  later, separate step once the test gate is green.
