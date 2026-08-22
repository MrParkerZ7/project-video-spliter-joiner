# Uncovered invariants (todo-automate gap list)

### SPEC-001 stream-copy-split — 13 gap(s) of 38
- I7 — a cut whose SNAPPED time lands <=0 or >=duration is dropped with an 'outside the file bounds — dropped' warning (post-snap guard).
    → Required-Fail/boundary: SplitPlannerTests covers the PRE-snap out-of-range drop (Plan_CutAtOrBeyondDuration) but no test feeds a cut that snaps onto ~0 or ~duration and asserts the post-snap drop branch + its distinct warning. Add a planner test with a keyframe at ~duration so a near-end cut snaps out of bounds.
- I8 — empty keyframe list leaves cuts UNSNAPPED (raw times, StartDelta=0) and the split still proceeds.
    → Required-Success/boundary: no SplitPlannerTests case passes an empty keyframes list. Add Plan_NoKeyframes_UsesRawTimes_ZeroDelta asserting segments use the raw cut times and StartDelta is zero (no snapper invoked).
- I9 — coarse GOP (averageGop>2s) with a snap moving >0.5s raises the coarse-GOP precision warning.
    → Optional/warning: existing planner tests use averageGop=1s, so the coarse branch never fires. Add a test with averageGop>2s and a cut that snaps >0.5s, asserting a warning containing 'coarse GOP'.
- I11 — no cut surviving keyframe snapping → SplitException ('after keyframe snapping').
    → Required-Fail: Plan_AllCutsOutOfRange covers the pre-snap throw, but the distinct post-snap-collapse throw (all cuts snap onto the bounds) is untested. Add a planner test where every surviving cut snaps to ~0/~duration and assert the 'after keyframe snapping' message.
- I15 — SegmentMuxer with zero interior cuts → SplitException ('needs at least one interior cut time').
    → Required-Fail: no SplitArgsInvariantTests case calls SegmentMuxer with an empty cut list. Add a test asserting the throw and message.
- I16 — PerSegment places -ss BEFORE -i (input-side seek).
    → Ordering invariant: PerSegmentFallback tests assert -ss and -i are PRESENT but not their relative order. Add tokens.IndexOf('-ss') < tokens.IndexOf('-i') (mirrors the existing FfmpegThumbnailServiceTests -ss-before-i assertion).
- I17 — PerSegment emits -to == (end - start) duration, clamped >=0, not the absolute end.
    → Boundary/value: PerSegmentFallback_Command passes start=3,end=6 but only asserts -to is present, never its VALUE. Add an assertion that the token after -to equals '3' (6-3), plus an end<start case asserting the clamp to 0.
- I22 — engine asserts SatisfiesCopyInvariant at runtime before every ffmpeg launch and throws if violated.
    → Defensive guard: SatisfiesCopyInvariant is unit-tested directly, but the ENGINE's AssertCopyInvariant throw path is unexercised (unreachable via the real builder). Cover with a SplitEngine test using a stub/args-injection that produces an encoder-contaminated command and assert SplitException 'Refusing to run' (or accept as an intentionally-untestable defensive assertion).
- I27 — a non-null selection with no in-range indices → SplitException ('none … fall within the planned parts').
    → Required-Fail: SplitAsync_EmptySelection_Rejected covers the empty-list case (I26) but not a non-empty selection of only out-of-range indices (e.g. {99}). Add SplitAsync_AllIndicesOutOfRange_Rejected asserting the distinct 'None of the selected segment indices' message.
- I31 — EnsureEnoughFreeSpace throws DiskFull when free space < inputSize+16MB; unmeasurable drive skips.
    → Required-Fail + skip-path: no SplitEngine test exercises the disk pre-flight (BulkTrimEngineTests' Preflight cases test the batch orchestrator's own check, not SplitEngine.EnsureEnoughFreeSpace). Add a test forcing a knowably-too-small drive to assert the DiskFull SplitException, and one on an unmeasurable path asserting the check is skipped.
- I32 — ValidateRequestShape rejects empty InputPath / missing input file / empty OutputDir / null-or-empty CutPoints / unwritable OutputDir before probing.
    → Required-Fail: no split test covers ValidateRequestShape. Add parameterised SplitAsync tests for each rejection (empty input path, non-existent input, empty output dir, empty cut list, unwritable output dir) asserting SplitException with the matching message and that the runner/probe is never called.
- I33 — a failed probe → SplitException ('Cannot split '<input>': <reason>').
    → Required-Fail: no test drives SplitAsync with a probe that returns ProbeFailed. Add a test with a fake probe returning a failure and assert the 'Cannot split' message includes the reason.
