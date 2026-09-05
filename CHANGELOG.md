# Changelog

All notable changes to VideoSplitJoiner are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project aims to follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The `1.0.0` release is the target
goal; `0.1.0` is the first end-to-end, shippable cut.

## [Unreleased]

### Fixed
- **Split and Join now say why a dropped file did not arrive.** Bulk Cut learned this in 1.2.0; the other
  two screens still took what they recognised and discarded the rest without a word, which from the
  outside is indistinguishable from a dead drop target. Each screen says its own truth: Split
  **opens one file at a time**, so dropping five videos now tells you the other four were skipped and why
  — previously they simply vanished. Join says when a file is not a video, and when you dropped the same
  one twice in a single go.
- **A dropped folder is now called a folder** — "1 is a folder (drop the files inside it)" — instead of
  "not a video file", which was untrue and unhelpful for the most natural gesture there is.
- **Clearing a screen clears its drop note.** On Bulk Cut the message stayed on screen after *Clear all*,
  so an empty list could still be insisting three files were not added.
- **The drag-and-drop log now records what actually happened.** Every drop was written down as accepted,
  even when nothing was taken — in the one file you are asked to attach to a bug report, and about the
  exact question it exists to answer (whether the app ever saw your drag at all).
- **Opening a second file on the Split screen can no longer break the split you are already running.**
  Dropping a video — or pressing Load, or using the picker — while a split was exporting quietly tore
  that export down from the inside: **the Cancel button stopped cancelling it**, its progress line reset,
  and the run itself could fail. It now declines the new file and says a split is still running. Pressing
  Run twice did the same thing, and the Run button is now disabled while a split is in progress.

### Changed
- **The Bulk Cut footer has room to breathe.** Eight controls and three status notes had ended up on one
  line, added a feature at a time, so the row you use to arm the irreversible options was the most
  crowded thing on the screen. The output options now get their own full-width row above the buttons,
  and the three destructive ones — *Replace originals*, *Auto-delete originals*, *and empty bin* — sit
  together at the end instead of being interleaved with the ordinary settings. Nothing moved that you
  press: Run stays where it was, and *Delete originals* stays at the far opposite end from it.

### Known limitation, now written down
- Dragging files the app recognises **none** of never reaches the screen: Windows shows a no-entry cursor
  and the app is never told the drop happened, so it cannot explain that one in words. The cursor is the
  answer in that case. This was previously documented as something the message covered; it never was.

### Internal
- A thumbnail-gate test failed at random — it waited on two wall-clock deadlines for work another thread
  had queued, so it lost whenever the machine was busy and reported the concurrency bound as broken when
  the grabs had simply not arrived yet. Both waits are now signalled from inside the lock that counts
  them. This matters more than it used to: the release gate only became capable of failing last week, and
  a gate that fails at random is barely better than one that cannot fail at all.

## [1.2.0] - 2026-09-02

A **minor**, not a patch: this run added features (profile backup/restore, delete-originals, snapshot
thumbnails, batch-wide apply) alongside the fixes.

### Added
- **Cut profiles — back up and restore** (G-051): a **Back up…** / **Restore…** pair in the profile bar
  writes every profile, pictures included, to one file you can keep, move to another PC, or share.
  Uninstalling never removed your profiles, but that only ever covered *this* machine; a file covers a new
  PC and a wiped disk too. Restoring is additive — profiles you already have are kept unless you say
  otherwise, and a damaged backup changes nothing at all.
- **Bulk Cut — Delete originals** (G-050): once a batch finishes, reclaim the space it left behind. The
  button names how many files and roughly how much space, sends them to the Recycle Bin (never a
  permanent delete), and skips anything that failed or whose trimmed file is missing. If a file cannot be
  removed it now says **what is holding it** — "Still in use: ep12.mp4 (held by ffmpeg.exe)" — instead of
  leaving you to guess.
- **Bulk Cut — auto-delete after a successful batch**, with an optional **empty the Recycle Bin**: for
  when the disk fills mid-session and reclaiming space by hand after every batch is a chore. Both are off
  by default. A batch where any row failed never deletes anything, and the two together are permanent
  deletion, so arming that combination asks you once and the footer says so in red before you press Run.
- **Cut profiles — use the frame you are looking at** (G-047): a **Use current frame** button makes the
  frame showing in the preview the profile's picture, instead of hunting for an image file.
- **Bulk Cut — one gesture sets the whole batch** (G-046): "Set intro/outro here" now applies across every
  ticked row rather than the selected one, so a season of episodes takes one action.
- **Bulk Cut — Run says what it will do first** (G-046): the count beside Run explains which rows are
  included and which are being skipped, before you press it. A user set one cut, pressed Run, got one
  file, and had to ask why.
- **The app reopens the way you left it** (G-049): the last-used tab is remembered across restarts, as the
  layout orientation already was.
- **Exact cut can replace originals safely**: the exact-cut path is now cleared for the replace-originals
  output mode, and is verified against real media rather than fakes.

