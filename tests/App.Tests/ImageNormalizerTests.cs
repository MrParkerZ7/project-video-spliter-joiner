using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using VideoSplitJoiner.App.Io;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-169 (SPEC-007 I74) — uploads are re-encoded to the one stored width, so a profile's picture is the
/// same size whichever of the three gestures produced it.
///
/// <para>I74 has always CLAIMED this. It was false for uploads, which the store copies byte-for-byte
/// (I42): a real machine held 6 pictures at 64px (uploads) against 4 at 96px (captures), so preview
/// sharpness silently depended on provenance. These tests are what make the claim true.</para>
/// </summary>
public sealed class ImageNormalizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "vsj-norm-" + Guid.NewGuid().ToString("N"));

    public ImageNormalizerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void APictureWiderThanTheTarget_IsShrunkToIt()
    {
        var source = WritePng("wide.png", 900, 500);

        var result = ImageNormalizer.ShrinkToWidth(source, 320);

        result.Should().NotBeNull("a 900px upload is exactly what needs normalizing");
        WidthOf(result!).Should().Be(320, "the whole point is that every stored picture is one width");
        File.Delete(result!);
    }

    /// <summary>
    /// The aspect ratio survives. A squashed thumbnail would be worse than a small one, and scaling only
    /// the width is the easy way to get that wrong.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ShrinkingKeepsTheAspectRatio()
    {
        var source = WritePng("wide.png", 800, 200);   // 4:1

        var result = ImageNormalizer.ShrinkToWidth(source, 320);

        result.Should().NotBeNull();
        var (w, h) = SizeOf(result!);
        (w / (double)h).Should().BeApproximately(4.0, 0.05, "800x200 is 4:1 and must stay 4:1");
        File.Delete(result!);
    }

    /// <summary>
    /// Never upscale. Blowing a 64px picture up to 320 adds bytes and no detail, turning "small but
    /// sharp" into "large and soft" — the exact outcome the hover preview exists to avoid. Six of the
    /// eleven pictures in the real store that prompted this work are 64px wide.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void APictureAlreadyNarrowerThanTheTarget_IsLeftEntirelyAlone()
    {
        var source = WritePng("small.png", 64, 36);
        var before = File.ReadAllBytes(source);

        ImageNormalizer.ShrinkToWidth(source, 320).Should().BeNull(
            "null means 'store the original' — a 64px picture must not be inflated to 320");

        File.ReadAllBytes(source).Should().Equal(before, "and the source is not touched either");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ExactlyTheTargetWidthIsLeftAlone()
    {
        var source = WritePng("exact.png", 320, 180);

        ImageNormalizer.ShrinkToWidth(source, 320).Should().BeNull(
            "re-encoding a picture that is already the right width would lose quality for nothing");
    }

    /// <summary>
    /// Best-effort by contract: anything unusable returns null and the caller stores the original. A
    /// picture that cannot be re-encoded is still a picture — refusing it here would turn a cosmetic
    /// improvement into data loss. (Files that are not images at all are refused upstream by T-170.)
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AnythingUnusableReturnsNullInsteadOfThrowing()
    {
        var notAnImage = Path.Combine(_dir, "junk.png");
        File.WriteAllText(notAnImage, "x");

        ImageNormalizer.ShrinkToWidth(notAnImage, 320).Should().BeNull();
        ImageNormalizer.ShrinkToWidth(Path.Combine(_dir, "missing.png"), 320).Should().BeNull();
        ImageNormalizer.ShrinkToWidth(null, 320).Should().BeNull();
        ImageNormalizer.ShrinkToWidth(WritePng("ok.png", 900, 500), 0).Should().BeNull(
            "a nonsense target width is not a reason to throw");
    }

    private string WritePng(string name, int width, int height)
    {
        var path = Path.Combine(_dir, name);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 251);   // non-uniform, so the encoder cannot collapse it to nothing
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, palette: null, pixels, stride);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);

        return path;
    }

    private static int WidthOf(string path) => SizeOf(path).Width;

    private static (int Width, int Height) SizeOf(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return (image.PixelWidth, image.PixelHeight);
    }
}