- I35 — fewer parts produced than planned (missing temp file at move) → SplitException ('got fewer segments than planned').
    → Required-Fail: no test simulates ffmpeg succeeding but not writing all expected temp parts. Add a SplitEngine test with a fake runner that returns success without creating the temp files, asserting the 'was not produced by ffmpeg' SplitException.

### SPEC-002 bulk-trim-engine — 3 gap(s) of 32
- I13 — a probe failure (ProbeResult not ProbeSucceeded) makes KeptMiddleRequestBuilder throw SplitException, recorded as a Failed row
    → Required-Fail: construct a KeptMiddleRequestBuilder over a FakeProbe returning ProbeResult.ProbeFailed and assert BuildAsync throws SplitException (message 'Cannot trim …'); optionally run it through BulkTrimEngine and assert the row is ItemOutcome.Failed with a non-null Error — distinguishes the genuine-error path from the NoOpTrim→Skipped path. No existing test drives a probe failure (FakeProbe always succeeds).
- I23 — a collision-resolution exception is isolated to its own row (Failed) and never aborts the batch
    → Required-Fail: force ResolveCollision/ResolveAutoSuffix to throw for one row (e.g. an item whose desired output stem cannot be resolved to a free name) and assert that row is ItemOutcome.Failed while the surrounding rows still run and the batch outcome is CompletedWithFailures. No existing test exercises the pre-resolve catch block (lines 76-80).
- I26 — the disk pre-flight per-root size estimate excludes rows already decided at pre-resolve (collision-Skipped / resolution-Failed)
    → Boundary: run a batch where one row is collision-Skipped and the remaining runnable rows' summed source sizes fit within a tight FakeDiskSpaceProbe free-bytes value that would NOT fit if the skipped row were counted — assert the batch is NOT Blocked and runs the runnable rows. No existing test combines a pre-skipped row with a tight disk pre-flight.

### SPEC-003 join-concat — 12 gap(s) of 31
- I4 — empty/whitespace OutputPath refused with field "output"
    → Required-Fail: JoinAsync with a valid InputPaths but OutputPath="" (or whitespace) → Success false, Refusal.Mismatches contains field=="output", no ffmpeg. No existing test asserts the "output" field (only "input_count" is covered).
- I9 — time-base mismatch → field "time_base"
    → Required-Fail: CompatChecker.Compare of two clips whose video StreamInfo.TimeBase differs (e.g. 1/30 vs 1/25) → incompatible with a Mismatch field=="time_base". CompatCheckerUnitTests covers codec/resolution/pix_fmt but not time_base.
- I10 — video-stream presence difference → field "video_presence"
    → Required-Fail: Compare a video+audio clip against an audio-only clip → Mismatch field=="video_presence". Only the audio_presence analog is tested.
- I11 — audio-codec mismatch → field "audio_codec"
    → Required-Fail: Compare two clips with identical video but audio codec aac vs mp3 → Mismatch field=="audio_codec". Tests cover audio_sample_rate and audio_presence but not audio_codec.
- I13 — audio channel-count mismatch → field "audio_channels"
    → Required-Fail: Compare clips with audio channels 2 vs 6 → Mismatch field=="audio_channels". No existing test exercises the channels branch.
- I17 — stream-field string comparisons are case-insensitive
    → Required-Success (boundary): Compare clips whose codec/pix_fmt differ only in case (e.g. "H264" vs "h264", "YUV420P" vs "yuv420p") → Compatible (no mismatch). No test asserts the OrdinalIgnoreCase behavior.
- I18 — Overwrite=false + existing output → refusal field "output_exists" before ffmpeg
    → Required-Fail: create a file at OutputPath, JoinAsync with compatible inputs + Overwrite=false → Success false, Mismatch field=="output_exists", ffmpeg never runs. Split has this test; Join does not (JoinViewModelTests only checks Overwrite is forwarded to the request).
- I19 — Overwrite=true replaces an existing output
    → Required-Success: pre-create a file at OutputPath, JoinAsync with compatible inputs + Overwrite=true → Success true, output replaced. No engine-level test creates a pre-existing target with Overwrite=true.
- I23 — runtime copy-invariant guard refuses field "invariant"
    → Required-Fail (defensive branch): drive JoinAsync with a builder/args that fail SatisfiesCopyInvariant → refusal field=="invariant", no ffmpeg launch. The internal guard is untested (practically unreachable via ConcatCopy, but a documented refusal path).
- I27 — cancellation deletes partial temp output + rethrows; list file always cleaned
    → Required-Fail: JoinAsync with a runner that observes a pre-cancelled/cancelling CancellationToken → OperationCanceledException rethrown, no output file, temp list file removed. No join cancellation test exists.
- I30 — "Joining" detail is "1 clip" for single input, "N clips" for multiple
    → Required-Success (boundary): real JoinEngine.JoinAsync with a single-input request and a status recorder → a "Joining" stage carrying detail "1 clip". The App StagedStatusWiringTests uses a scripted fake engine ("2 clips") and the integration test asserts stage names only — the real-engine label branch is unasserted.
