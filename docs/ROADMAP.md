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
| ✅ | Bulk Cut | Keyframe snapping made **visible** (G-041) — each row shows the requested time plus where it landed (`→ 00:04.0 (−1.0s)`); the editable IN/OUT field commits only a real edit, so it can no longer write the displayed (snapped, 0.1s-truncated) value back over the request; the coarse-grid advisory now also fires at an exactly-4.0s mean GOP and reports how far the cut actually moved |
| ✅ | Bulk Cut | Opt-in **Replace originals** output mode (G-041) — a third axis (`OutputMode`) beside collision policy, not a collision policy; every produced part is verified *before* any destination is touched, then swapped in via `File.Replace` with a backup (never delete-then-move, with a rename-aside fallback + restore-on-failure); the replaced original goes to the Recycle Bin, and the run is gated by a counted confirmation whose seam defaults to refusing |
| ✅ | Bulk Cut | **Exact cut** (G-042) — frame-exact trimming: only the head fragment (requested time → next keyframe) is re-encoded, the remainder is stream-copied, and the two are concatenated (`SmartCutEngine`, a separate engine — `SplitEngine` stays copy-only). `CutPrecision.Lossless` remains the default; a source whose codecs cannot be reproduced falls back per row and says why. **Covered by unit tests against fakes AND by `SmartCutEngineIntegrationTests` against real ffmpeg** — including that a re-encoded head concatenates cleanly onto a copied tail |
| ✅ | Bulk Cut | **Select all / Select none**, and a row checkbox that actually works (G-043) — the checkbox was bound to the app's own eligibility check, so a freshly imported row read as unticked while the intent behind it was already set: one click did nothing, and a second click silently dropped that video from the batch (it stayed excluded even after a real cut was set, and apply-to-all skipped it). The user's intent and the computed eligibility are now separate, and a ticked row the app is nonetheless excluding dims and says why ("nothing to trim yet — set an intro or outro"). Exact-mode rows are no longer excluded by judging them against the keyframe-snapped time an exact cut does not use |
| ✅ | Bulk Cut | Cut-profile thumbnails are re-pictureable (G-044) — a **Thumbnail…** button on the always-visible profile bar; the upload previously lived only inside the "Save current as…" popup, so re-picturing an existing profile was effectively unreachable. An explicit upload now reports a failure instead of silently doing nothing, and the store copies the new picture in before removing the old, so a copy that fails part-way can no longer leave the profile with no image at all |
| ✅ | UI | Dark + gold theme, custom window chrome + vector caption icons, themed scrollbar + tooltips, vertical/horizontal layout modes, audio waveform strip |
| ✅ | Robustness | Global crash safety net; safe FFME re-open (MediaReopenGuard); in-flight keyframe-scan dedup + honest Preparing/ETA |
| ✅ | Packaging | App-local ffmpeg bundling; single-file publish; portable-.NET dev setup |
| ✅ | Quality | Living-spec layer (15 SPEC-NNN, 606/615 invariants covered — audited, then the gaps closed; see [specs/_index.md](specs/_index.md)), 1111 automated tests, `serves-spec:` traceability |
| ✅ | Release | MIT `LICENSE` at the repo root, added when v1.0.0 was cut — the bundled ffmpeg's own terms stay recorded in `THIRD-PARTY-NOTICES.md` / [adr/0012-gpl-lgpl-licensing-fork.md](adr/0012-gpl-lgpl-licensing-fork.md) |
| ✅ | Release | **v1.1.0** (2026-08-29) — Exact cut, Replace originals, visible snapping, and the Bulk Cut selection + profile-thumbnail fixes; 1111 tests green at the cut ([CHANGELOG.md](../CHANGELOG.md)). The tag-driven release build targets the dedicated installer repo `MrParkerZ7/installer-video-spliter-joiner` for the installer + portable zip, so binaries never live in this tree — its publish step still depends on the `RELEASE_PAT` secret (Ops row below) |

## Deferred / future 📋

| Status | Area | Item |
|--------|------|------|
| 📋 | Bulk Cut | **Auto-detect** intro/outro (scene-change / black-frame / silence / audio-fingerprint) — currently manual per-video + apply-to-all |
| 📋 | Cuts | Frame-exact cuts on the **Split** screen — shipped for Bulk Cut only (`CutPrecision.Exact`, G-042); `SplitEngine` remains copy-only by design |
| 📋 | Bulk Cut | The row checkbox does **double duty** — it decides which videos the batch runs **and** which rows **Apply to all** / **Apply profile** write to, so *Select all* also widens what the next apply-to-all overwrites. Consciously accepted (it predates G-043; the button tooltips say so) — a separate "apply to" selection would be the fix |
| 📋 | Bulk Cut | Custom / per-video output folders; multiple kept segments per video; >1 row per source; configurable output-name templates |
| 📋 | Quality | Close the **9 remaining** spec-coverage gaps — T-105 closed 7 of the original 16 (helper extractions + a `SplitEngine` disk-probe seam); what is left is intentionally-unreachable defensive asserts, WPF-render/MediaElement-bound behaviour, or needs a windowed/STA harness — see `docs/specs/_GAPS.md` |
| 📋 | Ops | No build/test gate on push or PR yet — the only workflow is the tag-driven [.github/workflows/release.yml](../.github/workflows/release.yml) (test → package → installer), and even its publish step is skipped until a `RELEASE_PAT` secret is set; see [adr/0014-no-ci-yet.md](adr/0014-no-ci-yet.md) |

> Out-of-scope items are recorded per-epic in `docs/todo/G-*.md` and per-design in `docs/design/D-*`; this table is the roll-up.
