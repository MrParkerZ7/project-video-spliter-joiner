using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Waveform;

/// <summary>
/// Default <see cref="IWaveformService"/>: extracts a downsampled mono PCM stream to a temp file via the
/// ffmpeg CLI, reduces it to a normalized 0..1 peak array (max-abs per bucket), and caches the result with
/// a bounded (LRU) cache + best-effort (never-throws) semantics.
///
/// <para><b>ffmpeg args:</b> <c>-i &lt;input&gt; -ac 1 -ar &lt;lowRate&gt; -f s16le -y &lt;temp.pcm&gt;</c> — a mono
/// mixdown at a low sample rate (default 4000 Hz) written as raw little-endian Int16 PCM. Mono + low-rate keeps
/// the temp tiny and is plenty of resolution for a waveform. <b>Temp file, not piped</b> — <see cref="FfmpegRunner"/>
/// reads stdout as UTF-8 text, so raw PCM bytes MUST go through a temp file (the exact constraint that shaped the
/// thumbnail service).</para>
///
/// <para><b>Bucketing/normalization:</b> the temp PCM is read as Int16 LE samples, split into <c>buckets</c>
/// contiguous windows, the max absolute amplitude of each window is taken, then normalized to 0..1 by dividing
/// by <see cref="Int16NormalizationDivisor"/> (32768 — the magnitude of <see cref="short.MinValue"/>). A shorter
/// buckets-worth of samples than requested still yields a full array (empty windows normalize to 0).</para>
///
/// <para><b>Cache:</b> keyed by <c>(inputPath | last-write-time-ticks | length | buckets)</c>. A same-key second
/// call returns the cached peak array WITHOUT running ffmpeg. The cache is LRU-bounded (default 16 entries — a
/// peak array is small); the oldest entry is evicted past the cap. A change to the file (mtime/length) or the
/// bucket count naturally produces a new key.</para>
///
/// <para><b>No audio:</b> a source with no audio track produces no/empty PCM (or ffmpeg errors) → the service
/// returns <c>null</c> (the App hides the band). Never an error.</para>
///
/// <para><b>Temp location:</b> <c>%LOCALAPPDATA%/VideoSplitJoiner/waveform-cache/&lt;hash-of-input&gt;/audio.pcm</c>.
/// The temp root is injectable so tests never touch the real per-user folder.</para>
///
/// <para><b>Best-effort:</b> every public method wraps its work in try/catch and returns <c>null</c> / no-ops on
/// any failure — matching the codebase's best-effort discipline (mirrors <see cref="Thumbnails.FfmpegThumbnailService"/>).</para>
/// </summary>
public sealed class FfmpegWaveformService : IWaveformService
{
    /// <summary>Cache-dir folder name under the app-data root (mirrors the thumbnail service).</summary>
    public const string AppFolderName = "VideoSplitJoiner";

    /// <summary>Sub-folder under the app-data root that holds all waveform temp/caches.</summary>
    public const string CacheFolderName = "waveform-cache";

    /// <summary>Temp PCM file name inside each input's cache sub-folder.</summary>
    public const string PcmFileName = "audio.pcm";

    /// <summary>Default mono sample rate for extraction — low (tiny temp) but ample for a waveform.</summary>
    public const int DefaultSampleRateHz = 4000;

    /// <summary>Default LRU cap on live cache entries (each maps to one peak array — small).</summary>
    public const int DefaultMaxEntries = 16;

    /// <summary>Divisor that maps an Int16 magnitude to 0..1 (|short.MinValue| = 32768).</summary>
    public const float Int16NormalizationDivisor = 32768f;

    private readonly IFfmpegRunner _runner;
    private readonly string _cacheRoot;
    private readonly int _sampleRateHz;
    private readonly int _maxEntries;

    // LRU: key → peak array, in access order. Guarded by _gate. LinkedList = O(1) move-to-front + evict-tail.
    private readonly object _gate = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index = new(StringComparer.Ordinal);

