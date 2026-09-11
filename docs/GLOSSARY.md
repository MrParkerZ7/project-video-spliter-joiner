# Glossary

Domain and codebase terms used across VideoSplitJoiner's docs, specs, and code.

## Video / ffmpeg
- **Stream-copy (`-c copy`)** — remuxing the source's encoded packets into a new file **without re-encoding**. Near-instant
  and lossless (bytes are reproduced), so it's resolution-independent (4K stays fast). The app's core promise and the
  default for every cut; the one opt-in exception is Bulk Cut's **Exact cut** (below). See
  [adr/0001-stream-copy-only.md](adr/0001-stream-copy-only.md), [specs/SPEC-001-stream-copy-split.md](specs/SPEC-001-stream-copy-split.md).
- **Keyframe (I-frame)** — a self-contained frame the decoder can start from. Stream-copy can only cut cleanly on a keyframe.
- **Keyframe-snap** — moving a requested cut point to the **nearest keyframe** so the `-c copy` boundary is clean. The
  displayed *snapped* time is the real cut; the signed offset is the *snap delta*. See `MediaProbe.SnapToNearestKeyframe`.
  This is the **Lossless** (default) behaviour; an **Exact cut** honours the requested time instead.
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
- **Apply-to-all** — copy one row's **requested** cut points to every other **ticked**, keyframes-ready row (outro applied
  **from the end** so uneven-length episodes align). Each target re-snaps against its own keyframes; rows the copy leaves
  invalid are reported, never silently dropped. See `BulkCutViewModel.ApplyToAll`.
- **Cut precision — Lossless vs Exact cut** — Bulk Cut's per-batch choice (`CutPrecision`). **Lossless** (default) snaps
  each cut to a keyframe and stream-copies every byte. **Exact cut** honours the requested time by re-encoding only the
  leading fragment up to the next keyframe (the whole range, if it ends before one) and stream-copying the rest
  (`SmartCutEngine`, the "smart cut"); a source with no encoder mapped for its codecs is cut Lossless instead. See [adr/0018-smart-cut-exact-trimming.md](adr/0018-smart-cut-exact-trimming.md).
- **Replace originals (`OutputMode`)** — Bulk Cut's opt-in destructive output (`OutputMode.ReplaceOriginal`): each
  row's result is produced in a temp file and replaces that row's source only once it is verified complete, so a row
  that fails or is cancelled keeps its original (rows that already finished have been replaced); the replaced original
  goes to the Recycle Bin. Collision policy is ignored in this mode. The default, `OutputMode.NewFile`, writes a new `_trimmed` file beside the source. See
  [adr/0017-output-mode-replace-original.md](adr/0017-output-mode-replace-original.md).
- **Delete originals / auto-delete / empty bin** — reclaiming space after a run. **Delete originals** (Bulk Cut) and
  **Delete original** (Split) send the source to the Recycle Bin, never a permanent delete, through the
  `IOriginalDisposer` seam (`RecycleBinOriginalDisposer` in the app). Bulk Cut offers it per finished row whose output is
  on disk and non-empty; Split only when **every** part it produced is on disk and non-empty. **Auto-delete** does the
  same automatically after a clean run (off by default, remembered separately per screen); **and empty bin**, which can
  only be armed on top of auto-delete and asks first, then empties the whole Recycle Bin — files other programs binned
  included. See [adr/0022-silent-shell-recycle-over-vb-fileio.md](adr/0022-silent-shell-recycle-over-vb-fileio.md),
  [adr/0024-per-screen-delete-eligibility.md](adr/0024-per-screen-delete-eligibility.md).
- **Row intent vs eligibility** — a Bulk Cut row's tick is two properties. `IsCheckedByUser` is the user's **intent**:
  the only thing the checkbox binds to, and also the target set apply-to-all and a profile's *Apply to all* write to.
  `IsEnabled` is read-only computed **eligibility** — intent AND not auto-excluded (unreadable file, nothing to trim
  yet, an invalid cut, or its original already deleted) — and is what the batch runs on. A ticked-but-excluded row
  stays ticked and shows its `ExclusionReason`. See [adr/0019-row-intent-vs-computed-eligibility.md](adr/0019-row-intent-vs-computed-eligibility.md).
- **Cut profile** — a saved, reusable `{ name, intro-from-start, outro-from-end?, optional thumbnail }` applied to rows for
  same-series batches. See [specs/SPEC-007-cut-profiles.md](specs/SPEC-007-cut-profiles.md).
- **Profiles bar / profile chip** — Bulk Cut's full-width list of saved profiles (`ProfileBar`), one picture-and-name
  **chip** per profile (`ProfileChipItem`), wrapping onto new lines and scrolling downward past its height cap. Clicking a
  chip only selects the profile; *Apply to selected* / *Apply to all* do the applying.
- **Preview card** — the hover card on a profile chip (`ProfilePreviewCard`, a ToolTip): the picture
  letterboxed in a 320×180 frame (no frame when the profile has none), the full name, and the intro/outro it would apply.
- **Snapshot thumbnail** — *Use current frame*: makes the frame on screen the selected profile's picture
  (`SnapshotProfileThumbnailAsync`). A picture source beside the automatic intro-end grab on save and an upload (see SPEC-007 I74).
- **Thumbnail normalization** — every picture source is stored no wider than `ProfileThumbnailWidth` (320px): captured frames are grabbed at
  that width, and an upload wider than it is re-encoded down (`ImageNormalizer.ShrinkToWidth`). That re-encode never upscales an upload; a
  picture that cannot be re-encoded is stored as it is.
- **Image signature** — `ImageSignature.IsImage`: the leading-bytes check (PNG, JPEG, BMP, GIF, TIFF, WEBP) that refuses
  an uploaded thumbnail that is not an image at all, leaving the current picture untouched. It does not prove an image is
  undamaged.
- **Profile backup** — *Back up…* / *Restore…*: every profile, pictures included inline, in one file
  (`ProfileBackup`). Restoring is planned first, so a corrupt, empty or newer-format file changes nothing, and it is additive:
  profiles you already have are overwritten only if you confirm. See
  [adr/0021-profiles-survive-reinstall-via-backup-file.md](adr/0021-profiles-survive-reinstall-via-backup-file.md).
- **Accept list** — the video extensions `VideoFileFilter` recognises. It decides what a drop accepts and also builds
  the file pickers' *Video files* filter (`VideoFileFilter.DialogFilter`), so the two cannot disagree.
- **Drop refusal** — `DropRefusal`: the one vocabulary all three screens use to say what part of an accepted drop did
  not arrive and why (not a video, a folder, already in the list, dropped twice, other videos skipped on Split). A
  drag with no recognised video is refused at the cursor instead and never reaches it. See
  [adr/0023-refuse-at-the-cursor-not-in-words.md](adr/0023-refuse-at-the-cursor-not-in-words.md).
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
