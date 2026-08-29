# ADR 0019: A Bulk Cut row's selection is TWO properties — a bindable user intent and a read-only computed eligibility; one property could not be both

## Status

Accepted.

## Context

G-043 exists because of two user reports filed together: *"bulk cut is not proceed able to cut after
import multiple video to cut"* and *"bulk cut should have select all/dis select all for item list"*. Both
land on the same property. Since T-096 shipped the Bulk Cut list (design
[D-004](../design/D-004/README.md), [ADR 0015](0015-bulk-trim-reuses-split-single-segment.md)), one row
property answered two different questions:

```csharp
private bool _isEnabledByUser = true;
public bool IsEnabled
{
    get => _isEnabledByUser && !IsAutoDisabled;   // "will this row run?"     — computed
    set { if (_isEnabledByUser != value) { _isEnabledByUser = value; OnPropertyChanged(); } }
}                                                // "does the user want it?" — stored
private bool IsAutoDisabled => _loadFailed || (KeyframesReady && (IsNoOpTrim || !IsValidCut));
```

and the row checkbox bound straight to it, two-way: `IsChecked="{Binding IsEnabled, Mode=TwoWay}"`.

The forces in tension:

- **A freshly imported row is legitimately ineligible, and that is correct.** A row arrives with intro-end
  at 0 and no outro, so it keeps the whole file — `IsNoOpTrim`, therefore `IsAutoDisabled`, therefore the
  getter answers `false` for **every** row while the backing intent field is `true` for every row. The
  auto-exclusion rule is right and G-043 puts it explicitly out of scope; the list is *supposed* to come up
  with nothing runnable until cuts are set.
- **A two-way binding writes the setter and reads the getter.** When those two disagree, WPF has no way to
  find out: the control pushes the click in, nothing comes back, and it keeps rendering its own idea of the
  state until a `PropertyChanged` forces a re-read.
- **The `!=` idempotence guard is standard and correct everywhere else.** It is what
  `ObservableObject.SetProperty` does on every other property in the app
  ([ADR 0007](0007-hand-rolled-mvvm.md)) — it is not the defect. It is simply fatal on a property whose
  getter can already disagree with the field the guard compares.
- **The codebase already half-knew the two concepts were different.**
  `internal bool IsCheckedByUser => _isEnabledByUser;` existed from T-096, read by apply-to-all targeting
  alone — because apply-to-all deliberately wanted the raw intent rather than the computed value (SPEC-011
  **I22**, **I56**). The split existed internally and stopped at the UI boundary.
- **The Split screen has no equivalent problem to copy a solution from.**
  `SplitSegmentViewModel.IsSelected` is a plain observable bool, because a planned part is always
  producible — there is nothing to conflate. Bulk Cut is the first list whose rows can be ineligible for
  reasons the user did not choose.

**The failure, and why it was invisible.** A view-model-level diagnostic walked the reported journey before
any fix was written (recorded in T-127):

```
1. import 3 videos        -> every row IsEnabled=false, IsNoOpTrim=true, "Run bulk cut (0)"
2. row0.IsEnabled = true  -> getter STILL false, PropertyChanged fired 0x   <- the dead gesture
3. set intro=5s on row0   -> the row enables, "Run bulk cut (1)"            <- happy path fine
4. ApplyToAll             -> applied=2, "Run bulk cut (3)"                  <- fine
5. untick row1, then give it a real cut -> still excluded; ApplyToAll skips it
```

Ticking a not-yet-eligible row wrote `true` over an already-`true` field, so the guard short-circuited, so
**no `PropertyChanged` was raised at all** — and with nothing pushing the getter back to the target, the box
could sit **rendering ticked while the row was excluded**: a checkbox that lies, above a `Run bulk cut (0)`
that silently disagreed with it. The user's click could only ever move the property one way. Clicking twice
wrote `false`, which *did* take, and poisoned the row: it stayed excluded even after a real cut was set, and
apply-to-all skipped it entirely (it filters on the raw intent). Fiddling with the boxes — the natural
response to a click that appears to do nothing — silently dropped videos from the user's own batch.

The ticket's original claim, that Run was dead after a multi-file import, was too strong and is corrected
here: setting a cut on any row *did* enable the batch, and apply-to-all *did* fan it out. What was broken is
narrower and still real — the click was a dead gesture, and the second click was destructive.

## Decision

**Split the row's selection into two properties: a public, always-notifying INTENT that the control binds
to, and a read-only, derived ELIGIBILITY that the engine filters on.** They are deliberately not the same
property, and they must not be merged back.

- **(a) `IsCheckedByUser` is the intent, and the only thing the checkbox binds to.** The internal read-only
  getter T-096 kept for apply-to-all is promoted to a public, settable, two-way-bound property defaulting to
  `true`; the view binds `IsChecked="{Binding IsCheckedByUser, Mode=TwoWay}"`. **Nothing on the eligibility
  path ever writes it** — a failed probe (`MarkLoadFailed`), an in-flight scan, an out-of-range cut all
  leave intent untouched, so a row the app currently excludes keeps the user's answer and joins the batch
  the moment it becomes eligible (SPEC-011 **I94**).

