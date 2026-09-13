# D-005 — Apply a cut before the snap

> Status: **draft** (opened 2026-09-13 via `todo-design`; stays draft until `todo-design-done`) · refines
> [D-004](../D-004/README.md) · amends [SPEC-007](../../specs/SPEC-007-cut-profiles.md) ·
> [SPEC-011](../../specs/SPEC-011-bulk-cut-screen.md) · sequence:
> [`./apply-before-snap-sequence.drawio`](./apply-before-snap-sequence.drawio)

---

## The question

> *"Before we can apply a profile cut, doesn't it require snapping first?"*

**Today, yes — but only by inheritance.** All three ways of copying a cut onto rows skip any row whose keyframe
scan has not finished. None of them reads keyframes. They write **requested** times, computed from the row's
`Duration`; the snap is derived from those requests afterwards. The only consumer of the snapped time is the run, and
the run already waits for it on its own. The skip dates from the first `ApplyToAll` (D-004 / T-096) and was never
decided.

| Path | Guard today | What the path actually reads |
|---|---|---|
| Profile apply — `CutProfileApplier.ApplyProfile` | `CutProfileApplier.cs:45` `!target.KeyframesReady → continue` | profile times · `target.Duration` (clamp, outro from end) |
| Row ⧉ apply-to-all — **source** | `BulkCutViewModel.cs:1490` `!source.KeyframesReady → null` | `source.IntroEnd.Requested` · `Duration − OutroStart.Requested` |
| Row ⧉ apply-to-all — **targets** | `BulkCutViewModel.cs:1503` `!target.KeyframesReady → continue` | `target.Duration` (outro from end) |
| Set-at-playhead fan-out (T-133) | `FanOutToCheckedRows` → `ApplyToAll` (`:1413`) | same as apply-to-all |

**What it costs the user today.** The Apply buttons stay enabled during scans (`CanApplyProfileToSelected` `:393`,
`CanApplyProfileToAll` `:396`). Drop 20 files, apply a profile while 17 still scan, and the app says
`Applied to 3 row(s).` The other 17 stay untouched, nothing on screen says why, and the user has to notice, wait,
and apply again.

## Decisions (clarify round 1 — 2026-09-13)

1. **When may a row take a cut?** → **once the row is probed** (its `Duration` is known). Unprobed and load-failed
   rows are still skipped.
2. **Which paths?** → **all three, one rule** — profile apply, row ⧉ apply-to-all and the set-at-playhead fan-out
   share one eligibility predicate and one outcome classification.

## Concept & mental model

A cut has two halves:

- the **request** — where the user or the profile says to cut. Pure time arithmetic against `Duration`.
- the **snap** — where a stream-copy cut will really land. Needs the keyframes.

Only the run needs the snap. So a row accepts the request as soon as it is probed, shows the snap as pending until
the scan resolves it, and the run keeps waiting for the snap exactly as it does now.

```
row lifecycle      unprobed ──probe──▶ probed · scanning ──scan lands──▶ ready
takes a cut?          no                  YES  (new)                      yes
handle reads          —                   "00:12.0 → snapping…"           "00:12.0 → 00:11.6 (−0.4s)"
Run can cut it?       no                  no   (unchanged)                yes
```

## Scope

**In**
- **R1** one eligibility predicate — *probed*, not *ready* — read by all three paths.
- **R2** the pending display is **already** guaranteed; the design now depends on it, so it gets an invariant and a
  test (no code change).
- **R3** run safety — unchanged code, now load-bearing, so it gets its own test.
- **R4** the apply report tells the truth: rows that will snap later, rows skipped as not loaded, and no false
  "invalid" alarm on a row that is only scanning.
- **R5** no frame grab at a provisional time.

**Out**
- Making the run stop waiting for keyframes — the snapped cut *is* the product, and `-c copy` needs keyframes.
- Scan prioritisation (scan the selected row first) — adjacent, separate idea.
- Re-issuing the aggregate apply line when the scans land (OQ3).
- Gating the Apply buttons on scan state — they stay enabled; that is the point.
- Split-tab markers — SPEC-010 I13 owns their pending path; untouched.

---

## The model

