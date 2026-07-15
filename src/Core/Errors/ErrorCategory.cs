namespace VideoSplitJoiner.Core.Errors;

/// <summary>
/// Classification of an ffmpeg/ffprobe failure, derived from scanning the captured
/// stderr tail (and exit code) for known signatures. Drives the friendly headline and
/// hint surfaced to the user.
/// </summary>
public enum ErrorCategory
{
    /// <summary>The ffmpeg/ffprobe binary could not be found or launched.</summary>
    BinaryNotFound,

    /// <summary>A codec/encoder/decoder required by the operation is unavailable.</summary>
    UnsupportedCodec,

    /// <summary>The target disk ran out of space while writing output.</summary>
    DiskFull,

    /// <summary>The process lacked permission to read the input or write the output.</summary>
    PermissionDenied,

    /// <summary>The inputs of a join/concat were not compatible (param/stream mismatch, unsafe name).</summary>
    IncompatibleJoin,

    /// <summary>The input file is corrupt or contains invalid data.</summary>
    CorruptInput,

    /// <summary>An argument/option passed to ffmpeg was invalid or unknown.</summary>
    InvalidArgument,

    /// <summary>The operation was cancelled by the user.</summary>
    Cancelled,

    /// <summary>The failure did not match any known signature.</summary>
    Unknown,
}
