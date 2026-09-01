# ⏸ Pause — 2026-09-02, `todo-next-flow` drain

Paused by `todo-pause` at a **clean ticket boundary** (stop-point kind **a**). Nothing was mid-edit,
nothing mid-flight, no ticket left `in-progress`, tree clean at `a959926`, everything pushed.

## What this drain landed (kept, done)

| ticket | outcome |
|---|---|
| **T-148** | Three suites now clear the `SynchronizationContext` they install, at all 25 install sites — `PumpContext` is `IDisposable` and each site binds `using var pumpScope = pump;`. Flake rate 0/20 → 0/20 on verified-clean builds. Commit `711a941`. |
| **T-146** | Reopened by review and genuinely fixed: the destructive "Delete originals" button was laid out **on Run's pixels**. Footer regrid + a layout test at five widths. Commit `a959926`. |

## Why the drain did not simply converge

The `todo-next-flow` run put three adversarial lenses on T-148's result. **Two of three refuted it** —
not the code, but its claims. Acting on that is what produced the second half of the work:

- **Mechanism misattributed.** `AsyncTaskMethodBuilder.Start` restores the caller's context, not xUnit's
  machinery — so the leak is structurally impossible at the async sites and "18 of 25 load-bearing"
  overstates it.
- **Unguarded clear.** `PumpContext.Dispose()` nulled the ambient context unconditionally, tearing out
  xUnit's own context at the async sites — the exact action the ticket gives as its reason for not
  touching the six conforming suites. Now guarded with `ReferenceEquals`.
- **Binary-hash evidence retracted.** Builds here are deterministic and the claimed "after" md5 cannot be
  reproduced from committed source; probe instrumentation was most likely still compiled in.

And the discovery pass found a **shipped user-facing defect hiding in a ticked criterion** — T-146's
"not adjacent to Run" had been ticked from the XAML comment rather than the markup.

## Resume — no new verb

Any of `todo-next-all` / `todo-next-flow` / `proceed` picks the queue up. Nothing is paused mid-ticket,
so there is no per-ticket resume brief to read; the queue below is simply the next work.

```
T-149  high    next    CI release gate is vacuously green — 43 ffmpeg tests early-return as PASSED
T-151  high    next    Cut v1.1.1 — 17 unreleased commits; no git tag has EVER existed  (blocked-by T-149)
T-150  medium  next    SPEC-011 I118 deletion-time re-check is enforced by no test
T-152  medium  after   Nine done tickets each carry one unticked hand-verification criterion
T-153  low     after   Spec index 58 invariants stale; two docs deny a test that exists
```

**Suggested order.** T-149 before T-151 — releasing behind a gate that cannot fail is worse than not
releasing, and T-151 is already blocked on it. T-150 is small, independent, and closes a real
data-loss window (binning an original whose output vanished between confirm and sweep).

Suite at pause: **1303 green** on a `--no-incremental` build.
