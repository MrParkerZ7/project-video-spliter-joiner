using System;
using System.Windows;
using System.Windows.Controls;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// A two-region split container whose split axis flips between horizontal (side-by-side) and vertical
/// (stacked) from a single <see cref="IsVertical"/> flag — the layout heart of D-001 / T-081.
/// <para>
/// It hosts exactly two content regions — <see cref="FirstChild"/> (video/timeline block or clip list)
/// and <see cref="SecondChild"/> (the tool panel) — separated by a themed <see cref="GridSplitter"/>.
/// The SAME two region instances are used in both axes (no markup duplication, no re-parenting into a
/// different logical group): only the container axis, the children's <c>Grid.Row</c>/<c>Grid.Column</c>,
/// and the splitter's <c>ResizeDirection</c>/orientation change when <see cref="IsVertical"/> flips.
/// </para>
/// <para>
/// The split position is remembered <b>independently per axis</b> (D6) via
/// <see cref="HorizontalRatio"/> / <see cref="VerticalRatio"/> — the first region's fraction of the
/// total length along the active axis. Both are two-way bindable so the owning view can persist them to
/// settings. A user drag on the splitter writes the active axis's ratio back; a flip restores the other
/// axis's stored ratio, so neither drag distorts the other.
/// </para>
/// This is a <see cref="Grid"/> subclass so it reuses WPF's proven grid layout + <see cref="GridSplitter"/>
/// drag machinery rather than re-implementing hit-testing; it simply rebuilds its two definitions and
/// re-places its three visual children whenever the axis or a size changes.
/// </summary>
public sealed class OrientedSplitPanel : Grid
{
    private readonly GridSplitter _splitter = new();
    private bool _built;
    private bool _applyingRatio;

    /// <summary>The thickness (px) of the draggable splitter along the split axis.</summary>
    private const double SplitterThickness = 6d;

    /// <summary>Minimum length (px) either region is allowed to shrink to, so neither collapses to nothing.</summary>
    private const double RegionMinLength = 80d;

    public OrientedSplitPanel()
    {
        // Mirror the two views' existing GridSplitter look/feel (themed via the implicit GridSplitter
        // style in Controls.xaml). ResizeBehavior spans the two neighboring regions; direction is set
        // per-axis in Rebuild.
        _splitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        _splitter.ToolTip = "Drag to resize the tool panel";
        _splitter.DragCompleted += OnSplitterDragCompleted;

        Loaded += (_, _) => EnsureBuilt();
    }

    // ---- Dependency properties ---------------------------------------------------------------

    /// <summary>The first region (video/timeline block on the left/top).</summary>
    public static readonly DependencyProperty FirstChildProperty = DependencyProperty.Register(
        nameof(FirstChild), typeof(UIElement), typeof(OrientedSplitPanel),
        new PropertyMetadata(null, OnChildrenChanged));

    /// <summary>The second region (tool panel on the right/bottom).</summary>
    public static readonly DependencyProperty SecondChildProperty = DependencyProperty.Register(
        nameof(SecondChild), typeof(UIElement), typeof(OrientedSplitPanel),
        new PropertyMetadata(null, OnChildrenChanged));

    /// <summary><c>true</c> = vertical stacked layout; <c>false</c> = horizontal side-by-side.</summary>
    public static readonly DependencyProperty IsVerticalProperty = DependencyProperty.Register(
        nameof(IsVertical), typeof(bool), typeof(OrientedSplitPanel),
        new PropertyMetadata(false, OnAxisChanged));

    /// <summary>First-region fraction (0..1) of the width in horizontal mode (two-way, per-axis — D6).</summary>
    public static readonly DependencyProperty HorizontalRatioProperty = DependencyProperty.Register(
        nameof(HorizontalRatio), typeof(double), typeof(OrientedSplitPanel),
        new FrameworkPropertyMetadata(0.7, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRatioChanged));

    /// <summary>First-region fraction (0..1) of the height in vertical mode (two-way, per-axis — D6).</summary>
    public static readonly DependencyProperty VerticalRatioProperty = DependencyProperty.Register(
        nameof(VerticalRatio), typeof(double), typeof(OrientedSplitPanel),
        new FrameworkPropertyMetadata(0.62, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRatioChanged));

    public UIElement? FirstChild
    {
        get => (UIElement?)GetValue(FirstChildProperty);
        set => SetValue(FirstChildProperty, value);
    }

    public UIElement? SecondChild
    {
        get => (UIElement?)GetValue(SecondChildProperty);
        set => SetValue(SecondChildProperty, value);
    }

    public bool IsVertical
    {
        get => (bool)GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    public double HorizontalRatio
    {
        get => (double)GetValue(HorizontalRatioProperty);
        set => SetValue(HorizontalRatioProperty, value);
    }

    public double VerticalRatio
    {
        get => (double)GetValue(VerticalRatioProperty);
        set => SetValue(VerticalRatioProperty, value);
    }

    // ---- Build / rebuild ---------------------------------------------------------------------

    private static void OnChildrenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((OrientedSplitPanel)d).EnsureBuilt(force: true);

    private static void OnAxisChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((OrientedSplitPanel)d).Rebuild();

    private static void OnRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (OrientedSplitPanel)d;
        if (!panel._applyingRatio)
        {
            panel.ApplyRatioToDefinitions();
        }
    }