### R1 — eligibility: *probed*, not *ready*

The rule is spelled three times today, which is the drift "one rule" closes. State it once, on the row:

```csharp
// BulkItemViewModel
/// <summary>A row can take a cut once its duration is known; the snap may follow later (D-005).</summary>
public bool CanTakeCut => Duration is not null;
```

- `CutProfileApplier.cs:45` → `if (target is null || !target.CanTakeCut) continue;`
- `BulkCutViewModel.cs:1490` → `if (source is null || !source.CanTakeCut) return null;`
- `BulkCutViewModel.cs:1503` → `… || !target.IsCheckedByUser || !target.CanTakeCut`

`Duration` is assigned only on `ProbeSucceeded` (`PopulateAsync`, `:1194-1197`), so a row that failed to load never
has one. No separate load-failed term is needed.

### R2 — the pending display is already right (verified; pinned, not built)

A handle written during a scan must not present a provisional time as final. For every Bulk handle, the code already
guarantees that:

1. **Both handles are born pending.** `IntroEnd` (`BulkItemViewModel.cs:174`) and every `AddOutro` handle (`:262`) are
   constructed with `snapPending: true`.
2. **A write does not clear the flag.** The `Requested` setter calls `Resnap()`. With no keyframes yet that is an
   identity snap, and `Resnap()` never touches `IsSnapPending`. The flag survives any number of writes.
3. **Only a resolve clears it.** `IsSnapPending = false` exists only in `ResolveSnap()` (`CutMarkerViewModel.cs:179`).
   On a Bulk row that is called from:
   - `ScanBodyAsync` — success (`:728-729`) and failure (`:711-712`);
   - `AddOutro` — only when `KeyframesReady` (`:263-265`).

So a profile apply, apply-to-all, drag (`BulkRowScrubView.xaml.cs:451`) or IN/OUT edit during a scan reads
`→ snapping…` today. D-005 turns that from a rare path into a common one, so the guarantee becomes an invariant with
a test. The marker is not changed.

> Correction to an earlier note: I had flagged "a mid-scan write lands an identity snap without `IsSnapPending`".
> That is true only for a marker created *not* pending. No Bulk handle is created that way.

### R3 — run safety (unchanged, now load-bearing)

The run cuts at `EffectiveIntroEnd`, which is `Snapped` (or `Requested` under exact cut, `:366`). A pending `Snapped`
is provisional, and the run cannot reach it:

- `IsValidCut` returns false while `!KeyframesReady` (`:389-395`), so the row sits in `Loading`.
- `CanRunBatch` requires every enabled row to be `KeyframesReady` (SPEC-011 I37).

Before D-005 no apply could land during a scan, so nothing tests that such a cut is held back. After it, a test pins
it.

### R4 — the report tells the truth

**The trap.** Both copy loops classify after writing: `if (!target.IsValidCut) invalidated.Add(target)`, and
`IsValidCut` is false for **every** scanning row. Dropping the guard alone would report each row the user just fixed
as `now invalid (see the red rows)` — a false alarm on exactly the rows that worked.

**The classification** — one helper, used by both loops:

```csharp
applied++;
if (target.KeyframesReady)            { if (!target.IsValidCut) invalidated.Add(target); }  // as today
else if (!target.IsRequestedCutValid) { invalidated.Add(target); }                           // hopeless before any snap
else                                  { pendingSnap.Add(target); }                           // snaps when the scan lands
```

`IsRequestedCutValid` applies the `IsValidCut` bounds to what was *asked for*, without the keyframes gate. It is a
pure extract-method: `IsValidCut` keeps its exact behaviour, pinned by its existing tests.

```csharp
internal bool IsRequestedCutValid =>
    Duration is { } d && CutBoundsHold(IntroEnd.Requested, OutroStart?.Requested, d);

public bool IsValidCut =>
    KeyframesReady && Duration is { } d && CutBoundsHold(EffectiveIntroEnd, EffectiveOutroStart, d);

private bool CutBoundsHold(TimeSpan intro, TimeSpan? outro, TimeSpan duration)
{
    var upper = outro ?? duration;
    return intro >= TimeSpan.Zero && upper <= duration && intro < upper - MinKeptSpan;
}
```

