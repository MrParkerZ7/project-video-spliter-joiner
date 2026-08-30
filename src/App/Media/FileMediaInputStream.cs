using System;
using System.IO;
using Unosquare.FFME.Common;

namespace VideoSplitJoiner.App.Media;

/// <summary>
/// Feeds the preview player from a <see cref="FileStream"/> instead of a <see cref="Uri"/> (T-132).
///
/// <para><b>Why this exists.</b> A UNC path whose SERVER NAME is not a legal URI hostname — most
/// realistically because it contains a space, as consumer NAS boxes often do (<c>\\Seagate NAS\…</c>) —
/// cannot be expressed as a <see cref="Uri"/> at all. Every construction was probed under T-131 and all
/// of them throw, so <c>MediaElement.Open(Uri)</c> is simply unreachable for those paths. FFME's other
/// entry point, <c>Open(IMediaInputStream)</c>, addresses the file through this adapter instead — the
/// path never has to survive a round trip through URI parsing.</para>
///
/// <para><b>The unsafe surface is deliberately confined here.</b> <see cref="Read"/> and
/// <see cref="Seek"/> are ffmpeg AVIO callbacks and take raw pointers, which is why
/// <c>AllowUnsafeBlocks</c> is enabled for this project (ADR-0019). Nothing else in the app uses
/// pointers, and the pointer work here is a single <see cref="Span{T}"/> wrap — no manual arithmetic.</para>
/// </summary>
public sealed unsafe class FileMediaInputStream : IMediaInputStream
{
    /// <summary>ffmpeg's <c>AVSEEK_SIZE</c>: "do not seek, report the total length instead".</summary>
    private const int AvseekSize = 0x10000;

    /// <summary>ffmpeg's <c>AVERROR_EOF</c> — <c>-MKTAG('E','O','F',' ')</c>.</summary>
    private const int AvErrorEof = -541478725;

    private readonly FileStream _stream;
    private bool _disposed;

    /// <param name="path">
    /// The real filesystem path. Opened share-<see cref="FileShare.ReadWrite"/> on purpose: the cutting
    /// engine may be reading or writing the same file, and a preview must never be the reason a cut is
    /// refused.
    /// </param>
    public FileMediaInputStream(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path is required.", nameof(path));
        }

        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // A pseudo-URI ONLY — FFME uses it to label the stream, never to open anything. It has to be a
        // legal Uri, which is exactly what the real path is not, so it is synthesised rather than derived.
        StreamUri = new Uri("vsj-stream://local/" + Guid.NewGuid().ToString("N"));
    }

    /// <inheritdoc />
    public Uri StreamUri { get; }

    /// <inheritdoc />
    public bool CanSeek => _stream.CanSeek;

    /// <inheritdoc />
    /// <remarks>32 KiB. FFME suggests ~4096; a larger buffer means fewer managed↔native hops per frame
    /// over a network share, which is precisely where this adapter is used.</remarks>
    public int ReadBufferLength => 32 * 1024;

    /// <inheritdoc />
    public InputStreamInitializing? OnInitializing => null;

    /// <inheritdoc />
    public InputStreamInitialized? OnInitialized => null;

    /// <summary>
    /// Fill <paramref name="targetBuffer"/> with up to <paramref name="targetBufferLength"/> bytes.
    /// Returns the count written, or <c>AVERROR_EOF</c> at end of file — ffmpeg treats a plain 0 as
    /// "no data yet" and can spin on it, so the explicit EOF code is the correct answer.
    /// </summary>
    public int Read(void* opaque, byte* targetBuffer, int targetBufferLength)
    {
        if (_disposed || targetBufferLength <= 0)
        {
            return AvErrorEof;
        }

        try
        {
            var read = _stream.Read(new Span<byte>(targetBuffer, targetBufferLength));
            return read <= 0 ? AvErrorEof : read;
        }
        catch (Exception)
        {
            // A read failure mid-playback (share dropped, file vanished) must surface to ffmpeg as EOF
            // rather than tearing down the process from a native callback.
            return AvErrorEof;
        }
    }

    /// <summary>
    /// Seek within the file, or report its length when <paramref name="whence"/> is
    /// <c>AVSEEK_SIZE</c>. Returns a negative value if the request cannot be honoured.
    /// </summary>
    public long Seek(void* opaque, long offset, int whence)
    {
        if (_disposed)
        {
            return -1;
        }

        try
        {
            if (whence == AvseekSize)
            {
                return _stream.Length;
            }

            var origin = whence switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => (SeekOrigin?)null,
            };

            return origin is { } o ? _stream.Seek(offset, o) : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Releasing the handle is best-effort; a throw here would surface as a spurious player error.
        }
    }
}
