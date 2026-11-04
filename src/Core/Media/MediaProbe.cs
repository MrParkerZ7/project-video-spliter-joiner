using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Media;

/// <summary>
/// Probes media files for duration, streams, codecs, and keyframe positions, and snaps
/// arbitrary times to the nearest keyframe (so a cut lands on a copyable boundary).
/// Built on the T-002 <see cref="IFfprobeRunner"/>.
/// </summary>
public interface IMediaProbe
{
    /// <summary>
    /// Probe <paramref name="path"/> for its container, duration, and streams. A corrupt or
    /// non-media file returns <see cref="ProbeResult.ProbeFailed"/> — never throws for a bad
    /// file. Cancellation still surfaces as <see cref="OperationCanceledException"/>. A successful
    /// result is cached keyed by (path, file mtime, file length), like the keyframes, so a repeat
    /// call on an unchanged file runs no second ffprobe (T-193); failures are never cached.
    /// </summary>
    Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Return the video keyframe timestamps of <paramref name="path"/>, sorted ascending and
    /// distinct. The result is cached keyed by (path, file mtime, file length) so repeat calls
    /// on an unchanged file are cheap.
    /// </summary>
    Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Snap <paramref name="requested"/> to the nearest entry in <paramref name="keyframes"/>.
    /// Ties resolve to the EARLIER keyframe. A request past the last keyframe clamps to the
    /// last; before the first clamps to the first.
    /// </summary>
    KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested);

    /// <summary>
    /// Average spacing between consecutive keyframes (the mean GOP length). Useful for warning
    /// the user when snapping will be coarse. Returns <see cref="TimeSpan.Zero"/> for fewer
    /// than two keyframes.
    /// </summary>
    TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes);
}