- I31 — numeric progress reports 0..1 and reaches 1.0 on success
    → Required-Success: JoinAsync of a compatible set with an IProgress<double> recorder → final reported value is 1.0 (and values are within 0..1). Join integration/staged tests assert duration and stage names but never the numeric progress channel.

### SPEC-004 media-probe — 10 gap(s) of 32
- I1 — empty/whitespace path → ProbeResult.Failure (no throw)
    → Required-Fail: call ProbeAsync("") / ProbeAsync("   ") and assert result is ProbeFailed with a non-empty reason and no exception. No existing test exercises the empty-path guard.
- I2 — non-existent path → ProbeResult.Failure (no throw)
    → Required-Fail: call ProbeAsync on a random non-existent path and assert ProbeFailed('File does not exist'). The non-media test writes a real file; the missing-file branch is untested.
- I4 — unparseable ffprobe JSON → ProbeResult.Failure
    → Required-Fail: inject an IFfprobeRunner that returns non-JSON text and assert ProbeFailed mentioning invalid JSON. No test drives the JsonException catch.
- I5 — zero/absent streams → ProbeResult.Failure ('No media streams')
    → Required-Fail: fake runner returning valid JSON with an empty/absent streams array; assert ProbeFailed('No media streams'). The non-media integration test hits the ffprobe-nonzero branch (I3), not this parse branch.
- I7 — Duration falls back to longest stream when format.duration absent
    → Boundary: fake runner returning JSON with no format.duration but two streams of differing durations; assert MediaInfo.Duration equals the longest. Only the format-duration-present path is currently tested.
- I8 — cancellation surfaces as OperationCanceledException (not swallowed into Failure)
    → Required-Fail: pass an already-cancelled token (or a runner that throws OCE) to ProbeAsync and assert OperationCanceledException propagates rather than returning ProbeFailed. Untested.
- I9 — GetKeyframesAsync empty path → ArgumentException
    → Required-Fail: await GetKeyframesAsync("") and assert ArgumentException. No test covers this guard.
- I10 — GetKeyframesAsync non-existent path → FileNotFoundException
    → Required-Fail: await GetKeyframesAsync on a missing path and assert FileNotFoundException. No test covers this guard.
- I17 — cache invalidation on mtime/length change forces a re-scan
    → Boundary: with a query-aware/counting fake runner, scan a temp file, rewrite it so length + LastWriteTime change, scan again, and assert the runner was invoked twice (new cache key). Existing cache tests only prove the HIT path; the invalidation path is untested for MediaProbe (only FfmpegWaveformService has this pattern).
- I23 — null keyframes → ArgumentNullException
    → Required-Fail: call SnapToNearestKeyframe(null, ...) and assert ArgumentNullException. Only the empty-list case (I24) is tested; the null guard is not.

### SPEC-005 thumbnail-service — 5 gap(s) of 25
- I11 — a cache hit requires the tracked file to still exist on disk; a tracked entry whose file was deleted externally re-extracts (File.Exists guard, line 114)
    → Required-Success/boundary: warm the cache for a bucket, delete the temp file out from under the service, request the same bucket again → assert the runner is called again (re-extraction) and a fresh non-null path is returned. No existing test deletes a tracked file externally while its entry is still in the index.
- I12 — a temp file left on disk by a prior process (on disk but untracked in memory) is reused without running ffmpeg and re-tracked (lines 122-126)
    → Required-Success: pre-create the exact ResolveTempPath file for (input, bucket) on disk, then call GetThumbnailAsync on a fresh service instance → assert the runner is never called (CallCount 0), the pre-existing path is returned, and a second call now hits the in-memory cache. No existing test seeds an on-disk file that bypasses the in-memory cache.
- I23 — constructor throws ArgumentNullException on null runner or null cacheRoot (lines 72-73)
    → Required-Fail: `new FfmpegThumbnailService(null, root)` and `new FfmpegThumbnailService(runner, null)` each throw ArgumentNullException. No existing test exercises the constructor null guards.
- I24 — non-positive bucketGranularity falls back to 1s default and non-positive maxEntries falls back to 128 default (lines 75-77)
    → Boundary: construct with bucketGranularity = TimeSpan.Zero (or negative) and assert 1s bucketing behavior (e.g. 1.5s and 0.5s share bucket 0/1 as the default would); construct with maxEntries = 0 and assert 128-entry capacity rather than immediate eviction. No existing test passes a non-positive granularity or cap.
