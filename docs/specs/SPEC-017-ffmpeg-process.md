---
id: SPEC-017
slug: ffmpeg-process
area: core
title: ffmpeg / ffprobe process layer
status: current
sources:
  - src/Core/Ffmpeg/FfmpegArgs.cs
  - src/Core/Ffmpeg/FfmpegBinaryLocator.cs
  - src/Core/Ffmpeg/FfmpegRunner.cs
  - src/Core/Ffmpeg/FfprobeRunner.cs
  - src/Core/Ffmpeg/FfmpegProgress.cs
  - src/Core/Ffmpeg/RollingTail.cs
  - src/Core/Ffmpeg/FfmpegResult.cs
  - src/Core/Ffmpeg/FfmpegExceptions.cs
serves-goal: [G-001, G-010]
updated: 2026-09-12
---

## What
The single choke-point every engine shells out through (T-002). Six small types: `FfmpegArgs` builds a
**typed token list** (never a concatenated command line); `FfmpegBinaryLocator` (`IFfmpegBinaryLocator`)
resolves `ffmpeg`/`ffprobe` by explicit override → app-local `ffmpeg/` folder → `PATH`, or throws
`FfmpegNotFoundException`; `FfmpegRunner` (`IFfmpegRunner`) launches ffmpeg windowless with all three std
streams redirected and UTF-8-decoded, closes stdin, drains stdout, streams stderr line-by-line into a
bounded tail **and** a progress parser, and returns a `FfmpegResult(ExitCode, StdErrTail)` for **any** exit
code — killing the whole process tree and rethrowing on cancellation; `FfprobeRunner` (`IFfprobeRunner`)
runs the same process discipline but returns stdout (the JSON payload) and **throws** `FfprobeException`
on a non-zero exit; `FfmpegProgress` turns ffmpeg's `time=` stderr markers into a clamped, monotonic 0..1
fraction; `RollingTail` is the fixed-capacity ring buffer that bounds the retained stderr.

## Why
Everything the app does that is not UI is an ffmpeg or ffprobe child process — split, smart-cut, join,
probe, keyframe scan, thumbnails, waveform. Concentrating process handling in one layer is what makes the
rest of Core boring: paths with spaces and non-ASCII characters reach the child intact because arguments
are passed as discrete `ArgumentList` tokens with no shell re-parse; stderr is decoded as UTF-8 so a
Japanese path surfaces as itself instead of mojibake (T-036 / G-010); a cancel kills the process tree
promptly instead of leaving a 30-minute encode running; and memory is bounded no matter how much a
detection pass prints. The ffmpeg/ffprobe **asymmetry** is deliberate and is the whole point of the result
types: ffmpeg's non-zero exit is ordinary (bad input, unsupported codec) so it is a `FfmpegResult`, while
ffprobe is a query whose failure returned no data at all, so it is an exception (ADR-0002).

