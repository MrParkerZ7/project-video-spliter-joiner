# ADR 0024: Delete-original eligibility is decided per screen, not by a shared abstraction

## Status

Accepted — 2026-09-06 (T-162 / G-052)

## Context

Two screens can now send a user's original to the Recycle Bin: Bulk Cut (G-050, 2026-09-01) and Split
(G-052, this epic). Both are irreversible-adjacent, both are gated, both end in the same
`IOriginalDisposer`. The obvious instinct — and the instruction written into T-162's own spec — was
*"prefer extracting the shared part over copying it"*.

Most of that instinct was right, and was followed. Three things **were** extracted:

- **`App/Io/FileFacts`** — `Exists` / `IsNonEmpty` / `Same`, the never-throwing disk predicates both
  screens ask. Each one guards an irreversible action, so two subtly different answers to "is this file
  really there?" on two screens is a deleted original on one of them.
- **`DangerButton`** — the destructive-control style, promoted out of `BulkCutView` into
  `Themes/Controls.xaml` when Split needed the same vocabulary.
- **The Recycle-Bin mechanism itself** — already shared via `IOriginalDisposer` / `ShellRecycleBin`
  (ADR-0022) and reused untouched.

What remained was the part that looked most like duplication and is not: **the eligibility rule and the
sweep around it.**

## The two rules are different questions

| | Bulk Cut | Split |
|---|---|---|
| unit | one **row**, repeated | one **source** |
| outputs | one per row | **N parts** from the one source |
| the question | *is this row's output on disk and non-empty?* | *are **ALL** the parts on disk and non-empty?* |
| failure isolation | per row — one refusal never stops the rest | none to do — it is a single all-or-nothing decision |
| reporting | "Sent 4 to the Recycle Bin. Still in use: …" over a set | one file, one outcome |
| the degenerate case | replace-originals made the output the original | Split's output folder **defaults to the source's own** |

The consequence of getting Split's version wrong is not a cosmetic difference: binning a 4 GB source
while part 4 of 6 is missing destroys footage that exists nowhere else, because the source is the only
copy of the material those parts were cut from. Bulk Cut has no equivalent — a failed row simply keeps
its own original.

## Decision

**Keep two eligibility rules, one per screen. Share the predicates they are built from, not the rule.**

A shared abstraction over these would need: a collection or a single item, a per-item output or a set of
outputs, all-or-nothing versus per-item isolation, and two different reporting shapes. That is four
parameters and two strategy hooks to express "check some files exist, then delete something" — a
generalisation that reads worse than either concrete version and hides the one line that actually
matters in each (`row.OutputPath` vs. *every* `segment.Path`).

This is the code least improved by cleverness. The rule each screen enforces should be readable in one
screenful, by someone deciding whether it is safe.

## Consequences

**Good**

- Each rule states its own invariant plainly, next to the screen it protects. SPEC-011 I122-onward and
  SPEC-010 I52-I60 can each be read against one method.
- The all-parts rule — the thing this epic hangs on — is not buried behind a strategy parameter.
- The genuinely common parts are still single-sourced, so a fix to "does this file exist" reaches both.

**Bad / accepted**

- Two methods do superficially similar work, and a future reader may reach for the same instinct T-162's
  spec did. That is what this ADR is for.
- A change to the *shape* of the deletion flow (say, a retry policy) has to be made twice. Accepted: the
  mechanism that would actually carry such a policy — `ShellRecycleBin` — is already shared, so what
  would be duplicated is only the call-site.

## Alternatives considered

- **One `DeleteOriginalsFor(items, outputsOf, isolation, report)` helper.** The honest version of the
  extraction, and rejected on readability: every caller would pass lambdas describing what the other
  caller does differently, which is a sign the abstraction is tracking the wrong seam.
- **Make Split reuse Bulk's per-row rule with a one-row collection.** Superficially neat and wrong in the
  dangerous direction: it asks "is this row's output present?", and Split's source has N outputs, so the
  all-parts rule would have to be smuggled in anyway — with the risk that a future edit "simplifies" it
  back out.
- **Defer and share later, once a third screen wants it.** Reasonable, and effectively what happened for
  `FileFacts` and `DangerButton` — both were extracted at the moment the second caller appeared. The
  eligibility rule is the piece where the second caller demonstrated the shapes *differ* rather than
  repeat, which is the opposite signal.