`MinKeptSpan` is `max(1 s, AverageGop(Keyframes))` (`:379-386`), and `AverageGop` returns zero below two keyframes
(`MediaProbe.cs:366-371`). A scanning row is therefore judged against the 1 s floor. A row within one long GOP of the
limit can pass as pending and turn red after the snap: edge 11, OQ3.

**The record** (`BulkCutViewModel.cs:58`) gains the two facts it cannot express today:

```csharp
public sealed record ApplyToAllReport(
    int AppliedCount,
    IReadOnlyList<BulkItemViewModel> InvalidatedRows,
    IReadOnlyList<BulkItemViewModel> PendingSnapRows,   // applied; the snap lands when their scan does
    int SkippedNotLoadedCount);                          // targets with no Duration yet
```

**The wording** (`ApplyReportSummary`, `:547`). Parts are joined with ` · ` like the run-scope line. Today's two
strings stay unchanged when nothing is pending or skipped:

| Situation | Line |
|---|---|
| all ready, all valid | `Applied to 5 row(s).` *(unchanged)* |
| some invalid | `Applied to 5 row(s) · 1 now invalid (see the red rows).` *(unchanged)* |
| some still scanning | `Applied to 5 row(s) · 3 will snap when their scan finishes.` |
| both | `Applied to 5 row(s) · 1 now invalid (see the red rows) · 3 will snap when their scan finishes.` |
| a target not loaded yet | `Applied to 4 row(s) · 1 not loaded yet, skipped.` |
| select-apply on an unprobed row | `Applied to 0 row(s) · 1 not loaded yet, skipped.` *(today: a bare `Applied to 0 row(s).`)* |

"still scanning" is already the run-scope vocabulary (`:627-630`); "will snap" echoes the handle's own `→ snapping…`.

### R5 — no frame grab at a provisional time

`OnHandleChanged` (`BulkItemViewModel.cs:886`) grabs a frame on every `Snapped` change, and a pending write moves
`Snapped` to its identity time. So ffmpeg grabs a frame at a time the cut will not use, and a second grab follows
when the scan lands. A 20-row apply means 20 wasted grabs at the busiest moment. This already happens for a drag
during a scan; D-005 would multiply it.

```csharp
if (e.PropertyName == nameof(CutMarkerViewModel.Snapped) && sender is CutMarkerViewModel { IsSnapPending: false })
```

**Ordering check.** `ResolveSnap()` runs `Resnap()` *before* `IsSnapPending = false`, so the resolve's own `Snapped`
change is gated too. That is fine: both completion branches of `ScanBodyAsync` already call `RequestAllThumbnails()`
explicitly, so exactly one grab happens, at the resolved time.

---

## Behaviour — ordering (all on the UI thread)

`ScanBodyAsync` resumes on the captured context (`ConfigureAwait(true)`), and every writer — commands, drag, field —
runs on the UI thread. The completion block therefore runs as one synchronous unit:

`Keyframes = scanned` → `IsIndexingKeyframes = false` → `IntroEnd.ResolveSnap()` · `OutroStart?.ResolveSnap()` →
`RecomputeAll()` → `RequestAllThumbnails()`

- A write **before** the block keeps the handle pending, and the block resolves it against the real keyframes.
- A write **after** the block snaps synchronously against the real keyframes, and R4 classifies it exactly as today.

Nothing can land between `Keyframes = scanned` and the resolves.

## Edge cases

