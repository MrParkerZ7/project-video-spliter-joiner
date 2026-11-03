using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Thumbnails;

/// <summary>
/// Default <see cref="IThumbnailService"/>: extracts a single frame at a bucketed time to a temp jpg via
/// the ffmpeg CLI, with a bounded (LRU) time-bucket cache and best-effort (never-throws) semantics.
///
/// <para><b>ffmpeg args (fast, frame-accurate seek):</b> <c>-ss &lt;t&gt;</c> BEFORE <c>-i &lt;input&gt;</c>,
/// then <c>-frames:v 1 -vf scale=&lt;width&gt;:-1 -y &lt;temp.jpg&gt;</c>. The input seek jumps to the keyframe
/// before <c>t</c> and decodes from there to <c>t</c> itself, so the picture is the frame at the (bucketed) time,
/// not the keyframe (measured on MP4, MOV and MKV).</para>
///
/// <para><b>An MPEG-TS hover seeks early and trims by absolute time (T-227, SPEC-005 I34):</b> the mpegts demuxer's seek lands on the
/// first keyframe AFTER the target, so the grab above showed the next keyframe, up to one GOP late (frame 60 for a hover at 1 s on a 2 s
/// GOP clip, which should show frame 30). For a <c>.ts</c>, <c>.m2ts</c> or <c>.mts</c> bucketed request whose probed format start time is
/// known, the input <c>-ss</c> is <see cref="TransportStreamSeekLead"/> early (so the demuxer lands on a keyframe at or before the time) and a
/// second, output <c>-ss</c> of <c>start + time</c> trims to the frame; <c>-copyts</c> keeps the timestamps absolute, which is what lets the
/// output <c>-ss</c> be a plain timestamp (measured: without it the same two seeks showed other frames, 60, 91 and 133 for the targets 90,
/// 135 and 177 of a 30 fps clip with a keyframe every 2 s). No
/// start-time source, an unknown start or a GOP longer than the lead keeps today's args and today's (late) frame; a run that yields no picture
/// is run once more with them.</para>
///
/// <para><b>The stream-info pass is skipped only for an MP4/MOV whose probed start time is exactly 0 (T-195, T-226, SPEC-005
/// I27):</b> skipping it (<see cref="StreamInfoSkip.Flag"/>, 0.05-0.09 s per 4K H.264 grab) also leaves ffmpeg without the format
/// start time an input <c>-ss</c> counts from, so on an MP4/MOV whose start time is not 0 the grab would exit 0 with the wrong
/// frame (the first part of the app's own lossless split of an H.264 B-frame + AAC MP4 starts at about 0.043 s, and a skipping grab
/// came back one frame early; so do an <c>-output_ts_offset</c> MOV and MPEG-TS content named <c>.mp4</c>). So the flag goes
/// before <c>-i</c> only when the input is <c>.mp4</c>, <c>.m4v</c> or <c>.mov</c> AND the start-time source given to the constructor
/// (the shared probe, whose result is cached, so the gate costs no process) says the start is exactly zero. No source, a probe
/// that failed or does not know, or any other start: today's unflagged args. A flagged grab that exits non-zero, writes no file or an
/// empty one is run once more without the flag (as the waveform does, SPEC-006 I24); a cancelled grab is never retried.</para>
///
/// <para><b>Cache:</b> keyed by <c>(inputPath, bucket, width)</c> where <c>bucket</c> is the requested time
/// floored to a configurable granularity (default 1s). A cached temp file for that bucket AT THAT WIDTH is
/// returned WITHOUT running ffmpeg; the same second at another width is a different file (T-179 — a 640px
/// profile picture must never be handed a 64px row chip's cached frame). The cache is LRU-bounded (default
/// 128 entries) — the oldest entry (and its file) is evicted when the cap is exceeded.</para>
///
/// <para><b>Exact requests (T-196, SPEC-005 I28-I32):</b> <see cref="GetExactThumbnailAsync"/> seeks to the time itself, at six
/// decimals and never rounded to milliseconds, for a cut-point or profile picture, whose frame must be the one the run cuts at and
/// whose keyframe is not to be re-decoded from the GOP before it (a floored <c>-ss</c> lands before the keyframe). Its cache key
/// and file name are <c>x&lt;µs&gt;_w&lt;width&gt;</c>: an <c>x</c> followed by whole microseconds, which no bucket name (digits
/// only) can equal, in the same per-input folder and the same LRU, so an exact request and a hover never serve each other, two
/// widths of one exact time are two files, and <see cref="Clear"/> / <see cref="ClearAll"/> remove them. The time is an ffmpeg
/// seek offset from the format start time and is passed through unchanged.</para>
///
/// <para><b>Coalesce/cancel:</b> the supplied <see cref="CancellationToken"/> is honored end-to-end, so a
/// superseded request can be cancelled and never clobbers a newer one. The service is stateless-per-call
/// beyond the cache; the caller (the T-078 VM) enforces latest-wins by cancelling stale requests and
/// using only the returned path for the time it asked about.</para>
///
/// <para><b>Temp location:</b> <c>%LOCALAPPDATA%/VideoSplitJoiner/thumb-cache/v&lt;CacheVersion&gt;/&lt;hash-of-input&gt;/&lt;bucketMs&gt;_w&lt;width&gt;.jpg</c>.
/// The temp root is injectable so tests never touch the real per-user folder.</para>
///
/// <para><b>The cache is versioned (T-243, SPEC-005 I36):</b> the key above carries no version of the code that wrote a picture, so the
/// version is the folder: <see cref="CacheVersion"/> is bumped by any fix that changes what a grab returns, and a build then never
/// reads a folder an older one wrote. The first grab of a service made by the default constructors (or <see cref="ForCacheBase"/>)
/// sweeps the older version folders once, with truncated pictures and stale work files of the current one; see
/// <see cref="SweepStaleCache"/>. An explicit cache root is used as given and never swept.</para>
///
/// <para><b>A picture is published whole (T-228, SPEC-005 I35):</b> ffmpeg writes each grab to a work file of its own beside the final one
/// (<c>&lt;name&gt;_w&lt;width&gt;.&lt;guid&gt;.tmp.jpg</c>), and the final name is taken by a rename only after a run that succeeded and wrote a
/// non-empty file. So the final name only ever holds a whole picture: a run killed in the middle of its write (a superseded hover), one that
/// exits non-zero after writing bytes, or one that exits 0 with an empty file leaves nothing the next request, in this process or the next, can
/// be handed or cache. The work file is removed whatever the outcome, and the first of two grabs of one key to finish keeps the final name. An
/// empty file under the final name (an older build wrote in place) is not reused.</para>
///
/// <para><b>Best-effort:</b> every public method wraps its work in try/catch and returns <c>null</c> /
/// no-ops on any failure — matching the codebase's best-effort discipline (e.g. <c>ErrorLogWriter</c>).</para>
/// </summary>
public sealed class FfmpegThumbnailService : IThumbnailService
{
    /// <summary>Cache-dir folder name under the app-data root (mirrors <c>ErrorLogWriter.AppFolderName</c>).</summary>
    public const string AppFolderName = "VideoSplitJoiner";

