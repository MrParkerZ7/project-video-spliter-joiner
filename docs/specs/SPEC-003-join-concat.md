---
id: SPEC-003
slug: join-concat
area: core
title: Join / concat engine
status: current
sources:
  - src/Core/Join/JoinEngine.cs
  - src/Core/Join/JoinRequest.cs
  - src/Core/Join/JoinResult.cs
  - src/Core/Join/CompatChecker.cs
  - src/Core/Join/CompatReport.cs
  - src/Core/Join/JoinArgsBuilder.cs
  - src/Core/Join/Mismatch.cs
serves-goal: [G-001]
updated: 2026-08-22
---

## What
The join engine (`JoinEngine : IJoinEngine`) glues several video clips, in a caller-given
order, into one output file via **lossless stream-copy** (`ffmpeg -c copy` through the concat
demuxer) — never a re-encode. Before touching ffmpeg it runs a pure pre-flight
compatibility check (`CompatChecker`) that takes the first clip as the reference and compares
every other clip's video (codec, width, height, pixel format, time base) and audio (codec,
sample rate, channels). On **any** mismatch — or a probe failure, an empty input set, an empty
output path, or an existing output when `Overwrite` is false — the join **refuses and writes
nothing**, returning a `CompatReport` that names the offending clip and field. A compatible set
is concatenated to a temp file that is then moved into place; a single input is passed through
with the same copy command. Progress is reported 0..1 against the summed input duration and the
engine emits ordered stage transitions (Checking compatibility → Joining → Finalizing → Done).

## Why
Re-encoding on join is slow and lossy; the product promise is "lossless · no re-encode."
Stream-copy concat is only safe when the clips share codec/resolution/pixel-format/timebase and
audio layout — otherwise ffmpeg emits a broken or unplayable file. Rather than silently produce
a corrupt output, the engine refuses up front with a human-readable reason naming the exact
clip and conflicting field, and guarantees a refused join never leaves a partial or misleading
file behind.

## Scope
**In:** the `Core/Join` engine — request/result shape, the pure compatibility comparison, the
`-c copy` concat-demuxer command construction + copy invariant, the concat list-file rendering
and path escaping, temp-then-move output handling, cancellation cleanup, ffmpeg-failure logging,
progress, and staged status.
**Out:** the Join screen UI / view-model wiring (`JoinViewModel`, drag-reorder, live compat
banner — its own App-layer spec), the ffmpeg runner + probe internals (`IFfmpegRunner` /
`IMediaProbe` — separate specs), and the split engine.

## Current behavior & invariants
Grounded in `JoinEngine`, `CompatChecker`, `JoinArgsBuilder`, `JoinRequest`, `JoinResult`,
`CompatReport`, `Mismatch`.

- **I1** — Order-significant concat: `JoinAsync` joins `JoinRequest.InputPaths` head-to-tail in
  the listed order; a multi-clip compatible join yields an output whose duration ≈ the sum of
  the input durations (`JoinEngine.JoinAsync` → `JoinArgsBuilder.RenderConcatList` preserves
  order).
- **I2** — Result duality: `JoinResult` populates exactly one of `{Success + OutputPath}` or
  `{!Success + Refusal}`; a refusal always has `OutputPath == null` and leaves **no** output
  file on disk (`JoinResult.Ok` / `JoinResult.Refused`).
- **I3** — An empty / null `InputPaths` is refused with a `Mismatch` of field `input_count` and
  **no** ffmpeg is launched (`JoinAsync` early guard; `CheckCompatibilityAsync` and
  `CompatChecker.Compare` both return `input_count` for zero inputs).
- **I4** — An empty / whitespace `OutputPath` is refused with a `Mismatch` of field `output`
  before any ffmpeg run (`JoinAsync` `string.IsNullOrWhiteSpace` guard).
- **I5** — A missing file or failed probe on **any** input is reported as a refusal with field
  `probe` naming the 1-based clip — the engine never throws for a bad input
  (`CheckCompatibilityAsync` probe loop collects `probeFailures`).
- **I6** — A video-codec difference vs the reference (clip 1) yields a `Mismatch` of field
  `codec` naming the offending clip (`CompatChecker.CompareVideo`).
- **I7** — A resolution difference (width or height) yields a `Mismatch` of field `resolution`
  naming the clip and both `WxH` values (`CompatChecker.CompareVideo`).
- **I8** — A pixel-format difference yields a `Mismatch` of field `pix_fmt`
  (`CompatChecker.CompareVideo`).
- **I9** — A time-base difference yields a `Mismatch` of field `time_base`
  (`CompatChecker.CompareVideo`).
- **I10** — A video-stream presence difference (reference has video, clip does not, or vice
  versa) yields a `Mismatch` of field `video_presence` (`CompatChecker.CompareVideo`).
- **I11** — An audio-codec difference yields a `Mismatch` of field `audio_codec`
  (`CompatChecker.CompareAudio`).
- **I12** — An audio sample-rate difference yields a `Mismatch` of field `audio_sample_rate`
  (`CompatChecker.CompareAudio`).
- **I13** — An audio channel-count difference yields a `Mismatch` of field `audio_channels`
  (`CompatChecker.CompareAudio`).
- **I14** — An audio-stream presence difference yields a `Mismatch` of field `audio_presence`;
  when neither reference nor clip has audio the audio checks no-op (`CompatChecker.CompareAudio`).