| # | Situation | Behaviour |
|---|---|---|
| 1 | Target not probed yet (`Duration` null) | skipped · counted in `SkippedNotLoadedCount` · untouched |
| 2 | Target failed to load | same as 1 — it never gets a `Duration` |
| 3 | Applied mid-scan; scan succeeds | pending → `ResolveSnap` snaps to the nearest keyframe → row leaves `Loading` → one frame grab |
| 4 | Applied mid-scan; scan **fails** | failure branch resolves to an identity snap (cut at the requested time, SPEC-010 I16 semantics); pending cleared; one grab |
| 5 | Applied mid-scan; row **removed** or list **cleared** | `CancelScan` (`:1218`, `:1246`) → the cancel branch clears the flag without `ResolveSnap`. The row is gone and nothing renders it. **Boundary:** `CancelScan` must stay reserved for rows leaving the list. A future caller that cancels a *surviving* row's scan must resolve its handles, or a pending handle sits on a `KeyframesReady` row whose `IsValidCut` can pass on an identity snap (SPEC-011 I159) |
| 6 | Scan superseded (`StartKeyframeScanAsync` again) | flag stays true, writes stay pending, the newest scan resolves. Only caller today: `PopulateAsync` `:1197`, once per row |
| 7 | Two applies mid-scan (profile A, then B) | still pending; the one resolve at scan end snaps B's times |
| 8 | Apply mid-scan, then drag mid-scan | last write wins; one resolve snaps the drag |
| 9 | Apply mid-scan with **Exact cut** on | `SuppressSnapNote` hides the note (T-125); the cut lands on `Requested` anyway; the run still waits for the scan |
| 10 | Apply-to-all **from** a scanning source | allowed — copies `Requested` and a `Duration`-relative tail; the source's own handles stay pending |
| 11 | Valid on request, invalid after the snap (a long GOP lifts `MinKeptSpan` above the 1 s floor, or the snap pulls intro across the limit) | reported pending; the row turns red on resolve; its chip is the truth; the apply line is not re-issued (OQ3) |
| 12 | Invalid on request (tail longer than the file, intro past outro) | reported `now invalid` immediately |
| 13 | Run pressed while applied rows still scan | `CanRunBatch` false; the run-scope line already says `N still scanning` |
| 14 | Save a profile from a row whose handles are pending | `BuildProfileFromRow` reads `Requested` (`CutProfileApplier.cs:94-99`) — unaffected |

## Invariants — spec amendments (written at build, not now)

| Spec | Id | Change |
|---|---|---|
| SPEC-007 | I25 | **amend** — rows without a probed `Duration` are skipped and not counted; rows still scanning keyframes **are** applied, and their handles stay snap-pending |
| SPEC-007 | I27 | **amend** — `AppliedCount` = probed rows applied; `PendingSnapRows` ⊆ applied; `SkippedNotLoadedCount` = targets without a `Duration` |
| SPEC-007 | I108 | **new** — outcome classification: ready rows by `IsValidCut`; scanning rows by `IsRequestedCutValid` → `InvalidatedRows`, else → `PendingSnapRows` |
| SPEC-011 | I21 | **amend** — `ApplyToAll` returns null only for a null or not-yet-probed source; a scanning source applies |
| SPEC-011 | I22 | **amend** — target filter = `IsCheckedByUser && CanTakeCut`; the source row is still skipped |
| SPEC-011 | I76 | **amend** — `→ snapping…` also holds for a cut **written** during the scan (apply, drag, field): `Resnap` never clears `IsSnapPending`; only `ResolveSnap` does |
| SPEC-011 | I156 | **new** — a cut applied during a scan never reaches the run: `IsValidCut` and `CanRunBatch` stay false until the scan resolves, and the run then uses the snapped time |
| SPEC-011 | I157 | **new** — no frame grab while a handle is snap-pending; scan completion grabs once, at the resolved time |
| SPEC-011 | I158 | **new** — `ApplyReportSummary` wording for pending and not-loaded counts (R4 table) |
| SPEC-011 | I159 | **new** — `CancelScan` is reserved for rows leaving the list (edge 5) |

## Test plan — every new guard ships with the mutation that kills it

The fake probe already parks scans — `probe.GatedPaths.Add(path)` … `probe.ReleaseScans()`
(`BulkSpecGapTests.cs:202`, `:224`). The thumbnail fake already counts grabs —
`FakeThumbnailService.GetThumbnailCallCount` (`BulkTestFakes.cs:239`). No new test infrastructure.

**Changed**

