# ADR 0018: Frame-exact ("smart") cutting as a separate opt-in engine — re-encode one head GOP, copy the rest; stream copy stays the default

## Status

Accepted.

## Context

Epic G-042 exists because of a user report: they pressed *"Set intro-end here"* at 5s and the app cut at
4s; they moved the playhead to 6s, pressed again, and still got 4s. Nothing was broken — that is
[ADR 0001](0001-stream-copy-only.md) working exactly as designed. A `-c copy` segment **must start on a
keyframe**, so the only reachable cut points are the keyframes; `MediaProbe.SnapToNearestKeyframe`
(`src/Core/Media/MediaProbe.cs`) picks the nearest one and breaks ties **toward the earlier** keyframe. On
a ~4s keyframe grid that makes 5s → 4s (nearest) *and* 6s → 4s (a 4s/8s tie, broken earlier) — so moving
the playhead by a whole second changed nothing the user could see.

The previous epic, G-041, made that snap **visible**: each row now shows the requested time and the
keyframe it actually landed on (`CutMarkerViewModel.SnapNote` / `HasSnapNote`). That was the right first
step — a silent wrong number is worse than a visible one — but it answers a different question. The user
does not want the offset explained; they want the cut at 5s.

No snap *policy* can give them that. G-042 tabulates the whole option space against the reported case:

```
nearest (today)   5s -> 4s   6s -> 4s     leaves 1-2s of intro behind
ceil / forward    5s -> 8s   6s -> 8s     removes 3s of real content
exact             5s -> 5s   6s -> 6s     needs the first GOP re-encoded
```

The forces in tension:

- **Lossless stream copy is the product's identity, not a preference.** [ADR 0001](0001-stream-copy-only.md)
  makes `-c copy` a runtime-enforced invariant on split and join, and G-042's success criteria keep it as
  the **default** — *"no regression to stream-copy speed or quality for anyone who stays on the default."*
  Whatever answers the report has to be additive and opt-in.
- **The nearest-keyframe snap is correct and load-bearing.** It is what makes the copy legal, and it feeds
  the split plan (`SplitPlan.InteriorSnappedCuts`), the bulk kept-segment selection
  ([ADR 0015](0015-bulk-trim-reuses-split-single-segment.md)) and the cut profiles
  ([ADR 0016](0016-shared-bulk-preview-player-and-cut-profiles.md)). G-042 puts the Core snap contract and
  the ties-to-earlier rule explicitly **out of scope** — both stay right for the lossless path.
- **A re-encode cannot live on the split path.** Honouring 5s exactly means decoding and re-encoding the
  fragment before the next keyframe, which means emitting `-c:v libx264` / `-c:a aac` — precisely the
  tokens `SplitArgsBuilder.ForbiddenEncoderTokens` denies and `SplitEngine.AssertCopyInvariant` refuses to
  launch. That refusal is the guarantee working, not an obstacle to route around.
- **Re-encoding the whole file is not an option.** It would be slow, lossy end to end, and would break the
  output-size assumptions the disk pre-flight and the join estimate rely on. The cost has to stay bounded
  by the GOP that contains the request.
- **A concat is valid only across parameter-identical inputs.** The same rule that forces `JoinEngine`'s
  compatibility pre-flight ([ADR 0001](0001-stream-copy-only.md) → `CompatChecker`) applies to a
  re-encoded head being joined to a copied tail: a parameter mismatch does not fail at build time, it
  fails — or silently corrupts — at concat time.

## Decision

Adopt **smart cutting as a separate, opt-in engine**: re-encode only the fragment between the requested
time and the next keyframe, stream-copy everything after it, and concatenate the two — while
keyframe-snapped stream copy remains the default for every batch.

- **(a) A new engine beside `SplitEngine`, never inside it.** `ISmartCutEngine` / `SmartCutEngine`
  (`src/Core/Split/SmartCutEngine.cs`) is a separate Core service. **`SplitEngine` is untouched**: its
  `-c copy` builders, its `SatisfiesCopyInvariant` assertion before every launch, and its SPEC-001
  guarantees are exactly what they were, so [ADR 0001](0001-stream-copy-only.md) stays *literally* true
  rather than true-with-an-asterisk. The head command deliberately carries encoder tokens; it could not
  pass through `SplitArgsBuilder` even if someone tried.

- **(b) The shape of a cut is decided by a pure planner.** `SmartCutPlanner.Plan(start, end, keyframes)`
  (`src/Core/Split/SmartCutPlanner.cs`) returns one of three strategies and touches no I/O:
  **`PureCopy`** when the request already sits on a keyframe (within `OnKeyframeTolerance`, 10 ms) — the
  lossless path already produces exactly this, so nothing is re-encoded; **`HeadReencode`** with
  `HeadEnd` = the first keyframe strictly after the request — the head `[Start, HeadEnd)` is re-encoded and
  the tail `[HeadEnd, End)` is copied; **`FullReencode`** when no keyframe lies strictly between start and
  end — there is no copyable tail, and the re-encoded range is by construction shorter than one GOP.
  `Start` is **never moved** — honouring it exactly is the entire point — and `ReencodedDuration` states
  the cost the user actually pays.