### Fixed
- **Dropped files no longer vanish without explanation**: dragging a video the app did not recognise, or
  one already in the list, simply did nothing — indistinguishable from a broken drop target. The
  recognised-format list grew from 11 to 25 (adding **`.m2ts`/`.mts`** for camcorder and Blu-ray footage
  and **`.3gp`** for phone video, among others), and anything still not added is now reported: *"3 files
  were not added: 2 are not video files, 1 is already in the list"*.
- **Deleting originals no longer interrupts you**: Windows raised a "file in use" dialog on every run,
  even though the deletion then succeeded. It now reports refusals itself, in the app, and names the
  program holding the file.
- **Videos on a network share** (G-045): a share whose name contains a space (`\Seagate NAS\…`) failed
  to open with *"Invalid URI: The hostname could not be parsed"*. Such paths now play, and any path the
  preview genuinely cannot open explains itself instead of leaking a .NET error.
- **Saving a cut profile no longer breaks the screen**: saving the first profile made the header taller,
  and the layout let it swallow the rest of the tab until something forced a redraw. The content area now
  keeps its space however tall the header gets.
- **The save dialog no longer edits the wrong profile**: its thumbnail controls acted on the
  *previously selected* profile, so Clear there silently destroyed a different profile's picture. All
  picture editing lives in the profile bar now, where the selected profile is the one being acted on.
- **A profile's picture is optional and changeable**: it can be set from a frame, uploaded, or removed at
  any time after the profile is saved.
- **The profile bar no longer hides its own buttons**: the bar wraps instead of clipping when it runs out
  of width.
- **Delete originals no longer skips the video you were watching**: the preview held that file open, so
  the sweep refused exactly it and quietly removed one fewer file than it said. The app now releases its
  own handle first.
- **Delete originals is styled and placed as the destructive action it is**: it uses the screen's danger
  styling and sits at the opposite end of the footer from Run — it had been appearing on the pixels Run
  occupied a moment earlier, under the cursor of someone who had just been pressing it.

### Internal
- The window header now shows the running version, so it is obvious which build you are looking at.
- The release gate can now fail: 43 integration tests had been reporting *passed* on CI without executing,
  because the ffmpeg they need was looked for at one hardcoded path and fetched only after the tests ran.
- Running the test suite no longer overwrites your real application settings.
- A ~40%-per-run flake in the test suite was diagnosed and fixed (a wall-clock wait, not a race).

## [1.1.0] - 2026-08-29

### Added
- **Bulk Cut — Select all / Select none** (G-043): tick or untick every video in the list in one gesture,
  matching the Split screen. Ticked rows are the ones Run cuts — and the ones Apply to all writes to.
- **Bulk Cut — "Exact cut"** (G-042): cuts land exactly where you set them instead of snapping to the
  nearest keyframe. Only the fragment between the cut and the next keyframe is re-encoded (~1s); the
  remainder is still stream-copied. Lossless stays the default. A source whose codecs cannot be
  reproduced falls back to the lossless cut and reports why, rather than risking a bad file.
- **Bulk Cut — "Replace originals"** (G-041): opt-in output mode that writes each trimmed result over
  its original. Produce → verify → atomic replace, with the replaced original sent to the Recycle Bin;
  a counted confirmation is required and every failure path leaves the original untouched.

### Fixed
- **Bulk Cut — the snap is now visible** (G-041): rows show the requested time plus where it landed
  (`00:05.0 → 00:04.0 (−1.0s)`). Previously a cut that snapped to the same keyframe as the previous one
  changed nothing on screen, making a correct snap indistinguishable from an ignored click.
- **Bulk Cut — the cut-time field no longer overwrites your request** (G-041): clicking away from the
  IN/OUT field used to commit the displayed (snapped, 0.1s-truncated) value back over the time you set,
  and send that truncated value to the engine. Only a genuine edit commits now.
