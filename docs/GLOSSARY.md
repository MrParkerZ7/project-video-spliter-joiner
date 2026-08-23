# Glossary

Domain and codebase terms used across VideoSplitJoiner's docs, specs, and code.

## Video / ffmpeg
- **Stream-copy (`-c copy`)** — remuxing the source's encoded packets into a new file **without re-encoding**. Near-instant
  and lossless (bytes are reproduced), so it's resolution-independent (4K stays fast). The app's core promise. See
  [adr/0001-stream-copy-only.md](adr/0001-stream-copy-only.md), [specs/SPEC-001-stream-copy-split.md](specs/SPEC-001-stream-copy-split.md).
- **Keyframe (I-frame)** — a self-contained frame the decoder can start from. Stream-copy can only cut cleanly on a keyframe.
- **Keyframe-snap** — moving a requested cut point to the **nearest keyframe** so the `-c copy` boundary is clean. The
  displayed *snapped* time is the real cut; the signed offset is the *snap delta*. See `MediaProbe.SnapToNearestKeyframe`.
- **GOP (Group of Pictures)** — the span between keyframes. A **coarse GOP** (keyframes far apart) means snapping can move a
  cut noticeably; the app surfaces a coarse-GOP warning.
- **ffmpeg / ffprobe** — the bundled CLI binaries. `ffmpeg` does the copy/cut; `ffprobe` reads duration + keyframes. Bundled
  app-locally (no PATH dependency) — see [adr/0010-shared-ffmpeg-bundling.md](adr/0010-shared-ffmpeg-bundling.md).
- **FFME (FFmpeg MediaElement)** — the WPF media-player control used for the in-app preview, chosen over the stock
  `MediaElement`. See [adr/0004-ffme-over-mediaelement.md](adr/0004-ffme-over-mediaelement.md).
- **Segment muxer vs per-segment** — two extraction paths: the single-pass `-f segment` muxer (all parts at once) vs a
  per-part `-ss/-to -c copy` run (a selected subset). A **bulk trim** uses the per-segment path for its one kept part.

## App concepts
- **Split** — cut one video into contiguous segments at chosen (keyframe-snapped) cut points.
- **Join** — concatenate compatible clips into one output (stream-copy where compatible).
- **Bulk Cut** — batch-trim the **intro** (and optional **outro**) off many videos at once, keeping the middle of each.
  A bulk trim *is a Split that keeps exactly one middle segment*. See [adr/0015-bulk-trim-reuses-split-single-segment.md](adr/0015-bulk-trim-reuses-split-single-segment.md).
- **Intro-end / outro-start** — the two cut points on a Bulk row: drop the leading `[0 → intro-end]` and (optional) trailing
  `[outro-start → EOF]`, keep the middle `[intro-end → outro-start | EOF]`. Rendered as a gold + blue dual-handle scrub bar.
- **Kept-segment / kept-middle** — the one segment a bulk trim keeps. `KeptSegmentSelector` resolves which planned part it is.
- **Apply-to-all** — copy one row's cut points to every other row (outro applied **from the end** so uneven-length episodes align).
- **Cut profile** — a saved, reusable `{ name, intro-from-start, outro-from-end?, optional thumbnail }` applied to rows for
  same-series batches. See [specs/SPEC-007-cut-profiles.md](specs/SPEC-007-cut-profiles.md).
- **AutoSuffix** — the default output-collision policy: a pre-existing `name_trimmed.ext` becomes `name_trimmed_2.ext` (never clobbers the source).
- **Cut marker** — a snap-aware cut point VM (`CutMarkerViewModel`) exposing requested vs snapped time + the delta.

## Codebase
- **WPF-free VM** — a view-model built only from `ObservableObject`/`RelayCommand` + Core/BCL types (no `PresentationFramework`
  reference), so it's unit-testable headlessly. Core stays WPF-free (guarded by `CoreIsUiFreeTests`). Hand-rolled MVVM — no
  toolkit. See [adr/0007-hand-rolled-mvvm.md](adr/0007-hand-rolled-mvvm.md).
- **OrientedSplitPanel** — a `Grid` subclass that flips rows↔columns (+ splitter orientation) on `IsVertical`, driving the
  vertical/horizontal layout modes.
- **`serves-spec:`** — a test trait/comment tying an automated test back to the `SPEC-NNN` invariant it verifies (living-spec traceability).
- **Living spec** — a `docs/specs/SPEC-NNN` file stating a feature's current behavior as numbered, testable invariants;
  the source `todo-automate` derives test cases from.
- **Case-Coverage Matrix** — the testing bar beyond line %: Required-Success · Required-Fail · Optional · boundary cases.
