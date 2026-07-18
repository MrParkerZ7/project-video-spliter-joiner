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
/// Code-behind for the timeline overlay strip (T-014). All projection/routing logic lives in the
/// WPF-free <see cref="TimelineViewModel"/>; this file is purely the render + hit-test seam: it draws
/// the playhead line + one tick per marker onto the <c>Track</c> canvas (redrawn on SizeChanged and
/// whenever the VM re-projects), and translates a track click into either a tick-route (seek) or a
/// click-to-cut against the bound VM.
///
/// <para>Marker ticks are tall solid bars. A click within <see cref="TickHitRadiusPx"/> of a tick
/// routes to that tick (seek); otherwise the click position → normalized X →
/// <see cref="TimelineViewModel.ClickAt"/> drops a snapped cut.</para>
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

    private TimelineViewModel? _vm;

    /// <summary>Marker ticks + playhead colour — the gold theme accent, resolved live from resources.</summary>
    private Brush AccentBrush => TryFindResource("AccentBrush") as Brush ?? FallbackAccentBrush;

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

        _vm = DataContext as TimelineViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
        }

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

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    // ---- Rendering --------------------------------------------------------------------------

    private void Redraw()
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

        // Gold accent, resolved once per redraw from the live theme resource.
        var accent = AccentBrush;

        // Marker ticks (tall solid bars) — gold pins.
        foreach (var tick in _vm.MarkerTicks)
        {
            DrawTick(tick.Normalized * width, height, accent, thickness: 3d, tag: tick);
        }

        // Playhead line (drawn last, on top) — gold.
        var playX = _vm.PlayheadNormalized * width;
        var playhead = new Line
        {
            X1 = playX,
            X2 = playX,
            Y1 = 0,
            Y2 = height,
            Stroke = accent,
            StrokeThickness = 2d,
            IsHitTestVisible = false,
        };
        Track.Children.Add(playhead);
    }

    private void DrawTick(double x, double barHeight, Brush brush, double thickness, TimelineTick tag)
    {
        var line = new Line
        {
            X1 = x,
            X2 = x,
            Y1 = Track.ActualHeight - barHeight,
            Y2 = Track.ActualHeight,
            Stroke = brush,
            StrokeThickness = thickness,
            // Let clicks fall through to the canvas so a single handler does hit-testing against X.
            IsHitTestVisible = false,
            Tag = tag,
        };
        Track.Children.Add(line);
    }

    // ---- Click routing ----------------------------------------------------------------------

    private void OnTrackClicked(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var width = Track.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var x = e.GetPosition(Track).X;

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
        TimelineTick? best = null;
        var bestDist = TickHitRadiusPx;

        foreach (var tick in _vm!.MarkerTicks)
        {
            var dist = Math.Abs(tick.Normalized * width - xPx);
            if (dist <= bestDist)
            {
                best = tick;
                bestDist = dist;
            }
        }

        return best;
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
