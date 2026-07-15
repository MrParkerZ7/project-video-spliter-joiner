# Third-Party Notices

VideoSplitJoiner bundles third-party software. This file records attribution and license
obligations for the components shipped inside a packaged distribution.

## FFmpeg

This product bundles **FFmpeg** (`ffmpeg.exe` and `ffprobe.exe`) in the `ffmpeg/` folder next
to the application executable.

- Project: https://ffmpeg.org
- Bundled build: **gyan.dev "essentials" static build, FFmpeg 7.1.1** (win64).
  - Source of binaries: https://www.gyan.dev/ffmpeg/builds/
- The full FFmpeg license text as shipped by that build is included alongside the application
  as `LICENSE` (copied from the ffmpeg build at package time).

FFmpeg is free software. The copyright and license notices of FFmpeg and its dependencies are
retained in the accompanying `LICENSE` file.

## Licensing

> **IMPORTANT — read before public distribution.**

The bundled gyan.dev **"essentials" static build is licensed under the GNU General Public
License (GPL), version 3.** It is compiled with GPL-only components enabled and is therefore a
**GPL** distribution, not LGPL.

What this means for VideoSplitJoiner:

- **Bundling this GPL build makes the combined, distributed product subject to the GPL.** For
  personal / internal / development use this is fine. For **public redistribution**, the GPL's
  obligations (including making corresponding source available and licensing the combined work
  under GPL-compatible terms) apply.
- **To distribute under permissive terms**, swap the bundled binaries for an **LGPL** FFmpeg
  **shared** build and invoke it as a separately-shipped executable / dynamically-linked library
  (the app already shells out to `ffmpeg.exe` / `ffprobe.exe` as external processes, which keeps
  the coupling loose and LGPL-friendly). Replace the `-FfmpegSource` used by
  `packaging/package.ps1` with the LGPL build's `bin` folder and update this notice accordingly.

**Decision required before public release:** ship the GPL "essentials" build as-is (and comply
with the GPL), **or** switch to an LGPL FFmpeg build. This packaging bundles the GPL build for
the developer/personal distributable; the choice above is deferred to the maintainer.
