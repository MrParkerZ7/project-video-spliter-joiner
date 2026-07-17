using System.Collections.Generic;
using FluentAssertions;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Verifies the signature-based classification in <see cref="FfmpegErrorMapper"/>. Each case
/// feeds a representative stderr tail and asserts the category, a non-empty friendly headline,
/// and that the raw tail is always preserved for the details expander.
/// </summary>
public sealed class FfmpegErrorMapperTests
{
    private static UserFacingError Map(int exitCode, params string[] tail)
        => FfmpegErrorMapper.Map(tail, exitCode);

    [Fact]
    public void Map_BinaryNotFound_IsClassified()
    {
        var error = Map(127, "'ffmpeg' is not recognized as an internal or external command,");

        error.Category.Should().Be(ErrorCategory.BinaryNotFound);
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.RawTail.Should().Contain("not recognized");
    }

    [Fact]
    public void Map_UnknownEncoder_IsUnsupportedCodec()
    {
        var error = Map(1, "[libx265 @ 0000] Unknown encoder 'libx265'");

        error.Category.Should().Be(ErrorCategory.UnsupportedCodec);
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.RawTail.Should().Contain("Unknown encoder");
    }

    [Fact]
    public void Map_DecoderNotFound_IsUnsupportedCodec()
    {
        var error = Map(1, "Decoder (codec av1) not found for input stream #0:0");

        error.Category.Should().Be(ErrorCategory.UnsupportedCodec);
        error.RawTail.Should().Contain("not found");
    }

    [Fact]
    public void Map_NoSpaceLeft_IsDiskFull()
    {
        var error = Map(1, "av_interleaved_write_frame(): No space left on device");

        error.Category.Should().Be(ErrorCategory.DiskFull);
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.RawTail.Should().Contain("No space left on device");
    }

    [Fact]
    public void Map_Exit28_IsDiskFull_EvenWithBenignWarningTail()
    {
        // The real T-035 shape: ffmpeg fails writing the output with exit -28 (== AVERROR(ENOSPC)),
        // but the captured stderr tail is the benign mpegts "start time…" warning, NOT the
        // "No space left on device" phrase. Keying on the exit code must still classify DiskFull.
        var error = Map(
            -28,
            "[mpegts @ 000001] start time for stream 2 is not set in estimate_timings_from_pts",
            "Input #0, mpegts, from 'F:\\_Janpanese\\...\\映像.ts':");

        error.Category.Should().Be(ErrorCategory.DiskFull);
        error.Message.Should().Contain("space");
        error.RawTail.Should().Contain("start time for stream 2");
    }

    [Fact]
    public void Map_ENOSPCPhrase_IsDiskFull()
    {
        var error = Map(1, "Error writing trailer: ENOSPC");

        error.Category.Should().Be(ErrorCategory.DiskFull);
        error.RawTail.Should().Contain("ENOSPC");
    }

    [Fact]
    public void Map_PermissionDenied_IsPermissionDenied()
    {
        var error = Map(1, "output.mp4: Permission denied");

        error.Category.Should().Be(ErrorCategory.PermissionDenied);
        error.RawTail.Should().Contain("Permission denied");
    }

    [Fact]
    public void Map_UnsafeFileName_IsIncompatibleJoin()
    {
        var error = Map(1, "[concat @ 0000] Unsafe file name '../a.mp4'");

        error.Category.Should().Be(ErrorCategory.IncompatibleJoin);
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.RawTail.Should().Contain("Unsafe file name");
    }

    [Fact]
    public void Map_StreamParamMismatch_IsIncompatibleJoin()
    {
        var error = Map(
            1,
            "[concat @ 0000] Input link in0:v0 parameters (size 1920x1080) do not match the corresponding output link in0:v0 parameters (1280x720)");

        error.Category.Should().Be(ErrorCategory.IncompatibleJoin);
        error.RawTail.Should().Contain("do not match");
    }

    [Fact]
    public void Map_InvalidDataFound_IsCorruptInput()
    {
        var error = Map(1, "input.mp4: Invalid data found when processing input");

        error.Category.Should().Be(ErrorCategory.CorruptInput);
        error.RawTail.Should().Contain("Invalid data found");
    }

    [Fact]
    public void Map_DoesNotContainAnyStream_IsCorruptInput()
    {
        var error = Map(1, "input.txt: does not contain any stream");

        error.Category.Should().Be(ErrorCategory.CorruptInput);
        error.RawTail.Should().Contain("does not contain any stream");
    }

    [Fact]
    public void Map_NoSuchFileForInput_IsInvalidArgument()
    {
        var error = Map(1, "missing.mp4: No such file or directory");

        error.Category.Should().Be(ErrorCategory.InvalidArgument);
        error.RawTail.Should().Contain("No such file or directory");
    }

    [Fact]
    public void Map_OptionNotFound_IsInvalidArgument()
    {
        var error = Map(1, "Option nonsense not found.");

        error.Category.Should().Be(ErrorCategory.InvalidArgument);
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.RawTail.Should().Contain("Option nonsense not found");
    }

    [Fact]
    public void Map_CancelSignalExitCode_IsCancelled()
    {
        var error = Map(130, "Exiting normally, received signal 2.");

        error.Category.Should().Be(ErrorCategory.Cancelled);
        error.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Map_UnmatchedTail_IsUnknown_AndPreservesFullRawTail()
    {
        var tail = new[]
        {
            "frame= 42 fps=0.0 q=-1.0",
            "some entirely novel ffmpeg message we have never catalogued",
        };

        var error = FfmpegErrorMapper.Map(tail, 69);

        error.Category.Should().Be(ErrorCategory.Unknown);
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.Message.Should().NotContain("novel ffmpeg message", "the raw stderr must never be the headline");
        error.RawTail.Should().Contain("novel ffmpeg message");
        error.RawTail.Should().Contain("frame= 42");
    }

    [Fact]
    public void Map_FromFfmpegResult_UsesTailAndExitCode()
    {
        var result = new FfmpegResult(1, new List<string> { "output.mp4: Permission denied" });

        var error = FfmpegErrorMapper.Map(result);

        error.Category.Should().Be(ErrorCategory.PermissionDenied);
        error.RawTail.Should().Contain("Permission denied");
    }

    [Fact]
    public void Map_AlwaysPopulatesRawTail_EvenWhenEmpty()
    {
        var error = FfmpegErrorMapper.Map(new List<string>(), 1);

        error.Category.Should().Be(ErrorCategory.Unknown);
        error.RawTail.Should().NotBeNull();
    }
}