- **(b) `IsEnabled` is read-only computed eligibility — `IsCheckedByUser && !IsAutoDisabled`, with no
  setter.** The auto-disable rule itself is unchanged
  (`_loadFailed || (KeyframesReady && (IsNoOpTrim || !IsValidCut))`; a still-scanning row is deliberately
  *not* auto-disabled, because `CanRunBatch` waits on it). Every engine-facing projection reads this and
  only this — `CanRunBatch`, the `Run bulk cut (N)` label, the row set handed to `BulkTrimEngine`, and the
  at-risk count in the replace-originals confirmation ([ADR 0017](0017-output-mode-replace-original.md)).
  Because it has no setter, nothing outside the row can force a row into the batch or overwrite what the
  user asked for (SPEC-011 **I96**).

- **(c) Every mutator of a derived input must re-raise the whole derived set.** The intent setter keeps its
  `!=` guard — harmless now precisely *because the control binds to the same property the guard compares*,
  so a same-value write is a genuine no-op instead of a swallowed gesture — and raises `IsCheckedByUser`,
  `IsEnabled`, `ExclusionReason` **and** `IsExcludedDespiteBeingChecked` together. The eligibility side
  publishes the same trio through `RecomputeAll`, which every handle move, scan completion, `MarkLoadFailed`
  and precision flip funnels into (SPEC-011 **I95**, **I99**). `StartKeyframeScanAsync` was the one mutator
  that did not: restarting a scan on an already-scanned row flipped `KeyframesReady` — and with it the whole
  eligibility projection — with nothing pushed to the view. It funnels through `RecomputeAll` now. That miss
  is the shape of this bug class, one call site away from the original.

- **(d) The ticked-but-excluded state is named and explained, never hidden.**
  `IsExcludedDespiteBeingChecked` (`IsCheckedByUser && IsAutoDisabled`) makes the previously invisible state
  a first-class property of the row, and `ExclusionReason` gives it words — `can't read this file`,
  `nothing to trim yet — set an intro or outro`, `intro and outro are too close — keep at least <N>s`,
  `cut is outside the video` — phrased as a **state, not an error**, because "nothing to trim yet" is the
  normal condition of a row you just imported. It is `null` whenever it does not apply: an unticked row, an
  eligible row, and a still-scanning row. The row renders it as a muted italic line collapsed while null,
  **and dims the whole card** (`Opacity` 0.55 under the `IsExcludedDespiteBeingChecked` trigger) — see the
  cost that buys in Consequences (SPEC-011 **I97**, **I98**).

- **(e) Eligibility is measured against the cut the engine will ACTUALLY make.** `EffectiveIntroEnd` /
  `EffectiveOutroStart` return `Requested` under `CutPrecision.Exact` and `Snapped` on the lossless path,
  and `IsValidCut` / `IsNoOpTrim` / `KeptDuration` are all computed from them — because `BuildBulkTrimItem`
  hands the engine the **requested** time and [ADR 0018](0018-smart-cut-exact-trimming.md)'s smart engine
  honours it exactly. Judged on the snapped value, an Exact-mode row whose request snaps back to 0 on a
  coarse grid would be excluded — and, since (d), told *"nothing to trim yet"* about a trim Exact mode
  performs correctly. `SetExactCut` therefore recomputes the entire derived set, not just the snap warning.

- **(f) Select all / select none write intent, never eligibility.** `SetAllItemsChecked(bool)` — behind
  `SelectAllItemsCommand` / `SelectNoItemsCommand`, both gated by `CanChangeSelection`
  (`Items.Count > 0 && !Operation.IsRunning`, mirroring `CanClear`) — sets `IsCheckedByUser` across every
  row and nothing else: no probe, no scan, no re-snap, no thumbnail grab. Select-all therefore ticks
  auto-excluded rows too; they stay excluded-with-a-reason until they have a real cut, which is also what
  makes *select all* compose with *apply profile → all* (SPEC-011 **I100**). The write suspends the per-row
  run-state fan-out and refreshes exactly once at the end, because `CanRunBatch` / `RunLabel` are themselves
  O(N) and each row raises two properties (**I101**).

**Alternatives rejected.** **Keep one property and drop the `!=` guard** — the write would notify, but the
getter would still answer `false`, so the box would visibly flip back under the user's finger; and an
unguarded setter re-enters the whole notify fan-out on every same-value write. **Bind the checkbox one-way
and route the click through a command** — it hides the symptom without separating the concepts: the row
still could not hold an intent ahead of eligibility, and apply-to-all still needs a target set to read.
**Relax the auto-exclusion rules so a fresh row is eligible** — explicitly out of scope in G-043, and a
no-op trim genuinely should not run: it would write a file identical to its source. **Untick or hide the
ineligible rows** — the same lie in the other direction, and it destroys the very intent apply-to-all
targets. **Disable the checkbox on an ineligible row** — the user could then not say *"include this one once
I set its cut"*, which is most of what a batch workflow is for.

