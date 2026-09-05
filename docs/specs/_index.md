# Feature Specs — Index

Living-spec layer per [../standards/feature-spec-structure.md](../standards/feature-spec-structure.md).
One spec per feature; numbered invariants are the source `todo-automate` derives test cases from.

## Structure
- **Shape:** flat `docs/specs/SPEC-NNN-<slug>.md` (ids stable).
- **Template:** [_TEMPLATE.md](_TEMPLATE.md) · **Standard:** [../standards/feature-spec-structure.md](../standards/feature-spec-structure.md)
- **Deferred gaps** (view-only / native / needs-refactor): [_GAPS.md](_GAPS.md) → tracked by T-105.

## Specs

| Spec | Slug | Area | Invariants | Covered | Gaps |
|------|------|------|:--:|:--:|:--:|
| [SPEC-001](SPEC-001-stream-copy-split.md) | stream-copy-split | core | 47 | 46 | 1 |
| [SPEC-002](SPEC-002-bulk-trim-engine.md) | bulk-trim-engine | core | 60 | ? | ? |
| [SPEC-003](SPEC-003-join-concat.md) | join-concat | core | 31 | 30 | 1 |
| [SPEC-004](SPEC-004-media-probe.md) | media-probe | core | 32 | 32 | 0 |
| [SPEC-005](SPEC-005-thumbnail-service.md) | thumbnail-service | core | 25 | 25 | 0 |
| [SPEC-006](SPEC-006-waveform-service.md) | waveform-service | core | 23 | 23 | 0 |
| [SPEC-007](SPEC-007-cut-profiles.md) | cut-profiles | core | 99 | ? | ? |
| [SPEC-008](SPEC-008-operation-progress-eta.md) | operation-progress-eta | app | 45 | ? | ? |
| [SPEC-009](SPEC-009-app-settings.md) | app-settings | app | 28 | ? | ? |
| [SPEC-010](SPEC-010-split-screen.md) | split-screen | app | 67 | ? | ? |
| [SPEC-011](SPEC-011-bulk-cut-screen.md) | bulk-cut-screen | app | 147 | ? | ? |
| [SPEC-012](SPEC-012-join-screen.md) | join-screen | app | 37 | ? | ? |
| [SPEC-013](SPEC-013-preview-player.md) | preview-player | app | 52 | ? | ? |
| [SPEC-014](SPEC-014-timeline.md) | timeline | app | 30 | ? | ? |
| [SPEC-015](SPEC-015-app-shell-theming.md) | app-shell-theming | ui | 28 | 24 | 4 |
| **TOTAL** | | | **751** | **see note** | **see note** |

**Invariant counts recounted mechanically 2026-09-02** (T-153) — they had drifted **in both
directions** and are now generated from the spec files rather than hand-incremented:

  - SPEC-002: listed 53, actually 60 (+7)
  - SPEC-007: listed 72, actually 94 (+22)
  - SPEC-011: listed 102, actually 121 (+19)
  - SPEC-013: listed 48, actually 52 (+4)
  - SPEC-014: listed 35, actually 30 (-5)

Total documented invariants: **723** (633 before the 09-02 recount, 680 at that recount, +21 from
T-154/T-155/T-156 documented the same day, +22 on 09-04 from T-154's Split/Join half — SPEC-010 +8,
SPEC-012 +9, SPEC-011 +5; +4 more from T-157 — SPEC-010 +3, SPEC-008 +1; +3 on 09-05 from T-160 —
SPEC-011 I145-I147, the two-row footer; +5 from T-161 — SPEC-007 I95-I99, the scrollable profile bar; +9 from T-162 — SPEC-010 I52-I60, delete-original on Split; +7 from T-163 — SPEC-010 I61-I67, the auto layer). SPEC-010 and SPEC-012 drop to `?` coverage under the rule below; two of the
new invariants are knowingly uncovered and say so in place — the honest `accepted` flag on the drop
trace (SPEC-010 I47 / SPEC-012 I36) is set in WPF code-behind that needs a windowed harness, and the
DragOver boundary (SPEC-010 I48 / SPEC-012 I37) documents a region no drop event ever reaches, so
there is nothing to assert.

**The `Covered` / `Gaps` columns are NOT re-measured.** Where a spec's invariant count changed, those
cells read `?` — carrying the old arithmetic forward would state a coverage figure for invariants that did
not exist when coverage was measured. Establishing real numbers again needs another per-invariant audit
(the 2026-08-28 one is described below); that is deliberately its own ticket, because a guessed coverage
percentage is worse than an absent one.