## Scope
**In:** argument tokenisation (`FfmpegArgs`), binary resolution + the not-found message
(`FfmpegBinaryLocator`, `FfmpegNotFoundException`), the two runners' process discipline (stream
redirection, UTF-8 decoding, stdin close, stdout drain, exit handling, cancellation + tree kill), the
result/exception contracts (`FfmpegResult`, `FfprobeException`), stderr-tail bounding (`RollingTail`), and
`time=`-marker progress parsing (`FfmpegProgress`).
**Out:** what any caller *asks* ffmpeg to do — the split/smart-cut argument shapes (SPEC-001), join/concat
(SPEC-003), bulk trim (SPEC-002), the probe's JSON deserialisation and keyframe logic on top of
`IFfprobeRunner` (SPEC-004), thumbnails (SPEC-005), waveform PCM extraction (SPEC-006); turning a stderr
tail into a user-facing message (`src/Core/Errors/FfmpegErrorMapper.cs` — ADR-0002, its own tests);
`OperationStatus` (it lives in this folder but is the engines' stage channel, SPEC-008); how the binaries
get onto disk (packaging / ADR-0010); and the in-process FFME preview, which P/Invokes the shared
libraries and never uses this layer (ADR-0010, SPEC-013).

## Current behavior & invariants

### Argument building (`FfmpegArgs`)
- **I1** — `FfmpegArgs.ForFfmpeg()` seeds every ffmpeg command with `-hide_banner` and `-nostdin`, so no build banner is printed and ffmpeg never blocks reading stdin (`ForFfmpeg`).
- **I2** — `FfmpegArgs.ForFfprobe()` seeds `-hide_banner` **only** — ffprobe has no `-nostdin` flag (`ForFfprobe`). *(the `-hide_banner` seed is asserted; the **only** — that `-nostdin` is absent — is not, and that absence is the entire difference from I1)*
- **I3** — `Input(path)` appends exactly two tokens, `-i` then the path, with the path as **one** token: spaces and non-ASCII characters survive unsplit (`FfmpegArgs.Input`).
- **I4** — `Output(path)` appends the path as a single trailing positional token (`FfmpegArgs.Output`).
- **I5** — `Raw(params string[])` appends each supplied argument as its own token — nothing is split on whitespace, joined, or quoted (`FfmpegArgs.Raw`).
- **I6** — Token order is insertion order: the seed flags first, then each fluent call in the order it was made (`_args` is append-only; `ForFfmpeg().Input(i).Raw("-c","copy").Output(o)` → `-hide_banner -nostdin -i <i> -c copy <o>`).
- **I7** — The tokens reach the child through `ProcessStartInfo.ArgumentList`, one element per token — never a concatenated command line — so there is no shell re-parse and no manual quoting anywhere in the layer (`FfmpegRunner.RunAsync` / `FfprobeRunner.RunJsonAsync`, the `foreach (var a in args.ToList())` loops).
- **I8** — *(not asserted)* `ToList()` returns `_args.AsReadOnly()` — a read-only **view** over the builder's live list, not a copy: the caller cannot mutate it, but tokens appended to the builder after the call are visible through a previously returned list (`FfmpegArgs.ToList`).

### Binary resolution (`FfmpegBinaryLocator`, `FfmpegNotFoundException`)
- **I9** — Resolution order per tool is (a) explicit constructor override, (b) app-local `<AppContext.BaseDirectory>/ffmpeg/<exe>`, (c) the bare tool name when it is discoverable on `PATH`; the first hit wins and later sources are not consulted (`FfmpegBinaryLocator.Resolve`; ADR-0010). *(only the override-wins branch is exercised; the app-local → `PATH` ordering is not asserted — no test has both present)*
- **I10** — An override path that **exists** is returned verbatim; the locator does not check that the file is really ffmpeg (`Resolve`, the `File.Exists(overridePath)` branch).
- **I11** — An override path that does **not** exist throws `FfmpegNotFoundException` whose message contains *"does not exist"* — it never falls through to the app-local or `PATH` sources (`Resolve`, the override branch's throw).
- **I12** — A null, empty, or whitespace override is normalised to *no override* at construction, so the locator auto-discovers (`FfmpegBinaryLocator` ctor). *(the whitespace case is not asserted)*
- **I13** — *(not asserted — the suite runs on Windows only)* The probed file name is OS-dependent: `<tool>.exe` on Windows, bare `<tool>` elsewhere, and it is used for both the app-local probe and the `PATH` probe (`Resolve`, `OperatingSystem.IsWindows()`).
- **I14** — *(not asserted)* The app-local candidate is exactly `Path.Combine(AppContext.BaseDirectory, "ffmpeg", exeName)` — the layout `package.ps1` produces (`Resolve`; ADR-0010).
- **I15** — When resolution falls through to `PATH`, the locator returns the **bare tool name** (`"ffmpeg"` / `"ffprobe"`), not the absolute path it found — the OS re-resolves it at process start (`Resolve`, `return toolName`). *(asserted only on the branch the machine happens to be in — the one test is a try/catch that asserts this when ffmpeg IS on `PATH` and I17 when it is not, never both on the same run)*
- **I16** — *(not asserted)* The `PATH` probe splits `PATH` on `Path.PathSeparator` discarding empty entries, trims each directory, and accepts a candidate only when `File.Exists` says the file is there; an unset or empty `PATH` yields no hit (`FindOnPath`).
- **I17** — When nothing resolves, the thrown `FfmpegNotFoundException` names the tool (*"Could not locate '\<tool\>'"*) and lists all three tried sources — explicit override, the app-local folder **with its concrete path**, and `PATH` — plus the three remediations (`Resolve`, the final throw). *(only the `"Could not locate '<tool>'"` clause is asserted — neither the source list nor the remediations is; and even that fires only on the branch the machine happens to be in, the same try/catch that asserts I15 on the other branch)*
- **I18** — *(not asserted)* Resolution happens **per run**, inside `RunAsync`/`RunJsonAsync`, not at construction — so a missing binary surfaces as `FfmpegNotFoundException` out of the call that needed it, and a binary installed mid-session is picked up without rebuilding the runner (`FfmpegRunner.RunAsync` line 52, `FfprobeRunner.RunJsonAsync` line 38).

### ffmpeg execution (`FfmpegRunner`, `FfmpegResult`)
- **I19** — *(not asserted)* The constructor rejects a null locator and `RunAsync` rejects a null `args` with `ArgumentNullException` (`FfmpegRunner` ctor, `ArgumentNullException.ThrowIfNull(args)`).
- **I20** — *(not asserted)* The child is launched with `UseShellExecute = false` and `CreateNoWindow = true`, with stdout, stderr **and** stdin all redirected — no console window flashes and no shell is involved (`RunAsync`, the `ProcessStartInfo` initialiser).
- **I21** — *(not asserted)* stdin is closed immediately after `Start()`, best-effort (an already-closed stream is swallowed), so ffmpeg can never block reading it — belt-and-braces with the `-nostdin` flag of I1 (`RunAsync`, the `process.StandardInput.Close()` try/catch).
- **I22** — stderr is read line-by-line on a background task, and **every** line is appended to the rolling tail *and* fed to the progress parser (`RunAsync`, the `stdErrTask` loop). *(not asserted — the tail is only ever observed non-empty (I27); neither the "every line" half nor the parser feed is exercised, as I33 also concedes)*
- **I23** — *(not asserted — no test observes the drain or its discard)* stdout is drained concurrently with `ReadToEndAsync` so a full pipe can never deadlock the child, and the drained text is **discarded** — `FfmpegResult` carries no stdout, which is why binary payloads must be written to a temp file rather than piped (`RunAsync`, `stdOutTask`; see SPEC-006).
- **I24** — Both stdout and stderr are decoded as **UTF-8** regardless of the console codepage, so a non-ASCII path echoed by ffmpeg appears intact in the captured tail — no cp1252 mojibake and no `U+FFFD` replacement characters (`RunAsync`, `StandardErrorEncoding`/`StandardOutputEncoding = Encoding.UTF8`; T-036).
- **I25** — **Any** exit code returns a `FfmpegResult` — a non-zero exit is a normal result, never an exception (`RunAsync`'s single `return new FfmpegResult(process.ExitCode, tail.Snapshot())`; ADR-0002).
- **I26** — `FfmpegResult.Success` is `ExitCode == 0`, and `StdErrText` is the tail joined with `Environment.NewLine` (`FfmpegResult`). *(the `Success` half is asserted; the `Environment.NewLine` join is not — `StdErrText` is read by one test, which only does `Contain`/`NotContain` on it)*
- **I27** — A run that fails carries diagnostics: its `StdErrTail` is non-empty (`RunAsync` feeds every stderr line into the tail before the result is built).
- **I28** — *(not asserted — no test observes the await-before-return ordering)* On the normal path both reader tasks are awaited **before** the result is constructed, so the returned tail is complete rather than whatever had arrived when the process exited (`RunAsync`, the two awaits preceding the `return`).
- **I29** — Cancellation is observed on `WaitForExitAsync(ct)`; the runner then kills the **entire process tree** (`Kill(entireProcessTree: true)`) and rethrows — a cancelled run never returns a `FfmpegResult` (`RunAsync`'s `catch (OperationCanceledException)` → `KillTree`).
- **I30** — Cancellation unwinds promptly rather than waiting out the job: a ~30 s wall-clock ffmpeg run cancelled after 500 ms throws within seconds, not after the full duration (`KillTree` on the cancel path).
- **I31** — *(not asserted)* `KillTree` is best-effort — it skips a process that has already exited and swallows the access/exit race — so cancellation surfaces as `OperationCanceledException` and nothing else (`FfmpegRunner.KillTree`).
- **I32** — *(not asserted)* On the cancellation path the stderr/stdout reader tasks are **not** awaited and the partial tail is discarded; a cancelled run yields no diagnostics through this layer (`RunAsync` — `KillTree` then `throw`, skipping the awaits of I28).
- **I33** — *(the wiring is not asserted end-to-end; the parser itself is — see I42-I50)* Progress is reported only when the parser reports an advance, and it is reported synchronously on the thread-pool thread that drains stderr — nothing is marshalled to a UI thread, and a null `progress` is simply not called (`RunAsync`, `if (fraction is { } f) progress?.Report(f)` inside the `Task.Run` loop).
- **I34** — *(not asserted)* The `Process` is disposed on every path, cancelled or not (`using var process` in `RunAsync`).

### ffprobe execution (`FfprobeRunner`, `FfprobeException`)
- **I35** — *(not asserted)* The constructor rejects a null locator and `RunJsonAsync` rejects a null `args` with `ArgumentNullException` (`FfprobeRunner` ctor, `ArgumentNullException.ThrowIfNull(args)`).
- **I36** — *(not asserted)* ffprobe runs under the same process discipline as ffmpeg — `UseShellExecute = false`, `CreateNoWindow = true`, all three streams redirected, stdin closed best-effort, both streams decoded as UTF-8 — so JSON and diagnostics carrying non-ASCII paths survive intact (`RunJsonAsync`, the `ProcessStartInfo` initialiser + stdin close; T-036).
- **I37** — On exit code 0, `RunJsonAsync` returns ffprobe's **stdout verbatim** as a string; it does no parsing and no validation of the payload (`RunJsonAsync`'s `return stdout`).
- **I38** — A non-zero exit throws `FfprobeException` — the deliberate opposite of the ffmpeg contract in I25, because a failed probe returned no data for the caller to work with (`RunJsonAsync`'s `if (process.ExitCode != 0)`; ADR-0002).
- **I39** — `FfprobeException` carries the `ExitCode` and the `StdErrTail`, and its message is `"ffprobe exited with code <n>."` followed by the newline-joined tail (`FfprobeException` ctor). *(the exit code and message text are not asserted; the non-empty tail is)*
- **I40** — *(not asserted)* Both reader tasks are awaited **before** the exit-code check, so the tail carried by the exception is complete (`RunJsonAsync` — `await stdErrTask` and `await stdOutTask` precede the throw).
- **I41** — *(not asserted — only the ffmpeg cancellation path has a test)* Cancellation behaves as it does for ffmpeg: observed on `WaitForExitAsync(ct)`, the whole process tree is killed and the `OperationCanceledException` rethrown (`RunJsonAsync`'s catch → `FfprobeRunner.KillTree`).

### Progress parsing (`FfmpegProgress`)
- **I42** — `Feed(line)` extracts a `time=<h>:<mm>:<ss>[.frac]` marker from an ffmpeg status line and returns `elapsed / total` — e.g. `time=00:00:05.000` against a 10 s total is `0.5` (`FfmpegProgress.TimeRegex` + `Feed`).
- **I43** — The fraction is clamped to `1.0` when elapsed exceeds the total (`Feed`'s upper clamp).
- **I44** — Progress is **monotonic**: a line whose fraction is less than or equal to the last reported value returns `null` and leaves `Current` unchanged, so a momentary rewind in ffmpeg's output cannot make a progress bar go backwards (`Feed`'s `if (fraction <= _last) return null;`).
- **I45** — Successive advancing lines return strictly increasing fractions, and `Current` is always the most recently returned value (`Feed`, `_last`/`Current`).
- **I46** — An unknown or non-positive total makes progress un-computable: `Feed` always returns `null` and `Current` stays `0.0` — a fraction is never fabricated (`FfmpegProgress` ctor `total is { Ticks: > 0 }`, `Feed`'s null-total guard). *(the null case is asserted; `TimeSpan.Zero`/negative is not)*
- **I47** — A line with no `time=` marker returns `null` and does not disturb `Current`; a null or empty line does the same (`Feed`'s empty guard + `!m.Success`). *(the no-marker case is asserted; null/empty is not)*
- **I48** — *(not asserted)* The hour/minute/second fields are parsed with `CultureInfo.InvariantCulture`, so a comma-decimal machine locale cannot corrupt the fractional seconds (`Feed`'s three `Parse` calls).
- **I49** — Minute and second fields are **not** range-checked: elapsed is simply `h*3600 + m*60 + s`, so `time=00:00:80.000` is read as 80 seconds (0.80 of a 100 s total), not rejected (`Feed`'s elapsed computation).
- **I50** — *(not asserted)* A `time=` marker **without** an hour field (e.g. `time=01:23.4`) does not match and reports no progress — the regex requires two colons. The code comment above `TimeRegex` (`FfmpegProgress.cs:13`) claiming that shape is matched is wrong; real ffmpeg always emits `HH:MM:SS.ms`, so nothing depends on it (`FfmpegProgress.TimeRegex`).

### Bounded stderr retention (`RollingTail`)
- **I51** — *(not asserted — `RollingTail` has no test of its own; it is exercised only indirectly through the two runners' tails)* The buffer holds at most `capacity` lines and evicts the **oldest** first, so what is retained is always the tail (`RollingTail.Add`, the dequeue-at-capacity branch).
- **I52** — *(not asserted)* `Snapshot()` returns an immutable copy (`_lines.ToArray()`), oldest first, so a snapshot handed to a caller is unaffected by later `Add` calls (`RollingTail.Snapshot`).
- **I53** — *(not asserted; no call site passes a value below 1)* A capacity below 1 is clamped to 1 — the buffer is never zero-capacity and the constructor never throws (`RollingTail` ctor).
- **I54** — *(not asserted)* The caps differ by purpose: ffmpeg retains 100 000 lines (`FfmpegRunner.TailSize`) so a detection pass's full stderr survives, ffprobe only 40 (`FfprobeRunner.TailSize`) because a probe failure needs just the error. Both are bounded, so no stream can grow memory without limit.

## Links
- Design: ADR-0002 (per-subsystem error contract — the ffmpeg-result / ffprobe-exception asymmetry) · ADR-0010 (shared ffmpeg build; the app-local `ffmpeg/` folder the locator probes) · ADR-0003 (cancel safety — the engine-side temp-then-move that pairs with the tree kill here)
- Goals: G-001 (ship v1.0 — T-002 built this layer as the choke-point every engine sits on) · G-010 (non-ASCII paths end-to-end, no mojibake — T-036 added the UTF-8 stream decoding)
- Related specs: SPEC-001 (stream-copy split — the biggest consumer of `IFfmpegRunner`) · SPEC-003 (join/concat) · SPEC-004 (media probe — built on `IFfprobeRunner`, converts `FfprobeException` into a typed `ProbeResult`) · SPEC-005 (thumbnail service) · SPEC-006 (waveform service — writes PCM to a temp file because of I23) · SPEC-008 (operation progress/status — consumes the fractions produced by I42-I45)
- Key code: `src/Core/Ffmpeg/FfmpegArgs.cs` · `FfmpegBinaryLocator.cs` · `FfmpegRunner.cs` · `FfprobeRunner.cs` · `FfmpegProgress.cs` · `RollingTail.cs` · `FfmpegResult.cs` · `FfmpegExceptions.cs`
- Tests: `tests/Core.Tests/FfmpegArgsTests.cs` (I1-I6) · `FfmpegBinaryLocatorTests.cs` (I10, I11, I15, I17) · `FfmpegRunnerIntegrationTests.cs` (I25, I26, I27, I29, I30) · `FfprobeRunnerIntegrationTests.cs` (I37, I38, I39) · `UnicodePathIntegrationTests.cs` (I7, I24, I27) · `FfmpegProgressTests.cs` (I42-I47, I49) · `FfmpegTestBinaries.cs` (how the integration tests locate the real binaries; `VSJ_REQUIRE_FFMPEG=1` turns a missing binary into a failure instead of a skip)