- **(c) Head re-encode → tail copy → concat, in exactly three ffmpeg runs.** `SmartCutArgsBuilder`
  (`src/Core/Split/SmartCutArgsBuilder.cs`) builds the head with an **OUTPUT seek** (`-ss` *after* `-i`),
  which decodes from the previous keyframe and starts the output on the exact requested frame — the decode
  cost is accepted because it is bounded by one GOP. The tail is not a new command at all: `TailCopy`
  **delegates to `SplitArgsBuilder.PerSegment`**, so the remainder is byte-for-byte what the lossless path
  would have written. The join reuses `JoinArgsBuilder.ConcatCopy` rather than a second concat
  implementation. That is one encode + one copy + one concat — three invocations for a `HeadReencode`, one
  for a `FullReencode`, never one per GOP (recorded as invariants I40–I48 in
  [SPEC-001](../specs/SPEC-001-stream-copy-split.md)). Every intermediate lives in a
  `.vsj-smartcut-<guid>` temp dir swept in a `finally`, and the result is moved into place only once it
  exists — the same temp-then-move contract `SplitEngine` holds ([ADR 0003](0003-cancel-safety.md)).

- **(d) Encode parameters are read from the source's own probe, never assumed.**
  `SmartCutArgsBuilder.HeadReencode` reproduces what the concat demuxer keys on, taken from the `MediaInfo`
  the engine already probed: video codec → encoder (`h264`→`libx264`, `hevc`→`libx265`,
  `vp9`→`libvpx-vp9`, `av1`→`libsvtav1`, …) plus `-pix_fmt` and `-s <w>x<h>`; audio codec → encoder
  (`aac`→`aac`, `mp3`→`libmp3lame`, `opus`→`libopus`, …) plus `-ar` and `-ac`. A mismatch here is the main
  failure mode, so it is resolved up front instead of discovered at concat time.

- **(e) An unmappable source FALLS BACK with a stated reason — it is never guessed at.**
  `TryResolveEncoders` returns `false` with a reason (*"no known encoder for video codec 'prores_raw_hq'"*,
  or *"the source has no video or audio streams"*) rather than picking a plausible encoder, and
  `SmartCutEngine` turns that into a `SmartCutResult` with `FellBack = true` and no output. `PureCopy`
  reports `FellBack` too — the lossless path is already exact there, so re-encoding would buy nothing.
  `BulkTrimEngine` (`src/Core/Bulk/BulkTrimEngine.cs`) routes an `Exact` row to the smart engine and, on
  `FellBack`, runs the ordinary lossless split for that row instead, surfacing
  `exact cut unavailable (<reason>) - cut snapped to the nearest keyframe` as a **row warning** —
  except for the `PureCopy` case, which needs no note because the result is exact either way. A batch
  runner constructed without an `ISmartCutEngine` simply stays lossless.

- **(f) `Lossless` is the default; `Exact` is an explicit choice whose cost is stated where it is made.**
  `CutPrecision` (`src/Core/Bulk/CutPrecision.cs`) is a **third axis** on `BulkTrimOptions`, orthogonal to
  both `CollisionPolicy` ("what if the destination is taken?") and `OutputMode` ("which destination?"), and
  it defaults to `CutPrecision.Lossless`. The UI is an "Exact cut" checkbox plus a bound `PrecisionNote`
  that names the trade-off in plain language — *"Exact — cuts land where you set them (re-encodes ~1s per
  cut)"* against *"Lossless — cuts snap to the nearest keyframe (instant, no quality loss)"*. Under `Exact`
  every row **stops advertising a snap offset**: `BulkItemViewModel.SetExactCut` sets each handle's
  `CutMarkerViewModel.SuppressSnapNote` and zeroes the row's worst-snap magnitude, so G-041's
  *"cut moved Xs to the nearest keyframe"* warning goes quiet too — that offset will not happen, and
  showing it would mislead. (The coarse-keyframe advisory is untouched; it still describes the source.)

**Alternatives rejected.** **Keep the nearest snap** — that *is* the reported defect. **Ceil / snap
forward to the next keyframe** — it fixes nothing: on a 4s grid 5s and 6s both become 8s, so the two
gestures stay indistinguishable *and* 3s of real content is now thrown away; wrong in the other direction
is worse, not better. **Re-encode the whole file** — unbounded cost, lossy end to end, the opposite of
what the app is for. **Add a re-encode branch inside `SplitEngine`** — it would have to weaken or
special-case `AssertCopyInvariant`, falsifying [ADR 0001](0001-stream-copy-only.md)'s central claim for
every caller, including the copy-only ones. **Make `Exact` the default** — explicitly out of scope in
G-042; the lossless promise is why the app exists.

