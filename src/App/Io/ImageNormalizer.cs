using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace VideoSplitJoiner.App.Io;

/// <summary>
/// Re-encodes a user-supplied picture to the width the app stores thumbnails at (T-169).
///
/// <para><b>Why this exists.</b> A profile thumbnail has three sources and SPEC-007 I74 claimed all three
/// "store at <c>ProfileThumbnailWidth</c>, so the stored picture is the same size whichever produced it."
/// That was false: the auto and snapshot paths grab at that width through ffmpeg, but an <b>upload</b> was
/// copied into the store byte-for-byte (I42). Measured on a real machine, 6 of 11 stored pictures were
/// 64px wide uploads against 4 captures at 96 — so preview sharpness silently depended on how the picture
/// had been made, with nothing on screen explaining why one profile looked crisp and another did not.
/// Normalizing on save is what makes I74 true rather than aspirational.</para>
///
/// <para><b>Never upscales.</b> A picture already narrower than the target is stored as it is. Blowing a
/// 64px image up to 320 would add bytes and no detail, and would turn "small but sharp" into "large and
/// soft" — the outcome the hover preview exists to avoid.</para>
///
/// <para><b>Best-effort by contract.</b> Any failure returns null and the caller stores the original,
/// exactly as it does today. A picture that cannot be re-encoded is still a picture; refusing it here
/// would turn a cosmetic improvement into data loss. Files that are not images at all are already
/// refused upstream (<see cref="ImageSignature"/>, T-170).</para>
/// </summary>
internal static class ImageNormalizer
{
    /// <summary>
    /// Re-encode <paramref name="sourcePath"/> to at most <paramref name="targetWidth"/> pixels wide,
    /// writing a JPEG beside it in the temp folder and returning that path. Returns null when the source
    /// is already narrow enough, or when anything at all goes wrong.
    /// </summary>
    internal static string? ShrinkToWidth(string? sourcePath, int targetWidth)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || targetWidth <= 0 || !File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;               // read now, don't lock the file
            source.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            source.UriSource = new Uri(sourcePath, UriKind.Absolute);
            source.EndInit();
            source.Freeze();

            if (source.PixelWidth <= targetWidth)
            {
                return null;   // already small enough — storing it verbatim loses nothing
            }

            var scale = targetWidth / (double)source.PixelWidth;
            var scaled = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
            scaled.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(scaled));

            var destination = Path.Combine(
                Path.GetTempPath(),
                "vsj-thumb-" + Guid.NewGuid().ToString("N") + ".jpg");

            using (var stream = File.Create(destination))
            {
                encoder.Save(stream);
            }

            return destination;
        }
        catch
        {
            // Best-effort: the caller falls back to storing the original untouched.
            return null;
        }
    }
}
