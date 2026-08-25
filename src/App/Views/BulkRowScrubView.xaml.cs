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
/// Code-behind for the per-row dual-handle scrub bar (D-004 / T-097). VIEW-ONLY: all projection lives
/// in the WPF-free <see cref="BulkItemViewModel"/> — this file only renders the two snapped offsets +
/// the dropped/keep spans onto the <c>Track</c> canvas and routes drag/hover gestures back to the VM,
/// modelled on <see cref="TimelineView"/> so a <c>Canvas x:Name</c> never has to live inside an
/// <c>ItemsControl</c> template.
///
/// <para><b>Render (from VM offsets):</b> <c>norm(t) = t / Duration</c> → dropped intro scrim
/// <c>[0→introEnd]</c>, dropped outro scrim <c>[outroStart→EOF]</c> (only when the row has an outro),
/// the gold <c>AccentMutedBrush</c> KEEP span <c>[introEnd→(outroStart|EOF)]</c> (the brightest thing on
/// the bar), a gold <c>AccentBrush</c> intro-end handle (top cap, <c>▸</c>), a blue <c>InfoBrush</c>
/// outro-start handle (bottom cap, <c>◂</c>) — disambiguated on THREE axes (colour / cap position /
/// glyph) — and a 3px bottom progress fill while the row runs.</para>
///
/// <para><b>Drag/snap:</b> on down the nearer handle within a hit radius is grabbed; while dragging the
/// live requested position is pushed to the handle's <see cref="CutMarkerViewModel.Requested"/> (which
/// re-snaps) and the dragged handle is painted at the live cursor X so it does not visually "stick" to
/// keyframes mid-drag; on release the drag state clears so the handle re-paints at the settled
/// <see cref="CutMarkerViewModel.Snapped"/> — the snapped value is the shown truth after release.</para>
///
/// <para><b>Hover:</b> a non-drag move feeds <see cref="ThumbnailPreviewViewModel.UpdateHover"/> (the VM
/// is created here from the tab VM's shared <c>IThumbnailService</c>, since the row VM owns no preview);
/// leaving the bar hides the popup.</para>
/// </summary>
public partial class BulkRowScrubView : UserControl
{
    private const double HandleHitRadiusPx = 8d;

    // Frozen fallbacks so the control renders in a design-time / test host without the merged token
    // dictionaries (same discipline as TimelineView). Values match the tokens in Themes/Tokens.xaml.
    private static readonly Brush FallbackAccent = Frozen("#FFE0A83A");
    private static readonly Brush FallbackInfo = Frozen("#FF5B9CF0");
    private static readonly Brush FallbackKeep = Frozen("#40E0A83A");
    private static readonly Brush FallbackScrim = Frozen("#B30A0B0D");

    private enum Handle
    {
        None,
        Intro,
        Outro,
    }

    private BulkItemViewModel? _row;
    private CutMarkerViewModel? _intro;
    private CutMarkerViewModel? _outro;
    private ThumbnailPreviewViewModel? _preview;

    private Handle _dragging = Handle.None;
    private double _dragX;

    private Brush AccentBrush => TryFindResource("AccentBrush") as Brush ?? FallbackAccent;

    private Brush InfoBrush => TryFindResource("InfoBrush") as Brush ?? FallbackInfo;

    private Brush KeepBrush => TryFindResource("AccentMutedBrush") as Brush ?? FallbackKeep;

    private Brush ScrimBrush => TryFindResource("DropScrimBrush") as Brush ?? FallbackScrim;

    public BulkRowScrubView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ---- Binding → subscribe + redraw -------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        _row = DataContext as BulkItemViewModel;
        if (_row is not null)
        {
            _row.PropertyChanged += OnRowChanged;
            HookHandles();
            AttachPreview();
        }

