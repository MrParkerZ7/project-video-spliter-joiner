using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Thumbnails;

/// <summary>
/// Default <see cref="IThumbnailService"/>: extracts a single frame at a bucketed time to a temp jpg via
/// the ffmpeg CLI, with a bounded (LRU) time-bucket cache and best-effort (never-throws) semantics.
///
/// <para><b>ffmpeg args (fast, keyframe-accurate seek):</b> <c>-ss &lt;t&gt;</c> BEFORE <c>-i &lt;input&gt;</c>,
/// then <c>-frames:v 1 -vf scale=&lt;width&gt;:-1 -y &lt;temp.jpg&gt;</c>. Input-seek keeps it near-instant;
/// keyframe accuracy is fine for a hover preview.</para>
///
/// <para><b>Cache:</b> keyed by <c>(inputPath, bucket)</c> where <c>bucket</c> is <paramref name="time"/>
/// floored to a configurable granularity (default 1s). A cached temp file for the bucket is returned
/// WITHOUT running ffmpeg. The cache is LRU-bounded (default 128 entries) — the oldest entry (and its
/// file) is evicted when the cap is exceeded.</para>
///
/// <para><b>Coalesce/cancel:</b> the supplied <see cref="CancellationToken"/> is honored end-to-end, so a
/// superseded request can be cancelled and never clobbers a newer one. The service is stateless-per-call
/// beyond the cache; the caller (the T-078 VM) enforces latest-wins by cancelling stale requests and
/// using only the returned path for the time it asked about.</para>
///
/// <para><b>Temp location:</b> <c>%LOCALAPPDATA%/VideoSplitJoiner/thumb-cache/&lt;hash-of-input&gt;/&lt;bucket&gt;.jpg</c>.
/// The temp root is injectable so tests never touch the real per-user folder.</para>
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

    /// <summary>Default bucket granularity — hover times are floored to whole seconds.</summary>
    public static readonly TimeSpan DefaultBucketGranularity = TimeSpan.FromSeconds(1);

    /// <summary>Default LRU cap on live cache entries (each maps to one temp jpg).</summary>
    public const int DefaultMaxEntries = 128;

    private readonly IFfmpegRunner _runner;
    private readonly string _cacheRoot;
    private readonly TimeSpan _bucketGranularity;
    private readonly int _maxEntries;

    // LRU: key → temp path, in access order. Guarded by _gate. LinkedList = O(1) move-to-front + evict-tail.
    private readonly object _gate = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index = new(StringComparer.Ordinal);

    /// <summary>Create the service over the T-002 runner, using the default per-user cache root + 1s buckets.</summary>
    public FfmpegThumbnailService(IFfmpegRunner runner)
        : this(runner, DefaultCacheRoot(), DefaultBucketGranularity, DefaultMaxEntries)
    {
    }

    /// <summary>
    /// Create the service with an explicit cache root, bucket granularity, and cache cap — used by tests to
    /// redirect the temp tree and control bucketing without hitting the real filesystem / ffmpeg.
    /// </summary>
    public FfmpegThumbnailService(
        IFfmpegRunner runner,
        string cacheRoot,
        TimeSpan? bucketGranularity = null,
        int maxEntries = DefaultMaxEntries)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));

        var g = bucketGranularity ?? DefaultBucketGranularity;
        _bucketGranularity = g > TimeSpan.Zero ? g : DefaultBucketGranularity;
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
    }

    /// <summary>The resolved cache root this service targets.</summary>
    public string CacheRoot => _cacheRoot;

    /// <summary>
    /// The default per-user cache root: <c>%LOCALAPPDATA%/VideoSplitJoiner/thumb-cache</c>. Falls back to
    /// the OS temp folder when local-app-data cannot be resolved (headless / restricted environments).
    /// </summary>
    public static string DefaultCacheRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, AppFolderName, CacheFolderName);
    }

    /// <inheritdoc />
    public async Task<string?> GetThumbnailAsync(string inputPath, TimeSpan time, int width, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(inputPath) || width <= 0)
            {
                return null;
            }

            ct.ThrowIfCancellationRequested();

            var bucket = FloorToBucket(time);
            var cacheKey = BuildCacheKey(inputPath, bucket);

            // Cache HIT: return the existing temp file WITHOUT running ffmpeg (only if it still exists).
            if (TryGetCached(cacheKey, out var cachedPath) && File.Exists(cachedPath))
            {
                return cachedPath;
            }

            var tempPath = ResolveTempPath(inputPath, bucket);

            // A prior run may have left the file on disk though it's not tracked (fresh process). Reuse it.
            if (File.Exists(tempPath))
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

            var args = BuildArgs(inputPath, bucket, width, tempPath);

            ct.ThrowIfCancellationRequested();
            var result = await _runner.RunAsync(args, totalDuration: null, progress: null, ct: ct)
                .ConfigureAwait(false);

            // ffmpeg failed, or produced nothing → best-effort null (do NOT cache a missing file).
            if (!result.Success || !File.Exists(tempPath))
            {
                return null;
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
    internal static FfmpegArgs BuildArgs(string inputPath, TimeSpan bucket, int width, string tempPath) =>
        FfmpegArgs.ForFfmpeg()
            .Raw("-ss", FormatSeconds(bucket))
            .Input(inputPath)
            .Raw("-frames:v", "1")
            .Raw("-vf", $"scale={width.ToString(CultureInfo.InvariantCulture)}:-1")
            .Raw("-y")
            .Output(tempPath);

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

    /// <summary>The temp path for one (input, bucket): <c>&lt;inputDir&gt;/&lt;bucketMs&gt;.jpg</c>.</summary>
    internal string ResolveTempPath(string inputPath, TimeSpan bucket)
    {
        var bucketName = ((long)bucket.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        return Path.Combine(InputCacheDir(inputPath), bucketName + ".jpg");
    }

    private static string BuildCacheKey(string inputPath, TimeSpan bucket) =>
        inputPath + "|" + ((long)bucket.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

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
