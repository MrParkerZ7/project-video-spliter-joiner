using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-170 (SPEC-007) — the acceptance check behind the thumbnail upload refusal.
///
/// <para>The defect was found in a REAL store, not imagined: one of eleven files in
/// <c>%LOCALAPPDATA%/VideoSplitJoiner/profile-thumbs</c> was <b>1 byte</b> containing the single
/// character <c>x</c>, attached to a profile as its picture. The store copies bytes verbatim
/// (SPEC-007 I42), the record validates nothing (I35), and the picker offers an <i>All files</i> escape
/// hatch — so nothing anywhere asked whether the file was an image.</para>
///
/// <para>Every format here is one the picker actually offers, so the list cannot quietly drift from the
/// dialog's filter without a test noticing.</para>
/// </summary>
public sealed class ImageSignatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "vsj-imgsig-" + Guid.NewGuid().ToString("N"));

    public ImageSignatureTests() => Directory.CreateDirectory(_dir);

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

    public static TheoryData<string, byte[]> RealSignatures => new()
    {
        { "png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } },
        { "jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 } },
        { "bmp", new byte[] { 0x42, 0x4D } },
        { "gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 } },
        { "tiff-le", new byte[] { 0x49, 0x49, 0x2A, 0x00 } },
        { "tiff-be", new byte[] { 0x4D, 0x4D, 0x00, 0x2A } },
        { "webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 } },
    };

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [MemberData(nameof(RealSignatures))]
    public void EveryFormatThePickerOffersIsAccepted(string label, byte[] magic)
    {
        var path = Write(label + ".bin", magic.Concat(Encoding.UTF8.GetBytes("…and then some pixels")).ToArray());

        ImageSignature.IsImage(path).Should().BeTrue(
            $"{label} is offered by the thumbnail picker's filter, so refusing it would reject a file the " +
            "dialog invited the user to choose");
    }

    /// <summary>The reported file, byte for byte.</summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void TheOneByteFileFoundInARealStoreIsRefused()
    {
        var path = Write("P_148de9c5a7a44d19.jpg", Encoding.UTF8.GetBytes("x"));

        ImageSignature.IsImage(path).Should().BeFalse(
            "this is the exact file found attached to a profile in a real store — 1 byte, the character " +
            "'x', named .jpg. An extension is not evidence");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Theory]
    [InlineData("text-in-a-jpg.jpg", "img-bytes")]
    [InlineData("empty.png", "")]
    [InlineData("html-error-page.jpg", "<!DOCTYPE html><html><body>404")]
    public void FilesThatAreNotImagesAreRefused_HoweverTheyAreNamed(string name, string content)
    {
        var path = Write(name, Encoding.UTF8.GetBytes(content));

        ImageSignature.IsImage(path).Should().BeFalse(
            "the name and the extension are the user's, not the file's");
    }

    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AMissingOrBlankPathIsNotAnImage_AndNeverThrows()
    {
        ImageSignature.IsImage(Path.Combine(_dir, "nope.png")).Should().BeFalse();
        ImageSignature.IsImage("   ").Should().BeFalse();
        ImageSignature.IsImage(null).Should().BeFalse();
    }

    /// <summary>
    /// A signature shorter than the 12-byte header buffer must still be recognised — a 2-byte BMP file
    /// is legal input to the check, and reading fewer bytes than requested must not be read as failure.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void AFileShorterThanTheHeaderBufferIsStillMatchedOnItsSignature()
    {
        var path = Write("tiny.bmp", new byte[] { 0x42, 0x4D });

        ImageSignature.IsImage(path).Should().BeTrue(
            "the check reads UP TO 12 bytes; a 2-byte file that starts with a real signature is matched " +
            "on what is there, not rejected for being short");
    }

    /// <summary>
    /// RIFF alone is not WEBP — a .wav is also a RIFF container. Pinned because matching the first four
    /// bytes and stopping would be the obvious, wrong implementation.
    /// </summary>
    [Trait("serves-spec", "SPEC-007")]
    [Fact]
    public void ARiffContainerThatIsNotWebpIsRefused()
    {
        var wav = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x24, 0x08, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 };
        var path = Write("audio.webp", wav);

        ImageSignature.IsImage(path).Should().BeFalse(
            "RIFF is a container, not a format — WAVE is not a picture even when named .webp");
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
