using System;
using System.Linq;
using System.Reflection;

namespace VideoSplitJoiner.App;

/// <summary>
/// The running build's version, for display in the app header (T-154).
///
/// <para><b>Why this exists.</b> A drag-and-drop bug was investigated, fixed, published and re-tested —
/// and the report came back "still doesn't work", because the app being dragged onto was a **published
/// build from the previous day** sitting in <c>dist/publish/</c> while the fix lived in a Debug build
/// somewhere else. Two copies of the same program were running side by side and nothing on screen told
/// them apart. That cost a full round-trip.</para>
///
/// <para>Showing the version makes "which build am I looking at?" answerable at a glance, by anyone, in
/// any bug report — including a screenshot.</para>
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Short display form: <c>v1.2.0</c>, or <c>v1.2.0+a696837</c> when the build stamped a commit.
    /// Never throws and never returns null — a missing version is not worth a crash.
    /// </summary>
    public static string Display
    {
        get
        {
            try
            {
                var informational = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                if (string.IsNullOrWhiteSpace(informational))
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v is null ? "v?" : $"v{v.Major}.{v.Minor}.{v.Build}";
                }

                // "1.2.0+a696837e9..." -> "v1.2.0+a696837": the version, plus enough sha to identify
                // the exact build without turning the header into a hash.
                var parts = informational.Split('+');
                var version = parts[0];
                var sha = parts.Length > 1 && parts[1].Length >= 7 ? parts[1][..7] : null;

                return sha is null ? $"v{version}" : $"v{version}+{sha}";
            }
            catch
            {
                return "v?";
            }
        }
    }
}
