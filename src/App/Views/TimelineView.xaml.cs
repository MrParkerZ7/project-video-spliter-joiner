using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VideoSplitJoiner.App.ViewModels;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Code-behind for the timeline overlay strip (T-014) fused with the audio-waveform band (T-084 /
/// D-002). All projection/routing logic lives in the WPF-free <see cref="TimelineViewModel"/>; this
/// file is purely the render + hit-test seam.
///
/// <para><b>Timeline (T-014):</b> draws the playhead line + one tick per marker onto the <c>Track</c>
/// canvas (redrawn on SizeChanged and whenever the VM re-projects), and translates a track click into
/// either a tick-route (seek) or a click-to-cut against the bound VM. Marker ticks are tall solid
/// bars; a click within <see cref="TickHitRadiusPx"/> of a tick routes to that tick (seek), otherwise
/// the click position → normalized X → <see cref="TimelineViewModel.ClickAt"/> drops a snapped cut.</para>
///
/// <para><b>Waveform band (T-084):</b> the <c>Wave</c> canvas sits DIRECTLY ABOVE the <c>Track</c> and
/// shares its exact width + <c>x = time/duration · width</c> mapping (both canvases stretch to the same
/// width, so a peak's horizontal position is the same moment as the tick/playhead below it). The wave is
/// a filled, mirrored <see cref="StreamGeometry"/> built from the VM's normalized <c>Peaks</c>,
/// re-bucketed/scaled to the current pixel width on every redraw (D1: crisp vector, not a raster).
/// The playhead + marker ticks are drawn onto BOTH canvases (D2: they span the combined wave+track
/// height as one aligned unit), and a click ANYWHERE on the wave routes through the same
/// <see cref="OnTrackClicked"/> handler as the track — so clicking the wave seeks / drops a cut exactly
/// like clicking the track. The band collapses when the source has no audio
/// (<see cref="WaveformViewModel.HasAudio"/> false).</para>
/// </summary>
public partial class TimelineView : UserControl
{
    private const double TickHitRadiusPx = 6d;

    /// <summary>
    /// Fallback gold used only if the <c>AccentBrush</c> theme resource cannot be resolved
    /// (e.g. in a design-time / test host without the merged token dictionaries). Matches
    /// <c>AccentColor</c> in <c>Themes/Tokens.xaml</c>. At runtime the ticks + playhead pull the
    /// live <c>AccentBrush</c> via <see cref="AccentBrush"/> so they track the theme, not a literal.
    /// </summary>
    private static readonly Brush FallbackAccentBrush = Frozen("#E0A83A");

    /// <summary>
    /// Fallback muted surface used only if <c>Surface3Brush</c> cannot be resolved (design-time /
    /// test host). Matches <c>Surface3Color</c> in <c>Themes/Tokens.xaml</c> — the wave body fill sits
    /// on a muted surface tone so the gold peak accent reads over it without overpowering the markers.
    /// </summary>
    private static readonly Brush FallbackWaveBodyBrush = Frozen("#1C2028");

    private TimelineViewModel? _vm;
    private WaveformViewModel? _waveform;

    /// <summary>Marker ticks + playhead colour — the gold theme accent, resolved live from resources.</summary>
    private Brush AccentBrush => TryFindResource("AccentBrush") as Brush ?? FallbackAccentBrush;

    /// <summary>The waveform body fill — a muted surface tone, resolved live from the theme.</summary>
    private Brush WaveBodyBrush => TryFindResource("Surface3Brush") as Brush ?? FallbackWaveBodyBrush;

    /// <summary>
    /// The waveform peak accent — a translucent gold tint (25% opacity gold, matching
    /// <c>AccentMutedColor</c>) so the wave reads under the solid gold markers/playhead rather than
    /// competing with them. Resolved live where possible, else a frozen literal.
    /// </summary>
    private Brush WavePeakBrush => TryFindResource("AccentMutedBrush") as Brush ?? FallbackWavePeakBrush;

    private static readonly Brush FallbackWavePeakBrush = Frozen("#40E0A83A");

    public TimelineView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // ---- Data binding → redraw --------------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
        }

        if (_waveform is not null)
        {
            _waveform.PropertyChanged -= OnWaveformChanged;
        }

        _vm = DataContext as TimelineViewModel;
        _waveform = _vm?.Waveform;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
        }

        if (_waveform is not null)
        {
            _waveform.PropertyChanged += OnWaveformChanged;
        }

        ApplyWaveBandVisibility();
        Redraw();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimelineViewModel.MarkerTicks)
            or nameof(TimelineViewModel.PlayheadNormalized))
        {
            Redraw();
        }
    }

    private void OnWaveformChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WaveformViewModel.HasAudio))
        {
            ApplyWaveBandVisibility();
            Redraw();
        }
        else if (e.PropertyName is nameof(WaveformViewModel.Peaks)
            or nameof(WaveformViewModel.IsLoading))
        {
            Redraw();
        }
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void OnWaveSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    /// <summary>
    /// Show the waveform band only when the loaded file has audio (T-084). No audio / no VM →
    /// collapse it so the timeline is exactly the pre-T-084 single-track control (D-002: "no audio
    /// track → hide the band"). Collapsed (not Hidden) so it takes no layout height when absent.
    /// </summary>
    private void ApplyWaveBandVisibility()
    {
        if (WaveBand is null)
        {
            return;
        }

        WaveBand.Visibility = _waveform is { HasAudio: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---- Rendering --------------------------------------------------------------------------

    private void Redraw()
    {
        DrawWave();
        DrawTrack();
    }

    /// <summary>
    /// Draw the audio waveform band (T-084): a filled, mirrored vertical bar per column, re-bucketed
    /// from the VM's normalized <c>Peaks</c> to the current pixel width, plus the SAME playhead +
    /// marker ticks the track carries — drawn full-height across the band so the wave and track read
    /// as one fused unit. No-op / cleared when the band is collapsed or has no peaks.
    /// </summary>
    private void DrawWave()
    {
        if (Wave is null)
        {
            return;
        }

        Wave.Children.Clear();

        var width = Wave.ActualWidth;
        var height = Wave.ActualHeight;
        if (width <= 0 || height <= 0 || _waveform is null || !_waveform.HasAudio)
        {
            return;
        }

        var peaks = _waveform.Peaks;
        if (peaks.Length > 0)
        {
            var geometry = BuildWaveGeometry(peaks, width, height);
            if (geometry is not null)
            {
                Wave.Children.Add(new Path
                {
                    Data = geometry,
                    Fill = WavePeakBrush,
                    Stroke = WaveBodyBrush,
                    StrokeThickness = 0d,
                    IsHitTestVisible = false,
                });
            }
        }

        // The playhead + marker ticks span the wave band too (D2) so they line up as one unit with
        // the track below. Drawn full-height over the wave.
        if (_vm is not null)
        {
            DrawOverlay(Wave, width, height);
        }
    }

    /// <summary>
    /// Build a mirrored (top+bottom) filled waveform geometry from the normalized 0..1 <paramref name="peaks"/>,
    /// scaled to <paramref name="width"/> × <paramref name="height"/> about the vertical centre. When there
    /// are more peaks than pixels the peaks are re-bucketed (max-abs per pixel column) so the wave stays crisp
    /// at any width; when there are fewer, each is sampled per column. A tiny minimum bar height keeps a silent
    /// stretch visible as a faint centre line rather than nothing.
    /// </summary>
    private static StreamGeometry? BuildWaveGeometry(float[] peaks, double width, double height)
    {
        var columns = (int)Math.Floor(width);
        if (columns <= 0 || peaks.Length == 0)
        {
            return null;
        }

        var mid = height / 2d;
        const double minBar = 0.75d; // half-height floor so silence reads as a faint centre line

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geometry.Open())
        {
            // Top edge left→right, then bottom edge right→left, as one closed filled ribbon.
            var topPoints = new System.Collections.Generic.List<Point>(columns);
            var bottomPoints = new System.Collections.Generic.List<Point>(columns);

            for (var c = 0; c < columns; c++)
            {
                var peak = TimelineMath.PeakForColumn(peaks, c, columns);
                var half = Math.Max(minBar, peak * mid);
                var x = c + 0.5d;
                topPoints.Add(new Point(x, mid - half));
                bottomPoints.Add(new Point(x, mid + half));
            }

            ctx.BeginFigure(topPoints[0], isFilled: true, isClosed: true);
            ctx.PolyLineTo(topPoints, isStroked: false, isSmoothJoin: false);
            bottomPoints.Reverse();
            ctx.PolyLineTo(bottomPoints, isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Draw the marker ticks + playhead onto the <c>Track</c> canvas (T-014). Redrawn on SizeChanged and
    /// whenever the VM re-projects.
    /// </summary>
    private void DrawTrack()
    {
        if (Track is null)
        {
            return;
        }

        Track.Children.Clear();

        var width = Track.ActualWidth;
        var height = Track.ActualHeight;
        if (width <= 0 || height <= 0 || _vm is null)
        {
            return;
        }

        DrawOverlay(Track, width, height);
    }

    /// <summary>
    /// Draw the shared marker ticks + playhead onto <paramref name="canvas"/> at the current normalized
    /// positions, spanning its full <paramref name="height"/>. Used for BOTH the wave band and the track
    /// so the ticks + playhead line up as one fused unit across both (D2). The X mapping
    /// (<c>normalized · width</c>) is identical on both canvases because they stretch to the same width.
    /// </summary>
    private void DrawOverlay(Canvas canvas, double width, double height)
    {
        // Gold accent, resolved once per redraw from the live theme resource.
        var accent = AccentBrush;

        // Marker ticks (tall solid bars) — gold pins, full canvas height.
        foreach (var tick in _vm!.MarkerTicks)
        {
            var tx = tick.Normalized * width;
            canvas.Children.Add(new Line
            {
                X1 = tx,
                X2 = tx,
                Y1 = 0,
                Y2 = height,
                Stroke = accent,
                StrokeThickness = 3d,
                // Let clicks fall through to the canvas so a single handler does hit-testing against X.
                IsHitTestVisible = false,
                Tag = tick,
            });
        }

        // Playhead line (drawn last, on top) — gold, full canvas height.
        var playX = _vm.PlayheadNormalized * width;
        canvas.Children.Add(new Line
        {
            X1 = playX,
            X2 = playX,
            Y1 = 0,
            Y2 = height,
            Stroke = accent,
            StrokeThickness = 2d,
            IsHitTestVisible = false,
        });
    }

    // ---- Click routing ----------------------------------------------------------------------

    /// <summary>
    /// Handle a click on EITHER the wave band or the track (T-014 + T-084/D2): both canvases route here,
    /// so clicking the wave seeks / drops a cut exactly like clicking the track. Prefers routing to the
    /// nearest marker tick within the hit radius (seek); otherwise the click position → normalized X →
    /// drops a snapped cut. Hit-testing uses the clicked canvas's own width (both share the same
    /// coordinate system, so the normalized result is identical either way).
    /// </summary>
    private void OnTrackClicked(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not Canvas canvas)
        {
            return;
        }

        var width = canvas.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var x = e.GetPosition(canvas).X;

        // Prefer routing to the nearest marker tick within the hit radius (seek); else drop a cut.
        var hit = NearestTick(x, width);
        if (hit is not null)
        {
            if (hit.Ref is CutMarkerViewModel)
            {
                _vm.SeekMarkerTick(hit);
            }

            e.Handled = true;
            return;
        }

        _vm.ClickAt(x / width);
        e.Handled = true;
    }

    private TimelineTick? NearestTick(double xPx, double width)
    {
        var ticks = _vm!.MarkerTicks;
        var normalized = new double[ticks.Count];
        for (var i = 0; i < ticks.Count; i++)
        {
            normalized[i] = ticks[i].Normalized;
        }

        var index = TimelineMath.NearestNormalizedIndex(normalized, xPx, width, TickHitRadiusPx);
        return index >= 0 ? ticks[index] : null;
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
