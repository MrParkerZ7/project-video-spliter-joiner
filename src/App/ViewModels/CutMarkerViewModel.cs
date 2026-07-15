using System;
using System.Globalization;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// One cut marker in the split timeline list. Holds the user's <see cref="Requested"/> time and,
/// once snapped against the loaded file's keyframes, the <see cref="Snapped"/> keyframe time and
/// the signed <see cref="Delta"/> (Snapped − Requested). Exposes a single human-readable
/// <see cref="Display"/> string so the snap is visible per row in the marker list
/// (e.g. <c>"01:23.4 → 01:22.0 (−1.4s)"</c>).
/// </summary>
public sealed class CutMarkerViewModel : ObservableObject
{
    private readonly IMediaProbe _probe;
    private readonly Func<IReadOnlyList<TimeSpan>> _keyframesAccessor;
    private TimeSpan _requested;
    private TimeSpan _snapped;
    private TimeSpan _delta;

    /// <summary>
    /// Create a marker. <paramref name="keyframesAccessor"/> returns the current keyframe list of
    /// the loaded file so a marker re-snaps against the latest keyframes whenever its
    /// <see cref="Requested"/> time changes (deferred, since the file may load after markers exist
    /// in some flows — in practice markers are only added once a file is loaded).
    /// </summary>
    public CutMarkerViewModel(IMediaProbe probe, Func<IReadOnlyList<TimeSpan>> keyframesAccessor, TimeSpan requested)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _keyframesAccessor = keyframesAccessor ?? throw new ArgumentNullException(nameof(keyframesAccessor));
        _requested = requested;
        Resnap();
    }

    /// <summary>The user's requested cut time (pre-snap). Setting it re-snaps against the keyframes.</summary>
    public TimeSpan Requested
    {
        get => _requested;
        set
        {
            if (SetProperty(ref _requested, value))
            {
                Resnap();
            }
        }
    }

    /// <summary>The requested time snapped to the nearest keyframe (equals Requested when no keyframes).</summary>
    public TimeSpan Snapped
    {
        get => _snapped;
        private set
        {
            if (SetProperty(ref _snapped, value))
            {
                OnPropertyChanged(nameof(Display));
            }
        }
    }

    /// <summary>Signed snap offset (<c>Snapped − Requested</c>); negative = snapped earlier.</summary>
    public TimeSpan Delta
    {
        get => _delta;
        private set
        {
            if (SetProperty(ref _delta, value))
            {
                OnPropertyChanged(nameof(Display));
            }
        }
    }

    /// <summary>
    /// Per-row snap preview, e.g. <c>"01:23.4 → 01:22.0 (−1.4s)"</c>. When there is no snap (no
    /// keyframes, or the request already sits on a keyframe) the delta reads <c>(0.0s)</c>.
    /// </summary>
    public string Display =>
        $"{FormatClock(Requested)} → {FormatClock(Snapped)} ({FormatDelta(Delta)})";

    private void Resnap()
    {
        var keyframes = _keyframesAccessor();
        if (keyframes is { Count: > 0 })
        {
            var snap = _probe.SnapToNearestKeyframe(keyframes, _requested);
            Snapped = snap.Snapped;
            Delta = snap.Delta;
        }
        else
        {
            // No keyframes available (file not loaded / degenerate) → identity snap.
            Snapped = _requested;
            Delta = TimeSpan.Zero;
        }

        OnPropertyChanged(nameof(Display));
    }

    /// <summary>Format a time as <c>MM:SS.f</c> (or <c>HH:MM:SS.f</c> past an hour).</summary>
    internal static string FormatClock(TimeSpan t)
    {
        var sign = t < TimeSpan.Zero ? "-" : string.Empty;
        var a = t < TimeSpan.Zero ? t.Negate() : t;
        return a.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{(int)a.TotalHours:00}:{a.Minutes:00}:{a.Seconds:00}.{a.Milliseconds / 100}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{a.Minutes:00}:{a.Seconds:00}.{a.Milliseconds / 100}");
    }

    /// <summary>Format the signed delta in seconds, e.g. <c>−1.4s</c> / <c>+0.6s</c> / <c>0.0s</c>.</summary>
    internal static string FormatDelta(TimeSpan delta)
    {
        var seconds = delta.TotalSeconds;
        // Use a real minus sign for negative to match the "(−1.4s)" spec; plus for positive.
        var sign = seconds < 0 ? "−" : seconds > 0 ? "+" : string.Empty;
        var magnitude = Math.Abs(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{magnitude:0.0}s");
    }
}