    /// <summary>Sub-folder under the app-data root that holds all thumbnail caches.</summary>
    public const string CacheFolderName = "thumb-cache";

    /// <summary>
    /// The version of the cache layout and of the pictures it holds (T-243, SPEC-005 I36). Bump it in the SAME change as any fix that
    /// changes what a grab returns (the frame, the scale, the seek): a picture an older build cached is then never served, because the
    /// pictures live under <c>thumb-cache/v&lt;CacheVersion&gt;/</c> and the first use sweeps the older version folders away.
    /// 1 is the layout without a version folder (<c>thumb-cache/&lt;hash&gt;/</c>), which every build before T-243 wrote; 2 is the
    /// first versioned one, and also the one that carries the T-227 MPEG-TS frame fix.
    /// </summary>
    public const int CacheVersion = 2;

    /// <summary>The version folder under <see cref="CacheFolderName"/>: <c>v2</c> for <see cref="CacheVersion"/> 2.</summary>
    public static readonly string CacheVersionFolderName = "v" + CacheVersion.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// How old a <c>*.tmp.jpg</c> work file must be before the sweep takes it for one a hard kill left (T-243). A live grab's file is
    /// seconds old; the margin keeps the sweep off a grab of another running instance of the app.
    /// </summary>
    internal static readonly TimeSpan StaleWorkFileAge = TimeSpan.FromHours(1);

