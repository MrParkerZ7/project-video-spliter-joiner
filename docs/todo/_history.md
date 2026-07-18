# Task Board History

<!-- latest on top; entries are never deleted -->

## 2026-07-18 — todo-goal PLANNED: G-019 match UX/UI to the sample (design EXTRACTED)

- Decoded the sample bundle (`docs/design/references/2026-07-18_redesign-standalone.html`) via a 🚫 python extractor — got the REAL design (not just palette): **font IBM Plex Mono + Plex Sans** (G-017 used Segoe UI = wrong), full dark surface scale `#0a0b0d→#232935`, text `#eef0f3`/`#9aa0ab`/`#767c88`, gold `#e0a83a`+`#f0bc55`, semantic green/blue/purple, **tight radii 6–12px** (not 24), dense 8–18px type; LAYOUT: header + "lossless · no re-encode" tagline + format badge · file-info cards ("matroska · 10:00 · 1.4 GB") · "Cut markers" · "Parts to export (All/None)" · DIR/NAME/FILE mono labels · Join "Estimated result" panel.
- **G-019** "Match UX/UI to sample" — T-058 refine tokens (bundle IBM Plex fonts + full palette + tight radii + type) · T-059 restructure screens to the sample layout (header/badge/cards/panels/mono labels + UX/UI review) · T-060 docs. NOTE D1: confirm exact spacing from a browser RENDER/screenshot before finalizing layout. Plan-only; `proceed G-019`.

## 2026-07-18 — ✅ G-018 DONE — themed window frame

- **T-056 ✅** `ae6d09a` custom WindowChrome dark title bar (title + gold accent + min/max/close, close=red; WM_GETMINMAXINFO taskbar clamp + maximized content margin). 379 tests.
- **T-057 ✅** CHANGELOG + CLAUDE note. **G-018 sealed done.** Window frame now dark+gold. Live behavior/look = user-verified.

## 2026-07-18 — todo-goal-next: G-018 themed window frame (plan+build)

- **G-018** "Themed window frame" — custom `WindowChrome` dark+gold title bar (caption + min/max/close buttons + correct resize/maximize/drag) replacing the clashing light native chrome. Tasks: T-056 frame · T-057 docs. todo-goal-next → building immediately.
- **T-056 🔵** custom themed WindowChrome title bar dispatched. (Fallback noted: DWM dark native title bar if custom chrome regresses window behavior.)

## 2026-07-18 — ✅ G-017 DONE — dark+gold theme (todo-next-all, 3/3)

- **T-054 ✅** `5e9cdb8` all screens restyled to tokens (gold play/Run/pins/playhead, dark rounded panels, token text, dark-tuned compat/error), UX/UI review, no leftover hardcoded colors. 379 tests.
- **T-055 ✅** CHANGELOG (Changed: dark+gold theme) + CLAUDE token convention.
- **G-017 sealed done.** Chain: T-053 `b014d72` → T-054 `5e9cdb8` → T-055 (docs).
- Theme = token-driven (`src/App/Themes/Tokens.xaml`+`Controls.xaml`). Visual look = app-run/user-verified.

## 2026-07-18 — T-053 done → T-054 (G-017 theme drain)

- **T-053 ✅** tokens — `Tokens.xaml` (brushes/radii/type) + `Controls.xaml` (re-templated Button/AccentButton/Slider/CheckBox/ComboBox/Tabs/ProgressBar/ListBox/GridSplitter/TextBox), merged in App.xaml. 379 tests. Commit `b014d72`.
- **T-054 🔵** restyle all screens + UX/UI review dispatched.

## 2026-07-18 — todo-next-all: draining G-017 (theme)

