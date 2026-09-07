using System;
using System.IO;

namespace VideoSplitJoiner.App;

/// <summary>
/// Answers "is this file an image at all?" from its leading bytes (T-170).
///
/// <para><b>Why this exists.</b> The profile-thumbnail store copies the chosen file's bytes verbatim
/// (SPEC-007 I42) and the record performs no format validation on the path (I35), so the upload gesture
/// stored whatever the picker returned and reported success. The picker offers an <i>All files</i> escape
/// hatch and only sets <c>CheckFileExists</c>, which answers a different question. A real store on a real
/// machine ended up holding a <b>1-byte file containing the single character <c>x</c></b> attached to a
/// profile as its picture.</para>
///
/// <para><b>Why a signature check rather than a decode.</b> <see cref="ViewModels.BulkCutViewModel"/> is
/// deliberately WPF-free, so it cannot reach for <c>BitmapDecoder</c>; injecting a decoder seam that
/// defaults to null would mean the guard is absent unless the composition root wires it — the exact way
/// T-162's delete feature shipped inert. A signature test is pure, needs no wiring, and cannot be
/// forgotten.</para>
///
/// <para><b>What it does NOT claim.</b> It refuses files that are not images; it does not certify that an
/// image is undamaged. A truncated JPEG with an intact header passes here and fails at decode time, where
/// the null-safe path converter already shows the placeholder. That is the honest boundary: this stops
/// junk being stored as a picture, not every possible corruption.</para>
/// </summary>
internal static class ImageSignature
{
    /// <summary>Longest signature we compare (the 12-byte RIFF/WEBP pair).</summary>
    private const int HeaderBytes = 12;

    /// <summary>
    /// True when <paramref name="path"/> begins with the signature of a format the thumbnail picker
    /// offers. Never throws: an unreadable, missing or empty file is simply not an image.
    /// </summary>
    internal static bool IsImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        Span<byte> head = stackalloc byte[HeaderBytes];
        int read;

        try
        {
            using var stream = File.OpenRead(path);
            read = stream.ReadAtLeast(head, HeaderBytes, throwOnEndOfStream: false);
        }
        catch
        {
            // Missing, locked, unreadable — all "not usable as a picture", which is the caller's question.
            return false;
        }

        var bytes = head[..read];

        return StartsWith(bytes, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) // PNG
            || StartsWith(bytes, 0xFF, 0xD8, 0xFF)                               // JPEG
            || StartsWith(bytes, 0x42, 0x4D)                                     // BMP  "BM"
            || StartsWith(bytes, 0x47, 0x49, 0x46, 0x38)                         // GIF  "GIF8"
            || StartsWith(bytes, 0x49, 0x49, 0x2A, 0x00)                         // TIFF little-endian
            || StartsWith(bytes, 0x4D, 0x4D, 0x00, 0x2A)                         // TIFF big-endian
            || IsRiffWebp(bytes);
    }

    /// <summary>WEBP is a RIFF container: "RIFF" ???? "WEBP" — the size field in between is skipped.</summary>
    private static bool IsRiffWebp(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 12
            && StartsWith(bytes, 0x52, 0x49, 0x46, 0x46)
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50;

    private static bool StartsWith(ReadOnlySpan<byte> bytes, params byte[] signature)
        => bytes.Length >= signature.Length && bytes[..signature.Length].SequenceEqual(signature);
}