    /// <summary>Default bucket granularity — hover times are floored to whole seconds.</summary>
    public static readonly TimeSpan DefaultBucketGranularity = TimeSpan.FromSeconds(1);

    /// <summary>Default LRU cap on live cache entries (each maps to one temp jpg).</summary>
    public const int DefaultMaxEntries = 128;

    /// <summary>
    /// How far before the hovered time an MPEG-TS grab starts its input seek (T-227, SPEC-005 I34). The mpegts demuxer lands on the first
    /// keyframe at or AFTER the seek point, so the point must be at least one GOP early for the landing to be at or before the time. 3 s
    /// covers the 0.5-2 s GOPs of broadcast, camera and AVCHD streams; a longer GOP shows the next keyframe, as before the fix. The decode
    /// cost is the frames between the landing and the time, at most this much video.
    /// </summary>
    internal static readonly TimeSpan TransportStreamSeekLead = TimeSpan.FromSeconds(3);

    private static readonly string[] TransportStreamExtensions = { ".ts", ".m2ts", ".mts" };

    private readonly IFfmpegRunner _runner;
    private readonly Func<string, CancellationToken, Task<TimeSpan?>>? _startTimeOf;
    private readonly string _cacheRoot;
    private readonly TimeSpan _bucketGranularity;
    private readonly int _maxEntries;

    // T-243: the one-time sweep of the older cache versions (null for an explicit root, which the caller owns). Every grab awaits it, so
    // the sweep never runs beside a grab and cannot remove a work file or picture this process is writing.
    private readonly Lazy<Task>? _sweep;

    // LRU: key → temp path, in access order. Guarded by _gate. LinkedList = O(1) move-to-front + evict-tail.
    private readonly object _gate = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index = new(StringComparer.Ordinal);

    /// <summary>Create the service over the T-002 runner, using the default per-user cache root + 1s buckets.</summary>
    public FfmpegThumbnailService(IFfmpegRunner runner)
        : this(runner, DefaultCacheRoot(), DefaultBucketGranularity, DefaultMaxEntries, null, DefaultCacheBase())
    {
    }

    /// <summary>
    /// As above, with the source of a file's probed format start time that lets a zero-start MP4/MOV grab skip the stream-info decode
    /// (T-226, SPEC-005 I27). The app passes the shared probe's cached <c>MediaInfo.StartTime</c>; a null result means "unknown".
    /// </summary>
    public FfmpegThumbnailService(IFfmpegRunner runner, Func<string, CancellationToken, Task<TimeSpan?>>? startTimeOf)
        : this(runner, DefaultCacheRoot(), DefaultBucketGranularity, DefaultMaxEntries, startTimeOf, DefaultCacheBase())
    {
    }

    /// <summary>
    /// A service whose pictures live under <paramref name="cacheBase"/>'s current version folder (<c>&lt;cacheBase&gt;/v&lt;CacheVersion&gt;</c>)
    /// and whose first grab sweeps what older versions left in <paramref name="cacheBase"/> (T-243, SPEC-005 I36). The default constructors
    /// are this over <see cref="DefaultCacheBase"/>; tests pass a temp base.
    /// </summary>
    public static FfmpegThumbnailService ForCacheBase(
        IFfmpegRunner runner, string cacheBase, Func<string, CancellationToken, Task<TimeSpan?>>? startTimeOf = null)
    {
        ArgumentNullException.ThrowIfNull(cacheBase);
        return new FfmpegThumbnailService(
            runner, Path.Combine(cacheBase, CacheVersionFolderName), DefaultBucketGranularity, DefaultMaxEntries, startTimeOf, cacheBase);
    }

    /// <summary>
    /// Create the service with an explicit cache root, bucket granularity, and cache cap — used by tests to
    /// redirect the temp tree and control bucketing without hitting the real filesystem / ffmpeg.
    /// </summary>
    public FfmpegThumbnailService(
        IFfmpegRunner runner,
        string cacheRoot,
        TimeSpan? bucketGranularity = null,
        int maxEntries = DefaultMaxEntries,
        Func<string, CancellationToken, Task<TimeSpan?>>? startTimeOf = null)
        : this(runner, cacheRoot, bucketGranularity, maxEntries, startTimeOf, sweepBase: null)
    {
    }

