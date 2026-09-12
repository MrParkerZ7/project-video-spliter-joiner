---
id: SPEC-016
slug: error-reporting
area: core
title: Error reporting — classification, copyable text, per-run log
status: current
sources:
  - src/Core/Errors/ErrorCategory.cs
  - src/Core/Errors/FfmpegErrorMapper.cs
  - src/Core/Errors/UserFacingError.cs
  - src/Core/Errors/ErrorLogWriter.cs
  - src/App/Views/ErrorActions.cs
serves-goal: [G-010]
updated: 2026-09-12
---

## What
The user-facing error model: how a failed ffmpeg run becomes something a person can read, copy, and
send. Three Core types plus one thin App glue class. `FfmpegErrorMapper` classifies a failure from
**only** the captured stderr tail + the exit code, by scanning for known ffmpeg signatures in a fixed
first-match order, and returns a `UserFacingError` carrying one of nine `ErrorCategory` values, a
**fixed friendly headline**, an optional actionable hint, and the raw tail — the raw stderr is never
promoted to the headline, not even in the `Unknown` fallback. `UserFacingError` is an immutable record
that computes the **copy blob** (`CopyText`) and the **detail-box body** (`DetailText`) itself, so the
clipboard text is unit-testable and identical everywhere it is rendered. `ErrorLogWriter` persists the
complete diagnostic text of a failed run to a per-run file under
`%LOCALAPPDATA%/VideoSplitJoiner/logs/<op>-<yyyyMMdd-HHmmss>.log` and is **best-effort** — any write
failure is swallowed and returns `null`. `ErrorActions` (App) is the OS glue behind the two affordances:
copy to clipboard, and reveal the saved log in Explorer; both swallow failures.

## Why
G-010 came from a real report: a `.ts` split at a Japanese path failed with `exit -28`, the app showed a
benign mpegts warning as the failure text, and the user could not even select the message to send it. The
three fixes are the three halves of this spec. **Classify on cause, not on the last line printed** — an
`ENOSPC` write failure is reported as out-of-space even when the retained tail carries only an unrelated
warning (`I12`). **Never surface raw stderr as the headline** — an uncatalogued failure gets a fixed
sentence and keeps the output in the detail (`I22`, `I23`); a headline is a thing you can act on, a stderr
blob is not. **Keep the whole output** — the UI shows a bounded tail, so the full text goes to a per-run
log file whose path travels with the error and can be opened or copied (`I26`, `I43`). Everything on the
diagnostic path obeys ADR-0002 § 5: a logging problem must never crash the operation it is describing, so
the writer returns `null` instead of throwing and the App actions swallow clipboard/shell failures.

## Scope
**In:** `ErrorCategory`; the whole of `FfmpegErrorMapper` (both `Map` overloads, the signature sets, the
first-match order, the headline/hint text, the `Unknown` fallback); `UserFacingError`'s shape and its
computed `CopyText` / `DetailText` / `HasLogFile`; `ErrorLogWriter`'s **ffmpeg-failure** path
(`DefaultLogDirectory`, `TryWrite`, `BuildLogBody`, the shared `SanitizeOp` file-naming rule) and its
best-effort contract; and `ErrorActions` (`CopyError`, `TryCopy`, `OpenLog`).

**Out:** the process layer that produces `FfmpegResult`/`StdErrTail` and bounds it (SPEC-017 — the mapper
consumes that tail, it does not create it); `ErrorLogWriter`'s **crash** path `TryWriteCrash` /
`BuildCrashBody` (SPEC-015 — this spec covers only the ffmpeg-failure path, and the two share only
`SanitizeOp` + the logs dir); `OperationViewModel.MapException` / `HeadlineOf` and the `Failed` state
machine that holds the `Error` (SPEC-008); the engine-side carriers that thread the log path out —
`SplitException.LogFilePath`/`FullStdErr` (SPEC-001), `JoinResult.RefusedWithLog` (SPEC-003),
`BulkTrimEngine.MapSplitException` (SPEC-002); and the XAML error surfaces that bind these properties
(SPEC-010 / SPEC-011 / SPEC-012).

## Current behavior & invariants

### Classification — `FfmpegErrorMapper`
Grounded in `FfmpegErrorMapper` (both `Map` overloads, the local `Has` predicate, the signature blocks in
source order) and `ErrorCategory`. The mapper is a pure static function of (tail, exit code): no I/O, no
state, no clock.