- **Bulk Cut — ticking a row now works** (G-043): the row checkbox and the app's own eligibility check
  shared one property, so every freshly imported row read as unticked while the underlying intent was
  already set. Clicking did nothing; clicking twice silently dropped the video from the batch — it stayed
  excluded even after you set a cut on it, and "Apply to all" skipped it. Intent and eligibility are now
  separate: a ticked row stays ticked, and a row the app is excluding says why ("nothing to trim yet —
  set an intro or outro") and dims instead of looking identical to a row that will run.
- **Bulk Cut — Exact cut no longer excludes rows it can cut** (G-043): eligibility was measured against
  the keyframe-snapped time while Exact mode cuts at the time you set. On a coarse keyframe grid a short
  intro snapped back to zero, so the row was excluded as "nothing to trim" for a trim Exact performs
  correctly.
- **Cut profiles — changing a thumbnail is reachable** (G-044): the upload existed but only inside the
  "Save current as…" popup, so re-picturing an existing profile was effectively impossible. There is now
  a **Thumbnail…** button in the profile bar, and a failed upload reports the problem instead of quietly
  doing nothing.
- **Cut profiles — a failed thumbnail upload no longer destroys the old one** (G-044): the previous
  picture was deleted before the new one was copied, so a copy that failed part-way left the profile
  with no image at all.
- **Bulk Cut — the coarse-keyframe warning blind spot** (G-041): a file with a mean GOP of exactly 4.0s
  produced no warning. The advisory now also reports how far a cut actually moved.

## [1.0.0] - 2026-08-26

**First published release.** The app is feature-complete for its original vision: split, join, and
bulk-trim video **losslessly** (ffmpeg stream-copy, keyframe-snapped — no re-encode, no quality loss),
with a full in-app preview player and a themed desktop shell. 922 automated tests green; 98% of the
documented spec invariants are covered (`docs/specs/`).

> Sections below detail the preview-player era (G-002 → G-016). The scope added after that, shipped
> across G-017 → G-040, is summarised here:

### Added (G-017 → G-040)

- **Bulk Cut** (G-036–G-040) — a third tab that batch-trims many videos at once: drop a list, set an
  intro-end (and optional outro-start) per row, keep the middle, run the batch. Includes a mini preview
  player with jog controls, **reusable cut profiles** (saveable, with optional user-uploaded thumbnails),
  **frame thumbnails at each cut point**, layout-mode-aware panes, and apply-to-all (per row / by profile).
- **Audio waveform** above the timeline (G-033) — see speech vs silence to place cuts.
- **Vertical-monitor mode** (G-032) — flip the two-column layout to a stacked portrait layout.
- **Hover-thumbnail preview** on the scrub bar (G-030) — the frame at the hovered time.
- **Progress surfacing** (G-025, G-027) — Windows taskbar-button progress + ETA in the title, per-part
  split progress, and clear Running / Completed / Cancelled / Failed operation states.
- **Themed desktop shell** (G-017, G-018, G-019, G-023, G-027, G-029) — a token-driven dark + gold theme
  across every screen, a custom themed window frame with vector caption icons, themed scrollbars, and
  a matching typographic system.

### Changed

- Split output now defaults to the loaded file's folder and resets on every load (G-020); cut markers
  are ordered by time position rather than insertion order (G-026); the Split tool panel was reflowed
  (G-034, G-035); release builds self-bundle ffmpeg (G-024).

### Fixed

- Clicking the scrub bar seeks to the click point instead of page-stepping (G-028); the app no longer
  self-closes on split → clear → load-new, backed by a global crash safety net (G-031); jog buttons
  freeze the preview on the exact frame instead of playing a ~1s burst; Bulk row selection is instant
  (the heavy preview open is debounced, latest-wins).


Goal G-002: an **in-app video preview player** with **visual cut selection** on the Split screen,
then hardened over G-004→G-007 — the preview now decodes through **FFmpeg** (so it plays what the app
can cut, including HEVC / MKV / 4K), the black/white/scene **auto-detect feature was removed**, and
the player gained a full navigation-control surface plus a drag-resizable video pane. Still no
re-encode — the player only previews; every cut continues to keyframe-snap through the same path.

### Added

- **Bulk Cut tab — batch intro/outro trim (G-036, implements design D-004)** — a **third tab** batch-trims
  the **intro** (and an optional **outro**) off many videos at once, keeping the middle of each. Add videos
  (multi-select or drag-drop, deduped by path) into a scrollable list; on each row's **dual-handle scrub bar**
  mark the **gold intro-end** handle and an optional **blue outro-start** handle (the kept middle glows gold),
  or set one row and **"apply cut points to all"** — the **outro is measured from the end**, so same-series
  episodes of different lengths line up, and each target re-snaps + re-validates (rows the copy invalidates are
  **reported, not silently dropped**). Every cut is **stream-copy + keyframe-snapped**, exactly like Split.
  Output is `<name>_trimmed<ext>` **beside each source**; **originals are never touched**; a name collision
  **auto-suffixes** (`_trimmed_2`, `_3`, …) unless you tick **Overwrite**. The batch runs **sequentially and
  failure-isolated** (one bad row never aborts the rest; failed rows are offered for retry), with a per-row +
  aggregate progress surface on the taskbar/title, and **Cancel keeps finished files and rolls back only the
  in-flight one**. Architecturally this adds **no second ffmpeg path** — a bulk trim IS a Split that keeps one
  middle segment: rows funnel through the existing `ISplitEngine.SplitAsync` per-segment `-c copy` path
  (kept-index resolved by `KeptSegmentSelector`), orchestrated by a new UI-free Core `BulkTrimEngine`
  (sequential batch loop, collision policy, disk pre-flight, ledger) that the WPF-free `BulkCutViewModel` /
  `BulkItemViewModel` delegate to. See [ADR 0015](docs/adr/0015-bulk-trim-reuses-split-single-segment.md).
- **Bulk Cut — preview player, jog controls, and reusable cut profiles (G-037)** — the Bulk Cut tab gains
  an **in-app preview player**: click a row and it plays in a **shared preview pane** at the top of the
  tab (one decoder for the whole list, **reusing the Split screen's player** — transport, scrub,
  hover-thumbnail, the **±1s / 5s / 10s / 20s / 1m / 5m / 10m / 20m** jog buttons, and frame-step), so you
  can find the exact frame and click **Set intro-end here** / **Set outro-start here** to drop that row's
  cut from the playhead (**keyframe-snapped**, same as every cut). A **draggable splitter** divides the
  pane from the list and the selected row carries a **gold ring**; only the active tab decodes (a tab
  switch or a batch run stops the preview). And **reusable cut profiles**: **Save current as…** names a
  row's cut (intro-from-start + optional outro-from-end) and **persists it to settings** (survives
  restart, upsert-by-name); pick one from the profiles bar and **Apply → selected** or **Apply → all** —
  the outro measured **from the end** so uneven-length episodes align, each target **re-snapped +
  re-validated**, and any row the profile invalidates **flagged red** with an aggregate "Applied to N ·
  M now invalid" note; **Delete** removes it. There is **no new player or ffmpeg path** — the pane reuses
  `PlayerView` and the set-at-playhead gestures re-snap through the existing cut path; the `CutProfile`
  model is a WPF-free Core record persisted by `IAppSettings`. See
  [ADR 0016](docs/adr/0016-shared-bulk-preview-player-and-cut-profiles.md).
