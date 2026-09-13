# ⏸ Pause — 2026-09-13, `todo-design-done` D-005 → auto-plan (`todo-task D-005`)

Paused by `todo-pause` at a **clean boundary** (stop-point kind **a**). The drive was planning, and nothing was
mid-flight:
- the review workflow had already finished;
- no product code or test has been touched;
- the design seal was already landed.

This pause lands the plan on the board, so it outlives the session scratchpad.

## What was running

`todo-design-done` on [[D-005]] ("apply a cut before the snap"). The command seals the design, then plans it.

1. **Seal — done and pushed.**
   - `5af1d20`: the D-005 draft, the README and its sequence diagram.
   - `bc1af2a`: D-005 confirmed. OQ1–OQ3 were resolved to their recommended defaults, and the D-005 back-links
     were stamped into SPEC-007 and SPEC-011.
2. **Auto-plan (`todo-task D-005`) — paused here.**
   - Fat specs drafted.
   - Deepen loop run.
   - Three new forks asked and answered.
   - The plan revised to match, then paused before its final review pass.

## What this pause lands

| Artifact | State | Why |
|---|---|---|
| [[G-057]] | epic, 4 tasks | carries the user's three decisions in § Decisions and a fixed invariant-id allocation in § Plan |
| [[T-173]] | `spec: needs-human` | revised to the user's answers after the panel; **not re-reviewed since** |
| [[T-174]] | `spec: needs-human` | **stale on Exact cut** — carries a warning banner at the top; the revision is spelled out in its build log |
| [[T-176]] | `spec: draft` | new — the older Exact-cut bug the panel found; never reviewed |
| [[T-175]] | terminal docs task | not panel-reviewed by design; updated to the shipped wording and the three implementation tasks |

The plan is **three independent roots**. T-173 (L), T-174 (M) and T-176 (S) each block T-175.

## What the review run bought

The run was `wf_419242f5-43c`. It used 58 agents over four rounds: five lenses (solution architect, tech lead, SDET,
application designer, product owner) plus a code-claim grounding lens, then two coherence passes. It was read-only —
the target tree stayed clean and the drafts untouched. Neither implementation task converged. Its findings changed
the plan:

- **D-005's classification rule would have raised false alarms.** Judging a scanning row against the 1 s
  `MinKeptSpan` floor calls rows invalid that a snap can still rescue. Before the scan, only an outro handle at or
  before its intro is certain. A no-outro intro past `Duration` can still turn Ready, because `Duration` never
  snaps.
- **Spec amendments D-005 missed.** SPEC-007 I20 and I24, and SPEC-011 I24, I26 and I57 (T-173). SPEC-011 I62 and
  I64 (T-174).
- **`(see the red rows)` is false for a scanning row.** Such a row is `Loading` (a muted `loading…` chip), not red.
  → the user's decision 3.
- **`AddOutro` kicks a grab unconditionally.** So T-174's gate belongs in the two request helpers, not in
  `OnHandleChanged`.
- **Under Exact cut, the "provisional" frame is the real cut frame.** Meanwhile today's chip shows the nearest
  keyframe after the scan. → the user's decision 1.
- **An older, separate bug.** Rows added after the Exact toggle, and outro handles added later, ignore Exact.
  → the user's decision 2, [[T-176]].
- **A `LoadFailed` row's handles stay pending forever.** Its chip keeps the placeholder.
- **Test-seam facts.**
  - The member is `InFlightGrabs`, not `InFlightGrab`.
  - A held scan fails through the fake's public `ScanGate.TrySetException`; T-173's planned `FailScanPaths` fake
    was unnecessary.
  - There is no held-scan harness under the `PumpContext`, because of the T-159 hang.
- **Two parallel landings could mint the same SPEC-011 id.** The freshness guard only counts lines, so it would
  stay green. Now fixed: T-173 = I156/I157, T-174 = I158, T-176 amends I91 in place. Both implementation tickets
  carry a land-time duplicate-id check.

## The user's decisions (2026-09-13, recorded in [[G-057]] § Decisions)

1. Thumbnails under Exact cut → **show the real cut frame**.
2. The older Exact-cut bug → **its own ticket in this epic** ([[T-176]]).
3. Invalid rows still scanning → **count them, say when they turn red**.

## What is unfinished

1. **T-174** — revise for decision 1; the exact steps are in its build log. The first of them checks whether
   `FfmpegThumbnailService` caches frames.
2. **T-173** — re-offer the revision to the panel.
3. **T-176** — its first panel review.
4. **One coherence pass** over G-057 and all four tasks, once they converge.

## Resume

Run **`todo-task G-057`**. A re-plan re-offers `needs-human` and `draft` specs to the review panel and leaves
converged ones alone. Revise T-174 first; its build log is the brief.

**Do not start building** — `todo-next-all` / `proceed G-057` — until T-173, T-174 and T-176 read
`spec: reviewed`.

Full review output (every lens verdict and finding, per round) sits in the session scratchpad as
`plan-d005/result.json`. It is **temporary**: everything the plan needs has been lifted into the tickets and
this note.
