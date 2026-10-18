# ADR 0002: A per-subsystem error contract, not one uniform strategy

## Status

Accepted.

## Context

The app shells out to two external binaries (`ffmpeg`, `ffprobe`) and layers
four Core subsystems on top of them — media probing, splitting, joining, and
diagnostics (logging + thumbnails). Each subsystem has a *different* notion of
what "failure" means, so a single uniform error strategy (all-exceptions, or
all-result-types) would misrepresent at least one of them and push branching
logic into the wrong layer. The forces:

- **ffmpeg's non-zero exit is normal, not exceptional.** A bad input, an
  unsupported codec, or a refused overwrite makes ffmpeg exit non-zero as part
  of ordinary operation. Throwing on every non-zero exit would turn the common
  case into an exception. See `FfmpegResult` (`src/Core/Ffmpeg/FfmpegResult.cs`):
  it is *"Returned for ANY exit code"* and exposes `Success => ExitCode == 0`.
- **ffprobe is a query — its failure IS exceptional at the runner boundary.** A
  probe that exits non-zero returned no data; the caller cannot continue.
  `FfprobeRunner.RunJsonAsync` (`src/Core/Ffmpeg/FfprobeRunner.cs`) throws
  `FfprobeException` (`src/Core/Ffmpeg/FfmpegExceptions.cs`) carrying the exit
  code + stderr tail. This is the deliberate asymmetry with the ffmpeg runner,
  which for the identical process discipline returns a `FfmpegResult` instead.
- **But "bad file" must not throw at the *domain* boundary.** The join engine
  probes every input and needs a bad clip to be *data*, not a `catch`.
  `MediaProbe.ProbeAsync` (`src/Core/Media/MediaProbe.cs`) therefore catches the
  runner's `FfprobeException` and converts it into a typed
  `ProbeResult.ProbeFailed` (`src/Core/Media/ProbeResult.cs`).
- **A lossless join must be all-or-nothing.** The concat is `-c copy`; on any
  incompatibility (or a failed ffmpeg run) the join must leave **no** partial
  output behind — a refusal, not a half-written file.
- **Diagnostics must never crash the operation they describe.** Writing a log
  file or rendering a hover thumbnail is a side-benefit; if it fails, the split
  / join / preview the user actually asked for must still succeed.

## Decision

Adopt a **four-way (five-subsystem) error contract**, each subsystem using the
signalling shape that matches its failure semantics:

1. **ffmpeg → `FfmpegResult` (result object).** `IFfmpegRunner.RunAsync`
   returns `FfmpegResult(ExitCode, StdErrTail)` for every exit code; callers
   branch on `.Success`. Non-zero is a value, never a throw. (Only
   binary-not-found and cancellation throw — `FfmpegNotFoundException`,
   `OperationCanceledException`.)

2. **ffprobe → `FfprobeException` (exception).** `IFfprobeRunner.RunJsonAsync`
   throws `FfprobeException` on non-zero exit. This is the intentional
   **asymmetry** with (1): same process plumbing, opposite failure shape,
   because a query with no result cannot hand back a usable value.

3. **probe → `ProbeResult.ProbeFailed` (typed failure / discriminated union).**
   `MediaProbe.ProbeAsync` never throws for a bad file. Empty path, missing
   file, non-zero ffprobe (caught `FfprobeException`), invalid JSON, and
   zero-stream files all become `ProbeResult.Failure(reason)`. Callers pattern-
   match `ProbeSucceeded` / `ProbeFailed` instead of catching. (Cancellation
   still surfaces as `OperationCanceledException`; `GetKeyframesAsync` keeps
   argument-throwing semantics — the no-throw rule is scoped to *probing a
   file's content*.)

4. **join → `JoinResult.Refused` (refusal).** `JoinEngine.JoinAsync`
   (`src/Core/Join/JoinEngine.cs`) returns `JoinResult` where *exactly one* of
   success/refusal is populated and **a refusal never leaves an output file**
   (`src/Core/Join/JoinResult.cs`). Pre-flight refusals (empty input, empty
   output path, incompatible clips, existing-output-without-overwrite, a probe
   failure on any input, the internal copy-invariant guard) use `Refused`; a
   failed ffmpeg concat run uses `RefusedWithLog`, threading the saved log path
   + full stderr through. The split side mirrors this split-personality with its
   *own* pairing tuned to its semantics: an invalid *request* throws
   `SplitException` (`src/Core/Split/SplitException.cs`), while user-fixable
   adjustments (out-of-range / duplicate cuts) are non-fatal `Warnings` on
   `SplitResult` (`src/Core/Split/SplitResult.cs`).

5. **logging + thumbnails → best-effort null (swallow + null).**
   `ErrorLogWriter.TryWrite` / `TryWriteCrash`
   (`src/Core/Errors/ErrorLogWriter.cs`) and
   `FfmpegThumbnailService.GetThumbnailAsync`
   (`src/Core/Thumbnails/FfmpegThumbnailService.cs`) wrap all work in `try` and
   return `null` (or no-op) on *any* failure — a logging or preview problem must
   never crash the real operation. The thumbnail service explicitly cites the
   log writer as the pattern it matches.

## Consequences

**Positive**

- Each caller writes the branching its subsystem actually needs: `.Success` on
  ffmpeg, `try/catch` at the ffprobe runner seam, `switch` on `ProbeResult`,
  one-of on `JoinResult`, and `?? fallback` on the best-effort pair. No
  subsystem pays for another's failure model.
- The refusal contract makes "the join wrote a corrupt/partial file" structurally
  impossible — the type guarantees a refusal carries no `OutputPath`.
- Best-effort diagnostics are crash-proof by construction, including inside the
  global crash handler (`TryWriteCrash` never throws — no recursion).
- The captured stderr flows cleanly to the user surface: a failed run's full log
  path + text ride on `JoinResult`/`SplitException` into `UserFacingError`
  (`src/Core/Errors/UserFacingError.cs`).

**Negative**

- **Inconsistent signatures across the Core.** A new contributor must learn
  which subsystem throws, which returns a result, and which returns null — there
  is no single rule. This ADR is the map.
- **The ffmpeg/ffprobe asymmetry is a footgun** if mistaken for an accident:
  calling the ffprobe runner without a `try` will let `FfprobeException` escape,
  whereas the identical-looking ffmpeg call cannot throw on exit code.
- **Best-effort null hides real failures.** A silently unwritable log dir or a
  never-appearing thumbnail is invisible by design; debugging those means
  reaching past the swallowed `catch`.

**Forced follow-ons**

- `MediaProbe` is the mandatory adapter that converts strategy (2) into (3):
  every content-probe path must catch `FfprobeException` and return
  `ProbeFailed`, or the no-throw guarantee callers rely on breaks.
- Any new engine that runs ffmpeg and can fail must decide up front which of the
  five shapes it adopts, and (if it produces output) uphold the
  refusal-writes-nothing / temp-then-move discipline `JoinEngine` uses.
- Callers of the best-effort pair must treat `null` as "unavailable," never as
  an error to surface.

See ADR 0004 (`0004-ffme-over-mediaelement.md`) for the preview player, whose
still-unplayable-source handling follows the same best-effort spirit (graceful
`Failed` → banner, never a crash).
