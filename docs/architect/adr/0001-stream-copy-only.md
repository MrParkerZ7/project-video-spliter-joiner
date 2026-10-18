# ADR 0001: Lossless stream-copy invariant (+ keyframe-snap & join-compat consequences, runtime-enforced denylist)

## Status

Accepted.

## Context

VideoSplitJoiner exists to split and join video **without re-encoding** — that is
the product's entire reason to exist (`CLAUDE.md` line 3: *"splits and joins video
**without re-encoding**"*). Every operation must reproduce the source bitstream
byte-for-byte through the container, never decode-and-recompress. This is at once
the #1 **product** promise (instant, zero-quality-loss cuts and concats) and the #1
**safety** guarantee (the app can never silently degrade a user's footage).

`CLAUDE.md` states it as a hard rule (lines 82–85):

> **The `-c copy` no-re-encode invariant is sacred (split + join).** Split and join
> must never emit an encoder flag. The args-builders forbid encoder tokens and
> require a bare `copy`; the invariant is re-asserted at runtime before launch and
> by unit tests on the token list. Do not add a re-encode path in v1.

The forces in tension:

- **A single leaked encoder flag silently breaks the promise.** One stray `-c:v
  libx264` / `-crf` / `-vf` anywhere in a built command turns a lossless copy into a
  re-encode — slow, lossy, and invisible until the user inspects the output. The
  invariant cannot rely on reviewer vigilance alone.
- **Commands are built in code, not typed.** All ffmpeg execution flows through the
  typed `FfmpegArgs` builder (`CLAUDE.md` lines 54–56), so the guard belongs on the
  produced token list, at a single choke-point per operation.
- **Stream-copy only works on clean boundaries.** A `-c copy` cut can land cleanly
  only on a keyframe; a concat `-c copy` works only when the inputs share codecs and
  parameters. The invariant therefore *forces* behavior elsewhere — it is not a
  local flag choice.

## Decision

Adopt **stream-copy (`-c copy`) as a runtime-enforced invariant** on both the split
and join paths, guarded by an explicit encoder-token **denylist** checked before
every ffmpeg launch — not merely a convention.

- **Single choke-point per operation.** `SplitArgsBuilder`
  (`src/Core/Split/SplitArgsBuilder.cs`) is *"the SINGLE choke-point that builds
  every ffmpeg command the split engine runs"* — both the segment-muxer path
  (`SegmentMuxer` → `-map 0 -c copy -f segment …`) and the per-segment subset path
  (`PerSegment` → `-ss/-to -map 0 -c copy …`). `JoinArgsBuilder`
  (`src/Core/Join/JoinArgsBuilder.cs`) is the join equivalent (`ConcatCopy` →
  `-f concat -safe 0 -i <list> -map 0 -c copy <out>`). Neither builder ever emits an
  encoder flag.
- **Explicit denylist.** Both builders expose an identical
  `ForbiddenEncoderTokens` set — `-c:v`, `-c:a`, `-vcodec`/`-acodec`,
  `-crf`, `-preset`, `-b:v`/`-b:a`, `libx264`/`libx265`/`h264`/`hevc`/`aac`/…,
  and the filter flags `-vf`/`-af`/`-filter:v`/`-filter:a`/`-filter_complex`.
- **The invariant predicate.** `SatisfiesCopyInvariant(tokens)` returns true **iff**
  the token list contains a bare `copy` token AND none of `ForbiddenEncoderTokens`
  (case-insensitive) appears. This is asserted two ways:
  - **In unit tests**, directly on the built token list.
  - **At runtime, before launch.** `SplitEngine.AssertCopyInvariant`
    (`src/Core/Split/SplitEngine.cs`) calls it on *every* built command (segment-muxer
    and each per-segment run) and throws a `SplitException` — *"built ffmpeg command
    violates the stream-copy invariant (would re-encode). Refusing to run."* — rather
    than execute. `JoinEngine.JoinAsync` (`src/Core/Join/JoinEngine.cs`) does the same
    check and returns a refusal `JoinResult` writing nothing.
- **No re-encode path in v1.** There is deliberately no fallback that re-encodes to
  reconcile a bad cut or an incompatible join.

## Consequences

**Positive**

- **The product promise is mechanically guaranteed.** A re-encode can only happen if
  a builder both emits `copy` *and* smuggles a forbidden token past a case-insensitive
  denylist — and even then the runtime assert refuses to launch. Correctness does not
  depend on review discipline.
- **Instant, lossless operations.** Split and join copy the source bitstream, so both
  are near-instant and bit-exact; output size ≈ input size (relied on by the
  disk-space pre-flight and the join size estimate).
- **Defense in depth.** The same predicate guards at test time *and* runtime, so a
  regression is caught by CI if a test exists and still refused in production if one
  slips through.

**Negative**

- **Denylist is enumerated, not exhaustive.** `ForbiddenEncoderTokens` is a fixed
  list; a novel encoder/filter flag not in the set could pass the check. The list must
  be kept current as ffmpeg surface evolves, and split/join copies must stay in sync.
- **No graceful reconciliation.** Because there is no re-encode path, an incompatible
  join or an un-snappable cut cannot be "fixed" automatically — the operation refuses
  (see forced follow-ons). That is intentional but shifts the burden onto the user.
- **`copy`-token check is structural, not semantic.** The predicate proves a `copy`
  token is present and no denied token is; it does not model full ffmpeg argument
  grammar. It is a guard-rail, not a parser.

**Forced follow-ons** (this decision *causes* these; they are not optional)

- **Keyframe-snap is mandatory, not cosmetic.** Because a `-c copy` cut can only land
  on a keyframe boundary, every requested cut is snapped to the nearest keyframe
  (ties → earlier, clamped at ends — `CLAUDE.md` lines 101–103). The split plan carries
  keyframe-snapped boundaries (`SplitPlan.InteriorSnappedCuts`, each segment's
  `SnappedStart`/`SnappedEnd`), so the builders' `-c copy` is always clean. The
  user-visible snap delta is therefore a *design guarantee to surface*, not a bug to
  eliminate — and the keyframe scan that feeds it is itself the subject of
  **ADR 0009** (two-path keyframe scan). Any future change to how keyframe times are
  derived must preserve that cuts land on real keyframes.
- **Join is refused, not fixed.** Because concat `-c copy` is valid only across
  parameter-identical inputs, `JoinEngine` runs a **compatibility pre-flight**
  (`CheckCompatibilityAsync` → `CompatChecker.Compare`, `src/Core/Join/CompatChecker.cs`)
  that probes every input and compares video (codec, width, height, pix_fmt,
  time_base) and audio (codec, sample_rate, channels) against the first as reference.
  On any mismatch the join **refuses and writes nothing**, returning a `CompatReport`
  naming the offending clip and field. The engine never silently re-encodes to
  reconcile (`CLAUDE.md` lines 114–115). Reconciling mismatched clips is left to the
  user, guided by `FfmpegErrorMapper`'s *"Re-encode the clips to a common format
  before joining"* hint.
- **The denylist and predicate must stay mirrored** across `SplitArgsBuilder` and
  `JoinArgsBuilder`, and the runtime asserts in both engines must stay wired in front
  of every launch — the invariant is only as strong as its weakest un-guarded path.