    private void EnsureBuilt(bool force = false)
    {
        if (FirstChild is null || SecondChild is null)
        {
            return;
        }

        if (_built && !force)
        {
            Rebuild();
            return;
        }

        Children.Clear();

        // Add the three visual children once; their placement is (re)assigned per axis in Rebuild.
        Children.Add(FirstChild);
        Children.Add(_splitter);
        Children.Add(SecondChild);

        _built = true;
        Rebuild();
    }

    /// <summary>
    /// Rebuild the two definitions (rows in vertical mode, columns in horizontal) + re-place the three
    /// children + point the splitter along the active axis. Uses star sizing weighted by the active
    /// axis's ratio so the split honors the remembered per-axis position.
    /// </summary>
    private void Rebuild()
    {
        if (!_built)
        {
            EnsureBuilt();
            return;
        }

        RowDefinitions.Clear();
        ColumnDefinitions.Clear();

        var ratio = CurrentRatio();
        var first = Math.Clamp(ratio, 0.05, 0.95);
        var second = 1d - first;

        if (IsVertical)
        {
            // 3 rows: first region (top) · splitter · tool panel (bottom).
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(first, GridUnitType.Star), MinHeight = RegionMinLength });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(SplitterThickness) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(second, GridUnitType.Star), MinHeight = RegionMinLength });

            PlaceRow(FirstChild!, 0);
            PlaceRow(_splitter, 1);
            PlaceRow(SecondChild!, 2);

            _splitter.Height = SplitterThickness;
            _splitter.Width = double.NaN;
            _splitter.ResizeDirection = GridResizeDirection.Rows;
            _splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            _splitter.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            // 3 columns: first region (left) · splitter · tool panel (right).
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(first, GridUnitType.Star), MinWidth = RegionMinLength });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitterThickness) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(second, GridUnitType.Star), MinWidth = RegionMinLength });

            PlaceColumn(FirstChild!, 0);
            PlaceColumn(_splitter, 1);
            PlaceColumn(SecondChild!, 2);

            _splitter.Width = SplitterThickness;
            _splitter.Height = double.NaN;
            _splitter.ResizeDirection = GridResizeDirection.Columns;
            _splitter.HorizontalAlignment = HorizontalAlignment.Center;
            _splitter.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    private static void PlaceRow(UIElement child, int row)
    {
        SetRow(child, row);
        SetColumn(child, 0);
    }

    private static void PlaceColumn(UIElement child, int column)
    {
        SetColumn(child, column);
        SetRow(child, 0);
    }

    /// <summary>Re-apply the current axis's ratio to the existing star definitions (no full rebuild).</summary>
    private void ApplyRatioToDefinitions()
    {
        if (!_built)
        {
            return;
        }

        var first = Math.Clamp(CurrentRatio(), 0.05, 0.95);
        var second = 1d - first;

        if (IsVertical && RowDefinitions.Count == 3)
        {
            RowDefinitions[0].Height = new GridLength(first, GridUnitType.Star);
            RowDefinitions[2].Height = new GridLength(second, GridUnitType.Star);
        }
        else if (!IsVertical && ColumnDefinitions.Count == 3)
        {
            ColumnDefinitions[0].Width = new GridLength(first, GridUnitType.Star);
            ColumnDefinitions[2].Width = new GridLength(second, GridUnitType.Star);
        }
    }

    private double CurrentRatio() => IsVertical ? VerticalRatio : HorizontalRatio;

    /// <summary>
    /// After a drag, read the realized star weights back off the active axis's definitions and store
    /// them into the active axis's ratio DP (which the owning view persists). Only the active axis is
    /// touched, so the other axis's remembered ratio is untouched (D6).
    /// </summary>
    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        double first, second;

        if (IsVertical && RowDefinitions.Count == 3)
        {
            first = RowDefinitions[0].ActualHeight;
            second = RowDefinitions[2].ActualHeight;
        }
        else if (!IsVertical && ColumnDefinitions.Count == 3)
        {
            first = ColumnDefinitions[0].ActualWidth;
            second = ColumnDefinitions[2].ActualWidth;
        }
        else
        {
            return;
        }

        var total = first + second;
        if (total <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(first / total, 0.05, 0.95);

        // Guard so writing the ratio DP back doesn't recurse through OnRatioChanged into a rebuild.
        _applyingRatio = true;
        try
        {
            if (IsVertical)
            {
                VerticalRatio = ratio;
            }
            else
            {
                HorizontalRatio = ratio;
            }
        }
        finally
        {
            _applyingRatio = false;
        }
    }
}
