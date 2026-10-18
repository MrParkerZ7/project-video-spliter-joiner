# Standard: Feature-Spec Structure (living specs)

The `docs/specs/` layer is the app's **living-spec contract**: one spec per feature, stating the feature's
**current behavior + invariants** in code-grounded terms, with each automated test traceable back to the
invariant it verifies (`serves-spec:`). Specs are the source `todo-automate` derives test cases from.

## Structure

- **Shape:** flat — `docs/specs/SPEC-NNN-<slug>.md` (one file per feature; `NNN` zero-padded, ids stable).
- **Index:** `docs/specs/_index.md` lists every spec (id · slug · area · one-line) + this `## Structure` declaration.
- **Template:** `docs/specs/_TEMPLATE.md` — the section skeleton every spec follows.

## Required sections (every SPEC-NNN)

```
---
id: SPEC-NNN
slug: <kebab-slug>
area: core | app | ui        # coarse grouping
title: <feature name>
status: current | designed   # designed = documented-not-built (honest stub, no fabricated tests)
sources: [src/... paths the spec is grounded in]
serves-goal: [G-NNN, …]      # the board goals this feature came from (optional)
updated: <YYYY-MM-DD>
---

## What        — one paragraph: what the feature does (user- or contract-facing).
## Why         — why it exists / the problem it solves.
## Scope        — in / out (what this spec covers vs adjacent specs).
## Current behavior & invariants   — NUMBERED invariants (I1, I2, …), each a testable rule grounded in the
                                     cited code. This is the case source for todo-automate.
## Links        — related SPEC-NNN · the D-NNN design (if any) · the G-NNN goals · key src/ files.
```

## Invariant rules
- **Grounded, never invented** — every invariant traces to real code (cite the file/type). A feature that is
  *designed but not built* gets `status: designed` + the invariants it *will* have, and **no test is fabricated** for it.
- **Numbered + atomic** — one rule per invariant (`I1 …`, `I2 …`) so a test can cite exactly which it verifies.
- **Testable** — phrased as a checkable behavior (input → guaranteed output / property), not prose.

## Test traceability (`serves-spec:`)
- Each automated test that verifies an invariant carries a **`serves-spec: SPEC-NNN[#I<k>]`** marker (an
  xUnit `[Trait("serves-spec", "SPEC-NNN")]` or a `// serves-spec: SPEC-NNN#I3` comment on the test).
- `todo-automate` derives each spec's Case-Coverage Matrix (Required-Success · Required-Fail · Optional ·
  boundary) and reports `Tests: N% of spec cases covered`; a documented invariant with no test is a **gap**,
  never silenced by weakening an assertion.

## Currency
- A spec whose `## Current behavior` drifts from the code it cites is **stale** — refresh it from the code.
- `todo-automate specs` bulk-authors/refreshes this layer; the per-land spec-sync keeps one feature current.
