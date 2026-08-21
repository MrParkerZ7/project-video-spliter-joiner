# ADR 0003: Cancel safety — temp-then-move + refuse-don't-corrupt

## Status

Accepted.

## Context

Both Core engines produce their output by shelling out to a long-running `ffmpeg`
process that the user can cancel at any moment (the operation is driven through a
`CancellationToken` and reports progress live). Every operation is a lossless
`-c copy` pass — a stream-copy split (`src/Core/Split/SplitEngine.cs`) or a
concat-demuxer join (`src/Core/Join/JoinEngine.cs`) — so the byte stream ffmpeg
writes IS the user's real media, not a re-encode that could be re-run cheaply. That
raises the stakes on a mid-run abort: if ffmpeg were writing straight to the
user-named destination, a cancel (or a crash, or a failed exit) would leave a
truncated file sitting exactly where a valid one belongs, silently overwriting a
previous good copy.

The forces:

- **A cancel must never corrupt a destination.** ffmpeg emits container bytes
  incrementally; killing it mid-write yields a partial, unplayable file. That file
  must not be the path the user asked for.
- **A refusal must write nothing at all.** The join is all-or-nothing: on any
  incompatibility it returns a refusal (ADR 0002 strategy 4). A refusal that had
  already touched the output would violate its own contract — `JoinResult` guarantees
  *"a refusal NEVER leaves an output file behind"* (`src/Core/Join/JoinResult.cs`),
  and an invalid split request throws `SplitException`
  (`src/Core/Split/SplitException.cs`) before any file work begins.
- **Overwrite is a deliberate, up-front decision — not a side effect of writing.**
  Clobbering an existing output must be gated by an explicit `Overwrite` flag checked
  *before* ffmpeg runs, so the destructive step is a decision, not an accident of where
  the encoder happened to point.
- **Temp litter must not survive the operation** even on the abort and failure paths.

## Decision

Both engines write to a **`.vsj-*-<guid>` temp location first, move into place only
after ffmpeg succeeds, and clean up in `finally`** — never letting ffmpeg's handle
point at a user-named destination. Refusals and invalid requests short-circuit before
any output byte is written.

1. **Temp-then-move, per engine.**
   - Split extracts every part into a fresh temp *directory*
     `.vsj-split-<guid>` created under the output dir
     (`SplitEngine.SplitAsync`, `Path.Combine(req.OutputDir, ".vsj-split-" + Guid.NewGuid().ToString("N"))`),
     runs ffmpeg into it, and only then calls `MoveTempSegmentsIntoPlace` to move each
     produced part onto its planned destination. Its doc comment states the intent
     verbatim: *"Doing the move AFTER ffmpeg succeeds means a cancel mid-run leaves only
     temp files (cleaned in finally), never a half-written FINAL segment."*
   - Join writes to a temp *file* beside the target
     (`outFull + ".vsj-join-<guid>" + ext`) plus a concat-list file
     (`vsj-join-<guid>.txt` in the temp path), runs ffmpeg into the temp output, and
     only on success does `File.Move(tempOut, outFull)`.

2. **Move only after ffmpeg success.** In both engines the move is downstream of the
   `_runner.RunAsync(...)` success check (`result.Success`). A non-zero exit skips the
   move — Split maps it to a `SplitException` via `FfmpegErrorMapper`, Join returns
   `JoinResult.RefusedWithLog` — so a failed run leaves the destination untouched.

3. **Clean up in `finally` (+ cancel `catch`).** Split wraps the whole extract-and-move
   in `try { … } finally { TryDeleteDirectory(tempDir); }`, so the temp dir is swept on
   success, failure, and cancellation alike. Join deletes the concat list in `finally`
   and, on `catch (OperationCanceledException)`, deletes the temp output before
   re-throwing (its per-file cleanup is targeted rather than a blanket dir wipe). Both
   cleanup helpers (`TryDeleteDirectory`, `TryDeleteFile`) swallow their own errors — a
   locked temp is not a caller-facing failure.

4. **Overwrite is permission-checked upstream; the move trusts that check.** Each engine
   refuses to clobber an existing output *before* running ffmpeg unless `Overwrite=true`
   (Split loops its selected destinations and throws `SplitException`; Join returns
   `JoinResult.Refused` with an `output_exists` mismatch). The later move step therefore
   deletes an existing destination with only a terse *"Overwrite already permission-checked
   upstream"* comment — the guard lives at the front, not at the write.

5. **Refuse-don't-corrupt.** A refused join returns `JoinResult.Refused` /
   `RefusedWithLog` **before** the temp output is ever moved (pre-flight refusals never
   even create it), so zero bytes reach the destination. An invalid split request throws
   `SplitException` before the temp directory is created. Corruption of a destination by a
   refusal is structurally impossible, not merely avoided by convention.

## Consequences

**Positive**

- A cancel or crash mid-run can, at worst, leave a `.vsj-*-<guid>` temp artifact —
  never a truncated file at a real destination, and never an overwritten good copy.
- The refusal contract (ADR 0002) is upheld at the filesystem level: the output path is
  only ever touched by a `File.Move` that runs *after* a successful ffmpeg exit.
- Overwrite is a single, auditable up-front gate; the write path stays dumb and trusts it.
- Temp litter is bounded — the `finally`/`catch` sweep runs on every exit path.

**Negative**

- **Move-not-write assumes same-volume atomicity.** `File.Move` is cheap and atomic only
  when temp and destination share a volume. Split's temp dir lives *inside* the output dir
  and Join's temp file sits *beside* the output, so both stay same-volume by construction —
  but that placement is a load-bearing invariant, not a coincidence, and a future change
  that relocates temp to another drive would silently turn the move into a slow copy+delete
  that is no longer atomic.
- **Transient double disk usage.** During the split's finalize phase the temp copies and
  the moved-into-place files briefly coexist; the pre-flight free-space check
  (`EnsureEnoughFreeSpace`) sizes for the input, not for temp + destination simultaneously.
- **Two different cleanup shapes.** Split uses a blanket temp-directory delete in `finally`;
  Join uses targeted per-file deletes split across `catch` + `finally`. The asymmetry
  matches each engine's output shape (N segments in a dir vs one file), but a reader must
  learn both.

**Forced follow-ons**

- Any new engine that runs ffmpeg and produces output must adopt this same
  temp-then-move + refuse-writes-nothing discipline — ADR 0002 already names it as a
  forced follow-on of its error contract.
- The temp location must remain on the destination volume; a relocation would need to
  restore atomicity (e.g. a same-volume staging dir) or accept the copy+delete downgrade
  explicitly.
- Because the destructive delete rides on the upstream `Overwrite` gate, any new write path
  into these engines must route through that gate, not add its own inline overwrite.

See ADR 0002 (`0002-error-model.md`) for the per-subsystem error contract this cancel-safety
discipline enforces at the filesystem layer (the join's *refusal-writes-nothing* rule and
the split's throw-on-invalid-request), and ADR 0009 (`0009-two-path-keyframe-scan.md`) for
why every cut is a keyframe-snapped `-c copy` — the lossless-copy property that makes a
mid-write abort so costly, and thus makes temp-then-move worth its price.
