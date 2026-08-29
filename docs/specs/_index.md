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
| [SPEC-002](SPEC-002-bulk-trim-engine.md) | bulk-trim-engine | core | 53 | 53 | 0 |
| [SPEC-003](SPEC-003-join-concat.md) | join-concat | core | 31 | 30 | 1 |
| [SPEC-004](SPEC-004-media-probe.md) | media-probe | core | 32 | 32 | 0 |
| [SPEC-005](SPEC-005-thumbnail-service.md) | thumbnail-service | core | 25 | 25 | 0 |
| [SPEC-006](SPEC-006-waveform-service.md) | waveform-service | core | 23 | 23 | 0 |
| [SPEC-007](SPEC-007-cut-profiles.md) | cut-profiles | core | 72 | 72 | 0 |
| [SPEC-008](SPEC-008-operation-progress-eta.md) | operation-progress-eta | app | 44 | 42 | 2 |
| [SPEC-009](SPEC-009-app-settings.md) | app-settings | app | 25 | 25 | 0 |
| [SPEC-010](SPEC-010-split-screen.md) | split-screen | app | 40 | 40 | 0 |
| [SPEC-011](SPEC-011-bulk-cut-screen.md) | bulk-cut-screen | app | 102 | 102 | 0 |
| [SPEC-012](SPEC-012-join-screen.md) | join-screen | app | 28 | 28 | 0 |
| [SPEC-013](SPEC-013-preview-player.md) | preview-player | app | 48 | 46 | 2 |
| [SPEC-014](SPEC-014-timeline.md) | timeline | app | 35 | 34 | 1 |
| [SPEC-015](SPEC-015-app-shell-theming.md) | app-shell-theming | ui | 28 | 24 | 4 |
| **TOTAL** | | | **633** | **622** | **11** |

**Coverage: 98.5% of spec cases** (606/615), measured 2026-08-29. The 9 uncovered at that measurement are
exactly the accepted not-unit-testable set in [_GAPS.md](_GAPS.md) — nothing was merely *untested*.

**Since that measurement the table reads 622/633 (98.3%) — arithmetic, not a re-measurement.** Eighteen
invariants were documented after the audit ran: **+5 on SPEC-007** (I68–I72, the explicit thumbnail upload
reporting its failures) and **+9 on SPEC-011** (I94–I102, row intent vs computed eligibility, select
all/none) — both landed *with* their tests in the G-043/G-044 commit — plus **+4 on SPEC-008** (I41–I44,
the `ReportFailure` seam those upload messages travel through, documented 2026-08-30). The per-spec
`Invariants` figures above are a hand-count of the documented invariants taken 2026-08-30; a later spec
edit re-stales them.

Two of the four SPEC-008 additions have **no test**: reporting *while a run is in flight*, and the "starts
no run, touches no run state" guarantee. So the *nothing merely untested* line above no longer holds —
those 2 are untested, not untestable, and deliberately NOT in [_GAPS.md](_GAPS.md).

How this number moved, so it stays auditable:

| Date | Figure | How it was arrived at |
|---|--:|---|
| before 2026-08-28 | "99%" | **wrong** — hand-incremented alongside the specs; never measured |
| 2026-08-28 | 89% (547/615) | adversarial per-invariant audit, 9 agents reading every invariant against the real tests. All 9 slices returned OVER-STATED; **59 invariants documented as covered had no test** |
| 2026-08-29 | 98.5% (606/615) | those 59 closed across two generation passes — 44 high-confidence, then the remaining 30 medium/low gap entries |
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