- I25 — DefaultCacheRoot() resolves to %LOCALAPPDATA%/VideoSplitJoiner/thumb-cache with an OS-temp fallback (lines 87-96)
    → Required-Success: assert DefaultCacheRoot() ends with the AppFolderName/CacheFolderName segments and is rooted under LocalApplicationData (or GetTempPath when that is empty). No existing test asserts the default cache-root path composition or its fallback.

### SPEC-006 waveform-service — 2 gap(s) of 23
- I22 — Temp layout & default root: temp PCM path = <cacheRoot>/<sha256-hex first-16-bytes of inputPath>/audio.pcm; default root = %LOCALAPPDATA%/VideoSplitJoiner/waveform-cache with OS-temp fallback.
    → Required-Success/boundary: existing tests inject cacheRoot and only use InputCacheDir as an opaque handle — none assert the SHA-256-first-16-bytes subfolder naming, the audio.pcm leaf, or DefaultCacheRoot()'s LOCALAPPDATA path + temp fallback. Add a test asserting ResolveTempPath(known input) == cacheRoot/<precomputed-sha256-hex-16>/audio.pcm, and a test that DefaultCacheRoot() ends with VideoSplitJoiner/waveform-cache (and falls back to Path.GetTempPath()-rooted when LOCALAPPDATA is empty).
- I23 — Construction: ctor throws ArgumentNullException on null runner or null cacheRoot; non-positive sampleRateHz or maxEntries falls back to defaults (4000 / 16).
    → Required-Fail + boundary: no test passes a null runner/cacheRoot (assert ArgumentNullException) or a 0/negative sampleRate/maxEntries. Add a test that `new FfmpegWaveformService(null, root)` and `new(runner, null)` throw ArgumentNullException, and that constructing with sampleRateHz:0 emits -ar 4000 while maxEntries:0 still evicts at the 16 default.

### SPEC-007 cut-profiles — 2 gap(s) of 30
- I11 — SaveProfile(null) throws ArgumentNullException (ArgumentNullException.ThrowIfNull in AppSettings.SaveProfile)
    → Required-Fail: call settings.SaveProfile(null!) and assert it throws ArgumentNullException. No existing persistence test passes null to SaveProfile — CutProfilePersistenceTests only ever saves valid records.
- I30 — BuildProfileFromRow(name, null) throws ArgumentNullException (ArgumentNullException.ThrowIfNull on row)
    → Required-Fail: call CutProfileApplier.BuildProfileFromRow("X", null!) and assert ArgumentNullException. CutProfileApplierTests covers ApplyProfile null-args (ApplyProfile_NullArgs_Throw) but never the null-row guard on BuildProfileFromRow.

### SPEC-008 operation-progress-eta — 4 gap(s) of 40
- I11 — Progress is clamped to [0,1] (a reported value outside the range is bounded before it is stored). [OperationViewModel.BeginRun Progress<double> → Math.Clamp(value,0,1)]
    → Required-boundary: no existing test reports an out-of-range fraction. Add a case that Reports 1.5 (→ Progress==1) and Reports -0.5 (→ Progress==0) through the run's IProgress<double> and asserts the marshalled Progress lands at the clamped bound.
- I23 — FormatStatus edge forms: a detail that itself ends in an ellipsis renders as "Stage — detail" (sub-status) rather than "Stage… (detail)"; a null/blank stage → empty string. [OperationViewModel.FormatStatus]
    → Required-Success + Required-Fail: no test drives an OperationStatus whose Detail ends in '…'. Add a case reporting OperationStatus("Preparing","scanning keyframes…") and assert StatusText == "Preparing — scanning keyframes…", plus an OperationStatus with null/whitespace Stage asserting StatusText == "".
- I27 — SeedEstimatedDuration(TimeSpan?) seeds the NEXT run's duration fallback (positive only; null/non-positive disables it); consumed once at BeginRun and cleared so it never leaks into a later run. [OperationViewModel.SeedEstimatedDuration + BeginRun]
    → Required-Success + boundary: only EtaEstimator.SeedDuration is tested directly — the VM method and its once-and-cleared wiring are untested. Add a VM test that SeedEstimatedDuration(TimeSpan) then runs work reporting only fraction 0 and asserts EtaText becomes a concrete "~…left" (fallback fired); a second run with NO seed asserts EtaText stays "estimating…" (no leak); and a SeedEstimatedDuration(TimeSpan.Zero)/null leaves the fallback disabled.
- I34 — The EtaEstimator constructor rejects an alpha outside (0,1] with ArgumentOutOfRangeException. [EtaEstimator ctor]
    → Required-Fail: no test exercises invalid alpha. Add a case asserting new EtaEstimator(alpha:0) and new EtaEstimator(alpha:1.5) each throw ArgumentOutOfRangeException, and that alpha:1.0 is accepted (inclusive upper bound).