## Consequences

**Positive**

- **The reported symptom is gone at the engine level.** A request at 5s on a 4s grid plans a head of
  `5s → 8s` and starts the output at 5s; a request at 6s plans a 2s head and starts at 6s — moving the
  playhead now genuinely changes the result (`SmartCutTests`).
- **ADR 0001 needs no amendment.** The copy invariant, its denylist and its runtime assert are unchanged,
  and the *only* re-encode in the product lives in a type `SplitEngine` neither calls nor knows about. A
  reader of ADR 0001 is not misled.
- **The cost is bounded and legible.** Roughly one GOP is re-encoded per cut and the other 99%+ of the
  output is untouched bytes; `ReencodedDuration` reports exactly how much was paid, and `PrecisionNote`
  states the trade before the run rather than after it.
- **Almost no new ffmpeg surface.** The tail leg *is* `SplitArgsBuilder.PerSegment` and the join *is*
  `JoinArgsBuilder.ConcatCopy`, so two of the three legs inherit shapes that are already tested and
  asserted; only the head command is new.
- **A source we cannot reproduce degrades instead of corrupting.** The fallback keeps the
  refuse-don't-corrupt posture: the row still gets a correct lossless cut, and the user is told why it was
  not exact.

**Negative**

- **There is now a second ffmpeg code path** — the very thing
  [ADR 0015](0015-bulk-trim-reuses-split-single-segment.md) went out of its way to avoid for bulk trim. It
  is justified only because the first path *must not* be able to do this, and it is kept as thin as
  possible (one new builder method; the other two legs reuse).
- **The encoder maps are enumerated, not exhaustive** — like ADR 0001's denylist. A codec outside
  `VideoEncoders`/`AudioEncoders` cannot be exact-cut at all; widening coverage means growing the maps
  deliberately, and the honest fallback is what makes deferring that safe.
- **The head fragment is genuinely re-encoded.** It is a generation of loss on ~1 GOP, and the builder
  matches only what the join keys on (codec, pixel format, resolution; audio codec, sample rate, channels)
  — profile/level, frame rate and rate control take the encoder's defaults rather than the source's.
- **ADR 0001's runtime assert has no jurisdiction here.** `SmartCutEngine` launches through `IFfmpegRunner`
  directly and never calls `AssertCopyInvariant`; the tail and concat legs are copy-shaped *by
  construction* (and unit-asserted with `SatisfiesCopyInvariant` on the built tokens), but nothing refuses
  at launch the way it does on the split and join paths — because the head leg must carry encoder tokens.
- **`Exact` + "Replace originals" does not get the replace-in-place safety net.** `SmartCutEngine`'s
  `MoveIntoPlace` deletes an existing destination and moves the finished file over it; it does not route
  through `SplitEngine.ReplaceOriginalInPlace` (`File.Replace` with a `.vsj-original` backup, rename-aside
  fallback, restore-on-failure, and the `IOriginalDisposer` that sends the backup to the Recycle Bin), so
  on an exact row the replaced original is not recoverable that way.
- **Per-row progress under `Exact` is coarse.** The engine reports at stage boundaries (0.5 after the head,
  0.8 after the tail, 1.0 after the concat) rather than streaming ffmpeg progress, so an exact row's bar
  moves in three steps.
- **Exact cutting is Bulk-Cut-only today.** `MainViewModel` hands a `SmartCutEngine` to `BulkCutViewModel`
  alone; the Split screen has no precision choice, so the same gesture there still snaps.

**Forced follow-ons** (this decision *causes* these; they are not optional)

- **`SplitEngine` must stay copy-only, forever.** Any future "just add an encoder flag there" would
  falsify ADR 0001 and SPEC-001 for every caller. Frame-exact work belongs in `SmartCutEngine` — or in
  another sibling engine — never in the copy path.
- **The tail must keep delegating to `SplitArgsBuilder.PerSegment`, and the join to
  `JoinArgsBuilder.ConcatCopy`.** The claim "the remainder is byte-identical to the lossless result" is
  structurally true only while those legs are literally the same code; a forked copy would turn a fact
  into a promise.
- **The fallback must always reach the user.** An exact-cut mode that quietly degraded to a snapped cut
  would be a worse version of the defect this epic fixes: the row warning and the `PrecisionNote` are part
  of the contract, not decoration.
- **Any surface that offers `Exact` must suppress the snap readout.** `SuppressSnapNote` exists because a
  displayed keyframe offset is untrue under exact cutting; a new precision-aware screen has to propagate
  it the way `BulkItemViewModel.SetExactCut` does.
- **If exact cutting is extended to the Split screen, or married more closely to replace-in-place, the
  destination contract must be shared rather than re-implemented** — verify-then-`File.Replace`-with-backup
  and the `IOriginalDisposer` seam live in `SplitEngine` today and would have to be lifted, not copied.
