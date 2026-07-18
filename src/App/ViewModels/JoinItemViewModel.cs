using System;
using System.IO;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// One clip queued for a join: its full <see cref="Path"/>, a display filename, and an optional
/// info chip (codec / resolution) populated lazily once the file is probed. WPF-free so the join
/// VM tests can construct and inspect items directly.
/// </summary>
public sealed class JoinItemViewModel : ObservableObject
{
    private string? _infoText;

    /// <summary>Wrap a clip path (info chip is filled in later, once probed).</summary>
    public JoinItemViewModel(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Display = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(Display))
        {
            Display = path;
        }
    }

    /// <summary>Full path of the clip on disk (feeds the join request in list order).</summary>
    public string Path { get; }

    /// <summary>Filename shown in the ordered list.</summary>
    public string Display { get; }

    /// <summary>
    /// A compact "codec · WxH" chip filled in async after a probe; null until probed (or if the
    /// probe failed — the info chip is best-effort and never blocks the compat verdict).
    /// </summary>
    public string? InfoText
    {
        get => _infoText;
        set => SetProperty(ref _infoText, value);
    }

    /// <summary>
    /// The clip's probed duration, or null until probed / if the probe failed. Feeds the Join screen's
    /// "Estimated result" total-duration sum (T-059). Set once by the VM after a successful probe.
    /// </summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// The clip's on-disk byte size, or 0 if it could not be read. Feeds the Join screen's
    /// "Estimated result" approximate-size sum (T-059). Set once by the VM when the clip is added.
    /// </summary>
    public long SizeBytes { get; set; }
}