- Order: T-053 (tokens + base styles) → T-054 (restyle all views + UX/UI review) → T-055 (docs). Serialized (shared XAML).
- **T-053 🔵** design-token ResourceDictionary dispatched. (Worker headless → works from the extracted palette; exact mockup detail deferred to the user's live review.)

## 2026-07-18 — ✅ G-016 done (scrub) · todo-goal PLANNED: G-017 theme

- **G-016 ✅** — T-051 `c211810` live coalesced+throttled scrub (video follows the pin; one-in-flight + latest-target via Seeked; 70ms throttle; T-033 hold preserved; 379 tests) + T-052 CHANGELOG. Sealed done.
- **G-017 PLANNED (todo-goal, plan-only):** apply the user's redesign — **premium dark + gold** theme, token-driven. Archived the reference `docs/design/references/2026-07-18_redesign-standalone.html` (a self-extracting HTML bundle; palette extracted from its preview SVG: bg #0d0f13 · surface #15181e · surface2 #1a1e25 · video #000 · accent gold #e0a83a). Tasks: T-053 `Tokens.xaml` + base control styles (UI-Designer, token-first) · T-054 restyle all screens + UX/UI review · T-055 docs. NOTE: full mockup detail is in a JS bundle → render it in a browser to confirm exact spacing/type before finalizing. `proceed G-017`.

## 2026-07-18 — todo-next-all: draining G-016 (responsive scrub)

- **T-051 🔵** live throttled + coalesced scrub dispatched. Then T-052 docs → converge.

## 2026-07-18 — todo-goal PLANNED: G-016 responsive scrub (pin lag)

- User: dragging the video time pin still lags. Diagnosis (grounded): current scrub is **seek-on-release** (PlayerView `DragStarted→BeginUserScrub` suppresses, `DragCompleted→EndUserScrub` seeks once) → video frozen under the finger during drag + no seek coalescing. Feels delayed.
- **G-016** "Responsive scrub" — T-051 live throttled+coalesced scrub (video follows the pin during drag; coalesce = one in-flight seek + latest-target-only via the T-033 `Seeked` signal; throttle; keep pop-back protection) · T-052 docs. Plan-only; `proceed G-016`.

## 2026-07-18 — ✅ G-012→G-015 DONE — todo-next-all backlog converged (10/10)

- **T-049 ✅** selectable segments `d1659f3` — `SplitSegmentViewModel` + `SelectedSegmentIndices` (full→muxer, subset→per-segment `-c copy`, only selected written); real-ffmpeg verified; fixed a latent per-segment `-to` bug. 372 tests.
- **Docs ✅** `d40fef3` — combined pass (README/USER_GUIDE/ARCHITECTURE/CHANGELOG/CLAUDE) closing T-043/046/048/050.
- **Sealed done:** G-012 (instant cut + visible progress) · G-013 (staged status + ETA) · G-014 (clear) · G-015 (selectable segments).
- Drain commits local: 1b85a32·6721337·50cbfc1·112a87a·e18176a·d1659f3·d40fef3.

## 2026-07-18 — T-047 done → T-049 (backlog drain; last build task)

- **T-047 ✅** clearable file — `IMediaPlayer.Unload()` (FFME `Close()`), Split ClearCommand (reset+bg-index-cancel), Join Clear-all; buttons wired. 360 tests (9 new). Commit `e18176a`.
- **T-049 🔵** selectable segments (export only chosen parts) dispatched — the L task.

## 2026-07-18 — T-045 done → T-047 (backlog drain; progress cluster complete)

- **T-045 ✅** ETA — `EtaEstimator` (EMA-smoothed, "~Nm Ss left"/"estimating…") + `OperationViewModel.EtaText` via Stopwatch; SplitView/JoinView show it beside StatusText. 351 tests (20 new). Commit `112a87a`.
- G-012 + G-013 progress/feedback cluster done (T-041/042/044/045).
- **T-047 🔵** clearable file (Clear / Clear all + `IMediaPlayer.Unload`) dispatched.

## 2026-07-18 — T-044 done → T-045 (backlog drain)

- **T-044 ✅** staged status — `OperationStatus` record + optional `IProgress<OperationStatus>` on engines; split/join emit real stages → `StatusText`; segment N/M = "M parts" fallback. 331 tests (4 new). Commit `50cbfc1`.
- **T-045 🔵** ETA (estimated time remaining) dispatched.

## 2026-07-18 — T-042 done → T-044 (backlog drain)

- **T-042 ✅** visible progress — `OperationViewModel.IsIndeterminate` (running && progress≤0) + `StatusText` public; SplitView/JoinView bind IsIndeterminate + status label. Root cause was UI-side. 327 tests (9 new). Commit `6721337`.
- **T-044 🔵** staged status synced to each process action dispatched (engine stage callback → StatusText).

## 2026-07-18 — T-041 done → T-042 (backlog drain)

- **T-041 ✅** instant cut — optimistic pending marker + async `ResolveSnap` on index-arrival + dedupe-on-resolve + stale-load guard; `CutMarkerViewModel.IsSnapPending`/"snapping…". 318 tests (7 new). Commit `1b85a32`.
- **T-042 🔵** visible split/join progress dispatched (also lays StatusText/IsIndeterminate groundwork for T-044/T-045).

## 2026-07-18 — todo-next-all: draining the backlog G-012→G-015 (10 tasks)

- Order: T-041 (instant cut) → T-042 (visible progress) → T-044 (stage text) → T-045 (ETA) → T-047 (clear) → T-049 (selectable segments) → one docs pass (T-043/046/048/050). Serialized (shared app tree).
- **T-041 🔵** responsive Set-cut-at-playhead dispatched.

## 2026-07-18 — todo-goal PLANNED: G-015 selectable segments

- User Q: does split let you pick only some parts? ANSWER: no — it writes ALL parts (segment muxer). Not current behavior. Good feature (save time+disk); engine already has a per-segment `-ss/-to -c copy` path to build on.
- **G-015** "Selectable segments" — segment list (from markers) with checkboxes; export only selected parts via per-segment copy (full set → segment muxer as today); still `-c copy`. Tasks: T-049 selectable segments · T-050 docs. Plan-only.
- Planned queue: G-012 · G-013 · G-014 · G-015 (10 tasks, all unbuilt). 13 commits unpushed.

## 2026-07-18 — todo-goal PLANNED: G-014 clearable file

- **G-014** "Clearable file" — Split "Clear" unloads the current video + resets the screen (player unload, markers/timeline/output cleared, cancel bg index); Join "Clear all" empties the clip list. New `IMediaPlayer.Unload()`. Tasks: T-047 clear/reset · T-048 docs. Plan-only; `proceed G-014`.
- Planned queue now: G-012 (responsive cut + progress) · G-013 (staged progress + ETA) · G-014 (clear). 13 commits still unpushed.

## 2026-07-18 — todo-goal PLANNED: G-013 staged progress + ETA

- **G-013** "Staged progress synced to each process action + ETA" — user wants the operation to show the current STAGE (synced to real work: Preparing → Splitting segment N/M → Finalizing → Done) + an estimated time remaining. Tasks: T-044 staged status (engine stage callback → OperationViewModel.StatusText) · T-045 ETA (`EtaEstimator` from real elapsed vs progress, smoothed, "~1m 20s left") · T-046 docs.
- **Closely tied to G-012** (visible progress) — both touch OperationViewModel/progress surface; recommend building together (T-042 + T-044/T-045). Plan-only; `proceed G-012 + G-013` together ideal.

## 2026-07-17 — todo-goal PLANNED: G-012 responsive cut + visible split progress

- Two user-reported "seems broken but actually slow/silent" issues, both diagnosed in code:
  - **Set-cut slow** — `AddCutAt` (SplitViewModel.cs:535) DEFERS the visible marker-add until the background keyframe index finishes (`AddCutAtWhenIndexedAsync` awaits `EnsureKeyframesAsync`). On a big file the marker doesn't appear until the scan completes → feels broken. (Side effect of T-030 non-blocking load.)
  - **Split no feedback** — a ProgressBar exists (SplitView.xaml:196, bound Operation.Progress) but `-c copy` progress can be sparse/instant → bar doesn't visibly move → looks stuck.
- Tasks: T-041 optimistic marker + async snap-resolve (instant add, "snapping…" → final) · T-042 visible split/join progress (moving bar / indeterminate + "Splitting…" status) · T-043 docs.
- Plan-only; `proceed G-012`.

## 2026-07-17 — ✅ G-010 + G-011 DONE — todo-next-all converged (6/6)

- **T-039 ✅** docs `4e55301` — CHANGELOG/USER_GUIDE/README/ARCHITECTURE/CLAUDE updated for unicode paths, .ts fix, exit-28→DiskFull, copyable log, remembered folders, ±10m/20m skips.
- **G-010 sealed done** (T-035/036/037/038/039) + **G-011 done** (T-040). 313 tests, 0-warning.
- **User's .ts blocker RESOLVED** (root = UTF-8 path encoding). Plus copyable error log, remembered folders, ±10m/20m skips.
- Drain commits local (unpushed): 4eef2d0·c8618c0·7f7ce35·dc9f314·ee64702·4e55301.

## 2026-07-17 — T-040 done (G-011 ✅) → T-039 docs (last drain task)

- **T-040 ✅** ±10m/±20m skip buttons on the jog row (CommandParameter ±600/±1200, no VM change). 313 tests. Commit `ee64702`. **G-011 sealed done.** (Minor: new buttons use − U+2212 vs old ASCII `-` — cosmetic, flagged.)
- **T-039 🔵** docs dispatched — covers G-010 (.ts/encoding/copyable-log/settings) + G-011 (±10m/20m). Last task of the drain.

## 2026-07-17 — T-038 done → T-040 (drain: last feature task)

- **T-038 ✅** remember location — `IAppSettings`/`AppSettings` → `%APPDATA%/VideoSplitJoiner/settings.json` (lastInputDir/lastOutputDir, robust); picker InitialDirectory + output-dir default read/write; VM optional-dep ctor. 311 tests (14 new). Commit `dc9f314`.
- **T-040 🔵** ±10m/±20m skip buttons (G-011) dispatched.

## 2026-07-17 — T-037 done → T-038 (G-010 drain)

- **T-037 ✅** copyable error — selectable read-only TextBox + Copy button + Open-log; full stderr (+command+exit+timestamp) written to `%LOCALAPPDATA%/VideoSplitJoiner/logs/` via `ErrorLogWriter`; `UserFacingError` gained `LogFilePath`/`FullText`/`CopyText`. 297 tests (12 new). Commit `7f7ce35`.
- **T-038 🔵** remember last input/output location dispatched.

## 2026-07-17 — T-035 done → T-037 (G-010 drain)

- **T-035 ✅** — FINDING: **.ts split succeeds after T-036** (exit -28 was the mangled-path symptom, fixed by UTF-8). Verified mpegts fixture + unicode-path split → playable segments. Hardened: exit -28/ENOSPC → DiskFull message + pre-flight free-space check; `-c copy` preserved. 285 tests (5 new). Commit `c8618c0`. **User's blocker resolved.**
- **T-037 🔵** copyable + full error log dispatched.

## 2026-07-17 — T-036 done → T-035 (G-010 drain)

- **T-036 ✅** UTF-8 encoding — `StandardError/OutputEncoding = UTF8` on both runners; ArgumentList path-in confirmed intact (no lossy round-trip). Unicode-path fixture: probe + split-to-unicode-output work; captured error un-garbled (proven by fix-removal → test fails). 280 tests (3 new). Commit `4eef2d0`. Likely resolves the .ts write failure root.
- **T-035 🔵** investigate + fix .ts/mpegts split (exit -28) dispatched.

## 2026-07-17 — todo-next-all: draining G-010 + G-011 (6 tasks)

- Order: T-036 (UTF-8, foundational) → T-035 (.ts fix) → T-037 (copyable log) → T-038 (settings) → T-040 (±10m/20m skips) → T-039 (docs). Serialized (shared app tree).
- **T-036 🔵** UTF-8 process encoding + non-ASCII paths dispatched.

## 2026-07-17 — ✅ G-009 DONE — scrub pop-back fixed

- **T-033 ✅** `3e2ad4e` — seek-target hold + new `IMediaPlayer.Seeked` completion event + drag gating + bounded-tick anti-freeze; core regression test (stale 12s echo doesn't pop a 40s target) green. 277 tests.
- **T-034 ✅** CHANGELOG Fixed entry (scrub pop-back). G-009 sealed done.
- G-009 commits local (unpushed). G-010 (.ts/log/settings) + G-011 (±10m/20m skips) still planned.

## 2026-07-17 — todo-goal PLANNED: G-011 add ±10m/±20m skip buttons

- **G-011** "Add ±10m/±20m skip buttons" — tiny: 4 buttons on the existing jog WrapPanel bound to the parameterized `SkipCommand` (CommandParameter −1200/−600/600/1200); `SkipBy` already clamps → no VM change. Task T-040. Plan-only; `proceed G-011` (or fold into the current wave). (T-033 scrub-fix still building.)

## 2026-07-17 — proceed G-009 (scrub pop-back) → building

- User: "proceed / spec to be done" → building the planned G-009 scrub pop-back fix.
- **T-033 🔵** dispatched. (G-010 .ts/log/settings still planned — offer to continue after.)

## 2026-07-17 — todo-goal ×2 PLANNED (plan-only): G-009 scrub pop-back · G-010 .ts/encoding/log/settings

- **G-009** "Fix scrub pop-back" — diagnosed: `PlayerViewModel.OnPositionChanged` (L358) overwrites the slider with the stale player position during an in-flight async FFME seek; `_suppressSeek` only blocks re-seek, not the display yank. Tasks: T-033 fix (seek-target hold + drag gating + FFME seek-completion) · T-034 docs.
- **G-010** "Fix .ts split + copyable log + remember location" — from a real user error: `ffmpeg split failed (exit -28)` on an mpegts/.ts file at a Japanese path shown as MOJIBAKE. Diagnosis: **exit -28 = AVERROR(ENOSPC)** (can't write output — disk-full OR mangled non-ASCII output path); mojibake = stderr captured with wrong (console codepage) encoding; error not copyable (non-selectable TextBlock + truncated tail); no remembered folders. Tasks: T-035 investigate+fix .ts (exit -28) · T-036 UTF-8 encoding + unicode paths (foundational) · T-037 copyable+full error log (+ log file) · T-038 remember last input/output dir (settings.json) · T-039 docs.
- Both PLAN-ONLY (not built). `proceed G-009` / `proceed G-010`. G-010's .ts failure is the user's active blocker → T-035/T-036 priority.

## 2026-07-17 — ✅ G-008 DONE — fast load converged (todo-next-all, 3/3)

- **T-032 ✅** docs `2018c22` — USER_GUIDE/ARCHITECTURE/CHANGELOG/CLAUDE/README updated for non-blocking load + demux keyframe scan.
- **G-008 sealed done.** Chain: T-030 `76f60e6` → T-031 `d1cba99` → T-032 `2018c22`. 269 tests, 0-warning.
- Fixes the reported slow drag-drop load: preview opens on probe (before indexing) + keyframe scan 3.86× faster (demux packet-flags). G-008 commits local (unpushed).

## 2026-07-17 — T-031 done → T-032 (G-008 drain)

- **T-031 ✅** faster keyframe scan — demux packet-flag query (`-show_packets ... flags` K-filter, pts→dts fallback) replacing frame-decode scan; frame-scan fallback on empty/throw; `LastScanPath` for testability. Measured **3.86× faster** (4K: 216ms→56ms, 10=10 keyframes). 269 tests (15 new). Commit `d1cba99`.
- **T-032 🔵** docs (fast load) dispatched — last G-008 node.

## 2026-07-17 — T-030 done → T-031 (G-008 drain)

- **T-030 ✅** non-blocking load — LoadAsync gates only on ProbeAsync; keyframes index in a cancellable background task (`IsIndexingKeyframes`, per-load CTS + stale-guard); cut-during-index awaits the in-flight scan (no snap-to-empty); ProbeFailed path unchanged. 254 tests (7 new). Commit `76f60e6`.
- **T-031 🔵** faster keyframe scan (demux packet-flag query + fallback) dispatched.

## 2026-07-17 — todo-next-all: draining G-008 (fast load)

- Driving G-008 to convergence: T-030 (non-blocking load) → T-031 (faster keyframe scan) → T-032 (docs). Serialized (shared load/probe path).
- **T-030 🔵** non-blocking load dispatched.

## 2026-07-17 — ✅ finalization complete — G-004/5/6/7 sealed

- **T-026 ✅** docs `fadecf7` — README·USER_GUIDE·ARCHITECTURE·CHANGELOG·CLAUDE updated for FFME + no-detect + 4K + resizable pane + player controls; grep-clean of auto-detect/MediaElement refs.
- **Sealed done:** G-004 (FFME preview) · G-005 (remove auto-detect + 4K) · G-006 (resizable pane) · G-007 (player controls). All feature work + finalization complete.
- Ready for `cycle-push` (commits local, G-002→G-007 + docs). **G-008 (fast load) remains PLANNED** (not built) — `proceed G-008` when wanted.
- Minor follow-up flagged by docs worker (not blocking): 2 stale in-code comments (SplitView.xaml candidates comment, SplitViewModel.cs "no candidates" doc-comment) — cosmetic, a later cleanup ticket.

## 2026-07-17 — T-025 done → T-026 (docs); + G-008 (fast load) PLANNED

- **T-025 ✅** packaging — `package.ps1` now bundles the FFME ffmpeg SHARED build (8 shared DLLs + ffmpeg/ffprobe exes) into one `dist/publish/ffmpeg/` folder serving BOTH FFME (FFmpegDirectory) + engine (locator); THIRD-PARTY-NOTICES adds FFME (Ms-PL) + ffmpeg 7.1 shared (GPL). Ran: 73MB exe → 127MB zip. 247 tests. Commit `63055fe`.
- **T-026 🔵** docs (FFME + no-detect + 4K + resizable + player-controls; closes G-004→G-007) dispatched — last finalization step before cycle-push.
- **G-008 PLANNED (todo-goal, plan-only):** "Fast video load" — diagnosed: `LoadAsync` blocks on the full keyframe scan (SplitViewModel.cs:270) before opening the preview (L301), and the scan uses slow `-skip_frame nokey` frame decode. Tasks: T-030 non-blocking load (preview instant + background index) · T-031 faster scan (demux packet-flag query + fallback) · T-032 docs. Follow-up — does NOT block finalization; `proceed G-008` later.

## 2026-07-17 — user "approve" → finalization (T-025 packaging → T-026 docs)

- User approved proceeding without the live-verify gate. Driving the two remaining finalization tasks to convergence.
- **T-025 🔵** packaging — bundle the FFME ffmpeg SHARED build (DLLs + exes) into the package; update THIRD-PARTY-NOTICES; closes G-004 packaging. Then **T-026** docs (covers G-004→G-007). Then ready for cycle-push (18 commits local).

## 2026-07-17 — T-028 done → T-029 started (G-007 drive)

- **T-028 ✅** skip/jog — `SkipBy` (relative Scrub, clamped) + parameterized `SkipCommand` (±1/5/10/20/60/300s) + frame-step (`IMediaPlayer.StepFrame` → FFME `StepForward`/`StepBackward`) + jump-to-start/end; WrapPanel jog row. 240 tests (11 new). Commit `2548024`.
- **T-029 🔵** volume/mute/playback-speed dispatched (extends IMediaPlayer: Volume/IsMuted/SpeedRatio).

## 2026-07-17 — todo-goal-next: G-007 (player controls) planned → building

- **G-007** "Player controls" — T-028 skip/jog (±1s/5s/10s/20s/1m/5m) + frame-step + jump-to-ends (find the exact split point) · T-029 fundamental options (volume/mute/playback-speed). Docs fold into T-026 (now G-004/5/6/7).
- Decisions: **D1** skip = relative-seek VM-only over existing Seek · **D2** frame-step + volume/mute/speed extend IMediaPlayer (FFME maps StepForward/Backward/Volume/IsMuted/SpeedRatio) · **D3** fundamental set only (fullscreen/loop parked).
- Noted to user: FFME playback still unconfirmed live — these features build on it.
- **T-028 🔵** skip/jog + frame-step dispatched.

## 2026-07-17 — todo-goal-next: G-006 (adjustable video pane) planned → building

- **G-006** "Adjustable video preview area" — lean goal (1 build task): drag-resizable player pane via GridSplitter, remove the 320px cap, FFME scales to fit. Docs folded into the pending T-026 finalization (now covers G-004+G-005+G-006).
- **T-027 🔵** resizable player pane dispatched. Pure XAML layout, no VM change.
- (T-025 packaging + T-026 docs still the shared finalization, pending user verification of FFME/4K.)

## 2026-07-17 — T-023 done → T-024 started (G-005 drive)

- **T-023 ✅** auto-detect removed — deleted `Core/Detect/` + `CandidateViewModel` + all SplitViewModel/Timeline/SplitView candidate wiring + 29 detect tests; grep-clean (0 refs). `SplitViewModel` ctor now `(IMediaProbe, ISplitEngine, IMediaPlayer? = null)`. **209 tests** green, 0-warning. Commit `0798c6d`.
- **T-024 🔵** 4K performance dispatched — FFME HW-decode + downscale-preview fallback + verify 4K split (copy) + keyframe-scan stay fast. Also the live re-verification of FFME playback.

## 2026-07-16 — todo-goal-next: G-005 (remove auto-detect + 4K perf) planned → building

- G-004 FFME code shipped (T-019 ✅ deps+probe, T-020 ✅ swap, launches clean pid 17564). User pivoted before confirming playback → treating "support 4K play" as implicit confirmation; 4K work re-verifies play.
- **G-004 T-021/T-022 dropped (superseded)** → folded into G-005 T-025 (packaging) + T-026 (docs) for one clean finalization.
- **G-005** planned: T-023 remove auto-detect (Detect + all VM/UI/test wiring) · T-024 4K perf (FFME HW-decode/downscale preview + verify fast 4K split — split is `-c copy`, resolution-independent) · T-025 packaging (bundle FFME shared build) · T-026 docs.
- Decisions: **D1** remove auto-detect wholesale (keep markers/playhead/click-to-cut) · **D2** 4K play = HW-decode first, downscale-preview fallback (never touches the cut) · **D3** split stays copy · **D4** finalize G-004 here.
- **T-023 🔵** remove auto-detect dispatched.

## 2026-07-16 — T-019 done → T-020 started (G-004 drive)

- **T-019 ✅** FFME deps — package `FFME.Windows 7.0.361-beta.1` (id ≠ `Unosquare.FFME.Windows`; only the beta binds ffmpeg 7.x via FFmpeg.AutoGen 7.0.0) + BtbN ffmpeg 7.1 shared build in gitignored `ffmpeg-shared/` + `Library.FFmpegDirectory` startup resolver + `packaging/fetch-ffmpeg-shared.{ps1,sh}`. **Init probe PASSED — FFME loaded ffmpeg n7.1.5 clean.** 238 tests. Commit `b2972fd`. (FFME is a prerelease — the only ffmpeg-7.x build.)
- **T-020 🔵** swap MediaElement → FFME behind IMediaPlayer dispatched.

## 2026-07-16 — bug: "can't play video" → diagnosed + G-004 (FFME) planned

- **Diagnosis:** user reported preview "can't play". Traced full player path — wiring is CORRECT (MainViewModel → MediaElementPlayer → PlayerView attach → Open). User confirmed the **yellow "Preview unavailable" banner** shows → CODEC issue: Windows Media Foundation lacks the decoder (HEVC/H.265 or MKV). The realized **D1 risk** from G-002. Cut/join unaffected.
- User chose the **FFME (ffmpeg-decoded preview)** fix over LibVLC / HEVC-extension / leave-as-is.
- **G-004** "FFME preview" planned → T-019 (FFME pkg + matched ffmpeg SHARED build + startup init + engine consolidation) · T-020 (player impl swap behind IMediaPlayer) · T-021 (packaging bundles DLLs) · T-022 (docs).
- Feasibility: no shared build on disk; network to nuget.org + gyan.dev OK.
- **T-019 🔵** driven inline (version-matching is make-or-break).

## 2026-07-16 — ✅ G-003 DONE — drag-and-drop converged (3/3)

- **T-018 ✅** docs — drag-and-drop woven into README · USER_GUIDE · ARCHITECTURE · CHANGELOG (0.2.0) · CLAUDE. Commit `599da2d`.
- **G-003 sealed `status: done`** (todo-goal-next plan+build). Chain: T-016 `721afc8` → T-017 `638aac4` → T-018 `599da2d`.
- Tests: **237 passing** (127 Core + 110 App), 0-warning.
- Feature: drop videos onto Split (load) / Join (add, in order); drag to reorder Join clips (shared `Move` path); non-video ignored; pure `VideoFileFilter` tested; live gesture verified via app-run relaunch.

## 2026-07-16 — T-017 done → T-018 started (G-003 drive)

- **T-017 ✅** drag-reorder — single `MoveAsync(from,to)` (ObservableCollection.Move + RefreshCompatAsync); Up/Down delegate to it; ListBox item-drag distinguished from external FileDrop by payload type (file drop bubbles to T-016 handler). 237 tests (110 App, 7 new). Commit `638aac4`. Live gesture NOT verified (headless).
- **T-018 🔵** perfect-docs (drag-and-drop) dispatched — terminal node.

## 2026-07-16 — T-016 done → T-017 started (G-003 drive)

- **T-016 ✅** file drag-drop — pure `VideoFileFilter` (AcceptVideoFiles/HasAnyVideo, 25 tests) + AllowDrop/Drop on Split (→ LoadCommand, first file) + Join (→ AddFilesCommand, all, FileDrop-only guard) + highlight overlay. 230 tests (103 App). Commit `721afc8`. Live drop NOT verified (headless).
- **T-017 🔵** drag-to-reorder Join list dispatched.

## 2026-07-16 — todo-goal-next: G-003 (drag-and-drop) planned → building

- **G-003** "Drag-and-drop support" created; T-016 (file-drop → Split load / Join add + pure `VideoFileFilter`) · T-017 (drag-reorder Join list) · T-018 (docs). All `spec: reviewed`.
- Decisions: **D1** standard WPF AllowDrop/Drop code-behind → existing Load/Add commands (no new load logic) · **D2** pure tested filter, event wiring app-run-verified · **D3** reorder-drag auto-included under --auto-approve (isolated in T-017).
- Fills a gap: T-007 speced drop-to-load but only wired the picker.
- **T-016 🔵** external file drag-drop dispatched.

## 2026-07-16 — ✅ G-002 DONE — in-app video player converged (4/4)

- **T-015 ✅** docs — README · USER_GUIDE · ARCHITECTURE · CHANGELOG (0.2.0-Unreleased) · CLAUDE updated for the player. Commit `d97a405`.
- **G-002 sealed `status: done`** — all 4 tasks done (todo-goal-next plan+build converged).
- Chain: T-012 `8bd4afd` → T-013 `a053b70` → T-014 `e98871b` → T-015 `d97a405`.
- Tests: **205 passing** (127 Core + 78 App), 0-warning build.
- Feature: load → in-app play/scrub → "Set cut at playhead" (snaps) → timeline strip (playhead + marker/candidate ticks) → click-to-cut / click-to-seek. All visual cuts reuse the one `AddCutAt`→snap path.
- ⚠ Live playback/render unverified in headless workers → verified via `app-run` relaunch on the real desktop.

## 2026-07-16 — T-014 done → T-015 started (G-002 drive)

- **T-014 ✅** timeline overlay — pure `TimelineMath` (ToNormalized/FromNormalized) + `TimelineViewModel` projection (playhead + marker/candidate ticks) + `TimelineView` (click-to-cut via `AddCutAt`, click-tick → existing seek/preview commands). 205/205 (21 new). Commit `e98871b`. Live render/click NOT verified (headless).
- **T-015 🔵** perfect-docs (player feature) dispatched — terminal node.

## 2026-07-16 — T-013 done → T-014 started (G-002 drive)

- **T-013 ✅** set-cut-from-playhead — `AddCutAt(t)` single entry (snap+dedup; old `AddMarker` refactored onto it) + `SetCutAtPlayhead` (guarded on HasFile+IsReady) + `SeekToMarker`/`PreviewCandidate` (`Player.Scrub`). 184/184 (8 new). Commit `a053b70`. Live seek NOT verified (headless).
- **T-014 🔵** timeline overlay (markers/candidates ticks + playhead, click-to-cut / click-to-seek) dispatched — last feature node.

## 2026-07-16 — T-012 done → T-013 started (G-002 drive)

- **T-012 ✅** player control — `IMediaPlayer` + `MediaElementPlayer` (DispatcherTimer poll, Open/Play/Pause/Stop/Seek) + `PlayerViewModel` (seek-feedback guarded via `_suppressSeek`) + `PlayerView`; `NullMediaPlayer` default keeps SplitViewModel ctor back-compat. 176/176 (14 new). Commit `8bd4afd`. Live playback NOT verified (headless).
- **T-013 🔵** set-cut-from-playhead + seek-to-marker + preview-candidate dispatched.

## 2026-07-16 — todo-goal-next: G-002 (in-app video player) planned → building

- **G-002** "In-app video player — preview playback + scrub + pick cut points from the playhead" created; decomposed into T-012→T-015 (all `spec: reviewed`).
- Locked decisions: **D1** player = WPF MediaElement (auto-picked under --auto-approve; FFME parked as D1-alt upgrade) · **D2** `IMediaPlayer` abstraction (fake-testable, live playback via app-run only) · **D3** reuse existing keyframe-snap for playhead-captured cuts.
- Plan→build fusion (todo-goal-next): proceeding immediately. **T-012 🔵** player control dispatched.
- (G-001 remains ✅ done + pushed to origin/main @ 00b8b9d.)

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
