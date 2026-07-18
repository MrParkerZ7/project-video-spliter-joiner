# Third-Party Notices

VideoSplitJoiner bundles third-party software. This file records attribution and license
obligations for the components shipped inside a packaged distribution.

## FFME (Unosquare.FFME.Windows)

This product bundles **FFME** (`FFME.Windows`, version `7.0.361-beta.1`) — the WPF media
element used for the in-app **video preview**. FFME P/Invoke-loads the bundled ffmpeg
**shared** libraries (see the FFmpeg entry below) from the `ffmpeg/` folder at runtime.

- Project: https://github.com/unosquare/ffmediaelement
- Package: `FFME.Windows` (NuGet), which binds to `FFmpeg.AutoGen 7.0.0` (the ffmpeg 7.x ABI).
- License: **Ms-PL** (Microsoft Public License), per the package. FFME itself is a managed
  library; the accompanying native decoding is provided by FFmpeg (below).

## FFmpeg

This product bundles the **FFmpeg SHARED build** in the `ffmpeg/` folder next to the
application executable. The folder contains BOTH:

- the shared libraries (`avcodec-61.dll`, `avformat-61.dll`, `avutil-59.dll`,
  `avfilter-10.dll`, `avdevice-61.dll`, `swscale-8.dll`, `swresample-5.dll`,
  `postproc-58.dll`) — P/Invoke-loaded by **FFME** for the preview, and
- the tools (`ffmpeg.exe`, `ffprobe.exe`) — shelled out to by the split/join **engine**.

One `ffmpeg/` folder therefore serves both the preview and the engine.

- Project: https://ffmpeg.org
- Bundled build: **BtbN `win64-gpl-shared` build, FFmpeg 7.1** (win64).
  - Source of binaries: https://github.com/BtbN/FFmpeg-Builds/releases
    (`ffmpeg-n7.1-latest-win64-gpl-shared-7.1.zip`), fetched via
    `packaging/fetch-ffmpeg-shared.ps1` / `.sh`.
- The full FFmpeg license text (when shipped by the build) is included alongside the
  application as `LICENSE`, copied from the ffmpeg build at package time.

FFmpeg is free software. The copyright and license notices of FFmpeg and its dependencies are
retained in the accompanying `LICENSE` file (where present) and in this notice.

> **Packaged size note:** switching from the prior exe-only bundle to the shared build adds
> the shared `*.dll` set (~+40 MB) to the distributable, because the DLLs the FFME preview
> P/Invokes now ship alongside the tool exes.

## IBM Plex (fonts)

This product bundles the **IBM Plex Mono** and **IBM Plex Sans** typefaces (the `.ttf`
files under `src/App/Fonts/`, embedded as WPF Resources) — used for the application UI
(mono readouts/labels + sans headings). The fonts are referenced via pack URI from
`src/App/Themes/Tokens.xaml`.

- Project: https://github.com/IBM/plex
- License: **SIL Open Font License, Version 1.1** (OFL-1.1).
  - Copyright © 2017 IBM Corp. with Reserved Font Name "Plex".
  - Full license text: https://github.com/IBM/plex/blob/master/LICENSE.txt
- The OFL permits bundling and redistribution of the fonts (including embedded in an
  application) provided the copyright/license notice is retained (this notice) and the
  fonts are not sold by themselves. The Reserved Font Name "Plex" must not be used on any
  derivative/modified version of the fonts. VideoSplitJoiner ships the fonts unmodified.

## Licensing

> **IMPORTANT — read before public distribution.**

The bundled BtbN **`win64-gpl-shared` build is licensed under the GNU General Public
License (GPL), version 3.** It is compiled with GPL-only components enabled and is therefore a
**GPL** distribution, not LGPL.

What this means for VideoSplitJoiner:

- **Bundling this GPL build makes the combined, distributed product subject to the GPL.** For
  personal / internal / development use this is fine. For **public redistribution**, the GPL's
  obligations (including making corresponding source available and licensing the combined work
  under GPL-compatible terms) apply.
- **To distribute under permissive terms**, fetch an **LGPL** FFmpeg **shared** build instead
  (e.g. a BtbN `win64-lgpl-shared` release — pass its URL to `fetch-ffmpeg-shared.ps1` via the
  `-Url` parameter) and point `packaging/package.ps1`'s `-FfmpegSource` at that folder. The
  shared libraries are dynamically loaded (FFME P/Invoke) and the tool exes are invoked as
  external processes, which keeps the coupling loose and LGPL-friendly. Update this notice
  accordingly when you switch builds.

**Decision required before public release:** ship the GPL `win64-gpl-shared` build as-is (and
comply with the GPL), **or** switch to an LGPL FFmpeg shared build. This packaging bundles the
GPL shared build for the developer/personal distributable; the choice above is deferred to the
maintainer.