        Redraw();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The shared thumbnail service is reachable only once we are in the tree under BulkCutView.
        AttachPreview();
        Redraw();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A scrolled-away / recycled row must not leave a stuck preview popup.
        _preview?.MouseLeave();
    }

    private void Detach()
    {
        if (_row is not null)
        {
            _row.PropertyChanged -= OnRowChanged;
        }

        UnhookHandles();
        _row = null;
    }

    private void HookHandles()
    {
        UnhookHandles();

        _intro = _row?.IntroEnd;
        _outro = _row?.OutroStart;

        if (_intro is not null)
        {
            _intro.PropertyChanged += OnHandleChanged;
        }

        if (_outro is not null)
        {
            _outro.PropertyChanged += OnHandleChanged;
        }
    }

    private void UnhookHandles()
    {
        if (_intro is not null)
        {
            _intro.PropertyChanged -= OnHandleChanged;
            _intro = null;
        }

        if (_outro is not null)
        {
            _outro.PropertyChanged -= OnHandleChanged;
            _outro = null;
        }
    }

    private void AttachPreview()
    {
        if (_row is null || _preview is not null)
        {
            return;
        }

        var service = FindThumbnailService();
        if (service is null)
        {
            return; // design-time / detached host — hover is simply inert
        }

        _preview = new ThumbnailPreviewViewModel(service);
        _preview.SetInput(_row.Path, _row.Duration);
        HoverThumbPopup.DataContext = _preview;
    }

    private VideoSplitJoiner.Core.Thumbnails.IThumbnailService? FindThumbnailService()
    {
        DependencyObject? node = this;
        while (node is not null)
        {
            if (node is FrameworkElement fe && fe.DataContext is BulkCutViewModel bulk)
            {
                return bulk.Thumbnails;
            }

            node = VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
        }

        return null;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BulkItemViewModel.OutroStart):
            case nameof(BulkItemViewModel.HasOutro):
                HookHandles(); // the outro handle was added/removed — re-subscribe
                Redraw();
                break;
            case nameof(BulkItemViewModel.Duration):
                _preview?.SetDuration(_row?.Duration);
                Redraw();
                break;
            case nameof(BulkItemViewModel.RowState):
            case nameof(BulkItemViewModel.Progress):
            case nameof(BulkItemViewModel.KeptDuration):
            case nameof(BulkItemViewModel.IsValidCut):
            case nameof(BulkItemViewModel.KeyframesReady):
                Redraw();
                break;
        }
    }

    private void OnHandleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CutMarkerViewModel.Snapped)
            or nameof(CutMarkerViewModel.Requested))
        {
            Redraw();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

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
        if (width <= 0 || height <= 0 || _row is null)
        {
            return;
        }

        // Guard: no duration yet (probe pending) / keyframes still indexing → draw the empty track only.
        if (_row.Duration is not { } dur || dur.TotalSeconds <= 0)
        {
            return;
        }

        var total = dur.TotalSeconds;
        var introX = BulkScrubMath.SecondsToX(_row.IntroEnd.Snapped.TotalSeconds, total, width);
        var hasOutro = _row.HasOutro && _row.OutroStart is not null;
        var outroX = hasOutro ? BulkScrubMath.SecondsToX(_row.OutroStart!.Snapped.TotalSeconds, total, width) : width;

        // While dragging, paint the grabbed handle at the live cursor X so it does not stick to
        // keyframes mid-drag; the ungrabbed handle stays at its snapped position.
        if (_dragging == Handle.Intro)
        {
            introX = Math.Clamp(_dragX, 0, width);
        }
        else if (_dragging == Handle.Outro)
        {
            outroX = Math.Clamp(_dragX, 0, width);
        }

        // Keep the spans coherent if the two crossed over during a drag.
        var (keepLeft, keepRight) = BulkScrubMath.KeepSpan(introX, outroX);

        // 1. dropped intro [0 → introX] + dropped outro [outroX → width] under the scrim.
        AddRect(0, 0, introX, height, ScrimBrush);
        if (hasOutro)
        {
            AddRect(outroX, 0, width - outroX, height, ScrimBrush);
        }

        // 2. KEEP span — the primary "this is what you keep" cue, literally the brightest thing here.
        AddRect(keepLeft, 0, keepRight - keepLeft, height, KeepBrush);

        // 3. intro-end handle: gold bar + TOP cap + ▸ glyph.
        DrawHandle(introX, height, AccentBrush, topCap: true, "▸");

        // 4. outro-start handle (when present): blue bar + BOTTOM cap + ◂ glyph.
        if (hasOutro)
        {
            DrawHandle(outroX, height, InfoBrush, topCap: false, "◂");
        }

        // 5. running progress fill — a 3px bottom bar in gold while this row is trimming.
        if (_row.RowState == RowState.Running && _row.Progress > 0)
        {
            AddRect(0, height - 3, _row.Progress * width, 3, AccentBrush);
        }
    }

    private void DrawHandle(double x, double height, Brush brush, bool topCap, string glyph)
    {
        // 3px full-height bar.
        AddRect(x - 1.5, 0, 3, height, brush);

        // Cap block at the top (intro) or bottom (outro).
        const double capW = 12d;
        const double capH = 9d;
        var capY = topCap ? 0d : height - capH;
        AddRect(x - (capW / 2d), capY, capW, capH, brush);

        // Directional glyph, near the capped edge.
        var text = new TextBlock
        {
            Text = glyph,
            FontSize = 9d,
            Foreground = brush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(text, x + 3d);
        Canvas.SetTop(text, topCap ? 0d : height - 13d);
        Track.Children.Add(text);
    }

    private void AddRect(double x, double y, double w, double h, Brush fill)
    {
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var rect = new Rectangle
        {
            Width = w,
            Height = h,
            Fill = fill,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        Track.Children.Add(rect);
    }

    // ---- Gestures ---------------------------------------------------------------------------

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_row is null || _row.Duration is not { } dur || dur.TotalSeconds <= 0)
        {
            return;
        }

        var width = Track.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var pos = e.GetPosition(Track);
        var handle = PickHandle(pos, width, dur.TotalSeconds);
        if (handle == Handle.None)
        {
            return; // rows are not click-to-seek — a miss does nothing
        }

        _dragging = handle;
        _dragX = pos.X;
        _preview?.MouseLeave(); // no hover preview mid-drag
        Track.CaptureMouse();
        SetRequestedFromX(pos.X, width, dur.TotalSeconds);
        Redraw();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_row is null || _row.Duration is not { } dur || dur.TotalSeconds <= 0)
        {
            return;
        }

        var width = Track.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var pos = e.GetPosition(Track);

        if (_dragging != Handle.None)
        {
            _dragX = pos.X;
            SetRequestedFromX(pos.X, width, dur.TotalSeconds);
            Redraw();
            return;
        }

        // Hover (non-drag): feed the preview with the time under the cursor.
        if (_preview is not null)
        {
            var t = TimeSpan.FromSeconds(Clamp(pos.X / width) * dur.TotalSeconds);
            _preview.UpdateHover(t, pos.X);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging == Handle.None)
        {
            return;
        }

        if (_row?.Duration is { } dur && dur.TotalSeconds > 0)
        {
            var width = Track.ActualWidth;
            if (width > 0)
            {
                SetRequestedFromX(e.GetPosition(Track).X, width, dur.TotalSeconds);
            }
        }

        _dragging = Handle.None;
        Track.ReleaseMouseCapture();
        Redraw(); // re-paint the handle at the settled Snapped position (snap-on-release)
        e.Handled = true;
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        if (_dragging == Handle.None)
        {
            _preview?.MouseLeave();
        }
    }

    /// <summary>Pick the nearer handle within the hit radius; ties broken by which cap the cursor is closer to.</summary>
    private Handle PickHandle(Point pos, double width, double totalSeconds)
    {
        var introX = BulkScrubMath.SecondsToX(_row!.IntroEnd.Snapped.TotalSeconds, totalSeconds, width);
        var hasOutro = _row.HasOutro && _row.OutroStart is not null;
        double? outroX = hasOutro
            ? BulkScrubMath.SecondsToX(_row.OutroStart!.Snapped.TotalSeconds, totalSeconds, width)
            : null;

        return BulkScrubMath.PickHandle(introX, outroX, pos.X, pos.Y, Track.ActualHeight, HandleHitRadiusPx) switch
        {
            ScrubHandle.Intro => Handle.Intro,
            ScrubHandle.Outro => Handle.Outro,
            _ => Handle.None,
        };
    }

    private void SetRequestedFromX(double x, double width, double totalSeconds)
    {
        if (_row is null)
        {
            return;
        }

        var t = TimeSpan.FromSeconds(Clamp(x / width) * totalSeconds);
        if (_dragging == Handle.Intro)
        {
            _row.IntroEnd.Requested = t;
        }
        else if (_dragging == Handle.Outro && _row.OutroStart is not null)
        {
            _row.OutroStart.Requested = t;
        }
    }

    private static double Clamp(double v) => Math.Clamp(v, 0d, 1d);

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