### SPEC-009 app-settings — 4 gap(s) of 22
- I3 — new AppSettings((string)null!) throws ArgumentNullException (ctor null guard)
    → Required-Fail: construct AppSettings with a null path and assert it throws ArgumentNullException — no existing test passes a null path (every test injects a real temp path).
- I13 — a persisted NaN/Infinity ratio maps to null on load (not a clamped number)
    → Boundary: write a file with horizontalSplitRatio = NaN (and Infinity) and assert the loaded ratio is null. Existing SplitRatio_OutOfRangeInFile_IsClampedOnLoad only exercises finite out-of-range numbers (9.0 / -3.0), not the non-finite→null branch.
- I15 — dirty-check setters skip re-persisting when assigned the current value
    → Required-Success: set a value, capture the file's last-write-time (or delete the file), re-assign the SAME value, and assert Save was not re-invoked (file not rewritten). No existing test verifies the unchanged-value no-write guard for any of the setters.
- I16 — atomic temp-then-rename write (no half-written file, no stray .tmp)
    → Required-Success: after a normal Save assert no <path>.tmp lingers, and (mechanism) that a pre-existing good file is replaced via temp-then-rename. Round-trip tests prove the write lands but none assert the atomic-rename mechanism or .tmp cleanup.

### SPEC-010 split-screen — 4 gap(s) of 40
- I1 — LoadAsync(null-or-whitespace) is a no-op: returns immediately, mutates nothing.
    → Required-Fail/boundary: call LoadAsync(null) and LoadAsync("   ") on a fresh VM and assert InputPath stays null, HasFile false, Operation untouched, no throw. No existing test exercises the null/blank-path early return.
- I22 — No file loaded, or duration <= 0, yields an empty Segments projection.
    → Required-Fail/boundary: assert Segments is empty on a fresh (unloaded) VM, and after Clear; and with a loaded file whose probed duration is Zero, assert no parts are projected. No existing test asserts Segments emptiness for the no-file / zero-duration branch (Clear tests assert Markers/Keyframes empty but not Segments).
- I27 — RunSplitAsync is a no-op unless CanRunSplit.
    → Required-Fail: with a loaded file but CanRunSplit false (e.g. no markers, or OutputDir blank, or zero segments selected), call RunSplitAsync and assert the engine was never invoked (LastRequest null) and Operation.State stays unchanged. Every existing RunSplit test sets up CanRunSplit=true first, so the guarded early-return branch is untested.
- I30 — A blank NamingPattern is replaced with SplitRequest.DefaultNamingPattern (Overwrite passthrough is already covered).
    → Required-Success/boundary: leave NamingPattern unset/blank, run the split, and assert engine.LastRequest.NamingPattern == SplitRequest.DefaultNamingPattern. The existing request-building test sets an explicit custom pattern, so the blank-to-default substitution branch is never taken.

### SPEC-011 bulk-cut-screen — 8 gap(s) of 60
- I5 — AddFilesAsync records the last-added file's directory into IAppSettings.LastInputDir (so the next file-picker seeds there).
    → Required-Success: add a row over C:\v\a.mp4 through BulkCutViewModel.AddFilesAsync and assert settings.LastInputDir == C:\v. (The existing LastInputDir test at ViewModelSettingsTests covers JoinViewModel, not the Bulk VM.)
- I14 — StartKeyframeScanAsync supersedes any in-flight scan (Interlocked.Exchange of the CTS cancels the prior one) and a stale scan's result is dropped (only the current CTS commits Keyframes).
    → Required-Success: start a gated scan, start a second scan on the same row before releasing, release both, assert the row commits the SECOND scan's keyframes and the first is discarded (mirrors SplitViewModelNonBlockingLoadTests' stale-guard test, none exists for BulkItemViewModel).
- I15 — a keyframe scan that throws (GetKeyframesAsync error) leaves Keyframes empty, clears IsIndexingKeyframes, and resolves both handles to identity snaps (Requested==Snapped) — the row still becomes KeyframesReady.
    → Required-Fail: a probe whose GetKeyframesAsync throws → assert row.Keyframes empty, row.KeyframesReady true, and IntroEnd.Snapped == IntroEnd.Requested (identity fallback).
