using System;
using System.IO;

namespace VideoSplitJoiner.App.Io;

/// <summary>
/// The three disk questions a delete-originals path has to ask, answered without ever throwing.
///
/// <para>Extracted in T-162 (G-052) when the Split screen needed exactly the predicates Bulk Cut had
/// been carrying privately. Every one of them guards an irreversible action, so having two copies is
/// the worst possible place for them to drift — a subtly different "is this file really there?" on one
/// screen is a deleted original on the other.</para>
///
/// <para><b>They never throw, by design.</b> A too-long, reserved, malformed or unreachable path makes
/// the underlying call throw, and these are used to decide whether it is *safe* to delete something. A
/// question that cannot be answered must answer "no" — an exception escaping here would either abort a
/// sweep midway or, worse, be caught somewhere that treats it as a pass.</para>
/// </summary>
internal static class FileFacts
{
    /// <summary>True when the path names a file that exists. False for null/blank, and on any error.</summary>
    internal static bool Exists(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the path names a file that exists <b>and has content</b>.
    ///
    /// <para>The length check is the load-bearing half. A zero-byte output is present as far as
    /// <see cref="File.Exists"/> is concerned and contains none of the footage it should — treating it
    /// as a successful result is how an original gets binned after a failed write.</para>
    /// </summary>
    internal static bool IsNonEmpty(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when two paths resolve to the same file, comparing full paths case-insensitively.
    ///
    /// <para>Used to catch the case where an output was written over its own source — deleting "the
    /// original" then destroys the only copy. Both screens can reach it: Bulk Cut through
    /// replace-originals mode, Split because its output folder defaults to the source's own.</para>
    /// </summary>
    internal static bool Same(string? a, string? b)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }

            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