## Consequences

**Positive**

- **The gesture is live, and the control can no longer render a state the view model disagrees with.** The
  property the box binds to is the same one the guard compares, and it always notifies —
  `BulkRowIntentTests` pins tick-after-import notifying and sticking, a fresh row becoming eligible with no
  further gesture, and an unticked row staying excluded after a real cut until it is re-ticked.
- **Toggling a box is pure view-model state.** No keyframe scan, no frame grab, no re-snap; the intent path
  notifies its own projection and never the cut-recompute pipeline (both asserted). Select-all over a large
  list is one pass of bool writes and a single refresh.
- **Intent survives ineligibility, so the gestures compose.** *Select all* followed by *apply profile → all*
  reaches the rows the user meant, including the ones with no cut set yet — which is the normal state of a
  fresh import.
- **The engine-facing filter is now impossible to write to.** `IsEnabled` has no setter, so no future caller
  can smuggle a row into the batch or clobber the user's answer; every gesture writes intent.
- **Exact-mode rows are judged on the time they will actually be cut at**, so the precision choice and the
  eligibility rules stop disagreeing about the same row.

**Negative**

- **A ticked row that will not run now renders ticked — and the UI has to carry that.** T-127's own
  acceptance list asked for *"no state exists where the checkbox renders ticked but the row is excluded"*;
  the shipped design makes that state **legal** and explains it instead, because the alternative was writing
  over the user's intent. The `ExclusionReason` line and the 0.55-opacity de-emphasis are therefore
  load-bearing, not decoration: drop either and the screen over-promises — N ticked boxes sitting above a
  `Run bulk cut (0)` with nothing saying that none of them will run.
- **The reason strings are a UI contract now.** They are hand-written per exclusion case and derived from
  the same predicates as the exclusion itself; a new auto-disable condition without a matching sentence
  falls through to the generic `cut is outside the video` and mislabels itself.
- **Two properties a word apart sit side by side, and the difference is invisible at the call site.**
  `IsCheckedByUser` and `IsEnabled` read as synonyms; nothing in the code says why they are separate, which
  makes "simplify these back into one" a plausible-looking refactor that reintroduces a shipped defect —
  **this ADR is the guard**. The XAML compounds it: the row binds `IsChecked` to the row's intent *and* the
  checkbox control's own WPF `IsEnabled` to `CanChangeSelection` — two unrelated meanings of one word on one
  element.
- **`RecomputeAll` is a hand-maintained list of `OnPropertyChanged` calls.** With no source generators
  ([ADR 0007](0007-hand-rolled-mvvm.md)), a new eligibility input that forgets to funnel through it is a
  silent stale-projection bug — the one `StartKeyframeScanAsync` carried until this change found it.
- **The fix is proven at the view-model level only.** The dead gesture, the poisoned row and the exclusion
  reasons are covered by `BulkRowIntentTests` / `BulkSelectAllTests`, but the symptom that made the defect
  *invisible* — the box rendering ticked while the row is excluded — is WPF two-way-binding behaviour that no
  view-model test can observe. T-127 shipped with that hand-verification criterion deliberately still open
  rather than quietly ticked.
- **ACCEPTED LIMITATION — intent still does double duty.** `IsCheckedByUser` decides run membership **and**
  is the target set `ApplyToAll` / `ApplyProfileToAll` write to (SPEC-011 **I22**, **I56**). Select-all
  therefore also widens what apply-to-all overwrites: ticking everything in order to *run* everything also
  makes every row a *paste* target. This predates G-043 and was consciously left alone — a third concept (a
  separate paste-target set) was not judged worth the surface for a list gesture. The UI states it rather
  than hiding it: the row tooltip reads *"Include this video in the batch. Also makes it a target for Apply
  to all / Apply profile"*, and both header buttons say the same in their own words.

**Forced follow-ons** (this decision *causes* these; they are not optional)

- **Every new input to eligibility must publish through `RecomputeAll`** (or raise the
  `IsEnabled` / `ExclusionReason` / `IsExcludedDespiteBeingChecked` trio itself). One that skips it is
  invisible to the view — the same class of bug, in a new place.
- **Every new gesture over rows must write intent, and `IsEnabled` must stay setterless.** The moment
  something writes eligibility, the two concepts are one again and the checkbox can lie again.
- **Every new auto-disable condition must ship its own `ExclusionReason` sentence**, or the row goes quiet
  about why it is not counted — which is this defect wearing different clothes.
- **The ticked-but-excluded rendering must survive UI reworks.** The reason line and the row de-emphasis are
  the price paid for not overwriting user intent; a redesign that drops them turns an honest state back into
  a lying checkbox.
- **Any future precision mode must extend `EffectiveIntroEnd` / `EffectiveOutroStart`**, or eligibility is
  judged against a cut point the engine will not use, and rows are excluded (or admitted) for a reason that
  is not true.
- **If a separate apply-to-all target set is ever introduced, the tooltips must change with it.** They are
  currently the only place the double duty of a tick is disclosed.
