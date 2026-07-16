# VideoSplitJoiner

A fast Windows desktop app for **splitting** and **joining** video files **without re-encoding**.
Because every operation is a lossless stream copy (`ffmpeg -c copy`), splits and joins finish in
seconds instead of minutes and never lose a frame of quality — the deliberate trade-off is that
cuts land on the nearest **keyframe**, not on an exact frame.

Built as a .NET 8 WPF app with a UI-free Core library that shells out to a bundled FFmpeg.

## Features

- **Split** a video at one or more cut points. Each cut snaps to the nearest keyframe (so the
  copy is clean), and the app shows you exactly how far each cut moved: `requested → snapped (±delta)`.
- **In-app video preview player + visual cut selection** — the loaded file plays right on the Split
  screen (play / pause / stop / **scrub**). Park the playhead and click **"Set cut point at
  playhead"**, or **click the timeline strip** under the player, to drop a cut visually; clicking a
  marker or candidate tick seeks the player there. Every visually placed cut keyframe-snaps like any
  other. (Playback uses Windows Media Foundation codecs — if a file can't preview you get a "preview
  unavailable, cut still works" banner and can still cut it.)
- **Auto-detect** natural split points — **black** intervals, **white** intervals, and hard
  **scene cuts** — as ranked candidates you can accept or adjust. Detection is decode-only (it
  writes no files and never re-encodes).
- **Join** compatible clips head-to-tail. A compatibility pre-flight compares codec, resolution,
  pixel format, time base, and audio layout; an incompatible set is **refused with a named reason**
  rather than producing a broken file. v1 never re-encodes to force a fit.
- **Drag and drop** — drag video files from Explorer onto the **Split** screen to load (the first
  file) or onto the **Join** screen to add them all in drop order, and **drag Join clips to reorder**
  them (same effect as the Up/Down buttons). Non-video files are ignored.
- Live **progress**, **cancel**, and friendly **error** messages with a details expander showing
  the raw FFmpeg output.

## Install & run (packaged release)

1. Download `VideoSplitJoiner-v<version>-win-x64.zip` from a release.
2. Unzip it anywhere.
3. Run `VideoSplitJoiner.App.exe`.

FFmpeg is **bundled** — `ffmpeg.exe` and `ffprobe.exe` ship inside an `ffmpeg/` folder next to the
executable, so there is nothing else to install. (The app resolves the binaries from that folder
automatically; see [Architecture → binary resolution](docs/ARCHITECTURE.md#binary-resolution).)

> The bundled FFmpeg in the developer distributable is a **GPL** build. See
> [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) before redistributing publicly — an LGPL build
> can be swapped in at package time.

## Build from source

The .NET 8 SDK in this environment lives at `D:\_env_storeage\dotnet` and is **not on PATH**, so
invoke it by full path (or add it to PATH yourself):

```powershell
# Build
D:\_env_storeage\dotnet\dotnet.exe build VideoSplitJoiner.sln -c Release

# Test (integration tests self-skip when FFmpeg binaries are absent)
D:\_env_storeage\dotnet\dotnet.exe test VideoSplitJoiner.sln

# Produce the distributable zip (single-file self-contained win-x64 + bundled ffmpeg)
powershell -File packaging/package.ps1
```

`packaging/package.ps1` publishes a single-file, self-contained win-x64 build, copies
`ffmpeg.exe` / `ffprobe.exe` into an app-local `ffmpeg/` folder, bundles the license notices,
and zips the result into `dist/`. Point it at an alternate FFmpeg with `-FfmpegSource <path>`.

Running the app itself needs FFmpeg present — either bundled (as in the packaged zip), placed in
an `ffmpeg/` folder next to the built exe, or discoverable on PATH.

## How it works / limitations

- **Keyframe-snap is intentional.** Stream-copy can only cut on a keyframe, so a requested cut is
  moved to the nearest keyframe before extraction. On a source with a coarse GOP (keyframes far
  apart) a cut can move by **seconds** — the app surfaces this both as a per-file warning after
  load and as the per-marker `±delta` so nothing is hidden. This is the price of zero re-encode and
  near-instant operation; it is not frame-exact editing.
- **Join refuses incompatible sets.** v1 will not re-encode to reconcile mismatched clips. If the
  clips differ in codec, resolution, pixel format, time base, or audio layout, the join is refused
  with the exact reason so you can fix the offending clip rather than getting a silently broken file.
- **Detection may over-detect** on busy footage — the candidates are ranked suggestions, not a
  finished cut list.

## Documentation

- [User Guide](docs/USER_GUIDE.md) — step-by-step for Split, Auto-detect, and Join.
- [Architecture](docs/ARCHITECTURE.md) — layering, engine contracts, binary resolution, MVVM shape.
- [Changelog](CHANGELOG.md) — release history.
- [Third-party notices](THIRD-PARTY-NOTICES.md) — FFmpeg attribution + licensing caveat.

## FFmpeg attribution

This product uses **FFmpeg** (https://ffmpeg.org), bundled as external `ffmpeg.exe` / `ffprobe.exe`
processes. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full attribution and the
GPL/LGPL licensing note.