- **I1** — Classification reads exactly two inputs — `result.StdErrTail` and `result.ExitCode`; the `FfmpegResult` overload does nothing but null-check (I2) and delegate to the two-arg overload, so nothing else about the result can influence the outcome *(FfmpegErrorMapper.Map(FfmpegResult))*.
- **I2** — `Map(FfmpegResult)` with a null result throws `ArgumentNullException` *(FfmpegErrorMapper.Map, `ArgumentNullException.ThrowIfNull`)*. *(not asserted)*
- **I3** — `Map(IReadOnlyList<string>, int)` tolerates a **null** tail, treating it as empty rather than throwing *(`stderrTail ?? Array.Empty<string>()`)*. *(not asserted — the empty-list case is)*
- **I4** — Every returned error carries `RawTail` = the tail joined with `Environment.NewLine`, non-null for every category including an empty tail, so a details expander always has the real output *(FfmpegErrorMapper.Map, `raw`)*.
- **I5** — Signature matching is a **case-insensitive substring** scan over that same joined string — not a line-anchored or regex match *(the local `Has` predicate, `StringComparison.OrdinalIgnoreCase`)*. *(not asserted — every test case supplies the signature in its canonical casing)*
- **I6** — Classification is **first-match over a fixed order**, most-specific first: Cancelled → BinaryNotFound → DiskFull → PermissionDenied → UnsupportedCodec → IncompatibleJoin → CorruptInput (invalid data) → CorruptInput (no usable stream) → InvalidArgument (missing file) → InvalidArgument (bad option) → Unknown. A tail matching two families resolves to the earlier one *(FfmpegErrorMapper.Map, block order — `:40`, `:50`, `:68`, `:80`, `:90`, `:107`, `:123`, `:136`, `:147`, `:157`, `:171`)*. *(not asserted — no test supplies a tail that matches two different family blocks; the two join tests each carry two triggers from within the same block, and the `-28` case of I12 reaches DiskFull by exit code over a tail that matches no block at all. The order is verified by reading the source only)*
- **I7** — Exit code `130`, `137` or `143`, **or** the phrase `Exiting normally, received signal 2`, classifies `Cancelled` with the headline `The operation was cancelled.` *(FfmpegErrorMapper.Map, cancellation block)*. *(the category is asserted; the test supplies both triggers at once so neither is isolated, and the headline wording is not asserted — only that it is non-blank)*
- **I8** — `Cancelled` is the only category returned with a **null** `Hint`; every other category returns a non-empty hint sentence *(FfmpegErrorMapper.Map, `Hint: null` on the cancellation branch only)*. *(not asserted)*
- **I9** — `BinaryNotFound` is triggered by any of `No such file or directory: 'ffmpeg`, `is not recognized as an internal or external command`, `ffmpeg: command not found`, `Cannot find ffmpeg`, `The system cannot find the file specified` *(FfmpegErrorMapper.Map, binary block)*. *(one of the five is asserted — `Map_BinaryNotFound_IsClassified` supplies `is not recognized as an internal or external command`; the other four are unexercised)*
- **I10** — Because the binary block precedes the missing-file block, the Windows phrasing `The system cannot find the file specified` classifies `BinaryNotFound`, **not** the `InvalidArgument` of I19 *(block order: binary before missing-file)*. *(not asserted)*
- **I11** — `DiskFull` is triggered by the phrase `No space left on device` **or** `ENOSPC` *(FfmpegErrorMapper.Map, disk block)*.
- **I12** — Exit code `-28` (`AVERROR(ENOSPC)`) alone classifies `DiskFull`, **even when the tail contains no space-related phrase at all** — the T-035/G-010 regression, where the retained tail was a benign mpegts warning that would otherwise have been surfaced as the cause *(FfmpegErrorMapper.Map, `exitCode == -28`)*.
- **I13** — The phrase `Permission denied` classifies `PermissionDenied` *(FfmpegErrorMapper.Map, permission block)*.
- **I14** — `UnsupportedCodec` is triggered by `Unknown encoder`, `Unknown decoder`, `Decoder not found`, `Encoder not found`, `Unsupported codec`, or the **split pair** forms (`Decoder` anywhere **and** `not found` anywhere; likewise `Encoder`) — the pair form is what catches ffmpeg's real `Decoder (codec av1) not found for input stream #0:0` wording *(FfmpegErrorMapper.Map, codec block)*. *(two of the seven trigger forms are asserted — `Unknown encoder` and the `Decoder`/`not found` pair; the other five are unexercised)*
- **I15** — `IncompatibleJoin` is triggered by `Unsafe file name`, `do not match the corresponding output link`, `Input link parameters`, `differ in dimension`, `Cannot find a matching stream`, `concat`, or `streams are not matching`, and is checked **before** both `CorruptInput` and `InvalidArgument` because concat failures usually also mention options *(FfmpegErrorMapper.Map, join block)*. *(two of the seven triggers are asserted — `Unsafe file name` and `do not match the corresponding output link`; the other five and the before-CorruptInput/InvalidArgument ordering are not — see I6)*
- **I16** — The bare substring `concat` is one of those triggers, so **any** failure whose stderr carries an ffmpeg `[concat @ …]` component prefix classifies `IncompatibleJoin` — including one whose tail also contains a corrupt-input or bad-option phrase, since those blocks come later *(FfmpegErrorMapper.Map, `Has("concat")`)*. *(not asserted in isolation — the join tests supply a second, more specific trigger alongside it)*
- **I17** — `Invalid data found when processing input`, `moov atom not found`, `Invalid NAL unit size`, or `error while decoding` classifies `CorruptInput` with the headline `The input file appears to be corrupt or unreadable.` *(FfmpegErrorMapper.Map, corrupt block)*. *(the category is asserted; the headline wording is not)*
- **I18** — `does not contain any stream` or `could not find codec parameters` classifies `CorruptInput` too, but with the **distinct** headline `The input file doesn't contain any usable video or audio.` — a present-but-empty file is not the same message as a damaged one *(FfmpegErrorMapper.Map, no-stream block)*. *(the category is asserted; the headline wording is not)*
- **I19** — A plain `No such file or directory` that did not match a binary signature classifies `InvalidArgument` with the headline `A file that was referenced could not be found.` *(FfmpegErrorMapper.Map, missing-file block)*. *(the category and the raw tail are asserted; the headline wording is not)*
- **I20** — `Option not found`, the pair form (`Option` and `not found`), `Unrecognized option`, `Invalid argument`, or `Error splitting the argument list` classifies `InvalidArgument` with the "invalid setting" headline *(FfmpegErrorMapper.Map, option block)*.
- **I21** — A tail matching no signature classifies `Unknown` and still carries the complete tail in `RawTail` *(FfmpegErrorMapper.Map, fallback)*.
- **I22** — The `Unknown` headline is one of two fixed sentences — `The operation failed (exit code <n>).` for a non-zero exit, `The operation reported an unexpected result.` for exit 0 — the exit code being the only interpolated value; **raw stderr is never the headline** *(FfmpegErrorMapper.Map, fallback)*. *(the never-raw-stderr rule is asserted; neither headline's wording is, and the exit-0 branch is unexercised — it is reachable via `BulkTrimEngine.ParseFfmpegExit` returning 0 when it cannot recover the code)*
- **I23** — No category interpolates stderr text into `Message`: every headline is a fixed English sentence chosen by category, and the only interpolation anywhere in the mapper is the exit code of I22 *(FfmpegErrorMapper.Map, all blocks)*.
- **I24** — The mapper never populates `LogFilePath` or `FullText` — both are null on every mapper result. Attaching them is the caller's job, done either by a record `with` expression on the mapped value (`BulkTrimEngine.MapSplitException`) or by constructing the error directly (`OperationViewModel.MapException`, `JoinViewModel`'s join `failureSelector`, and — on the crash path — `App.xaml.cs`, SPEC-015) *(FfmpegErrorMapper.Map — no branch passes those arguments)*. *(not asserted)*

