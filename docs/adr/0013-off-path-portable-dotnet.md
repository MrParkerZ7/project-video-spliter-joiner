# ADR 0013: Absolute dotnet path baked into packaging

## Status

Accepted

## Context

The .NET 8 SDK in this build environment is a **portable, off-PATH** install: it lives at
`D:\_env_storeage\dotnet` and is deliberately **not** added to the machine `PATH` (documented in
root `CLAUDE.md` and `README.md` — both invoke it as `D:\_env_storeage\dotnet\dotnet.exe`). A bare
`dotnet build` / `dotnet publish` therefore resolves to *nothing* (or, worse, to some other SDK that
happens to be on PATH).

`packaging/package.ps1` — the one script that produces a shippable artifact — depends on `dotnet`
for its most expensive step: the `dotnet publish` single-file, self-contained, win-x64 build
(`& $Dotnet publish $AppProj -c Release -r win-x64 -p:PublishSingleFile=true -o $PublishDir`, per the
publish props in `VideoSplitJoiner.App.csproj` gated on `'$(PublishSingleFile)' == 'true'`). If the
script relied on a bare `dotnet` on PATH, packaging would fail on this box, and — more dangerously —
could succeed against an unintended SDK.

Two forces pull in opposite directions:

- **Reproducibility here** — the packaging run must find *this* portable SDK reliably, unattended.
- **Portability elsewhere** — the absolute path is machine-specific; a CI box or another dev's
  machine will not have `D:\_env_storeage\dotnet\dotnet.exe`.

The script's own header already flags the constraint (*"dotnet is NOT on PATH in this
environment"*), so the resolution needed to be explicit rather than assumed.

## Decision

**Bake the absolute portable-SDK path in as a `param()` default, but keep it overridable.**

`packaging/package.ps1` declares:

```powershell
param(
    [string]$FfmpegSource,
    [string]$Dotnet       = 'D:\_env_storeage\dotnet\dotnet.exe'
)
```

- The `-Dotnet` default is the absolute path to the portable SDK, so a bare
  `powershell -File packaging/package.ps1` works on this box with zero PATH setup.
- It is a **parameter, not a hard-coded literal** at the call site — any other machine passes
  `-Dotnet <its path>` (or a bare `dotnet` if on PATH) without editing the script.
- The script **fails loudly** before the expensive publish if the resolved dotnet is absent:
  `if (-not (Test-Path $Dotnet)) { throw "dotnet not found at '$Dotnet'." }` — no silent fallback
  to an unintended SDK.

This mirrors the same off-PATH invocation already codified for humans in `CLAUDE.md` / `README.md`,
and is captured here **plus** the DEV runbook so the machine-specific path is discoverable and
maintained in one authoritative place rather than rediscovered each time packaging breaks.

## Consequences

**Positive**

- Packaging is turnkey on the primary build box — no per-run PATH juggling, no wrong-SDK ambiguity.
- The absolute path is a **default**, not a lock-in: `-Dotnet` keeps CI and other machines
  first-class without a source edit (parallels the `-FfmpegSource` override that lets a permissive
  ffmpeg build be swapped in — see ADR 0010).
- The explicit `Test-Path` guard converts a would-be confusing mid-publish failure into a single
  clear error at the top of the run.

**Negative / trade-offs**

- The default path is **machine-specific**; a clone on a box without `D:\_env_storeage\dotnet` must
  pass `-Dotnet` or the run throws immediately. The throw is intentional (loud > silent-wrong).
- The portable-SDK location is now referenced in three places (`CLAUDE.md`, `README.md`, this ADR +
  the DEV runbook) — moving the SDK means updating all of them.

**Forced follow-ons**

- The DEV runbook must document the off-PATH SDK path, the bare-invocation gotcha, and the
  `-Dotnet` override for CI / foreign machines — this ADR is the decision record; the runbook is the
  operational how-to.
- A CI pipeline must supply `-Dotnet` explicitly (or provision an on-PATH SDK) rather than assuming
  the baked-in default resolves.
- If the portable SDK is ever relocated or added to PATH, revisit this ADR and the runbook so the
  default no longer points at a dead path.

_Related: ADR 0010 (shared-ffmpeg bundling — same "sensible absolute default, overridable via a
`-…Source`/`-…` parameter" pattern for the packaging script's other machine-specific input) and
ADR 0011 (self-contained single-file win-x64 publish — the `dotnet publish` step this path drives).
The stream-copy split path (ADR 0001, superseded by the keyframe-snap decision in ADR 0009) is
unaffected: this decision governs only how the build toolchain is located, not how video is cut._
