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
    private bool _isSnapPending;

    /// <summary>
    /// Create a marker. <paramref name="keyframesAccessor"/> returns the current keyframe list of
    /// the loaded file so a marker re-snaps against the latest keyframes whenever its
    /// <see cref="Requested"/> time changes (deferred, since the file may load after markers exist
    /// in some flows — in practice markers are only added once a file is loaded).
    ///
    /// <para><paramref name="snapPending"/> (T-041) creates the marker in an UNRESOLVED state: the
    /// snap is left as an identity (Snapped = Requested, Delta = 0) and <see cref="IsSnapPending"/>
    /// is true, so the marker can appear INSTANTLY while the background keyframe scan is still
    /// running. Once the keyframes arrive the owner calls <see cref="ResolveSnap"/> to recompute the
    /// snap in place. When false (default) the marker snaps synchronously against whatever keyframes
    /// the accessor returns — the original, keyframes-ready path.</para>
    /// </summary>
    public CutMarkerViewModel(
        IMediaProbe probe,
        Func<IReadOnlyList<TimeSpan>> keyframesAccessor,
        TimeSpan requested,
        bool snapPending = false)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _keyframesAccessor = keyframesAccessor ?? throw new ArgumentNullException(nameof(keyframesAccessor));
        _requested = requested;

        if (snapPending)
        {
            // Optimistic add (T-041): show at the requested time now, resolve the snap later.
            _isSnapPending = true;
            _snapped = requested;
            _delta = TimeSpan.Zero;
        }
        else
        {
            Resnap();
        }
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
                RaiseSnapNote();
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
                RaiseSnapNote();
            }
        }
    }

    /// <summary>
    /// True while this marker was placed optimistically (T-041) before the background keyframe scan
    /// finished — its <see cref="Snapped"/>/<see cref="Delta"/> are provisional (identity) until
    /// <see cref="ResolveSnap"/> runs. The view can show a "snapping…" hint while this is true.
    /// </summary>
    public bool IsSnapPending
    {
        get => _isSnapPending;
        private set
        {
            if (SetProperty(ref _isSnapPending, value))
            {
                OnPropertyChanged(nameof(Display));
                RaiseSnapNote();
            }
        }
    }

    /// <summary>
    /// Per-row snap preview, e.g. <c>"01:23.4 → 01:22.0 (−1.4s)"</c>. When there is no snap (no
    /// keyframes, or the request already sits on a keyframe) the delta reads <c>(0.0s)</c>. While
    /// the snap is still pending (T-041), shows the requested time with a "snapping…" hint instead.
    /// </summary>
    public string Display =>
        _isSnapPending
            ? $"{FormatClock(Requested)} → snapping…"
            : $"{FormatClock(Requested)} → {FormatClock(Snapped)} ({FormatDelta(Delta)})";

    /// <summary>
    /// Compact secondary readout for a row that already shows the REQUESTED time (T-119, epic G-041):
    /// <c>"→ 00:04.0 (−1.0s)"</c>, or <c>"→ snapping…"</c> while the scan is in flight. Empty when the
    /// request already sits on a keyframe (delta 0) so a fine-GOP file carries no visual noise.
    ///
    /// <para>This exists because a cut that snaps to the SAME keyframe as the previous one changes
    /// nothing on screen when only <see cref="Snapped"/> is shown — making a correct snap
    /// indistinguishable from a click the app ignored (the G-041 bug report).</para>
    /// </summary>
    public string SnapNote =>
        _isSnapPending
            ? "→ snapping…"
            : HasSnapNote ? $"→ {FormatClock(Snapped)} ({FormatDelta(Delta)})" : string.Empty;

    /// <summary>True when there is a snap worth showing (pending, or a non-zero offset).</summary>
    public bool HasSnapNote => _isSnapPending || _delta != TimeSpan.Zero;

    private void RaiseSnapNote()
    {
        OnPropertyChanged(nameof(SnapNote));
        OnPropertyChanged(nameof(HasSnapNote));
    }

    /// <summary>
    /// Recompute this marker's snap against the current keyframes and clear
    /// <see cref="IsSnapPending"/> (T-041). Called by the owner once the background keyframe scan
    /// completes so an optimistically-placed marker resolves to its nearest keyframe in place. If no
    /// keyframes are available it falls back to an identity snap (delta 0), never crashing.
    /// </summary>
    public void ResolveSnap()
    {
        Resnap();
        IsSnapPending = false;
    }

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