### The copyable error — `UserFacingError`
Grounded in `UserFacingError` (the record header, `CopyText`, `DetailText`, `HasLogFile`).

- **I25** — `UserFacingError` is an immutable record of `Category`, `Message`, `RawTail` (required) plus `Hint`, `LogFilePath`, `FullText` (optional, defaulting to null); being a record, a consumer can attach a log path and full text to an already-classified error with a `with` expression instead of re-classifying *(UserFacingError record header)*.
- **I26** — `CopyText` is assembled in one fixed order: `Message`; then, when `Hint` is non-empty, a newline + the hint; then, when the detail is non-empty, a **blank line** + the detail; then, when `LogFilePath` is non-empty, a **blank line** + the log line *(UserFacingError.CopyText)*. *(the presence of all four parts is asserted; the exact separators and their order are not)*
- **I27** — The detail used by `CopyText` is `FullText` when non-empty, else `RawTail` — so when a caller has attached the run-level stderr (`OperationViewModel.MapException`, `BulkTrimEngine.MapSplitException`, `JoinViewModel`'s `failureSelector`) the clipboard carries **that** text rather than whatever tail the mapper classified from; `DetailText` (I30) applies the identical preference, so the detail box and the clipboard never disagree *(UserFacingError.CopyText, `detail`)*.
- **I28** — A part whose source is null or empty is omitted entirely: no padding blank lines, and no bare `Full log:` label when there is no path *(UserFacingError.CopyText, the three `IsNullOrEmpty` guards)*. *(not asserted)*
- **I29** — The log line is exactly the literal prefix `Full log: ` followed by the path, so the path survives copy-paste out of the blob as a usable path *(UserFacingError.CopyText)*. *(asserted only as containment of the path, not of the prefix)*
- **I30** — `DetailText` is `FullText` when non-empty, else `RawTail`, and contains **only** that output — never the headline or hint, which render above the box *(UserFacingError.DetailText)*.
- **I31** — `HasLogFile` is true if and only if `LogFilePath` is non-empty; it gates the **operation-level** "Open log" affordance on all three screens (`SplitView.xaml:669`, `JoinView.xaml:335`, `BulkCutView.xaml:1481`). The per-failed-row "Log" button in Bulk Cut (`BulkCutView.xaml:1426`) carries no visibility binding at all — it is always visible and relies on `ErrorActions.OpenLog`'s own empty-path guard instead (I50) *(UserFacingError.HasLogFile)*.

### The per-run ffmpeg log — `ErrorLogWriter`
Grounded in `ErrorLogWriter` (`DefaultLogDirectory`, `TryWrite`, `BuildLogBody`, `SanitizeOp`). The crash
path (`TryWriteCrash` / `BuildCrashBody`) is SPEC-015's; it shares only `SanitizeOp` and the directory.

- **I32** — The default log directory is `<LocalApplicationData>/VideoSplitJoiner/logs`, falling back to the OS temp folder when `LocalApplicationData` resolves empty; the folder name is the `AppFolderName` constant *(ErrorLogWriter.DefaultLogDirectory, ErrorLogWriter.AppFolderName)*. *(not asserted — every test injects an explicit temp directory, which is the point of the seam)*
- **I33** — `TryWrite` writes into the writer's own `LogDirectory` and returns the full path of the file it wrote *(ErrorLogWriter.TryWrite, ErrorLogWriter.LogDirectory)*.
- **I34** — The constructor rejects a null directory with `ArgumentNullException` *(ErrorLogWriter ctor)*. *(not asserted)*
- **I35** — `TryWrite` creates the log directory on demand; a first failure on a machine that has never logged still produces a file *(ErrorLogWriter.TryWrite, `Directory.CreateDirectory`)*.
- **I36** — The file name is `<sanitized-op>-<yyyyMMdd-HHmmss>.log`, the stamp being **UTC** in the invariant culture *(ErrorLogWriter.TryWrite)*. *(the `<op>-` prefix and `.log` suffix are asserted; the stamp format is not)*
- **I37** — `SanitizeOp` trims and lowercases the operation label and replaces every non-alphanumeric character with `-`; a null/whitespace label becomes the literal `op`, so a file name is always well-formed *(ErrorLogWriter.SanitizeOp)*. *(the lowercasing is asserted only through the crash path's `crash-dispatcher-` naming (SPEC-015); the ffmpeg path's own tests pass already-lowercase labels, and the blank-label fallback is unasserted)*
- **I38** — If a file of that name already exists (two failures inside the same second), the name is retried with a `-<guid:N>` suffix, so an earlier log is never clobbered *(ErrorLogWriter.TryWrite, the `File.Exists` collision branch)*. *(not asserted for this path — the crash twin of this branch is, by SPEC-015#I25)*
- **I39** — `BuildLogBody` renders a fixed header then the caller's stderr verbatim: `VideoSplitJoiner — <op> failed`, `Timestamp : <yyyy-MM-dd HH:mm:ss'Z'>` (UTC, invariant culture), `Exit code : <n>`, `Command   : <command>`, a blank line, `---- ffmpeg stderr (full) ----`, then `fullStdErr` unmodified — so a log always answers *what ran*, *when*, *how it exited*, and *what it printed* *(ErrorLogWriter.BuildLogBody)*. *(the `Exit code : ` and `Command   : ` lines, the `ffmpeg stderr (full)` banner and the verbatim body are asserted; the `VideoSplitJoiner — <op> failed` first line is not, and the timestamp is asserted only as the presence of the literal word `Timestamp`, never as the `yyyy-MM-dd HH:mm:ss'Z'` format)*
- **I40** — `BuildLogBody` is static and performs **no** file I/O, so the log format is unit-testable without touching the filesystem. It is *not* deterministic — the `Timestamp` line reads `DateTime.UtcNow` at render time (I39) — and its only caller is `TryWrite`; no UI surface renders it (the detail box shows `DetailText`, i.e. the raw stderr, per I30) *(ErrorLogWriter.BuildLogBody, `DateTime.UtcNow`)*.
- **I41** — A null `command` or null `fullStdErr` renders as an empty string — never the text `null`, never a throw *(ErrorLogWriter.BuildLogBody, `?? string.Empty`)*. *(not asserted)*
- **I42** — The file is written as UTF-8 **without** a BOM, so a non-ASCII path in the command or stderr round-trips into the log readably (the G-010 mojibake case) *(ErrorLogWriter.TryWrite, `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`)*. *(not asserted)*
- **I43** — `TryWrite` returns the written path on success and `null` on **any** failure — an unwritable directory, a locked file, a security exception are all caught and swallowed; it never throws *(ErrorLogWriter.TryWrite, the catch-all)*.
- **I44** — A `null` return is a normal outcome, not an error condition: the caller continues to surface the failure it was already reporting, just with no `LogFilePath` (hence no "Open log" affordance, per I31) *(SplitEngine.ThrowIfFailed and JoinEngine pass the possibly-null path straight into their carriers)*. *(not asserted — the tests exercise the successful-write branch)*
- **I45** — Exactly one log is written per failed ffmpeg run **on the two logged paths** — `split` *(SplitEngine.ThrowIfFailed)* and `join` *(JoinEngine.JoinAsync)* — before the failure is surfaced; the command recorded is the reconstructed `"ffmpeg " + <token list>` *(both engines)*. A third label, `preview-open-refused`, comes from the media-address refusal *(FfmeMediaPlayer.TryLogRefusal — SPEC-013's path, listed here because it is part of the log-name vocabulary)*; those three are the only `TryWrite` call sites in the tree. The frame-exact `SmartCutEngine` path (SPEC-001 I40–I48) throws a bare `SplitException` on each of its three ffmpeg runs, and the best-effort thumbnail/waveform services return `null`, all **without** writing a log — a failed ffmpeg run there produces no `LogFilePath` and no classification *(SmartCutEngine, FfmpegThumbnailService, FfmpegWaveformService — no `TryWrite` call site)*. *(the write itself is asserted end-to-end on both engine paths; "exactly one", the `split-`/`join-` file labels as produced by the engines, and `preview-open-refused` are not)*

### The OS affordances — `ErrorActions` (App)
Grounded in `src/App/Views/ErrorActions.cs`. **No test constructs `ErrorActions`** — it is WPF
code-behind glue over `Clipboard` and `Process.Start`, deliberately kept behavior-free so the testable
text lives on `UserFacingError` (I26–I31). Every invariant in this section is therefore *(not asserted)*.

- **I46 (not asserted)** — `CopyError` places exactly `error.CopyText` on the clipboard; no caller re-composes a `UserFacingError`'s text (`SplitView.xaml.cs:82`, `JoinView.xaml.cs:39`, `BulkCutView.xaml.cs:527`/`:557`, `App.xaml.cs:118`). The preview-failure banner is the one non-`UserFacingError` copy path and goes through `TryCopy` directly with a plain string (`PlayerView.xaml.cs:46` — SPEC-013) *(ErrorActions.CopyError → ErrorActions.TryCopy)*.
- **I47 (not asserted)** — `CopyError(null)` is a no-op and leaves the clipboard untouched *(ErrorActions.CopyError, null guard)*.
- **I48 (not asserted)** — `TryCopy` ignores null/empty text, so a copy of an empty error never **clears** the user's clipboard *(ErrorActions.TryCopy, `IsNullOrEmpty` guard)*.
- **I49 (not asserted)** — A clipboard failure (another app holds it, no desktop session) is swallowed — `TryCopy` never throws *(ErrorActions.TryCopy, catch-all)*.
- **I50 (not asserted)** — `OpenLog` is a no-op when the error is null or its `LogFilePath` is null/empty *(ErrorActions.OpenLog, null/empty guard)*.
- **I51 (not asserted)** — When the log file exists, `OpenLog` starts `explorer.exe /select,"<path>"` with `UseShellExecute = true` — the file is **revealed selected** in Explorer, never opened in an editor *(ErrorActions.OpenLog, file branch)*.
- **I52 (not asserted)** — When the file is gone but its containing directory exists, `OpenLog` opens that directory instead; when neither exists it does nothing *(ErrorActions.OpenLog, directory fallback)*.
- **I53 (not asserted)** — A shell failure is swallowed — `OpenLog` never throws, so a broken Explorer association cannot crash the app from an error dialog *(ErrorActions.OpenLog, catch-all)*.

## Links
- Design: ADR-0002 (per-subsystem error contract — § 5 "logging + thumbnails → best-effort null" is the rule I43/I44/I49/I53 implement)
- Goals: G-010 (fix the `.ts` split failure + copyable error log — T-035 classify on cause not on the last line, T-036 UTF-8 so a non-ASCII path is readable, T-037 the copyable surface + per-run log)
- Related specs: SPEC-017 (the process layer that produces the `StdErrTail` and `ExitCode` this spec classifies) · SPEC-015 (the crash-log path of the same `ErrorLogWriter`, and the shell that hosts the error surface) · SPEC-008 (`OperationViewModel` — `MapException`/`HeadlineOf` and the `Failed` state that holds the `Error`) · SPEC-001 (`SplitException` carries `LogFilePath` + `FullStdErr` out of the split engine) · SPEC-003 (`JoinResult.RefusedWithLog` does the same for join) · SPEC-002 (`BulkTrimEngine.MapSplitException` re-maps a `SplitException` through this mapper) · SPEC-013 (the preview player's `preview-open-refused` log write)
- Key code: `src/Core/Errors/ErrorCategory.cs` · `src/Core/Errors/FfmpegErrorMapper.cs` · `src/Core/Errors/UserFacingError.cs` · `src/Core/Errors/ErrorLogWriter.cs` · `src/App/Views/ErrorActions.cs`
- Tests: `tests/Core.Tests/FfmpegErrorMapperTests.cs` (I1, I4, I7, I9, I11-I15, I17-I23) · `tests/Core.Tests/UserFacingErrorTests.cs` (I26, I27, I30, I31) · `tests/Core.Tests/ErrorLogWriterTests.cs` (I33, I35, I36, I39, I40, I43) · `tests/Core.Tests/SplitEngineUnitTests.cs` + `tests/Core.Tests/JoinEngineUnitTests.cs` (I39, I43, I45 end-to-end — a failed run writes a log whose content carries the full stderr + exit code) · `tests/App.Tests/SplitViewModelTests.cs` + `tests/App.Tests/JoinViewModelTests.cs` (I26, I27, I30, I31 at the view-model surface)
- Gaps (documented, no test): I2, I3, I5, I6, I8, I10, I16, I24, I25, I28, I29, I32, I34, I37, I38, I41, I42, I44, and all of I46-I53 (`ErrorActions` has no tests at all). Every invariant lands in exactly one of these two lists; the entries in the Tests list that are only *partly* backed carry an inline parenthetical saying which half is asserted (I6's block order, I25's record shape and I29's `Full log: ` prefix are unasserted outright, hence their place here)
- Traceability gap: none of the cited test files carries the `serves-spec` marker that `docs/standards/feature-spec-structure.md` § Test traceability requires (`FfmpegErrorMapperTests.cs` 0, `UserFacingErrorTests.cs` 0, and `ErrorLogWriterTests.cs`'s single marker is the SPEC-015 crash case). The link above is one-directional until a `[Trait("serves-spec", "SPEC-016")]` or `// serves-spec: SPEC-016#I<k>` is added test-side, so coverage tooling reads this spec as 0%