    private FfmpegThumbnailService(
        IFfmpegRunner runner,
        string cacheRoot,
        TimeSpan? bucketGranularity,
        int maxEntries,
        Func<string, CancellationToken, Task<TimeSpan?>>? startTimeOf,
        string? sweepBase)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _startTimeOf = startTimeOf;
        _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));

        var g = bucketGranularity ?? DefaultBucketGranularity;
        _bucketGranularity = g > TimeSpan.Zero ? g : DefaultBucketGranularity;
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;

        if (sweepBase is not null)
        {
            _sweep = new Lazy<Task>(() => Task.Run(() => SweepStaleCache(sweepBase)), LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    /// <summary>The resolved cache root this service targets.</summary>
    public string CacheRoot => _cacheRoot;

    /// <summary>
    /// The default per-user cache root: <c>%LOCALAPPDATA%/VideoSplitJoiner/thumb-cache/v&lt;CacheVersion&gt;</c> (T-243), the version
    /// folder under <see cref="DefaultCacheBase"/>.
    /// </summary>
    public static string DefaultCacheRoot() => Path.Combine(DefaultCacheBase(), CacheVersionFolderName);

    /// <summary>
    /// The folder every cache version lives in: <c>%LOCALAPPDATA%/VideoSplitJoiner/thumb-cache</c>. Falls back to
    /// the OS temp folder when local-app-data cannot be resolved (headless / restricted environments).
    /// </summary>
    public static string DefaultCacheBase()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, AppFolderName, CacheFolderName);
    }

    /// <inheritdoc />
    public Task<string?> GetThumbnailAsync(string inputPath, TimeSpan time, int width, CancellationToken ct)
    {
        var bucket = FloorToBucket(time);
        return GrabAsync(inputPath, bucket, BucketName(bucket), width, hover: true, ct);
    }

    /// <inheritdoc />
    public Task<string?> GetExactThumbnailAsync(string inputPath, TimeSpan time, int width, CancellationToken ct)
    {
        // T-196: the exact time (never floored, never rounded to milliseconds), zero when it is not positive; its own name.
        var exact = time > TimeSpan.Zero ? time : TimeSpan.Zero;
        return GrabAsync(inputPath, exact, ExactName(exact), width, hover: false, ct);
    }

    /// <summary>
    /// The one grab both requests share: look up <paramref name="name"/> (a bucket's milliseconds, or an exact time's
    /// <c>x&lt;µs&gt;</c>) at <paramref name="width"/>, reuse a file on disk under that name, or run ffmpeg once for
    /// <paramref name="seek"/> and remember the result. Best-effort: any failure is null. <paramref name="hover"/> is the bucketed
    /// request, the only one that takes the MPEG-TS early seek: an exact request on a TS is at a keyframe's own time minus the start,
    /// where the plain seek already lands on the frame (T-196, SPEC-005 I32).
    /// </summary>
    private async Task<string?> GrabAsync(string inputPath, TimeSpan seek, string name, int width, bool hover, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(inputPath) || width <= 0)
            {
                return null;
            }

            ct.ThrowIfCancellationRequested();

            // T-243: the first grab of the process waits for the one-time sweep of the older versions (the rest wait with it).
            if (_sweep is not null)
            {
                await _sweep.Value.ConfigureAwait(false);
            }

            var cacheKey = BuildCacheKey(inputPath, name, width);

            // Cache HIT: return the existing temp file WITHOUT running ffmpeg (only if it still exists).
            if (TryGetCached(cacheKey, out var cachedPath) && File.Exists(cachedPath))
            {
                return cachedPath;
            }

            var tempPath = ResolveTempPath(inputPath, name, width);

            // A prior run may have left the file on disk though it's not tracked (fresh process). Reuse it —
            // the width is in the file name, so only a file of the requested width can match. T-228: a picture is published under this
            // name only when whole (below), so what is here is a picture; an empty file (an older build wrote in place) is not one.
            if (HasPicture(tempPath))
            {
                Remember(cacheKey, tempPath);
                return tempPath;
            }

            // Best-effort dir creation; a failure here surfaces as "no thumbnail" below.
            var dir = Path.GetDirectoryName(tempPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // T-226: an MP4/MOV whose probed start time is exactly 0 skips the stream-info decode; any doubt is today's args.
            var skipStreamInfo = await CanSkipStreamInfoAsync(inputPath, ct).ConfigureAwait(false);

            // T-227: a bucketed hover on an MPEG-TS whose start time is known seeks early and trims by absolute time; any doubt is today's args.
            var transportStreamStart = hover ? await TransportStreamStartAsync(inputPath, ct).ConfigureAwait(false) : null;

            // T-228, SPEC-005 I35: ffmpeg writes under a work name of its own beside the final one; the final name is taken only by a
            // rename of a whole picture. A run that is killed (a superseded hover), fails or writes nothing leaves no file at the final name,
            // and the work file is removed whatever the outcome.
            var workPath = WorkPathFor(tempPath);
            try
            {
                var args = BuildArgs(inputPath, seek, width, workPath, skipStreamInfo, transportStreamStart);

                ct.ThrowIfCancellationRequested();
                var result = await _runner.RunAsync(args, totalDuration: null, progress: null, ct: ct)
                    .ConfigureAwait(false);

                // A flagged run (or an MPEG-TS early-seek run) that failed, wrote no file or an empty one is run once more with today's args:
                // the extension and the start time are hints, and a wrong one must cost a second run, never a missing picture. A cancelled
                // grab is never retried (it threw above); the retry is judged on its own output, never on a file the failed run left behind.
                if ((skipStreamInfo || transportStreamStart is not null) && !(result.Success && HasPicture(workPath)))
                {
                    ct.ThrowIfCancellationRequested();
                    TryDeleteFile(workPath);
                    result = await _runner.RunAsync(BuildArgs(inputPath, seek, width, workPath), totalDuration: null, progress: null, ct: ct)
                        .ConfigureAwait(false);
                }

                // ffmpeg failed, or produced nothing (or an empty file) → best-effort null (do NOT cache a missing file).
                if (!result.Success || !HasPicture(workPath) || !await TryPublishAsync(workPath, tempPath).ConfigureAwait(false))
                {
                    return null;
                }
            }
            finally
            {
                await DeleteWorkFileAsync(workPath).ConfigureAwait(false);
            }

            Remember(cacheKey, tempPath);
            return tempPath;
        }
        catch (OperationCanceledException)
        {
            // A superseded/cancelled request resolves to "no thumbnail" — never throws, never clobbers.
            return null;
        }
        catch
        {
            // Any other failure (I/O, locator-missing, security) → best-effort null.
            return null;
        }
    }

    /// <inheritdoc />
    public void Clear(string inputPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return;
            }

            var dir = InputCacheDir(inputPath);
            ForgetUnder(dir);
            TryDeleteDirectory(dir);
        }
        catch
        {
            // Best-effort — a missing dir or a lock must never surface to the caller.
        }
    }

    /// <inheritdoc />
    public void ClearAll()
    {
        try
        {
            lock (_gate)
            {
                _lru.Clear();
                _index.Clear();
            }

            TryDeleteDirectory(_cacheRoot);
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// Build the frame-grab args: fast seek (<c>-ss</c> BEFORE <c>-i</c>), single frame, scale to width.
    /// Exposed <c>internal</c> so a unit test can assert the exact token order/values with no ffmpeg run.
    /// </summary>
    internal static FfmpegArgs BuildArgs(
        string inputPath, TimeSpan bucket, int width, string tempPath, bool skipStreamInfo = false, TimeSpan? transportStreamStart = null)
    {
        FfmpegArgs args;
        if (transportStreamStart is { } start)
        {
            // T-227, SPEC-005 I34: an MPEG-TS input seek lands on the first keyframe AFTER the point, so seek one lead early and let
            // the output -ss trim to the frame. -copyts keeps the timestamps absolute (start time included), so that output -ss is
            // the plain timestamp start + bucket; without it the same two seeks showed other frames (measured, SPEC-005 I34).
            var early = bucket - TransportStreamSeekLead;
            args = FfmpegArgs.ForFfmpeg()
                .Raw("-copyts")
                .Raw("-ss", FormatSeconds(early > TimeSpan.Zero ? early : TimeSpan.Zero))
                .Input(inputPath)
                .Raw("-ss", FormatSeconds(start + bucket));
        }
        else
        {
            args = FfmpegArgs.ForFfmpeg().Raw("-ss", FormatSeconds(bucket));
            if (skipStreamInfo)
            {
                args.Raw(StreamInfoSkip.Flag); // an input option: immediately before -i
            }

            args.Input(inputPath);
        }

        return args
            .Raw("-frames:v", "1")
            .Raw("-vf", $"scale={width.ToString(CultureInfo.InvariantCulture)}:-1")
            .Raw("-y")
            .Output(tempPath);
    }

    /// <summary>True when <paramref name="path"/> ends in <c>.ts</c>, <c>.m2ts</c> or <c>.mts</c>, in any case: the MPEG-TS family (T-227).</summary>
    internal static bool IsTransportStreamExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        foreach (var candidate in TransportStreamExtensions)
        {
            if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The probed format start time of an MPEG-TS input (T-227, SPEC-005 I34), or null when the early-seek args do not apply: the extension is
    /// not .ts/.m2ts/.mts, no start-time source was given, it does not know the start, or it threw. A cancellation propagates.
    /// </summary>
    private async Task<TimeSpan?> TransportStreamStartAsync(string inputPath, CancellationToken ct)
    {
        if (_startTimeOf is null || !IsTransportStreamExtension(inputPath))
        {
            return null;
        }

        try
        {
            return await _startTimeOf(inputPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when the grab of <paramref name="inputPath"/> may skip the stream-info decode (T-226, SPEC-005 I27): the extension is
    /// MP4/M4V/MOV AND the start-time source says the format starts at exactly 0. No source, an unknown start, or a source that threw
    /// is false (today's args); a cancellation propagates.
    /// </summary>
    private async Task<bool> CanSkipStreamInfoAsync(string inputPath, CancellationToken ct)
    {
        if (_startTimeOf is null || !StreamInfoSkip.IsIsoBmffExtension(inputPath))
        {
            return false;
        }

        try
        {
            return await _startTimeOf(inputPath, ct).ConfigureAwait(false) is { } start && start == TimeSpan.Zero;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The work file one grab writes (T-228, SPEC-005 I35): <c>&lt;name&gt;_w&lt;width&gt;.&lt;guid&gt;.tmp.jpg</c> beside the final file, in the
    /// same folder so publishing it is a rename. A guid makes every grab's file its own, so two grabs of one key in flight together never write
    /// the same file and a file a killed run left is never mistaken for a later run's. It ends in <c>.jpg</c>, which is what ffmpeg picks the
    /// still-image muxer by, and it never equals a final name (those end in <c>_w&lt;width&gt;.jpg</c>).
    /// </summary>
    internal static string WorkPathFor(string finalPath) =>
        Path.ChangeExtension(finalPath, null) + "." + Guid.NewGuid().ToString("N") + ".tmp.jpg";

    /// <summary>How many times a refused publish is tried in all (the first at once, the rest 20 ms apart).</summary>
    private const int PublishAttempts = 6;

    /// <summary>
    /// Move a whole picture onto its final name (T-228). The first grab to finish wins: a picture already there came from a grab of the same
    /// key and may be open in the app, so it is kept and never renamed over, and this one is dropped (the caller deletes the work file); an empty
    /// file there (an older build wrote in place) is removed first. A refused move (a scanner or indexer holding the new file for a moment, or a
    /// racing grab publishing between the check and the move) is looked at again a few times, 20 ms apart. False when the picture could not be
    /// put in place and none is there.
    /// </summary>
    private static async Task<bool> TryPublishAsync(string workPath, string finalPath)
    {
        for (var attempt = 0; attempt < PublishAttempts; attempt++)
        {
            if (HasPicture(finalPath))
            {
                return true;
            }

            if (!File.Exists(workPath))
            {
                break; // the folder was cleared meanwhile: nothing left to publish
            }

            try
            {
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(workPath, finalPath);
                return true;
            }
            catch
            {
                // Refused: look again after a moment.
            }

            if (attempt < PublishAttempts - 1)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        return HasPicture(finalPath);
    }

    /// <summary>
    /// Remove a grab's work file, whatever the outcome. A killed ffmpeg may still hold the file for a moment after the cancel (the runner kills
    /// the process tree without waiting for it), so a refused delete is tried again a few times, 20 ms apart; the first try is immediate.
    /// Best-effort: a file that stays is only an unused name in the per-input folder, which <see cref="Clear"/> removes.
    /// </summary>
    private static async Task DeleteWorkFileAsync(string path)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                File.Delete(path);
                return;
            }
            catch
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The one-time sweep of <paramref name="cacheBase"/> (T-243, SPEC-005 I36), best-effort (a locked or missing file is skipped, never
    /// thrown). It removes (1) every older version folder: <c>v&lt;n&gt;</c> with n below <see cref="CacheVersion"/>, and the
    /// pre-version per-input folders (32 hex characters) a build before T-243 wrote straight into the base; and, inside the current
    /// version folder, (2) a picture whose bytes do not end the way a JPEG does (FF D9) - an older build wrote in place, so a killed run
    /// could leave one under its final name - and (3) a <c>*.tmp.jpg</c> work file at least <see cref="StaleWorkFileAge"/> old, which a
    /// hard kill left. A newer version's folder and anything else in the base is left alone. Returns how many items it removed, so a
    /// second start finds 0.
    /// </summary>
    internal static int SweepStaleCache(string cacheBase)
    {
        var removed = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(cacheBase) || !Directory.Exists(cacheBase))
            {
                return 0;
            }

            foreach (var dir in Directory.EnumerateDirectories(cacheBase).ToList())
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, CacheVersionFolderName, StringComparison.Ordinal))
                {
                    removed += SweepCurrentVersion(dir);
                }
                else if (IsOlderLayoutFolder(name) && TryDeleteDirectoryNow(dir))
                {
                    removed++;
                }
            }
        }
        catch
        {
            // Best-effort: whatever stays is swept by the next start.
        }

        return removed;
    }

    private static int SweepCurrentVersion(string versionRoot)
    {
        var removed = 0;
        foreach (var inputDir in SafeList(() => Directory.EnumerateDirectories(versionRoot)))
        {
            foreach (var file in SafeList(() => Directory.EnumerateFiles(inputDir, "*.jpg")))
            {
                try
                {
                    var stale = file.EndsWith(".tmp.jpg", StringComparison.OrdinalIgnoreCase)
                        ? DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= StaleWorkFileAge
                        : !IsWholeJpeg(file);
                    if (stale)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch
                {
                    // A locked file stays until the next start.
                }
            }
        }

        return removed;
    }

    private static List<string> SafeList(Func<IEnumerable<string>> list)
    {
        try
        {
            return list().ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>True for <c>v&lt;n&gt;</c> with n below the current version, and for a pre-version per-input folder (32 lowercase hex).</summary>
    private static bool IsOlderLayoutFolder(string name)
    {
        if (name.Length > 1 && name[0] == 'v' && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return version < CacheVersion;
        }

        return name.Length == 32 && name.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    }

    /// <summary>
    /// True when the file starts with the JPEG start marker (FF D8) and ends with the end marker (FF D9), ignoring zero padding after it.
    /// A picture cut short by a killed run has no end marker. Unreadable is treated as whole (the sweep never deletes on a doubt).
    /// </summary>
    private static bool IsWholeJpeg(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 4)
            {
                return false;
            }

            var head = new byte[2];
            stream.ReadExactly(head);
            if (head[0] != 0xFF || head[1] != 0xD8)
            {
                return false;
            }

            var tailLength = (int)Math.Min(stream.Length, 16);
            var tail = new byte[tailLength];
            stream.Seek(-tailLength, SeekOrigin.End);
            stream.ReadExactly(tail);

            var end = tail.Length;
            while (end > 0 && tail[end - 1] == 0)
            {
                end--;
            }

            return end >= 2 && tail[end - 2] == 0xFF && tail[end - 1] == 0xD9;
        }
        catch
        {
            return true;
        }
    }

    private static bool TryDeleteDirectoryNow(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A file with at least one byte: what a grab that "worked" must have written.</summary>
    private static bool HasPicture(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true, Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Floor a time to the bucket granularity (never negative). Public-shaped for testing.</summary>
    internal TimeSpan FloorToBucket(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var buckets = (long)(time.Ticks / _bucketGranularity.Ticks);
        return TimeSpan.FromTicks(buckets * _bucketGranularity.Ticks);
    }

    /// <summary>Format a timestamp as invariant-culture seconds (matches the split builder's convention).</summary>
    internal static string FormatSeconds(TimeSpan t) =>
        t.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The per-input cache sub-folder: <c>&lt;cacheRoot&gt;/&lt;hash-of-inputPath&gt;</c>.</summary>
    internal string InputCacheDir(string inputPath) => Path.Combine(_cacheRoot, HashInput(inputPath));

    /// <summary>
    /// The temp path for one (input, bucket, width): <c>&lt;inputDir&gt;/&lt;bucketMs&gt;_w&lt;width&gt;.jpg</c>. The
    /// width is part of the name so a file another request left on disk is reused only at its own width.
    /// </summary>
    internal string ResolveTempPath(string inputPath, TimeSpan bucket, int width) =>
        ResolveTempPath(inputPath, BucketName(bucket), width);

    /// <summary>The temp path of an EXACT request (T-196): <c>&lt;inputDir&gt;/x&lt;µs&gt;_w&lt;width&gt;.jpg</c>.</summary>
    internal string ResolveExactTempPath(string inputPath, TimeSpan time, int width) =>
        ResolveTempPath(inputPath, ExactName(time > TimeSpan.Zero ? time : TimeSpan.Zero), width);

    private string ResolveTempPath(string inputPath, string name, int width) =>
        Path.Combine(InputCacheDir(inputPath), name + "_w" + width.ToString(CultureInfo.InvariantCulture) + ".jpg");

    private static string BuildCacheKey(string inputPath, string name, int width) =>
        inputPath + "|" + name + "|w" + width.ToString(CultureInfo.InvariantCulture);

    private static string BucketName(TimeSpan bucket) =>
        ((long)bucket.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// An exact request's name (T-196): <c>x</c> and the time in whole microseconds. The <c>x</c> keeps it from ever equalling a
    /// bucket's name (digits only), so the two requests never share a cache entry or a file in either direction.
    /// </summary>
    private static string ExactName(TimeSpan time) =>
        "x" + (time.Ticks / (TimeSpan.TicksPerMillisecond / 1000)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Stable, filesystem-safe subdir name for an input path (SHA-256, hex, first 16 bytes).</summary>
    private static string HashInput(string inputPath)
    {
        var bytes = Encoding.UTF8.GetBytes(inputPath);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(32);
        for (var i = 0; i < 16; i++)
        {
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    // ---- LRU cache plumbing (all under _gate) ----

    private bool TryGetCached(string key, out string path)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                // Touch → move to front (most-recently-used).
                _lru.Remove(node);
                _lru.AddFirst(node);
                path = node.Value.Path;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private void Remember(string key, string path)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                existing.Value = new CacheEntry(key, path);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, path));
            _lru.AddFirst(node);
            _index[key] = node;

            // Evict the least-recently-used entries (and their files) past the cap.
            while (_index.Count > _maxEntries && _lru.Last is { } tail)
            {
                _lru.RemoveLast();
                _index.Remove(tail.Value.Key);
                TryDeleteFile(tail.Value.Path);
            }
        }
    }

    /// <summary>Drop every tracked entry whose file lives under <paramref name="dir"/> (used by Clear).</summary>
    private void ForgetUnder(string dir)
    {
        var full = Path.GetFullPath(dir);
        lock (_gate)
        {
            var node = _lru.First;
            while (node is not null)
            {
                var next = node.Next;
                var entryDir = Path.GetFullPath(Path.GetDirectoryName(node.Value.Path) ?? string.Empty);
                if (string.Equals(entryDir, full, StringComparison.OrdinalIgnoreCase))
                {
                    _lru.Remove(node);
                    _index.Remove(node.Value.Key);
                }

                node = next;
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort eviction.
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort — a missing/locked dir is not a caller-facing failure.
        }
    }

    private struct CacheEntry
    {
        public CacheEntry(string key, string path)
        {
            Key = key;
            Path = path;
        }

        public string Key { get; }

        public string Path { get; }
    }
}
