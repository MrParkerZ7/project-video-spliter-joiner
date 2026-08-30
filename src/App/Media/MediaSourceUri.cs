using System;

namespace VideoSplitJoiner.App.Media;

/// <summary>
/// Decides how a file path can be handed to the preview player, and explains it when it cannot (T-131).
///
/// <para>WPF-free by design so the decision is unit-testable headlessly — <see cref="FfmeMediaPlayer"/>
/// is the only thing that touches the FFME element.</para>
///
/// <para><b>The defect this exists for.</b> The player opened every file with
/// <c>new Uri(path, UriKind.RelativeOrAbsolute)</c> inside a catch-all that surfaced
/// <c>ex.Message</c> verbatim. A UNC path whose SERVER NAME is not a legal URI hostname — most
/// realistically because it contains a space, as consumer NAS boxes often do (<c>\\Seagate NAS\…</c>) —
/// makes that constructor throw <see cref="UriFormatException"/>, so the user was shown
/// <i>"Invalid URI: The hostname could not be parsed."</i> with no hint of the cause or a way forward.
/// A mapped drive letter, an IP address, and hosts containing dots or dashes all parse fine; only
/// characters that are illegal in a URI authority fail.</para>
/// </summary>
public static class MediaSourceUri
{
    /// <summary>
    /// True when <paramref name="path"/> can be expressed as a <see cref="Uri"/> the player can open.
    /// Never throws — a blank path simply answers false.
    /// </summary>
    public static bool TryCreate(string? path, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            uri = new Uri(path, UriKind.RelativeOrAbsolute);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The message shown when <see cref="TryCreate"/> refuses <paramref name="path"/>. States the real
    /// constraint and the two workarounds that actually work, rather than repeating .NET's wording.
    ///
    /// <para>Both workarounds are load-bearing and were verified, not guessed: the cutting engine passes
    /// raw paths straight to ffmpeg as process arguments (no <see cref="Uri"/> anywhere), so typing the
    /// times still produces a correct cut; and a mapped drive letter parses as an ordinary local path.</para>
    /// </summary>
    public static string ExplainRefusal(string? path)
    {
        var host = TryGetUncHost(path);

        var what = host is null
            ? "This location cannot be opened by the preview player."
            : string.Concat(
                "The preview can't open files on the network share \"", host,
                "\" because its name contains a character (usually a space) that a media address can't carry.");

        return string.Concat(
            what,
            " Cutting itself still works: type the times into the IN and OUT boxes.",
            " To get the preview back, map the share to a drive letter (for example Z:) and add the files from there.");
    }

    /// <summary>
    /// The server name of a UNC path (<c>\\server\share\…</c>), or null when <paramref name="path"/> is
    /// not a UNC path. Used only to name the share in <see cref="ExplainRefusal"/>.
    /// </summary>
    public static string? TryGetUncHost(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        const char Sep = '\\';
        if (path!.Length < 3 || path[0] != Sep || path[1] != Sep)
        {
            return null;
        }

        var rest = path.Substring(2);
        var end = rest.IndexOf(Sep);
        var host = end < 0 ? rest : rest.Substring(0, end);

        return host.Length == 0 ? null : host;
    }
}