/// <inheritdoc cref="IMediaProbe" />
public sealed class MediaProbe : IMediaProbe, IVideoFrameProbe, ISeekLandingProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IFfprobeRunner _ffprobe;
    private readonly ConcurrentDictionary<string, IReadOnlyList<TimeSpan>> _keyframeCache = new();

    // T-193: successful metadata probes, keyed exactly like _keyframeCache (BuildCacheKey: full path | mtime ticks |
    // length), so Join, Split and Bulk Cut stop re-running ffprobe for a file they already probed. Success only: a
    // ProbeFailed or a cancellation is never stored, so a retry probes again. No in-flight dedup (none of the callers
    // probes one file twice at once; two simultaneous calls would each run ffprobe and store equal results). The
    // MediaInfo record is immutable (IReadOnlyList streams), so callers can share the cached instance.
    private readonly ConcurrentDictionary<string, ProbeResult.ProbeSucceeded> _probeCache = new();

    // T-093: in-flight dedup. A keyframe scan for a given file (keyed exactly like _keyframeCache)
    // registers its running Task here so a SECOND concurrent caller for the same key awaits the
    // SAME scan instead of launching a duplicate ffprobe pass. The shared scan runs on an
    // INDEPENDENT token (not any one caller's CT), so a caller that cancels its own await can never
    // cancel the shared scan for the other awaiters (cancellation-safety). T-239: the entry is stored
    // BEFORE the scan starts (GetOrStartSharedScan: TryAdd) and removed by INSTANCE before the shared task
    // settles (RunSharedScanAsync), whether the scan succeeded or failed; only SUCCESSFUL results are promoted
    // to _keyframeCache. So a settled scan is never left registered, a failed/cancelled shared scan is never
    // served to a later call, and a later retry re-scans cleanly.
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<TimeSpan>>> _inFlightScans = new();

    /// <summary>
    /// Test-only seam (T-239): called with the shared scan's Task right after the scan has been started and before
    /// the registering call returns, so a test can hold that call until the scan has finished.
    /// </summary>
    internal Action<Task>? AfterScanStartedForTest { get; set; }

    /// <summary>
    /// Which scan path the LAST <see cref="GetKeyframesAsync"/> call actually used. Internal —
    /// exposed only so tests can assert the sample-table path ran, then the fast packet path (and
    /// that each fallback fires when the one before it yields nothing). Not part of the public
    /// contract; not reset by cache hits.
    /// </summary>
    internal KeyframeScanPath LastScanPath { get; private set; }

    /// <summary>
    /// Test seam (T-194): opens the file for the sample-table reader. The default is read-only and shares the file with writers and
    /// deleters (<c>FileShare.ReadWrite | Delete</c>), so the read can never block a Replace-originals swap, a rename, a delete or
    /// a file still being written.
    /// </summary>
    internal Func<string, Stream> OpenKeyframeSource { get; set; } = OpenForKeyframeIndex;

    /// <summary>
    /// Test seam (T-194): the sample-table reader. The default is <see cref="IsoBmffKeyframeIndex.TryRead(Func{Stream}, CancellationToken)"/>.
    /// A test substitutes one that returns a list the file does not hold, to see the anchor refuse it.
    /// </summary>
    internal Func<Func<Stream>, CancellationToken, SampleTableKeyframes?> KeyframeIndexReader { get; set; } =
        (open, ct) => IsoBmffKeyframeIndex.TryRead(open, ct);

    /// <summary>Create a probe over an existing ffprobe runner.</summary>
    public MediaProbe(IFfprobeRunner ffprobe)
    {
        _ffprobe = ffprobe ?? throw new ArgumentNullException(nameof(ffprobe));
    }

    /// <inheritdoc />
    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ProbeResult.Failure("Path is empty.");
        }

        if (!File.Exists(path))
        {
            return ProbeResult.Failure($"File does not exist: '{path}'.");
        }

        // T-193: the lookup comes after both guards, so an empty path or a missing file is answered without the cache
        // (a cached entry never masks a deleted file). The key is read before ffprobe runs, so a file that changes
        // during the probe is cached under its old version and the next call probes again. A file that vanishes
        // between the guard and the key read has no key: it is probed uncached, as before the cache existed.
        // A cancelled token still throws on a cache hit, as it did when every call reached ffprobe (I8), so the
        // only change the cache makes is the ffprobe process it saves.
        var hasKey = TryBuildCacheKey(path, out var cacheKey);
        if (hasKey && _probeCache.TryGetValue(cacheKey, out var cached))
        {
            ct.ThrowIfCancellationRequested();
            return cached;
        }

        var args = FfmpegArgs.ForFfprobe()
            .Raw("-show_streams", "-show_format", "-print_format", "json")
            .Input(path);

        string json;
        try
        {
            json = await _ffprobe.RunJsonAsync(args, ct).ConfigureAwait(false);
        }
        catch (FfprobeException ex)
        {
            // A non-media / corrupt file makes ffprobe exit non-zero — convert to a typed failure.
            return ProbeResult.Failure($"ffprobe could not read '{path}': {ex.Message}");
        }

        FfprobeShowRoot? root;
        try
        {
            root = JsonDeserialize<FfprobeShowRoot>(json);
        }
        catch (JsonException ex)
        {
            return ProbeResult.Failure($"ffprobe output for '{path}' was not valid JSON: {ex.Message}");
        }

        if (root?.Streams is null || root.Streams.Count == 0)
        {
            return ProbeResult.Failure($"No media streams found in '{path}'.");
        }

        var video = new List<StreamInfo>();
        var audio = new List<StreamInfo>();
        var streamDurations = new List<TimeSpan>();
        var extraStreams = 0;

        foreach (var s in root.Streams)
        {
            var info = MapStream(s);
            if (info.IsVideo)
            {
                video.Add(info);
                if (s.Disposition is { AttachedPic: 1 })
                {
                    extraStreams++; // a cover picture is a video stream that is not the picture
                }
            }
            else if (info.IsAudio)
            {
                audio.Add(info);
            }
            else
            {
                extraStreams++; // subtitle, data, attachment (T-224)
            }

            if (TryParseSeconds(s.Duration, out var sd))
            {
                streamDurations.Add(sd);
            }
        }

        var duration = ResolveDuration(root.Format?.Duration, streamDurations);
        var container = root.Format?.FormatName ?? "unknown";

        // T-219: the time base of ffmpeg's input -ss (an offset from format.start_time); zero when absent or unparsable.
        var startTime = TryParseSeconds(root.Format?.StartTime, out var st) ? st : TimeSpan.Zero;

        var info2 = new MediaInfo(duration, container, video.AsReadOnly(), audio.AsReadOnly(), startTime, extraStreams);
        var success = new ProbeResult.ProbeSucceeded(info2);
        if (hasKey)
        {
            _probeCache[cacheKey] = success; // success only — every failure path above returned before this line
        }

        return success;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimeSpan>> GetKeyframesAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is empty.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Cannot read keyframes; file does not exist.", path);
        }

        var cacheKey = BuildCacheKey(path);
        if (_keyframeCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        // T-093: in-flight dedup. Get (or start) the ONE shared scan for this key. GetOrStartSharedScan
        // starts the scan on an INDEPENDENT token so no single caller's CT can tear it down for the
        // others; this caller then awaits it through its OWN CT via WaitAsync, so cancelling here
        // throws for THIS caller only and leaves the shared scan running for the rest.
        var scan = GetOrStartSharedScan(path, cacheKey);

        // WaitAsync observes the shared task through this caller's CT: a cancellation here throws an
        // OperationCanceledException for THIS awaiter without cancelling `scan` itself. When the CT is
        // already default/None this is effectively a straight await.
        return await scan.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the ONE shared scan for this key, starting it when none is registered (T-093, T-239). The entry is
    /// stored BEFORE the scan starts, so the scan's own removal can never run ahead of the registration: whoever
    /// wins <c>TryAdd</c> owns the scan, and the removal below only ever removes that very entry.
    /// </summary>
    private Task<IReadOnlyList<TimeSpan>> GetOrStartSharedScan(string path, string cacheKey)
    {
        while (true)
        {
            if (_inFlightScans.TryGetValue(cacheKey, out var existing))
            {
                return existing;
            }

            var completion = new TaskCompletionSource<IReadOnlyList<TimeSpan>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inFlightScans.TryAdd(cacheKey, completion.Task))
            {
                continue; // another caller registered first (or the entry just settled): look again
            }

            _ = RunSharedScanAsync(path, cacheKey, completion);
            AfterScanStartedForTest?.Invoke(completion.Task);
            return completion.Task;
        }
    }

    /// <summary>
    /// The single shared keyframe scan for one cache key (T-093). Runs on its OWN token
    /// (<see cref="CancellationToken.None"/>) so it is not tied to any individual caller's
    /// cancellation, promotes a SUCCESSFUL result into <see cref="_keyframeCache"/>, and removes ITS OWN
    /// <see cref="_inFlightScans"/> entry (compared by instance, T-239) BEFORE it completes the shared task — so a
    /// failed/cancelled scan caches nothing, is never served to a later call, and a later call re-scans from scratch.
    /// </summary>
    private async Task RunSharedScanAsync(string path, string cacheKey, TaskCompletionSource<IReadOnlyList<TimeSpan>> completion)
    {
        IReadOnlyList<TimeSpan>? scanned = null;
        Exception? failure = null;
        try
        {
            scanned = await ScanSharedAsync(path, cacheKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Drop the entry first (only if it is still this scan's task), then settle: a caller that sees the result
        // can never find the settled entry still registered. On success the result already lives in _keyframeCache.
        _inFlightScans.TryRemove(new KeyValuePair<string, Task<IReadOnlyList<TimeSpan>>>(cacheKey, completion.Task));
        if (failure is null)
        {
            completion.SetResult(scanned!);
        }
        else
        {
            completion.SetException(failure);
        }
    }

    private async Task<IReadOnlyList<TimeSpan>> ScanSharedAsync(string path, string cacheKey)
    {
        // Run the scan body off the caller's synchronous stretch.
        await Task.Yield();

        // First (T-194): an MP4/MOV file lists its keyframes in its own sample table, which is read in a few
        // milliseconds without touching the media data; two short ffprobe packet windows at the ends of the file then
        // check the list against the demuxer. Anything the reader cannot prove, and any disagreement, leaves this null
        // and the whole-file scan below runs exactly as it always did.
        IReadOnlyList<TimeSpan>? result = await TryKeyframesFromSampleTableAsync(path).ConfigureAwait(false);
        if (result is not null)
        {
            LastScanPath = KeyframeScanPath.Index;
        }

        // Then (T-031): read keyframe PACKETS at the demux level — no frame decoding, so the
        // scan is fast even on 4K clips. Fall back to the frame-decode scan if the packet query
        // fails or yields zero keyframes, so correctness never regresses. The shared scan is not
        // bound to a caller CT (CancellationToken.None) — see the dedup note on _inFlightScans.
        if (result is null)
        {
            try
            {
                var packetKeyframes = await ScanKeyframesFromPacketsAsync(path, CancellationToken.None).ConfigureAwait(false);
                if (packetKeyframes.Count > 0)
                {
                    LastScanPath = KeyframeScanPath.Packets;
                    result = packetKeyframes;
                }
            }
            catch (FfprobeException)
            {
                // Packet query failed outright — fall through to the frame scan below.
            }
        }

        if (result is null)
        {
            result = await ScanKeyframesFromFramesAsync(path, CancellationToken.None).ConfigureAwait(false);
            LastScanPath = KeyframeScanPath.Frames;
        }

        // Success only: promote to the durable cache so repeat calls are cheap.
        _keyframeCache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Test-only: run ONLY the fast packet path (no fallback, no cache). Used by parity/perf tests.
    /// </summary>
    internal Task<IReadOnlyList<TimeSpan>> ScanKeyframesFromPacketsForTestAsync(string path, CancellationToken ct = default) =>
        ScanKeyframesFromPacketsAsync(path, ct);

    /// <summary>
    /// Test-only: run ONLY the decode-based frame path (no fallback, no cache). Used by parity/perf tests.
    /// </summary>
    internal Task<IReadOnlyList<TimeSpan>> ScanKeyframesFromFramesForTestAsync(string path, CancellationToken ct = default) =>
        ScanKeyframesFromFramesAsync(path, ct);

    /// <summary>
    /// Fast path: demux-level keyframe scan via <c>-show_packets</c>. Keeps packets whose flags
    /// contain the <c>K</c> keyframe marker; timestamp = <c>pts_time</c> (falling back to
    /// <c>dts_time</c>). Returns sorted-distinct times. Packets arrive in DTS order, so sorting
    /// is required.
    /// </summary>
    private async Task<IReadOnlyList<TimeSpan>> ScanKeyframesFromPacketsAsync(string path, CancellationToken ct)
    {
        var json = await _ffprobe.RunJsonAsync(PacketScanArgs(path), ct).ConfigureAwait(false);
        var root = JsonDeserialize<FfprobePacketsRoot>(json);
        return KeyframeTimes(root).ToList().AsReadOnly();
    }

    /// <summary>
    /// The args of the packet query: the first video stream's packets with the time and flags the keyframe scan keeps. The whole-file
    /// scan runs it as it is; the sample-table check (T-194) adds <c>-read_intervals</c>, and <c>-nofind_stream_info</c> because a
    /// window of one GOP needs none of the stream probing ffprobe does first.
    /// </summary>
    private static FfmpegArgs PacketScanArgs(string path, string? readInterval = null)
    {
        var args = FfmpegArgs.ForFfprobe()
            .Raw(
                "-select_streams", "v:0",
                "-show_packets",
                "-show_entries", "packet=pts_time,dts_time,flags",
                "-print_format", "json");
        if (readInterval is not null)
        {
            args.Raw("-read_intervals", readInterval, "-nofind_stream_info");
        }

        return args.Input(path);
    }

    /// <summary>
    /// The sorted, distinct times of the packets flagged as keyframes: <c>pts_time</c>, <c>dts_time</c> when a packet has no pts.
    /// </summary>
    private static SortedSet<TimeSpan> KeyframeTimes(FfprobePacketsRoot? root)
    {
        var times = new SortedSet<TimeSpan>();
        if (root?.Packets is not null)
        {
            foreach (var p in root.Packets)
            {
                if (!IsKeyframeFlag(p.Flags))
                {
                    continue;
                }

                var raw = p.PtsTime ?? p.DtsTime;
                if (TryParseSeconds(raw, out var t))
                {
                    times.Add(t);
                }
            }
        }

        return times;
    }

    // ---- The sample-table path (T-194) ----------------------------------------------------------------------------------

    /// <summary>How far past a keyframe the anchor windows reach: a window must hold the packet of the keyframe itself.</summary>
    private static readonly TimeSpan AnchorMargin = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The end of the tail window, in seconds: far past any file. An interval with a start and no end (<c>9.001%</c>) is not
    /// deterministic in ffprobe (the bundled n7.1.5 listed no packet at all in 14-23 of 100 runs on one unchanged file, and in
    /// none of 100 with an end; its debug log was the same in both cases, as if the unset end were read as garbage), so the
    /// window states its end and still reads to the end of the file.
    /// </summary>
    private const string TailWindowEnd = "1000000000";

    private static Stream OpenForKeyframeIndex(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    /// <summary>
    /// The keyframes of an MP4/MOV file from its sample table, or null when the reader declines the file or ffprobe disagrees with
    /// the table at either end. Inside the one shared scan of the file, so there is still one scan per file per key (T-093): the
    /// reader runs here, then two short ffprobe packet windows — one from the start of the file to just past the second keyframe,
    /// one from just past the last keyframe to the end — and the table is used only when both windows list exactly the keyframes
    /// the table puts there (<see cref="AnchorAgrees"/>). They cost about a GOP each, bounded by the GOP, not by the file.
    /// A failing window (a non-zero exit, output that is not JSON) is a disagreement; a cancellation or a missing ffprobe is
    /// not, and escapes as it does from the packet scan.
    /// </summary>
    private async Task<IReadOnlyList<TimeSpan>?> TryKeyframesFromSampleTableAsync(string path)
    {
        // On the thread pool: the shared scan's continuation after `Task.Yield` returns to the caller's context, which is the
        // UI thread for Split and Bulk Cut, and the reader is blocking file I/O (a network share, a cold disk).
        var read = await Task.Run(() => KeyframeIndexReader(() => OpenKeyframeSource(path), CancellationToken.None)).ConfigureAwait(false);
        if (read is null || read.Keyframes.Count < 2)
        {
            return null;
        }

        var index = read.Keyframes;
        var headEnd = index[1] + AnchorMargin;
        var tailStart = index[^1] + AnchorMargin;
        if (tailStart <= TimeSpan.Zero)
        {
            return null; // -read_intervals cannot start before zero
        }

        // Both windows run at once: each is mostly the start of an ffprobe process.
        var head = ReadPacketWindowAsync(path, "%" + FormatInterval(headEnd));
        var tail = ReadPacketWindowAsync(path, FormatInterval(tailStart) + "%" + TailWindowEnd);
        try
        {
            await Task.WhenAll(head, tail).ConfigureAwait(false);
        }
        catch
        {
            // Each task is looked at on its own below, so that a failure of one window does not hide the other's.
        }

        var headWindow = await SettleAsync(head).ConfigureAwait(false);
        var tailWindow = await SettleAsync(tail).ConfigureAwait(false);
        return headWindow is not null && tailWindow is not null && AnchorAgrees(index, headWindow, tailWindow)
            ? index
            : null;
    }

    private static string FormatInterval(TimeSpan time) =>
        time.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>What an anchor window listed: the time of its first packet, and the keyframes among its packets.</summary>
    internal sealed record PacketWindow(TimeSpan? FirstTime, IReadOnlyList<TimeSpan> Keyframes);

    private async Task<PacketWindow> ReadPacketWindowAsync(string path, string interval)
    {
        var json = await _ffprobe.RunJsonAsync(PacketScanArgs(path, interval), CancellationToken.None).ConfigureAwait(false);
        var root = JsonDeserialize<FfprobePacketsRoot>(json);
        TimeSpan? first = null;
        if (root?.Packets is { Count: > 0 } packets && TryParseSeconds(packets[0].PtsTime ?? packets[0].DtsTime, out var firstTime))
        {
            first = firstTime;
        }

        return new PacketWindow(first, KeyframeTimes(root).ToList().AsReadOnly());
    }

    private static async Task<PacketWindow?> SettleAsync(Task<PacketWindow> window)
    {
        try
        {
            return await window.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FfprobeException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when ffprobe's two windows list exactly the keyframes the sample table puts there, TimeSpan for TimeSpan (T-194).
    /// <list type="bullet">
    /// <item>The HEAD window starts at the beginning of the file and ends just past the second keyframe: it must list every
    /// keyframe of the table before that end, and no other. There is no lower bound, so a keyframe the table has and ffprobe
    /// does not list (one before an edit list's start, say) is a disagreement.</item>
    /// <item>The TAIL window starts just past the last keyframe: ffprobe seeks back to the keyframe at or before the start and
    /// lists every packet from there, so it must list every keyframe of the table from its FIRST packet on, and no other. A last
    /// "keyframe" that is not one makes the seek land on the one before it, and the table then has an entry the window lacks.</item>
    /// <item>Each window must list at least one keyframe.</item>
    /// </list>
    /// What the windows cannot see is an error in the middle of the table that leaves both ends right.
    /// </summary>
    internal static bool AnchorAgrees(IReadOnlyList<TimeSpan> index, PacketWindow head, PacketWindow tail)
    {
        if (index.Count < 2 || head.Keyframes.Count == 0 || tail.Keyframes.Count == 0 || tail.FirstTime is not { } tailFirst)
        {
            return false;
        }

        var headEnd = index[1] + AnchorMargin;
        return head.Keyframes.SequenceEqual(index.Where(k => k < headEnd))
               && tail.Keyframes.SequenceEqual(index.Where(k => k >= tailFirst));
    }

    /// <summary>
    /// Fallback path: decode-level keyframe scan via <c>-skip_frame nokey</c> (the pre-T-031
    /// behaviour). Slower on high-resolution clips but always correct; used when the packet query
    /// fails or returns no keyframes.
    /// </summary>
    private async Task<IReadOnlyList<TimeSpan>> ScanKeyframesFromFramesAsync(string path, CancellationToken ct)
    {
        // Read only keyframes of the first video stream: -skip_frame nokey drops non-keyframes.
        var args = FfmpegArgs.ForFfprobe()
            .Raw(
                "-select_streams", "v:0",
                "-skip_frame", "nokey",
                "-show_entries", "frame=pts_time,best_effort_timestamp_time,key_frame,media_type",
                "-print_format", "json")
            .Input(path);

        var json = await _ffprobe.RunJsonAsync(args, ct).ConfigureAwait(false);
        var root = JsonDeserialize<FfprobeFramesRoot>(json);

        var times = new SortedSet<TimeSpan>();
        if (root?.Frames is not null)
        {
            foreach (var f in root.Frames)
            {
                // With -skip_frame nokey every returned frame is a keyframe, but be defensive.
                if (f.KeyFrame is 0)
                {
                    continue;
                }

                var raw = f.PtsTime ?? f.PktPtsTime ?? f.BestEffortTimestampTime;
                if (TryParseSeconds(raw, out var t))
                {
                    times.Add(t);
                }
            }
        }

        return times.ToList().AsReadOnly();
    }

    /// <summary>
    /// How far past the end of a requested interval the frame reads go: for a frame at or after the request
    /// (<see cref="FirstVideoFrameAtOrAfterAsync"/>), and past the end of a window (<see cref="GetVideoFrameTimesAsync"/>), where it
    /// is what puts the packets of an OPEN GOP in the read - the key frame that ends the window is decoded BEFORE the leading
    /// B-frames that are displayed before it, so a read that stops at the key frame misses frames the window holds.
    /// </summary>
    private static readonly TimeSpan FrameSearchWindow = TimeSpan.FromSeconds(2);

    /// <summary>A frame this close before the requested time counts as at it (a timestamp is printed to six decimals).</summary>
    internal static readonly TimeSpan FrameTimeTolerance = TimeSpan.FromMilliseconds(1);

    /// <inheritdoc />
    public async Task<TimeSpan?> FirstVideoFrameAtOrAfterAsync(string path, TimeSpan time, CancellationToken ct = default)
    {
        var times = await ReadFrameTimesAsync(path, time, time + FrameSearchWindow, ct).ConfigureAwait(false);
        if (times is null)
        {
            return null;
        }

        TimeSpan? first = null;
        foreach (var t in times)
        {
            if (t >= time - FrameTimeTolerance && (first is null || t < first))
            {
                first = t;
            }
        }

        return first;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimeSpan>?> GetVideoFrameTimesAsync(
        string path, TimeSpan from, TimeSpan to, CancellationToken ct = default)
    {
        // The read runs FrameSearchWindow past `to`: the frames BEFORE `to` can sit after it in decode order (see above).
        var times = await ReadFrameTimesAsync(path, from, to + FrameSearchWindow, ct).ConfigureAwait(false);
        if (times is null)
        {
            return null;
        }

        var inWindow = times.Where(t => t >= from - FrameTimeTolerance && t < to - FrameTimeTolerance).ToList();
        inWindow.Sort();
        return inWindow.AsReadOnly();
    }

    /// <summary>How many leading bytes of a keyframe packet are read for its NAL units (parameter sets and SEI come before the slice).</summary>
    private const int KeyframeBytesRead = 16 * 1024;

    /// <inheritdoc />
    public async Task<bool?> IsIdrKeyframeAsync(string path, TimeSpan keyframeTime, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || keyframeTime < TimeSpan.Zero)
        {
            return null;
        }

        // -read_intervals START%+#1: ffprobe seeks back to the keyframe at or before START and prints that one packet. START is a
        // millisecond past the keyframe so the seek lands on it whichever way its printed time rounded, and the packet's own time is
        // checked below: a keyframe that is not at the time asked (the list was wrong) is no answer.
        var interval = string.Create(
            CultureInfo.InvariantCulture, $"{(keyframeTime + FrameTimeTolerance).TotalSeconds:0.######}%+#1");
        var args = FfmpegArgs.ForFfprobe()
            .Raw(
                "-v", "error",
                "-select_streams", "v:0",
                "-read_intervals", interval,
                "-show_entries", "packet=pts_time,dts_time,flags,size,data:stream=is_avc,nal_length_size",
                "-show_data",
                "-print_format", "json")
            .Input(path);

        string json;
        try
        {
            json = await _ffprobe.RunJsonAsync(args, ct).ConfigureAwait(false);
        }
        catch (FfprobeException)
        {
            return null;
        }

        FfprobePacketsRoot? root;
        try
        {
            root = JsonDeserialize<FfprobePacketsRoot>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?.Packets is not { Count: > 0 } packets)
        {
            return null;
        }

        var packet = packets[0];
        if (!IsKeyframeFlag(packet.Flags)
            || !TryParseSeconds(packet.PtsTime ?? packet.DtsTime, out var time)
            || (time - keyframeTime).Duration() > IdrKeyframeTimeTolerance)
        {
            return null;
        }

        // The packet's framing comes from the stream (is_avc, nal_length_size) and its size from the packet: the bytes are read in
        // that one framing only, with every length held against the size (H264Packet). A stream that says neither is no answer.
        int? lengthSize = null;
        if (root.Streams is { Count: > 0 } streams)
        {
            var framing = streams[0];
            if (framing.IsAvc == "false")
            {
                lengthSize = 0;
            }
            else if (framing.IsAvc == "true"
                     && int.TryParse(framing.NalLengthSize, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                     && n is >= 1 and <= 4)
            {
                lengthSize = n;
            }
        }

        if (lengthSize is not { } nalLengthSize
            || !long.TryParse(packet.Size, NumberStyles.None, CultureInfo.InvariantCulture, out var packetSize))
        {
            return null;
        }

        var kind = H264Packet.FirstPictureKind(
            H264Packet.BytesFromHexDump(packet.Data, KeyframeBytesRead) ?? Array.Empty<byte>(), nalLengthSize, packetSize);
        return kind switch
        {
            H264PictureKind.Idr => true,
            H264PictureKind.NonIdr => false,
            _ => null,
        };
    }

    /// <inheritdoc />
    public async Task<TimeSpan?> GetSeekLandingAsync(string path, TimeSpan aim, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || aim < TimeSpan.Zero)
        {
            return null;
        }

        // -read_intervals AIM%+#1: ffprobe seeks the way ffmpeg's input -ss and the concat demuxer's inpoint do (avformat_seek_file to the
        // aim, landing on the last keyframe whose index time is not after it) and prints the first video packet it reads there.
        var interval = string.Create(CultureInfo.InvariantCulture, $"{aim.TotalSeconds:0.######}%+#1");
        var args = FfmpegArgs.ForFfprobe()
            .Raw(
                "-v", "error",
                "-select_streams", "v:0",
                "-read_intervals", interval,
                "-show_entries", "packet=pts_time,dts_time,flags",
                "-print_format", "json")
            .Input(path);

        string json;
        try
        {
            json = await _ffprobe.RunJsonAsync(args, ct).ConfigureAwait(false);
        }
        catch (FfprobeException)
        {
            return null;
        }

        FfprobePacketsRoot? root;
        try
        {
            root = JsonDeserialize<FfprobePacketsRoot>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?.Packets is not { Count: > 0 } packets)
        {
            return null;
        }

        var packet = packets[0];
        return IsKeyframeFlag(packet.Flags) && TryParseSeconds(packet.PtsTime ?? packet.DtsTime, out var time) ? time : null;
    }

    /// <summary>How far the packet <see cref="IsIdrKeyframeAsync"/> reads may be from the keyframe time it was asked about.</summary>
    private static readonly TimeSpan IdrKeyframeTimeTolerance = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// The frame time of every video packet ffprobe lists when it reads <c>[from, readTo)</c> of <paramref name="path"/> (it seeks
    /// back to the key frame before <paramref name="from"/>, so earlier packets are listed too, in decode order), or null when
    /// the answer cannot be given. A demux-level read, packet times only, no decoding.
    /// </summary>
    private async Task<List<TimeSpan>?> ReadFrameTimesAsync(string path, TimeSpan from, TimeSpan readTo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || from < TimeSpan.Zero)
        {
            return null;
        }

        // -read_intervals START%END (both absolute: a "+" END would count from the first packet read, which is the keyframe
        // BEFORE START). ffprobe seeks back to that keyframe, so packets before START are listed too; the callers drop them.
        // Packet times only, no decoding: the cost is the GOP before START plus the window, not the file.
        var interval = string.Create(
            CultureInfo.InvariantCulture, $"{from.TotalSeconds:0.######}%{readTo.TotalSeconds:0.######}");
        var args = FfmpegArgs.ForFfprobe()
            .Raw(
                "-v", "error",
                "-select_streams", "v:0",
                "-read_intervals", interval,
                "-show_entries", "packet=pts_time,dts_time",
                "-print_format", "json")
            .Input(path);

        string json;
        try
        {
            json = await _ffprobe.RunJsonAsync(args, ct).ConfigureAwait(false);
        }
        catch (FfprobeException)
        {
            return null;
        }

        FfprobePacketsRoot? root;
        try
        {
            root = JsonDeserialize<FfprobePacketsRoot>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is null)
        {
            return null;
        }

        // A packet's frame time is its pts; one without a pts (an AVI with B-frames lists about a third of its video packets so)
        // takes its dts, as the keyframe scan does - but only while the stream shows no re-ordering. A packet that carries both
        // times and they differ is that evidence: a dts is then a decode time, not a display time, and a packet without a pts
        // has no frame time to give. A packet with neither has none either. Both make the answer null (no snap, no check), never
        // a guess. No packets at all (a file without a video stream) is an empty list: no frames.
        var times = new List<TimeSpan>();
        var withoutPts = false;
        var reordered = false;
        foreach (var p in root.Packets ?? new List<FfprobePacket>())
        {
            var hasPts = TryParseSeconds(p.PtsTime, out var pts);
            var hasDts = TryParseSeconds(p.DtsTime, out var dts);
            if (hasPts)
            {
                times.Add(pts);
                reordered |= hasDts && (pts - dts).Duration() > FrameTimeTolerance;
            }
            else if (hasDts)
            {
                withoutPts = true;
                times.Add(dts);
            }
            else
            {
                return null;
            }
        }

        return withoutPts && reordered ? null : times;
    }

    /// <summary>
    /// True when an ffprobe packet <c>flags</c> string marks a keyframe — i.e. it contains the
    /// <c>K</c> marker (e.g. <c>"K__"</c>, <c>"K_"</c>). <c>"___"</c>/<c>"__"</c>/null → false.
    /// Pure function; unit-testable.
    /// </summary>
    internal static bool IsKeyframeFlag(string? flags) =>
        !string.IsNullOrEmpty(flags) && flags.Contains('K', StringComparison.Ordinal);

    /// <inheritdoc />
    public KeyframeSnap SnapToNearestKeyframe(IReadOnlyList<TimeSpan> keyframes, TimeSpan requested)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        if (keyframes.Count == 0)
        {
            throw new ArgumentException("Keyframe list is empty; nothing to snap to.", nameof(keyframes));
        }

        // Keyframes are expected sorted (GetKeyframesAsync returns sorted distinct), but do not
        // trust the caller — evaluate every candidate and keep the nearest, ties → earlier.
        var best = keyframes[0];
        var bestDist = Abs(best - requested);

        for (var i = 1; i < keyframes.Count; i++)
        {
            var candidate = keyframes[i];
            var dist = Abs(candidate - requested);

            // Strictly-less keeps the earliest candidate on a tie (we iterate ascending, but
            // guard against unsorted input by also preferring the earlier time on exact ties).
            if (dist < bestDist || (dist == bestDist && candidate < best))
            {
                best = candidate;
                bestDist = dist;
            }
        }

        return new KeyframeSnap(best, best - requested);
    }

    /// <inheritdoc />
    public TimeSpan AverageGop(IReadOnlyList<TimeSpan> keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        if (keyframes.Count < 2)
        {
            return TimeSpan.Zero;
        }

        // Sort defensively so an unsorted caller still gets a sensible span-based average.
        var ordered = keyframes.OrderBy(k => k).ToList();
        var totalTicks = ordered[^1].Ticks - ordered[0].Ticks;
        var gaps = ordered.Count - 1;
        return TimeSpan.FromTicks(totalTicks / gaps);
    }

    private static StreamInfo MapStream(FfprobeStream s)
    {
        int? sampleRate = TryParseInt(s.SampleRate, out var sr) ? sr : null;
        TimeSpan? startTime = TryParseSeconds(s.StartTime, out var st) ? st : null;
        return new StreamInfo(
            Index: s.Index,
            CodecName: s.CodecName ?? "unknown",
            Type: s.CodecType ?? "unknown",
            Width: s.Width,
            Height: s.Height,
            PixFmt: s.PixFmt,
            SampleRate: sampleRate,
            Channels: s.Channels,
            TimeBase: s.TimeBase,
            AvgFrameRate: ParseFrameRate(s.AvgFrameRate),
            RFrameRate: ParseFrameRate(s.RFrameRate),
            StartTime: startTime);
    }

    /// <summary>
    /// Parse an ffprobe frame-rate rational (<c>"30000/1001"</c> → 29.97003, <c>"30/1"</c> → 30, a bare <c>"25"</c> →
    /// 25) to frames per second (T-219). Null when absent, <c>N/A</c>, unparsable, not finite, or with a zero or
    /// negative part — <c>"0/0"</c> is what ffprobe prints for an audio stream or an unknown rate.
    /// </summary>
    internal static double? ParseFrameRate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "N/A")
        {
            return null;
        }

        double fps;
        var slash = raw.IndexOf('/');
        if (slash < 0)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out fps))
            {
                return null;
            }
        }
        else
        {
            if (!double.TryParse(raw.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                || !double.TryParse(raw.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
                || num <= 0
                || den <= 0)
            {
                return null;
            }

            fps = num / den;
        }

        return double.IsFinite(fps) && fps > 0 ? fps : null;
    }

    private static TimeSpan ResolveDuration(string? formatDuration, IReadOnlyList<TimeSpan> streamDurations)
    {
        if (TryParseSeconds(formatDuration, out var fmt))
        {
            return fmt;
        }

        // Fall back to the longest stream duration when the container lacks a format-level one.
        var longest = TimeSpan.Zero;
        foreach (var d in streamDurations)
        {
            if (d > longest)
            {
                longest = d;
            }
        }

        return longest;
    }

    private static string BuildCacheKey(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        var mtime = info.LastWriteTimeUtc.Ticks;
        var length = info.Length;
        return string.Create(CultureInfo.InvariantCulture, $"{full}|{mtime}|{length}");
    }

    /// <summary>
    /// <see cref="BuildCacheKey"/> that never throws (T-193): false when the file's metadata cannot be read — it was
    /// deleted after the caller's <see cref="File.Exists(string)"/> check, or the path cannot be resolved — so
    /// <see cref="ProbeAsync"/> keeps its never-throws-for-a-bad-file contract and probes uncached instead.
    /// </summary>
    internal static bool TryBuildCacheKey(string path, out string key)
    {
        try
        {
            key = BuildCacheKey(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            key = string.Empty;
            return false;
        }
    }

    /// <summary>A time printed in seconds (<c>pts_time</c>: a decimal with six places) as a TimeSpan; false for absent, N/A or not a finite number.</summary>
    internal static bool TryParseSeconds(string? raw, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(raw) || raw == "N/A")
        {
            return false;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && !double.IsNaN(seconds)
            && !double.IsInfinity(seconds))
        {
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return false;
    }

    private static bool TryParseInt(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw) || raw == "N/A")
        {
            return false;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static TimeSpan Abs(TimeSpan t) => t < TimeSpan.Zero ? t.Negate() : t;

    private static T? JsonDeserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);
}

/// <summary>Which internal scan path <see cref="MediaProbe.GetKeyframesAsync"/> used (T-031, T-194).</summary>
internal enum KeyframeScanPath
{
    /// <summary>No scan performed yet this instance.</summary>
    None = 0,

    /// <summary>
    /// The MP4/MOV sample table read by <see cref="IsoBmffKeyframeIndex"/> and checked by two short ffprobe packet windows (T-194).
    /// </summary>
    Index,

    /// <summary>Demux-level packet-flag scan of the whole file (the T-031 path; the fallback of the sample table).</summary>
    Packets,

    /// <summary>Decode-level <c>-skip_frame nokey</c> fallback.</summary>
    Frames,
}
