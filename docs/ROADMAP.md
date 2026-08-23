# Roadmap

Status of VideoSplitJoiner's features. Icons: ✅ done · 🚧 in progress · 📋 planned/future · ⏸ paused · ❌ dropped.
The day-to-day task/goal board lives under `docs/todo/`; this is the durable feature-status view.

## Shipped ✅

| Status | Area | Feature |
|--------|------|---------|
| ✅ | Split | Lossless split at keyframe-snapped cut points (`-c copy`, no re-encode); markers ordered by time; output defaults to the source folder |
| ✅ | Split | In-app preview player — playback, scrub, jog (±1s/5s/10s/20s/1m/5m/10m/20m), step-frame; jog freezes on the exact frame; click-to-seek; hover-thumbnail; set-cut-at-playhead |
| ✅ | Join | Concatenate compatible clips (stream-copy), with compatibility checks |
| ✅ | Bulk Cut | Batch-trim intro (+ optional outro) off many videos, keeping the middle; failure-isolated sequential batch; AutoSuffix collision; per-row + aggregate progress |
| ✅ | Bulk Cut | Per-row dual-handle scrub bars; apply-to-all (outro-from-end); layout-mode-aware (vertical/horizontal); preview player + set-at-playhead |
| ✅ | Bulk Cut | Reusable **cut profiles** — save/apply/delete, persisted; optional user-uploaded thumbnail (auto-defaulting to the intro-end frame); per-row cut-point frame thumbnails |
| ✅ | UI | Dark + gold theme, custom window chrome + vector caption icons, themed scrollbar + tooltips, vertical/horizontal layout modes, audio waveform strip |
| ✅ | Robustness | Global crash safety net; safe FFME re-open (MediaReopenGuard); in-flight keyframe-scan dedup + honest Preparing/ETA |
| ✅ | Packaging | App-local ffmpeg bundling; single-file publish; portable-.NET dev setup |
| ✅ | Quality | Living-spec layer (15 SPEC-NNN, ~97% invariant coverage), ~880 automated tests, `serves-spec:` traceability |

## Deferred / future 📋

| Status | Area | Item |
|--------|------|------|
| 📋 | Bulk Cut | **Auto-detect** intro/outro (scene-change / black-frame / silence / audio-fingerprint) — currently manual per-video + apply-to-all |
| 📋 | Cuts | Frame-exact / re-encoded cuts — out of scope by design (would break the `-c copy` invariant) |
| 📋 | Bulk Cut | Custom / per-video output folders; multiple kept segments per video; >1 row per source; configurable output-name templates |
| 📋 | Quality | Close the 16 deferred spec-coverage gaps (extract pure helpers from views + add a `SplitEngine` disk-probe seam) — see `docs/specs/_GAPS.md` / board task T-105 |
| 📋 | Ops | CI is intentionally not wired yet — see [adr/0014-no-ci-yet.md](adr/0014-no-ci-yet.md) |
| 📋 | Release | A repository `LICENSE` file (the app bundles ffmpeg → GPL/LGPL considerations, see [adr/0012-gpl-lgpl-licensing-fork.md](adr/0012-gpl-lgpl-licensing-fork.md)) |

> Out-of-scope items are recorded per-epic in `docs/todo/G-*.md` and per-design in `docs/design/D-*`; this table is the roll-up.
