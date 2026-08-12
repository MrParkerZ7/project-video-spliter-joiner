using System;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// WPF-free view model for the Split screen's audio waveform band (T-084 / D-002). Holds the
/// normalized 0..1 peak array extracted by the Core <see cref="Core.Waveform.IWaveformService"/> plus
/// two flags the view binds to: <see cref="IsLoading"/> (extraction in flight — a faint baseline while
/// it runs) and <see cref="HasAudio"/> (a non-null peak array arrived — draw the band; false → hide it).
///
/// <para>The waveform sits <b>above</b> the timeline <c>Track</c> and shares its exact
/// <c>x = time/duration · width</c> coordinate system (the band is rendered by <c>TimelineView</c>'s
/// code-behind, which reads <see cref="Peaks"/> from this VM). This VM owns only the DATA + STATE; it
/// draws nothing and holds no WPF types, so it is fully unit-testable with a fake service.</para>
///
/// <para><b>Lifecycle (driven by the owning <see cref="SplitViewModel"/>):</b> a load calls
/// <see cref="BeginLoad"/> (IsLoading = true, band shown as a faint baseline), then exactly one of
/// <see cref="ApplyPeaks"/> (a non-null array → HasAudio = true, redraw) or the null case (HasAudio =
/// false → hide). <see cref="Reset"/> clears everything back to the empty/no-audio state on unload.</para>
/// </summary>
public sealed class WaveformViewModel : ObservableObject
{
    private float[] _peaks = Array.Empty<float>();
    private bool _hasAudio;
    private bool _isLoading;

    /// <summary>
    /// The normalized 0..1 audio peaks (one per waveform column) for the loaded file, or an empty
    /// array when none are available yet / the track is silent-but-hidden. The view re-buckets/scales
    /// this to the current width on redraw. Never null (empty when absent) so the view can index freely.
    /// </summary>
    public float[] Peaks
    {
        get => _peaks;
        private set => SetProperty(ref _peaks, value ?? Array.Empty<float>());
    }

    /// <summary>
    /// True once a non-null peak array has been applied for the current file — the band is drawn.
    /// False before extraction resolves, when the source has no audio track (service returned null),
    /// or after <see cref="Reset"/>. The view binds this to the band's visibility (false → collapsed).
    /// </summary>
    public bool HasAudio
    {
        get => _hasAudio;
        private set => SetProperty(ref _hasAudio, value);
    }

    /// <summary>
    /// True while background extraction is in flight for the current file (a load has begun and no
    /// result has arrived yet). The view can show a faint baseline/shimmer while this holds. Cleared
    /// the moment a result (peaks or null) is applied, or on <see cref="Reset"/>.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// Enter the loading state for a fresh extraction (called when a load kicks off waveform
    /// extraction). Marks <see cref="IsLoading"/> and drops any prior file's peaks so a stale wave is
    /// never shown against the new file while the new extraction runs. <see cref="HasAudio"/> goes
    /// false until <see cref="ApplyPeaks"/> or <see cref="ApplyNoAudio"/> resolves this load.
    /// </summary>
    public void BeginLoad()
    {
        IsLoading = true;
        HasAudio = false;
        Peaks = Array.Empty<float>();
    }

    /// <summary>
    /// Apply a resolved extraction: a non-null <paramref name="peaks"/> array → show the band
    /// (<see cref="HasAudio"/> = true, view redraws); a null array (no audio / best-effort failure) →
    /// hide the band (<see cref="HasAudio"/> = false). Either way <see cref="IsLoading"/> is cleared.
    /// A defensive copy is stored so a later mutation of the caller's array can't corrupt the wave.
    /// </summary>
    public void ApplyPeaks(float[]? peaks)
    {
        IsLoading = false;

        if (peaks is null)
        {
            HasAudio = false;
            Peaks = Array.Empty<float>();
            return;
        }

        // Store our own copy (the service already hands back a copy, but this keeps the VM defensive).
        Peaks = (float[])peaks.Clone();
        HasAudio = true;
    }

    /// <summary>
    /// Resolve the current load as "no audio" — the source has no audio track or extraction failed
    /// (best-effort). Hides the band (<see cref="HasAudio"/> = false) and clears
    /// <see cref="IsLoading"/>. Convenience wrapper over <see cref="ApplyPeaks"/> with a null array.
    /// </summary>
    public void ApplyNoAudio() => ApplyPeaks(null);

    /// <summary>
    /// Reset to the empty / no-audio state on unload (Clear): drop the peaks, hide the band, and clear
    /// the loading flag. The band collapses and the timeline is left unchanged.
    /// </summary>
    public void Reset()
    {
        IsLoading = false;
        HasAudio = false;
        Peaks = Array.Empty<float>();
    }
}