- **I15** — When a clip differs in multiple fields, each differing field yields its own separate
  `Mismatch` (they are reported together, not short-circuited) (`CompatChecker.Compare` loop).
- **I16** — A single input is self-compatible (`CompatChecker.Compare` returns `Ok()` for one
  input) and is passed through with the same `-c copy` concat command — it still produces a
  playable output (`CompatChecker` count==1 branch; `JoinAsync` single-input passthrough).
- **I17** — Stream-field string comparisons (video codec, pix_fmt, time_base, audio codec) are
  case-insensitive (`StringComparison.OrdinalIgnoreCase` in `CompatChecker`).
- **I18** — With `Overwrite == false`, a join whose `OutputPath` already exists is refused with a
  `Mismatch` of field `output_exists` **before** any ffmpeg runs (`JoinAsync` `File.Exists`
  guard).
- **I19** — With `Overwrite == true`, an existing file at `OutputPath` is replaced (the pre-flight
  overwrite guard is skipped and the finalize step deletes any existing target before the move)
  (`JoinAsync` `!req.Overwrite && File.Exists` guard + finalize `File.Delete`).
- **I20** — The built ffmpeg command is a concat-demuxer stream-copy:
  `-y -f concat -safe 0 -i <listFile> -map 0 -c copy <out>`
  (`JoinArgsBuilder.ConcatCopy`).
- **I21** — No `ForbiddenEncoderTokens` (e.g. `-c:v`, `-c:a`, `-crf`, `libx264`, `-vf`,
  `-filter_complex`, …) ever appear in the built command — join never re-encodes
  (`JoinArgsBuilder.ForbiddenEncoderTokens` + `ConcatCopy`).
- **I22** — `SatisfiesCopyInvariant(tokens)` is true iff a bare `copy` token is present **and**
  none of `ForbiddenEncoderTokens` appears (`JoinArgsBuilder.SatisfiesCopyInvariant`).
- **I23** — Runtime invariant guard: before launching ffmpeg the engine asserts the built command
  with `SatisfiesCopyInvariant`; a violation refuses with a `Mismatch` of field `invariant`
  and launches nothing (`JoinAsync` `if (!JoinArgsBuilder.SatisfiesCopyInvariant(...))`).
- **I24** — `RenderConcatList` emits one `file '<absolute-path>'` line per input, in order,
  with each path made absolute, joined by `\n` and terminated with a trailing `\n`
  (`JoinArgsBuilder.RenderConcatList`).
- **I25** — Each concat-list path is single-quote wrapped and an embedded single quote is escaped
  the concat-demuxer way (`'` → `'\''`); spaces are preserved verbatim inside the quotes
  (`JoinArgsBuilder.QuoteConcatPath`).
- **I26** — On success the output is written by rendering the concat list + a temp output file
  and then moving the temp into `Path.GetFullPath(OutputPath)`; the parent directory is created
  if absent (`JoinAsync` temp-then-`File.Move`, `Directory.CreateDirectory`).
- **I27** — On cancellation the partially-written temp output is deleted and
  `OperationCanceledException` is rethrown; the temp concat list file is always cleaned up in the
  `finally` block regardless of outcome (`JoinAsync` catch/finally, `TryDeleteFile`).
- **I28** — An ffmpeg concat run failure returns `JoinResult.RefusedWithLog` with a `Mismatch` of
  field `ffmpeg`, deletes the temp output (no file left behind), persists the **full** stderr
  (+ command + exit code + timestamp) to a per-run log via `ErrorLogWriter`, and threads the
  `LogFilePath` + `FullStdErr` onto the result (`JoinAsync` `!result.Success` branch).
- **I29** — The engine emits ordered stage transitions through the optional status channel:
  `Checking compatibility` → `Joining` → `Finalizing` → `Done`, synced to real phases
  (`JoinAsync` `status?.Report(...)` calls).
- **I30** — The `Joining` stage detail is `"1 clip"` for a single input and `"{N} clips"` for
  multiple inputs (`JoinAsync` `req.InputPaths.Count == 1 ? "1 clip" : $"{...} clips"`).
- **I31** — The numeric progress channel reports 0..1 against the summed input duration and
  reaches `1.0` on success; a probe hiccup while summing degrades progress to unknown
  (null total) without affecting correctness (`JoinAsync` `SumDurationsAsync` + `progress?.Report(1.0)`).

## Links
- Design: — (T-005 in `docs/todo/_history.md`; join UI is T-008)
- Goals: G-001 (ship v1.0 — stream-copy, no re-render)
- Related specs: SPEC-001/002 (split engine — sibling `-c copy` guard) · Join UI / `JoinViewModel` spec (App layer)
- Key code: `src/Core/Join/JoinEngine.cs` · `CompatChecker.cs` · `JoinArgsBuilder.cs` · `JoinRequest.cs` · `JoinResult.cs` · `CompatReport.cs` · `Mismatch.cs`
- Tests: `tests/Core.Tests/JoinEngineUnitTests.cs` · `JoinEngineIntegrationTests.cs` · `CompatCheckerUnitTests.cs` · `JoinArgsInvariantTests.cs` · `StagedStatusIntegrationTests.cs` (Join) · `tests/App.Tests/StagedStatusWiringTests.cs` (Join wiring)