- **Bulk Cut — profile thumbnails + per-row cut-point frame previews (G-038)** — cut profiles now carry an
  **optional thumbnail**: **Save current as…** auto-captures the **frame at the row's intro-end** as the
  profile's default thumbnail, **Upload image…** overrides it with your own picture, and **Clear** removes it;
  the thumbnail shows **beside the profile's name in the picker** and **persists across restarts** (deleting a
  profile removes its thumbnail too). And each row now shows small **frame thumbnails at its cut points** — the
  **intro-end** (gold ring) and, when an outro is set, the **outro-start** (blue ring) — so you can verify a cut
  by eye; they **update as you drag** the handles. Both are **best-effort** — a frame that can't be captured just
  shows a placeholder and never blocks a save or a batch. There is **no new ffmpeg/frame path**: cut-point frames
  reuse the same `IThumbnailService` the hover-preview uses (debounced + latest-wins, behind a **dedicated
  concurrency gate** so they never starve the keyframe scans), and profile thumbnails are copied into a per-user
  `profile-thumbs` folder by a new `ProfileThumbnailStore`. The stored value is a **path only, never image
  bytes**, and the `settings.json` round-trip is **backward-compatible** (an older file with no `thumbnailPath`
  loads cleanly).
- **Split tool-panel 50/50 layout (G-035, implements design D-003)** — the Split screen's tool panel is now a
  **bounded 50/50 star-row layout**: the **Cut markers** list and the **Parts to export** list each take ~half of
  the panel's height and grow with the window, and each scrolls internally when it overflows its half. "Add cut at
  playhead" now shares **one wrapping row** with the POSITION time field + "Add at time" (both add gestures on a
  single line that wraps gracefully when narrow), and **Parts to export is full-width** below the markers. The Run
  button stays reachable on short windows (the lists' internal scroll absorbs overflow), and the split is
  axis-agnostic so it works in both horizontal and vertical (D-001) modes.
- **Audio waveform above the timeline (G-033, implements design D-002)** — the Split screen now shows the
  loaded video's **audio waveform** as a band above the timeline, aligned to the same time axis, so you can
  *see* speech vs silence and place cut points on natural boundaries. It's **vector-drawn + themed**, and
  **fused with the mark bar** — the playhead line and cut-marker ticks run continuously through both the wave
  and the timeline, and **clicking the wave seeks**. Peaks are extracted in the background on load (a new Core
  `IWaveformService` — ffmpeg → downsampled mono PCM → normalized peak array, LRU-cached, best-effort) and never
  block the load; a **silent / no-audio** video hides the band. Stacks with vertical mode.
- **Vertical-monitor mode (G-032, implements design D-001)** — a **toggle button in the title bar** flips the
  whole app between the horizontal two-column layout and a **vertical stacked layout** for portrait monitors:
  video + timeline on top (full width), the tool panel stacked below (scrollable), with the divider rotated to a
  horizontal splitter. One toggle flips **both Split and Join**; the chosen **`LayoutMode` persists** across launches
  (`settings.json`), and the horizontal and vertical splitter positions are remembered **independently per axis**.
  Built as a reusable `OrientedSplitPanel` (one instance of each region, column↔row flip driven by `IsVertical`) so
  every existing control/binding — and the hover thumbnail + scrub click — is unchanged.
- **Scrub-bar hover thumbnail — hovering the timeline shows a frame preview at that time (G-030).**
  Hovering the player scrub bar shows a small frame image at the hovered time, following the cursor with
  an `mm:ss` label, so you can find a split point by sight without moving the main player. Frames come
  from a separate ffmpeg CLI process (bucket-cached temp jpgs, kept apart from the FFME preview); hover
  is debounced + coalesced latest-wins so a fast sweep stays smooth, best-effort (a failed grab shows
  nothing, never blocks), and the temp cache is swept on new load / clear.
- **Per-part split progress (G-025)** — splitting into N parts now shows each part's row in "Parts to
  export" advancing **Pending → Writing (live %) → Done (✓)** as it's written, not just one overall bar.
  A dedicated `IProgress<PartProgress>` channel drives it: the per-segment subset path reports its part
  index naturally, and the **fast single-pass segment-muxer path is preserved** — the current part is
  *derived* from ffmpeg's reported time via a pure, unit-tested `PartAt(time, boundaries)` mapping (no
  extra ffmpeg passes). The active row shows a gold live-fill; completed rows a green ✓.
