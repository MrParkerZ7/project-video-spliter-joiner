# ADR 0014: Deferred CI gate despite 517 tests + CoreIsUiFree guard

## Status

Accepted — partly superseded 2026-08-28 (a tag-driven release workflow landed; the
push/PR test gate did not — see § Update)

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

## Update — 2026-08-28: partly superseded by a tag-driven release workflow

Commit `de83841` ("release: v1.0.0 packaging") added
**`.github/workflows/release.yml`** — the first automation in the tree. It falsifies
one Context bullet above (*"**No automation exists.** There is **no
`.github/workflows/`**"*) without overturning the Decision: what landed is
**tag-driven and release-shaped**, so the `push` / `pull_request` test gate this ADR
defers still does not exist, and every Negative recorded above stands unchanged for
ordinary commits.

**What actually landed** (verified against `.github/workflows/release.yml`)

- **It triggers on `push: tags: ['v*.*.*']` and `workflow_dispatch`** (manual, with a
  `version` input) — never on a branch push or a pull request. Nothing runs between
  tags: a red commit still reaches the default branch unblocked, and is caught only
  when someone cuts a release.
- **The runner and the entry point are exactly the shape sketched above.**
  `runs-on: windows-latest` (`timeout-minutes: 45`,
  `concurrency: release-${{ github.ref }}`, `permissions: contents: read`),
  `actions/setup-dotnet@v4` on `8.0.x`, then a step literally named *"Test (release
  gate)"* running `dotnet test VideoSplitJoiner.sln -c Debug --nologo` — the single
  entry point item 2 above asked for, so `CoreAssembly_ShouldNotReferenceWpf` and the
  `SplitArgsBuilder` copy-invariant tests are machine-enforced the moment a tag is
  pushed. **Two deltas:** the gate builds `-c Debug`, not `-c Release` (the Release
  build happens later, inside `packaging/package.ps1` → `dotnet publish … -c Release`);
  warnings-as-errors still bites regardless, because
  `src/Core/VideoSplitJoiner.Core.csproj` sets `TreatWarningsAsErrors` unconditionally
  rather than per-configuration.
- **Point 3 — real integration tests — is NOT met, and reordering alone will not meet
  it.** `packaging/fetch-ffmpeg-shared.ps1` does run in CI, but *after* the test step
  and to feed packaging, not the suite. Hoisting it would still change nothing:
  `FfmpegTestBinaries` (`tests/Core.Tests/FfmpegTestBinaries.cs`) guards on **hard-coded
  absolute paths** —
  `D:\_env_storeage\ffmpeg-7.1.1-essentials_build\bin\{ffmpeg,ffprobe}.exe` — with no
  override, so the FFmpeg-dependent tests self-skip on a hosted runner no matter what
  `ffmpeg-shared/` holds. Un-skipping them is a test-side change, not a workflow-side
  one.
- **Packaging — declared out of scope for the first gate — is what shipped first.**
  After the test step the workflow runs `packaging/package.ps1 -Dotnet 'dotnet'`
  (single-file publish — ADR 0011), `choco install innosetup` + `ISCC.exe` for the Inno
  installer, and a `SHA256SUMS.txt` step. That `-Dotnet 'dotnet'` argument is ADR 0013's
  forced follow-on being honoured: CI overrides the machine-specific portable-SDK
  default instead of editing the script.
- **Publishing is cross-repo, and currently a no-op.** The last step is
  `softprops/action-gh-release@v2` targeting `MrParkerZ7/installer-video-spliter-joiner`
  (binaries live in neither git tree), gated `if: env.RELEASE_PAT != ''`. Until the
  one-time `RELEASE_PAT` secret is set on this repo, the build runs and the publish is
  **skipped with a `::warning::`, never a failure** — a tag build stays green while
  shipping nothing. v1.0.0 itself was cut from a dev machine via `release-local`, so
  this workflow's publish path has not yet produced a release.

**What this changes about the decision.** Only the "no automation at all" premise. The
bar is still developer-enforced between tags, and it has grown: the suite is now **987
tests** (`c89be0b`) against the ~517 in this record's title, while the enforcement point
has not moved. The one Negative partly relieved is *No cross-machine signal* — a tag
build now proves the tree restores, builds and tests green on a clean `windows-latest`
box. Late and rare, but no longer never.

**Revised follow-ons** (these supersede the first bullet of *Forced follow-ons* above)

- **Still the primary debt:** a `push` / `pull_request` workflow (Windows runner →
  `dotnet test VideoSplitJoiner.sln`). `release.yml` is a ready-made template — same
  runner, same `setup-dotnet`, same one-command gate; only the trigger differs.
- **Set the one-time `RELEASE_PAT` secret** (the exact `gh secret set` line is in the
  workflow's header comment), or every tag build keeps producing artifacts it never
  publishes.
- **If the integration tests are to run for real in CI**, give `FfmpegTestBinaries` an
  environment / `ffmpeg-shared/` fallback *first*; only then does moving the fetch step
  above the test step buy anything.
- **Supersede this record fully with a new ADR** — per `README.md`'s "a later decision
  supersedes an earlier one" — when the push/PR gate lands, and update its Status column
  in the index.
