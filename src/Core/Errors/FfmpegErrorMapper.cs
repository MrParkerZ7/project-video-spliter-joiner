using System;
using System.Collections.Generic;
using System.Linq;
using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Core.Errors;

/// <summary>
/// Turns a raw <see cref="FfmpegResult"/> (or a stderr tail + exit code) into a
/// <see cref="UserFacingError"/> with a friendly headline. Classification is signature-based:
/// it scans the stderr tail for well-known ffmpeg/ffprobe phrases. The raw tail is ALWAYS
/// preserved on the result so a "details" expander can show the real output — a bare
/// exception/stderr string is never surfaced as the headline.
/// </summary>
public static class FfmpegErrorMapper
{
    /// <summary>Maps a full <see cref="FfmpegResult"/> to a friendly error.</summary>
    public static UserFacingError Map(FfmpegResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Map(result.StdErrTail, result.ExitCode);
    }

    /// <summary>
    /// Maps a stderr tail + exit code to a friendly error. Signature scan is case-insensitive.
    /// The tail is matched most-specific-first so overlapping phrases resolve deterministically.
    /// </summary>
    public static UserFacingError Map(IReadOnlyList<string> stderrTail, int exitCode)
    {
        var tail = stderrTail ?? Array.Empty<string>();
        var raw = string.Join(Environment.NewLine, tail);
        var haystack = raw;

        bool Has(string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        // Cancellation is surfaced explicitly by callers; treat sentinel exit codes as cancelled.
        // ffmpeg has no dedicated "cancelled" code, but our runner may pass one through; guard on
        // the common cancel-signal codes so a cancel path never renders as Unknown.
        if (exitCode is 130 or 137 or 143 || Has("Exiting normally, received signal 2"))
        {
            return new UserFacingError(
                ErrorCategory.Cancelled,
                "The operation was cancelled.",
                raw,
                Hint: null);
        }

        // Binary not found — the process could not be launched at all.
        if (Has("No such file or directory: 'ffmpeg")
            || Has("is not recognized as an internal or external command")
            || Has("ffmpeg: command not found")
            || Has("Cannot find ffmpeg")
            || Has("The system cannot find the file specified"))
        {
            return new UserFacingError(
                ErrorCategory.BinaryNotFound,
                "The ffmpeg tool could not be found.",
                raw,
                "Make sure ffmpeg is installed and its location is configured.");
        }

        // Disk full.
        if (Has("No space left on device"))
        {
            return new UserFacingError(
                ErrorCategory.DiskFull,
                "The disk ran out of space while writing the output.",
                raw,
                "Free up space on the output drive, or choose a different output folder.");
        }

        // Permission denied.
        if (Has("Permission denied"))
        {
            return new UserFacingError(
                ErrorCategory.PermissionDenied,
                "Access was denied while reading the input or writing the output.",
                raw,
                "Check that the file isn't open elsewhere and that you can write to the output folder.");
        }

        // Unsupported codec / encoder / decoder.
        if (Has("Unknown encoder")
            || Has("Unknown decoder")
            || Has("Decoder not found")
            || Has("Encoder not found")
            || Has("Unsupported codec")
            || (Has("Decoder") && Has("not found"))
            || (Has("Encoder") && Has("not found")))
        {
            return new UserFacingError(
                ErrorCategory.UnsupportedCodec,
                "This file uses a video/audio format that isn't supported.",
                raw,
                "The required codec isn't available in this build of ffmpeg.");
        }

        // Incompatible join / concat — check before generic InvalidArgument since concat
        // failures often also mention options.
        if (Has("Unsafe file name")
            || Has("do not match the corresponding output link")
            || Has("Input link parameters")
            || Has("differ in dimension")
            || Has("Cannot find a matching stream")
            || Has("concat")
            || Has("streams are not matching"))
        {
            return new UserFacingError(
                ErrorCategory.IncompatibleJoin,
                "These clips can't be joined directly because their formats don't match.",
                raw,
                "Re-encode the clips to a common format before joining.");
        }

        // Corrupt input / invalid data.
        if (Has("Invalid data found when processing input")
            || Has("moov atom not found")
            || Has("Invalid NAL unit size")
            || Has("error while decoding"))
        {
            return new UserFacingError(
                ErrorCategory.CorruptInput,
                "The input file appears to be corrupt or unreadable.",
                raw,
                "Try re-exporting or re-downloading the source file.");
        }

        // "does not contain any stream" — file present but has nothing usable.
        if (Has("does not contain any stream")
            || Has("could not find codec parameters"))
        {
            return new UserFacingError(
                ErrorCategory.CorruptInput,
                "The input file doesn't contain any usable video or audio.",
                raw,
                "Confirm the file is a valid media file.");
        }

        // "No such file or directory" (input path, not the binary) — invalid argument/path.
        if (Has("No such file or directory"))
        {
            return new UserFacingError(
                ErrorCategory.InvalidArgument,
                "A file that was referenced could not be found.",
                raw,
                "Check that the input path is correct and the file still exists.");
        }

        // Invalid argument / unknown option.
        if (Has("Option not found")
            || (Has("Option") && Has("not found"))
            || Has("Unrecognized option")
            || Has("Invalid argument")
            || Has("Error splitting the argument list"))
        {
            return new UserFacingError(
                ErrorCategory.InvalidArgument,
                "The operation was configured with an invalid setting.",
                raw,
                "This is likely a bug — the arguments passed to ffmpeg were rejected.");
        }

        // Unmatched — never surface raw stderr as the headline; keep the full tail for details.
        return new UserFacingError(
            ErrorCategory.Unknown,
            exitCode == 0
                ? "The operation reported an unexpected result."
                : $"The operation failed (exit code {exitCode}).",
            raw,
            "See the details for the raw output from ffmpeg.");
    }
}
