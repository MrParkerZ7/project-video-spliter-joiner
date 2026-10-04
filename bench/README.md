# VideoSplitJoiner bench (T-188)

A dev-only performance bench. It times the real Core services (`MediaProbe`, `SplitEngine`, `JoinEngine`,
`BulkTrimEngine`, `SmartCutEngine`, the thumbnail and waveform services) through the app's own
`FfmpegRunner`/`FfprobeRunner`, in the order the view models call them, on **synthetic fixtures it generates
itself**. Every G-059 ticket records its before and after figures here, in its Build log; no test asserts a
wall-clock time (T-137).

- **Never packaged.** `bench/VideoSplitJoiner.Bench` references `src/Core` only and is in
  `VideoSplitJoiner.sln`, so a Core API change breaks its build. It is not a test project (`dotnet test` runs
  none of it), the App does not reference it, and `packaging/package.ps1` publishes only the App.
- **Synthetic fixtures only.** The bench must only ever see the files `bench fixtures` made. Never copy, link
  or point it at a real video. The fixture root's guard (below) refuses anything outside the root, and any file in
  it that the bench did not make (not listed in its `fixtures.json`, not under its `work\`), but it cannot tell a
  hard link from a file, so do not put links to your own media in the root.
- **It starts no process itself.** All spawning goes through `FfmpegRunner`/`FfprobeRunner` with typed
  `FfmpegArgs`, like the app. Bytes read come from a Windows job object instead (below).

It was ported from the T-187 prototype, whose reference copy stays in
[`docs/design/references/T-187-bench/`](../docs/design/references/T-187-bench/) as the evidence record:
`CoreScenarios.cs`, `Alternatives.cs`, `Smart2.cs`, the results writer and `show.py`'s filter. Not ported:
`IoMeter.cs` (it started processes itself; the job object replaces it) and `Mp4Index.cs` (T-194 builds the
production sample-table reader in Core, and the bench then times it through `MediaProbe`).

## Build and run

```powershell
D:\_env_storeage\dotnet\dotnet.exe build VideoSplitJoiner.sln -c Release
$bench = "bench\VideoSplitJoiner.Bench\bin\Release\net8.0\VideoSplitJoiner.Bench.exe"

& $bench fixtures                 # generate + self-check the matrix (about 8 GB; 4k10m alone needs ~5 GB free)
& $bench selftest                 # the guard, the job accounting, the self-checks, the results file
& $bench core 4k kf smart         # time scenarios on fixtures
& $bench core                     # every scenario on every valid fixture (the full baseline)
& $bench show --session last --markdown
& $bench show --session last --scenario smart --markdown   # one scenario's rows, for a Build log
```

It needs the app's ffmpeg shared build in `ffmpeg-shared/` (found walking up from the bench binary). Without it
the bench exits with the hint to run `packaging/fetch-ffmpeg-shared.ps1`; the test suite is unaffected.

| command | what it does |
|---|---|
| `fixtures [keys…] [--nvenc] [--force]` | Generates the missing or changed fixtures, re-checks the valid ones. `--nvenc` uses the T-187 evidence recipe (h264_nvenc/hevc_nvenc); the default is libx264/libx265, so no NVIDIA GPU is needed. `--force` regenerates. |
| `core [fixtures…] [scenarios…]` | Times the Core scenarios. No fixture = every valid one; no scenario = all. A fixture is a matrix key, or a path inside the root to a fixture listed in its `fixtures.json` or to a file under its `work\` folder. |
| `alt [fixtures…] [sections…]` | The ffmpeg-level alternatives behind the T-187 evidence: `floor io probe kf thumb smart join wf decode nofsi thumbframe`, and `smart2 [4k104 hevc10 hevc44 4k10cuda g10 4k10m540]`. |
| `selftest` | Runs the selftest cases below; exit 0 only when all pass. Needs the `4k` fixture. |
| `show [--session id\|last] [--fixture k] [--op text] [--scenario s] [--area a] [--markdown]` | Filters the ledger (the prototype's `show.py`). `--scenario` takes what a command ran: a `core` scenario, an `alt` section, `fixtures` or `selftest` (the row's `section`). `--area` takes the app area in the row's `scenario` field (`shared`, `split`, `join`, `bulk`, `fixtures`, `alt-*`, `error`), the only filter for rows written before `section` existed. An unknown `--scenario` is refused (exit 2), and so is a filter no row matches (exit 5, with the sections the ledger holds), so an empty table is never pasted into a Build log. |

Options: `--root <dir>` moves the fixture root (fixtures, work files and results move together; default
`%TEMP%\vsj-bench-fixtures`). It must be a new or empty folder, or an existing bench root (see below). `--runs <n>`
sets the repeats per measurement (default 3, as in T-187).

Exit codes: 0 ok · 1 an unexpected error, or a scenario that threw · 2 usage · 3 path or root refused by the
guard · 4 ffmpeg missing · 5 no valid fixture, or no ledger row matches `show` · 6 a check failed · 7 not enough
disk space · 8 the job object could not be set up.

**A failing scenario does not end the run.** Each `core` scenario and `alt` section runs on its own: one that
throws (a Core exception on a "before" build, an ffprobe error, a full disk) gets a `FAILED: <scenario>` row in
the ledger (area `error`, the exception in its note), the next scenario runs, and the command exits with the first
failure's code: 1 for an exception, or the bench's own code (7 for disk space, say). An error outside any scenario
prints `bench: unexpected error: …` and exits 1; the bench never ends on an unhandled-exception trace. A `join`
whose split failed gets a FAILED row of its own, since it has no parts to join.

## The fixture root and its guard

Everything lives under the root: the fixtures, `fixtures.json` (the ones that passed their self-check, with the
length and time stamp they had then, their stream start times and their keyframe list), `work/` (split parts,
cold copies, service caches; safe to delete), `errlogs/` (the engines' full-error logs, never `%LOCALAPPDATA%`),
`results/`, and the `.vsj-bench-root` marker.

**Only a bench root is used as one.** The bench writes and deletes its own scratch folders under its root
(`work\cache`, `work\split-*`, `work\bulk-out`, `selftest\`), so before `fixtures`, `core`, `alt` or `selftest`
touches anything it checks the folder: one with the `.vsj-bench-root` marker is a root; a new or empty folder
becomes one (the marker is written); a non-empty folder without the marker becomes one only when it already holds
the bench's own `fixtures.json` or results ledger (a root made before the marker existed). Anything else is refused
(exit 3) and left exactly as it was, so a mistyped `--root` can never delete someone's files. `show` only reads.

Every scenario resolves its inputs through one `FixtureRoot.Resolve(path)`, which accepts a path only when its
full, link-resolved form is inside the root. It looks at the path alone: a lexical test first, so a path outside
the root or a `..` escape is refused without touching the disk; then a walk over the components below the root
that reads their attributes and, for a junction or symbolic link, the link itself, never its target. A link that
leads outside the root refuses the path, and so does a link whose target is a volume or device path the guard
cannot place on the path alone: a junction to `\??\Volume{GUID}\…` (which .NET reports as `Volume{GUID}\…`, a
relative-looking path), `GLOBALROOT\…`, a leftover `\\?\` or `\\.\` prefix, or a rooted path with no drive. Then
a path input must also be something the bench made: a fixture listed in the root's `fixtures.json` (and unchanged
since its self-check) or a file under the root's `work\`; any other file, even one inside the root, is refused.
A refused input stops the command (exit 3) before any file but `fixtures.json` is opened or any ffmpeg/ffprobe is
started; the selftest checks that with the job's counters.

## How it measures

At start-up the bench puts its own process in a **Windows job object**. Every child that `FfmpegRunner` and
`FfprobeRunner` start inherits it, so the job's `JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION` gives the bytes
read and written, the CPU time and the number of processes started (`TotalProcesses`) of everything a scenario
ran, as a delta around it. Windows starts a console host (`conhost.exe`) for each ffmpeg/ffprobe child, in the
same job, so **one ffmpeg or ffprobe run counts as 2 processes** (a metadata probe shows `processes 2`; halve the
column to count runs). Scenarios run one at a time, so a delta belongs to its scenario. The bench's own reads
count too (the sample-table reader after T-194, for instance), which is what "bytes read" should mean; so does
the small pipe traffic between the bench and its children (a few hundred KB of ffprobe JSON at most). If the job
cannot be set up the bench stops; it has no fallback that starts processes itself.

Each measurement appends one JSON line to `<root>/results/timings.jsonl` (opened for append only, so earlier
lines never change): `session`, `scenario` (the app area: `shared`, `split`, `join`, `bulk`, `fixtures`, `alt-*`,
`error`; the name is kept so the T-187-era rows read the same), `section` (what the command ran, as the command
line names it: `probe` … `smart`, an `alt` section, `fixtures`, `selftest`; what `show --scenario` filters on),
`op`, `fixture`, `recipe`, `mode` (warm = file in the page cache, cold = a fresh unbuffered copy), `run`,
`wall_s`, `cpu_s`, `read_bytes`, `write_bytes`, `processes`, `load_others_cores`, `cores`, `git`, `core_build`,
`core_dirty_hint`, `note`.

**Which code a row ran.** `git` is the checked-out commit, read from `.git`. G-059's "after" figures are
measured on the uncommitted fix, so they carry the same sha as the "before" rows. `core_build` tells them apart:
it is the loaded VideoSplitJoiner.Core's module version id, which the deterministic compiler derives from the
compiled code, so it changes whenever Core's code changes. `core_dirty_hint` is a hint, not proof: true when a
source file under `src/Core` (or a root `Directory.Build.*`) was written after HEAD last moved (the HEAD
reflog's time stamp), null when that cannot be told. Both are read without starting a process, and the markdown
header shows them (`git … · Core build … (Core sources edited after that commit)`).

The **load sample** is the number of logical cores kept busy by
processes outside the bench's job during the measurement (system busy time from `GetSystemTimes` minus the job's
CPU time, over the wall time). Machine load moved T-187's numbers about 1.6x, so compare only before and after
from the same run, and quote the load column. Lines from one multi-step run (a Split load, a Bulk add) share that
run's usage. At the end of every command the bench prints this session's rows as a markdown table for a Build log.

**Scratch space.** Besides the fixtures (about 8 GB), a cold copy needs the fixture's size free, and `bulk` needs
about five times the fixture at its peak (two row copies plus three cold row copies: about 22 GB on `4k10m`, 4.5 GB
on `4k`). Each is checked before anything is written (exit 7 when short).

## The fixture matrix

Only the shapes some G-059 Build log names. The first five are the T-187 shapes, so "before" figures stay
comparable. Shapes a ticket checks for correctness rather than times (an MPEG-TS source, `+faststart`, hev1,
Main10, a rotated MP4, …) are generated inside that ticket's own tests, where `dotnet test` sees them.

| key | file | recipe (default) | used by |
|---|---|---|---|
| `4k` | `4k_h264_g2s_120s.mp4` | 3840x2160 H.264 High, 60 Mbit/s CBR, 120 s, 30 fps, GOP 2 s, 2 B-frames, AAC 48 kHz stereo, moov at end | all |
| `1080` | `1080_h264_g2s_120s.mp4` | the same at 1920x1080, 15 Mbit/s | T-192, T-193, T-195 |
| `hevc` | `4k_hevc_g2s_60s.mp4` | 4K HEVC Main, hvc1, 60 s, GOP 2 s, AAC | T-194, T-195 |
| `g10` | `4k_h264_g10s_60s.mp4` | 4K H.264, 60 s, GOP 10 s | T-191, T-192, T-195, T-196 |
| `4k10m` | `4k_h264_g2s_600s.mp4` | `4k` concatenated five times with `-c copy`, 600 s, ~4.5 GB; its video starts 21 ms after its audio (below) | T-190, T-191, T-192, T-193, T-194, T-219 |
| `ntsc` | `1080_h264_ntsc_g2s_120s.mp4` | `1080` at 30000/1001 fps (GOP 60 frames = 2.002 s, keyframes between whole seconds) | T-196 |
| `mkv` | `4k_h264_g2s_120s.mkv` | `4k` remuxed to Matroska; its video starts 21 ms after its audio (below) | T-194, T-195 |

Pixels come from lavfi `testsrc2` and the audio from `sine`, encoded by libx264 (`-preset veryfast`, CBR with
filler so the bitrate is a camera's, not a test pattern's) or libx265 (`-preset ultrafast`). `--nvenc` uses
h264_nvenc/hevc_nvenc preset p2 CBR, the T-187 evidence recipe. Every results line carries the recipe; absolute
timings differ between recipes, so a Build log says which one it used and compares within one run.

**Self-check.** A fixture counts only after ffprobe confirms its container, codec, profile, tag (avc1/hvc1),
size, frame rate, pixel format, B-frames (H.264: `has_b_frames` ≥ 1, or 0 for a `-bf 0` recipe), audio (AAC
48 kHz stereo), duration, GOP (the keyframe list from `MediaProbe`, evenly spaced), the moov box after mdat,
`format.start_time` (zero expected in every container: the audio starts at 0), the **video stream's own
`start_time`** and bitrate. `format.start_time` is the earliest stream's start, so it cannot show a late video
stream; the video start is checked to 1 ms against the recipe: 0 for an encode (the mp4 muxer's edit list hides
the AAC encoder's 1024-sample priming), and 1024/48000 s (0.021333 s; ffprobe shows 0.021029 in the mp4's 1/15360
time base and 0.021 in Matroska's milliseconds) for `4k10m` and `mkv`, because a `-c copy` concat or remux carries
the priming packet over and shifts every stream so the audio starts at 0. The app's own Join does the same to a
file it concatenates. The self-check line in the results file records `format.start_time`, the video and the audio
`start_time`; T-190's and T-219's Build logs quote them. `fixtures.json` also keeps the stream starts and the
keyframe list. A fixture that fails is deleted and not recorded. The generator pre-flights free space (4k10m needs
about 5 GB). A fixture checked by an older bench has no keyframe list in `fixtures.json`; `bench fixtures`
re-checks it (no re-encode) and `core` asks for that.

**Named times follow the file, not the nominal clock.** The "on keyframe" thumbnail time is the matrix time
snapped to the fixture's own keyframe list (300.021 s on `4k10m`, 60.021 s on `mkv`) and the mid-GOP time lies
the same distance past it. The split's part-frame plan runs from the first video frame (the probed video
`start_time`) through the snapped cuts, and its last part is what remains of the source's counted video frames,
so it is exact on `4k10m` (4500/7500/6000) and `mkv` (900/1200/1500) as on the rest.

## Scenarios (`bench core`)

| scenario | what it times |
|---|---|
| `probe` | `MediaProbe.ProbeAsync`, warm ×(runs+1) and cold |
| `kf` | `MediaProbe.GetKeyframesAsync` (the packet scan) warm and cold, and the ffprobe process alone |
| `wf` | `FfmpegWaveformService.GetPeaksAsync(1800)` |
| `thumb` | hover / Bulk chip / profile pictures on a keyframe and mid-GOP, a cache hit, a 10-bucket hover sweep, cold. Each row's note says where the service really seeked (`FfmpegThumbnailService` floors a request to its 1 s bucket, read back from the file it returns) and how far past the keyframe before it: on `4k10m` and `mkv` the "on keyframe" request at 300.021 s seeks to 300 s, 1.979 s past the keyframe at 298.021 s, so it decodes almost a whole GOP and is slower than the mid-GOP grab |
| `load` | `SplitViewModel.LoadAsync`'s order: probe, then keyframes and waveform in parallel. A keyframe scan that throws is noted FAILED and a null waveform NULL on its line, never recorded as a plain time |
| `split` | `SplitEngine` full split (segment muxer), part 2 only (per-segment), cold; plus each part's frame count against the plan (not timed). `work\split-<key>` holds exactly the full split's parts; the part-2-only run writes to `work\split-<key>-sel` and the cold run to `work\split-cold-<key>` |
| `join` | adding 3 clips (at once and one by one), `JoinEngine.JoinAsync` warm and cold, on the parts of one full split. `join` alone runs only that one full split (its row is in section `join`), not the per-segment and cold split rows |
| `bulk` | rows added (1, 3, 3 cold), then Lossless and Exact runs of 1 and 3 rows. The two extra rows are fresh copies every run (copied under a temporary name and renamed when complete, deleted at the end), after a free-space check |
| `smart` | at each named cut position: SmartCutEngine's head re-encode, tail copy and concat (its own builders), then the head variants (input seek, `veryfast`, `ultrafast`, NVENC, CUDA+NVENC) |

Named cut positions: `4k`, `1080`, `ntsc`, `mkv` 10.5 s and 104.5 s; `hevc` 10.5 s and 44.5 s; `g10` 12.5 s
and 44.5 s; `4k10m` 10.5 s and 540.5 s.

## Selftest cases

- a path inside the root is accepted; a path outside it, a `..` path that escapes it, a junction inside it that
  points outside, a junction to a volume-GUID path (`\??\Volume{GUID}\…`) that leads outside, and a symbolic link
  (when the account may make one) that points outside are refused — each with **exit 3**, and a job delta of
  **0 processes started and 0 bytes read**; a control shows the same junction path is read under a root that holds
  its target and lists it in its `fixtures.json`;
- a media file inside the root that is not in its `fixtures.json` (nor under `work\`) is refused with exit 3, no
  process started and nothing read but `fixtures.json`;
- `ffprobe -show_packets` on `4k` reads at least 90% of the file; a metadata probe reads under 10 MB;
- the `4k` recipe with `-bf 0` (at a selftest length of 4 s) is rejected by its self-check on B-frames alone and
  not recorded; the same recipe with its B-frames passes;
- with the ffmpeg location pointed at an empty folder (a selftest-only override), `fixtures` exits non-zero with
  the fetch hint and records nothing as valid;
- a second run grows `timings.jsonl` and leaves the earlier bytes as they were;
- `show --scenario probe` prints that run's rows, an unknown `--scenario` is refused (exit 2) and a filter no row
  matches is refused (exit 5);
- `core` on an empty fixture root refuses with a message; a non-empty folder without the marker is refused as
  `--root` (exit 3) and its `work\cache` and `selftest` files are left alone;
- a scenario that throws (injected through a selftest-only switch, so the case does not depend on a Core bug) gets a
  FAILED row, the next scenario still runs, and the run exits 1; an exception outside any scenario exits 1 with
  `bench: unexpected error: …`;
- the selftest's own runs (in `<root>/selftest/root`) leave the main root's ledger alone (`--root` keeps a root's
  fixtures and results together).

## T-187 findings and how to reproduce them

Each was reproduced on the bench's own libx264/libx265 fixtures on 2026-10-04 (git `bd06844`, 5-19 other cores
busy). Read the named line of the output.

| finding | ticket | repro | on the bench fixtures |
|---|---|---|---|
| **HEVC Exact output is corrupt after the joint.** The final keeps the libx265 head's hev1 extradata instead of the source's hvc1, and the copied tail decodes with errors (T-187: 250-316 error lines, tail PSNR 11-15 dB). | T-189 | `bench alt smart2 hevc10` → the `current engine args` row: `joint decode`, `tail 2s vs source PSNR` | 322 decode error lines (`chroma_log2_weight_denom 10 is invalid`), tail PSNR 23.6 dB |
| **The H.264 Exact tail lands 80 ms late.** `TailCopy` (`-ss` before `-i`, `-c copy`, `-avoid_negative_ts make_zero`) gives the tail a video start of 0.080 s against audio at 0, so about 80 ms of audio overlaps at the joint. | T-190 | `bench alt smart2 4k104` → `tail starts v=… a=…` | `v=0.080013 a=0.000000`; tail PSNR 26.4 dB |
| **The Exact head decodes from the start of the file.** `SmartCutArgsBuilder.HeadReencode` uses an output seek while its comment claims a one-GOP decode, so the head's cost grows with the cut position; an input seek gives the same frames at any position. | T-191 | `bench core 4k smart` → `head re-encode [104.5s..]` (wall time, `read %` of the file) against `head variant: -ss … -i in (input seek)` | 12.5-12.8 s and 89% read at 104.5 s, 2.9-3.2 s at 10.5 s; input seek 1.7-2.1 s, 3% read; `4k10m` at 540.5 s 53-56 s, 91% read. With `--nvenc` fixtures (the T-187 recipe) 22.2 s at 104.5 s (T-187: 22.8 s) |
| **The full-file split ends each part one GOP late on a B-frame source** (plan review). The segment muxer cuts 3-part splits at the wrong keyframe when the source has B-frames (T-187: 120/90/90 frames instead of 90/90/120 on the test suite's `MediaFixtures` recipe; `-bf 0` or `-segment_time_delta 0.016` fix it). | T-219 | `bench core 1080 split` → `part video frames (segment muxer)`: actual against planned | `4k`/`1080`/`mkv` 960/1200/1440 against 900/1200/1500; `g10` 900/600/300 against 600/600/600; `hevc` 660/600/540 against 600/600/600 |
| **…and when the last cut snaps to the source's final keyframe, Split fails outright** (found at T-188 review, 2026-10-05). The same one-GOP-late cutting leaves the segment muxer one segment short, and `SplitEngine` throws `SplitException: Expected segment '…part002.mp4' was not produced by ffmpeg (got fewer segments than planned)` (`SplitEngine.cs:484`) instead of writing a valid file one GOP late. | T-219 | make a 3 s B-frame clip under a bench root's `work\` (`ffmpeg -f lavfi -i testsrc2=size=320x240:rate=30:duration=3 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=3 -map 0:v:0 -map 1:a:0 -c:v libx264 -g 30 -keyint_min 30 -sc_threshold 0 -bf 2 -c:a aac -t 3 <root>\work\bf2_3s.mp4`), then `bench core --root <root> --runs 1 <root>\work\bf2_3s.mp4 split` (its cuts snap to 1 s and 2 s, the last keyframe) → the `FAILED: split` row; the same clip with `-bf 0` splits 30/30/30 as planned | `FAILED: split` (`SplitException`, exit 1) with `-bf 2`; 30/30/30 against 30/30/30 with `-bf 0` |
| **A lossless cut of a TS source starts one GOP late** (plan review). Keyframe times are source pts, but `PerSegment`'s `-ss` is an offset from the format `start_time` (about 1.4 s on TS). | T-219 | not in the matrix (T-217 adds `ts` if its trigger fires): remux a fixture to MPEG-TS under the root (`ffmpeg -i <root>\1080_h264_g2s_120s.mp4 -c copy <root>\work\t.ts`), read its `format.start_time` with ffprobe, and compare the first frame of `SplitArgsBuilder.PerSegment` output at a keyframe time with and without `start_time` subtracted | not measured here |
