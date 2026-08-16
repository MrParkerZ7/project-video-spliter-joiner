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
    /// file. Cancellation still surfaces as <see cref="OperationCanceledException"/>.
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
public sealed class MediaProbe : IMediaProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IFfprobeRunner _ffprobe;
    private readonly ConcurrentDictionary<string, IReadOnlyList<TimeSpan>> _keyframeCache = new();

    // T-093: in-flight dedup. A keyframe scan for a given file (keyed exactly like _keyframeCache)
    // registers its running Task here so a SECOND concurrent caller for the same key awaits the
    // SAME scan instead of launching a duplicate ffprobe pass. The shared scan runs on an
    // INDEPENDENT token (not any one caller's CT), so a caller that cancels its own await can never
    // cancel the shared scan for the other awaiters (cancellation-safety). The entry is removed on
    // completion — success OR failure — and only SUCCESSFUL results are promoted to _keyframeCache,
    // so a failed/cancelled shared scan leaves nothing behind and a later retry re-scans cleanly.
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<TimeSpan>>> _inFlightScans = new();

    /// <summary>
    /// Which scan path the LAST <see cref="GetKeyframesAsync"/> call actually used. Internal —
    /// exposed only so tests can assert the fast packet path ran (and that the fallback fires when
    /// the packet query yields nothing). Not part of the public contract; not reset by cache hits.
    /// </summary>
    internal KeyframeScanPath LastScanPath { get; private set; }

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

        foreach (var s in root.Streams)
        {
            var info = MapStream(s);
            if (info.IsVideo)
            {
                video.Add(info);
            }
            else if (info.IsAudio)
            {
                audio.Add(info);
            }

            if (TryParseSeconds(s.Duration, out var sd))
            {
                streamDurations.Add(sd);
            }
        }

        var duration = ResolveDuration(root.Format?.Duration, streamDurations);
        var container = root.Format?.FormatName ?? "unknown";

        var info2 = new MediaInfo(duration, container, video.AsReadOnly(), audio.AsReadOnly());
        return ProbeResult.Success(info2);
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

        // T-093: in-flight dedup. Get (or start) the ONE shared scan for this key. GetOrAdd's factory
        // starts the scan on an INDEPENDENT token so no single caller's CT can tear it down for the
        // others; this caller then awaits it through its OWN CT via WaitAsync, so cancelling here
        // throws for THIS caller only and leaves the shared scan running for the rest.
        var scan = _inFlightScans.GetOrAdd(cacheKey, key => RunSharedScanAsync(path, key));

        // WaitAsync observes the shared task through this caller's CT: a cancellation here throws an
        // OperationCanceledException for THIS awaiter without cancelling `scan` itself. When the CT is
        // already default/None this is effectively a straight await.
        return await scan.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The single shared keyframe scan for one cache key (T-093). Runs on its OWN token
    /// (<see cref="CancellationToken.None"/>) so it is not tied to any individual caller's
    /// cancellation, promotes a SUCCESSFUL result into <see cref="_keyframeCache"/>, and ALWAYS
    /// removes its <see cref="_inFlightScans"/> entry on completion — so a failed/cancelled scan
    /// caches nothing and a later call re-scans from scratch.
    /// </summary>
    private async Task<IReadOnlyList<TimeSpan>> RunSharedScanAsync(string path, string cacheKey)
    {
        // Yield so GetOrAdd finishes registering this Task before the scan body runs — the first
        // synchronous stretch then can't complete-and-remove the entry before a concurrent caller
        // observes it (keeps the dedup window open for the second caller).
        await Task.Yield();

        try
        {
            // Primary (T-031): read keyframe PACKETS at the demux level — no frame decoding, so the
            // scan is fast even on 4K clips. Fall back to the frame-decode scan if the packet query
            // fails or yields zero keyframes, so correctness never regresses. The shared scan is not
            // bound to a caller CT (CancellationToken.None) — see the dedup note on _inFlightScans.
            IReadOnlyList<TimeSpan>? result = null;
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

            if (result is null)
            {
                result = await ScanKeyframesFromFramesAsync(path, CancellationToken.None).ConfigureAwait(false);
                LastScanPath = KeyframeScanPath.Frames;
            }

            // Success only: promote to the durable cache so repeat calls are cheap.
            _keyframeCache[cacheKey] = result;
            return result;
        }
        finally
        {
            // Always drop the in-flight entry (success OR failure). On success the result already
            // lives in _keyframeCache; on failure nothing is cached, so a retry starts a fresh scan.
            _inFlightScans.TryRemove(cacheKey, out _);
        }
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
        var args = FfmpegArgs.ForFfprobe()
            .Raw(
                "-select_streams", "v:0",
                "-show_packets",
                "-show_entries", "packet=pts_time,dts_time,flags",
                "-print_format", "json")
            .Input(path);

        var json = await _ffprobe.RunJsonAsync(args, ct).ConfigureAwait(false);
        var root = JsonDeserialize<FfprobePacketsRoot>(json);

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

        return times.ToList().AsReadOnly();
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
        return new StreamInfo(
            Index: s.Index,
            CodecName: s.CodecName ?? "unknown",
            Type: s.CodecType ?? "unknown",
            Width: s.Width,
            Height: s.Height,
            PixFmt: s.PixFmt,
            SampleRate: sampleRate,
            Channels: s.Channels,
            TimeBase: s.TimeBase);
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

    private static bool TryParseSeconds(string? raw, out TimeSpan value)
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

/// <summary>Which internal scan path <see cref="MediaProbe.GetKeyframesAsync"/> used (T-031).</summary>
internal enum KeyframeScanPath
{
    /// <summary>No scan performed yet this instance.</summary>
    None = 0,

    /// <summary>Fast demux-level packet-flag scan (the T-031 primary path).</summary>
    Packets,

    /// <summary>Decode-level <c>-skip_frame nokey</c> fallback.</summary>
    Frames,
}