- I16 — BulkItemViewModel.Warning surfaces the computed notes 'coarse keyframes — cuts may move ~Ns' (avg GOP > 4s), 'nothing trimmed from the tail' (outro ≈ EOF with a real intro), and 'very short keep (~Ns)' (0 < kept < MinKeptSpan).
    → Boundary: build rows with a coarse-GOP probe / an outro snapped to EOF / a sub-MinKeptSpan keep and assert each Warning substring. (Only the ledger-warning FOLDING is tested today, via RunBatch_Ledger's 'coarse note'.)
- I20 — BulkCutViewModel.ApplyToAll returns null and mutates nothing when the source row is null, not KeyframesReady, or has no probed Duration.
    → Required-Fail: call vm.ApplyToAll over a still-indexing source and assert the return is null and no target row's IntroEnd changed.
- I22 — in BulkCutViewModel.ApplyToAll, when the SOURCE row has no outro, every applied target has its outro CLEARED (ClearOutro) so it mirrors the source's keep-to-EOF shape.
    → Required-Success: source with intro only (no outro), a target that currently HAS an outro → after vm.ApplyToAll(source), assert target.HasOutro == false. (The CutProfileApplier analogue is tested by ApplyProfile_NoOutroProfile_ClearsAnExistingOutro, but the VM's own ApplyToAll path is not.)
- I24 — ApplyToAllCommand.CanExecute is true only when Items.Count > 1 (the apply-to-all gesture needs at least one other row).
    → Boundary: with 0 then 1 then 2 rows, assert ApplyToAllCommand.CanExecute(row) flips false→false→true.
- I43 — a late MarkRunning or SetProgress after a row reached a terminal batch state (Done/Failed/Skipped/Cancelled) is ignored — it never re-animates or overrides the terminal RowState/Progress.
    → Required-Success: drive a row to Done via the ledger, then call row.MarkRunning()/SetProgress(0.3) and assert RowState stays Done and Progress stays 1.0 (guards the D-004 'late progress post' hazard).

### SPEC-012 join-screen — 4 gap(s) of 28
- I1 — AddFilesAsync(null) is a no-op: no items, no probe, no compat check
    → Required-Fail/boundary: call vm.AddFilesAsync(null) on a fresh VM and assert Items empty, CompatCheckCount == 0, no throw. Existing add tests only pass non-null arrays.
- I10 — CheckCompatibilityAsync throw is caught defensively → Compat=null, IsCompatible=false, CompatSummary="Could not verify compatibility: …", Run gated
    → Required-Fail: give the fake IJoinEngine a CheckCompatibilityAsync that throws, add ≥2 items, assert IsCompatible false + summary prefix + CanRunJoin false. The current FakeJoinEngine never throws, so this catch branch is unexercised.
- I14 — Move(int,int) synchronous wrapper delegates to MoveAsync
    → Boundary: call vm.Move(2,0) (not MoveAsync/the commands) and assert the same reordered order the MoveAsync tests expect. Existing reorder tests exercise MoveAsync and MoveUp/DownCommand but never the sync Move() entry point the drag code-behind uses.
- I28 — CancelCommand delegates to Operation.CancelCommand
    → Required-Success: assert ReferenceEquals(vm.CancelCommand, vm.Operation.CancelCommand) (or that invoking it cancels an in-flight join). No Join-level test currently references CancelCommand.

### SPEC-013 preview-player — 4 gap(s) of 48
- I35 — Unload() resets the same preview state as Open (player.Unload; PreviewFailed/PreviewFailedReason cleared, Duration→null so IsReady false, IsPlaying false, Volume=1.0/IsMuted=false/SpeedRatio=1.0, seek+scrub holds cleared, Position→0) and calls Thumbnail.Clear().
    → Required-Success: a PlayerViewModelTests fact that drives a ready VM into a dirtied state (playing, muted, non-default speed/volume, mid-seek-hold, non-zero position, PreviewFailed set), calls vm.Unload(), and asserts player.Calls contains "Unload", IsReady==false, Duration==null, IsPlaying==false, Position==Zero, Volume==1.0, IsMuted==false, SpeedRatio==1.0, PreviewFailed==false. Mirrors the existing Open_CallsPlayerOpen_ResetsState / Open_ResetsVolumeMuteSpeed pair but for the Unload path, which has zero tests today.
- I39 — Hover-thumbnail wiring: Open forwards (path, current duration) to Thumbnail.SetInput; OnDurationAvailable forwards the known duration to Thumbnail.SetDuration; Unload calls Thumbnail.Clear.
    → Required-Success: construct PlayerViewModel with a recording IThumbnailService (or spy ThumbnailPreviewViewModel) via the full ctor, then (a) Open(path) → assert SetInput received the path, (b) raise DurationAvailable → assert SetDuration received the duration, (c) Unload → assert Clear was invoked. PlayerViewModelTests builds the VM through the thumbnail-less ctor and never references Thumbnail (0 mentions), so this wiring is unverified even though ThumbnailPreviewViewModel itself is separately tested.
- I47 — FfmeMediaPlayer.Seek completion surfaces PositionChanged then Seeked (the async-seek settle contract the VM's T-033 hold-release depends on).
    → Boundary/contract: the VM-side reliance on Seeked releasing the hold is covered via FakeMediaPlayer.RaiseSeeked (I20/I21), but FfmeMediaPlayer's own Run(seek, onSuccess=>{PositionChanged; Seeked;}) raising BOTH events on completion is not asserted anywhere. FfmeMediaPlayer is WPF/MediaElement-bound and documented as not headlessly unit-tested (verified live via app-run); a covering test would need a MediaElement harness. Honest gap: verified live, no automated test.
- I48 — FfmeMediaPlayer.StepFrame is a paused operation: if playing it pauses first (sets _isPlaying=false, calls Pause) before issuing the single-frame StepForward/StepBackward, so the frame lands stable rather than fighting the play loop.
    → Required-Success: exercise FfmeMediaPlayer.StepFrame while _isPlaying and assert a Pause precedes the step. FfmeMediaPlayer is WPF-bound (real MediaElement) and documented as not headlessly unit-tested; the VM-level StepForward/StepBack→±1 delegation IS covered (I15) but the pause-first semantics live only in the untested WPF impl. Honest gap: verified live via app-run.

### SPEC-014 timeline — 6 gap(s) of 35
- I13 — TimelineViewModel(owner) throws ArgumentNullException when owner is null (TimelineViewModel ctor, `owner ?? throw`).
    → Required-Fail: `new TimelineViewModel(null!)` should throw ArgumentNullException with paramName "owner". No existing test constructs the VM with a null owner — every builder passes a real SplitViewModel.
- I31 (view-only) — Timeline click prefers the nearest marker tick within TickHitRadiusPx (6px) → routes to SeekMarkerTick (seek); otherwise ClickAt(x/width) drops a snapped cut (TimelineView.OnTrackClicked + NearestTick).
    → View-only WPF code-behind (UserControl hit-test over ActualWidth) — not a unit-test target; the seek-vs-cut branch is exercised only through the VM commands (SeekMarkerTick/ClickAt, already covered). The 6px nearest-tick preference itself is verified by manual/visual QA. If made testable, extract NearestTick(x,width,ticks,radius) as a pure helper and assert: a click 5px from a tick returns that tick; 7px returns null → cut.
- I32 (view-only) — Bulk scrub render geometry: introX = clamp(introSnapped/total)*width, dropped-intro scrim [0→introX] + dropped-outro scrim [outroX→width] in DropScrimBrush, keep-span [min(introX,outroX)→max] in AccentMutedBrush; while dragging the grabbed handle paints at the clamped cursor X, on release it repaints at the settled Snapped (snap-on-release) (BulkRowScrubView.Redraw / OnUp).
    → View-only WPF Canvas render — not unit-testable directly. The VM half (Requested→Snapped re-snap) is covered by I26; the pixel geometry (rect placement, keep-span min/max coherence when handles cross, live-cursor drag paint) is visual QA only. Made testable only by extracting a pure span-geometry function (introX/outroX/keepLeft/keepRight from snapped offsets + width).
- I33 (view-only) — Bulk handle pick: OnDown grabs the nearer of intro/outro within HandleHitRadiusPx (8px); a miss does nothing (rows are NOT click-to-seek); an equidistant tie is broken by vertical position (top half → intro, bottom half → outro) (BulkRowScrubView.PickHandle).
    → View-only (uses Track.ActualHeight/ActualWidth + mouse Point) — not a unit-test target. No existing test drives the WPF gesture. Testable only if PickHandle's math is extracted as a pure function (dIntro/dOutro/radius/tie-by-Y → Handle enum).
- I34 (view-only) — Waveform band visibility: shown only when Waveform.HasAudio is true, else Collapsed (zero layout height); the playhead + marker ticks are drawn full-height across BOTH the wave and track canvases so they align as one unit (TimelineView.ApplyWaveBandVisibility + DrawOverlay).
    → View-only render/layout. The data-side trigger (HasAudio true/false) is covered on the VM by I21/I22; the actual Visibility.Collapsed toggle and the dual-canvas overlay draw are WPF render behavior verified visually — no automated unit case exists or is appropriate.
- I35 (view-only) — Waveform re-bucketing: PeakForColumn maps each pixel column to the MAX peak over its source window (downsampling keeps the loudest sample per column; fewer-peaks-than-columns samples the nearest), and BuildWaveGeometry applies a minBar (0.75px) floor so a silent stretch reads as a faint centre line (TimelineView.PeakForColumn / BuildWaveGeometry).
    → Pure functions but declared `private static` inside a WPF UserControl (App.dll references PresentationFramework), so effectively view-only / not reachable from App.Tests as written. No existing test covers max-per-column bucketing. Testable if PeakForColumn is promoted to an internal/public pure helper (or moved to a WPF-free math class): assert peaks=[0.1,0.9] into 1 column → 0.9 (max kept, not dropped).

### SPEC-015 app-shell-theming — 13 gap(s) of 28
- I6 — StopInactiveScreenPlayers stops the preview player of each non-active screen on every tab switch (Split.Player.Stop() when tab != 0, BulkCut.Player.Stop() when not bulk-active).
    → Required-Success: build MainViewModel with a recording/fake IMediaPlayer for Split and Bulk, switch SelectedTabIndex 0→1→2→0 and assert the just-deactivated screen's player received Stop() while the active one did not; plus a null-safe case (null/NullMediaPlayer + legacy no-Bulk ctor) that does not throw. No existing test injects a player that records Stop calls.
- I9 — CaptionTitle always equals BaseTitle and is decoupled from WindowTitle (caption never shows progress overlay).
    → Required-Success: assert vm.CaptionTitle == MainViewModel.BaseTitle both idle and while an op is running (when WindowTitle carries the 'verb %%' overlay), proving the two are decoupled. No existing test references CaptionTitle.
- I10 — HookOperations re-raises WindowTitle only on the active op's State/IsRunning/Progress/StatusText/EtaText changes.
    → Required-Success: subscribe to vm.PropertyChanged, drive the active screen's Operation through a run (Progress/StatusText/EtaText), and assert WindowTitle PropertyChanged fired for those; a boundary case that an unrelated op property does not raise it. Existing Tab2_Running reads vm.WindowTitle's value but never asserts the PropertyChanged notification, so the subscription itself is unverified.
- I16 — OrientedSplitPanel flips axis from IsVertical (3 RowDefinitions vertical / 3 ColumnDefinitions horizontal) re-placing the same children.
    → Required-Success (STA): construct the panel with two children, set IsVertical=false and assert ColumnDefinitions.Count==3 + splitter ResizeDirection==Columns; flip IsVertical=true and assert RowDefinitions.Count==3 + ResizeDirection==Rows, with the same child instances re-placed (not re-created). No OrientedSplitPanel test exists.
- I17 — Active-axis ratio drives clamped star sizing with a RegionMinLength (80px) minimum and 6px splitter.
    → Boundary: set VerticalRatio/HorizontalRatio to 0.3, 5.0, -1.0 and assert the first definition's star value is Math.Clamp(ratio, 0.05, 0.95) and second = 1-first, and MinHeight/MinWidth==80. Untested (no panel test).
- I18 — Splitter DragCompleted writes back only the active axis's ratio (other axis untouched — D6), recursion-guarded.
    → Required-Success (STA): after simulating a drag in vertical mode, assert VerticalRatio updated to the realized fraction while HorizontalRatio is unchanged (and vice versa), and that the write-back did not trigger a rebuild loop. Untested.
- I21 — WindowStateToBorderThicknessConverter returns Thickness(0) when Maximized, Thickness(1) otherwise.
    → Required-Success: Convert(WindowState.Maximized)→Thickness(0); Convert(WindowState.Normal)→Thickness(1). Pure converter, trivially unit-testable, but no test file references it.
- I22 — WindowStateToContentMarginConverter returns the resize-border thickness when Maximized, Thickness(0) otherwise.
    → Required-Success: Convert(WindowState.Normal)→Thickness(0); Convert(WindowState.Maximized)→SystemParameters.WindowResizeBorderThickness. Pure converter, untested.
- I23 — Maximized window is clamped to the monitor work area via the WM_GETMINMAXINFO hook.
    → This is native P/Invoke code-behind in MainWindow.xaml.cs; a pure-logic test would require extracting WmGetMinMaxInfo's rcWork→ptMax math into a testable helper. Currently untestable as written and untested.
- I24 — App wires the three global crash handlers with the documented Handled/SetObserved/swallow behavior.
    → The wiring lives in the WPF Application subclass (App.xaml.cs), not exercised by any test. A test would need the handler bodies refactored to injectable methods (log-then-dialog-then-Handled). The ErrorLogWriter crash-format side (I25) is tested; the App-side wiring is a gap.
- I26 — Global implicit ToolTip style themes all tooltips (Surface3 card, TextPrimary text, wrap at 360).
    → Required-Success (STA resource-load): load Controls.xaml, resolve the implicit ToolTip style, assert Background==Surface3Brush, Foreground==TextPrimaryBrush, MaxWidth==360. No resource-load/style test exists for it.
- I27 — Global implicit ScrollBar style themes all scrollbars (10px dark track, gold-on-hover thumb, orientation swap).
    → Required-Success (STA resource-load): resolve the implicit ScrollBar style, assert Width==10 + VerticalScrollBar template, and the Orientation=Horizontal trigger swaps to the horizontal template. Untested.
- I28 — HeroButton adds Bold + font 14 + padding 16,8 + gold DropShadowEffect over AccentButton.
    → Required-Success (STA resource-load): resolve the HeroButton style and assert FontWeight==Bold, FontSize==14, Padding==16,8, and an Effect of type DropShadowEffect with AccentColor. Untested.