- **Windows taskbar-button progress + ETA in the title (G-025)** — a running split/join shows a live
  progress fill on the **Windows taskbar button** (green while running, indeterminate pulse while
  preparing, red on failure, clearing cleanly when done), via `TaskbarItemInfo` bound to the active
  screen's operation. Because the taskbar button can't render text, the **ETA + %** ride in the window
  **title** (`"Splitting 45% · ~1m 20s — Video Split / Join"`, visible on taskbar hover / alt-tab); the
  in-app caption keeps showing the app name.
- **Clear operation states — done / cancelled are no longer invisible (G-027)** — the progress UI used
  to vanish silently on completion. Now the operation lifecycle has four distinct surfaces: **Running**
  (gold bar + status + ETA + Cancel), **Completed** (green ✓ + a result line — "Split into 3 parts" /
  "Joined 4 clips → joined.mkv" — plus **Open folder**), **Cancelled** (a muted note, not red), and
  **Failed** (the red error block with Copy error / Open log). Exactly one shows at a time and it resets
  on the next run / load / Clear (no stale "done"). On both Split and Join.
- **Themed scrollbars (G-027)** — scrollbars now use a thin dark-and-gold style consistent with the
  IBM Plex design (track on a low surface tier, a rounded `BorderStrong` thumb that turns **gold on
  hover/drag**), applied app-wide via an implicit `ScrollBar` style — replacing the default light
  Windows scrollbar chrome.
- **Cut markers list is ordered by time position, not the order added.** When you place cuts out of
  chronological order (a cut at 5:00, then one at 2:00), the "Cut markers" list now reads top-to-bottom
  in time order (2:00 above 5:00) instead of add order. A marker placed while the keyframe scan is still
  running settles into its correct time slot once its snap resolves, and removing a marker keeps the rest
  ordered. The split output was already time-ordered (the plan and the "Parts to export" segments sort by
  time) — this is a marker-list display fix only.
- **Two-column layout matching the design sample (G-019)** — both screens now split into a **left
  visual column** (the preview player + timeline/scrubber) and a **right tool panel** (Load / Clear
  and everything below — file-info, cut markers, parts-to-export, output, Run) behind a **draggable
  column splitter** (right panel 360px default, 300–520 range). The app adopts the sample's identity:
  **IBM Plex Mono / Sans** bundled (OFL-1.1, in `src/App/Fonts/`), the full dark surface + gold +
  semantic palette, and tight 6–12px radii. New sample structure — an app header with the
  "lossless · no re-encode" tagline, a gold **format badge** (`HEVC · MATROSKA`), a Split **file-info
  card** (`container · duration · size`), **"Cut markers"** and **"Parts to export"** section headers,
  mono **DIR / NAME** output fields, and a Join **"Estimated result"** panel (total duration + approx
  size). Pure formatting/estimate helpers extracted to `Core/Media/MediaFormat.cs` (fully unit-tested);
  all existing bindings/commands preserved — a relayout + restyle, not a rewire.
- **Output folder defaults to the loaded file's folder (G-020)** — the split output directory now
  **defaults to wherever the loaded file lives** and **re-anchors on every new load** (drag or picker),
  so exports land next to the source by default. It stays fully editable for the one-off case; a manual
  change is discarded the next time you load a file. The file-picker's remembered *input* folder is
  unchanged.
- **Selectable split parts (G-015)** — after you set cut points, the Split screen lists the resulting
  parts as a checklist (`Part 2 · 05:00–10:00 · 5:00`) with **All / None** toggles, and **only the
  parts you check are written** (`SplitSegmentViewModel` + `SplitRequest.SelectedSegmentIndices`).
  Unselected parts cost no time or disk — a strict subset extracts via a per-segment `-ss/-to -c copy`
  path (one ffmpeg run per chosen part), while a full selection keeps the fast single-pass segment-muxer
  path. Still lossless, and each selected part keeps its **original** part index in the filename (a
  chosen middle part stays `…_part02`). The Run button reflects the selection ("Split 2 of 3 parts").
