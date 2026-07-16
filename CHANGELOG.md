# Changelog

All notable changes to VideoSplitJoiner are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project aims to follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The `1.0.0` release is the target
goal; `0.1.0` is the first end-to-end, shippable cut.

## [0.2.0] - Unreleased

Goal G-002: an **in-app video preview player** with **visual cut selection** on the Split screen.
Still no re-encode — the player only previews; every cut continues to keyframe-snap through the same
path as before.

### Added

- **In-app preview player** on the Split screen — the loaded file plays right there with
  play / pause / stop and a scrubbable timeline slider, and a `mm:ss.f / mm:ss.f` position/duration
  readout. Built behind an `IMediaPlayer` abstraction (`MediaElementPlayer` over a WPF `MediaElement`
  in production; `NullMediaPlayer` no-op default) with a WPF-free `PlayerViewModel`.
- **Set cut point at playhead** — park the player and drop a cut marker at the current position.
- **Visual timeline strip** under the player showing the **playhead**, a tick per **cut marker**, and
  a tick per **detected candidate** coloured by kind (**black** / **white** / **scene**), with a
  legend. **Click the strip** to drop a cut at that position; **click a marker tick** to seek to its
  snapped cut; **click a candidate tick** to preview its detected time. Built from pure
  `TimelineMath` (normalized ↔ time) + a `TimelineViewModel` projection.
- Every visually placed cut (playhead-capture or timeline-click) funnels through the existing
  `AddCutAt` → **keyframe-snap + dedupe** path — one snap implementation, no new cut logic.

### Notes

- The preview uses **Windows Media Foundation** codecs, whose coverage is narrower than the bundled
  FFmpeg. A file the player cannot open shows a **"Preview unavailable — you can still cut this
  file"** banner and remains fully cuttable — preview failure is not a load failure. (FFME is parked
  as the upgrade behind `IMediaPlayer` if coverage proves too narrow.)

## [0.1.0] - 2026-07-15

First end-to-end release (goal G-001): a working Windows split/join app with a bundled FFmpeg and
a packaged distributable. Every operation is lossless stream-copy (`-c copy`) — no re-encode.

### Added

- **Split screen** — load a video, place cut markers manually or via auto-detect, and extract
  contiguous segments in a single stream-copy pass via FFmpeg's segment muxer. Cuts snap to the
  nearest keyframe; each segment reports its actual snapped boundary and signed delta. Coarse-GOP
  files raise a warning that cuts may move noticeably. Configurable output directory and segment
  naming pattern; overwrite protection.
- **Auto-detect split points** — three decode-only passes (black via `blackdetect`, white via
  `negate,blackdetect`, hard scene cuts via `select=gt(scene),metadata=print`), merged and returned
  as keyframe-snapped, ranked candidates. Never writes a file, never re-encodes.
- **Join screen** — gather and reorder clips, with a live compatibility verdict. A compat pre-flight
  compares codec, resolution, pixel format, time base, and audio layout against the first clip;
  incompatible sets are **refused with a named reason and no output written**. Compatible clips are
  joined via the stream-copy concat demuxer.
- **UI-free Core library** — `MediaProbe` (duration/streams/codecs, cached keyframe index,
  nearest-keyframe snapping, average GOP), `SplitEngine`, `JoinEngine` + `CompatChecker`,
  `SplitPointDetector`.
- **Single FFmpeg choke-point** — `FfmpegRunner` / `FfprobeRunner` (all execution flows through here;
  kill-tree cancel; never throws on non-zero exit), `FfmpegBinaryLocator` (explicit override →
  app-local `ffmpeg/` folder → PATH), and a typed `ArgumentList`-based `FfmpegArgs` builder.
- **No-re-encode invariant** enforced structurally, at runtime, and by tests for both split and join;
  detection enforced decode-only the same way.
- **Friendly errors** — `FfmpegErrorMapper` categorizes raw FFmpeg stderr into user-facing messages
  with hints, always preserving the raw output for a details expander. Shared progress / cancel /
  error handling via `OperationViewModel`.
- **Packaging** — `packaging/package.ps1` produces a single-file, self-contained win-x64 publish,
  bundles `ffmpeg.exe` / `ffprobe.exe` into an app-local `ffmpeg/` folder, includes license notices,
  and zips a versioned distributable. `THIRD-PARTY-NOTICES.md` documents FFmpeg attribution and flags
  that the bundled gyan.dev "essentials" build is GPL (swap to an LGPL build before public release).

### Known limitations

- Cuts are keyframe-accurate, not frame-exact — the deliberate trade-off for zero re-encode.
- Join refuses incompatible clip sets rather than re-encoding to reconcile them (no re-encode in v1).

[0.2.0]: https://keepachangelog.com/
[0.1.0]: https://keepachangelog.com/