The last honest measurement stands as: **98.5% of spec cases (606/615), measured 2026-08-29**, over the
615 invariants that existed then. The 9 uncovered at that measurement were exactly the accepted
not-unit-testable set in [_GAPS.md](_GAPS.md).

**The former SPEC-008 tripwire is now cleared.** This file used to warn that two of the four SPEC-008
additions (I42, reporting *while a run is in flight*; I44, the "starts no run, touches no run state"
guarantee) had no test. Both are covered —
`OperationViewModelTests.ReportFailure_WhileRunning_SetsErrorOnly_AndDoesNotDerailTheRun`,
`…_SurvivesTheRunsEnd_UntilTheNextRunClearsIt` and `…_StartsNoRunAndEndsNone_MarshallingNothing`. The
warning had gone stale in the reassuring direction, which is the worse one for a tripwire.

**A test now keeps the counts honest**: `SpecIndexFreshnessTests` recounts the invariants in every spec
file and fails when this table disagrees. The drift above accumulated silently over three days precisely
because nothing checked.

How this number moved, so it stays auditable:

| Date | Figure | How it was arrived at |
|---|--:|---|
| before 2026-08-28 | "99%" | **wrong** — hand-incremented alongside the specs; never measured |
| 2026-08-28 | 89% (547/615) | adversarial per-invariant audit, 9 agents reading every invariant against the real tests. All 9 slices returned OVER-STATED; **59 invariants documented as covered had no test** |
| 2026-08-29 | 98.5% (606/615) | those 59 closed across two generation passes — 44 high-confidence, then the remaining 30 medium/low gap entries |
| 2026-09-02 | *not stated* | **counts recounted mechanically (680 documented); coverage deliberately left unstated rather than re-derived by arithmetic** (T-153) |
| 2026-08-30 | 98.3% (622/633) | **derived, nothing re-measured** — +18 invariants documented after the audit (SPEC-007 +5 and SPEC-011 +9, both covered by the 34 tests that shipped with G-043/G-044; SPEC-008 +4 for `ReportFailure`, 2 of them uncovered), counted by hand and added to the 08-29 measurement |

Breakdown of the 615 documented invariants **as measured on 2026-08-29** (the 18 added since are
accounted for above, not here):

| | Count | Meaning |
|---|--:|---|
| Covered **and** `serves-spec`-tagged | ~261 | traceable end-to-end. **Derived, not measured** — 187 + the 74 gap closures, which assumes each closure landed on a previously-untagged invariant |
| Covered by an **untagged** test | ~345 | the balance of 606. Real coverage; the test predates the trait convention — a traceability gap, not a coverage gap |
| **Genuinely uncovered** | 0 | closed 2026-08-29 |
| Not unit-testable (accepted) | 9 | see [_GAPS.md](_GAPS.md) — WPF render / MediaElement / native / unreachable guards |

The 74 gap entries that closed the 59 invariants split into **59 whole-invariant gaps** and **15 load-bearing
clauses** of invariants the audit already counted as covered (e.g. an invariant tested for the intro handle
but not the outro branch; a defensive-copy tested on the fresh-compute path but not the cache-hit path).
Closing a clause does not move the count but is where the `AppSettings` profile data-loss bug was
found (an out-of-try conversion that let one bad row wipe every saved profile).

> **How much to trust 98.5%.** The 606 comes from the audit's own per-invariant accounting applied to the
> closed gaps — it is arithmetic on a measurement, not a fresh measurement. Test *existence* was verified
> for all 74; test *sensitivity* was spot-checked by mutation testing on 3 (drop the stray-`.tmp` sweep in
> `AppSettings.Save`; drop `ClearSeekHold()` from `PlayerViewModel.Stop`; relax the segment boundary filter
> from strict to inclusive) — **3 mutations, 3 kills**. A full mutation run across the suite is the honest
> next step and has not been done.

> **The per-spec `Covered` column below is a documentation artifact, not a measurement.** Treat the table's
> `Invariants` count as authoritative and the coverage figure above as the real number. Re-run the
> verification (not a hand-count) before changing it.

Generated by `todo-automate` (bootstrap 2026-08-22): 126 gap tests added across 3 test-gen passes;
+14 dual-assertion perf tests, T-105's +25 make-testable tests, the G-041/G-042 epics, and the G-043/G-044
fixes (+14 `BulkRowIntentTests`, +12 `BulkSelectAllTests`, +8 `BulkCutProfileThumbnailTests`) -> 1111 tests
green (688 App + 423 Core, counted 2026-08-30).