    /// <summary>Create the service over the T-002 runner, using the default per-user cache root + 4kHz mono.</summary>
    public FfmpegWaveformService(IFfmpegRunner runner)
        : this(runner, DefaultCacheRoot(), DefaultSampleRateHz, DefaultMaxEntries)
    {
    }

    /// <summary>
    /// Create the service with an explicit cache root, sample rate, and cache cap — used by tests to redirect the
    /// temp tree and control extraction without hitting the real filesystem / ffmpeg.
    /// </summary>
    public FfmpegWaveformService(
        IFfmpegRunner runner,
        string cacheRoot,
        int sampleRateHz = DefaultSampleRateHz,
        int maxEntries = DefaultMaxEntries)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
        _sampleRateHz = sampleRateHz > 0 ? sampleRateHz : DefaultSampleRateHz;
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
    }

    /// <summary>The resolved cache root this service targets.</summary>
    public string CacheRoot => _cacheRoot;

    /// <summary>
    /// The default per-user cache root: <c>%LOCALAPPDATA%/VideoSplitJoiner/waveform-cache</c>. Falls back to the
    /// OS temp folder when local-app-data cannot be resolved (headless / restricted environments).
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
    public async Task<float[]?> GetPeaksAsync(string inputPath, int buckets, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(inputPath) || buckets <= 0)
            {
                return null;
            }

            ct.ThrowIfCancellationRequested();

            var cacheKey = TryBuildCacheKey(inputPath, buckets);

            // Cache HIT: return the existing peaks WITHOUT running ffmpeg. Return a defensive copy so a
            // caller cannot mutate the cached array.
            if (cacheKey is not null && TryGetCached(cacheKey, out var cachedPeaks))
            {
                return (float[])cachedPeaks.Clone();
            }

            var tempPath = ResolveTempPath(inputPath);

            // Best-effort dir creation; a failure here surfaces as "no waveform" below.
            var dir = Path.GetDirectoryName(tempPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var args = BuildArgs(inputPath, _sampleRateHz, tempPath);

            ct.ThrowIfCancellationRequested();
            var result = await _runner.RunAsync(args, totalDuration: null, progress: null, ct: ct)
                .ConfigureAwait(false);

            // ffmpeg failed, or produced nothing → best-effort null (do NOT cache).
            if (!result.Success || !File.Exists(tempPath))
            {
                return null;
            }

            ct.ThrowIfCancellationRequested();
            var pcm = await ReadAllBytesAsync(tempPath, ct).ConfigureAwait(false);

            // Empty / sub-sample PCM (no audio track, silent-but-empty stream) → hide the band.
            if (pcm.Length < 2)
            {
                return null;
            }

            var peaks = ComputePeaks(pcm, buckets);
            if (peaks is null)
            {
                return null;
            }

            if (cacheKey is not null)
            {
                Remember(cacheKey, peaks);
            }

            // Hand the caller its own copy so the cached array stays immutable.
            return (float[])peaks.Clone();
        }
        catch (OperationCanceledException)
        {
            // A superseded/cancelled request resolves to "no waveform" — never throws, never clobbers.
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

            ForgetForInput(inputPath);
            TryDeleteDirectory(InputCacheDir(inputPath));
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
    /// Build the extraction args: mono mixdown at a low sample rate, raw little-endian Int16 PCM to a temp file.
    /// Exposed <c>internal</c> so a unit test can assert the exact token order/values with no ffmpeg run.
    /// </summary>
    internal static FfmpegArgs BuildArgs(string inputPath, int sampleRateHz, string tempPath) =>
        FfmpegArgs.ForFfmpeg()
            .Input(inputPath)
            .Raw("-vn") // drop video: we only want the audio stream
            .Raw("-ac", "1")
            .Raw("-ar", sampleRateHz.ToString(CultureInfo.InvariantCulture))
            .Raw("-f", "s16le")
            .Raw("-y")
            .Output(tempPath);

    /// <summary>
    /// Reduce raw Int16-LE PCM bytes to a normalized 0..1 peak array of length <paramref name="buckets"/>
    /// (max-abs per contiguous sample window). Returns <c>null</c> when there are no samples. Exposed
    /// <c>internal</c> so a unit test can feed known samples and assert the peak array directly.
    /// </summary>
    internal static float[]? ComputePeaks(byte[] pcm, int buckets)
    {
        if (buckets <= 0 || pcm is null)
        {
            return null;
        }

        var sampleCount = pcm.Length / 2; // 16-bit samples
        if (sampleCount <= 0)
        {
            return null;
        }

        var peaks = new float[buckets];

        for (var b = 0; b < buckets; b++)
        {
            // Contiguous, near-even window [start, end) over the sample range. Using long math avoids overflow
            // on very long (downsampled but still large) streams.
            var start = (int)((long)b * sampleCount / buckets);
            var end = (int)((long)(b + 1) * sampleCount / buckets);
            if (end <= start)
            {
                // More buckets than samples → this window is empty; leave the peak at 0.
                continue;
            }

            var maxAbs = 0;
            for (var i = start; i < end; i++)
            {
                var lo = pcm[i * 2];
                var hi = pcm[(i * 2) + 1];
                var sample = (short)(lo | (hi << 8)); // little-endian Int16
                var abs = sample < 0 ? -sample : sample; // |short.MinValue| fits in int, so no overflow
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                }
            }

            var value = maxAbs / Int16NormalizationDivisor;
            peaks[b] = value > 1f ? 1f : value; // clamp for safety (never > 1 in practice)
        }

        return peaks;
    }

    /// <summary>The per-input cache sub-folder: <c>&lt;cacheRoot&gt;/&lt;hash-of-inputPath&gt;</c>.</summary>
    internal string InputCacheDir(string inputPath) => Path.Combine(_cacheRoot, HashInput(inputPath));

    /// <summary>The temp PCM path for one input: <c>&lt;inputDir&gt;/audio.pcm</c>.</summary>
    internal string ResolveTempPath(string inputPath) => Path.Combine(InputCacheDir(inputPath), PcmFileName);

    /// <summary>
    /// Build the cache key from the input's identity: <c>path | last-write-ticks | length | buckets</c>. Returns
    /// <c>null</c> if the file cannot be stat-ed (missing / unreadable) — extraction can still proceed uncached and
    /// let ffmpeg report the real failure.
    /// </summary>
    private static string? TryBuildCacheKey(string inputPath, int buckets)
    {
        try
        {
            var info = new FileInfo(inputPath);
            if (!info.Exists)
            {
                return null;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{inputPath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{buckets}");
        }
        catch
        {
            return null;
        }
    }

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

    private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct) =>
        await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

    // ---- LRU cache plumbing (all under _gate) ----

    private bool TryGetCached(string key, out float[] peaks)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                // Touch → move to front (most-recently-used).
                _lru.Remove(node);
                _lru.AddFirst(node);
                peaks = node.Value.Peaks;
                return true;
            }
        }

        peaks = Array.Empty<float>();
        return false;
    }

    private void Remember(string key, float[] peaks)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                existing.Value = new CacheEntry(key, peaks);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, peaks));
            _lru.AddFirst(node);
            _index[key] = node;

            // Evict the least-recently-used entries past the cap (in-memory only — arrays are tiny).
            while (_index.Count > _maxEntries && _lru.Last is { } tail)
            {
                _lru.RemoveLast();
                _index.Remove(tail.Value.Key);
            }
        }
    }

    /// <summary>Drop every tracked cache entry whose key belongs to <paramref name="inputPath"/> (used by Clear).</summary>
    private void ForgetForInput(string inputPath)
    {
        var prefix = inputPath + "|";
        lock (_gate)
        {
            var node = _lru.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _lru.Remove(node);
                    _index.Remove(node.Value.Key);
                }

                node = next;
            }
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
        public CacheEntry(string key, float[] peaks)
        {
            Key = key;
            Peaks = peaks;
        }

        public string Key { get; }

        public float[] Peaks { get; }
    }
}