- **Clear / Clear all (G-014)** — a **Clear** button on the Split screen unloads the current video and
  resets the whole screen (blank preview via `IMediaPlayer.Unload()`, markers / timeline / output /
  results cleared, the background keyframe index cancelled); a **Clear all** button on the Join screen
  empties the clip list and resets the compatibility verdict. Both are disabled while an operation is
  running so you can't wipe the workspace mid-op.
- **Staged operation status (G-013)** — a running split/join shows the current **stage** synced to the
  real work rather than a timer (`OperationStatus` reported through an `IProgress<OperationStatus>`):
  split runs **Preparing → Splitting (N parts) → Finalizing → Done**; join runs **Checking
  compatibility → Joining → Finalizing → Done**.
- **Estimated time remaining (G-013)** — while a split/join runs, a friendly ETA shows beside the
  status ("~1m 20s left", or "estimating…" until there's enough signal). It's smoothed (EMA over
  elapsed-vs-progress in `EtaEstimator`) so it trends down without lurching on ffmpeg's bursty
  `time=` reports, and it clears when the run ends.
- **In-app preview player** on the Split screen — the loaded file plays right there with
  play / pause / stop and a scrubbable timeline slider, and a `mm:ss.f / mm:ss.f` position/duration
  readout. Built behind an `IMediaPlayer` abstraction (`FfmeMediaPlayer` in production;
  `NullMediaPlayer` no-op default) with a WPF-free `PlayerViewModel`.
- **FFmpeg-decoded preview (G-004)** — the preview now decodes via **FFME/FFmpeg**
  (`src/App/Media/FfmeMediaPlayer.cs`, package `FFME.Windows`, behind the unchanged `IMediaPlayer`
  seam), **replacing the WPF `MediaElement`**. Because it decodes through the same bundled FFmpeg as
  the split/join engine, it plays formats Windows Media Foundation could not — **HEVC, MKV, 4K, and
  other exotic container/codec combos** — so the "preview unavailable" banner is now rare.
  `App.OnStartup` sets `Unosquare.FFME.Library.FFmpegDirectory` before any FFME control loads. **One
  bundled ffmpeg *shared* build** (`ffmpeg/` folder) now serves **both** the preview (shared DLLs)
  and the engine (`ffmpeg.exe` / `ffprobe.exe`); dev setup fetches it via
  `packaging/fetch-ffmpeg-shared.ps1`, packaging bundles it, and `THIRD-PARTY-NOTICES.md` covers FFME
  + the GPL ffmpeg build.
- **Player controls to find the exact split point (G-007)** — skip buttons (**±1s / ±5s / ±10s /
  ±20s / ±1m / ±5m / ±10m / ±20m**), **frame-step** (±1 frame), **jump to start / end**, plus a
  **volume slider + mute** and a **playback-speed** selector (**0.25×–2×**), all on `PlayerViewModel`
  / `PlayerView`.
- **±10m / ±20m skip buttons (G-011)** — the player's jog row gained back/forward **10-minute** and
  **20-minute** skips alongside the existing 1s/5s/10s/20s/1m/5m jumps, so long clips can be traversed
  in far fewer clicks (`PlayerView.xaml`, routed through the same `SkipCommand`).
- **Copyable error + saved full log (G-010)** — a failed split/join now shows a **selectable** error
  with a **Copy error** button (copies the headline + hint + full detail + log path) and an **Open
  log file** button that reveals the saved log in Explorer. The complete ffmpeg output — command,
  exit code, UTC timestamp, and full stderr — is written to
  `%LOCALAPPDATA%/VideoSplitJoiner/logs/<op>-<timestamp>.log` (`ErrorLogWriter`), and `UserFacingError`
  gained `LogFilePath` / `FullText` to carry it. The preview-unavailable banner is likewise selectable
  with its own Copy button.
- **Remembered last folders (G-010)** — the app now remembers your **last input and output folders**
  across runs. They persist to `%APPDATA%/VideoSplitJoiner/settings.json` (`AppSettings`); the file
  picker opens at the last input folder and the output directory defaults to the last-used one.
- **Resizable video pane (G-006)** — the Split screen's preview area is drag-resizable via a
  `GridSplitter` in `SplitView.xaml`.
- **Set cut point at playhead** — park the player and drop a cut marker at the current position.
- **Visual timeline strip** under the player showing the **playhead** and a tick per **cut marker**.
  **Click the strip** to drop a cut at that position; **click a marker tick** to seek to its snapped
  cut. Built from pure `TimelineMath` (normalized ↔ time) + a `TimelineViewModel` projection.
- Every visually placed cut (playhead-capture or timeline-click) funnels through the existing
  `AddCutAt` → **keyframe-snap + dedupe** path — one snap implementation, no new cut logic.
- **Drag and drop** — drag video files from Explorer onto the **Split** screen to load the first file
  (via the existing `SplitViewModel.LoadCommand`) or onto the **Join** screen to add them all in drop
  order (via `JoinViewModel.AddFilesCommand`, compat re-check follows), plus **drag-to-reorder** the
  Join clip list (same `MoveAsync` path as the Up/Down buttons). Drop plumbing is thin code-behind
  over the existing VM commands; the accept-filter is a pure, tested `VideoFileFilter` helper. An
  internal reorder drag is distinguished from an external file drop by its clipboard payload type
  (`JoinItemViewModel` = reorder, `FileDrop` = add). Non-video files are ignored.

### Changed

- **Bulk Cut — layout-mode-aware, refreshed profiles UI, and apply-to-all reliability fix (G-039)** — the
  Bulk Cut tab now **follows the vertical/horizontal layout toggle**: its preview pane and row list are
  wrapped in the same `OrientedSplitPanel` the Split screen uses, so **vertical mode stacks** the preview
  above the list and **horizontal mode places them side-by-side**, with a themed splitter draggable in
  both orientations and a **Bulk-specific remembered split position per axis** (kept separate from the
  Split tab's, and backward-compatible in `settings.json` — an older file with no bulk keys just falls
  back to the Bulk defaults). The **profiles area is regrouped** from a flat inline strip into a bordered
  **"Profiles" card** — a thumbnail-aware picker, a gold primary **Save current as…**, a paired
  **Apply → selected / → all** split-control, and a muted **Delete** — a tokens-only restyle with the
  same commands, thumbnail display, and gating (no behavior change). And **apply-to-all now re-fires
  reliably every time**: the per-row *apply cut points to all* and the profile *Apply → all* buttons
  used to go stale after their first use; they now re-enable deterministically on every change (an
  app-wide `RelayCommand` notification fix) and each re-apply re-reads the **current** source. See
  [ADR 0016](docs/adr/0016-shared-bulk-preview-player-and-cut-profiles.md).
- **Custom themed window frame (G-018)** — the default light Windows title bar is replaced by a custom
  **dark title bar** (WindowChrome) matching the theme: the app title with a gold accent on the left,
  and themed minimize / maximize-restore / close caption buttons (close hovers red) on the right. The
  window still drags, resizes on all edges, and maximizes/restores correctly without covering the
  taskbar (a `WM_GETMINMAXINFO` work-area clamp + maximized content margin).
- **New premium dark + gold theme (G-017)** — the whole app is restyled to a token-driven **dark theme
  with a gold accent** (`#e0a83a`): near-black window (`#0d0f13`), charcoal rounded panels (`#15181e`),
  pure-black video area, and gold used consistently for primary actions (Run, play), the timeline
  **playhead and cut pins**, focus, and selection. Built as a design-token system — `src/App/Themes/`
  `Tokens.xaml` (brushes, corner radii, typography) + `Controls.xaml` (themed control templates), merged
  in `App.xaml`; every view references tokens, no hardcoded colors. Text uses theme tokens (readable on
  dark); compat green/red and error affordances are preserved, dark-tuned.
- **Split/join always show visible progress (G-012)** — a running operation now always shows a progress
  bar plus a status label, never a silent window. When granular progress hasn't arrived yet the bar
  animates as an **indeterminate busy indicator** (`OperationViewModel.IsIndeterminate`) instead of
  sitting frozen at 0% — the cure for the "-c copy split looks stuck" problem, since ffmpeg's `time=`
  can be sparse. It flips to a determinate bar the instant a real fraction arrives.
- **Faster, non-blocking video load (G-008)** — loading a file on the Split screen no longer waits on
  the full keyframe scan. `SplitViewModel.LoadAsync` now gates only on the fast metadata probe: it
  shows the file info and **opens the preview immediately**, then indexes keyframes in a
  **cancellable background task**. A new `IsIndexingKeyframes` flag (with `KeyframesReady`) drives a
  non-blocking **"indexing…"** hint; a new load cancels the previous file's index (stale-guard), and a
  cut placed while indexing awaits the same in-flight scan so it still snaps correctly (never to an
  empty list). Separately, `MediaProbe.GetKeyframesAsync` now scans keyframes at the **demux (packet)
  layer** (`-show_packets`, keeping `K`-flag packets) instead of decoding frames (**~3.86× faster**;
  4K: 216ms→56ms), with the previous `-skip_frame nokey` frame scan kept as a **fallback** when the
  packet query is empty or throws. Same sorted-distinct keyframes, per-file cache, snapping, and GOP
  behavior.
- **4K preview performance (G-005)** — the FFME preview now uses **hardware-accelerated decoding**
  (D3D11VA / DXVA2 / …) plus a **downscaled preview surface** (`src/App/Media/PreviewScale.cs`, capped
  at ~1080p, aspect-preserving, even dimensions) so large 4K sources play back smoothly without
  saturating the WPF UI thread. The **cut is unaffected** — it stays `-c copy` and is never decoded,
  so it always runs at the source's full resolution.

### Removed

- **Auto-detect (G-005)** — the black/white/scene **auto-detect** feature has been **removed**: no
  more `Core/Detect` layer, `SplitPointDetector`, detect passes, or candidate UI (candidate ticks /
  ranked candidate list). Manual cut markers, playhead-capture, and timeline-click cuts remain the
  ways to place cuts.

### Fixed

- **Bulk Cut — instant row selection (debounced preview) + apply-to-all now reachable/labelled in both
  layout modes (G-040)** — selecting a row in the Bulk Cut list is now **instant**: the row highlights and
  its controls light up immediately, while the heavy preview open is **deferred behind a short (~250ms)
  latest-wins debounce**, so **arrowing or scrolling through the list opens only the row you settle on**
  instead of firing one FFME decoder init per row swept past (the old selection lag / "doesn't work").
  Clearing the selection or starting a batch run **cancels a still-pending open**, so a stale preview never
  lands after the list is cleared or the run has begun; the settled open still goes through the last-line
  `MediaReopenGuard`. Separately, the per-row **apply-to-all** (⧉) had been **clipped off the right edge and
  unreachable in horizontal (narrow-list) mode** once the cut-point thumbnails + IN/OUT readouts overflowed
  the row — it and the **remove** (✕) button now sit in a **right-docked action cluster that stays reachable
  in both layout modes**, relabelled per-row **⧉ "all"** (with a tooltip) and, in the Profiles card,
  **⧉ Apply to selected / ⧉ Apply to all**. The debounce is a WPF-free view-model change and the
  discoverability fix is view/label/layout only — **same commands, same apply semantics**, no behavior change.
- **Cut markers appear instantly (G-012)** — placing a cut (Set-cut-at-playhead, manual add, or a
  timeline click) now drops the marker **immediately**, even while the background keyframe index is
  still running, instead of waiting for the scan. The optimistic marker shows a **"snapping…"** hint
  (`CutMarkerViewModel.IsSnapPending`) and resolves in place to its nearest keyframe once the index
  arrives (re-deduping on the final snapped time). When keyframes are already present the cut still
  snaps synchronously as before.
- **Per-segment cut end boundary (G-015)** — the subset export path extracts each selected part with an
  explicit `-to == snapped end`, while the plan's final part omits `-to` to run to end of file — so a
  selected middle part gets the same boundary the segment muxer would have produced.
- **Non-ASCII / unicode paths (G-010)** — files with non-ASCII paths (e.g. Japanese characters) now
  work end-to-end. Both `FfmpegRunner` and `FfprobeRunner` decode the child process' stdout/stderr as
  **UTF-8** (`StandardOutputEncoding` / `StandardErrorEncoding = UTF8`) regardless of the Windows
  console codepage, so unicode paths in the ffprobe JSON and in error output survive intact instead of
  becoming mojibake, and error text shows the real characters.
- **`.ts` / mpegts split failure (G-010)** — splitting an `.ts` (mpegts) file (which previously failed
  with exit `-28`) now works. The failure was the mangled-path symptom of the encoding issue above and
  is resolved by the UTF-8 fix. Relatedly, an out-of-space write (exit `-28` / `ENOSPC`) is now mapped
  to a clear **"not enough space to write the output"** (DiskFull) error — instead of surfacing an
  unrelated benign mpegts warning as the headline — and `SplitEngine` runs a best-effort **pre-flight
  free-space check** so an obviously-too-small output drive fails early with that friendly message.
- **Scrub pop-back (G-009)** — dragging the scrub slider (or using skip / frame-step / jump) now
  lands the playhead **at the position you chose and holds it there**, paused or playing. Previously a
  stale `PositionChanged` echo arriving during FFME's async seek (or ongoing playback) would yank the
  slider back to where playback actually was. `PlayerViewModel` now arms a **seek-target hold** on
  every user seek and ignores off-target echoes until the seek settles — cleared deterministically by a
  new `IMediaPlayer.Seeked` completion event, with a ~250ms tolerance and a bounded-tick anti-freeze
  backstop so the slider can never get stuck. The scrub slider also suppresses echoes while the thumb
  is being dragged (seek on release).
- **Responsive scrub (G-016)** — the video now follows the pin **live while you drag** it, instead of
  staying frozen until release. Seeks are **coalesced** (only one seek is in flight at a time; while it
  runs, only the *latest* pin position is kept and issued on completion — stale intermediate positions
  are dropped) and **throttled** (~70ms), so a fast drag converges to where the pin is now with no
  backlog/lag. Routes through the same seek-target hold, so the pop-back protection above still holds.

### Notes

- The preview decodes through **FFmpeg** (via FFME) — the same bundled build the engine uses — so it
  plays what the app can cut. A file the player still cannot open shows a **"Preview unavailable —
  you can still cut this file"** banner and remains fully cuttable — preview failure is not a load
  failure.

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
