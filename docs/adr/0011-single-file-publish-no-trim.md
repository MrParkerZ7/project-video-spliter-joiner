# ADR 0011: Self-contained single-file win-x64 + ReadyToRun, PublishTrimmed banned

## Status

Accepted

## Context

The product ships as a downloadable Windows app a user can run without installing
the .NET runtime (goal G-001; packaging task T-010). Two forces shape how it is
published:

- **Runtime shape.** The app is WPF (`net8.0-windows`, `UseWPF=true`,
  `src/App/VideoSplitJoiner.App.csproj`) and must run on a machine with **no .NET
  SDK/runtime** — so the publish must be `SelfContained` for a fixed
  `RuntimeIdentifier` (`win-x64`). It also bundles a native ffmpeg **shared** build
  (ADR 0010): the FFME preview P/Invoke-loads `av*-NN.dll` in-process, and the
  split/join engine shells out to `ffmpeg.exe`/`ffprobe.exe`. Both consumers resolve
  those binaries from an app-local `ffmpeg/` folder next to the assembly —
  `FfmpegBinaryLocator.Resolve` probes `AppContext.BaseDirectory\ffmpeg\`
  (`src/Core/Ffmpeg/FfmpegBinaryLocator.cs`), and `App.OnStartup` points
  `FFmpegDirectory` at the same folder (ADR 0004 / 0010).

- **Two hazards of aggressive publish flags.**
  1. **Single-file + WPF native libs.** WPF ships managed *and* native assemblies. A
     naive `PublishSingleFile` leaves the native libs unable to load from inside the
     bundle unless they self-extract — hence `IncludeNativeLibrariesForSelfExtract`.
  2. **Trimming is unsafe for WPF.** `PublishTrimmed` uses static analysis to remove
     "unused" code, but WPF resolves types by **reflection** and loads XAML/resource
     dictionaries (`src/App/Themes/Tokens.xaml`, `Controls.xaml`, the bundled IBM Plex
     `Resource` fonts) by name at runtime. The trimmer cannot see those references and
     strips types the app actually needs, producing a build that compiles but throws
     at runtime. WPF trimming is explicitly unsupported.

Layered on top is a **developer-velocity** force: these publish settings must not tax
the everyday inner loop. A plain `dotnet build` / `dotnet test` should stay a fast,
framework-dependent, multi-file build — never a slow self-contained single-file
compile with a ReadyToRun (AOT-ish) pass.

## Decision

Publish a **self-contained, single-file `win-x64`** build with **ReadyToRun**, and
**never enable `PublishTrimmed`**. The settings are **condition-gated on
`PublishSingleFile`** so they apply only at publish time.

In `src/App/VideoSplitJoiner.App.csproj`, inside
`<PropertyGroup Condition="'$(PublishSingleFile)' == 'true'">`:

- `RuntimeIdentifier = win-x64`, `SelfContained = true` — runs without an installed
  runtime.
- `IncludeNativeLibrariesForSelfExtract = true` — WPF's native libs self-extract from
  the single-file bundle so they load (paired with ADR 0004's follow-on).
- `EnableCompressionInSingleFile = true` — shrink the bundle.
- `PublishReadyToRun = true` — precompiled native images for faster cold start;
  explicitly noted as *safe for WPF (unlike trimming)*.

`PublishTrimmed` is **absent by design**, guarded by an inline comment:
*"Trimming (PublishTrimmed) is deliberately NOT enabled — trimming is unsupported and
unsafe for WPF (reflection / XAML resource loading breaks). **Do not add it.**"* The
ban is a standing instruction to future editors, not just an omission.

Because the whole group is gated on `'$(PublishSingleFile)' == 'true'`, a normal
`dotnet build` / `dotnet test` picks up **none** of it — it stays framework-dependent
and multi-file, so the inner loop is unaffected. The settings activate only via
`dotnet publish … -p:PublishSingleFile=true`, which is exactly how
`packaging/package.ps1` invokes it
(`dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true`).

## Consequences

**Positive**

- **Runs with no .NET install.** Self-contained `win-x64` is a drop-and-run `.zip`
  distributable (T-010).
- **Fast cold start** from ReadyToRun without the runtime-crash risk trimming carries.
- **Zero inner-loop tax.** The `PublishSingleFile` condition keeps `dotnet build`/`test`
  fast and framework-dependent; only packaging pays the single-file/R2R cost.
- **Native ffmpeg + WPF libs both load.** `IncludeNativeLibrariesForSelfExtract`
  self-extracts WPF's native libs; the packaged `ffmpeg/` folder (laid down by
  `package.ps1`, ADR 0010) satisfies both `FfmpegBinaryLocator` and FFME's
  `FFmpegDirectory` from one location.

**Negative**

- **Large artifact.** Self-contained + R2R + the whole .NET runtime + the ~180 MB
  ffmpeg shared build (ADR 0010) make a heavy download; `EnableCompressionInSingleFile`
  only softens it.
- **Single-RID lock-in.** Pinned to `win-x64` — any other RID (arm64, x86) needs a
  separate publish; cross-platform is out of scope (T-010).
- **No dead-code stripping.** Banning `PublishTrimmed` forgoes the size reduction
  trimming would give — an accepted cost to keep WPF reflection/XAML correct.
- **Publish-only validation gap.** The single-file/self-extract path is exercised only
  when publishing, not on every `dotnet build` — regressions in that path surface at
  package time, not in the inner loop.

**Forced follow-ons**

- Keep `IncludeNativeLibrariesForSelfExtract = true` in lock-step with the FFME native
  dependency (ADR 0004's follow-on; ADR 0010's shared-build layout).
- **`PublishTrimmed` stays banned.** If a future change reintroduces trimming, it must
  first prove the WPF reflection/XAML surface survives — the default answer is no.
- Any new `win-x64`-only publish setting belongs **inside** the
  `Condition="'$(PublishSingleFile)' == 'true'"` group so the plain build stays fast and
  RID-agnostic.
- `package.ps1` must keep invoking publish with `-p:PublishSingleFile=true` (the trigger
  for this whole group) and continue bundling the ffmpeg shared build alongside the exe.