| Test | Today | After D-005 |
|---|---|---|
| `BulkSpecGapTests.ApplyToAll_NullOrNotReadySource_ReturnsNull_MutatesNothing` (`:197`) | a scanning source → null | split: null or unprobed source → null; a scanning source applies |
| `BulkSpecGapTests.ApplyToAll_SkipsTheSource_UncheckedRows_AndStillIndexingRows` (`:245`) | still-indexing `c` untouched, `AppliedCount` 1 | `c` applied (`Requested` 12 s, still pending), `AppliedCount` 2, `PendingSnapRows` = [c]; renamed |
| `CutProfileApplierTests.ApplyProfile_SkipsNotReadyRows_NotCountedAsApplied` (`:87`) | its row is **unprobed** | still true — renamed `…SkipsNotLoadedRows…`, asserts `SkippedNotLoadedCount` 1 |

**New**

| Test | Kills the mutation |
|---|---|
| `ApplyProfile_ToScanningRow_AppliesAndStaysPending` | restore `!KeyframesReady` in the applier guard |
| `ApplyToAll_ToScanningTarget_AppliesAndStaysPending` | restore it in the target filter |
| `ApplyToAll_FromScanningSource_Applies` | restore it in the source guard |
| `SetIntroAtPlayhead_FanOut_ReachesScanningRows` | a fan-out that bypasses `ApplyToAll` with its own guard |
| `WriteDuringScan_KeepsSnapPending_UntilScanLands` | `Resnap()` clearing `IsSnapPending` |
| `ScanLands_ResolvesAppliedCutToNearestKeyframe` | drop `ResolveSnap` from the success branch |
| `ScanFails_ResolvesAppliedCutToIdentity` | drop `ResolveSnap` from the failure branch |
| `AppliedDuringScan_RunStaysDisabled_UntilScanLands` | `IsValidCut` without its `KeyframesReady` term |
| `Report_ScanningValidRow_IsPending_NotInvalid` | classify scanning rows with `IsValidCut` (the trap) |
| `Report_ScanningHopelessRow_IsInvalid` | send every scanning row to pending |
| `Report_NotLoadedTarget_IsCountedSkipped` | drop the skip counter |
| `Summary_PendingAndSkipped_Wording` (theory over the R4 table) | each wording, ordering or separator mutation |
| `PendingHandle_GrabsNoFrame_ScanLandingGrabsOnce` | drop the `IsSnapPending` gate in `OnHandleChanged` |
| the existing `IsValidCut` suite, unchanged | a `CutBoundsHold` extract that drifts from the old formula |

## Open decisions (draft — resolve or carry at `todo-design-done`)

| # | Fork | Recommended | Alternative |
|---|---|---|---|
| OQ1 | Report scanning and not-loaded rows in the apply line | **Yes — the R4 wording** | keep `Applied to N row(s).` and let the row chips speak |
| OQ2 | Frame grabs while a handle is pending | **Defer to the one grab at scan end (R5)** | keep grabbing twice (today's drag behaviour) |
| OQ3 | A row reported pending turns invalid after the snap | **Row chip only — the apply line is a snapshot of the click** | re-issue the apply line when the last pending row of that apply resolves |

## Risks & unknowns

- **Visible behaviour change.** Apply now changes rows that are still scanning, and many rows can show
  `→ snapping…` at once. Intended, but new.
- **Borderline invalidation after the snap** (edge 11) — at most one GOP past the 1 s floor; the row chip covers it.
- **Latent trap** (edge 5) — pending handles on a cancelled-but-surviving row. Unreachable today; pinned as SPEC-011
  I159 so it cannot arrive silently.
- **Spec/test id drift found while reading.** `BulkSpecGapTests` comments cite `SPEC-011#I20` for the source guard
  (the spec numbers it I21) and `#I22` for the clear-outro mirror (the spec's I23). Fix alongside the amendments.

## Diagram

[`./apply-before-snap-sequence.drawio`](./apply-before-snap-sequence.drawio) is a sequence diagram, laid out as a
**ROAD** plan: time runs down, and the order of participants across the page is the only free choice. Its pivot is
the **Handle** (`CutMarkerViewModel`), the point where the request and the snap come apart. Frame ① is a profile
applied while the row scans; frame ② is the scan landing later.

It is generated by a script and gated by `kit_coverage_check.py`, `check_layout.py` (report profile) and
`check_diagram.py`. There is no draw.io CLI on this machine, so the check is structural only.
