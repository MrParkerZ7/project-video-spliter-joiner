using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Detect;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Code-behind for the timeline overlay strip (T-014). All projection/routing logic lives in the
/// WPF-free <see cref="TimelineViewModel"/>; this file is purely the render + hit-test seam: it draws
/// the playhead line + one tick per marker/candidate onto the <c>Track</c> canvas (redrawn on
/// SizeChanged and whenever the VM re-projects), and translates a track click into either a
/// tick-route (seek/preview) or a click-to-cut against the bound VM.
///
/// <para>Marker ticks are tall solid bars; candidate ticks are shorter bars coloured by kind. A click
/// within <see cref="TickHitRadiusPx"/> of a tick routes to that tick (seek for a marker, preview for
/// a candidate); otherwise the click position → normalized X → <see cref="TimelineViewModel.ClickAt"/>
/// drops a snapped cut.</para>
/// </summary>
public partial class TimelineView : UserControl
{
    private const double TickHitRadiusPx = 6d;

    /// <summary>Marker tick colour (a distinct, saturated blue — tall solid bars).</summary>
    public static readonly Brush MarkerBrush = Frozen("#3A6EA5");

    /// <summary>Candidate-kind palette (shared with the legend + <see cref="CandidateKindToBrushConverter"/>).</summary>
    public static readonly Brush BlackKindBrush = Frozen("#6B6B6B");
    public static readonly Brush WhiteKindBrush = Frozen("#6FA8DC");
    public static readonly Brush SceneKindBrush = Frozen("#E69138");

    private static readonly Brush PlayheadBrush = Frozen("#C0392B");

    private TimelineViewModel? _vm;

    public TimelineView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>The brush a candidate tick of <paramref name="kind"/> is drawn in.</summary>
    public static Brush BrushFor(CandidateKind kind) => kind switch
    {
        CandidateKind.Black => BlackKindBrush,
        CandidateKind.White => WhiteKindBrush,
        CandidateKind.Scene => SceneKindBrush,
        _ => MarkerBrush,
    };

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
            or nameof(TimelineViewModel.CandidateTicks)
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

        // Candidate ticks (shorter bars, coloured by kind) — drawn first so markers sit on top.
        foreach (var tick in _vm.CandidateTicks)
        {
            DrawTick(tick.Normalized * width, height * 0.55, BrushFor(tick.Kind ?? CandidateKind.Scene), thickness: 2d, tag: tick);
        }

        // Marker ticks (tall solid bars).
        foreach (var tick in _vm.MarkerTicks)
        {
            DrawTick(tick.Normalized * width, height, MarkerBrush, thickness: 3d, tag: tick);
        }

        // Playhead line (drawn last, on top).
        var playX = _vm.PlayheadNormalized * width;
        var playhead = new Line
        {
            X1 = playX,
            X2 = playX,
            Y1 = 0,
            Y2 = height,
            Stroke = PlayheadBrush,
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

        // Prefer routing to the nearest tick within the hit radius (seek/preview); else drop a cut.
        var hit = NearestTick(x, width);
        if (hit is not null)
        {
            if (hit.Ref is CutMarkerViewModel)
            {
                _vm.SeekMarkerTick(hit);
            }
            else if (hit.Ref is CandidateViewModel)
            {
                _vm.PreviewCandidateTick(hit);
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

        // Markers take precedence over candidates at the same spot (they are the committed cut).
        foreach (var tick in _vm!.MarkerTicks)
        {
            var dist = Math.Abs(tick.Normalized * width - xPx);
            if (dist <= bestDist)
            {
                best = tick;
                bestDist = dist;
            }
        }

        if (best is not null)
        {
            return best;
        }

        foreach (var tick in _vm.CandidateTicks)
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
