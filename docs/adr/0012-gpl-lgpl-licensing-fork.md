# ADR 0012: GPL-by-default ffmpeg, LGPL escape via `-FfmpegSource`

## Status

Accepted

## Context

VideoSplitJoiner ships no ffmpeg binaries of its own — they are fetched and bundled at
package time (ADR 0010). The build that gets bundled carries a **license**, and that
license flows through to the *combined, distributed product*. Two facts make this a
distribution decision rather than a code decision:

- **The default bundle is GPL.** `packaging/fetch-ffmpeg-shared.ps1` (`$Url`, line 24)
  pulls BtbN's `ffmpeg-n7.1-latest-win64-**gpl**-shared-7.1.zip` into repo-root
  `ffmpeg-shared/`. That shared build enables GPL-only components, so bundling it makes
  the distributed product a **GPL** work — spelled out in `THIRD-PARTY-NOTICES.md`
  § Licensing (*"the combined, distributed product subject to the GPL"*).
- **The app couples to ffmpeg loosely.** The split/join engine only ever shells out to
  `ffmpeg.exe` / `ffprobe.exe` as child processes with pure stream-copy args
  (`SplitArgsBuilder`/`JoinArgsBuilder` `.Raw("-c", "copy")`; the split INVARIANT), and
  the preview P/Invoke-loads the shared `av*` DLLs via `Unosquare.FFME.Library.FFmpegDirectory`
  (`src/App/App.xaml.cs:144`) — never a static link. Dynamic loading + external-process
  invocation is exactly the coupling shape that keeps an **LGPL** ffmpeg permissive.

Critically, **the licensing choice is invisible from application source.** `src/Core` and
`src/App` resolve ffmpeg purely by *path*, license-blind: the engine's
`FfmpegBinaryLocator` probes an app-local `ffmpeg/` folder then `PATH`, and the preview
points `Library.FFmpegDirectory` at whatever folder was bundled. Nothing in the code knows
or cares whether the DLLs behind those paths are GPL or LGPL. The *only* place the decision
is expressed is the packaging layer — specifically `package.ps1`'s `-FfmpegSource`
parameter. `THIRD-PARTY-NOTICES.md` § Licensing therefore closes with an explicit
**"Decision required before public release"** that this ADR resolves.

## Decision

**Default to the GPL build; provide an LGPL escape hatch entirely at the packaging layer,
via `package.ps1 -FfmpegSource`.**

- **GPL by default.** With no override, `package.ps1` bundles the repo-local `ffmpeg-shared/`
  (the BtbN `win64-gpl-shared` build). This is correct for personal / internal / developer
  distributables — the current `<Version>0.1.0` target — and requires GPL compliance if that
  build is ever redistributed publicly.
- **LGPL escape via one parameter, no code change.** To ship a permissive release, fetch a
  BtbN `win64-**lgpl**-shared` build (pass its URL to `fetch-ffmpeg-shared.ps1 -Url`) and
  point `package.ps1 -FfmpegSource` at that folder (documented in the script's `.PARAMETER
  FfmpegSource` and `.EXAMPLE` blocks). The loose coupling above makes this LGPL-safe. No
  edit to `src/Core` or `src/App` is required — the fork is a build input, not a code path.
- **Notices track the bundle.** `package.ps1` copies `THIRD-PARTY-NOTICES.md` and the ffmpeg
  `LICENSE` into the distributable; whoever switches to an LGPL source updates the § Licensing
  section to match the build actually shipped.

## Consequences

**Positive**

- The default "just works" for the everyday developer/personal build without anyone having
  to source an LGPL build first.
- The permissive path exists and is one CLI parameter away — no code fork, no divergent
  branch, no re-architecting the ffmpeg coupling.
- Keeping the decision at the packaging boundary means `src/Core` / `src/App` stay clean and
  license-agnostic; the coupling that makes LGPL viable (external process + P/Invoke, never
  static link) is already an architectural invariant, not a special case for this ADR.

**Negative / trade-offs**

- The distributed default is GPL, which is easy to redistribute *without realizing the
  obligations attach* — the license is invisible from the app UI and code. The mitigation is
  documentation-only (`THIRD-PARTY-NOTICES.md` § Licensing + this ADR); nothing mechanical
  blocks a GPL public release.
- The GPL-vs-LGPL correctness of any given release now depends on a **packaging-time human
  step** (which `-FfmpegSource` was used), which no build gate enforces — the same source tree
  can produce either a GPL or an LGPL distributable.

**Forced follow-ons**

- **Before any public release**, the maintainer must consciously choose: ship the GPL build
  and comply with the GPL, **or** re-run the fetch/package with an LGPL `-FfmpegSource`. This
  ADR records the *mechanism* and the default; it does not pre-decide a public license.
- Whenever the bundled build's license changes, `THIRD-PARTY-NOTICES.md` § Licensing (and the
  bundled `LICENSE`) must be updated to match — the notices are the single source of truth for
  what was actually shipped.

---

*Related:* ADR 0010 (shared, gitignored, ABI-pinned ffmpeg bundling — introduced the
`-FfmpegSource` seam this decision hangs on) and ADR 0011 (self-contained single-file
distribution — the artifact this license attaches to). The lossless `-c copy` stream-copy
contract that keeps the ffmpeg coupling loose is recorded in `docs/ARCHITECTURE.md` and
relied on by ADR 0009's keyframe-snap.
