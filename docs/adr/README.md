# Architecture Decision Records (ADRs)

This folder holds the Architecture Decision Records for VideoSplitJoiner — one
Markdown file per decision, capturing the context, the choice made, and its
consequences. Each ADR is immutable once accepted; a later decision supersedes
an earlier one rather than editing it in place.

The table below indexes every ADR currently on disk — one file per record, in
numbering order. The index reflects the **actual** records present.

| #  | Decision | Status |
|----|----------|--------|
| [0001](0001-stream-copy-only.md) | Lossless stream-copy invariant (+ keyframe-snap & join-compat consequences, runtime-enforced denylist) | Accepted |
| [0002](0002-error-model.md) | A per-subsystem error contract, not one uniform strategy | Accepted |
| [0003](0003-cancel-safety.md) | Cancel safety — temp-then-move + refuse-don't-corrupt | Accepted |
| [0004](0004-ffme-over-mediaelement.md) | FFME (native ffmpeg) preview player over WPF MediaElement | Accepted |
| [0005](0005-async-seek-state-machine.md) | Async-seek state machine — hold, coalesce-throttle, click dedupe | Accepted |
| [0006](0006-thumbnail-grab-approach.md) | Scrub-bar hover thumbnails via a second ffmpeg-CLI grab, not FFME frame captures | Accepted |
| [0007](0007-hand-rolled-mvvm.md) | Hand-rolled MVVM — no CommunityToolkit.Mvvm, view models WPF-free for headless test | Accepted |
| [0008](0008-custom-windowchrome-caption.md) | Custom WindowChrome dark/gold caption with WM_GETMINMAXINFO taskbar clamp | Accepted |
| [0009](0009-two-path-keyframe-scan.md) | Two-path keyframe scan — demux packets primary, decode fallback, cache | Accepted |
| [0010](0010-shared-ffmpeg-bundling.md) | Shared (not static) ffmpeg build, gitignored, dual-consumer, ABI-pinned 7.x | Accepted |
| [0011](0011-single-file-publish-no-trim.md) | Self-contained single-file win-x64 + ReadyToRun, PublishTrimmed banned | Accepted |
| [0012](0012-gpl-lgpl-licensing-fork.md) | GPL-by-default ffmpeg, LGPL escape via `-FfmpegSource` | Accepted |
| [0013](0013-off-path-portable-dotnet.md) | Absolute dotnet path baked into packaging | Accepted |
| [0014](0014-no-ci-yet.md) | Deferred CI gate despite 517 tests + CoreIsUiFree guard | Accepted |
| [0015](0015-bulk-trim-reuses-split-single-segment.md) | Batch intro/outro trim reuses SplitEngine's single-segment path — no second ffmpeg code path | Accepted |
| [0016](0016-shared-bulk-preview-player-and-cut-profiles.md) | One shared Bulk Cut preview player bound to the selected row (not per-row) + cut profiles persisted in AppSettings | Accepted |
| [0017](0017-output-mode-replace-original.md) | Opt-in "replace originals" as a separate OutputMode axis (not a 4th CollisionPolicy) — verify-all, then replace in place via a Recycle-Bin backup | Accepted |
| [0018](0018-smart-cut-exact-trimming.md) | Frame-exact ("smart") cutting as a separate opt-in engine — re-encode one head GOP, copy the rest; stream copy stays the default | Accepted |
| [0019](0019-row-intent-vs-computed-eligibility.md) | A Bulk Cut row's selection is two properties — a bindable user intent and a read-only computed eligibility (merging them back reintroduces a dead checkbox) | Accepted |
| [0020](0020-unsafe-for-ffme-input-stream.md) | Allow unsafe blocks in the app so a file whose path cannot be a URI (a share whose name has a space) can be opened through FFME's IMediaInputStream | Accepted |
| [0021](0021-profiles-survive-reinstall-via-backup-file.md) | Profiles survive reinstall by not being touched; portability via a backup file with images inline, rather than migrating the two storage roots | Accepted |
| [0022](0022-silent-shell-recycle-over-vb-fileio.md) | Bin files through SHFileOperation directly, not VB's FileIO helper — no UIOption value is silent, so every locked file raised a dialog | Accepted |
