# Task Board History

<!-- latest on top; entries are never deleted -->

## 2026-07-15 — ✅ G-001 DONE — todo-next-flow drain converged (11/11)

- **T-011 ✅** perfect-docs — README · docs/USER_GUIDE · docs/ARCHITECTURE · CHANGELOG (0.1.0) · CLAUDE, all from real code. Commit `4fd4f87`.
- **G-001 sealed `status: done`** — all 11 tasks done. Drain reached convergence (legitimate stop: nothing left proceedable).
- Full chain (commits): T-001 `1cd4f68` → T-002 `e364def` → T-003 `6740e19` → T-004 `b4701a6` → T-006 `3dd7eb8` → T-005 `c47308a` → T-009 `f715565` → T-007 `09211d2` → T-008 `192d1b8` → T-010 `2047424` → T-011 `4fd4f87`.
- Tests: 162 passing (127 Core + 35 App). App builds 0-warning, self-contained zip produced (72 MB exe / 126 MB zip).
- ⚠ Not verified in this headless env: live WPF GUI render/interaction + real end-to-end run on a clean machine → the one open follow-up (packaged smoke on the user's machine).
- ⚠ Decision before public release: bundled ffmpeg is GPL essentials — swap to LGPL via `package.ps1 -FfmpegSource` (no code change).

## 2026-07-15 — T-010 done → T-011 started (terminal node) (todo-next-flow drain)

- **T-010 ✅** packaging — single-file self-contained win-x64 publish (props gated on `PublishSingleFile`, no-trim for WPF) + `packaging/package.ps1` bundling ffmpeg into app-local `ffmpeg/` + versioned zip. Ran: 72 MB exe → 126 MB zip. `THIRD-PARTY-NOTICES.md` surfaces GPL-essentials vs LGPL-swap decision. 162/162, no regressions. Commit `2047424` (dist/ + binaries gitignored, not committed).
- All implementation leaves ✅. **T-011 🔵** perfect-docs (terminal) dispatched — README · user guide · ARCHITECTURE · CHANGELOG · CLAUDE.

## 2026-07-15 — T-008 done → T-010 started (todo-next-flow drain)

- **T-008 ✅** join UI — JoinView + JoinViewModel (ordered list, live green/red compat banner naming mismatches, reorder, run via OperationViewModel) + composition-root wiring. 162/162 (12 new VM tests). Commit `192d1b8`. Live GUI NOT verified (headless).
- Both feature screens complete. **T-010 🔵** packaging (self-contained single-file publish + bundled ffmpeg + versioned zip) dispatched — last build node.

## 2026-07-15 — T-007 done → T-008 started (todo-next-flow drain)

- **T-007 ✅** split UI — SplitView + SplitViewModel (load, markers w/ `MM:SS.f → MM:SS.f (±N.Ns)` snap display, coarse-GOP banner, ranked auto-detect, run via OperationViewModel) + composition root wiring the real Core graph. 150/150 (15 new VM tests). Commit `09211d2`. Live GUI render NOT verified (headless) — deferred to packaged smoke.
- **T-008 🔵** join screen UI (JoinView + JoinViewModel: ordered list, live compat banner, run) dispatched — last engine-consuming screen.

## 2026-07-15 — T-009 done → T-007 started (todo-next-flow drain)

- **T-009 ✅** UX layer — `FfmpegErrorMapper` (9 categories, signature-scan, RawTail preserved) + `OperationViewModel` (Idle/Running/Completed/Failed/Cancelled, progress/cancel, `RunWithResultAsync`). New `tests/App.Tests` project. 135/135 total. Commit `f715565`.
- **T-007 🔵** split screen UI (SplitView + SplitViewModel over the engines + OperationViewModel) dispatched. UI = headless env → build + VM unit tests only; live-render verification deferred to packaged smoke.

## 2026-07-15 — T-005 done → T-009 started · Core engine layer complete (todo-next-flow drain)

- **T-005 ✅** join engine — concat-demuxer stream-copy + pure `CompatChecker` pre-flight (codec/res/pix_fmt/timebase/audio), refuse-with-reason + no-file-on-incompatible. 112/112 tests; verified (7s join order-preserved; 640x480 refused naming clip 2). Commit `c47308a`.
- **Core engine layer COMPLETE**: probe (T-003) · split (T-004) · join (T-005) · detect (T-006). Remaining = UI + cross-cutting + packaging + docs.
- Ordering choice: **T-009 before the UIs** — its FfmpegErrorMapper + shared progress/cancel helper are consumed by T-007/T-008 (reuse-before-build); DAG permits (T-009 ready now, blocked-by T-004/T-005/T-006 all ✅).
- **T-009 🔵** progress / cancel / friendly-error layer dispatched.

## 2026-07-15 — T-006 done → T-005 started (todo-next-flow drain)

- **T-006 ✅** auto-detect — black (`blackdetect`) + white (`negate,blackdetect`) + scene (`select gt(scene),metadata=print`), 3 decode-only passes, merge/dedupe/rank + keyframe-snap. 88/88 tests; verified on synthetic fixture (Black@2.0, White@5.5, Scene@boundaries, ranked). Commit `3dd7eb8`. (Also raised T-002 RollingTail 40→100k for detection stderr — backward-compat.)
- T-007 split-UI now unblocked (T-001,T-004,T-006 all ✅).
- **T-005 🔵** join engine (stream-copy concat + compat pre-flight) dispatched.

## 2026-07-15 — T-004 done → T-006 started (todo-next-flow drain)

- **T-004 ✅** split engine — stream-copy keyframe-snap; `-c copy` invariant enforced structurally + runtime guard + asserted; segment-muxer single pass + temp-then-move. 65/65 tests; real split verified (3 segs, 10.000s sum, snap δ−0.4s). Commit `b4701a6`.
- **T-006 🔵** auto-detect (black + white + scene, ranked, decode-only) dispatched — next critical-path node (unblocks T-007 split-UI). T-005 join engine still queued.

## 2026-07-15 — T-003 done → T-004 started (todo-next-flow drain)

- **T-003 ✅** media probe — MediaInfo/StreamInfo, ProbeResult union (typed failure, no raw throw), keyframe index (cached), SnapToNearestKeyframe (ties→earlier, clamps), AverageGop. 37/37 tests; verified live vs synthetic 1s-GOP fixture. Commit `6740e19`.
- DAG fanned out at T-003: T-004/T-005/T-006 unblocked. Draining critical-path node first.
- **T-004 🔵** split engine (stream-copy, keyframe-snap) dispatched.

## 2026-07-15 — T-002 done → T-003 started (todo-next-flow drain)

- **T-002 ✅** ffmpeg/ffprobe wrapper — runner (kill-tree cancel, never-throw-on-nonzero), locator (override→app-local→PATH), typed args (ArgumentList), progress parser. 22/22 tests (6 real-binary integration). Commit `e364def`.
- **T-003 🔵** media probe (duration/streams/codecs/keyframe index + snap) dispatched.

## 2026-07-15 — T-001 done → T-002 started (todo-next-flow drain)

- **T-001 ✅** WPF scaffold — `App` + `Core` + `Core.Tests`, build clean (0 warnings), 2/2 tests pass, commit `1cd4f68`. Core-is-UI-free guard test in place.
- Toolchain resolved: dotnet SDK `D:\_env_storeage\dotnet\dotnet.exe` (8.0.422); ffmpeg/ffprobe `D:\_env_storeage\ffmpeg-7.1.1-essentials_build\bin\` (7.1.1).
- **T-002 🔵** ffmpeg/ffprobe wrapper dispatched to a build worker.

## 2026-07-15 — todo-goal: created G-001 + 11 tasks

- **Goal G-001** "Ship v1.0 — fast WPF video splitter/joiner (stream-copy, no re-render) with auto-detect split points" created (`status: planning`, `proceed-with: perfect-e2e --auto-approve`).
- Decomposed into **T-001…T-011** with `blocked-by` edges; all `spec: reviewed` (T-011 is the fixed terminal `perfect-docs` node).
- Locked decisions: **D1** stack = .NET/C# WPF · **D2** cut = keyframe-snap zero re-encode (`-c copy`) · **D3** auto-detect = black + white + scene cuts (ranked, decode-only). All user-confirmed at plan time.
- Greenfield: target was an empty git repo — entire scope is gap.
- Proceedable now: **T-001** (sole root).
